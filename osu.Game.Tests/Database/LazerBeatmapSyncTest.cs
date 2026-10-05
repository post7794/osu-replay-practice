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
using osu.Game.Database;
using osu.Game.Models;
using osu.Game.Rulesets;
using osu.Game.Scoring;
using osu.Game.Skinning;
using Realms;

namespace osu.Game.Tests.Database
{
    [Platform("Win")]
    public class LazerBeatmapSyncTest : RealmTest
    {
        [Test]
        public void TestRawTwoWayRoundTripPreservesUnrelatedRecords() => RunTestWithRealmAsync(async (realm, storage) =>
        {
            using var rulesets = new RealmRulesetStore(realm, storage);
            using var official = new TemporaryNativeStorage($"lazer-official-{Guid.NewGuid():N}");
            await seed(official);
            var local = new StableBeatmapSyncStore(storage, realm);
            using var sync = new LazerBeatmapSync(storage, local);
            string root = official.GetFullPath(string.Empty);
            var first = await sync.Synchronize(root, forceHash: true);
            Assert.That(first.Issues, Is.Empty);
            Assert.That(first.Imported, Is.EqualTo(1));
            Guid localId = activeID(realm);
            string original = read(local, localId, "test.osu");
            Assert.That(original, Does.Contain("256.125,192.75,1000.125"));

            using (var remote = RealmAccess.OpenSyncSnapshot(official, "client.realm"))
                replace(official, remote, activeID(remote), "audio.mp3", "edited in official");
            var pull = await sync.Synchronize(root, forceHash: true);
            Assert.That(pull.Issues, Is.Empty);
            Assert.That(pull.Imported, Is.EqualTo(1));
            localId = activeID(realm);
            Assert.That(read(local, localId, "audio.mp3"), Is.EqualTo("edited in official"));
            Assert.That(read(local, localId, "test.osu"), Is.EqualTo(original));

            replace(storage, realm, localId, "audio.mp3", "edited locally");
            string edited = original.Replace("1000.125", "1234.567", StringComparison.Ordinal);
            replace(storage, realm, localId, "test.osu", edited);
            var push = await sync.Synchronize(root, forceHash: true);
            Assert.That(push.Issues, Is.Empty);
            Assert.That(push.Exported, Is.EqualTo(1));
            using (var connection = new LazerLibraryConnection(storage, root))
            {
                Guid id = connection.GetAll().Single().ID;
                Assert.That(read(connection, id, "audio.mp3"), Is.EqualTo("edited locally"));
                Assert.That(read(connection, id, "test.osu"), Is.EqualTo(edited));
            }
            using (var remote = RealmAccess.OpenSyncSnapshot(official, "client.realm"))
            {
                Assert.That(remote.Run(r => r.All<ScoreInfo>().Count(s => s.DeletePending)), Is.EqualTo(1));
                Assert.That(remote.Run(r => r.All<SkinInfo>().Count(s => s.Name == "Preserve skin" && s.DeletePending)), Is.EqualTo(1));
                Assert.That(remote.Run(r => r.All<ScoreInfo>().Single().Files.Single().File.Hash),
                    Is.EqualTo(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("preserve replay"))).ToLowerInvariant()));
                var skinFile = remote.Run(r => r.All<SkinInfo>().Single(s => s.Name == "Preserve skin").Files.Single().File.Hash);
                Assert.That(File.ReadAllText(official.GetFullPath(LazerLibraryConnection.FilePath(skinFile))), Is.EqualTo("original audio"));
            }
            using var restarted = new LazerBeatmapSync(storage, local);
            var noEcho = await restarted.Synchronize(root, forceHash: true);
            Assert.That(noEcho.Issues, Is.Empty);
            Assert.That(noEcho.Imported + noEcho.Exported, Is.Zero);
            Assert.That(storage.GetFiles("lazer-sync/backups", "*.realm"), Is.Not.Empty);
        });

        [TestCase(LazerSyncResolution.UseLocal)]
        [TestCase(LazerSyncResolution.UseOfficial)]
        public void TestConflictResolution(LazerSyncResolution resolution) => RunTestWithRealmAsync(async (realm, storage) =>
        {
            using var rulesets = new RealmRulesetStore(realm, storage);
            using var official = new TemporaryNativeStorage($"lazer-conflict-{Guid.NewGuid():N}");
            await seed(official);
            string root = official.GetFullPath(string.Empty);
            var local = new StableBeatmapSyncStore(storage, realm);
            using var sync = new LazerBeatmapSync(storage, local);
            Assert.That((await sync.Synchronize(root)).Issues, Is.Empty);
            Guid id = activeID(realm);
            replace(storage, realm, id, "audio.mp3", "local conflict");
            using (var remote = RealmAccess.OpenSyncSnapshot(official, "client.realm"))
                replace(official, remote, activeID(remote), "audio.mp3", "official conflict");
            var conflict = await sync.Synchronize(root);
            Assert.That(conflict.Issues, Has.Count.EqualTo(1));
            Assert.That(conflict.Imported + conflict.Exported, Is.Zero);
            var resolved = await sync.Synchronize(root, selected: id, resolution: resolution);
            Assert.That(resolved.Issues, Is.Empty);
            string expected = resolution == LazerSyncResolution.UseLocal ? "local conflict" : "official conflict";
            Assert.That(read(local, activeID(realm), "audio.mp3"), Is.EqualTo(expected));
            using var connection = new LazerLibraryConnection(storage, root);
            Assert.That(read(connection, connection.GetAll().Single().ID, "audio.mp3"), Is.EqualTo(expected));
        });

        [TestCase("client.realm")]
        [TestCase("client.realm.lock")]
        public void TestOccupiedDatabaseIsNeverChanged(string file) => RunTestWithRealmAsync(async (realm, storage) =>
        {
            using var official = new TemporaryNativeStorage($"lazer-locked-{Guid.NewGuid():N}");
            await seed(official);
            string before = digest(official.GetFullPath("client.realm"));
            using (var occupied = new FileStream(official.GetFullPath(file), FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.Throws<LazerLibraryBusyException>(() => { using var connection = new LazerLibraryConnection(storage, official.GetFullPath(string.Empty)); });
            Assert.That(digest(official.GetFullPath("client.realm")), Is.EqualTo(before));
            await Task.CompletedTask;
        });

        [Test]
        public void TestLiveRealmConnectionIsRejected() => RunTestWithRealmAsync(async (realm, storage) =>
        {
            if (!OperatingSystem.IsWindows()) Assert.Ignore("Offline file leases are validated on Windows.");
            using var official = new TemporaryNativeStorage($"lazer-live-{Guid.NewGuid():N}");
            await seed(official);
            using var liveRealm = Realm.GetInstance(new RealmConfiguration(official.GetFullPath("client.realm")) { SchemaVersion = 52 });
            Assert.Throws<LazerLibraryBusyException>(() => { using var connection = new LazerLibraryConnection(storage, official.GetFullPath(string.Empty)); });
        });

        [Test]
        public void TestNewerSchemaRejectedWithoutMigrationOrRecovery() => RunTestWithRealmAsync(async (realm, storage) =>
        {
            using var official = new TemporaryNativeStorage($"lazer-newer-{Guid.NewGuid():N}");
            await seed(official);
            using (Realm.GetInstance(new RealmConfiguration(official.GetFullPath("client.realm")) { SchemaVersion = 53 })) { }
            string before = digest(official.GetFullPath("client.realm"));
            Assert.Catch(() => { using var connection = new LazerLibraryConnection(storage, official.GetFullPath(string.Empty)); });
            Assert.That(digest(official.GetFullPath("client.realm")), Is.EqualTo(before));
        });

        [Test]
        public void TestStorageRedirectAndWrongFolder() => RunTestWithRealmAsync(async (realm, storage) =>
        {
            using var official = new TemporaryNativeStorage($"lazer-data-{Guid.NewGuid():N}");
            using var entry = new TemporaryNativeStorage($"lazer-default-{Guid.NewGuid():N}");
            await seed(official);
            File.WriteAllText(entry.GetFullPath("storage.ini"), "FullPath = " + official.GetFullPath(string.Empty));
            Assert.That(LazerLibraryConnection.ResolveDataPath(entry.GetFullPath(string.Empty)), Is.EqualTo(Path.TrimEndingDirectorySeparator(official.GetFullPath(string.Empty))));
            Assert.Throws<IOException>(() => LazerLibraryConnection.ResolveDataPath(official.GetFullPath("files")));
            Assert.Throws<IOException>(() => { using var connection = new LazerLibraryConnection(official, official.GetFullPath(string.Empty)); });
            File.WriteAllText(official.GetFullPath("storage.ini"), "FullPath = " + entry.GetFullPath(string.Empty));
            Assert.Throws<IOException>(() => LazerLibraryConnection.ResolveDataPath(entry.GetFullPath(string.Empty)));
        });

        [Test]
        public void TestWholeSetDeletionNotPropagated() => RunTestWithRealmAsync(async (realm, storage) =>
        {
            using var rulesets = new RealmRulesetStore(realm, storage);
            using var official = new TemporaryNativeStorage($"lazer-deleted-{Guid.NewGuid():N}");
            await seed(official);
            string root = official.GetFullPath(string.Empty);
            using var sync = new LazerBeatmapSync(storage, new StableBeatmapSyncStore(storage, realm));
            await sync.Synchronize(root);
            using (var remote = RealmAccess.OpenSyncSnapshot(official, "client.realm"))
                remote.Write(r => r.All<BeatmapSetInfo>().Single(s => !s.DeletePending).DeletePending = true);
            var result = await sync.Synchronize(root);
            Assert.That(result.Issues, Has.Count.EqualTo(1));
            Assert.That(realm.Run(r => r.All<BeatmapSetInfo>().Count(s => !s.DeletePending)), Is.EqualTo(1));
        });

        [Test]
        public void TestPendingRecoveryAndCancellationBlockWrites() => RunTestWithRealmAsync(async (realm, storage) =>
        {
            using var official = new TemporaryNativeStorage($"lazer-recovery-{Guid.NewGuid():N}");
            await seed(official);
            string root = Path.TrimEndingDirectorySeparator(official.GetFullPath(string.Empty));
            string before = digest(official.GetFullPath("client.realm"));
            using var sync = new LazerBeatmapSync(storage, new StableBeatmapSyncStore(storage, realm));
            Assert.ThrowsAsync<OperationCanceledException>(async () => await sync.Synchronize(root, () => false));
            using (var writer = new StreamWriter(storage.CreateFileSafely($"lazer-sync/transactions/{LazerLibraryConnection.RootKey(root)}.json"))) writer.Write("interrupted");
            Assert.ThrowsAsync<IOException>(async () => await sync.Synchronize(root));
            Assert.That(digest(official.GetFullPath("client.realm")), Is.EqualTo(before));
        });

        [Test]
        public void TestPublishIsExplicitAndDoesNotDuplicateOnRestart() => RunTestWithRealmAsync(async (realm, storage) =>
        {
            using var rulesets = new RealmRulesetStore(realm, storage);
            using var official = new TemporaryNativeStorage($"lazer-publish-{Guid.NewGuid():N}");
            await seed(official);
            string root = official.GetFullPath(string.Empty);
            var local = new StableBeatmapSyncStore(storage, realm);
            using var sync = new LazerBeatmapSync(storage, local);
            Assert.That((await sync.Synchronize(root)).Issues, Is.Empty);
            string folder = storage.GetFullPath("new-song");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "new.osu"), File.ReadAllText(official.GetFullPath("seed/test.osu")).Replace("Lazer Sync Test", "New local song", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(folder, "audio.mp3"), "new local audio");
            var created = await local.Import(folder, null, () => true, default);
            Assert.That((await sync.Synchronize(root)).Exported, Is.Zero);
            var published = await sync.Synchronize(root, selected: created.ID);
            Assert.That(published.Issues, Is.Empty);
            Assert.That(published.Exported, Is.EqualTo(1));
            using var restarted = new LazerBeatmapSync(storage, local);
            var result = await restarted.Synchronize(root, forceHash: true);
            Assert.That(result.Issues, Is.Empty);
            Assert.That(result.Imported + result.Exported, Is.Zero);
            using var connection = new LazerLibraryConnection(storage, root);
            Assert.That(connection.GetAll(), Has.Count.EqualTo(2));
        });

        [Test]
        public void TestFirstLinkConflictDoesNotModifyEitherLibrary() => RunTestWithRealmAsync(async (realm, storage) =>
        {
            using var rulesets = new RealmRulesetStore(realm, storage);
            using var official = new TemporaryNativeStorage($"lazer-first-conflict-{Guid.NewGuid():N}");
            await seed(official);
            var local = new StableBeatmapSyncStore(storage, realm);
            var imported = await local.Import(official.GetFullPath("seed"), null, () => true, default);
            replace(storage, realm, imported.ID, "audio.mp3", "keep my local edit");
            string before = digest(official.GetFullPath("client.realm"));
            using var sync = new LazerBeatmapSync(storage, local);
            var result = await sync.Synchronize(official.GetFullPath(string.Empty));
            Assert.That(result.Issues, Has.Count.EqualTo(1));
            Assert.That(result.Imported + result.Exported, Is.Zero);
            Assert.That(read(local, imported.ID, "audio.mp3"), Is.EqualTo("keep my local edit"));
            Assert.That(digest(official.GetFullPath("client.realm")), Is.EqualTo(before));
        });

        [Test]
        public void TestFailedFileInstallKeepsDatabaseAndRecoveryBackup() => RunTestWithRealmAsync(async (realm, storage) =>
        {
            using var rulesets = new RealmRulesetStore(realm, storage);
            using var official = new TemporaryNativeStorage($"lazer-failed-write-{Guid.NewGuid():N}");
            await seed(official);
            string root = official.GetFullPath(string.Empty);
            var local = new StableBeatmapSyncStore(storage, realm);
            using var sync = new LazerBeatmapSync(storage, local);
            await sync.Synchronize(root);
            Guid id = activeID(realm);
            replace(storage, realm, id, "audio.mp3", "new audio");
            string corruptPath = official.GetFullPath(LazerLibraryConnection.FilePath(local.Get(id)!.Files["audio.mp3"]));
            Directory.CreateDirectory(Path.GetDirectoryName(corruptPath)!);
            File.WriteAllText(corruptPath, "unexpected existing content");
            string before = digest(official.GetFullPath("client.realm"));
            var result = await sync.Synchronize(root);
            Assert.That(result.Issues, Has.Count.EqualTo(1));
            Assert.That(result.Exported, Is.Zero);
            Assert.That(digest(official.GetFullPath("client.realm")), Is.EqualTo(before));
            Assert.That(storage.Exists($"lazer-sync/transactions/{LazerLibraryConnection.RootKey(Path.TrimEndingDirectorySeparator(root))}.json"), Is.False);
            Assert.That(storage.GetFiles("lazer-sync/backups", "*.realm").Any(f => digest(storage.GetFullPath(f)) == before), Is.True);
            Assert.That((await sync.Synchronize(root)).Issues, Has.Count.EqualTo(1));
        });

        [Test]
        public void TestOlderSchemaIsNotUpgraded() => RunTestWithRealmAsync(async (realm, storage) =>
        {
            using var official = new TemporaryNativeStorage($"lazer-older-{Guid.NewGuid():N}");
            Directory.CreateDirectory(official.GetFullPath("files"));
            using (Realm.GetInstance(new RealmConfiguration(official.GetFullPath("client.realm")) { SchemaVersion = 51 })) { }
            string before = digest(official.GetFullPath("client.realm"));
            Assert.Catch(() => { using var connection = new LazerLibraryConnection(storage, official.GetFullPath(string.Empty)); });
            Assert.That(digest(official.GetFullPath("client.realm")), Is.EqualTo(before));
            await Task.CompletedTask;
        });
        private static async Task seed(Storage storage)
        {
            using var realm = new RealmAccess(storage, "client");
            using var rulesets = new RealmRulesetStore(realm, storage);
            string folder = storage.GetFullPath("seed");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "audio.mp3"), "original audio", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder, "test.osu"), """
                osu file format v128

                [General]
                AudioFilename: audio.mp3
                Mode: 0

                [Metadata]
                Title: Lazer Sync Test
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
                256.125,192.75,1000.125,1,0,0:0:0:0:
                """, new UTF8Encoding(false));
            Assert.That(await new BeatmapImporter(storage, realm).Import(new ImportTask(folder)), Is.Not.Null);
            realm.Write(r =>
            {
                using var replay = new MemoryStream(Encoding.UTF8.GetBytes("preserve replay"));
                var fileStore = new RealmFileStore(realm, storage);
                var score = new ScoreInfo { ID = Guid.NewGuid(), BeatmapInfo = null, Ruleset = r.All<RulesetInfo>().First(), DeletePending = true };
                score.Files.Add(new RealmNamedFileUsage(fileStore.Add(replay, r), "replay.osr"));
                r.Add(score);
                var skin = new SkinInfo { ID = Guid.NewGuid(), Name = "Preserve skin", DeletePending = true };
                skin.Files.Add(new RealmNamedFileUsage(r.All<BeatmapSetInfo>().Single(s => !s.DeletePending).Files.Single(f => f.Filename == "audio.mp3").File, "shared.mp3"));
                r.Add(skin);
            });
        }

        private static Guid activeID(RealmAccess realm) => realm.Run(r => r.All<BeatmapSetInfo>().Single(s => !s.DeletePending).ID);

        private static void replace(Storage storage, RealmAccess realm, Guid id, string name, string contents)
        {
            var manager = new ModelManager<BeatmapSetInfo>(storage, realm);
            realm.Write(r =>
            {
                using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(contents));
                manager.ReplaceFile(r.Find<BeatmapSetInfo>(id)!.Files.Single(f => f.Filename == name), bytes, r);
            });
        }

        private static string read(IStableBeatmapSyncStore store, Guid id, string name)
        {
            using var input = store.OpenFile(store.Get(id)!, name, false);
            using var reader = new StreamReader(input);
            return reader.ReadToEnd();
        }

        private static string digest(string file)
        {
            using var input = File.OpenRead(file);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input));
        }
    }
}
