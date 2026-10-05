// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Testing;
using osu.Game.Database;

namespace osu.Game.Tests.Database
{
    [TestFixture]
    public class StableBeatmapSyncTest
    {
        private TemporaryNativeStorage sourceStorage = null!;
        private TemporaryNativeStorage storage = null!;
        private MemoryStore local = null!;
        private StableBeatmapSync sync = null!;
        private string songs = null!;
        private string song => Path.Combine(songs, "123 Artist - Title");
        private Guid setID => local.Data.Keys.Single();

        [SetUp]
        public void SetUp()
        {
            sourceStorage = new TemporaryNativeStorage("stable-sync-source");
            storage = new TemporaryNativeStorage("stable-sync-local");
            songs = sourceStorage.GetFullPath("Songs");
            Directory.CreateDirectory(song);
            write("map.osu", "osu file format v14\nTitle:Original");
            write("audio.mp3", "original audio");
            local = new MemoryStore();
            sync = new StableBeatmapSync(storage, local);
        }

        [TearDown]
        public void TearDown()
        {
            sync.Dispose();
            storage.Dispose();
            sourceStorage.Dispose();
        }

        [Test]
        public async Task TestImportThenUnchangedRestartDoesNothing()
        {
            Assert.That((await run()).Imported, Is.EqualTo(1));
            Assert.That(localText("map.osu"), Does.Contain("Original"));
            sync.Dispose();
            sync = new StableBeatmapSync(storage, local);
            var result = await run();
            Assert.That(result.Imported + result.Exported, Is.Zero);
            Assert.That(result.Issues, Is.Empty);
            Assert.That(local.ImportCount, Is.EqualTo(1));
        }

        [Test]
        public async Task TestSourceAssetOnlyUpdate()
        {
            await run();
            write("audio.mp3", "modified audio");
            var result = await run();
            Assert.That(result.Imported, Is.EqualTo(1));
            Assert.That(result.Issues, Is.Empty);
            Assert.That(localText("audio.mp3"), Is.EqualTo("modified audio"));
            Assert.That(local.ImportCount, Is.EqualTo(2));
        }

        [Test]
        public async Task TestLocalAssetOnlyUpdatePreservesUneditedBeatmapBytes()
        {
            await run();
            string original = File.ReadAllText(Path.Combine(song, "map.osu"));
            local.ConvertForStable = true;
            editLocal("audio.mp3", "local audio");
            var result = await run();
            Assert.That(result.Exported, Is.EqualTo(1));
            Assert.That(result.Issues, Is.Empty);
            Assert.That(File.ReadAllText(Path.Combine(song, "audio.mp3")), Is.EqualTo("local audio"));
            Assert.That(File.ReadAllText(Path.Combine(song, "map.osu")), Is.EqualTo(original));
        }

        [Test]
        public async Task TestLocalBeatmapConvertedOnceWithoutEchoAcrossRestart()
        {
            await run();
            local.ConvertForStable = true;
            editLocal("map.osu", "edited lazer beatmap");
            Assert.That((await run()).Exported, Is.EqualTo(1));
            Assert.That(File.ReadAllText(Path.Combine(song, "map.osu")), Is.EqualTo("edited lazer beatmap\nconverted"));
            Assert.That(localText("map.osu"), Is.EqualTo("edited lazer beatmap"));
            sync.Dispose();
            sync = new StableBeatmapSync(storage, local);
            var next = await run();
            Assert.That(next.Imported + next.Exported, Is.Zero);
            Assert.That(next.Issues, Is.Empty);
        }

        [Test]
        public async Task TestRenameAndFileRemovalBackedUp()
        {
            await run();
            local.Data[setID].Remove("map.osu");
            editLocal("renamed.osu", "renamed difficulty");
            local.Data[setID].Remove("audio.mp3");
            Assert.That((await run()).Exported, Is.EqualTo(1));
            Assert.That(File.Exists(Path.Combine(song, "map.osu")), Is.False);
            Assert.That(File.Exists(Path.Combine(song, "audio.mp3")), Is.False);
            Assert.That(File.Exists(Path.Combine(song, "renamed.osu")), Is.True);
            Assert.That(backupFiles("map.osu").Any(f => File.ReadAllText(f).Contains("Original")), Is.True);
            Assert.That(backupFiles("audio.mp3").Any(f => File.ReadAllText(f) == "original audio"), Is.True);
        }

        [Test]
        public async Task TestBothSidesChangedRequireExplicitResolution()
        {
            await run();
            write("map.osu", "stable edit");
            editLocal("map.osu", "local edit");
            var conflict = await run();
            Assert.That(conflict.Issues, Has.Count.EqualTo(1));
            Assert.That(conflict.Imported + conflict.Exported, Is.Zero);
            Assert.That(localText("map.osu"), Is.EqualTo("local edit"));
            Assert.That(File.ReadAllText(Path.Combine(song, "map.osu")), Is.EqualTo("stable edit"));

            var resolved = await sync.Synchronize(songs, selected: setID, resolution: StableSyncResolution.UseStable, forceHash: true);
            Assert.That(resolved.Imported, Is.EqualTo(1));
            Assert.That(resolved.Issues, Is.Empty);
            Assert.That(localText("map.osu"), Is.EqualTo("stable edit"));
            Assert.That(backupFiles("map.osu").Any(f => File.ReadAllText(f) == "local edit"), Is.True);
        }

        [Test]
        public async Task TestResolveConflictUsingLocal()
        {
            await run();
            write("audio.mp3", "stable edit");
            editLocal("audio.mp3", "local edit");
            var result = await sync.Synchronize(songs, selected: setID, resolution: StableSyncResolution.UseLocal, forceHash: true);
            Assert.That(result.Exported, Is.EqualTo(1));
            Assert.That(result.Issues, Is.Empty);
            Assert.That(File.ReadAllText(Path.Combine(song, "audio.mp3")), Is.EqualTo("local edit"));
            Assert.That(backupFiles("audio.mp3").Any(f => File.ReadAllText(f) == "stable edit"), Is.True);
        }

        [Test]
        public async Task TestSourceSetDeletionNeverDeletesLocal()
        {
            await run();
            Directory.Delete(song, true);
            var result = await run();
            Assert.That(result.Issues, Has.Count.EqualTo(1));
            Assert.That(local.Data, Has.Count.EqualTo(1));
            Assert.That(Directory.Exists(song), Is.False);
        }

        [Test]
        public async Task TestLocalSetDeletionNeverDeletesSource()
        {
            await run();
            local.Data.Clear();
            var result = await run();
            Assert.That(result.Issues, Has.Count.EqualTo(1));
            Assert.That(File.Exists(Path.Combine(song, "map.osu")), Is.True);
            Assert.That(local.Data, Is.Empty);
        }

        [Test]
        public async Task TestSourceFolderRenameRetainsLink()
        {
            await run();
            Guid original = setID;
            Directory.Move(song, Path.Combine(songs, "Renamed folder"));
            var result = await run();
            Assert.That(result.Issues, Is.Empty);
            Assert.That(result.Imported + result.Exported, Is.Zero);
            Assert.That(setID, Is.EqualTo(original));
            editLocal("audio.mp3", "new audio");
            Assert.That((await run()).Exported, Is.EqualTo(1));
            Assert.That(File.ReadAllText(Path.Combine(songs, "Renamed folder", "audio.mp3")), Is.EqualTo("new audio"));
        }

        [Test]
        public async Task TestNestedSongsDirectories()
        {
            string nested = Path.Combine(songs, "nested");
            Directory.CreateDirectory(nested);
            Directory.Move(song, Path.Combine(nested, "set"));
            Assert.That((await run()).Imported, Is.EqualTo(1));
        }

        [Test]
        public async Task TestPublishUnlinkedLocalSetOnlyOnRequest()
        {
            await run();
            Guid added = Guid.NewGuid();
            local.Data[added] = new Dictionary<string, byte[]> { ["new.osu"] = Encoding.UTF8.GetBytes("new map") };
            Assert.That((await run()).Exported, Is.Zero);
            var result = await sync.Synchronize(songs, selected: added, forceHash: true);
            Assert.That(result.Exported, Is.EqualTo(1));
            Assert.That(result.Issues, Is.Empty);
            Assert.That(File.ReadAllText(Path.Combine(songs, $"Replay Practice {added:N}", "new.osu")), Is.EqualTo("new map"));
            Assert.That((await run()).Imported + (await run()).Exported, Is.Zero);
        }

        [Test]
        public async Task TestRefuseSourceRootOverlappingLocalStorage()
        {
            Directory.CreateDirectory(storage.GetFullPath("Songs"));
            Assert.ThrowsAsync<IOException>(async () => await sync.Synchronize(storage.GetFullPath("Songs")));
            await Task.CompletedTask;
        }

        [Test]
        public async Task TestCorruptStateFailsClosed()
        {
            await run();
            string state = Directory.GetFiles(storage.GetFullPath("stable-sync"), "*.json").Single();
            File.WriteAllText(state, "not json");
            Assert.ThrowsAsync<Newtonsoft.Json.JsonReaderException>(async () => await run());
            Assert.That(local.ImportCount, Is.EqualTo(1));
        }

        [Test]
        public async Task TestForceHashDetectsSameSizeAndTimestampChange()
        {
            await run();
            string path = Path.Combine(song, "audio.mp3");
            var written = File.GetLastWriteTimeUtc(path);
            write("audio.mp3", "replaced audio");
            File.SetLastWriteTimeUtc(path, written);
            Assert.That((await run()).Imported, Is.EqualTo(1));
            Assert.That(localText("audio.mp3"), Is.EqualTo("replaced audio"));
        }

        [Test]
        public async Task TestGuardPreventsLocalOverwriteDuringImport()
        {
            await run();
            write("audio.mp3", "new stable audio");
            local.BeforeImport = () => editLocal("audio.mp3", "concurrent local edit");
            Assert.ThrowsAsync<OperationCanceledException>(async () => await run());
            Assert.That(localText("audio.mp3"), Is.EqualTo("concurrent local edit"));
            local.BeforeImport = null;
            Assert.That((await run()).Issues, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task TestPausedSyncDoesNotTouchEitherSide()
        {
            Assert.ThrowsAsync<OperationCanceledException>(async () => await sync.Synchronize(songs, () => false));
            Assert.That(local.Data, Is.Empty);
            await run();
            editLocal("audio.mp3", "local edit");
            Assert.ThrowsAsync<OperationCanceledException>(async () => await sync.Synchronize(songs, () => false));
            Assert.That(File.ReadAllText(Path.Combine(song, "audio.mp3")), Is.EqualTo("original audio"));
        }

        [Test]
        public async Task TestInterruptedWriteBlocksAutomaticRetryAndCanBeResolved()
        {
            await run();
            editLocal("audio.mp3", "local edit");
            bool allowed = true;
            local.BeforeOpen = (_, forStable) =>
            {
                if (!forStable)
                    allowed = false; // after staging, during backup
            };
            Assert.ThrowsAsync<OperationCanceledException>(async () => await sync.Synchronize(songs, () => allowed, forceHash: true));
            local.BeforeOpen = null;
            var retry = await run();
            Assert.That(retry.Issues.Single(), Does.Contain("interrupted"));
            Assert.That(File.ReadAllText(Path.Combine(song, "audio.mp3")), Is.EqualTo("original audio"));
            var resolved = await sync.Synchronize(songs, selected: setID, resolution: StableSyncResolution.UseLocal, forceHash: true);
            Assert.That(resolved.Exported, Is.EqualTo(1));
            Assert.That(resolved.Issues, Is.Empty);
        }

        [Test]
        public async Task TestInterruptedNewPublicationCanBeResolved()
        {
            await run();
            Guid added = Guid.NewGuid();
            local.Data[added] = new Dictionary<string, byte[]> { ["new.osu"] = Encoding.UTF8.GetBytes("new map") };
            bool allowed = true;
            local.BeforeOpen = (_, forStable) =>
            {
                if (!forStable)
                    allowed = false;
            };
            Assert.ThrowsAsync<OperationCanceledException>(async () => await sync.Synchronize(songs, () => allowed, selected: added, forceHash: true));
            local.BeforeOpen = null;
            var resumed = await sync.Synchronize(songs, selected: added, resolution: StableSyncResolution.UseLocal, forceHash: true);
            Assert.That(resumed.Issues, Is.Empty);
            Assert.That(resumed.Exported, Is.EqualTo(1));
            Assert.That(File.ReadAllText(Path.Combine(songs, $"Replay Practice {added:N}", "new.osu")), Is.EqualTo("new map"));
        }

        [Test]
        public async Task TestDuplicateSourcesDoNotShareWritableLink()
        {
            await run();
            string duplicate = Path.Combine(songs, "Duplicate");
            Directory.CreateDirectory(duplicate);
            File.Copy(Path.Combine(song, "map.osu"), Path.Combine(duplicate, "map.osu"));
            var result = await run();
            Assert.That(result.Imported + result.Exported, Is.Zero);
            Assert.That(result.Issues.Single(), Does.Contain("Duplicate"));
            Assert.That(local.Data, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task TestSourceSymlinkIsNotFollowed()
        {
            await run();
            string outside = sourceStorage.GetFullPath("outside.txt");
            File.WriteAllText(outside, "outside");
            try
            {
                File.CreateSymbolicLink(Path.Combine(song, "linked.txt"), outside);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                Assert.Ignore("Symbolic link creation is unavailable on this filesystem.");
            }
            var result = await run();
            Assert.That(result.Issues.Single(), Does.Contain("Symbolic links"));
            Assert.That(File.ReadAllText(outside), Is.EqualTo("outside"));
        }

        [Test]
        public async Task TestStableResourceEditDoesNotReimportLossyConvertedBeatmap()
        {
            await run();
            local.ConvertForStable = true;
            editLocal("map.osu", "precise lazer map");
            Assert.That((await run()).Exported, Is.EqualTo(1));
            write("audio.mp3", "updated in stable");
            var inbound = await run();
            Assert.That(inbound.Issues, Is.Empty);
            Assert.That(inbound.Imported, Is.EqualTo(1));
            Assert.That(localText("audio.mp3"), Is.EqualTo("updated in stable"));
            Assert.That(localText("map.osu"), Is.EqualTo("precise lazer map"));
            Assert.That(File.ReadAllText(Path.Combine(song, "map.osu")), Is.EqualTo("precise lazer map\nconverted"));
            var unchanged = await run();
            Assert.That(unchanged.Imported + unchanged.Exported, Is.Zero);
        }

        [TestCase("CON")]
        [TestCase("NUL.osu")]
        [TestCase("folder/COM1.txt")]
        [TestCase("../escape.osu")]
        [TestCase("nested/../../escape")]
        [TestCase("C:/outside.osu")]
        [TestCase("map.osu:stream")]
        [TestCase("folder./map.osu")]
        [TestCase("/outside.osu")]
        [TestCase("folder//map.osu")]
        public void TestRejectUnsafePaths(string relative) => Assert.Throws<IOException>(() => StableBeatmapSync.SafePath(song, relative));

        private Task<StableSyncReport> run() => sync.Synchronize(songs, forceHash: true);
        private void write(string filename, string text) => File.WriteAllText(Path.Combine(song, filename), text, new UTF8Encoding(false));
        private void editLocal(string filename, string text) => local.Data[setID][filename] = Encoding.UTF8.GetBytes(text);
        private string localText(string filename) => Encoding.UTF8.GetString(local.Data[setID][filename]);
        private string[] backupFiles(string filename) => Directory.GetFiles(storage.GetFullPath("stable-sync/backups"), filename, SearchOption.AllDirectories);

        private class MemoryStore : IStableBeatmapSyncStore
        {
            public readonly Dictionary<Guid, Dictionary<string, byte[]>> Data = new Dictionary<Guid, Dictionary<string, byte[]>>();
            public int ImportCount;
            public bool ConvertForStable;
            public Action? BeforeImport;
            public Action<string, bool>? BeforeOpen;

            public StableSyncSet? Get(Guid id) => Data.TryGetValue(id, out var files)
                ? new StableSyncSet(id, files.ToDictionary(f => f.Key, f => Convert.ToHexString(SHA256.HashData(f.Value)).ToLowerInvariant(), StringComparer.OrdinalIgnoreCase))
                : null;

            public Stream OpenFile(StableSyncSet set, string filename, bool forStable)
            {
                BeforeOpen?.Invoke(filename, forStable);
                byte[] bytes = Data[set.ID][filename];
                if (forStable && ConvertForStable && filename.EndsWith(".osu", StringComparison.OrdinalIgnoreCase))
                    bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes) + "\nconverted");
                return new MemoryStream(bytes);
            }

            public Task<StableSyncSet> Import(string directory, Guid? original, Func<bool> canCommit, CancellationToken cancellationToken)
            {
                BeforeImport?.Invoke();
                if (!canCommit())
                    throw new OperationCanceledException();
                Guid id = original ?? Guid.NewGuid();
                Data[id] = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(
                    f => Path.GetRelativePath(directory, f).Replace('\\', '/'), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
                ImportCount++;
                return Task.FromResult(Get(id)!);
            }
        }
    }
}
