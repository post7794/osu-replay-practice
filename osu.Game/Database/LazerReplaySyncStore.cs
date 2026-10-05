// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using osu.Framework.Platform;
using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Scoring;
using Realms;

namespace osu.Game.Database
{
    /// <summary>Copies existing score records without importing, recalculating or submitting scores.</summary>
    internal sealed class LazerReplaySyncStore
    {
        private readonly RealmAccess realm;
        private readonly Storage storage;
        private readonly string? fallbackRoot;

        public LazerReplaySyncStore(Storage storage, RealmAccess realm, string? fallbackRoot = null)
        {
            this.storage = storage;
            this.realm = realm;
            this.fallbackRoot = fallbackRoot;
        }

        public record Entry(Guid ID, string ReplayHash, bool Deleted);

        // Include tombstones on the receiving side, but never transfer them as sources.
        public IReadOnlyList<Entry> GetAll() => realm.Run(r => r.All<ScoreInfo>().AsEnumerable()
            .SelectMany(s => s.Files.Where(f => isReplay(f.Filename)).Select(f => new Entry(s.ID, f.File.Hash, s.DeletePending))).ToArray());

        private static bool isReplay(string filename) => filename.EndsWith(".osr", StringComparison.OrdinalIgnoreCase);

        public ScoreInfo Get(Entry entry) => realm.Run(r =>
        {
            var score = r.Find<ScoreInfo>(entry.ID);
            if (score == null || score.DeletePending || score.Files.Count(f => isReplay(f.Filename)) != 1
                || score.Files.Single(f => isReplay(f.Filename)).File.Hash != entry.ReplayHash)
                throw new IOException("Replay was deleted, changed, or has an ambiguous file manifest.");
            return score.Detach();
        });

        public bool Unchanged(ScoreInfo snapshot) => realm.Run(r =>
        {
            var score = r.Find<ScoreInfo>(snapshot.ID);
            return score != null && !score.DeletePending && score.BeatmapHash == snapshot.BeatmapHash
                   && score.Hash == snapshot.Hash && score.Files.Count == snapshot.Files.Count
                   && score.Files.All(f => snapshot.Files.Any(s => s.Filename == f.Filename && s.File.Hash == f.File.Hash));
        });

        public Stream OpenFile(string hash)
        {
            string relative = LazerLibraryConnection.FilePath(hash);
            string own = StableBeatmapSync.SafePath(storage.GetFullPath(string.Empty), relative);
            return File.OpenRead(File.Exists(own) || fallbackRoot == null ? own : StableBeatmapSync.SafePath(fallbackRoot, relative));
        }

        public BeatmapInfo MatchingBeatmap(ScoreInfo score) => realm.Run(r => matchingBeatmap(r, score).Detach());

        private static BeatmapInfo matchingBeatmap(Realm r, ScoreInfo score)
        {
            if (string.IsNullOrEmpty(score.BeatmapHash))
                throw new IOException("Replay has no beatmap hash; cannot safely associate it.");
            return r.All<BeatmapInfo>().Where(b => b.Hash == score.BeatmapHash).AsEnumerable()
                    .FirstOrDefault(b => b.BeatmapSet != null && !b.BeatmapSet.DeletePending)
                   ?? throw new IOException("Matching beatmap version is missing. Sync/publish the original beatmap first (do not match by title or online ID).");
        }

        public bool Import(ScoreInfo snapshot, string directory, Func<bool> canCommit)
        {
            // Detach again through Get(), not DeepClone(): embedded files and user must be independent.
            return realm.Write(r =>
            {
                if (!canCommit()) throw new OperationCanceledException();
                string hash = snapshot.Files.Single(f => isReplay(f.Filename)).File.Hash;
                if (r.All<ScoreInfo>().Filter("ANY Files.File.Hash == $0", hash).AsEnumerable().Any(s => s.Files.Any(f => isReplay(f.Filename) && f.File.Hash == hash)))
                    return false;
                if (r.Find<ScoreInfo>(snapshot.ID) != null)
                    throw new IOException("A different score already uses this record ID. It was not overwritten.");
                var beatmap = matchingBeatmap(r, snapshot);
                var ruleset = r.Find<RulesetInfo>(snapshot.Ruleset.ShortName);
                if (ruleset == null || ruleset.OnlineID != snapshot.Ruleset.OnlineID)
                    throw new IOException("The replay ruleset is not available in the destination library.");

                var files = new RealmFileStore(realm, storage);
                foreach (var usage in snapshot.Files)
                {
                    string fileHash = usage.File.Hash;
                    string destination = StableBeatmapSync.SafePath(storage.GetFullPath(string.Empty), LazerLibraryConnection.FilePath(fileHash));
                    if (File.Exists(destination))
                    {
                        using var existing = File.OpenRead(destination);
                        if (!LazerLibraryConnection.HashMatches(existing, fileHash))
                            throw new IOException("An existing replay resource is corrupt; it was not overwritten.");
                    }
                    using var input = File.OpenRead(StableBeatmapSync.SafePath(directory, fileHash));
                    if (!LazerLibraryConnection.HashMatches(input, fileHash))
                        throw new IOException("Replay resource checksum mismatch.");
                    input.Position = 0;
                    usage.File = files.Add(input, r);
                }
                snapshot.BeatmapInfo = beatmap;
                snapshot.Ruleset = ruleset;
                r.Add(snapshot);
                if (!canCommit()) throw new OperationCanceledException();
                return true;
            });
        }
    }
}
