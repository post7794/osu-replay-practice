// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Input;
using osu.Framework.Timing;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.Objects;
using osu.Game.Beatmaps;
using osu.Game.Input.Handlers;
using osu.Game.Replays;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects.Drawables;
using osu.Game.Rulesets.Osu.Configuration;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Objects.Drawables;
using osu.Game.Rulesets.Osu.Replays;
using osu.Game.Rulesets.UI;
using osu.Game.Scoring;
using osu.Game.Screens.Play;
using osuTK;

namespace osu.Game.Rulesets.Osu.UI
{
    public partial class DrawableOsuRuleset : DrawableRuleset<OsuHitObject>, IReplayPracticeRuleset
    {
        private Bindable<bool>? cursorHideEnabled;

        public new OsuInputManager KeyBindingInputManager => (OsuInputManager)base.KeyBindingInputManager;

        public new OsuPlayfield Playfield => (OsuPlayfield)base.Playfield;

        protected new OsuRulesetConfigManager Config => (OsuRulesetConfigManager)base.Config;

        public DrawableOsuRuleset(Ruleset ruleset, IBeatmap beatmap, IReadOnlyList<Mod>? mods = null)
            : base(ruleset, beatmap, mods)
        {
        }

        [BackgroundDependencyLoader]
        private void load(ReplayPlayer? replayPlayer)
        {
            if (replayPlayer != null && replayPlayer.IsReplayRuleset(this))
            {
                ReplayAnalysisOverlay analysisOverlay;
                PlayfieldAdjustmentContainer.Add(analysisOverlay = new ReplayAnalysisOverlay(replayPlayer.Score.Replay));
                Overlays.Add(analysisOverlay.CreateProxy().With(p => p.Depth = float.NegativeInfinity));
                replayPlayer.AddSettings(new ReplayAnalysisSettings(Config));

                cursorHideEnabled = Config.GetBindable<bool>(OsuRulesetSetting.ReplayCursorHideEnabled);

                // I have little faith in this working (other things touch cursor visibility) but haven't broken it yet.
                // Let's wait for someone to report an issue before spending too much time on it.
                cursorHideEnabled.BindValueChanged(enabled => Playfield.Cursor.FadeTo(enabled.NewValue ? 0 : 1), true);
            }
        }

        public override DrawableHitObject<OsuHitObject>? CreateDrawableRepresentation(OsuHitObject h) => null;

        public override bool ReceivePositionalInputAt(Vector2 screenSpacePos) => true; // always show the gameplay cursor

        protected override Playfield CreatePlayfield() => new OsuPlayfield();

        protected override PassThroughInputManager CreateInputManager() => new OsuInputManager(Ruleset.RulesetInfo);

        public override PlayfieldAdjustmentContainer CreatePlayfieldAdjustmentContainer() => new OsuPlayfieldAdjustmentContainer { AlignWithStoryboard = true };

        protected override ResumeOverlay CreateResumeOverlay()
        {
            if (Mods.Any(m => m is OsuModAutopilot or OsuModTouchDevice))
                return new DelayedResumeOverlay { Scale = new Vector2(0.65f) };

            return new OsuResumeOverlay();
        }

        private bool creatingFailureAnalysisHandler;

        protected override ReplayInputHandler CreateReplayInputHandler(Replay replay)
            => creatingFailureAnalysisHandler
                ? new OsuReplayFailureAnalysisInputHandler(replay, Beatmap.HitObjects)
                : new OsuFramedReplayInputHandler(replay);

        public void SetReplayScoreForFailureAnalysis(Score score)
        {
            // Scope the special handler to this load. Normal playback and takeover restoration
            // must still use the native handler, even if this ruleset later loads another replay.
            creatingFailureAnalysisHandler = true;
            try
            {
                SetReplayScore(score);
            }
            finally
            {
                creatingFailureAnalysisHandler = false;
            }
        }

        protected override ReplayRecorder CreateReplayRecorder(Score score) => new OsuReplayRecorder(score);

        private int? previewObjectIndex;
        private bool focusPreviewObject;

        private readonly Dictionary<Drawable, float> previewAlphas = new Dictionary<Drawable, float>();

        public void SetReplayObjectFocus(bool focused) => focusPreviewObject = focused;

        public void SetReplayObjectPreview(int? objectIndex)
        {
            if (previewObjectIndex == objectIndex)
                return;
            restorePreviewAlphas();
            previewObjectIndex = objectIndex;
            Playfield.ReplayObjectMarker.ClearTarget();
            if (objectIndex == null)
                focusPreviewObject = false;
        }

        protected override void Update()
        {
            // Undo last frame's presentation-only override before any gameplay or pooling update.
            restorePreviewAlphas();
            base.Update();
        }

        protected override void UpdateAfterChildren()
        {
            base.UpdateAfterChildren();
            if (FrameStableClock.IsRunning || previewObjectIndex is not int index || index < 0 || index >= Beatmap.HitObjects.Count)
            {
                Playfield.ReplayObjectMarker.ClearTarget();
                return;
            }

            var obj = Beatmap.HitObjects[index];
            if (Math.Abs(FrameStableClock.CurrentTime - ReplayTransport.ObjectAppearanceTime(obj)) >= 0.001)
            {
                Playfield.ReplayObjectMarker.ClearTarget();
                return;
            }

            // At the exact preempt boundary the native fade starts at alpha zero and cannot advance while paused.
            // Keep the original timestamp, geometry, skin, transforms and judgements; reveal only its frozen preview.
            var drawable = Playfield.HitObjectContainer.AliveObjects.FirstOrDefault(d => d.HitObject == obj);
            if (drawable == null || drawable.Judged)
            {
                Playfield.ReplayObjectMarker.ClearTarget();
                return;
            }

            var head = drawable switch
            {
                DrawableHitCircle circle => circle,
                DrawableSlider slider => slider.HeadCircle,
                _ => null,
            };
            RectangleF bounds;
            if (head != null)
                bounds = Playfield.ToLocalSpace(head.HitArea.ScreenSpaceDrawQuad).AABBFloat;
            else
            {
                var centre = Playfield.ToLocalSpace(drawable.ScreenSpaceDrawQuad.Centre);
                bounds = new RectangleF(centre.X - 32, centre.Y - 32, 64, 64);
            }
            Playfield.ReplayObjectMarker.SetTarget(index, bounds);
            if (focusPreviewObject)
            {
                // Root alpha also applies to its proxied approach circle / slider / spinner layers.
                // Restore before the next pool or gameplay update, just like the frozen preview.
                foreach (var other in Playfield.HitObjectContainer.AliveObjects.Where(d => d != drawable))
                {
                    previewAlphas.TryAdd(other, other.Alpha);
                    other.Alpha *= 0.12f;
                }
                previewAlphas.TryAdd(Playfield.FollowPoints, Playfield.FollowPoints.Alpha);
                Playfield.FollowPoints.Alpha *= 0.12f;
            }
            showPreview(drawable, 1);
            switch (drawable)
            {
                case DrawableHitCircle circle:
                    showCircle(circle);
                    break;
                case DrawableSlider slider:
                    showCircle(slider.HeadCircle);
                    showPreview(slider.Body, 0.4f);
                    break;
                case DrawableSpinner spinner:
                    showPreview(spinner.Body, 0.4f);
                    break;
            }

            void showCircle(DrawableHitCircle circle)
            {
                showPreview(circle, 1);
                showPreview(circle.CirclePiece, 0.4f);
                // Hidden's intentionally absent approach circle must stay hidden.
                if (!Mods.Any(m => m is OsuModHidden))
                    showPreview(circle.ApproachCircle, 0.4f);
            }
        }

        private void showPreview(Drawable drawable, float minimumAlpha)
        {
            previewAlphas.TryAdd(drawable, drawable.Alpha);
            drawable.Alpha = Math.Max(drawable.Alpha, minimumAlpha);
        }

        private void restorePreviewAlphas()
        {
            foreach (var (drawable, alpha) in previewAlphas)
                drawable.Alpha = alpha;
            previewAlphas.Clear();
        }

        public ReplayPracticeRange PrepareObjectPractice(int startingObjectIndex)
        {
            if (startingObjectIndex < 0 || startingObjectIndex >= Beatmap.HitObjects.Count)
                throw new ArgumentOutOfRangeException(nameof(startingObjectIndex));

            // Clone only the list. These are this independent Player's converted objects, never source beatmap objects.
            // Do not reconvert, restack, reapply mods or synthesize perfect results for the discarded prefix.
            var range = (Beatmap<OsuHitObject>)Beatmap.Clone();
            range.HitObjects = Beatmap.HitObjects.Skip(startingObjectIndex).ToList();
            var retained = new HashSet<HitObject>();
            foreach (var obj in range.HitObjects)
                includeNested(obj);
            var results = Playfield.GetJudgedResults().Where(r => retained.Contains(r.HitObject)).ToArray();
            foreach (var obj in Beatmap.HitObjects.Take(startingObjectIndex).ToArray())
                RemoveHitObject(obj);
            return new ReplayPracticeRange(range, results);

            void includeNested(HitObject obj)
            {
                retained.Add(obj);
                foreach (var nested in obj.NestedHitObjects)
                    includeNested(nested);
            }
        }

        public bool IsReplayPracticeReady(double time)
        {
            var handler = (OsuFramedReplayInputHandler?)KeyBindingInputManager.ReplayInputHandler;
            return handler != null && Math.Abs(FrameStableClock.CurrentTime - time) < 0.001
                                   && (handler.NextFrame == null || handler.NextFrame.Time > time);
        }

        public double GetSafeReplayPracticeTime(double requestedTime)
        {
            var next = Objects.Cast<OsuHitObject>().FirstOrDefault(h => h.GetEndTime() + h.HitWindows.WindowFor(HitResult.Miss) >= requestedTime);
            if (next == null)
                return requestedTime;
            double time = Math.Min(requestedTime, next.StartTime - Math.Max(next.TimePreempt, next.HitWindows.WindowFor(HitResult.Miss)) - 1);
            // An earlier object may still cross this point. Never select a point inside a long object or hit window.
            bool changed;
            do
            {
                changed = false;
                foreach (var obj in Objects.Cast<OsuHitObject>())
                {
                    double window = obj.HitWindows.WindowFor(HitResult.Miss);
                    if (obj.StartTime - window <= time && obj.GetEndTime() + window >= time)
                    {
                        time = obj.StartTime - Math.Max(obj.TimePreempt, window) - 1;
                        changed = true;
                    }
                }
            } while (changed);
            return time;
        }

        public ReplayPracticePreparation CreateReplayPracticePreparation(IFrameBasedClock clock)
        {
            var preparation = new OsuReplayPracticePreparation(this) { Clock = clock };
            AddInternal(preparation);
            return preparation;
        }

        public override double GameplayStartTime
        {
            get
            {
                if (Objects.FirstOrDefault() is OsuHitObject first)
                    return first.StartTime - Math.Max(2000, first.TimePreempt);

                return 0;
            }
        }
    }
}
