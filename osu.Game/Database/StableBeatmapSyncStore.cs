// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Platform;
using osu.Game.Beatmaps;
using osu.Game.Extensions;
using osu.Game.Overlays.Notifications;
using Realms;

namespace osu.Game.Database
{
    public record StableSyncSet(Guid ID, Dictionary<string, string> Files);

    public interface IStableBeatmapSyncStore
    {
        StableSyncSet? Get(Guid id);
        Stream OpenFile(StableSyncSet set, string filename, bool forStable);
        Task<StableSyncSet> Import(string directory, Guid? original, Func<bool> canCommit, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Keeps the synchroniser separate from Realm's thread-bound objects and the gameplay resource cache.
    /// </summary>
    public class StableBeatmapSyncStore : IStableBeatmapSyncStore
    {
        private readonly RealmAccess realm;
        private readonly BeatmapImporter importer;
        private readonly InitialLinkImporter initialImporter;
        private readonly SyncExporter exporter;
        private readonly RealmFileStore files;
        private readonly IWorkingBeatmapCache? cache;

        public StableBeatmapSyncStore(Storage storage, RealmAccess realm, BeatmapManager? manager = null)
        {
            this.realm = realm;
            cache = manager;
            files = new RealmFileStore(realm, storage);
            exporter = new SyncExporter(storage);
            importer = new BeatmapImporter(storage, realm);
            initialImporter = new InitialLinkImporter(storage, realm);
            if (manager != null)
                initialImporter.ProcessBeatmap = importer.ProcessBeatmap = (set, _) => manager.ProcessSyncedBeatmap(set, MetadataLookupScope.None);
        }

        public StableSyncSet? Get(Guid id) => realm.Run(r =>
        {
            var set = r.Find<BeatmapSetInfo>(id);
            return set == null || set.DeletePending ? null : snapshot(set);
        });

        public Stream OpenFile(StableSyncSet expected, string filename, bool forStable) => realm.Run(r =>
        {
            var set = r.Find<BeatmapSetInfo>(expected.ID);
            if (set == null || set.DeletePending || !StableBeatmapSync.SameFiles(expected.Files, snapshot(set).Files))
                throw new IOException("The local beatmap changed while synchronising.");

            var file = set.GetFile(filename) ?? throw new FileNotFoundException(filename);
            return (forStable ? exporter.Open(set, file) : files.Store.GetStream(file.File.GetStoragePath()))
                   ?? throw new FileNotFoundException(filename);
        });

        public async Task<StableSyncSet> Import(string directory, Guid? original, Func<bool> canCommit, CancellationToken cancellationToken)
        {
            var parameters = new ImportParameters
            {
                EnsureIndependentFiles = true,
                CanCommit = () => !cancellationToken.IsCancellationRequested && canCommit()
            };

            var previous = original.HasValue ? realm.Run(r => r.Find<BeatmapSetInfo>(original.Value)?.Detach()) : null;
            var result = previous == null
                ? await initialImporter.Import(new ImportTask(directory), parameters, cancellationToken).ConfigureAwait(false)
                : await importer.ImportAsUpdate(new ProgressNotification(), new ImportTask(directory), previous, parameters).ConfigureAwait(false);

            if (result == null)
                throw new IOException("No usable beatmap was imported.");

            if (previous != null)
                cache?.Invalidate(previous);

            var updated = result.PerformRead(set => set.Detach());
            cache?.Invalidate(updated);
            // BeatmapSetInfo.Detach intentionally omits Files; capture the manifest from the live model.
            return result.PerformRead(snapshot);
        }

        private static StableSyncSet snapshot(BeatmapSetInfo set) => new StableSyncSet(set.ID,
            set.Files.ToDictionary(f => f.Filename, f => f.File.Hash, StringComparer.OrdinalIgnoreCase));

        // There is no shared baseline on first connection. Never silently replace an existing local
        // version merely because its .osu hash or online set ID matches the source library.
        private class InitialLinkImporter : BeatmapImporter
        {
            public InitialLinkImporter(Storage storage, RealmAccess realm)
                : base(storage, realm)
            {
            }

            protected override bool CanReuseExisting(BeatmapSetInfo existing, BeatmapSetInfo import)
            {
                bool reusable = base.CanReuseExisting(existing, import);
                if (!reusable && !existing.DeletePending)
                    throw new IOException("An existing version differs from the source library. Export or preserve it and resolve the duplicate before linking.");
                return reusable;
            }

            protected override void PreImport(BeatmapSetInfo beatmapSet, Realm realm)
            {
                int id = beatmapSet.OnlineID;
                if (id > 0 && realm.All<BeatmapSetInfo>().Any(s => s.OnlineID == id && !s.DeletePending))
                    throw new IOException("An existing local version has the same online ID. Resolve the duplicate before linking.");
                base.PreImport(beatmapSet, realm);
            }
        }

        private class SyncExporter : LegacyBeatmapExporter
        {
            public SyncExporter(Storage storage)
                : base(storage)
            {
            }

            public Stream? Open(BeatmapSetInfo set, INamedFileUsage file) => GetFileContents(set, file);
        }
    }
}
