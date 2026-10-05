// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using osu.Framework.Platform;

namespace osu.Game.Database
{
    public enum LazerSyncResolution
    {
        None,
        UseOfficial,
        UseLocal
    }

    /// <summary>Raw lazer-to-lazer transfers; no stable compatibility export or shared live database.</summary>
    public sealed class LazerBeatmapSync : IDisposable
    {
        private readonly Storage storage;
        private readonly IStableBeatmapSyncStore local;
        private readonly LazerReplaySync? replays;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1);
        private volatile bool disposed;

        public LazerBeatmapSync(Storage storage, IStableBeatmapSyncStore local, LazerReplaySync? replays = null)
        {
            this.storage = storage;
            this.local = local;
            this.replays = replays;
        }

        public async Task<LazerSyncReport> Synchronize(string dataPath, Func<bool>? canCommit = null, Guid? selected = null,
                                                        LazerSyncResolution resolution = LazerSyncResolution.None, bool forceHash = false,
                                                        CancellationToken cancellationToken = default)
        {
            if (resolution != LazerSyncResolution.None && selected == null)
                throw new ArgumentException("Conflict resolution requires a selected beatmap.");
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                string root = LazerLibraryConnection.ResolveDataPath(dataPath);
                string statePath = $"lazer-sync/{LazerLibraryConnection.RootKey(root)}.json";
                StableBeatmapSync.SafePath(storage.GetFullPath(string.Empty), statePath);
                var state = loadState(statePath, root);
                var report = new LazerSyncReport();
                bool allowed() => !disposed && !cancellationToken.IsCancellationRequested && (canCommit?.Invoke() ?? true);
                void checkAllowed()
                {
                    if (!allowed()) throw new OperationCanceledException("Sync paused while playing, editing or changing settings.");
                }
                checkAllowed();
                using var official = new LazerLibraryConnection(storage, root);
                var sourceSets = official.GetAll();
                if (selected.HasValue && state.Links.All(l => l.LocalID != selected))
                {
                    if (resolution != LazerSyncResolution.None)
                        throw new IOException("The selected beatmap is not linked. Use Link / publish first.");
                    var selectedSet = local.Get(selected.Value) ?? throw new IOException("The selected local beatmap was deleted.");
                    var link = new Link { LocalID = selectedSet.ID };
                    state.Links.Add(link);
                    await transfer(link, null, selectedSet, false).ConfigureAwait(false);
                    return report;
                }

                // Work from a fixed snapshot: updated imports may assign new set IDs.
                var links = state.Links.ToList();
                if (selected == null)
                    links.AddRange(sourceSets.Where(s => state.Links.All(l => l.OfficialID != s.ID)).Select(s => new Link { OfficialID = s.ID }));
                foreach (var link in links)
                {
                    if (selected.HasValue && link.LocalID != selected) continue;
                    checkAllowed();
                    try
                    {
                        var source = link.OfficialID == Guid.Empty ? null : official.Get(link.OfficialID);
                        var target = link.LocalID == Guid.Empty ? null : local.Get(link.LocalID);
                        if (link.Pending && resolution == LazerSyncResolution.None)
                            throw new IOException("A previous transfer was interrupted. Review backups and explicitly choose a side.");
                        if (source == null && !(link.OfficialID == Guid.Empty && resolution == LazerSyncResolution.UseLocal))
                            throw new IOException("The linked official beatmap was deleted or replaced. Deletion was not propagated.");
                        if (target == null && link.LocalID != Guid.Empty && resolution != LazerSyncResolution.UseOfficial)
                            throw new IOException("The linked local beatmap was deleted or replaced. Deletion was not propagated.");
                        if (forceHash)
                        {
                            if (source != null) verifyFiles(official, source, cancellationToken);
                            if (target != null) verifyFiles(local, target, cancellationToken);
                        }
                        bool sourceChanged = source != null && !StableBeatmapSync.SameFiles(source.Files, link.Official);
                        bool targetChanged = target != null && link.LocalID != Guid.Empty && !StableBeatmapSync.SameFiles(target.Files, link.Local);
                        if (sourceChanged && targetChanged && resolution == LazerSyncResolution.None)
                            throw new IOException("Conflict: both clients changed this beatmap. Select a version explicitly.");
                        if (resolution == LazerSyncResolution.UseLocal || (targetChanged && !sourceChanged))
                            await transfer(link, source, target!, false).ConfigureAwait(false);
                        else if (sourceChanged || resolution == LazerSyncResolution.UseOfficial)
                            await transfer(link, source, target, true).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e)
                    {
                        report.Issues.Add($"{link.OfficialID} / {link.LocalID}: {e.Message}");
                    }
                }
                if (selected == null)
                    replays?.Synchronize(official, root, allowed, report, cancellationToken, forceHash);
                return report;

                async Task transfer(Link link, StableSyncSet? source, StableSyncSet? target, bool pull)
                {
                    StableSyncSet desired = (pull ? source : target) ?? throw new IOException("The chosen version no longer exists.");
                    if (!desired.Files.Keys.Any(f => f.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)))
                        throw new IOException("Refusing to sync a beatmap set without .osu files.");
                    if (!pull && source == null && state.Links.Any(l => l != link && l.OfficialID != Guid.Empty && official.Get(l.OfficialID) is { } s && StableBeatmapSync.SameFiles(s.Files, desired.Files)))
                        throw new IOException("This official beatmap is already linked.");
                    string work = $"lazer-sync/staging/transfer-{Guid.NewGuid():N}";
                    string stage = StableBeatmapSync.SafePath(storage.GetFullPath(string.Empty), work);
                    try
                    {
                        copySet(pull ? official : local, desired, stage, cancellationToken);
                        // The official side is exclusively leased. Its staged set ID can change
                        // during Import, so only check its old version before starting that import.
                        bool unchanged() => allowed() && sameVersion(local, target) && (!pull || sameVersion(official, source));
                        if (!unchanged() || !sameVersion(official, source)) throw new OperationCanceledException("A beatmap changed during staging.");
                        if (source != null && target != null || !pull)
                        {
                            string backup = StableBeatmapSync.SafePath(storage.GetFullPath(string.Empty), $"lazer-sync/backups/{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
                            Directory.CreateDirectory(backup);
                            if (source != null) copySet(official, source, Path.Combine(backup, "official"), cancellationToken);
                            if (target != null) copySet(local, target, Path.Combine(backup, "local"), cancellationToken);
                            File.WriteAllText(Path.Combine(backup, "manifest.json"), JsonConvert.SerializeObject(new { Root = root, Official = source, Local = target }));
                        }
                        checkAllowed();
                        link.Pending = true;
                        // A failed initial import must not bind unrelated pre-existing local versions.
                        bool initialPull = pull && link.LocalID == Guid.Empty && !state.Links.Contains(link);
                        if (!initialPull)
                        {
                            if (!state.Links.Contains(link)) state.Links.Add(link);
                            saveState(statePath, state);
                        }
                        var imported = await (pull ? local : official).Import(stage, pull ? target?.ID : source?.ID, unchanged, cancellationToken).ConfigureAwait(false);
                        if (!StableBeatmapSync.SameFiles(desired.Files, imported.Files))
                            throw new IOException("The imported files differ from the raw lazer snapshot; synchronization has been paused.");
                        if (state.Links.Any(l => l != link && (pull ? l.LocalID : l.OfficialID) == imported.ID))
                            throw new IOException("A duplicate beatmap resolved to an already linked set.");
                        if (pull) { target = imported; report.Imported++; }
                        else { source = imported; report.Exported++; }
                        link.OfficialID = source!.ID;
                        link.LocalID = target!.ID;
                        link.Official = source.Files;
                        link.Local = target.Files;
                        link.Pending = false;
                        if (!state.Links.Contains(link)) state.Links.Add(link);
                        saveState(statePath, state);
                    }
                    finally
                    {
                        if (Directory.Exists(stage) && LazerLibraryConnection.Within(storage.GetFullPath("lazer-sync/staging"), stage))
                            storage.DeleteDirectory(work);
                    }
                }
            }
            finally { gate.Release(); }
        }

        private static bool sameVersion(IStableBeatmapSyncStore store, StableSyncSet? expected)
            => expected == null || store.Get(expected.ID) is { } actual && StableBeatmapSync.SameFiles(expected.Files, actual.Files);

        private static void verifyFiles(IStableBeatmapSyncStore store, StableSyncSet set, CancellationToken token)
        {
            foreach (var file in set.Files)
            {
                token.ThrowIfCancellationRequested();
                using var input = store.OpenFile(set, file.Key, false);
                if (!LazerLibraryConnection.HashMatches(input, file.Value))
                    throw new IOException($"Content hash mismatch: {file.Key}. No overwrite was attempted.");
            }
        }

        private static void copySet(IStableBeatmapSyncStore store, StableSyncSet set, string directory, CancellationToken token)
        {
            Directory.CreateDirectory(directory);
            foreach (var file in set.Files)
            {
                token.ThrowIfCancellationRequested();
                string path = StableBeatmapSync.SafePath(directory, file.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var input = store.OpenFile(set, file.Key, false))
                using (var output = File.Create(path)) input.CopyTo(output);
                using var copy = File.OpenRead(path);
                if (!LazerLibraryConnection.HashMatches(copy, file.Value)) throw new IOException($"File changed while copying: {file.Key}");
            }
        }

        private State loadState(string path, string root)
        {
            if (!storage.Exists(path)) return new State { Root = root };
            using var reader = new StreamReader(storage.GetStream(path));
            var state = JsonConvert.DeserializeObject<State>(reader.ReadToEnd()) ?? throw new IOException("Invalid lazer sync state.");
            if (state.Version != 1 || !string.Equals(state.Root, root, StringComparison.OrdinalIgnoreCase) || state.Links == null
                || state.Links.Where(l => l.LocalID != Guid.Empty).GroupBy(l => l.LocalID).Any(g => g.Count() > 1)
                || state.Links.Where(l => l.OfficialID != Guid.Empty).GroupBy(l => l.OfficialID).Any(g => g.Count() > 1))
                throw new IOException("Invalid or incompatible lazer sync state. Preserve the file for recovery.");
            foreach (var link in state.Links)
            {
                if (link.Local == null || link.Official == null) throw new IOException("Invalid lazer sync manifests.");
                link.Local = new Dictionary<string, string>(link.Local, StringComparer.OrdinalIgnoreCase);
                link.Official = new Dictionary<string, string>(link.Official, StringComparer.OrdinalIgnoreCase);
            }
            return state;
        }

        private void saveState(string path, State state)
        {
            using var writer = new StreamWriter(storage.CreateFileSafely(path));
            writer.Write(JsonConvert.SerializeObject(state, Formatting.Indented));
        }

        public void Dispose() => disposed = true;

        private class State
        {
            public int Version { get; set; } = 1;
            public string Root { get; set; } = string.Empty;
            public List<Link> Links { get; set; } = new List<Link>();
        }

        private class Link
        {
            public Guid OfficialID { get; set; }
            public Guid LocalID { get; set; }
            public Dictionary<string, string> Official { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Local { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public bool Pending { get; set; }
        }
    }
}
