// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading.Tasks;
using System.Linq;
using osu.Framework.Bindables;
using osu.Framework.Localisation;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using osu.Framework.Screens;
using osu.Game.Beatmaps;
using osu.Game.Input.Bindings;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.UI;
using osu.Game.Scoring;
using osu.Game.Screens.Play.HUD;
using osu.Game.Screens.Play.Leaderboards;
using osu.Game.Screens.Ranking;

namespace osu.Game.Screens.Play
{
    /// <summary>
    /// Standalone, non-submitting training player. Retrying discards the entire player graph.
    /// </summary>
    public partial class ReplayPracticePlayer : Player, IKeyBindingHandler<GlobalAction>, IReplayTransport
    {
        public const double PREPARATION_DURATION = 3000;
        public ReplayPracticeSession Session { get; }
        public double? AttemptAccuracy => maximumBaseScore > 0 ? baseScore / maximumBaseScore : null;
        public int AttemptMisses { get; private set; }
        public double PreparationRemaining => Session.State == ReplayPracticeState.Paused && stateBeforePause == ReplayPracticeState.Preparing
            ? preparationRemainingAtPause
            : Session.State == ReplayPracticeState.Preparing ? Math.Max(0, preparationEndsAt - Clock.CurrentTime) : 0;
        public bool CanTogglePracticePause => Session.State is ReplayPracticeState.Preparing or ReplayPracticeState.Playing or ReplayPracticeState.Paused;

        public double TransportTime => DrawableRuleset.FrameStableClock.CurrentTime;
        public double TransportEndTime => GameplayState.Beatmap.GetLastObjectTime();
        public double TransportSeekStartTime => Math.Min(GameplayClockContainer.StartTime, GameplayState.Beatmap.HitObjects.Min(ReplayTransport.ObjectAppearanceTime));
        public ReplayObjectSelection? SelectedObject => Session.NavigationSelection;
        public double? TransportStartTime => Session.StartTime;
        public bool IsTransportPaused => GameplayClockContainer.IsPaused.Value;
        public bool ToggleTransportPause() => TogglePracticePause();
        public bool CanSeekTransport => LoadedBeatmapSuccessfully && this.IsCurrentScreen()
            && Session.State is not (ReplayPracticeState.Restoring or ReplayPracticeState.Returning);
        public Bindable<double> TransportRate => Session.UserPlaybackRate;
        public ReplayPanelLayout PanelLayout => Session.PanelLayout;
        public ReplayFailureIndex FailureIndex => Session.FailureIndex;
        public LocalisableString? FailureNavigationMessage => failureNavigator?.Message;
        private ReplayFailureNavigator? failureNavigator;
        public void SeekNextFailure(ReplayFailureKind kind) => failureNavigator?.Request(kind);
        public void SeekPreviousFailure(ReplayFailureKind kind) => failureNavigator?.Request(kind, backwards: true);

        private readonly Action returnToReplay;
        private readonly Action? quitPractice;
        private ReplayPracticePreparation? preparation;
        private double preparationEndsAt;
        private double preparationRemainingAtPause;
        private ReplayPracticeState stateBeforePause;
        private double baseScore;
        private double maximumBaseScore;
        private bool restarting;
        private bool entered;
        private bool restoreSeekRequested;
        private bool rangePrepared;
        private ReplayReconstructionContainer replayPresentation = null!;

        [Cached(typeof(IGameplayLeaderboardProvider))]
        private readonly EmptyGameplayLeaderboardProvider leaderboard = new EmptyGameplayLeaderboardProvider();

        public ReplayPracticePlayer(ReplayPracticeSession session, Action returnToReplay, Action? quitPractice = null)
            : base(new PlayerConfiguration
            {
                ShowResults = false,
                ShowLeaderboard = false,
                AllowSkipping = false,
                ShowFailingOverlay = false,
            })
        {
            Session = session;
            Session.State = ReplayPracticeState.Restoring;
            this.returnToReplay = returnToReplay;
            this.quitPractice = quitPractice;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            if (!LoadedBeatmapSuccessfully)
                return;
            // This real-time popup host is disposed together with the attempt, including on retry/return.
            AddInternal(new PopoverContainer
            {
                RelativeSizeAxes = Axes.Both,
                Depth = float.MinValue,
                Child = new ReplayPracticeOverlay(this),
            });
            DrawableRuleset.NewResult += onResult;
            AddInternal(failureNavigator = new ReplayFailureNavigator(this, Session.CreateReplayScore, selection => seekTransport(selection.Time, selection)));
        }

        protected override Score CreateScore(IBeatmap beatmap) => Session.CreateReplayScore();

        protected override Container CreateGameplayPresentationContainer(GameplayClockContainer gameplay)
            => replayPresentation = new ReplayReconstructionContainer(gameplay, restoring: true);

        protected override void PrepareReplay()
        {
            // Do not call base: it installs a recorder.
            DrawableRuleset.SetRecordTarget(null);
            DrawableRuleset.SetReplayScore(Score);
        }

        protected override bool CheckModsAllowFailure() => false;
        protected override bool ShowGameplayEntryTransition => false;
        protected override Task ImportScore(Score score) => Task.CompletedTask;
        protected override Task PrepareScoreForResultsAsync(Score score) => Task.CompletedTask;
        protected override ResultsScreen CreateResults(ScoreInfo score) => throw new InvalidOperationException("Practice must never show normal results.");

        protected override void StartGameplay()
        {
            Session.State = ReplayPracticeState.Restoring;
            if (GameplayClockContainer is MasterGameplayClockContainer master)
                master.UserPlaybackRate.BindTo(Session.UserPlaybackRate);
            GameplayClockContainer.Reset(Math.Min(GameplayClockContainer.StartTime, Session.StartTime));
            entered = true;
        }

        protected override void Update()
        {
            base.Update();
            if (LoadedBeatmapSuccessfully && this.IsCurrentScreen())
                ((IReplayPracticeRuleset)DrawableRuleset).SetReplayObjectPreview(
                    Session.State is ReplayPracticeState.Restoring or ReplayPracticeState.Preparing or ReplayPracticeState.Paused ? Session.ObjectStartIndex : null);
        }

        protected override void UpdateAfterChildren()
        {
            base.UpdateAfterChildren();
            if (!entered || !LoadedBeatmapSuccessfully || !this.IsCurrentScreen() || Session.State == ReplayPracticeState.Paused)
                return;

            // Wait for the initial replay frame to actually run, not merely for a scheduler callback.
            // Hidden / not-yet-updated children must never have their first consumption at the takeover point.
            if (Session.State == ReplayPracticeState.Restoring && !restoreSeekRequested)
            {
                if (((IReplayPracticeRuleset)DrawableRuleset).IsReplayPracticeReady(GameplayClockContainer.CurrentTime))
                {
                    restoreSeekRequested = true;
                    GameplayClockContainer.Seek(Session.StartTime);
                }
                return;
            }

            if (Session.State == ReplayPracticeState.Restoring && ((IReplayPracticeRuleset)DrawableRuleset).IsReplayPracticeReady(Session.StartTime))
            {
                if (!rangePrepared)
                {
                    rangePrepared = true;
                    if (Session.UsesObjectRange)
                    {
                        var range = ((IReplayPracticeRuleset)DrawableRuleset).PrepareObjectPractice(Session.ObjectStartIndex!.Value);
                        // Count only retained objects, so removed slider tails neither miss nor block natural completion.
                        // This reset is explicit object-range practice, not exact historical takeover.
                        ScoreProcessor.ApplyBeatmap(range.Beatmap);
                        HealthProcessor.ApplyBeatmap(range.Beatmap);
                        foreach (var result in range.RestoredResults)
                        {
                            HealthProcessor.ApplyResult(result);
                            ScoreProcessor.ApplyResult(result);
                        }
                        ScoreProcessor.PopulateScore(Score.ScoreInfo);
                    }
                }
                preparation ??= ((IReplayPracticeRuleset)DrawableRuleset).CreateReplayPracticePreparation(Clock);
                if (preparation.IsLoaded)
                {
                    Session.State = ReplayPracticeState.Preparing;
                    preparationEndsAt = Clock.CurrentTime + PREPARATION_DURATION;
                    if (Session.ConsumePauseAfterRestore())
                        Pause();
                    replayPresentation.SetRestoring(false);
                }
            }
            else if (Session.State == ReplayPracticeState.Preparing && Clock.CurrentTime >= preparationEndsAt)
            {
                ((IReplayPracticeRuleset)DrawableRuleset).SetReplayObjectPreview(null);
                preparation!.TakeOver();
                preparation.RemoveAndDisposeImmediately();
                preparation = null;
                Session.BeginAttempt();
                DrawableRuleset.Cursor?.Show();
                GameplayClockContainer.Start();
            }

            if (Session.State == ReplayPracticeState.Playing && ScoreProcessor.HasCompleted.Value)
            {
                GameplayClockContainer.Stop();
                Session.State = ReplayPracticeState.Completed;
                finishAttempt(true);
            }
        }

        private void onResult(JudgementResult result)
        {
            if (Session.State != ReplayPracticeState.Playing)
                return;
            if (result.Type == HitResult.Miss)
                AttemptMisses++;
            if (result.Judgement.MaxResult.AffectsAccuracy())
                maximumBaseScore += ScoreProcessor.GetBaseScoreForResult(result.Judgement.MaxResult);
            if (result.Type.AffectsAccuracy())
                baseScore += ScoreProcessor.GetBaseScoreForResult(result.Type);
        }

        protected override void CheckScoreCompleted()
        {
            // Completion is handled after children so the decisive judgement is included in attempt statistics.
            // Do not mark GameplayState.HasPassed or invalidate this screen. Retry and exit remain usable.
        }

        private void finishAttempt(bool completed = false) => Session.FinishAttempt(DrawableRuleset.FrameStableClock.CurrentTime, AttemptAccuracy, AttemptMisses, completed);

        public override bool Restart(bool quickRestart = false)
        {
            failureNavigator?.Cancel();
            finishAttempt(Session.State == ReplayPracticeState.Completed);
            restarting = true;
            try
            {
                return base.Restart(quickRestart);
            }
            finally
            {
                restarting = false;
            }
        }

        /// <summary>
        /// Freezes the attempt in place, without the normal menu, pause cooldown or click-to-resume flow.
        /// A separate live input tree permits positioning while judgement input remains disabled.
        /// </summary>
        public override bool Pause()
        {
            if (!LoadedBeatmapSuccessfully || !this.IsCurrentScreen())
                return false;
            if (Session.State is ReplayPracticeState.Paused or ReplayPracticeState.Completed)
                return true;
            if (Session.State is not (ReplayPracticeState.Playing or ReplayPracticeState.Preparing))
                return false;

            stateBeforePause = Session.State;
            preparationRemainingAtPause = PreparationRemaining;
            double time = DrawableRuleset.FrameStableClock.CurrentTime;
            GameplayClockContainer.Stop();
            Session.State = ReplayPracticeState.Paused;
            // Stopping the audio source may refresh its clock. Keep the already-rendered frame authoritative.
            GameplayClockContainer.Seek(time);
            preparation ??= ((IReplayPracticeRuleset)DrawableRuleset).CreateReplayPracticePreparation(Clock);
            return true;
        }

        public bool TogglePracticePause()
        {
            if (!CanTogglePracticePause)
                return false;
            if (Session.State != ReplayPracticeState.Paused)
                return Pause();
            if (preparation?.IsLoaded != true)
                return false;
            failureNavigator?.Cancel();

            if (stateBeforePause == ReplayPracticeState.Preparing)
            {
                preparationEndsAt = Clock.CurrentTime + preparationRemainingAtPause;
                Session.State = ReplayPracticeState.Preparing;
            }
            else
            {
                // Synchronise only currently-held physical input, without judging presses made while paused.
                preparation.TakeOver();
                preparation.RemoveAndDisposeImmediately();
                preparation = null;
                Session.State = ReplayPracticeState.Playing;
                DrawableRuleset.Cursor?.Show();
                GameplayClockContainer.Start();
            }
            return true;
        }

        public bool OnPressed(KeyBindingPressEvent<GlobalAction> e)
        {
            // Global bindings precede focused raw key input. Never restart while a row/text box is capturing keys.
            if (e.Repeat || !LoadedBeatmapSuccessfully || !this.IsCurrentScreen() || GetContainingInputManager()?.FocusedDrawable != null)
                return false;

            if (ReplayTransport.HandleAction(this, e.Action))
                return true;

            switch (e.Action)
            {
                case GlobalAction.ExitReplay:
                    QuitPractice();
                    return true;

                case GlobalAction.StepReplayBackward:
                    StepTransportFrame(-1);
                    return true;
                case GlobalAction.StepReplayForward:
                    StepTransportFrame(1);
                    return true;
                case GlobalAction.SeekReplayBackward:
                    ReplayTransport.SeekRelative(this, -5000 * Session.UserPlaybackRate.Value);
                    return true;
                case GlobalAction.SeekReplayForward:
                    ReplayTransport.SeekRelative(this, 5000 * Session.UserPlaybackRate.Value);
                    return true;

                case GlobalAction.TogglePauseReplay:
                    return TogglePracticePause();

                case GlobalAction.PauseGameplay:
                    // The first matching global pause action must bypass the inherited menu flow.
                    return TogglePracticePause();

                case GlobalAction.Back:
                    // Escape only pauses in training. Returning remains an explicit button/shortcut action.
                    return Pause();

                case GlobalAction.RetryReplayPractice:
                    Restart(true);
                    return true;

                case GlobalAction.ReturnToReplay:
                    ExitPractice();
                    return true;
            }

            return false;
        }

        public void OnReleased(KeyBindingReleaseEvent<GlobalAction> e)
        {
        }

        public override void Seek(double time) => SeekTransport(time);

        public void BeginTransportSeek() => Pause();

        public bool SeekTransport(double time) => seekTransport(time);

        private bool seekTransport(double time, ReplayObjectSelection? selection = null)
        {
            if (!CanSeekTransport || !double.IsFinite(time))
                return false;
            failureNavigator?.Cancel();
            Pause();
            double target = Math.Clamp(time, TransportSeekStartTime, TransportEndTime);
            finishAttempt(Session.State == ReplayPracticeState.Completed);
            Session.Reposition(target, selection);
            return Restart(true);
        }

        public void StepTransportFrame(int direction)
        {
            if (!CanSeekTransport)
                return;
            BeginTransportSeek();
            double target = ReplayTransport.FindFrameTime(Score.Replay.Frames.Select(f => f.Time), TransportTime, direction);
            if (target != TransportTime)
                SeekTransport(target);
        }

        public void StepTransportObject(int direction)
        {
            if (!CanSeekTransport)
                return;
            BeginTransportSeek();
            var selection = ReplayTransport.FindObject(GameplayState.Beatmap.HitObjects, TransportTime, direction, SelectedObject?.Index);
            if (selection != null)
                seekTransport(selection.Time, selection);
        }

        public void ExitPractice()
        {
            failureNavigator?.Cancel();
            finishAttempt(Session.State == ReplayPracticeState.Completed);
            Session.State = ReplayPracticeState.Returning;
            returnToReplay();
            Restart(true);
        }

        public bool QuitPractice() => PerformExit(true);

        protected override bool PerformExit(bool skipTransition = false)
        {
            if (restarting)
                return base.PerformExit(skipTransition);
            failureNavigator?.Cancel();
            finishAttempt(Session.State == ReplayPracticeState.Completed);
            Session.State = ReplayPracticeState.Returning;
            quitPractice?.Invoke();
            return base.PerformExit(skipTransition);
        }

        public override bool OnExiting(ScreenExitEvent e)
        {
            if (LoadedBeatmapSuccessfully)
                finishAttempt(Session.State == ReplayPracticeState.Completed);
            return base.OnExiting(e);
        }
    }
}
