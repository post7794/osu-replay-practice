// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Platform;
using osu.Framework.Testing;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.Database;
using osu.Game.IO;
using osu.Game.Rulesets;

namespace osu.Game.Tests.Database
{
    public class StableBeatmapSyncIntegrationTest : RealmTest
    {
        [Test]
        public void TestRealImportResourceUpdateAndStableExport() => RunTestWithRealmAsync(async (realm, storage) =>
        {
            using var rulesets = new RealmRulesetStore(realm, storage);
            using var source = new TemporaryNativeStorage("stable-integration-songs");
            string songs = source.GetFullPath("Songs");
            string song = Path.Combine(songs, "Test");
            createSong(song);
            var store = new StableBeatmapSyncStore(storage, realm);
            using var sync = new StableBeatmapSync(storage, store);

            var initial = await sync.Synchronize(songs, forceHash: true);
            Assert.That(initial.Issues, Is.Empty);
            Assert.That(initial.Imported, Is.EqualTo(1));
            Guid original = activeID(realm);
            string originalBeatmap = File.ReadAllText(Path.Combine(song, "test.osu"));

            // The .osu bytes and set hash stay unchanged. Resource changes must still be imported.
            File.WriteAllText(Path.Combine(song, "audio.mp3"), "stable audio update");
            var updated = await sync.Synchronize(songs, forceHash: true);
            Assert.That(updated.Issues, Is.Empty);
            Assert.That(updated.Imported, Is.EqualTo(1));
            Guid current = activeID(realm);
            Assert.That(current, Is.Not.EqualTo(original));
            using (var stream = store.OpenFile(store.Get(current)!, "audio.mp3", false))
            using (var reader = new StreamReader(stream))
                Assert.That(reader.ReadToEnd(), Is.EqualTo("stable audio update"));
            Assert.That(realm.Run(r => r.All<BeatmapSetInfo>().Count(s => !s.DeletePending)), Is.EqualTo(1));

            var files = new ModelManager<BeatmapSetInfo>(storage, realm);
            realm.Write(r =>
            {
                var set = r.Find<BeatmapSetInfo>(current)!;
                using var bytes = new MemoryStream(Encoding.UTF8.GetBytes("local audio update"));
                files.ReplaceFile(set.Files.Single(f => f.Filename == "audio.mp3"), bytes, r);
            });
            var outbound = await sync.Synchronize(songs, forceHash: true);
            Assert.That(outbound.Issues, Is.Empty);
            Assert.That(outbound.Exported, Is.EqualTo(1));
            Assert.That(File.ReadAllText(Path.Combine(song, "audio.mp3")), Is.EqualTo("local audio update"));
            Assert.That(File.ReadAllText(Path.Combine(song, "test.osu")), Is.EqualTo(originalBeatmap));

            // Exercise the real legacy conversion, not a synthetic zip or fake exporter.
            using var exported = store.OpenFile(store.Get(current)!, "test.osu", true);
            using var lineReader = new LineBufferedReader(exported);
            var decoded = new LegacyBeatmapDecoder().Decode(lineReader);
            Assert.That(decoded.HitObjects, Has.Count.EqualTo(1));
            var unchanged = await sync.Synchronize(songs, forceHash: true);
            Assert.That(unchanged.Imported + unchanged.Exported, Is.Zero);
        });

        [Test]
        public void TestPreviouslyHardLinkedImportBecomesIndependent() => RunTestWithRealmAsync(async (realm, storage) =>
        {
            using var rulesets = new RealmRulesetStore(realm, storage);
            using var source = new TemporaryNativeStorage("stable-hardlink-songs");
            string songs = source.GetFullPath("Songs");
            string song = Path.Combine(songs, "Test");
            createSong(song);
            var importer = new BeatmapImporter(storage, realm);
            var imported = await importer.Import(new ImportTask(song), new ImportParameters { PreferHardLinks = true });
            Assert.That(imported, Is.Not.Null);
            var store = new StableBeatmapSyncStore(storage, realm);
            Guid id = activeID(realm);
            File.WriteAllText(Path.Combine(song, "audio.mp3"), "probe hard link");
            using (var input = store.OpenFile(store.Get(id)!, "audio.mp3", false))
            using (var reader = new StreamReader(input))
            {
                if (reader.ReadToEnd() != "probe hard link")
                    Assert.Ignore("This filesystem does not support hard links between the test directories.");
            }
            File.WriteAllText(Path.Combine(song, "audio.mp3"), "original audio", new UTF8Encoding(false));

            using var sync = new StableBeatmapSync(storage, store);
            var initial = await sync.Synchronize(songs, forceHash: true);
            Assert.That(initial.Issues, Is.Empty);
            id = activeID(realm);
            File.WriteAllText(Path.Combine(song, "audio.mp3"), "changed externally");
            using (var input = store.OpenFile(store.Get(id)!, "audio.mp3", false))
            using (var reader = new StreamReader(input))
                Assert.That(reader.ReadToEnd(), Is.EqualTo("original audio"));
            var next = await sync.Synchronize(songs, forceHash: true);
            Assert.That(next.Issues, Is.Empty);
            Assert.That(next.Imported, Is.EqualTo(1));
        });

        [Test]
        public void TestFirstConnectionDoesNotReplaceDifferingLocalResources() => RunTestWithRealmAsync(async (realm, storage) =>
        {
            using var rulesets = new RealmRulesetStore(realm, storage);
            using var source = new TemporaryNativeStorage("stable-initial-conflict");
            string songs = source.GetFullPath("Songs");
            string song = Path.Combine(songs, "Test");
            createSong(song);
            var importer = new BeatmapImporter(storage, realm);
            var original = await importer.Import(new ImportTask(song));
            Assert.That(original, Is.Not.Null);
            var files = new ModelManager<BeatmapSetInfo>(storage, realm);
            realm.Write(r =>
            {
                var set = r.Find<BeatmapSetInfo>(original!.ID)!;
                using var bytes = new MemoryStream(Encoding.UTF8.GetBytes("pre-existing local edit"));
                files.ReplaceFile(set.Files.Single(f => f.Filename == "audio.mp3"), bytes, r);
            });
            var store = new StableBeatmapSyncStore(storage, realm);
            using var sync = new StableBeatmapSync(storage, store);
            var result = await sync.Synchronize(songs, forceHash: true);
            Assert.That(result.Issues, Has.Count.EqualTo(1));
            Assert.That(result.Imported + result.Exported, Is.Zero);
            Assert.That(activeID(realm), Is.EqualTo(original!.ID));
            using var input = store.OpenFile(store.Get(original.ID)!, "audio.mp3", false);
            using var reader = new StreamReader(input);
            Assert.That(reader.ReadToEnd(), Is.EqualTo("pre-existing local edit"));
            Assert.That(File.ReadAllText(Path.Combine(song, "audio.mp3")), Is.EqualTo("original audio"));
        });

        [TestCase("Relative=Songs", false)]
        [TestCase("SongsElsewhere", true)]
        public void TestCustomStableSongPath(string folder, bool absolute)
        {
            using var host = new CleanRunHeadlessGameHost();
            using var storage = new TemporaryNativeStorage("stable-config-songs");
            string expected = storage.GetFullPath(folder);
            string configured = absolute ? expected : folder;
            File.WriteAllText(storage.GetFullPath($"osu!.{Environment.UserName}.cfg"), $"BeatmapDirectory = {configured}\n", Encoding.UTF8);
            var stable = new StableStorage(storage.GetFullPath(string.Empty), host);
            Assert.That(Path.TrimEndingDirectorySeparator(stable.GetSongStorage().GetFullPath(string.Empty)), Is.EqualTo(Path.TrimEndingDirectorySeparator(expected)));
        }

        [Test]
        public void TestRealImportGuardDoesNotOverwriteLocal() => RunTestWithRealmAsync(async (realm, storage) =>
        {
            using var rulesets = new RealmRulesetStore(realm, storage);
            using var source = new TemporaryNativeStorage("stable-guard-songs");
            string song = source.GetFullPath("Test");
            createSong(song);
            var store = new StableBeatmapSyncStore(storage, realm);
            var original = await store.Import(song, null, () => true, default);
            File.WriteAllText(Path.Combine(song, "audio.mp3"), "rejected update");
            Exception? failure = null;
            try
            {
                await store.Import(song, original.ID, () => false, default);
            }
            catch (Exception e)
            {
                failure = e;
            }
            Assert.That(failure, Is.Not.Null);
            Assert.That(activeID(realm), Is.EqualTo(original.ID));
            using var input = store.OpenFile(store.Get(original.ID)!, "audio.mp3", false);
            using var reader = new StreamReader(input);
            Assert.That(reader.ReadToEnd(), Is.EqualTo("original audio"));
        });

        private static Guid activeID(RealmAccess realm) => realm.Run(r => r.All<BeatmapSetInfo>().Single(s => !s.DeletePending).ID);

        private static void createSong(string path)
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "audio.mp3"), "original audio", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(path, "test.osu"), """
                osu file format v14

                [General]
                AudioFilename: audio.mp3
                Mode: 0

                [Metadata]
                Title: Sync Test
                Artist: Test
                Creator: Sync
                Version: Normal
                BeatmapID: -1
                BeatmapSetID: -1

                [Difficulty]
                HPDrainRate:5
                CircleSize:4
                OverallDifficulty:5
                ApproachRate:5
                SliderMultiplier:1.4
                SliderTickRate:1

                [TimingPoints]
                0,500,4,1,0,100,1,0

                [HitObjects]
                256,192,1000,1,0,0:0:0:0:
                """, new UTF8Encoding(false));
        }
    }
}
