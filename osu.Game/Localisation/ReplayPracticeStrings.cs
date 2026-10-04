// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Localisation;

namespace osu.Game.Localisation
{
    /// <summary>
    /// Local resources are bundled with osu.Game so development builds do not depend on an updated osu-resources package.
    /// </summary>
    public static class ReplayPracticeStrings
    {
        private const string prefix = @"osu.Game.Localisation.ReplayPractice";

        internal static string GetKey(string key) => getKey(key);

        /// <summary>
        /// "Drag menu / resize corner"
        /// </summary>
        public static LocalisableString DragPanel => new TranslatableString(getKey(@"DragPanel"), @"Drag menu / resize corner");

        /// <summary>
        /// "Previous Miss"
        /// </summary>
        public static LocalisableString PreviousMiss => new TranslatableString(getKey(@"PreviousMiss"), @"Previous Miss");

        /// <summary>
        /// "Previous ignored"
        /// </summary>
        public static LocalisableString PreviousIgnored => new TranslatableString(getKey(@"PreviousIgnored"), @"Previous ignored");

        /// <summary>
        /// "Find the previous original replay combo-breaking miss, including slider breaks, and select its object at appearance."
        /// </summary>
        public static LocalisableString PreviousMissHelp => new TranslatableString(getKey(@"PreviousMissHelp"), @"Find the previous original replay combo-breaking miss, including slider breaks, and select its object at appearance.");

        /// <summary>
        /// "Find the previous original replay judgement that loses a combo increment without breaking combo, such as a dropped slider tail."
        /// </summary>
        public static LocalisableString PreviousIgnoredHelp => new TranslatableString(getKey(@"PreviousIgnoredHelp"), @"Find the previous original replay judgement that loses a combo increment without breaking combo, such as a dropped slider tail.");

        /// <summary>
        /// "No earlier Miss in the original replay"
        /// </summary>
        public static LocalisableString NoPreviousMiss => new TranslatableString(getKey(@"NoPreviousMiss"), @"No earlier Miss in the original replay");

        /// <summary>
        /// "No earlier lost combo increment in the original replay"
        /// </summary>
        public static LocalisableString NoPreviousIgnored => new TranslatableString(getKey(@"NoPreviousIgnored"), @"No earlier lost combo increment in the original replay");

        /// <summary>
        /// "Replay / practice: previous original Miss"
        /// </summary>
        public static LocalisableString PreviousMissAction => new TranslatableString(getKey(@"PreviousMissAction"), @"Replay / practice: previous original Miss");

        /// <summary>
        /// "Replay / practice: previous original lost combo increment"
        /// </summary>
        public static LocalisableString PreviousIgnoredAction => new TranslatableString(getKey(@"PreviousIgnoredAction"), @"Replay / practice: previous original lost combo increment");

        /// <summary>
        /// "Next Miss"
        /// </summary>
        public static LocalisableString NextMiss => new TranslatableString(getKey(@"NextMiss"), @"Next Miss");

        /// <summary>
        /// "Next ignored"
        /// </summary>
        public static LocalisableString NextIgnored => new TranslatableString(getKey(@"NextIgnored"), @"Next ignored");

        /// <summary>
        /// "Find the next original replay combo-breaking miss, including slider breaks, and select its object at appearance."
        /// </summary>
        public static LocalisableString NextMissHelp => new TranslatableString(getKey(@"NextMissHelp"), @"Find the next original replay combo-breaking miss, including slider breaks, and select its object at appearance.");

        /// <summary>
        /// "Find an original replay judgement that loses a combo increment without breaking combo, such as a dropped slider tail."
        /// </summary>
        public static LocalisableString NextIgnoredHelp => new TranslatableString(getKey(@"NextIgnoredHelp"), @"Find an original replay judgement that loses a combo increment without breaking combo, such as a dropped slider tail.");

        /// <summary>
        /// "Analysing original replay…"
        /// </summary>
        public static LocalisableString AnalysingReplay => new TranslatableString(getKey(@"AnalysingReplay"), @"Analysing original replay…");

        /// <summary>
        /// "No later Miss in the original replay"
        /// </summary>
        public static LocalisableString NoNextMiss => new TranslatableString(getKey(@"NoNextMiss"), @"No later Miss in the original replay");

        /// <summary>
        /// "No later lost combo increment in the original replay"
        /// </summary>
        public static LocalisableString NoNextIgnored => new TranslatableString(getKey(@"NoNextIgnored"), @"No later lost combo increment in the original replay");

        /// <summary>
        /// "Replay judgement analysis unavailable"
        /// </summary>
        public static LocalisableString FailureAnalysisUnavailable => new TranslatableString(getKey(@"FailureAnalysisUnavailable"), @"Replay judgement analysis unavailable");

        /// <summary>
        /// "Replay / practice: next original Miss"
        /// </summary>
        public static LocalisableString NextMissAction => new TranslatableString(getKey(@"NextMissAction"), @"Replay / practice: next original Miss");

        /// <summary>
        /// "Replay / practice: next original lost combo increment"
        /// </summary>
        public static LocalisableString NextIgnoredAction => new TranslatableString(getKey(@"NextIgnoredAction"), @"Replay / practice: next original lost combo increment");

        /// <summary>
        /// "Start from selected object (ignore earlier objects)"
        /// </summary>
        public static LocalisableString ObjectStartOption => new TranslatableString(getKey(@"ObjectStartOption"), @"Start from selected object (ignore earlier objects)");

        /// <summary>
        /// "Object navigation selects a numbered object. Practice removes earlier objects, including unfinished sliders, and resets score/health for this range. No perfect hits are awarded. Arbitrary-time takeover still restores the original scene."
        /// </summary>
        public static LocalisableString ObjectRangeHelp => new TranslatableString(getKey(@"ObjectRangeHelp"), @"Object navigation selects a numbered object. Practice removes earlier objects, including unfinished sliders, and resets score/health for this range. No perfect hits are awarded. Arbitrary-time takeover still restores the original scene.");

        /// <summary>
        /// "Object #{0} — earlier objects excluded in practice"
        /// </summary>
        public static LocalisableString ObjectSelected(int number) => new TranslatableString(getKey(@"ObjectSelected"), @"Object #{0} — earlier objects excluded in practice", number);

        /// <summary>
        /// "Object #{0} — preserve original scene"
        /// </summary>
        public static LocalisableString ObjectSelectedExact(int number) => new TranslatableString(getKey(@"ObjectSelectedExact"), @"Object #{0} — preserve original scene", number);

        /// <summary>
        /// "Practise selected object"
        /// </summary>
        public static LocalisableString TakeOverObject => new TranslatableString(getKey(@"TakeOverObject"), @"Practise selected object");

        private static string getKey(string key) => $"{prefix}:{key}";

        /// <summary>
        /// "Pause practice"
        /// </summary>
        public static LocalisableString PausePractice => new TranslatableString(getKey(@"PausePractice"), @"Pause practice");

        /// <summary>
        /// "Resume practice"
        /// </summary>
        public static LocalisableString ResumePractice => new TranslatableString(getKey(@"ResumePractice"), @"Resume practice");

        /// <summary>
        /// "Pause replay"
        /// </summary>
        public static LocalisableString PauseReplay => new TranslatableString(getKey(@"PauseReplay"), @"Pause replay");

        /// <summary>
        /// "Resume replay"
        /// </summary>
        public static LocalisableString ResumeReplay => new TranslatableString(getKey(@"ResumeReplay"), @"Resume replay");

        /// <summary>
        /// "Retry"
        /// </summary>
        public static LocalisableString Retry => new TranslatableString(getKey(@"Retry"), @"Retry");

        /// <summary>
        /// "Take over now"
        /// </summary>
        public static LocalisableString TakeOver => new TranslatableString(getKey(@"TakeOver"), @"Take over now");

        /// <summary>
        /// "&lt; Object"
        /// </summary>
        public static LocalisableString PreviousObject => new TranslatableString(getKey(@"PreviousObject"), @"< Object");

        /// <summary>
        /// "Object &gt;"
        /// </summary>
        public static LocalisableString NextObject => new TranslatableString(getKey(@"NextObject"), @"Object >");

        /// <summary>
        /// "-1 sec"
        /// </summary>
        public static LocalisableString BackwardSecond => new TranslatableString(getKey(@"BackwardSecond"), @"-1 sec");

        /// <summary>
        /// "+1 sec"
        /// </summary>
        public static LocalisableString ForwardSecond => new TranslatableString(getKey(@"ForwardSecond"), @"+1 sec");

        /// <summary>
        /// "&lt; Frame"
        /// </summary>
        public static LocalisableString PreviousFrame => new TranslatableString(getKey(@"PreviousFrame"), @"< Frame");

        /// <summary>
        /// "Frame &gt;"
        /// </summary>
        public static LocalisableString NextFrame => new TranslatableString(getKey(@"NextFrame"), @"Frame >");

        /// <summary>
        /// "Slower"
        /// </summary>
        public static LocalisableString Slower => new TranslatableString(getKey(@"Slower"), @"Slower");

        /// <summary>
        /// "Speed 1x"
        /// </summary>
        public static LocalisableString ResetSpeed => new TranslatableString(getKey(@"ResetSpeed"), @"Speed 1x");

        /// <summary>
        /// "Faster"
        /// </summary>
        public static LocalisableString Faster => new TranslatableString(getKey(@"Faster"), @"Faster");

        /// <summary>
        /// "Return to replay"
        /// </summary>
        public static LocalisableString Return => new TranslatableString(getKey(@"Return"), @"Return to replay");

        /// <summary>
        /// "Settings"
        /// </summary>
        public static LocalisableString Settings => new TranslatableString(getKey(@"Settings"), @"Settings");

        /// <summary>
        /// "Shortcuts"
        /// </summary>
        public static LocalisableString Shortcuts => new TranslatableString(getKey(@"Shortcuts"), @"Shortcuts");

        /// <summary>
        /// "Configure replay shortcuts"
        /// </summary>
        public static LocalisableString ConfigureShortcuts => new TranslatableString(getKey(@"ConfigureShortcuts"), @"Configure replay shortcuts");

        /// <summary>
        /// "Exit"
        /// </summary>
        public static LocalisableString Exit => new TranslatableString(getKey(@"Exit"), @"Exit");

        /// <summary>
        /// "PRACTICE — NOT SAVED"
        /// </summary>
        public static LocalisableString NotSaved => new TranslatableString(getKey(@"NotSaved"), @"PRACTICE — NOT SAVED");

        /// <summary>
        /// "REPLAY"
        /// </summary>
        public static LocalisableString Replay => new TranslatableString(getKey(@"Replay"), @"REPLAY");

        /// <summary>
        /// "AUTOPLAY BASELINE"
        /// </summary>
        public static LocalisableString AutoplayBaseline => new TranslatableString(getKey(@"AutoplayBaseline"), @"AUTOPLAY BASELINE");

        /// <summary>
        /// "AUTOPLAY PRACTICE — NOT SAVED"
        /// </summary>
        public static LocalisableString AutoplayPractice => new TranslatableString(getKey(@"AutoplayPractice"), @"AUTOPLAY PRACTICE — NOT SAVED");

        /// <summary>
        /// "This is an automatically generated baseline, not your previous play. Takeover removes Autoplay and preserves the other mods. Only your input is used after preparation."
        /// </summary>
        public static LocalisableString AutoplayBaselineHelp => new TranslatableString(getKey(@"AutoplayBaselineHelp"), @"This is an automatically generated baseline, not your previous play. Takeover removes Autoplay and preserves the other mods. Only your input is used after preparation.");

        /// <summary>
        /// "Restoring original replay..."
        /// </summary>
        public static LocalisableString Restoring => new TranslatableString(getKey(@"Restoring"), @"Restoring original replay...");

        /// <summary>
        /// "GET READY"
        /// </summary>
        public static LocalisableString GetReady => new TranslatableString(getKey(@"GetReady"), @"GET READY");

        /// <summary>
        /// "Position / hold keys"
        /// </summary>
        public static LocalisableString PositionKeys => new TranslatableString(getKey(@"PositionKeys"), @"Position / hold keys");

        /// <summary>
        /// "Get ready: {0}"
        /// </summary>
        public static LocalisableString Preparing(int seconds) => new TranslatableString(getKey(@"Preparing"), @"Get ready: {0}", seconds);

        /// <summary>
        /// "Paused — preparation pending"
        /// </summary>
        public static LocalisableString PausedPreparation => new TranslatableString(getKey(@"PausedPreparation"), @"Paused — preparation pending");

        /// <summary>
        /// "Paused"
        /// </summary>
        public static LocalisableString Paused => new TranslatableString(getKey(@"Paused"), @"Paused");

        /// <summary>
        /// "Playing"
        /// </summary>
        public static LocalisableString Playing => new TranslatableString(getKey(@"Playing"), @"Playing");

        /// <summary>
        /// "Finished"
        /// </summary>
        public static LocalisableString Finished => new TranslatableString(getKey(@"Finished"), @"Finished");

        /// <summary>
        /// "Attempt {0}"
        /// </summary>
        public static LocalisableString Attempt(int number) => new TranslatableString(getKey(@"Attempt"), @"Attempt {0}", number);

        /// <summary>
        /// "Accuracy: {0}  Miss: {1}"
        /// </summary>
        public static LocalisableString Statistics(string accuracy, int misses) => new TranslatableString(getKey(@"Statistics"), @"Accuracy: {0}  Miss: {1}", accuracy, misses);

        /// <summary>
        /// "Start: {0:0.000}s"
        /// </summary>
        public static LocalisableString Start(double time) => new TranslatableString(getKey(@"Start"), @"Start: {0:0.000}s", time);

        /// <summary>
        /// "Safe-start rewind: {0:0.000}s -&gt; {1:0.000}s"
        /// </summary>
        public static LocalisableString SafeStart(double requested, double start) => new TranslatableString(getKey(@"SafeStart"), @"Safe-start rewind: {0:0.000}s -> {1:0.000}s", requested, start);

        /// <summary>
        /// "Attempts: {0}  |  {1}"
        /// </summary>
        public static LocalisableString History(int count, string attempts) => new TranslatableString(getKey(@"History"), @"Attempts: {0}  |  {1}", count, attempts);

        /// <summary>
        /// "Default keys — A/D: objects  |  Q/E: 1 second  |  W/S: speed  |  F: speed 1x  |  Space: pause  |  Ctrl+Enter: take over"
        /// </summary>
        public static LocalisableString NavigationHint => new TranslatableString(getKey(@"NavigationHint"), @"Default keys — A/D: objects  |  Q/E: 1 second  |  W/S: speed  |  F: speed 1x  |  Space: pause  |  Ctrl+Enter: take over");

        /// <summary>
        /// "Freeze this frame and take over"
        /// </summary>
        public static LocalisableString FreezeFrame => new TranslatableString(getKey(@"FreezeFrame"), @"Freeze this frame and take over");

        /// <summary>
        /// "Replay takeover practice"
        /// </summary>
        public static LocalisableString PracticeTitle => new TranslatableString(getKey(@"PracticeTitle"), @"Replay takeover practice");

        /// <summary>
        /// "Rewind to a safe start (optional)"
        /// </summary>
        public static LocalisableString SafeStartOption => new TranslatableString(getKey(@"SafeStartOption"), @"Rewind to a safe start (optional)");

        /// <summary>
        /// "Freeze this frame, prepare for 3 seconds, then take over. No rewind. No score or replay will be saved."
        /// </summary>
        public static LocalisableString ExactHelp => new TranslatableString(getKey(@"ExactHelp"), @"Freeze this frame, prepare for 3 seconds, then take over. No rewind. No score or replay will be saved.");

        /// <summary>
        /// "Rewind to before the objects, then prepare for 3 seconds. No score or replay will be saved."
        /// </summary>
        public static LocalisableString SafeHelp => new TranslatableString(getKey(@"SafeHelp"), @"Rewind to before the objects, then prepare for 3 seconds. No score or replay will be saved.");

        /// <summary>
        /// "The beatmap could not be loaded."
        /// </summary>
        public static LocalisableString BeatmapNotLoaded => new TranslatableString(getKey(@"BeatmapNotLoaded"), @"The beatmap could not be loaded.");

        /// <summary>
        /// "Takeover is unavailable for this replay viewer."
        /// </summary>
        public static LocalisableString ViewerUnavailable => new TranslatableString(getKey(@"ViewerUnavailable"), @"Takeover is unavailable for this replay viewer.");

        /// <summary>
        /// "The loaded beatmap differs from the replay's beatmap. Restore the matching beatmap before taking over."
        /// </summary>
        public static LocalisableString BeatmapMismatch => new TranslatableString(getKey(@"BeatmapMismatch"), @"The loaded beatmap differs from the replay's beatmap. Restore the matching beatmap before taking over.");

        /// <summary>
        /// "A complete, non-empty replay is required."
        /// </summary>
        public static LocalisableString IncompleteReplay => new TranslatableString(getKey(@"IncompleteReplay"), @"A complete, non-empty replay is required.");

        /// <summary>
        /// "Waiting for replay seek to finish…"
        /// </summary>
        public static LocalisableString WaitingForSeek => new TranslatableString(getKey(@"WaitingForSeek"), @"Waiting for replay seek to finish…");

        /// <summary>
        /// "Seek back into the replay before taking over."
        /// </summary>
        public static LocalisableString SeekBeforeTakeover => new TranslatableString(getKey(@"SeekBeforeTakeover"), @"Seek back into the replay before taking over.");

        /// <summary>
        /// "Replay takeover currently supports osu!standard only."
        /// </summary>
        public static LocalisableString StandardOnly => new TranslatableString(getKey(@"StandardOnly"), @"Replay takeover currently supports osu!standard only.");

        /// <summary>
        /// "This replay contains an unknown mod."
        /// </summary>
        public static LocalisableString UnknownMod => new TranslatableString(getKey(@"UnknownMod"), @"This replay contains an unknown mod.");

        /// <summary>
        /// "{0} performs automatic gameplay. Keeping it would continue automation; removing it would change the replay's rules. Manual takeover is not supported."
        /// </summary>
        public static LocalisableString AutomaticMod(string mod) => new TranslatableString(getKey(@"AutomaticMod"), @"{0} performs automatic gameplay. Keeping it would continue automation; removing it would change the replay's rules. Manual takeover is not supported.", mod);

        /// <summary>
        /// "Take over replay"
        /// </summary>
        public static LocalisableString TakeOverAction => new TranslatableString(getKey(@"TakeOverAction"), @"Take over replay");

        /// <summary>
        /// "Retry replay practice"
        /// </summary>
        public static LocalisableString RetryAction => new TranslatableString(getKey(@"RetryAction"), @"Retry replay practice");

        /// <summary>
        /// "Return to replay"
        /// </summary>
        public static LocalisableString ReturnAction => new TranslatableString(getKey(@"ReturnAction"), @"Return to replay");

        /// <summary>
        /// "Previous replay / practice object"
        /// </summary>
        public static LocalisableString PreviousObjectAction => new TranslatableString(getKey(@"PreviousObjectAction"), @"Previous replay / practice object");

        /// <summary>
        /// "Next replay / practice object"
        /// </summary>
        public static LocalisableString NextObjectAction => new TranslatableString(getKey(@"NextObjectAction"), @"Next replay / practice object");

        /// <summary>
        /// "Seek replay / practice backward one second"
        /// </summary>
        public static LocalisableString BackwardSecondAction => new TranslatableString(getKey(@"BackwardSecondAction"), @"Seek replay / practice backward one second");

        /// <summary>
        /// "Seek replay / practice forward one second"
        /// </summary>
        public static LocalisableString ForwardSecondAction => new TranslatableString(getKey(@"ForwardSecondAction"), @"Seek replay / practice forward one second");

        /// <summary>
        /// "Increase replay / practice speed"
        /// </summary>
        public static LocalisableString IncreaseSpeedAction => new TranslatableString(getKey(@"IncreaseSpeedAction"), @"Increase replay / practice speed");

        /// <summary>
        /// "Decrease replay / practice speed"
        /// </summary>
        public static LocalisableString DecreaseSpeedAction => new TranslatableString(getKey(@"DecreaseSpeedAction"), @"Decrease replay / practice speed");

        /// <summary>
        /// "Reset replay / practice speed"
        /// </summary>
        public static LocalisableString ResetSpeedAction => new TranslatableString(getKey(@"ResetSpeedAction"), @"Reset replay / practice speed");

        /// <summary>
        /// "Exit replay or practice"
        /// </summary>
        public static LocalisableString ExitAction => new TranslatableString(getKey(@"ExitAction"), @"Exit replay or practice");

        /// <summary>
        /// "Pause / resume replay or practice"
        /// </summary>
        public static LocalisableString PauseAction => new TranslatableString(getKey(@"PauseAction"), @"Pause / resume replay or practice");
    }
}
