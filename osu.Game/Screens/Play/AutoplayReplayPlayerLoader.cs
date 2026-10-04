// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Screens;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Mods;
using osu.Game.Scoring;

namespace osu.Game.Screens.Play
{
    /// <summary>
    /// Generates an autoplay baseline once, in memory, then shares the normal replay practice lifecycle.
    /// No score is imported and no replay is recorded or exported.
    /// </summary>
    public partial class AutoplayReplayPlayerLoader : PlayerLoader
    {
        private readonly ReplayPlaybackSession playback;
        internal ReplayPracticeSession? PracticeSession => playback.Practice;
        public override bool ShowFooter => !QuickRestart;
        protected override bool ShowQuickRestartTransition => playback.Practice == null;

        public AutoplayReplayPlayerLoader(Func<IBeatmap, IReadOnlyList<Mod>, Score> createScore)
            : this(new ReplayPlaybackSession(createScore))
        {
        }

        private AutoplayReplayPlayerLoader(ReplayPlaybackSession playback)
            : base(playback.CreatePlayer)
        {
            this.playback = playback;
        }

        public override void OnResuming(ScreenTransitionEvent e)
        {
            if (playback.ModsForNextPlayer is { } mods)
                Mods.Value = mods;
            base.OnResuming(e);
        }
    }
}
