// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Input.Bindings;
using osu.Framework.Testing;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Input.Bindings;
using osu.Game.Database;
using osu.Game.Overlays;
using osu.Game.Overlays.Settings.Sections.Input;
using osu.Game.Screens.Play.PlayerSettings;
using osuTK.Input;

namespace osu.Game.Tests.Visual.Settings
{
    public partial class TestSceneReplayShortcuts : OsuManualInputManagerTestScene
    {
        [Cached]
        private readonly OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Purple);

        [Resolved]
        private RealmAccess database { get; set; } = null!;

        private readonly KeyBindingPanel panel;
        private readonly ReplayKeyBindingButton button;
        private ReplayKeyBindingButton.ReplayKeyBindingPopover popover = null!;

        protected override bool UseFreshStoragePerRun => true;

        public TestSceneReplayShortcuts()
        {
            Child = new PopoverContainer
            {
                RelativeSizeAxes = Axes.Both,
                Children = new Drawable[]
                {
                    panel = new KeyBindingPanel(),
                    button = new ReplayKeyBindingButton
                    {
                        RelativeSizeAxes = Axes.None,
                        Anchor = Anchor.TopRight,
                        Origin = Anchor.TopRight,
                        Width = 270,
                        Margin = new MarginPadding(15),
                        Depth = -1,
                    },
                },
            };
        }

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("show official panel", () =>
            {
                button.HidePopover();
                panel.Show();
            });
            AddUntilStep("official rows ready", () => panel.ChildrenOfType<KeyBindingRow>().Any(r => (r.Action is GlobalAction a) && a == GlobalAction.ReturnToReplay));
            AddStep("reset replay binding baseline", () => database.Write(r =>
            {
                foreach (var group in GlobalActionContainer.GetDefaultBindingsFor(GlobalActionCategory.Replay).GroupBy(b => b.Action))
                {
                    foreach (var (binding, defaults) in r.All<RealmKeyBinding>().AsEnumerable()
                                 .Where(b => b.RulesetName == null && b.ActionInt == (int)group.Key).Zip(group))
                        binding.KeyCombination = defaults.KeyCombination;
                }
            }));
            AddWaitStep("bindings settled", 5);
        }

        [TestCase(GlobalAction.TakeOverReplay)]
        [TestCase(GlobalAction.RetryReplayPractice)]
        [TestCase(GlobalAction.ReturnToReplay)]
        [TestCase(GlobalAction.PreviousReplayObject)]
        [TestCase(GlobalAction.NextReplayObject)]
        [TestCase(GlobalAction.NextReplayMiss)]
        [TestCase(GlobalAction.NextReplayIgnored)]
        [TestCase(GlobalAction.PreviousReplayMiss)]
        [TestCase(GlobalAction.PreviousReplayIgnored)]
        [TestCase(GlobalAction.SeekReplayOneSecondBackward)]
        [TestCase(GlobalAction.SeekReplayOneSecondForward)]
        [TestCase(GlobalAction.IncreaseReplayPlaybackSpeed)]
        [TestCase(GlobalAction.DecreaseReplayPlaybackSpeed)]
        [TestCase(GlobalAction.ResetReplayPlaybackSpeed)]
        [TestCase(GlobalAction.ExitReplay)]
        public void TestEditorsSharePersistentBindings(GlobalAction action)
        {
            KeyBindingRow officialRow = null!;
            KeyBindingRow replayRow = null!;
            AddStep("locate official action", () => officialRow = panel.ChildrenOfType<KeyBindingRow>().Single(r => r.Action is GlobalAction a && a == action));
            AddStep("show official row before editing", () => panel.SectionsContainer.ScrollTo(officialRow));
            AddWaitStep("official editor active", 5);
            AddAssert("official default registered", () => officialRow.KeyBindings.Single().KeyCombination,
                () => Is.EqualTo(GlobalActionContainer.GetDefaultBindingsFor(GlobalActionCategory.Replay).Single(b => (GlobalAction)b.Action == action).KeyCombination));

            openReplayEditor();
            AddStep("locate replay action", () => replayRow = popover.ChildrenOfType<KeyBindingRow>().Single(r => r.Action is GlobalAction a && a == action));
            AddStep("scroll replay row into view", () => popover.ChildrenOfType<OsuScrollContainer>().Single().ScrollTo(replayRow));
            AddWaitStep("scroll settled", 5);
            beginBinding(() => replayRow);
            AddStep("bind K from replay", () => InputManager.Key(Key.K));
            AddUntilStep("binding persisted", () => storedCombination(action).Equals(new KeyCombination(InputKey.K)));
            AddUntilStep("official editor updated live", () => officialRow.KeyBindings.Single().KeyCombination.Equals(new KeyCombination(InputKey.K)));
            AddStep("close replay configuration", () => button.HidePopover());
            AddWaitStep("popover closed", 5);

            AddStep("scroll official row into view", () => panel.SectionsContainer.ScrollTo(officialRow));
            AddWaitStep("scroll settled", 5);
            beginBinding(() => officialRow);
            AddStep("bind J from official settings", () => InputManager.Key(Key.J));
            AddUntilStep("official binding persisted", () => storedCombination(action).Equals(new KeyCombination(InputKey.J)));

            openReplayEditor();
            AddUntilStep("reopened replay editor sees J", () => popover.ChildrenOfType<KeyBindingRow>().Single(r => r.Action is GlobalAction a && a == action)
                .KeyBindings.Single().KeyCombination.Equals(new KeyCombination(InputKey.J)));
            AddStep("external update while both editors exist", () => database.Write(r => r.All<RealmKeyBinding>().Single(b => b.RulesetName == null && b.ActionInt == (int)action)
                .KeyCombination = new KeyCombination(InputKey.F7)));
            AddUntilStep("replay editor updates live", () => popover.ChildrenOfType<KeyBindingRow>().Single(r => r.Action is GlobalAction a && a == action)
                .KeyBindings.Single().KeyCombination.Equals(new KeyCombination(InputKey.F7)));
            AddUntilStep("official editor updates live again", () => officialRow.KeyBindings.Single().KeyCombination.Equals(new KeyCombination(InputKey.F7)));
            AddStep("restore defaults", () => officialRow.RestoreDefaults());
            AddUntilStep("default visible in both editors", () => officialRow.IsDefault.Value && popover.ChildrenOfType<KeyBindingRow>().Single(r => r.Action is GlobalAction a && a == action).IsDefault.Value);
            AddStep("close", () => button.HidePopover());
        }

        [Test]
        public void TestConflictResolutionInsideReplayEditor()
        {
            KeyBindingRow row = null!;
            openReplayEditor();
            AddStep("locate takeover row", () =>
            {
                row = popover.ChildrenOfType<KeyBindingRow>().Single(r => (GlobalAction)r.Action == GlobalAction.TakeOverReplay);
                popover.ChildrenOfType<OsuScrollContainer>().Single().ScrollTo(row);
            });
            AddWaitStep("scroll settled", 5);
            beginBinding(() => row);
            AddStep("conflict with replay pause", () => InputManager.Key(Key.Space));
            AddUntilStep("conflict prompt loaded", () => popover.ChildrenOfType<KeyBindingConflictPopover>().Any(p => p.IsLoaded && p.Alpha > 0));
            AddStep("apply new binding", () => popover.ChildrenOfType<KeyBindingConflictPopover>().Single()
                .ChildrenOfType<RoundedButton>().Last().TriggerClick());
            AddUntilStep("new binding persisted", () => storedCombination(GlobalAction.TakeOverReplay).Equals(new KeyCombination(InputKey.Space)));
            AddUntilStep("old conflicting binding cleared", () => !database.Run(r => r.All<RealmKeyBinding>().AsEnumerable()
                .Any(b => b.RulesetName == null && b.ActionInt == (int)GlobalAction.TogglePauseReplay && b.KeyCombination.Equals(new KeyCombination(InputKey.Space)))));
            AddAssert("replay editor remains open", () => popover.Alpha > 0);
            AddStep("close", () => button.HidePopover());
        }

        private KeyCombination storedCombination(GlobalAction action) => database.Run(r => r.All<RealmKeyBinding>()
            .Single(b => b.RulesetName == null && b.ActionInt == (int)action).KeyCombination);

        private void openReplayEditor()
        {
            AddStep("open replay editor", () => button.TriggerClick());
            AddUntilStep("replay editor loaded", () => this.ChildrenOfType<ReplayKeyBindingButton.ReplayKeyBindingPopover>().Any(p => p.IsLoaded && p.Alpha > 0));
            AddStep("capture current popover", () => popover = this.ChildrenOfType<ReplayKeyBindingButton.ReplayKeyBindingPopover>().Last(p => p.IsLoaded && p.Alpha > 0));
        }

        private void beginBinding(System.Func<KeyBindingRow> getRow)
        {
            AddStep("click key binding", () =>
            {
                InputManager.MoveMouseTo(getRow().ChildrenOfType<KeyBindingRow.KeyButton>().Single());
                InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("row capturing", () => getRow().HasFocus);
        }
    }
}
