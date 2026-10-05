// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Platform;
using osu.Framework.Testing;
using osu.Game.Beatmaps;
using Decoder = osu.Game.Beatmaps.Formats.Decoder;
using osu.Game.Database;
using osu.Game.IO;
using osu.Game.Models;
using osu.Game.Replays;
using osu.Game.Rulesets;
using osu.Game.Replays.Legacy;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Game.Tests.Beatmaps;
using Realms;

namespace osu.Game.Tests.Database
{
    [Platform("Win")]
    public class LazerReplaySyncTest : RealmTest
    {
        [Test]
        public void TestTwoWayRoundTripPreservesMetadataAndPlayableFrames() => RunTestWithRealmAsync(async (realm, storage) =>
        {
            using var rulesets = new RealmRulesetStore(realm, storage);
            using var official = new TemporaryNativeStorage($"replays-official-{Guid.NewGuid():N}");
            await seed(official);
            ScoreInfo source;
            using (var remote = RealmAccess.OpenSyncSnapshot(official, "client.realm"))
            {
                source = SeedReplay(remote, official, 1);
                // Different record ID and filename, identical replay bytes.
                var duplicate = remote.Run(r => r.Find<ScoreInfo>(source.ID)!.Detach());
                duplicate.ID = Guid.NewGuid();
                remote.Write(r =>
                {
                    duplicate.BeatmapInfo = r.Find<BeatmapInfo>(source.BeatmapInfo!.ID);
                    duplicate.Ruleset = r.Find<RulesetInfo>(source.Ruleset.ShortName)!;
                    duplicate.Files[0].File = r.Find<RealmFile>(source.Files[0].File.Hash)!;
                    duplicate.Files[0].Filename = "renamed.osr";
                    r.Add(duplicate);
                });
            }
            using var sync = createSync(storage, realm);
            var pull = await sync.Synchronize(official.GetFullPath(string.Empty));
            Assert.That(pull.Issues, Is.Empty);
            Assert.That(pull.Imported, Is.EqualTo(1));
            Assert.That(pull.ReplaysImported, Is.EqualTo(1));
            Assert.That(pull.ReplaysExported, Is.Zero);
            var received = realm.Run(r => r.All<ScoreInfo>().Single().Detach());
            assertMetadata(source, received);
            assertPlayable(storage, received);
            Assert.That(received.BeatmapInfo!.ID, Is.Not.EqualTo(source.BeatmapInfo!.ID));
            Assert.That(realm.Run(r => r.All<RulesetInfo>().Count()), Is.EqualTo(4));
            Assert.That(realm.Run(r => r.All<BeatmapInfo>().Count()), Is.EqualTo(1));

            var localScore = SeedReplay(realm, storage, 2);
            var secondLocalScore = SeedReplay(realm, storage, 3);
            var push = await sync.Synchronize(official.GetFullPath(string.Empty));
            Assert.That(push.Issues, Is.Empty);
            Assert.That(push.ReplaysImported, Is.Zero);
            Assert.That(push.ReplaysExported, Is.EqualTo(2));
            Assert.That(storage.GetFiles("lazer-sync/backups", "*.realm").Count(), Is.EqualTo(1), "one database installation per replay batch");
            using (var remote = RealmAccess.OpenSyncSnapshot(official, "client.realm"))
            {
                Assert.That(remote.Run(r => r.All<ScoreInfo>().Count()), Is.EqualTo(4));
                assertMetadata(source, remote.Run(r => r.Find<ScoreInfo>(source.ID)!.Detach()));
                var exported = remote.Run(r => r.Find<ScoreInfo>(localScore.ID)!.Detach());
                assertMetadata(localScore, exported);
                assertPlayable(official, exported);
                assertPlayable(official, remote.Run(r => r.Find<ScoreInfo>(secondLocalScore.ID)!.Detach()));
                Assert.That(remote.Run(r => r.All<RulesetInfo>().Count()), Is.EqualTo(4));
            }
            using var restarted = createSync(storage, realm);
            var noEcho = await restarted.Synchronize(official.GetFullPath(string.Empty));
            Assert.That(noEcho.Issues, Is.Empty);
            Assert.That(noEcho.ReplaysImported + noEcho.ReplaysExported, Is.Zero);
            Assert.That(realm.Run(r => r.All<ScoreInfo>().Count()), Is.EqualTo(3));
        });

        [TestCase(true)]
        [TestCase(false)]
        public void TestDeletionIsNotPropagatedOrResurrected(bool deleteLocal) => RunTestWithRealmAsync(async (realm, storage) =>
        {
            using var rulesets = new RealmRulesetStore(realm, storage);
            using var official = new TemporaryNativeStorage($"replays-delete-{Guid.NewGuid():N}");
            await seed(official);
            ScoreInfo source;
            using (var remote = RealmAccess.OpenSyncSnapshot(official, "client.realm")) source = SeedReplay(remote, official, 1);
            using var sync = createSync(storage, realm);
            await sync.Synchronize(official.GetFullPath(string.Empty));
            if (deleteLocal) realm.Write(r => r.Remove(r.Find<ScoreInfo>(source.ID)!));
            else
            {
                using var remote = RealmAccess.OpenSyncSnapshot(official, "client.realm");
                remote.Write(r => r.Remove(r.Find<ScoreInfo>(source.ID)!));
            }
            using var restarted = createSync(storage, realm);
            var result = await restarted.Synchronize(official.GetFullPath(string.Empty));
            Assert.That(result.Issues, Is.Empty);
            Assert.That(result.ReplaysImported + result.ReplaysExported, Is.Zero);
            Assert.That(realm.Run(r => r.All<ScoreInfo>().Count()), Is.EqualTo(deleteLocal ? 0 : 1));
            using var verify = RealmAccess.OpenSyncSnapshot(official, "client.realm");
            Assert.That(verify.Run(r => r.All<ScoreInfo>().Count()), Is.EqualTo(deleteLocal ? 1 : 0));
        });

        [TestCase("missing")]
        [TestCase("corrupt")]
        [TestCase("invalid")]
        [TestCase("no-frames")]
        [TestCase("wrong-beatmap")]
        public void TestBadReplayIsReportedAndNotImported(string fault) => RunTestWithRealmAsync(async (realm, storage) =>
        {
            using var rulesets = new RealmRulesetStore(realm, storage);
            using var official = new TemporaryNativeStorage($"replays-bad-{Guid.NewGuid():N}");
            await seed(official);
            ScoreInfo score;
            using (var remote = RealmAccess.OpenSyncSnapshot(official, "client.realm"))
            {
                score = SeedReplay(remote, official, 1, frames: fault != "no-frames", wrongBeatmap: fault == "wrong-beatmap");
                if (fault == "invalid")
                {
                    remote.Write(r =>
                    {
                        using var bytes = new MemoryStream(Encoding.UTF8.GetBytes("not an osr"));
                        var file = new RealmFileStore(remote, official).Add(bytes, r);
                        r.Find<ScoreInfo>(score.ID)!.Files[0].File = file;
                        r.Find<ScoreInfo>(score.ID)!.Hash = file.Hash;
                    });
                }
            }
            string path = official.GetFullPath(LazerLibraryConnection.FilePath(score.Files[0].File.Hash));
            if (fault == "missing") File.Delete(path);
            if (fault == "corrupt") File.WriteAllText(path, "corrupt", Encoding.UTF8);
            string before = digest(official.GetFullPath("client.realm"));
            using var sync = createSync(storage, realm);
            var result = await sync.Synchronize(official.GetFullPath(string.Empty));
            Assert.That(result.ReplaysImported + result.ReplaysExported, Is.Zero);
            Assert.That(result.Issues, Has.Count.EqualTo(1));
            Assert.That(result.Issues[0], Does.Contain("Replay"));
            Assert.That(realm.Run(r => r.All<ScoreInfo>().Count()), Is.Zero);
            Assert.That(digest(official.GetFullPath("client.realm")), Is.EqualTo(before));
        });

        [Test]
        public void TestMissingExactBeatmapWaitsUntilPublished() => RunTestWithRealmAsync(async (realm, storage) =>
        {
            using var rulesets = new RealmRulesetStore(realm, storage);
            using var official = new TemporaryNativeStorage($"replays-map-{Guid.NewGuid():N}");
            await seed(official);
            using var sync = createSync(storage, realm);
            await sync.Synchronize(official.GetFullPath(string.Empty));
            await seedBeatmap(storage, realm, "Local only");
            var score = SeedReplay(realm, storage, 1, title: "Local only");
            var missing = await sync.Synchronize(official.GetFullPath(string.Empty));
            Assert.That(missing.ReplaysExported, Is.Zero);
            Assert.That(missing.Issues, Has.Count.EqualTo(1));
            Assert.That(missing.Issues[0], Does.Contain("Matching beatmap version is missing"));
            var published = await sync.Synchronize(official.GetFullPath(string.Empty), selected: score.BeatmapInfo!.BeatmapSet!.ID);
            Assert.That(published.Issues, Is.Empty);
            var retried = await sync.Synchronize(official.GetFullPath(string.Empty));
            Assert.That(retried.Issues, Is.Empty);
            Assert.That(retried.ReplaysExported, Is.EqualTo(1));
        });

        [Test]
        public void TestDeletedAndOnlineOnlyScoresAreNotTransferred() => RunTestWithRealmAsync(async (realm, storage) =>
        {
            using var rulesets = new RealmRulesetStore(realm, storage);
            using var official = new TemporaryNativeStorage($"replays-filter-{Guid.NewGuid():N}");
            await seed(official);
            using (var remote = RealmAccess.OpenSyncSnapshot(official, "client.realm"))
            {
                var deleted = SeedReplay(remote, official, 1);
                var onlineOnly = SeedReplay(remote, official, 2);
                remote.Write(r =>
                {
                    r.Find<ScoreInfo>(deleted.ID)!.DeletePending = true;
                    r.Find<ScoreInfo>(onlineOnly.ID)!.Files.Clear();
                });
            }
            using var sync = createSync(storage, realm);
            var result = await sync.Synchronize(official.GetFullPath(string.Empty));
            Assert.That(result.Issues, Is.Empty);
            Assert.That(result.ReplaysImported + result.ReplaysExported, Is.Zero);
            Assert.That(realm.Run(r => r.All<ScoreInfo>().Count()), Is.Zero);
        });

        [Test]
        public void TestCancellationDoesNotInstallStagedReplay() => RunTestWithRealmAsync(async (realm, storage) =>
        {
            using var rulesets = new RealmRulesetStore(realm, storage);
            using var official = new TemporaryNativeStorage($"replays-cancel-{Guid.NewGuid():N}");
            await seed(official);
            using var sync = createSync(storage, realm);
            await sync.Synchronize(official.GetFullPath(string.Empty));
            var source = SeedReplay(realm, storage, 1);
            string before = digest(official.GetFullPath("client.realm"));
            using (var connection = new LazerLibraryConnection(storage, official.GetFullPath(string.Empty)))
            {
                string stage = storage.GetFullPath("manual-stage");
                Directory.CreateDirectory(stage);
                string hash = source.Files[0].File.Hash;
                File.Copy(storage.GetFullPath(LazerLibraryConnection.FilePath(hash)), Path.Combine(stage, hash));
                Assert.That(connection.Replays.Import(source, stage, () => true), Is.True);
                Assert.Throws<OperationCanceledException>(() => connection.CommitFiles(new[] { hash }, () => false, default));
                Assert.Throws<IOException>(() => connection.CommitFiles(new[] { hash }, () => true, default));
            }
            Assert.That(digest(official.GetFullPath("client.realm")), Is.EqualTo(before));
            Assert.That((await sync.Synchronize(official.GetFullPath(string.Empty))).ReplaysExported, Is.EqualTo(1));
        });

        [Test]
        public void TestManualVerificationReportsMissingPreviouslySyncedReplay() => RunTestWithRealmAsync(async (realm, storage) =>
        {
            using var rulesets = new RealmRulesetStore(realm, storage);
            using var official = new TemporaryNativeStorage($"replays-verify-{Guid.NewGuid():N}");
            await seed(official);
            using var sync = createSync(storage, realm);
            await sync.Synchronize(official.GetFullPath(string.Empty));
            var source = SeedReplay(realm, storage, 1);
            Assert.That((await sync.Synchronize(official.GetFullPath(string.Empty))).ReplaysExported, Is.EqualTo(1));
            File.Delete(storage.GetFullPath(LazerLibraryConnection.FilePath(source.Hash)));
            var result = await sync.Synchronize(official.GetFullPath(string.Empty), forceHash: true);
            Assert.That(result.ReplaysImported + result.ReplaysExported, Is.Zero);
            Assert.That(result.Issues, Has.Count.EqualTo(1));
            Assert.That(result.Issues[0], Does.Contain("Replay"));
        });

        [Test]
        public void TestFirstSyncDoesNotRestoreExistingTombstone() => RunTestWithRealmAsync(async (realm, storage) =>
        {
            using var rulesets = new RealmRulesetStore(realm, storage);
            using var official = new TemporaryNativeStorage($"replays-tombstone-{Guid.NewGuid():N}");
            await seed(official);
            using (var remote = RealmAccess.OpenSyncSnapshot(official, "client.realm")) SeedReplay(remote, official, 1);
            // Synchronise only beatmaps, then independently create the same replay and delete it locally.
            using (var beatmapsOnly = new LazerBeatmapSync(storage, new StableBeatmapSyncStore(storage, realm)))
                await beatmapsOnly.Synchronize(official.GetFullPath(string.Empty));
            var local = SeedReplay(realm, storage, 1);
            realm.Write(r => r.Find<ScoreInfo>(local.ID)!.DeletePending = true);
            using var sync = createSync(storage, realm);
            var result = await sync.Synchronize(official.GetFullPath(string.Empty));
            Assert.That(result.Issues, Is.Empty);
            Assert.That(result.ReplaysImported + result.ReplaysExported, Is.Zero);
            Assert.That(realm.Run(r => r.All<ScoreInfo>().Count()), Is.EqualTo(1));
            Assert.That(realm.Run(r => r.All<ScoreInfo>().Single().DeletePending), Is.True);
        });

        [Test]
        public void TestCollidingScoreIDIsReportedWithoutOverwriting() => RunTestWithRealmAsync(async (realm, storage) =>
        {
            using var rulesets = new RealmRulesetStore(realm, storage);
            using var official = new TemporaryNativeStorage($"replays-collision-{Guid.NewGuid():N}");
            await seed(official);
            ScoreInfo officialScore;
            using (var remote = RealmAccess.OpenSyncSnapshot(official, "client.realm")) officialScore = SeedReplay(remote, official, 1);
            using (var beatmapsOnly = new LazerBeatmapSync(storage, new StableBeatmapSyncStore(storage, realm)))
                await beatmapsOnly.Synchronize(official.GetFullPath(string.Empty));
            var local = SeedReplay(realm, storage, 2);
            realm.Write(r =>
            {
                var conflicting = r.Find<ScoreInfo>(local.ID)!.Detach();
                r.Remove(r.Find<ScoreInfo>(local.ID)!);
                conflicting.ID = officialScore.ID;
                conflicting.BeatmapInfo = r.Find<BeatmapInfo>(local.BeatmapInfo!.ID);
                conflicting.Ruleset = r.Find<RulesetInfo>(local.Ruleset.ShortName)!;
                conflicting.Files[0].File = r.Find<RealmFile>(local.Hash)!;
                r.Add(conflicting);
            });
            string before = digest(official.GetFullPath("client.realm"));
            using var sync = createSync(storage, realm);
            var result = await sync.Synchronize(official.GetFullPath(string.Empty));
            Assert.That(result.Issues, Has.Count.EqualTo(2));
            Assert.That(result.Issues, Is.All.Contains("record ID"));
            Assert.That(result.ReplaysImported + result.ReplaysExported, Is.Zero);
            Assert.That(realm.Run(r => r.All<ScoreInfo>().Single().Hash), Is.EqualTo(local.Hash));
            Assert.That(digest(official.GetFullPath("client.realm")), Is.EqualTo(before));
        });
        private static LazerBeatmapSync createSync(Storage storage, RealmAccess realm) => new LazerBeatmapSync(storage,
            new StableBeatmapSyncStore(storage, realm), new LazerReplaySync(storage, realm, b => working(storage, b)));

        private static async Task seed(Storage storage)
        {
            using var realm = new RealmAccess(storage, "client");
            using var rulesets = new RealmRulesetStore(realm, storage);
            await seedBeatmap(storage, realm, "Replay Sync Test");
        }

        private static async Task seedBeatmap(Storage storage, RealmAccess realm, string title)
        {
            string folder = storage.GetFullPath($"seed-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "test.osu"), $"""
                osu file format v14

                [General]
                Mode: 0

                [Metadata]
                Title:{title}
                Artist:Test
                Creator:Sync
                Version:Normal
                BeatmapID:-1
                BeatmapSetID:-1

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
            Assert.That(await new BeatmapImporter(storage, realm).Import(new ImportTask(folder)), Is.Not.Null);
        }

        internal static ScoreInfo SeedReplay(RealmAccess realm, Storage storage, int variant, bool frames = true, bool wrongBeatmap = false, string? title = null)
        {
            var info = realm.Run(r => r.All<BeatmapInfo>().AsEnumerable().First(b => title == null || b.Metadata.Title == title).Detach());
            var score = new ScoreInfo(info, info.Ruleset)
            {
                ClientVersion = "2026.1005.0-lazer",
                TotalScore = 123456 + variant,
                TotalScoreWithoutMods = 234567 + variant,
                Accuracy = 0.9876,
                Mods = new[] { new OsuModHidden() },
                MaxCombo = 1,
                Combo = 1,
                PP = 42.125,
                Rank = ScoreRank.A,
                Date = new DateTimeOffset(2026, 10, 5, 12, 0, variant, TimeSpan.Zero),
                OnlineID = 100000 + variant,
                LegacyOnlineID = -1,
                RealmUser = new RealmUser { OnlineID = 4321, Username = "Replay sync tester", CountryCode = osu.Game.Users.CountryCode.HK },
                StatisticsJson = "{\"Great\":1}",
                MaximumStatisticsJson = "{\"Great\":1}"
            };
            score.Pauses.Add(500);
            var replay = new Replay();
            if (frames)
            {
                replay.Frames.Add(new LegacyReplayFrame(0, 128, 192, ReplayButtonState.None));
                replay.Frames.Add(new LegacyReplayFrame(1000, 256, 192, ReplayButtonState.Left1));
                replay.Frames.Add(new LegacyReplayFrame(1020, 256, 192, ReplayButtonState.None));
            }
            using var bytes = new MemoryStream();
            if (wrongBeatmap) info.MD5Hash = new string('0', 32);
            new LegacyScoreEncoder(new Score { ScoreInfo = score, Replay = replay }, null).Encode(bytes, true);
            bytes.Position = 0;
            realm.Write(r =>
            {
                var file = new RealmFileStore(realm, storage).Add(bytes, r);
                score.Hash = file.Hash;
                score.BeatmapInfo = r.Find<BeatmapInfo>(info.ID);
                score.Ruleset = r.Find<RulesetInfo>(info.Ruleset.ShortName)!;
                score.Files.Add(new RealmNamedFileUsage(file, "replay.osr"));
                r.Add(score);
            });
            return realm.Run(r => r.Find<ScoreInfo>(score.ID)!.Detach());
        }

        private static WorkingBeatmap working(Storage storage, BeatmapInfo info)
        {
            using var input = File.OpenRead(storage.GetFullPath(LazerLibraryConnection.FilePath(info.Hash)));
            using var reader = new LineBufferedReader(input);
            var beatmap = Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
            beatmap.BeatmapInfo = info;
            return new TestWorkingBeatmap(beatmap);
        }

        private static void assertPlayable(Storage storage, ScoreInfo score)
        {
            using var input = File.OpenRead(storage.GetFullPath(LazerLibraryConnection.FilePath(score.Files[0].File.Hash)));
            Assert.That(LazerLibraryConnection.HashMatches(input, score.Files[0].File.Hash), Is.True);
            input.Position = 0;
            Assert.DoesNotThrow(() => LazerReplaySync.ValidateReplay(score, input, working(storage, score.BeatmapInfo!)));
        }

        private static void assertMetadata(ScoreInfo expected, ScoreInfo actual)
        {
            foreach (var property in typeof(ScoreInfo).GetProperties().Where(p => p.DeclaringType == typeof(ScoreInfo)
                         && p.GetCustomAttributes(typeof(IgnoredAttribute), true).Length == 0
                         && (p.PropertyType.IsValueType || p.PropertyType == typeof(string))))
                Assert.That(property.GetValue(actual), Is.EqualTo(property.GetValue(expected)), property.Name);
            Assert.That(actual.Pauses, Is.EqualTo(expected.Pauses));
            Assert.That(actual.RealmUser.OnlineID, Is.EqualTo(expected.RealmUser.OnlineID));
            Assert.That(actual.RealmUser.Username, Is.EqualTo(expected.RealmUser.Username));
            Assert.That(actual.RealmUser.CountryCode, Is.EqualTo(expected.RealmUser.CountryCode));
            Assert.That(actual.BeatmapInfo!.Hash, Is.EqualTo(expected.BeatmapHash));
            Assert.That(actual.Files.Select(f => (f.Filename, f.File.Hash)), Is.EqualTo(expected.Files.Select(f => (f.Filename, f.File.Hash))));
        }

        private static string digest(string file)
        {
            using var input = File.OpenRead(file);
            return Convert.ToHexString(SHA256.HashData(input));
        }
    }
}
