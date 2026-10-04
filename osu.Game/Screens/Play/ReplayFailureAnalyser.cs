// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Threading.Tasks;
using osu.Framework.Audio;
using osu.Framework.Audio.Track;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Screens;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.UI;
using osu.Game.Scoring;
using osu.Game.Screens.Ranking;

namespace osu.Game.Screens.Play
{
    /// <summary>
    /// Off-screen, input-only replay reconstruction on a virtual audio clock.
    /// Hosted by its own hidden ScreenStack, never controls the real song or enters any results/submission path.
    /// </summary>
    internal partial class ReplayFailureAnalyser : Player
    {
        private readonly Score source;
        private readonly ReplayFailureIndex index;
        private readonly int generation;
        private bool ownsAnalysis => index.IsAnalysing && index.Generation == generation;
        private readonly BindableDouble silence = new BindableDouble(0);
        private bool soughtEnd;
        private double end;

        public ReplayFailureAnalyser(Score source, ReplayFailureIndex index)
            : base(new PlayerConfiguration
            {
                AllowUserInteraction = false,
                AllowRestart = false,
                AllowSkipping = false,
                ShowFailingOverlay = false,
                ShowResults = false,
                ShowLeaderboard = false,
            })
        {
            this.source = source;
            this.index = index;
            generation = index.Generation;
            Alpha = 0;
            AlwaysPresent = true;
        }

        public override bool HandlePositionalInput => false;
        public override bool HandleNonPositionalInput => false;
        protected override bool PauseOnFocusLost => false;
        protected override Score CreateScore(IBeatmap beatmap) => ReplayPracticeSession.CloneSource(source);
        protected override GameplayClockContainer CreateGameplayClockContainer(WorkingBeatmap beatmap, double gameplayStart)
            => new AnalysisClock(new TrackVirtual(beatmap.Track.Length), gameplayStart);

        // Satisfy the framework's Screen lifecycle without Player's background, music or entry side effects.
        public override void OnEntering(ScreenTransitionEvent e) { }
        public override bool OnExiting(ScreenExitEvent e) => false;

        protected override void PrepareReplay()
        {
            DrawableRuleset.SetRecordTarget(null);
            ((IReplayPracticeRuleset)DrawableRuleset).SetReplayScoreForFailureAnalysis(Score);
            DrawableRuleset.Audio.AddAdjustment(AdjustableProperty.Volume, silence);
            if (ownsAnalysis)
                index.BindObjects(GameplayState.Beatmap.HitObjects);
            DrawableRuleset.NewResult += result =>
            {
                if (ownsAnalysis)
                    index.Add(result);
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            if (!ownsAnalysis)
                return;
            if (!LoadedBeatmapSuccessfully)
            {
                index.Fail();
                return;
            }
            end = GameplayState.Beatmap.GetLastObjectTime() + 1000;
            foreach (var mod in GameplayState.Mods.OfType<IApplicableToPlayer>())
                mod.ApplyToPlayer(this);
            foreach (var mod in GameplayState.Mods.OfType<IApplicableToTrack>())
                mod.ApplyToTrack(GameplayClockContainer.AdjustmentsFromMods);
            GameplayClockContainer.Reset(Math.Min(0, DrawableRuleset.GameplayStartTime));
        }

        protected override void UpdateAfterChildren()
        {
            base.UpdateAfterChildren();
            if (!ownsAnalysis || !LoadedBeatmapSuccessfully)
                return;
            var practiceRuleset = (IReplayPracticeRuleset)DrawableRuleset;
            if (!soughtEnd && practiceRuleset.IsReplayPracticeReady(GameplayClockContainer.CurrentTime))
            {
                soughtEnd = true;
                GameplayClockContainer.Seek(end);
            }
            else if (soughtEnd && practiceRuleset.IsReplayPracticeReady(end) && ScoreProcessor.HasCompleted.Value)
            {
                index.Complete();
            }
        }

        protected override bool CheckModsAllowFailure() => false;
        protected override void CheckScoreCompleted() { }
        protected override Task ImportScore(Score score) => Task.CompletedTask;
        protected override Task PrepareScoreForResultsAsync(Score score) => Task.CompletedTask;
        protected override ResultsScreen CreateResults(ScoreInfo score) => throw new InvalidOperationException("Replay analysis cannot show results.");

        protected override void Dispose(bool isDisposing)
        {
            if (isDisposing && ownsAnalysis)
                index.Cancel();
            base.Dispose(isDisposing);
        }

        private partial class AnalysisClock : GameplayClockContainer
        {
            private readonly TrackVirtual track;

            public AnalysisClock(TrackVirtual track, double gameplayStart)
                : base(track, applyOffsets: false, requireDecoupling: true)
            {
                this.track = track;
                GameplayStartTime = gameplayStart;
                StartTime = Math.Min(0, gameplayStart);
                track.BindAdjustments(AdjustmentsFromMods);
            }

            protected override void Dispose(bool isDisposing)
            {
                if (isDisposing)
                {
                    track.UnbindAdjustments(AdjustmentsFromMods);
                    track.Dispose();
                }
                base.Dispose(isDisposing);
            }
        }
    }
}
