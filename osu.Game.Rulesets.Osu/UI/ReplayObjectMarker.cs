// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Shapes;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Localisation;
using osuTK;

namespace osu.Game.Rulesets.Osu.UI
{
    /// <summary>A skin-independent annotation, not a hitobject or an approach circle.</summary>
    public partial class ReplayObjectMarker : CompositeDrawable
    {
        private readonly Container frame;
        private readonly Container badge;
        private readonly OsuSpriteText label;
        private RectangleF targetBounds;

        public int? TargetIndex { get; private set; }
        public RectangleF TargetBounds => targetBounds;

        public override bool HandlePositionalInput => false;
        public override bool HandleNonPositionalInput => false;

        public ReplayObjectMarker()
        {
            RelativeSizeAxes = Axes.Both;
            Depth = float.MinValue;
            Alpha = 0;
            InternalChildren = new Drawable[]
            {
                frame = new Container
                {
                    Children = new Drawable[]
                    {
                        new Corner { Anchor = Anchor.TopLeft },
                        new Corner { Anchor = Anchor.TopRight, Rotation = 90 },
                        new Corner { Anchor = Anchor.BottomRight, Rotation = 180 },
                        new Corner { Anchor = Anchor.BottomLeft, Rotation = 270 },
                    }
                },
                badge = new Container
                {
                    AutoSizeAxes = Axes.Both,
                    Masking = true,
                    CornerRadius = 3,
                    Children = new Drawable[]
                    {
                        new Box { RelativeSizeAxes = Axes.Both, Colour = Colour4.Black, Alpha = 0.9f },
                        label = new OsuSpriteText
                        {
                            Font = OsuFont.GetFont(size: 14, weight: FontWeight.Bold),
                            Colour = Colour4.FromHex("FFE36D"),
                            Margin = new MarginPadding { Horizontal = 6, Vertical = 3 },
                        },
                    },
                },
            };
        }

        public void SetTarget(int index, RectangleF bounds)
        {
            if (TargetIndex != index)
                label.Text = ReplayPracticeStrings.SelectedObjectMarker(index + 1);
            TargetIndex = index;
            targetBounds = bounds;
            frame.Position = new Vector2(bounds.Left - 6, bounds.Top - 6);
            frame.Size = new Vector2(bounds.Width + 12, bounds.Height + 12);
            Alpha = 1;
            positionBadge();
        }

        public void ClearTarget()
        {
            TargetIndex = null;
            Alpha = 0;
        }

        protected override void UpdateAfterChildren()
        {
            base.UpdateAfterChildren();
            if (TargetIndex.HasValue)
                positionBadge();
        }

        private void positionBadge()
        {
            float y = targetBounds.Top - badge.DrawHeight - 12;
            if (y < 0)
                y = targetBounds.Bottom + 12;
            badge.Position = new Vector2(
                Math.Clamp(targetBounds.Centre.X - badge.DrawWidth / 2, 0, Math.Max(0, DrawWidth - badge.DrawWidth)),
                Math.Clamp(y, 0, Math.Max(0, DrawHeight - badge.DrawHeight)));
        }

        private partial class Corner : CompositeDrawable
        {
            public Corner()
            {
                Size = new Vector2(18);
                InternalChildren = new Drawable[]
                {
                    new Box { Position = new Vector2(-2), Size = new Vector2(20, 7), Colour = Colour4.Black },
                    new Box { Position = new Vector2(-2), Size = new Vector2(7, 20), Colour = Colour4.Black },
                    new Box { Size = new Vector2(16, 3), Colour = Colour4.FromHex("FFE36D") },
                    new Box { Size = new Vector2(3, 16), Colour = Colour4.FromHex("FFE36D") },
                };
            }
        }
    }
}
