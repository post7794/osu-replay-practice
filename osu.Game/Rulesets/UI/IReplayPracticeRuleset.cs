// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Judgements;
using osu.Game.Scoring;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Timing;

namespace osu.Game.Rulesets.UI
{
    /// <summary>
    /// Ruleset-specific replay takeover, deliberately separate from normal replay playback.
    /// </summary>
    public interface IReplayPracticeRuleset
    {
        /// <summary>
        /// Loads input for detached failure analysis, including ruleset-specific judgement checkpoints.
        /// Does not change normal replay playback or manual practice.
        /// </summary>
        void SetReplayScoreForFailureAnalysis(Score score);
        void SetReplayObjectPreview(int? objectIndex);
        ReplayPracticeRange PrepareObjectPractice(int startingObjectIndex);
        double GetSafeReplayPracticeTime(double requestedTime);
        bool IsReplayPracticeReady(double time);
        ReplayPracticePreparation CreateReplayPracticePreparation(IFrameBasedClock clock);
    }

    /// <summary>
    /// A practice-only range retaining the converted objects and their already-restored judgements.
    /// </summary>
    public record ReplayPracticeRange(IBeatmap Beatmap, IReadOnlyList<JudgementResult> RestoredResults);

    /// <summary>
    /// Collects live input independently of the frozen gameplay subtree. Does not judge objects.
    /// </summary>
    public abstract partial class ReplayPracticePreparation : CompositeDrawable
    {
        protected ReplayPracticePreparation()
        {
            RelativeSizeAxes = Axes.Both;
            Depth = float.MinValue;
        }

        /// <summary>
        /// Detaches the replay and transfers the current physical input without replaying old clicks.
        /// </summary>
        public abstract void TakeOver();
    }
}
