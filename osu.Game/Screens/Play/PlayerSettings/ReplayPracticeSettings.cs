// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics;
using osu.Framework.Localisation;
using osu.Game.Localisation;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.UserInterfaceV2;

namespace osu.Game.Screens.Play.PlayerSettings
{
    public partial class ReplayPracticeSettings : PlayerSettingsGroup
    {
        private readonly ReplayPlayer player;
        private readonly RoundedButton takeover;
        private readonly PlayerCheckbox objectStart;
        private readonly OsuTextFlowContainer reason;
        private LocalisableString? lastMessage;

        public ReplayPracticeSettings(ReplayPlayer player)
            : base(ReplayPracticeStrings.PracticeTitle)
        {
            this.player = player;
            Children = new Drawable[]
            {
                new PlayerCheckbox { LabelText = ReplayPracticeStrings.SafeStartOption, Current = player.AutomaticallyChooseSafePracticeStart },
                takeover = new RoundedButton { Text = ReplayPracticeStrings.TakeOver, RelativeSizeAxes = Axes.X, Action = () => player.StartReplayPractice() },
                reason = new OsuTextFlowContainer { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Text = ReplayPracticeStrings.ExactHelp },
                objectStart = new PlayerCheckbox { LabelText = ReplayPracticeStrings.ObjectStartOption, TooltipText = ReplayPracticeStrings.ObjectRangeHelp, Current = player.IgnorePreviousObjectsDuringObjectPractice, Alpha = 0 },
            };
        }

        protected override void Update()
        {
            base.Update();
            LocalisableString? message = player.ReplayPracticeUnavailableReason;
            takeover.Enabled.Value = message == null;
            objectStart.Alpha = player.SelectedObject != null ? 1 : 0;
            bool objectRange = player.SelectedObject != null && player.IgnorePreviousObjectsDuringObjectPractice.Value;
            takeover.Text = objectRange ? ReplayPracticeStrings.TakeOverObject : ReplayPracticeStrings.TakeOver;
            LocalisableString text = message ?? (objectRange ? ReplayPracticeStrings.ObjectRangeHelp : player.AutomaticallyChooseSafePracticeStart.Value
                ? ReplayPracticeStrings.SafeHelp
                : ReplayPracticeStrings.ExactHelp);
            if (message == null && player.IsAutoplayBaseline)
                text = LocalisableString.Format("{0}\n{1}", text, ReplayPracticeStrings.AutoplayBaselineHelp);
            if (lastMessage != text)
            {
                reason.Text = text;
                lastMessage = text;
            }
        }
    }
}
