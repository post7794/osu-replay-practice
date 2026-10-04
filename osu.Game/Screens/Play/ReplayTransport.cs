// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Bindables;
using osu.Framework.Localisation;
using osu.Game.Input.Bindings;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Types;

namespace osu.Game.Screens.Play
{
    public interface IReplayTransport
    {
        double TransportTime { get; }
        double TransportEndTime { get; }
        double TransportSeekStartTime { get; }
        ReplayObjectSelection? SelectedObject { get; }
        ReplayPanelLayout PanelLayout { get; }
        ReplayFailureIndex FailureIndex { get; }
        LocalisableString? FailureNavigationMessage { get; }
        void SeekNextFailure(ReplayFailureKind kind);
        void SeekPreviousFailure(ReplayFailureKind kind);
        double? TransportStartTime { get; }
        bool CanSeekTransport { get; }
        bool IsTransportPaused { get; }
        bool ToggleTransportPause();
        Bindable<double> TransportRate { get; }
        void BeginTransportSeek();
        bool SeekTransport(double time);
        void StepTransportFrame(int direction);
        void StepTransportObject(int direction);
    }

    public record ReplayObjectSelection(int Index, double Time);

    public static class ReplayTransport
    {
        public static double FindFrameTime(IEnumerable<double> times, double current, int direction)
            => adjacentTime(times, current, direction);

        public static double ObjectAppearanceTime(HitObject obj)
            => obj.StartTime - ((obj as IHasTimePreempt)?.TimePreempt ?? 0);

        /// <summary>
        /// Navigate by root object identity, not by clicks, judgements or sorted appearance timestamps.
        /// The first backward step picks the most recent scheduled head (including a missed head).
        /// The first forward step picks an appearance still ahead, never a future head whose preempt has passed.
        /// Further steps use the selected object's index, even when appearances overlap or coincide.
        /// </summary>
        public static ReplayObjectSelection? FindObject(IReadOnlyList<HitObject> objects, double current, int direction, int? selectedIndex = null)
        {
            if (direction == 0)
                return null;
            int index = -1;
            if (selectedIndex is int selected)
                index = selected + Math.Sign(direction);
            else if (direction < 0)
            {
                for (int i = objects.Count - 1; i >= 0; i--)
                {
                    if (objects[i].StartTime <= current + 0.000001)
                    {
                        index = i;
                        break;
                    }
                }
            }
            else
            {
                for (int i = 0; i < objects.Count; i++)
                {
                    if (ObjectAppearanceTime(objects[i]) > current + 0.000001)
                    {
                        index = i;
                        break;
                    }
                }
            }
            return index >= 0 && index < objects.Count
                ? new ReplayObjectSelection(index, ObjectAppearanceTime(objects[index]))
                : null;
        }

        private static double adjacentTime(IEnumerable<double> times, double current, int direction)
        {
            var ordered = times.Distinct().OrderBy(t => t).ToArray();
            if (ordered.Length == 0)
                return current;
            return direction < 0
                ? ordered.LastOrDefault(t => t < current - 0.000001, current)
                : ordered.FirstOrDefault(t => t > current + 0.000001, current);
        }

        public static bool SeekRelative(IReplayTransport transport, double amount)
        {
            if (!transport.CanSeekTransport || !double.IsFinite(amount) || amount == 0)
                return false;
            transport.BeginTransportSeek();
            double current = transport.TransportTime;
            double target = Math.Clamp(current + amount, transport.TransportSeekStartTime, transport.TransportEndTime);
            // A completed attempt may be beyond the last object due to its judgement window.
            // Forward navigation at the end must not turn into a backwards seek.
            return (amount > 0 ? target > current : target < current) && transport.SeekTransport(target);
        }

        public static bool HandleAction(IReplayTransport transport, GlobalAction action)
        {
            switch (action)
            {
                case GlobalAction.PreviousReplayObject:
                    transport.StepTransportObject(-1);
                    return true;
                case GlobalAction.NextReplayObject:
                    transport.StepTransportObject(1);
                    return true;
                case GlobalAction.NextReplayMiss:
                    transport.SeekNextFailure(ReplayFailureKind.Miss);
                    return true;
                case GlobalAction.NextReplayIgnored:
                    transport.SeekNextFailure(ReplayFailureKind.Ignored);
                    return true;
                case GlobalAction.PreviousReplayMiss:
                    transport.SeekPreviousFailure(ReplayFailureKind.Miss);
                    return true;
                case GlobalAction.PreviousReplayIgnored:
                    transport.SeekPreviousFailure(ReplayFailureKind.Ignored);
                    return true;
                case GlobalAction.SeekReplayOneSecondBackward:
                case GlobalAction.SeekReplayOneSecondForward:
                    SeekRelative(transport, action == GlobalAction.SeekReplayOneSecondBackward ? -1000 : 1000);
                    return true;
                case GlobalAction.IncreaseReplayPlaybackSpeed:
                case GlobalAction.DecreaseReplayPlaybackSpeed:
                    transport.TransportRate.Value += action == GlobalAction.IncreaseReplayPlaybackSpeed ? 0.05 : -0.05;
                    return true;
                case GlobalAction.ResetReplayPlaybackSpeed:
                    transport.TransportRate.Value = 1;
                    return true;
                default:
                    return false;
            }
        }
    }
}
