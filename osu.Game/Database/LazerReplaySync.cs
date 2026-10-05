// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using osu.Framework.Platform;
using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;

namespace osu.Game.Database
{
    public class LazerSyncReport : StableSyncReport
    {
        public int ReplaysImported { get; set; }
        public int ReplaysExported { get; set; }
    }

    /// <summary>Append-only replay exchange, after beatmaps, under the existing offline library lease.</summary>
    public sealed class LazerReplaySync
    {
        private readonly Storage storage;
        private readonly LazerReplaySyncStore local;
        private readonly Func<BeatmapInfo, WorkingBeatmap> getWorkingBeatmap;

        public LazerReplaySync(Storage storage, RealmAccess realm, Func<BeatmapInfo, WorkingBeatmap> getWorkingBeatmap)
        {
            this.storage = storage;
            local = new LazerReplaySyncStore(storage, realm);
            this.getWorkingBeatmap = getWorkingBeatmap;
        }

        internal void Synchronize(LazerLibraryConnection official, string root, Func<bool> canCommit, LazerSyncReport report, CancellationToken token, bool forceHash = false)
        {
            string statePath = $"lazer-sync/replays-{LazerLibraryConnection.RootKey(root)}.json";
            StableBeatmapSync.SafePath(storage.GetFullPath(string.Empty), statePath);
            var state = new State { Root = root };
            if (storage.Exists(statePath))
            {
                using var reader = new StreamReader(storage.GetStream(statePath));
                state = JsonConvert.DeserializeObject<State>(reader.ReadToEnd()) ?? throw new IOException("Invalid replay sync state.");
                if (state.Version != 1 || !string.Equals(state.Root, root, StringComparison.OrdinalIgnoreCase) || state.Seen == null)
                    throw new IOException("Invalid replay sync state. Preserve the file for recovery.");
            }
            bool dirty = false;
            void checkAllowed()
            {
                token.ThrowIfCancellationRequested();
                if (!canCommit()) throw new OperationCanceledException();
            }
            void save()
            {
                if (!dirty) return;
                using var writer = new StreamWriter(storage.CreateFileSafely(statePath));
                writer.Write(JsonConvert.SerializeObject(state, Formatting.Indented));
                dirty = false;
            }

            try
            {
                transfer(official.Replays, local, true);
                transfer(local, official.Replays, false);
            }
            finally
            {
                // Receipts survive a later cancellation and prevent resurrection after a local deletion.
                save();
            }

            void transfer(LazerReplaySyncStore source, LazerReplaySyncStore target, bool pull)
            {
                var destination = target.GetAll();
                var destinationHashes = destination.Select(e => e.ReplayHash).ToHashSet(StringComparer.Ordinal);
                var activeDestinationHashes = destination.Where(e => !e.Deleted).Select(e => e.ReplayHash).ToHashSet(StringComparer.Ordinal);
                var exportedHashes = new HashSet<string>(StringComparer.Ordinal);
                var exportedFiles = new HashSet<string>(StringComparer.Ordinal);
                int exportedCount = 0;
                foreach (var entry in source.GetAll().Where(e => !e.Deleted))
                {
                    checkAllowed();
                    try
                    {
                        if (forceHash)
                        {
                            foreach (string hash in source.Get(entry).Files.Select(f => f.File.Hash).Distinct())
                            {
                                checkAllowed();
                                using var input = source.OpenFile(hash);
                                if (!LazerLibraryConnection.HashMatches(input, hash)) throw new IOException("Replay resource checksum mismatch.");
                            }
                        }
                        if (state.Seen.Contains(entry.ReplayHash)) continue;
                        if (destinationHashes.Contains(entry.ReplayHash))
                        {
                            // Existing soft-deleted records act as tombstones, not restoration requests.
                            if (activeDestinationHashes.Contains(entry.ReplayHash))
                            {
                                using var input = target.OpenFile(entry.ReplayHash);
                                if (!LazerLibraryConnection.HashMatches(input, entry.ReplayHash)) throw new IOException("Existing destination replay is corrupt; it was not overwritten.");
                            }
                            // A duplicate created in this staging batch is not durable until CommitFiles succeeds.
                            if (!exportedHashes.Contains(entry.ReplayHash)) dirty |= state.Seen.Add(entry.ReplayHash);
                            continue;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e)
                    {
                        report.Issues.Add($"Replay {(pull ? "official → local" : "local → official")} {entry.ID}: {e.Message}");
                        continue;
                    }
                    string work = $"lazer-sync/staging/replay-{Guid.NewGuid():N}";
                    string stage = StableBeatmapSync.SafePath(storage.GetFullPath(string.Empty), work);
                    try
                    {
                        var snapshot = source.Get(entry);
                        if (snapshot.Hash != entry.ReplayHash) throw new IOException("Replay content does not match its score hash.");
                        target.MatchingBeatmap(snapshot);
                        var beatmap = local.MatchingBeatmap(snapshot);
                        Directory.CreateDirectory(stage);
                        string[] hashes = snapshot.Files.Select(f => f.File.Hash).Distinct().ToArray();
                        foreach (string hash in hashes)
                        {
                            checkAllowed();
                            using (var input = source.OpenFile(hash))
                            using (var output = File.Create(StableBeatmapSync.SafePath(stage, hash))) input.CopyTo(output);
                            using var copy = File.OpenRead(StableBeatmapSync.SafePath(stage, hash));
                            if (!LazerLibraryConnection.HashMatches(copy, hash)) throw new IOException("Replay resource checksum mismatch.");
                        }
                        // Decode the real frames against an exact beatmap version, but discard decoder metadata:
                        // importing it would recalculate scores, lose user fields, and potentially make API calls.
                        using (var input = File.OpenRead(StableBeatmapSync.SafePath(stage, entry.ReplayHash)))
                            ValidateReplay(snapshot, input, getWorkingBeatmap(beatmap));
                        if (!source.Unchanged(snapshot)) throw new IOException("Replay changed during synchronization; retry on the next scan.");
                        bool added = target.Import(snapshot, stage, canCommit);
                        destinationHashes.Add(entry.ReplayHash);
                        if (pull)
                        {
                            if (added) report.ReplaysImported++;
                            dirty |= state.Seen.Add(entry.ReplayHash);
                            save();
                        }
                        else
                        {
                            if (added) exportedCount++;
                            exportedFiles.UnionWith(hashes);
                            exportedHashes.Add(entry.ReplayHash);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e)
                    {
                        report.Issues.Add($"Replay {(pull ? "official → local" : "local → official")} {entry.ID}: {e.Message}");
                    }
                    finally
                    {
                        if (Directory.Exists(stage) && LazerLibraryConnection.Within(storage.GetFullPath("lazer-sync/staging"), stage))
                            storage.DeleteDirectory(work);
                    }
                }
                if (exportedCount > 0)
                {
                    checkAllowed();
                    // One backed-up database installation for the whole replay batch, not one per score.
                    official.CommitFiles(exportedFiles, canCommit, token);
                    report.ReplaysExported += exportedCount;
                }
                foreach (string hash in exportedHashes) dirty |= state.Seen.Add(hash);
                save();
            }
        }

        internal static void ValidateReplay(ScoreInfo metadata, Stream input, WorkingBeatmap beatmap)
        {
            var decoded = new ReplayDecoder(beatmap, metadata.Ruleset).Parse(input);
            if (decoded.Replay == null || decoded.Replay.Frames.Count == 0)
                throw new IOException("Replay contains no playable frames.");
        }

        private class ReplayDecoder : LegacyScoreDecoder
        {
            private readonly WorkingBeatmap beatmap;
            private readonly RulesetInfo ruleset;

            public ReplayDecoder(WorkingBeatmap beatmap, RulesetInfo ruleset)
            {
                this.beatmap = beatmap;
                this.ruleset = ruleset;
            }

            protected override Ruleset GetRuleset(int rulesetId) => rulesetId == ruleset.OnlineID
                ? ruleset.CreateInstance() : throw new IOException("Replay ruleset does not match its score record.");

            protected override WorkingBeatmap GetBeatmap(string md5Hash) => md5Hash == beatmap.BeatmapInfo.MD5Hash
                ? beatmap : throw new IOException("Replay does not match the exact beatmap version.");
        }

        private class State
        {
            public int Version { get; set; } = 1;
            public string Root { get; set; } = string.Empty;
            public HashSet<string> Seen { get; set; } = new HashSet<string>(StringComparer.Ordinal);
        }
    }
}
