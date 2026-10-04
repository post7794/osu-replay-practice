// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osu.Game.Localisation;
using osu.Framework.Screens;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Input.Bindings;
using osu.Game.Overlays;
using osu.Game.Overlays.Notifications;
using osu.Game.Rulesets.Mods;
using osu.Game.Scoring;
using osu.Game.Rulesets.UI;
using osu.Game.Screens.Play.HUD;
using osu.Game.Screens.Play.Leaderboards;
using osu.Game.Screens.Play.PlayerSettings;
using osu.Game.Screens.Ranking;
using osu.Game.Screens.Ranking.Expanded;
using osu.Game.Skinning;
using osu.Game.Users;

namespace osu.Game.Screens.Play
{
    [Cached]
    public partial class ReplayPlayer : Player, IKeyBindingHandler<GlobalAction>, IReplayTransport
    {
        public const double BASE_SEEK_AMOUNT = 1000;

        public double TransportTime => DrawableRuleset.FrameStableClock.CurrentTime;
        public double TransportEndTime => GameplayState.Beatmap.GetLastObjectTime();
        public double TransportSeekStartTime => Math.Min(GameplayClockContainer.StartTime, GameplayState.Beatmap.HitObjects.Min(ReplayTransport.ObjectAppearanceTime));
        public ReplayObjectSelection? SelectedObject { get; private set; }
        public double? TransportStartTime => null;
        public bool IsTransportPaused => GameplayClockContainer.IsPaused.Value;
        public bool CanSeekTransport => LoadedBeatmapSuccessfully && this.IsCurrentScreen();
        public Bindable<double> TransportRate => ((MasterGameplayClockContainer)GameplayClockContainer).UserPlaybackRate;
        public ReplayTransportControls TransportControls { get; private set; } = null!;
        public ReplayPanelLayout PanelLayout { get; init; } = new ReplayPanelLayout();
        public ReplayFailureIndex FailureIndex { get; init; } = new ReplayFailureIndex();
        public LocalisableString? FailureNavigationMessage => failureNavigator?.Message;
        private ReplayFailureNavigator? failureNavigator;
        public void SeekNextFailure(ReplayFailureKind kind) => failureNavigator?.Request(kind);
        public void SeekPreviousFailure(ReplayFailureKind kind) => failureNavigator?.Request(kind, backwards: true);
        public bool IsReplayRuleset(DrawableRuleset ruleset) => DrawableRuleset == ruleset;

        internal Action<ReplayPracticeSession>? PracticeRequested;
        internal Action<Score>? ReplayPrepared;
        internal double? InitialReplayTime;
        internal double InitialReplayRate = 1;
        private Score? originalReplay;
        private readonly string? sourceBeatmapHash;
        private bool initialReplaySeekPending;
        private ReplayReconstructionContainer? replayPresentation;
        internal double? PendingReplaySeekTime { get; private set; }
        internal bool IsRestoringReplay => PendingReplaySeekTime.HasValue;

        [Resolved(canBeNull: true)]
        private NotificationOverlay? notifications { get; set; }

        public LocalisableString? ReplayPracticeUnavailableReason
        {
            get
            {
                if (!LoadedBeatmapSuccessfully)
                    return ReplayPracticeStrings.BeatmapNotLoaded;
                var unsupported = ReplayPracticeSession.GetUnsupportedReason(GameplayState.Ruleset.RulesetInfo.OnlineID, GameplayState.Mods);
                if (unsupported != null)
                    return unsupported;
                if (PracticeRequested == null || DrawableRuleset is not IReplayPracticeRuleset)
                    return ReplayPracticeStrings.ViewerUnavailable;
                if (!string.IsNullOrEmpty(sourceBeatmapHash) && !string.Equals(sourceBeatmapHash, Beatmap.Value.BeatmapInfo.Hash, StringComparison.OrdinalIgnoreCase))
                    return ReplayPracticeStrings.BeatmapMismatch;
                if (!Score.Replay.HasReceivedAllFrames || Score.Replay.Frames.Count == 0)
                    return ReplayPracticeStrings.IncompleteReplay;
                if (IsRestoringReplay || !((IReplayPracticeRuleset)DrawableRuleset).IsReplayPracticeReady(GameplayClockContainer.CurrentTime))
                    return ReplayPracticeStrings.WaitingForSeek;
                if (GameplayState.HasPassed || GameplayState.HasFailed)
                    return ReplayPracticeStrings.SeekBeforeTakeover;
                return null;
            }
        }

        public BindableBool IgnorePreviousObjectsDuringObjectPractice { get; init; } = new BindableBool(true);

        public BindableBool AutomaticallyChooseSafePracticeStart { get; init; } = new BindableBool(false);

        public bool StartReplayPractice() => StartReplayPractice(AutomaticallyChooseSafePracticeStart.Value);

        public bool StartReplayPractice(bool automaticallyChooseSafeStart)
        {
            if (ReplayPracticeUnavailableReason != null)
                return false;

            // Capture the state already shown by the ruleset, not the audio clock or a rounded replay timestamp.
            double requested = DrawableRuleset.FrameStableClock.CurrentTime;
            GameplayClockContainer.Stop();
            double start = automaticallyChooseSafeStart ? ((IReplayPracticeRuleset)DrawableRuleset).GetSafeReplayPracticeTime(requested) : requested;
            double rate = ((MasterGameplayClockContainer)GameplayClockContainer).UserPlaybackRate.Value;
            int? selectedIndex = SelectedObject is { } selection && Math.Abs(selection.Time - requested) < 0.001 ? selection.Index : null;
            PracticeRequested!.Invoke(new ReplayPracticeSession(originalReplay!, GameplayState.Mods, requested, start, rate,
                selectedIndex, IgnorePreviousObjectsDuringObjectPractice.Value, PanelLayout, FailureIndex));
            return Restart(true);
        }

        private readonly Func<IBeatmap, IReadOnlyList<Mod>, Score> createScore;

        [Cached(typeof(IGameplayLeaderboardProvider))]
        private readonly SoloGameplayLeaderboardProvider leaderboardProvider = new SoloGameplayLeaderboardProvider();

        protected override UserActivity? InitialActivity =>
            // score may be null if LoadedBeatmapSuccessfully is false.
            Score == null ? null : new UserActivity.WatchingReplay(Score.ScoreInfo);

        private bool isAutoplayPlayback => GameplayState.Mods.OfType<ModAutoplay>().Any();
        public bool IsAutoplayBaseline => LoadedBeatmapSuccessfully && GameplayState.Mods.Any(m => m is ModAutoplay and not ModCinema);

        private double? lastFrameTime;

        private double userPlaybackRateBeforeFastForward;

        private ReplayFailIndicator? failIndicator;
        private PlaybackSettings? playbackSettings;

        public ReplayOverlay ReplayOverlay { get; private set; } = null!;

        protected override bool CheckModsAllowFailure()
        {
            // autoplay should be able to fail if the beatmap is not humanly beatable
            if (isAutoplayPlayback)
                return base.CheckModsAllowFailure();

            // non-autoplay replays should be able to fail, but only after they've exhausted their frames.
            // note that the rank isn't checked here - that's because it is generally unreliable.
            // stable replays, as well as lazer replays recorded prior to https://github.com/ppy/osu/pull/28058,
            // do not even *contain* the user's rank.
            // not to mention possible gameplay mechanics changes that could make a replay fail sooner than it really should.
            if (GameplayClockContainer.CurrentTime >= lastFrameTime)
                return base.CheckModsAllowFailure();

            return false;
        }

        public ReplayPlayer(Score score, PlayerConfiguration? configuration = null)
            : this((_, _) => score, configuration)
        {
            sourceBeatmapHash = score.ScoreInfo.BeatmapHash;
        }

        public ReplayPlayer(Func<IBeatmap, IReadOnlyList<Mod>, Score> createScore, PlayerConfiguration? configuration = null)
            : base(configuration)
        {
            this.createScore = createScore;
            Configuration.ShowLeaderboard = true;
        }

        internal ReplayPlayer(Score? source, Func<IBeatmap, IReadOnlyList<Mod>, Score>? createScore)
            : this((beatmap, mods) => source?.DeepClone() ?? createScore!(beatmap, mods))
        {
            sourceBeatmapHash = source?.ScoreInfo.BeatmapHash;
        }

        /// <summary>
        /// Add a settings group to the HUD overlay. Intended to be used by rulesets to add replay-specific settings.
        /// </summary>
        /// <param name="settings">The settings group to be shown.</param>
        public void AddSettings(PlayerSettingsGroup settings) => Schedule(() => ReplayOverlay.Settings.Add(settings));

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager config)
        {
            if (!LoadedBeatmapSuccessfully)
                return;

            // The original playback settings button starts this clock directly, bypassing our transport button.
            // Every resume path must invalidate the paused object-navigation anchor synchronously.
            GameplayClockContainer.IsPaused.BindValueChanged(paused =>
            {
                if (!paused.NewValue)
                {
                    failureNavigator?.Cancel();
                    SelectedObject = null;
                    (DrawableRuleset as IReplayPracticeRuleset)?.SetReplayObjectPreview(null);
                }
            });

            AddInternal(leaderboardProvider);
            if (GameplayState.Ruleset.RulesetInfo.OnlineID == 0 && Score.Replay.HasReceivedAllFrames && Score.Replay.Frames.Count > 0)
                AddInternal(failureNavigator = new ReplayFailureNavigator(this, () => originalReplay!, selection =>
                {
                    SeekTransport(selection.Time);
                    SelectedObject = selection;
                }));

            // The desktop ScreenStack has no global popup host. Keep replay popups local to this player.
            GameplayClockContainer.Add(new PopoverContainer
            {
                RelativeSizeAxes = Axes.Both,
                Child = ReplayOverlay = new ReplayOverlay(),
            });
            AddInternal(new PopoverContainer
            {
                RelativeSizeAxes = Axes.Both,
                Depth = float.MinValue,
                Child = TransportControls = new ReplayTransportControls(this),
            });

            playbackSettings = new PlaybackSettings
            {
                Depth = float.MaxValue,
                Expanded = { BindTarget = config.GetBindable<bool>(OsuSetting.ReplayPlaybackControlsExpanded) }
            };

            if (GameplayClockContainer is MasterGameplayClockContainer master)
                playbackSettings.UserPlaybackRate.BindTo(master.UserPlaybackRate);

            ReplayOverlay.Settings.AddAtStart(playbackSettings);
            ReplayOverlay.Settings.AddAtStart(new ReplayPracticeSettings(this));

            OsuTextFlowContainer message = new OsuTextFlowContainer(cp => cp.Font = OsuFont.Style.Body) { AutoSizeAxes = Axes.Both };
            message.AddText("Watching ");
            message.AddText(Score.ScoreInfo.User.Username, s => s.Font = s.Font.With(weight: FontWeight.SemiBold));
            message.AddText(" play ");
            message.AddText(Beatmap.Value.BeatmapInfo.GetDisplayTitleRomanisable(), s => s.Font = s.Font.With(weight: FontWeight.SemiBold));
            message.AddText(" on ");
            message.AddArbitraryDrawable(new PlayedOnText(Score.ScoreInfo.Date, false)
            {
                Font = OsuFont.Style.Body.With(weight: FontWeight.SemiBold),
            });

            ReplayOverlay.SetMessage(new ScrollingMessage(message)
            {
                Y = 96,
                Anchor = Anchor.TopCentre,
                Origin = Anchor.TopCentre,
            });

            RulesetSkinProvidingContainer rulesetSkinProvider;
            AddInternal(rulesetSkinProvider = new RulesetSkinProvidingContainer(GameplayState.Ruleset, GameplayState.Beatmap, Beatmap.Value.Skin)
            {
                Child = failIndicator = new ReplayFailIndicator(GameplayClockContainer)
                {
                    GoToResults = () =>
                    {
                        if (!this.IsCurrentScreen())
                            return;

                        ValidForResume = false;
                        this.Push(new SoloResultsScreen(Score.ScoreInfo));
                    }
                }
            });
            config.BindWith(OsuSetting.BeatmapSkins, rulesetSkinProvider.BeatmapSkins);
            config.BindWith(OsuSetting.BeatmapColours, rulesetSkinProvider.BeatmapColours);
            config.BindWith(OsuSetting.BeatmapHitsounds, rulesetSkinProvider.BeatmapHitsounds);
        }

        protected override void PrepareReplay()
        {
            // Playback only reads frames. Deep frame copies are deferred until an actual practice session.
            originalReplay ??= Score.DeepClone();
            // Generated scores now contain the actual converted beatmap, mods and random seeds.
            // Capture this before playback mutates score statistics.
            ReplayPrepared?.Invoke(originalReplay);
            DrawableRuleset?.SetReplayScore(Score);
            lastFrameTime = Score.Replay.Frames.LastOrDefault()?.Time;
        }

        protected override Container CreateGameplayPresentationContainer(GameplayClockContainer gameplay)
            => DrawableRuleset is IReplayPracticeRuleset
                ? replayPresentation = new ReplayReconstructionContainer(gameplay, restoring: InitialReplayTime.HasValue)
                : base.CreateGameplayPresentationContainer(gameplay);

        protected override void StartGameplay()
        {
            if (InitialReplayTime == null)
            {
                base.StartGameplay();
                return;
            }
            ((MasterGameplayClockContainer)GameplayClockContainer).UserPlaybackRate.Value = InitialReplayRate;
            PendingReplaySeekTime = InitialReplayTime;
            GameplayClockContainer.Reset(Math.Min(GameplayClockContainer.StartTime, InitialReplayTime.Value));
            initialReplaySeekPending = true;
        }

        protected override void Update()
        {
            base.Update();
            if (LoadedBeatmapSuccessfully && this.IsCurrentScreen() && DrawableRuleset is IReplayPracticeRuleset practiceRuleset)
                practiceRuleset.SetReplayObjectPreview(IsTransportPaused ? SelectedObject?.Index : null);
        }

        protected override void UpdateAfterChildren()
        {
            base.UpdateAfterChildren();
            if (!LoadedBeatmapSuccessfully || !this.IsCurrentScreen())
                return;
            if (initialReplaySeekPending)
            {
                if (((IReplayPracticeRuleset)DrawableRuleset).IsReplayPracticeReady(GameplayClockContainer.CurrentTime))
                {
                    initialReplaySeekPending = false;
                    GameplayClockContainer.Seek(InitialReplayTime!.Value);
                }
                return;
            }
            if (PendingReplaySeekTime is double target && DrawableRuleset is IReplayPracticeRuleset ruleset
                && ruleset.IsReplayPracticeReady(IsTransportPaused ? target : GameplayClockContainer.CurrentTime))
            {
                PendingReplaySeekTime = null;
                replayPresentation?.SetRestoring(false);
            }
        }

        protected override Score CreateScore(IBeatmap beatmap) => createScore(beatmap, Mods.Value);

        protected override bool ShowGameplayEntryTransition => InitialReplayTime == null;

        // Don't re-import replay scores as they're already present in the database.
        protected override Task ImportScore(Score score) => Task.CompletedTask;

        protected override ResultsScreen CreateResults(ScoreInfo score) => new SoloResultsScreen(score)
        {
            // Only show the relevant button otherwise things look silly.
            AllowWatchingReplay = !isAutoplayPlayback,
            AllowRetry = isAutoplayPlayback,
        };

        public bool OnPressed(KeyBindingPressEvent<GlobalAction> e)
        {
            if (!LoadedBeatmapSuccessfully || GetContainingInputManager()?.FocusedDrawable != null)
                return false;

            if (!this.IsCurrentScreen())
                return false;
            if (ReplayTransport.HandleAction(this, e.Action))
                return true;

            switch (e.Action)
            {
                case GlobalAction.ExitReplay:
                    ExitReplay();
                    return true;

                case GlobalAction.TakeOverReplay:
                    if (e.Repeat || !this.IsCurrentScreen())
                        return false;

                    if (!StartReplayPractice() && ReplayPracticeUnavailableReason is LocalisableString reason)
                        notifications?.Post(new SimpleNotification { Text = reason });
                    return true;

                case GlobalAction.StepReplayBackward:
                    StepFrame(-1);
                    return true;

                case GlobalAction.StepReplayForward:
                    StepFrame(1);
                    return true;

                case GlobalAction.SeekReplayBackward:
                    SeekInDirection(-5 * (float)playbackSettings!.UserPlaybackRate.Value);
                    return true;

                case GlobalAction.SeekReplayForward:
                    SeekInDirection(5 * (float)playbackSettings!.UserPlaybackRate.Value);
                    return true;

                case GlobalAction.TogglePauseReplay:
                    return ToggleTransportPause();

                case GlobalAction.FastForwardReplay:
                    if (e.Repeat) return false;

                    userPlaybackRateBeforeFastForward = playbackSettings!.UserPlaybackRate.Value;
                    playbackSettings!.UserPlaybackRate.Value *= 2;
                    return true;
            }

            return false;
        }

        public void StepFrame(int direction) => StepTransportFrame(direction);

        public bool ToggleTransportPause()
        {
            if (!CanSeekTransport)
                return false;
            if (IsTransportPaused)
            {
                failureNavigator?.Cancel();
                SelectedObject = null;
                (DrawableRuleset as IReplayPracticeRuleset)?.SetReplayObjectPreview(null);
                GameplayClockContainer.Start();
            }
            else
                BeginTransportSeek();
            return true;
        }

        public override void Seek(double time)
        {
            failureNavigator?.Cancel();
            SelectedObject = null;
            (DrawableRuleset as IReplayPracticeRuleset)?.SetReplayObjectPreview(null);
            if (replayPresentation != null)
            {
                PendingReplaySeekTime = time;
                replayPresentation.SetRestoring(true);
            }
            base.Seek(time);
        }

        public void BeginTransportSeek()
        {
            double time = PendingReplaySeekTime ?? TransportTime;
            GameplayClockContainer.Stop();
            // The returning player must consume its initial frame before its first long seek.
            // A pause request during restoration must not abandon the pending destination.
            if (!initialReplaySeekPending)
                base.Seek(time);
        }

        public bool SeekTransport(double time)
        {
            if (!CanSeekTransport || !double.IsFinite(time))
                return false;
            // Seeking already chooses a new target. Do not seek back to the rendered frame first,
            // especially on every drag update (which would needlessly notify/reset clock consumers twice).
            GameplayClockContainer.Stop();
            Seek(Math.Clamp(time, TransportSeekStartTime, TransportEndTime));
            return true;
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
            if (selection == null)
                return;
            SeekTransport(selection.Time);
            SelectedObject = selection;
        }

        public void ExitReplay()
        {
            GameplayClockContainer.Stop();
            this.Exit();
        }

        public void SeekInDirection(float amount)
        {
            double target = Math.Clamp(GameplayClockContainer.CurrentTime + amount * BASE_SEEK_AMOUNT, 0, GameplayState.Beatmap.GetLastObjectTime());

            Seek(target);
        }

        public void OnReleased(KeyBindingReleaseEvent<GlobalAction> e)
        {
            switch (e.Action)
            {
                case GlobalAction.FastForwardReplay:
                    playbackSettings!.UserPlaybackRate.Value = userPlaybackRateBeforeFastForward;
                    return;
            }
        }

        protected override void PerformFail()
        {
            // base logic intentionally suppressed - we have our own custom fail interaction
            ScoreProcessor.FailScore(Score.ScoreInfo);
            failIndicator!.Display();
        }

        public override void OnSuspending(ScreenTransitionEvent e)
        {
            stopAllAudioEffects();
            base.OnSuspending(e);
        }

        public override bool OnExiting(ScreenExitEvent e)
        {
            // safety against filters or samples from the indicator playing long after the screen is exited
            failIndicator?.RemoveAndDisposeImmediately();
            return base.OnExiting(e);
        }

        private void stopAllAudioEffects()
        {
            // safety against filters or samples from the indicator playing long after the screen is exited
            failIndicator?.RemoveAndDisposeImmediately();

            if (GameplayClockContainer is MasterGameplayClockContainer master)
            {
                playbackSettings?.UserPlaybackRate.UnbindFrom(master.UserPlaybackRate);
                master.UserPlaybackRate.SetDefault();
            }
        }
    }
}
