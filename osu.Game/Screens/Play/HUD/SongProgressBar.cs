// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Allocation;
using osu.Framework.Graphics.Containers;
using osu.Framework.Input.Events;
using osu.Framework.Threading;
using osuTK;
using osuTK.Input;

namespace osu.Game.Screens.Play.HUD
{
    public abstract partial class SongProgressBar : CompositeDrawable
    {
        /// <summary>
        /// The current seek position of the audio, on a (0..1) range.
        /// This is generally the seek target, which will eventually match the gameplay clock when it catches up.
        /// </summary>
        protected double AudioProgress => length == 0 ? 1 : AudioTime / length;

        /// <summary>
        /// The current (non-frame-stable) audio time.
        /// </summary>
        protected double AudioTime => Math.Clamp(DisplayedTime - StartTime, 0.0, length);

        [Resolved]
        private Player? player { get; set; }

        /// <summary>
        /// Replay navigation has no cosmetic interpolation. Normal gameplay retains the skin's smoothing.
        /// </summary>
        protected bool ImmediateProgress => player is IReplayTransport;

        public bool IsSeeking { get; private set; }
        public double DisplayedTime => IsSeeking ? previewTime : GameplayClock.CurrentTime;
        private double previewTime;
        private bool practiceSeek;

        [Resolved]
        protected IGameplayClock GameplayClock { get; private set; } = null!;

        /// <summary>
        /// Action which is invoked when a seek is requested, with the proposed millisecond value for the seek operation.
        /// </summary>
        public Action<double>? OnSeek { get; set; }

        /// <summary>
        /// Whether the progress bar should allow interaction, ie. to perform seek operations.
        /// </summary>
        public virtual bool Interactive { get; set; }

        public double StartTime { get; set; }

        public double EndTime { get; set; } = 1.0;

        private double length => EndTime - StartTime;

        private bool handleClick;

        protected override bool OnMouseDown(MouseDownEvent e)
        {
            handleClick = true;
            if (e.Button == MouseButton.Left && Interactive && player is IReplayTransport transport && transport.CanSeekTransport)
            {
                transport.BeginTransportSeek();
                practiceSeek = player is ReplayPracticePlayer;
                IsSeeking = true;
                previewTime = transport.TransportTime;
                handleMouseInput(e, true);
                return true;
            }
            return base.OnMouseDown(e);
        }

        protected override bool OnClick(ClickEvent e)
        {
            if (player is IReplayTransport)
                return true;
            if (handleClick)
                handleMouseInput(e);

            return true;
        }

        protected override void OnDrag(DragEvent e)
        {
            handleMouseInput(e);
        }

        protected override bool OnDragStart(DragStartEvent e)
        {
            Vector2 posDiff = e.MouseDownPosition - e.MousePosition;

            if (Math.Abs(posDiff.X) < Math.Abs(posDiff.Y))
            {
                handleClick = false;
                return false;
            }

            handleMouseInput(e);
            return true;
        }

        protected override void OnMouseUp(MouseUpEvent e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButton.Left || !IsSeeking)
                return;
            handleMouseInput(e);
            IsSeeking = false;
            if (practiceSeek)
            {
                double target = previewTime;
                Schedule(() => ((IReplayTransport)player!).SeekTransport(target));
            }
        }

        private void handleMouseInput(UIEvent e, bool forceSeek = false)
        {
            if (!Interactive)
                return;

            double relativeX = Math.Clamp(ToLocalSpace(e.ScreenSpaceMousePosition).X / DrawWidth, 0, 1);
            double target = StartTime + (EndTime - StartTime) * relativeX;
            if (player is IReplayTransport transport)
            {
                if (!IsSeeking || !transport.CanSeekTransport)
                    return;
                bool changed = previewTime != target;
                previewTime = target;
                if (!practiceSeek && (changed || forceSeek))
                    transport.SeekTransport(target);
            }
            else
                onUserChange(target);
        }

        private ScheduledDelegate? scheduledSeek;

        private void onUserChange(double value)
        {
            scheduledSeek?.Cancel();
            scheduledSeek = Schedule(() => OnSeek?.Invoke(value));
        }
    }
}
