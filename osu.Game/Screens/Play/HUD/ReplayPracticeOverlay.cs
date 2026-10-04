// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace osu.Game.Screens.Play.HUD
{
    public partial class ReplayPracticeOverlay : CompositeDrawable
    {
        public ReplayPracticeOverlay(ReplayPracticePlayer player)
        {
            RelativeSizeAxes = Axes.Both;
            Depth = float.MinValue;
            InternalChildren = new Drawable[]
            {
                new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Child = new ReplayPracticeCountdown(player),
                },
                new ReplayTransportControls(player),
            };
        }
    }
}
