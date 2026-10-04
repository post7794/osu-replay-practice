// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Bindables;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Mods;
using osu.Game.Scoring;

namespace osu.Game.Screens.Play
{
    /// <summary>
    /// Keeps the replay loader alive across attempts without resuming disposed player components.
    /// Practice history is dropped as soon as the user returns to watching.
    /// </summary>
    internal class ReplayPlaybackSession
    {
        private Score? source;
        private readonly Func<IBeatmap, IReadOnlyList<Mod>, Score>? createScore;
        public Score Source => source ?? throw new InvalidOperationException("The generated replay has not been prepared yet.");
        public ReplayPracticeSession? Practice { get; private set; }
        private bool returning;
        public IReadOnlyList<Mod>? ModsForNextPlayer => Practice != null && !returning ? Practice.Mods : source?.ScoreInfo.Mods;
        private readonly BindableBool objectStartOnly = new BindableBool(true);
        private readonly BindableBool safePracticeStart = new BindableBool(false);
        private readonly ReplayPanelLayout panelLayout = new ReplayPanelLayout();
        private readonly ReplayFailureIndex failureIndex = new ReplayFailureIndex();
        public ReplayPlaybackSession(Score score)
        {
            ArgumentNullException.ThrowIfNull(score);
            if (score.Replay == null)
                throw new ArgumentException($"{nameof(score)} must have a non-null {nameof(score.Replay)}.", nameof(score));
            source = ReplayPracticeSession.CloneSource(score);
        }

        public ReplayPlaybackSession(Func<IBeatmap, IReadOnlyList<Mod>, Score> createScore)
        {
            ArgumentNullException.ThrowIfNull(createScore);
            this.createScore = createScore;
        }

        public Player CreatePlayer()
        {
            if (Practice != null && !returning)
                return new ReplayPracticePlayer(Practice, () => returning = true, () => Practice = null);

            var replay = new ReplayPlayer(source, createScore)
            {
                ReplayPrepared = score => source ??= ReplayPracticeSession.CloneSource(score),
                PracticeRequested = session => Practice = session,
                PanelLayout = panelLayout,
                FailureIndex = failureIndex,
                AutomaticallyChooseSafePracticeStart = safePracticeStart,
                IgnorePreviousObjectsDuringObjectPractice = objectStartOnly,
                InitialReplayTime = returning ? Practice!.StartTime : null,
                InitialReplayRate = returning ? Practice!.ReplayPlaybackRate : 1,
            };
            if (returning)
            {
                Practice = null;
                returning = false;
            }
            return replay;
        }
    }
}
