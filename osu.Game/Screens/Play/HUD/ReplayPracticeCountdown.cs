// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Localisation;
using osuTK;

namespace osu.Game.Screens.Play.HUD
{
    /// <summary>
    /// Visible preparation cue, driven by the player's real clock rather than frozen gameplay time.
    /// </summary>
    public partial class ReplayPracticeCountdown : CompositeDrawable
    {
        private readonly ReplayPracticePlayer player;
        private readonly OsuSpriteText number;

        public int SecondsRemaining { get; private set; }

        public override bool HandlePositionalInput => false;
        public override bool HandleNonPositionalInput => false;

        public ReplayPracticeCountdown(ReplayPracticePlayer player)
        {
            this.player = player;
            Anchor = Anchor.Centre;
            Origin = Anchor.Centre;
            // Leave the spinner centre visible while the player pre-positions the cursor.
            Y = -110;
            Size = new Vector2(180, 170);
            AlwaysPresent = true;
            Alpha = 0;

            InternalChild = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Masking = true,
                CornerRadius = 20,
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = Colour4.Black, Alpha = 0.8f },
                    new OsuSpriteText
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Y = 14,
                        Text = ReplayPracticeStrings.GetReady,
                        Font = OsuFont.GetFont(size: 20, weight: FontWeight.Bold),
                    },
                    number = new OsuSpriteText
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Y = 7,
                        Font = OsuFont.GetFont(size: 100, weight: FontWeight.Bold),
                    },
                    new OsuSpriteText
                    {
                        Anchor = Anchor.BottomCentre,
                        Origin = Anchor.BottomCentre,
                        Y = -14,
                        Text = ReplayPracticeStrings.PositionKeys,
                        Font = OsuFont.GetFont(size: 15),
                    },
                },
            };
        }

        protected override void Update()
        {
            base.Update();
            Y = -(float)Math.Max(110, player.DrawHeight * 0.18);

            int remaining = player.Session.State == ReplayPracticeState.Preparing
                ? Math.Clamp((int)Math.Ceiling(player.PreparationRemaining / 1000), 1, 3)
                : 0;

            Alpha = remaining > 0 ? 1 : 0;
            if (remaining == SecondsRemaining)
                return;

            SecondsRemaining = remaining;
            number.Text = remaining.ToString();
            number.ScaleTo(1.15f).ScaleTo(1, 180, Easing.OutQuint);
        }
    }
}
