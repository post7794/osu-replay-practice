// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Bindables;
using osu.Framework.Localisation;
using osu.Game.Localisation;
using osu.Game.Rulesets.Mods;
using osu.Game.Scoring;

namespace osu.Game.Screens.Play
{
    public enum ReplayPracticeState
    {
        Restoring,
        Preparing,
        Playing,
        Completed,
        Returning,
        Paused,
    }

    /// <summary>
    /// An in-memory session. It has no database, API, replay recorder or submission dependency.
    /// Each attempt gets a fresh score and fresh mutable replay frames.
    /// </summary>
    public class ReplayPracticeSession
    {
        private readonly Score source;
        private readonly List<ReplayPracticeAttempt> attempts = new List<ReplayPracticeAttempt>();
        private bool attemptActive;

        public IReadOnlyList<Mod> Mods { get; }
        public bool IsAutoplayBaseline { get; }
        public double RequestedTime { get; private set; }
        public double StartTime { get; private set; }
        public int? ObjectStartIndex { get; private set; }
        public bool IgnorePreviousObjects { get; }
        public ReplayObjectSelection? NavigationSelection { get; internal set; }
        public bool UsesObjectRange => ObjectStartIndex != null && IgnorePreviousObjects;
        public ReplayPanelLayout PanelLayout { get; }
        public ReplayFailureIndex FailureIndex { get; }
        internal bool PauseAfterRestore { get; private set; }
        public double ReplayPlaybackRate { get; }
        public int AttemptCount { get; private set; }
        public IReadOnlyList<ReplayPracticeAttempt> Attempts => attempts;
        public ReplayPracticeState State { get; internal set; } = ReplayPracticeState.Restoring;
        public BindableDouble UserPlaybackRate { get; } = new BindableDouble(1)
        {
            MinValue = 0.05,
            MaxValue = 2,
            Precision = 0.01,
        };

        public ReplayPracticeSession(Score source, IReadOnlyList<Mod> mods, double requestedTime, double startTime, double userPlaybackRate, int? objectStartIndex = null, bool ignorePreviousObjects = true,
                                     ReplayPanelLayout? panelLayout = null, ReplayFailureIndex? failureIndex = null)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (!double.IsFinite(requestedTime) || !double.IsFinite(startTime) || startTime > requestedTime)
                throw new ArgumentOutOfRangeException(nameof(startTime));
            if (!double.IsFinite(userPlaybackRate) || userPlaybackRate < UserPlaybackRate.MinValue || userPlaybackRate > UserPlaybackRate.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(userPlaybackRate));
            if (GetUnsupportedReason(source.ScoreInfo.Ruleset.OnlineID, mods) != null)
                throw new ArgumentException("This replay does not support manual takeover.", nameof(source));
            if (!source.Replay.HasReceivedAllFrames || source.Replay.Frames.Count == 0)
                throw new ArgumentException("A complete, non-empty replay is required.", nameof(source));

            if (objectStartIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(objectStartIndex));
            ObjectStartIndex = objectStartIndex;
            IgnorePreviousObjects = ignorePreviousObjects;
            PanelLayout = panelLayout ?? new ReplayPanelLayout();
            FailureIndex = failureIndex ?? new ReplayFailureIndex();
            NavigationSelection = objectStartIndex is int index ? new ReplayObjectSelection(index, requestedTime) : null;
            this.source = CloneSource(source);
            IsAutoplayBaseline = mods.Any(isSupportedAutoplay);
            // Autoplay supplies historical input only. Never install it in the manual player.
            Mods = mods.Where(m => !isSupportedAutoplay(m)).Select(m => m.DeepClone()).ToArray();
            RequestedTime = requestedTime;
            StartTime = startTime;
            ReplayPlaybackRate = userPlaybackRate;
            UserPlaybackRate.Value = userPlaybackRate;
        }

        public static LocalisableString? GetUnsupportedReason(int rulesetId, IReadOnlyList<Mod> mods)
        {
            if (rulesetId != 0)
                return ReplayPracticeStrings.StandardOnly;
            if (mods.Any(m => m is UnknownMod))
                return ReplayPracticeStrings.UnknownMod;
            var automatic = mods.FirstOrDefault(m => !isSupportedAutoplay(m) && (m.Type == ModType.Automation || !m.UserPlayable));
            if (automatic != null)
                return ReplayPracticeStrings.AutomaticMod(automatic.Name);
            return null;
        }

        private static bool isSupportedAutoplay(Mod mod) => mod is ModAutoplay and not ModCinema;

        public Score CreateReplayScore()
        {
            var replay = CloneSource(source);
            replay.ScoreInfo.Mods = Mods.Select(m => m.DeepClone()).ToArray();
            return replay;
        }

        public static Score CloneSource(Score score)
        {
            var clone = score.DeepClone();
            // Replay.DeepClone() intentionally shares frames. Practice must not share those mutable objects.
            clone.Replay.Frames = score.Replay.Frames.Select(f => f.DeepClone()).ToList();
            return clone;
        }

        internal void Reposition(double time, ReplayObjectSelection? selection = null)
        {
            if (!double.IsFinite(time))
                throw new ArgumentOutOfRangeException(nameof(time));
            if (attemptActive)
                throw new InvalidOperationException("Finish the current attempt before changing its start time.");
            RequestedTime = StartTime = time;
            NavigationSelection = selection;
            ObjectStartIndex = selection?.Index;
            PauseAfterRestore = true;
        }

        internal bool ConsumePauseAfterRestore()
        {
            bool pause = PauseAfterRestore;
            PauseAfterRestore = false;
            return pause;
        }

        internal void BeginAttempt()
        {
            if (attemptActive)
                throw new InvalidOperationException("An attempt is already active.");
            NavigationSelection = null;
            AttemptCount++;
            attemptActive = true;
            State = ReplayPracticeState.Playing;
        }

        internal void FinishAttempt(double endTime, double? accuracy, int misses, bool completed)
        {
            if (!attemptActive)
                return;
            attempts.Add(new ReplayPracticeAttempt(AttemptCount, StartTime, endTime, accuracy, misses, completed));
            attemptActive = false;
        }
    }

    public record ReplayPracticeAttempt(int Number, double StartTime, double EndTime, double? Accuracy, int Misses, bool Completed);
}
