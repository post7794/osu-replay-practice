// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using osu.Framework.Platform;

namespace osu.Game.Database
{
    public enum StableSyncResolution
    {
        None,
        UseStable,
        UseLocal
    }

    public class StableSyncReport
    {
        public int Imported { get; set; }
        public int Exported { get; set; }
        public List<string> Issues { get; } = new List<string>();
    }

    /// <summary>
    /// A conservative two-way synchroniser. The two baselines intentionally differ: stable export may
    /// quantise lazer objects. Comparing each side against its own baseline prevents conversion loops.
    /// Whole-set deletions never propagate. File removals within a set are backed up before applying.
    /// </summary>
    public sealed class StableBeatmapSync : IDisposable
    {
        private readonly Storage storage;
        private readonly IStableBeatmapSyncStore local;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1);
        private readonly ConcurrentDictionary<string, CachedHash> hashCache = new ConcurrentDictionary<string, CachedHash>(StringComparer.OrdinalIgnoreCase);
        private FileSystemWatcher? watcher;
        private readonly object watcherLock = new object();
        private bool disposed;

        public StableBeatmapSync(Storage storage, IStableBeatmapSyncStore local)
        {
            this.storage = storage;
            this.local = local;
        }

        public async Task<StableSyncReport> Synchronize(string songsPath, Func<bool>? canCommit = null, Guid? selected = null,
                                                       StableSyncResolution resolution = StableSyncResolution.None, bool forceHash = false,
                                                       CancellationToken cancellationToken = default)
        {
            if (resolution != StableSyncResolution.None && !selected.HasValue)
                throw new ArgumentException("Conflict resolution requires a selected set.", nameof(selected));

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(songsPath));
                validateRoot(root);
                watch(root);
                if (forceHash)
                    hashCache.Clear();

                string statePath = $"stable-sync/{hash(Encoding.UTF8.GetBytes(root.ToUpperInvariant()))}.json";
                var state = loadState(statePath, root);
                var report = new StableSyncReport();
                bool allowed() => !cancellationToken.IsCancellationRequested && (canCommit?.Invoke() ?? true);
                void checkAllowed()
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!allowed())
                        throw new OperationCanceledException("Synchronisation deferred while playing, editing or changing settings.");
                }

                checkAllowed();
                var directories = discover(root, cancellationToken).ToList();

                // Match a folder move only when the entire source manifest is unchanged and unambiguous.
                foreach (var link in state.Links.Where(l => !Directory.Exists(SafePath(root, l.Directory))))
                {
                    var matches = directories.Where(d => state.Links.All(l => !samePath(l.Directory, d)))
                                             .Where(d => SameFiles(scan(SafePath(root, d), cancellationToken), link.Source)).Take(2).ToArray();
                    if (matches.Length == 1)
                    {
                        link.Directory = matches[0];
                        saveState(statePath, state);
                    }
                }

                if (selected.HasValue && state.Links.All(l => l.SetID != selected.Value))
                {
                    if (resolution != StableSyncResolution.None)
                        throw new IOException("The selected set is not linked. Publish it explicitly first.");
                    var set = local.Get(selected.Value) ?? throw new IOException("The selected local beatmap is no longer available.");
                    var link = new Link { Directory = $"Replay Practice {set.ID:N}", SetID = set.ID };
                    if (Directory.Exists(SafePath(root, link.Directory)))
                        throw new IOException("The publication directory already exists. No files were overwritten.");
                    state.Links.Add(link);
                    // Persist the intent before creating anything in Songs. An interrupted publication is a conflict.
                    link.PendingWrite = true;
                    saveState(statePath, state);
                    await push(root, link, set, new Dictionary<string, string>(), statePath, state, allowed, cancellationToken).ConfigureAwait(false);
                    report.Exported++;
                    return report;
                }

                foreach (string directory in directories.Concat(state.Links.Select(l => l.Directory)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
                {
                    checkAllowed();
                    var link = state.Links.SingleOrDefault(l => samePath(l.Directory, directory));
                    if (selected.HasValue && link?.SetID != selected)
                        continue;

                    try
                    {
                        string sourceDirectory = SafePath(root, directory);
                        bool resumePublication = link?.PendingWrite == true && link.Source.Count == 0 && link.Local.Count == 0
                                                 && resolution == StableSyncResolution.UseLocal;
                        if (!Directory.Exists(sourceDirectory) && !resumePublication)
                            throw new IOException("Source folder is missing; deletion was not propagated.");

                        var source = Directory.Exists(sourceDirectory) ? scan(sourceDirectory, cancellationToken) : new Dictionary<string, string>();
                        if (!source.Keys.Any(isBeatmap) && !(link?.PendingWrite == true && resolution == StableSyncResolution.UseLocal))
                            throw new IOException("Source contains no .osu files; deletion was not propagated.");

                        if (link == null && state.Links.Any(l => sameBeatmaps(l.Source, source)))
                            throw new IOException("Duplicate source beatmaps are already linked from another folder.");

                        var set = link == null ? null : local.Get(link.SetID);
                        if (link != null && set == null && resolution != StableSyncResolution.UseStable)
                            throw new IOException("Linked local set was deleted or replaced; source files were left untouched.");
                        if (link?.PendingWrite == true && resolution == StableSyncResolution.None)
                            throw new IOException("A previous write was interrupted. Review backups and explicitly choose a side.");

                        bool sourceChanged = link == null || !SameFiles(source, link.Source);
                        bool localChanged = link != null && set != null && !SameFiles(set.Files, link.Local);
                        if (sourceChanged && localChanged && resolution == StableSyncResolution.None)
                            throw new IOException("Conflict: both sides changed. Select this beatmap and explicitly choose a side.");

                        if (resolution == StableSyncResolution.UseLocal || (localChanged && !sourceChanged))
                        {
                            await push(root, link!, set!, source, statePath, state, allowed, cancellationToken).ConfigureAwait(false);
                            report.Exported++;
                        }
                        else if (sourceChanged || resolution == StableSyncResolution.UseStable)
                        {
                            // Stage independent copies. Never import live files that stable might be halfway through saving.
                            string stage = newWorkDirectory("staging");
                            try
                            {
                                copySource(sourceDirectory, stage, source, cancellationToken);
                                if (!SameFiles(source, scan(sourceDirectory, cancellationToken, true)))
                                    throw new IOException("Source changed during staging; retry after saving finishes.");

                                if (set != null)
                                {
                                    // A resource-only edit in stable must not round-trip unchanged lazer maps
                                    // through the lossy compatibility export. Keep their local original bytes.
                                    if (link != null && resolution != StableSyncResolution.UseStable)
                                    {
                                        foreach (var file in source)
                                        {
                                            if (link.Source.TryGetValue(file.Key, out string? baseline) && baseline == file.Value && set.Files.ContainsKey(file.Key))
                                            {
                                                using var input = local.OpenFile(set, file.Key, false);
                                                using var output = File.Create(SafePath(stage, file.Key));
                                                input.CopyTo(output);
                                            }
                                        }
                                    }
                                    backup(sourceDirectory, source, set, cancellationToken);
                                }
                                checkAllowed();
                                var before = set;
                                var imported = await local.Import(stage, set?.ID, () => allowed() && unchanged(before), cancellationToken).ConfigureAwait(false);
                                if (state.Links.Any(l => l != link && l.SetID == imported.ID))
                                    throw new IOException("Identical source folders resolve to the same local set; only one may be linked.");

                                link ??= new Link { Directory = directory };
                                link.SetID = imported.ID;
                                link.Source = source;
                                link.Local = imported.Files;
                                link.PendingWrite = false;
                                if (!state.Links.Contains(link))
                                    state.Links.Add(link);
                                saveState(statePath, state);
                                report.Imported++;
                            }
                            finally
                            {
                                deleteWorkDirectory(stage);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        report.Issues.Add($"{directory}: {e.Message}");
                    }
                }
                return report;
            }
            finally
            {
                gate.Release();
            }
        }

        private Task push(string root, Link link, StableSyncSet set, Dictionary<string, string> source,
                          string statePath, State state, Func<bool> allowed, CancellationToken cancellationToken)
        {
            if (!set.Files.Keys.Any(isBeatmap))
                throw new IOException("Refusing to publish a set without any .osu files.");

            string target = SafePath(root, link.Directory);
            string stage = newWorkDirectory("staging");
            try
            {
                // Preserve byte-for-byte original .osu files which have not been edited locally.
                // Converting every difficulty would unnecessarily invalidate stable replay hashes.
                foreach (string filename in set.Files.Keys)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool preserve = link.Local.TryGetValue(filename, out string? previous) && previous == set.Files[filename] && source.ContainsKey(filename);
                    string destination = SafePath(stage, filename);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using var input = preserve ? File.OpenRead(SafePath(target, filename)) : local.OpenFile(set, filename, true);
                    using var output = File.Create(destination);
                    input.CopyTo(output);
                }

                var desired = scan(stage, cancellationToken, true);
                if (!unchanged(set) || !allowed())
                    throw new OperationCanceledException("Local state changed before publishing.");
                if (Directory.Exists(target) && !SameFiles(source, scan(target, cancellationToken, true)))
                    throw new IOException("Source changed before publishing; no files were overwritten.");

                backup(target, source, set, cancellationToken);
                link.PendingWrite = true;
                saveState(statePath, state);
                Directory.CreateDirectory(target);

                foreach (string filename in source.Keys.Union(desired.Keys, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!allowed() || !unchanged(set))
                        throw new OperationCanceledException("Publishing interrupted; backups have been retained.");
                    source.TryGetValue(filename, out string? oldHash);
                    desired.TryGetValue(filename, out string? newHash);
                    if (oldHash == newHash)
                        continue;

                    string destination = SafePath(target, filename);
                    string? currentHash = File.Exists(destination) ? hashFile(destination) : null;
                    if (currentHash != oldHash)
                        throw new IOException($"Source changed during publishing: {filename}. Backups have been retained.");

                    if (newHash == null)
                    {
                        // Only tracked files from this set are removed; backup contains the original bytes.
                        File.Delete(destination);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    string temporary = SafePath(target, filename + $".osu-sync-{Guid.NewGuid():N}.tmp");
                    try
                    {
                        File.Copy(SafePath(stage, filename), temporary);
                        // Same-directory replace avoids truncating an existing file or mutating a hard link.
                        File.Move(temporary, destination, true);
                    }
                    finally
                    {
                        if (File.Exists(temporary))
                            File.Delete(temporary);
                    }
                }

                if (!SameFiles(desired, scan(target, cancellationToken, true)))
                    throw new IOException("Source changed during publishing. Review the retained backup before resolving.");
                link.Source = desired;
                link.Local = set.Files;
                link.PendingWrite = false;
                saveState(statePath, state);
            }
            finally
            {
                deleteWorkDirectory(stage);
            }
            return Task.CompletedTask;
        }

        private bool unchanged(StableSyncSet? expected)
        {
            if (expected == null)
                return true;
            var current = local.Get(expected.ID);
            return current != null && SameFiles(expected.Files, current.Files);
        }

        private void backup(string sourceDirectory, Dictionary<string, string> source, StableSyncSet set, CancellationToken cancellationToken)
        {
            string destination = newWorkDirectory("backups");
            copySource(sourceDirectory, Path.Combine(destination, "stable"), source, cancellationToken);
            foreach (string filename in set.Files.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = SafePath(destination, "local/" + filename);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = local.OpenFile(set, filename, false);
                using var output = File.Create(path);
                input.CopyTo(output);
            }
            File.WriteAllText(Path.Combine(destination, "manifest.json"), JsonConvert.SerializeObject(new
            {
                SourceDirectory = sourceDirectory,
                LocalSetID = set.ID,
                SourceFiles = source,
                LocalFiles = set.Files
            }, Formatting.Indented), Encoding.UTF8);
        }

        private static void copySource(string sourceDirectory, string destination, Dictionary<string, string> files, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = SafePath(destination, file.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(SafePath(sourceDirectory, file.Key), path);
                if (hashFile(path) != file.Value)
                    throw new IOException($"Source changed while copying {file.Key}.");
            }
        }

        private IEnumerable<string> discover(string root, CancellationToken cancellationToken)
        {
            return walk(root);

            IEnumerable<string> walk(string directory)
            {
                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                        continue;
                    if (Directory.EnumerateFiles(child).Any(isBeatmap))
                        yield return Path.GetRelativePath(root, child);
                    else
                    {
                        foreach (string nested in walk(child))
                            yield return nested;
                    }
                }
            }
        }

        private Dictionary<string, string> scan(string root, CancellationToken cancellationToken, bool force = false)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            walk(root);
            return result;

            void walk(string directory)
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException($"Symbolic links and junctions are not supported in synced sets: {entry.Name}");
                    if (entry is DirectoryInfo)
                    {
                        walk(entry.FullName);
                        continue;
                    }
                    var file = (FileInfo)entry;
                    string relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
                    SafePath(root, relative);
                    string digest;
                    if (!force && hashCache.TryGetValue(file.FullName, out var cached) && cached.Length == file.Length && cached.Written == file.LastWriteTimeUtc)
                        digest = cached.Hash;
                    else
                    {
                        digest = hashFile(file.FullName);
                        var after = new FileInfo(file.FullName);
                        if (file.Length != after.Length || file.LastWriteTimeUtc != after.LastWriteTimeUtc)
                            throw new IOException($"File changed while hashing: {relative}");
                        hashCache[file.FullName] = new CachedHash(file.Length, file.LastWriteTimeUtc, digest);
                    }
                    if (!result.TryAdd(relative, digest))
                        throw new IOException($"Case-insensitive filename collision: {relative}");
                }
            }
        }

        private void watch(string root)
        {
            lock (watcherLock)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (watcher != null && samePath(watcher.Path, root))
                    return;
                watcher?.Dispose();
                hashCache.Clear();
                watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                watcher.Changed += (sender, e) => hashCache.TryRemove(e.FullPath, out _);
                watcher.Created += (sender, e) => hashCache.TryRemove(e.FullPath, out _);
                watcher.Deleted += (_, _) => hashCache.Clear();
                watcher.Renamed += (_, _) => hashCache.Clear();
                watcher.Error += (_, _) => hashCache.Clear();
                watcher.EnableRaisingEvents = true;
            }
        }

        private void validateRoot(string root)
        {
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException("The configured Songs folder does not exist.");
            var current = new DirectoryInfo(root);
            while (current != null)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The Songs path must not traverse a symbolic link or junction.");
                current = current.Parent;
            }
            string ownRoot = Path.GetFullPath(storage.GetFullPath(string.Empty));
            if (within(root, ownRoot) || within(ownRoot, root))
                throw new IOException("Songs and the client's data directory must be separate, non-overlapping directories.");
        }

        /// <summary>Resolve only ordinary relative paths; reject escapes, ADS, aliases and reparse points.</summary>
        public static string SafePath(string root, string relative)
        {
            if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative))
                throw new IOException("A non-empty relative path is required.");
            string[] pieces = relative.Replace('\\', '/').Split('/');
            if (pieces.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.Contains(':')))
                throw new IOException($"Unsafe relative path: {relative}");
            if (pieces.Any(p => isDeviceName(p.Split('.')[0])))
                throw new IOException($"Reserved device name: {relative}");
            string path = Path.GetFullPath(root);
            checkReparse(path);
            foreach (string piece in pieces)
            {
                path = Path.Combine(path, piece);
                checkReparse(path);
            }
            if (!within(root, path))
                throw new IOException("Path escaped its configured root.");
            return path;

            static void checkReparse(string candidate)
            {
                if ((File.Exists(candidate) || Directory.Exists(candidate)) && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Refusing to traverse a symbolic link or junction: {candidate}");
            }
        }

        public static bool SameFiles(IReadOnlyDictionary<string, string> first, IReadOnlyDictionary<string, string> second)
        {
            if (first.Count != second.Count)
                return false;
            var lookup = second is Dictionary<string, string> dictionary && ReferenceEquals(dictionary.Comparer, StringComparer.OrdinalIgnoreCase)
                ? dictionary
                : second.ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase);
            return first.All(f => lookup.TryGetValue(f.Key, out string? value) && value == f.Value);
        }

        private static bool isDeviceName(string name) =>
            name.Equals("CON", StringComparison.OrdinalIgnoreCase) || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase) || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && char.IsDigit(name[3]));

        private static bool sameBeatmaps(Dictionary<string, string> first, Dictionary<string, string> second) =>
            first.Where(f => isBeatmap(f.Key)).Select(f => f.Value).Order().SequenceEqual(second.Where(f => isBeatmap(f.Key)).Select(f => f.Value).Order());

        private static bool within(string root, string path)
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            return samePath(root, path) || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool samePath(string first, string second) => string.Equals(first.Replace('\\', '/'), second.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
        private static bool isBeatmap(string path) => path.EndsWith(".osu", StringComparison.OrdinalIgnoreCase);
        private static string hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        private static string hashFile(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        private string newWorkDirectory(string kind)
        {
            string path = storage.GetFullPath($"stable-sync/{kind}/{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
        }

        private void deleteWorkDirectory(string path)
        {
            string staging = storage.GetFullPath("stable-sync/staging");
            if (!within(staging, path) || samePath(staging, path))
                throw new IOException("Invalid staging cleanup path.");
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }

        private State loadState(string path, string root)
        {
            if (!storage.Exists(path))
                return new State { Root = root };
            using var stream = storage.GetStream(path);
            using var reader = new StreamReader(stream);
            var state = JsonConvert.DeserializeObject<State>(reader.ReadToEnd()) ?? throw new IOException("Empty sync state; refusing to reset links.");
            if (state.Version != 1 || !samePath(state.Root, root) || state.Links.GroupBy(l => l.Directory, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                throw new IOException("Invalid sync state; restore it from backup rather than overwriting it.");
            foreach (var link in state.Links)
            {
                SafePath(root, link.Directory);
                foreach (string filename in link.Source.Keys.Concat(link.Local.Keys))
                    SafePath(root, filename);
                link.Source = new Dictionary<string, string>(link.Source, StringComparer.OrdinalIgnoreCase);
                link.Local = new Dictionary<string, string>(link.Local, StringComparer.OrdinalIgnoreCase);
            }
            return state;
        }

        private void saveState(string path, State state)
        {
            using var output = storage.CreateFileSafely(path);
            using var writer = new StreamWriter(output, Encoding.UTF8);
            writer.Write(JsonConvert.SerializeObject(state, Formatting.Indented));
        }

        public void Dispose()
        {
            lock (watcherLock)
            {
                disposed = true;
                watcher?.Dispose();
                watcher = null;
            }
        }

        private record CachedHash(long Length, DateTime Written, string Hash);

        private class State
        {
            public int Version { get; set; } = 1;
            public string Root { get; set; } = string.Empty;
            public List<Link> Links { get; set; } = new List<Link>();
        }

        private class Link
        {
            public string Directory { get; set; } = string.Empty;
            public Guid SetID { get; set; }
            public Dictionary<string, string> Source { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Local { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public bool PendingWrite { get; set; }
        }
    }
}
