// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Screens;
using osu.Framework.Testing;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Localisation;
using osu.Game.Overlays.Settings;
using osu.Game.Overlays.Settings.Sections.Maintenance;
using osu.Game.Rulesets;
using osu.Game.Scoring;
using osu.Game.Tests.Database;
using osu.Game.Screens.Edit;
using osu.Game.Screens.Select;
using osu.Game.Tests.Resources;
using osuTK.Input;

namespace osu.Game.Tests.Visual.Navigation
{
    [HeadlessTest]
    [Platform("Win")]
    public partial class TestSceneLazerBeatmapSync : OsuGameTestScene
    {
        private TemporaryNativeStorage source = null!;
        private string songs = null!;

        private BeatmapSetInfo set = null!;
        private LazerBeatmapSyncManager sync => Game.ChildrenOfType<LazerBeatmapSyncManager>().Single();

        [Resolved]
        private FrameworkConfigManager frameworkConfig { get; set; } = null!;

        public override void SetUpSteps()
        {
            base.SetUpSteps();
            AddUntilStep("sync service loaded", () => Game.ChildrenOfType<LazerBeatmapSyncManager>().SingleOrDefault()?.IsLoaded == true);
            AddStep("prepare independent official lazer", () =>
            {
                source?.Dispose();
                source = new TemporaryNativeStorage($"lazer-sync-game-flow-{Guid.NewGuid():N}");
                songs = source.GetFullPath(string.Empty);
                using (var officialRealm = new RealmAccess(source, "client"))
                using (var rulesets = new RealmRulesetStore(officialRealm, source))
                {
                    var importer = new BeatmapImporter(source, officialRealm);
                    Assert.That(importer.Import(new ImportTask(TestResources.GetQuickTestBeatmapForImport())).GetAwaiter().GetResult(), Is.Not.Null);
                }
                Assert.That(Game.LocalConfig.Get<bool>(OsuSetting.LazerSyncEnabled), Is.False);
                Game.LocalConfig.SetValue(OsuSetting.LazerSyncPath, songs);
                Game.LocalConfig.SetValue(OsuSetting.LazerSyncEnabled, true);
                sync.RequestSync(forceHash: true);
            });
            AddUntilStep("automatic import completed", () => !sync.Busy.Value && Game.BeatmapManager.GetAllUsableBeatmapSets().Any(s => !s.Protected));
            AddUntilStep("sync successful", () => !sync.Busy.Value && sync.Status.Value.ToString().Contains("attention: 0", StringComparison.Ordinal));
            AddStep("get synced set", () => set = Game.BeatmapManager.GetAllUsableBeatmapSets().First(s => !s.Protected));
        }

        [TestCase("en")]
        [TestCase("zh")]
        public void TestSettingsControlsAndBoundStatus(string locale)
        {
            string previousLocale = string.Empty;
            LazerSyncSettings settings = null!;
            SettingsNote[] notes = null!;
            FormCheckBox checkbox = null!;
            AddStep("set language", () =>
            {
                previousLocale = frameworkConfig.Get<string>(FrameworkSetting.Locale);
                frameworkConfig.SetValue(FrameworkSetting.Locale, locale);
            });
            AddStep("show settings", () => Game.Settings.Show());
            AddUntilStep("sync settings loaded", () => Game.Settings.ChildrenOfType<LazerSyncSettings>().Any(s => s.IsLoaded));
            AddStep("get controls", () =>
            {
                settings = Game.Settings.ChildrenOfType<LazerSyncSettings>().Single();
                notes = settings.ChildrenOfType<SettingsNote>().Where(n => n.Current.Value != null).ToArray();
                checkbox = settings.ChildrenOfType<FormCheckBox>().Single();
            });
            AddAssert("both notes exist", () => notes.Length, () => Is.EqualTo(2));
            AddAssert("help and status notes have usable width", () => notes.Select(n => n.DrawWidth).ToArray(), () => Is.All.GreaterThan(100));
            AddStep("scroll to help", () => Game.Settings.SectionsContainer.ScrollTo(notes[0]));
            AddUntilStep("help wraps without pushing controls away", () => notes[0].DrawHeight > 0 && notes[0].DrawHeight < 500);
            AddAssert("all sync action buttons exist", () => settings.ChildrenOfType<SettingsButtonV2>().Count(), () => Is.EqualTo(6));
            AddAssert("enable checkbox is bound", () => checkbox.Current.Value);
            AddAssert("source path is bound", () => settings.ChildrenOfType<FormTextBox>().Single().Current.Value, () => Is.EqualTo(songs));
            AddStep("scroll to checkbox", () => Game.Settings.SectionsContainer.ScrollTo(checkbox));
            AddUntilStep("checkbox is visible", () => checkbox.IsPresent && Game.Settings.SectionsContainer.ScreenSpaceDrawQuad.Contains(checkbox.ScreenSpaceDrawQuad.Centre));
            AddStep("disable via checkbox", () =>
            {
                InputManager.MoveMouseTo(checkbox);
                InputManager.Click(MouseButton.Left);
            });
            AddAssert("disabled persisted", () => !Game.LocalConfig.Get<bool>(OsuSetting.LazerSyncEnabled));
            AddUntilStep("status updated", () => notes.Any(n => n.Current.Value?.Text == LazerSyncStrings.Disabled));
            AddStep("scroll to status", () => Game.Settings.SectionsContainer.ScrollTo(notes[1]));
            AddUntilStep("status has usable height", () => notes[1].DrawHeight > 0 && notes[1].DrawHeight < 500);
            AddStep("restore language", () => frameworkConfig.SetValue(FrameworkSetting.Locale, previousLocale));
        }

        [Test]
        public void TestRealEditorSaveWritesBackOnlyAfterLeavingEditor()
        {
            string oldPath = null!;
            string newPath = null!;
            AddStep("present synced set", () => Game.PresentBeatmap(set));
            AddUntilStep("song select ready", () => Game.ScreenStack.CurrentScreen is SoloSongSelect select && select.CarouselItemsPresented);
            AddStep("open editor", () => ((SoloSongSelect)Game.ScreenStack.CurrentScreen).Edit(set.Beatmaps.First(b => b.Ruleset.OnlineID == 0)));
            AddUntilStep("editor ready", () => Game.ScreenStack.CurrentScreen is Editor editor && editor.ReadyForUse);
            AddStep("rename and save difficulty", () =>
            {
                var editor = (Editor)Game.ScreenStack.CurrentScreen;
                var editable = editor.ChildrenOfType<EditorBeatmap>().Single();
                oldPath = editable.BeatmapInfo.Path!;
                editable.BeatmapInfo.DifficultyName = "Sync renamed";
                Assert.That(editor.Save(), Is.True);
                newPath = editable.BeatmapInfo.Path!;
            });
            AddStep("request sync inside editor", () => sync.RequestSync(forceHash: true));
            AddAssert("sync deferred", () => sync.Status.Value == LazerSyncStrings.Waiting && !sync.Busy.Value);
            AddAssert("source not changed yet", () =>
            {
                using var official = new LazerLibraryConnection(Game.Storage, songs);
                var files = official.GetAll().Single().Files;
                return files.ContainsKey(oldPath!) && !files.ContainsKey(newPath!);
            });
            AddStep("exit editor", () => Game.ScreenStack.CurrentScreen.Exit());
            AddUntilStep("back at song select", () => Game.ScreenStack.CurrentScreen is SoloSongSelect);
            AddStep("sync saved edit", () => sync.RequestSync(forceHash: true));
            AddUntilStep("write completed", () => !sync.Busy.Value && sync.Status.Value.ToString().Contains("Beatmaps in/out: 0/1", StringComparison.Ordinal));
            AddStep("check official database and raw file", () =>
            {
                using var official = new LazerLibraryConnection(Game.Storage, songs);
                var updated = official.GetAll().Single();
                Assert.That(updated.Files.ContainsKey(oldPath!), Is.False);
                Assert.That(updated.Files.ContainsKey(newPath!), Is.True);
                using var input = official.OpenFile(updated, newPath!, false);
                using var reader = new StreamReader(input);
                Assert.That(reader.ReadToEnd(), Does.Contain("Version: Sync renamed"));
            });
            AddStep("sync again", () => sync.RequestSync(forceHash: true));
            AddUntilStep("no echo", () => !sync.Busy.Value && sync.Status.Value.ToString().Contains("Beatmaps in/out: 0/0; replays in/out: 0/0; needs attention: 0"));
        }

        [Test]
        public void TestSyncedReplayIsAvailableToNormalScoreManager()
        {
            Guid id = Guid.Empty;
            AddStep("save a real replay in official library", () =>
            {
                using var realm = RealmAccess.OpenSyncSnapshot(source, "client.realm");
                id = LazerReplaySyncTest.SeedReplay(realm, source, 1).ID;
            });
            AddStep("sync replay through menu service", () => sync.RequestSync());
            AddUntilStep("replay imported", () => !sync.Busy.Value && sync.Status.Value.ToString().Contains("replays in/out: 1/0", StringComparison.Ordinal));
            AddStep("load replay through normal score manager", () =>
            {
                var scores = Game.Dependencies.Get<ScoreManager>();
                var score = scores.Query(s => s.ID == id);
                Assert.That(score, Is.Not.Null);
                var loaded = scores.GetScore(score!);
                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded!.Replay.Frames, Has.Count.EqualTo(3));
                Assert.That(loaded.Replay.Frames[1].Time, Is.EqualTo(1000));
                Assert.That(loaded.ScoreInfo.BeatmapInfo!.Hash, Is.EqualTo(score!.BeatmapHash));
            });
        }
        public override void TearDownSteps()
        {
            AddStep("disable sync", () => Game.LocalConfig.SetValue(OsuSetting.LazerSyncEnabled, false));
            AddUntilStep("worker idle", () => !sync.Busy.Value);
            base.TearDownSteps();
            AddStep("dispose source", () => source?.Dispose());
        }
    }
}
