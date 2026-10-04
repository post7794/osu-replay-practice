// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.UserInterface;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Input.Bindings;
using osu.Game.Localisation;
using osu.Game.Overlays;
using osu.Game.Overlays.Settings.Sections.Input;
using osuTK;

namespace osu.Game.Screens.Play.PlayerSettings
{
    /// <summary>
    /// Opens the official replay binding editor without leaving the replay or practice session.
    /// </summary>
    public partial class ReplayKeyBindingButton : RoundedButton, IHasPopover
    {
        public ReplayKeyBindingButton()
        {
            Text = ReplayPracticeStrings.ConfigureShortcuts;
            RelativeSizeAxes = Axes.X;
            Action = () => this.ShowPopover();
        }

        public Popover GetPopover() => new ReplayKeyBindingPopover();

        public partial class ReplayKeyBindingPopover : OsuPopover
        {
            [Cached]
            private readonly OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Purple);

            public ReplayKeyBindingPopover()
            {
                // Conflicts use a nested popover so they retain this editor's dependencies and do not dismiss it.
                Child = new PopoverContainer
                {
                    Size = new Vector2(400, 360),
                    Child = new OsuScrollContainer
                    {
                        RelativeSizeAxes = Axes.Both,
                        Child = new GlobalKeyBindingsSubsection(InputSettingsStrings.ReplaySection, GlobalActionCategory.Replay)
                        {
                            TrackExternalChanges = true,
                        },
                    },
                };
            }
        }
    }
}
