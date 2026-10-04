// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Scoring;

namespace osu.Game.Screens.Play
{
    public enum ReplayFailureKind
    {
        Miss,
        Ignored,
    }

    public record ReplayFailure(int ObjectIndex, double AppearanceTime, double JudgementTime, ReplayFailureKind Kind);

    /// <summary>
    /// Immutable-value outcomes of the original replay. Never records the user's practice branch.
    /// </summary>
    public class ReplayFailureIndex
    {
        private readonly Dictionary<HitObject, int> roots = new Dictionary<HitObject, int>();
        private readonly List<ReplayFailure> failures = new List<ReplayFailure>();
        private IReadOnlyList<HitObject> objects = new List<HitObject>();
        public bool IsAnalysing { get; private set; }
        public bool IsReady { get; private set; }
        public bool Unavailable { get; private set; }
        internal int Generation { get; private set; }
        public IReadOnlyList<ReplayFailure> Failures => failures;

        public bool TryBegin()
        {
            if (IsAnalysing || IsReady || Unavailable)
                return false;
            IsAnalysing = true;
            Generation++;
            failures.Clear();
            roots.Clear();
            return true;
        }

        internal void BindObjects(IReadOnlyList<HitObject> hitObjects)
        {
            objects = hitObjects;
            for (int i = 0; i < objects.Count; i++)
                include(objects[i], i);

            void include(HitObject obj, int root)
            {
                roots[obj] = root;
                foreach (var nested in obj.NestedHitObjects)
                    include(nested, root);
            }
        }

        internal void Add(JudgementResult result)
        {
            ReplayFailureKind? kind = Classify(result);
            if (kind == null || !roots.TryGetValue(result.HitObject, out int root))
                return;
            failures.Add(new ReplayFailure(root, ReplayTransport.ObjectAppearanceTime(objects[root]), result.TimeAbsolute, kind.Value));
        }

        public static ReplayFailureKind? Classify(JudgementResult result)
        {
            if (result.Type.BreaksCombo())
                return ReplayFailureKind.Miss;
            // A dropped tail is not a combo reset: it forfeits the increment a perfect tail would award.
            // IgnoreHit / bonus misses / timing grades are not this kind of lost combo.
            if (result.HasResult && !result.IsHit && result.Judgement.MaxResult.IncreasesCombo())
                return ReplayFailureKind.Ignored;
            return null;
        }

        internal void Complete()
        {
            IsAnalysing = false;
            IsReady = true;
            roots.Clear();
            objects = new List<HitObject>();
        }

        internal void Cancel()
        {
            if (IsReady)
                return;
            IsAnalysing = false;
            Generation++;
            failures.Clear();
            roots.Clear();
            objects = new List<HitObject>();
        }

        internal void Fail()
        {
            Cancel();
            Unavailable = true;
        }

        public ReplayObjectSelection? FindNext(ReplayFailureKind kind, double currentTime, int? selectedIndex)
        {
            if (!IsReady)
                return null;
            var next = failures.Where(f => f.Kind == kind && (selectedIndex is int selected
                    ? f.ObjectIndex > selected
                    : f.JudgementTime > currentTime + 0.000001))
                // Overlapping sliders can finish after the next object's head. Playback navigation
                // follows judgement time; an explicit selection instead advances by root identity.
                .OrderBy(f => selectedIndex.HasValue ? f.ObjectIndex : f.JudgementTime)
                .ThenBy(f => f.ObjectIndex).FirstOrDefault();
            return next == null ? null : new ReplayObjectSelection(next.ObjectIndex, next.AppearanceTime);
        }

        public ReplayObjectSelection? FindPrevious(ReplayFailureKind kind, double currentTime, int? selectedIndex)
        {
            if (!IsReady)
                return null;
            var previous = failures.Where(f => f.Kind == kind && (selectedIndex is int selected
                    ? f.ObjectIndex < selected
                    : f.JudgementTime < currentTime - 0.000001))
                // Reverse the same ordering as forward navigation, skipping all sub-failures
                // of the selected root rather than repeatedly returning to the same slider.
                .OrderByDescending(f => selectedIndex.HasValue ? f.ObjectIndex : f.JudgementTime)
                .ThenByDescending(f => f.ObjectIndex).FirstOrDefault();
            return previous == null ? null : new ReplayObjectSelection(previous.ObjectIndex, previous.AppearanceTime);
        }
    }
}
