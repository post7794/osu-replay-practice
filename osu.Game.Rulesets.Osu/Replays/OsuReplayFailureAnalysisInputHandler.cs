// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Game.Replays;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu.Objects;

namespace osu.Game.Rulesets.Osu.Replays
{
    /// <summary>
    /// Visits slider judgement boundaries while reconstructing a detached failure index.
    /// Uses the original replay's interpolation and button states; never synthesises input or results.
    /// </summary>
    internal class OsuReplayFailureAnalysisInputHandler : OsuFramedReplayInputHandler
    {
        private readonly double[] checkpoints;

        public OsuReplayFailureAnalysisInputHandler(Replay replay, IEnumerable<HitObject> objects)
            : base(replay)
        {
            checkpoints = objects.OfType<Slider>().SelectMany(slider => slider.NestedHitObjects
                                 .Where(o => o is SliderTick or SliderRepeat or SliderTailCircle)
                                 .Select(o => o is SliderTailCircle ? o.StartTime + SliderEventGenerator.TAIL_LENIENCY : o.StartTime)
                                 .Append(slider.TailCircle.StartTime))
                                 .Distinct().OrderBy(t => t).ToArray();
        }

        public override double? SetFrameFromTime(double time)
        {
            if (time > CurrentTime)
            {
                int next = Array.BinarySearch(checkpoints, CurrentTime);
                next = next < 0 ? ~next : next + 1;
                if (next < checkpoints.Length)
                    time = Math.Min(time, checkpoints[next]);
            }

            // A fast seek otherwise only visits recorded input frames. For example, a held
            // frame at 40336 ms and release at 40338 ms can straddle the tail's 40336.375 ms
            // leniency boundary. Skipping that boundary invents a dropped tail. Stop there
            // first, letting the native handler interpolate the cursor and retain the held
            // button. Existing replay frames (including releases) still execute in order.
            return base.SetFrameFromTime(time);
        }
    }
}
