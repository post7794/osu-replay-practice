// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Graphics.Primitives;
using osuTK;

namespace osu.Game.Screens.Play
{
    public enum ReplayPanelDock
    {
        None,
        Left,
        Right,
    }

    public enum ReplayPanelSide
    {
        Left,
        Right,
    }

    /// <summary>
    /// Session-only panel placement, independent of any disposable player or window resolution.
    /// </summary>
    public class ReplayPanelLayout
    {
        public ReplayPanelDock Dock { get; set; } = ReplayPanelDock.Right;
        public ReplayPanelSide Side { get; set; } = ReplayPanelSide.Right;
        public Vector2 RelativePosition { get; set; } = new Vector2(1, 0.5f);
        public Vector2 PreferredSize { get; set; } = new Vector2(220, 390);
    }

    internal static class ReplayPanelGeometry
    {
        /// <summary>
        /// The viewport's side gutters, excluding the playfield and a gap on both boundaries.
        /// Never manufactures a minimum width when there is no safe space.
        /// </summary>
        public static RectangleF GetSideBounds(RectangleF viewport, RectangleF playfield, ReplayPanelSide side)
        {
            const float gap = 4;
            float left = viewport.Left + gap;
            float right = Math.Max(left, viewport.Right - gap);
            float top = viewport.Top + gap;
            float height = Math.Max(0, viewport.Height - 2 * gap);
            if (side == ReplayPanelSide.Left)
                right = Math.Clamp(playfield.Left - gap, left, right);
            else
                left = Math.Clamp(playfield.Right + gap, left, right);
            return new RectangleF(left, top, Math.Max(0, right - left), height);
        }
    }
}
