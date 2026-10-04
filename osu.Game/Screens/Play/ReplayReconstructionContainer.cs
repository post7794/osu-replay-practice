// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace osu.Game.Screens.Play
{
    /// <summary>
    /// Separates historical reconstruction from presentation. The complete gameplay tree,
    /// including storyboard, judgements and HUD, keeps updating while its output is hidden.
    /// Live transport controls and the preparation countdown remain outside this container.
    /// </summary>
    internal partial class ReplayReconstructionContainer : Container
    {
        public bool IsRestoring { get; private set; }

        public ReplayReconstructionContainer(GameplayClockContainer gameplay, bool restoring = false)
        {
            RelativeSizeAxes = Axes.Both;
            // Alpha zero must not prevent the initial replay frame or catch-up from executing.
            AlwaysPresent = true;
            Child = gameplay;
            SetRestoring(restoring);
        }

        public void SetRestoring(bool restoring)
        {
            IsRestoring = restoring;
            // No fade: expose only the committed target frame, never the traversed history.
            Alpha = restoring ? 0 : 1;
        }
    }
}
