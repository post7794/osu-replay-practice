// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using osu.Framework.Graphics;
using osu.Framework.Input;
using osu.Framework.Input.StateChanges;
using osu.Game.Rulesets.Osu.Objects.Drawables;
using osu.Game.Rulesets.Osu.UI.Cursor;
using osu.Game.Rulesets.UI;

namespace osu.Game.Rulesets.Osu.UI
{
    /// <summary>
    /// Live input lives outside the frame-stable replay subtree while gameplay remains frozen.
    /// </summary>
    public partial class OsuReplayPracticePreparation : ReplayPracticePreparation
    {
        private readonly DrawableOsuRuleset ruleset;
        public OsuInputManager InputManager { get; }
        private readonly OsuCursorContainer cursor;

        public OsuReplayPracticePreparation(DrawableOsuRuleset ruleset)
        {
            this.ruleset = ruleset;
            InternalChild = InputManager = new OsuInputManager(ruleset.Ruleset.RulesetInfo)
            {
                RelativeSizeAxes = Axes.Both,
                Child = cursor = new OsuCursorContainer(),
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            ruleset.Cursor?.Hide();
            cursor.Show();
        }

        public override void TakeOver()
        {
            var input = ruleset.KeyBindingInputManager;
            // PassThroughInputManager only synchronises releases. Sampling its parent also catches
            // keys already held before preparation loaded (including a key held across retries).
            var real = GetContainingInputManager()!.CurrentState;
            // Suppress press judgement during synchronisation, but let the binding container track held actions.
            input.SuppressReplayTakeoverPresses = true;
            try
            {
                ruleset.SetReplayScore(null);
                new MousePositionAbsoluteInput { Position = real.Mouse.Position }.Apply(input.CurrentState, input);
                new KeyboardKeyInput(real.Keyboard.Keys, input.CurrentState.Keyboard.Keys).Apply(input.CurrentState, input);
                new MouseButtonInput(real.Mouse.Buttons, input.CurrentState.Mouse.Buttons).Apply(input.CurrentState, input);
                new JoystickButtonInput(real.Joystick.Buttons, input.CurrentState.Joystick.Buttons).Apply(input.CurrentState, input);
                new MidiKeyInput(real.Midi, input.CurrentState.Midi).Apply(input.CurrentState, input);
                new TabletPenButtonInput(real.Tablet.PenButtons, input.CurrentState.Tablet.PenButtons).Apply(input.CurrentState, input);
                new TabletAuxiliaryButtonInput(real.Tablet.AuxiliaryButtons, input.CurrentState.Tablet.AuxiliaryButtons).Apply(input.CurrentState, input);
                new TouchInput(real.Touch.ActiveSources.Select(s => new Touch(s, real.Touch.GetTouchPosition(s)!.Value)), true).Apply(input.CurrentState, input);
            }
            finally
            {
                input.SuppressReplayTakeoverPresses = false;
            }

            // The cursor jump is positioning, not a spin. Keep scoring history but replace the angular origin.
            foreach (var spinner in ruleset.Playfield.HitObjectContainer.AliveObjects.OfType<DrawableSpinner>())
                spinner.RotationTracker.RebaseForReplayTakeover(real.Mouse.Position);
            foreach (var slider in ruleset.Playfield.HitObjectContainer.AliveObjects.OfType<DrawableSlider>())
                slider.SliderInputManager.RebaseForReplayTakeover();
        }
    }
}
