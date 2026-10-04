// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Screens;
using osu.Game.Scoring;

namespace osu.Game.Screens.Play
{
    public partial class ReplayPlayerLoader : PlayerLoader
    {
        public readonly ScoreInfo Score;

        private readonly ReplayPlaybackSession playback;

        internal ReplayPracticeSession? PracticeSession => playback.Practice;

        protected override bool ShowQuickRestartTransition => playback.Practice == null;

        public ReplayPlayerLoader(Score score)
            : this(new ReplayPlaybackSession(score))
        {
        }

        private ReplayPlayerLoader(ReplayPlaybackSession playback)
            : base(playback.CreatePlayer)
        {
            this.playback = playback;
            var score = playback.Source;
            if (score.Replay == null)
                throw new ArgumentException($"{nameof(score)} must have a non-null {nameof(score.Replay)}.", nameof(score));

            Score = score.ScoreInfo;
            WindowShouldBeActiveForGameplayStart = false;
        }

        public override void OnResuming(ScreenTransitionEvent e)
        {
            if (playback.ModsForNextPlayer is { } mods)
                Mods.Value = mods;
            base.OnResuming(e);
        }

        public override void OnEntering(ScreenTransitionEvent e)
        {
            // these will be reverted thanks to PlayerLoader's lease.
            Mods.Value = Score.Mods;
            Ruleset.Value = Score.Ruleset;

            base.OnEntering(e);
        }
    }
}
