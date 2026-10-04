// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Input.Bindings;
using osu.Framework.Bindables;
using osu.Framework.Localisation;
using osu.Game.Input.Bindings;
using osu.Game.Screens.Play;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Scoring;
using osu.Framework.Graphics.Primitives;

namespace osu.Game.Tests.NonVisual
{
    [TestFixture]
    public class ReplayTransportTest
    {
        [Test]
        public void TestObjectNavigationUsesScheduledHeadsAndRemembersIdentity()
        {
            HitObject[] objects =
            {
                new Slider { StartTime = 1000, TimePreempt = 1200 },
                new Slider { StartTime = 2000, TimePreempt = 1200 },
                new Slider { StartTime = 3000, TimePreempt = 1200 },
            };
            // At a missed first head, the next slider has already appeared, but backward still selects the first slider.
            var first = ReplayTransport.FindObject(objects, 1500, -1);
            Assert.That(first, Is.EqualTo(new ReplayObjectSelection(0, -200)));
            var second = ReplayTransport.FindObject(objects, first!.Time, 1, first.Index);
            Assert.That(second, Is.EqualTo(new ReplayObjectSelection(1, 800)));
            var third = ReplayTransport.FindObject(objects, second!.Time, 1, second.Index);
            Assert.That(third, Is.EqualTo(new ReplayObjectSelection(2, 1800)));
            Assert.That(ReplayTransport.FindObject(objects, third!.Time, 1, third.Index), Is.Null);
            Assert.That(ReplayTransport.FindObject(objects, second.Time, -1, second.Index), Is.EqualTo(first));
            Assert.That(ReplayTransport.FindObject(objects, first.Time, -1, first.Index), Is.Null);
            Assert.That(ReplayTransport.FindObject(objects, 4000, 1), Is.Null);
            Assert.That(ReplayTransport.FindObject(System.Array.Empty<HitObject>(), 123, 1), Is.Null);
        }

        [Test]
        public void TestFirstForwardObjectStepCannotRewindIntoAnAlreadyVisibleObject()
        {
            HitObject[] objects =
            {
                new Slider { StartTime = 1000, TimePreempt = 1200 },
                new Slider { StartTime = 2000, TimePreempt = 1200 },
                new Slider { StartTime = 3000, TimePreempt = 1200 },
            };
            Assert.That(ReplayTransport.FindObject(objects, 1500, 1), Is.EqualTo(new ReplayObjectSelection(2, 1800)));
            Assert.That(ReplayTransport.FindObject(objects, 1800, 1), Is.Null);
            // Explicitly selected objects still advance by identity, including an already-visible slider.
            Assert.That(ReplayTransport.FindObject(objects, -200, 1, 0), Is.EqualTo(new ReplayObjectSelection(1, 800)));
        }

        [Test]
        public void TestCoincidentAndNonMonotonicAppearancesRemainIndividuallySelectable()
        {
            HitObject[] objects =
            {
                new Slider { StartTime = 1000, TimePreempt = 1200 },
                new Slider { StartTime = 2000, TimePreempt = 2200 },
                new Slider { StartTime = 3000, TimePreempt = 4000 },
            };
            var first = ReplayTransport.FindObject(objects, 1500, -1)!;
            var second = ReplayTransport.FindObject(objects, first.Time, 1, first.Index)!;
            var third = ReplayTransport.FindObject(objects, second.Time, 1, second.Index)!;
            Assert.Multiple(() =>
            {
                Assert.That(first, Is.EqualTo(new ReplayObjectSelection(0, -200)));
                Assert.That(second, Is.EqualTo(new ReplayObjectSelection(1, -200)));
                Assert.That(third, Is.EqualTo(new ReplayObjectSelection(2, -1000)));
            });
        }

        [Test]
        public void TestFrameNavigationKeepsExactTimestamps()
        {
            double[] times = { 1000.125, 1500.875, 1500.875, 1900.25 };
            Assert.That(ReplayTransport.FindFrameTime(times, 1500.875, -1), Is.EqualTo(1000.125));
            Assert.That(ReplayTransport.FindFrameTime(times, 1500.875, 1), Is.EqualTo(1900.25));
        }

        [Test]
        public void TestSecondsAreRateIndependentAndCannotReverseAtBounds()
        {
            var transport = new TestTransport { TransportTime = 1500, TransportRate = { Value = 0.5 } };
            ReplayTransport.HandleAction(transport, GlobalAction.SeekReplayOneSecondForward);
            Assert.That(transport.TransportTime, Is.EqualTo(2500));
            ReplayTransport.HandleAction(transport, GlobalAction.SeekReplayOneSecondBackward);
            Assert.That(transport.TransportTime, Is.EqualTo(1500));
            transport.TransportTime = 7300;
            Assert.That(ReplayTransport.SeekRelative(transport, 1000), Is.False);
            Assert.That(transport.TransportTime, Is.EqualTo(7300));
            ReplayTransport.SeekRelative(transport, -1000);
            Assert.That(transport.TransportTime, Is.EqualTo(6300));
            transport.TransportTime = 0;
            Assert.That(ReplayTransport.SeekRelative(transport, -1000), Is.False);
        }

        private class TestTransport : IReplayTransport
        {
            public double TransportTime { get; set; }
            public double TransportEndTime => 7000;
            public double TransportSeekStartTime => 0;
            public ReplayObjectSelection? SelectedObject => null;
            public ReplayPanelLayout PanelLayout { get; } = new ReplayPanelLayout();
            public ReplayFailureIndex FailureIndex { get; } = new ReplayFailureIndex();
            public LocalisableString? FailureNavigationMessage => null;
            public (ReplayFailureKind Kind, int Direction)? FailureRequest { get; private set; }
            public void SeekNextFailure(ReplayFailureKind kind) => FailureRequest = (kind, 1);
            public void SeekPreviousFailure(ReplayFailureKind kind) => FailureRequest = (kind, -1);
            public double? TransportStartTime => null;
            public bool CanSeekTransport => true;
            public bool IsTransportPaused => true;
            public Bindable<double> TransportRate { get; } = new BindableDouble(1);
            public bool ToggleTransportPause() => true;
            public void BeginTransportSeek() { }
            public bool SeekTransport(double time)
            {
                TransportTime = time;
                return true;
            }
            public void StepTransportFrame(int direction) { }
            public void StepTransportObject(int direction) { }
        }

        [TestCase(GlobalAction.PreviousReplayObject, InputKey.A)]
        [TestCase(GlobalAction.NextReplayObject, InputKey.D)]
        [TestCase(GlobalAction.SeekReplayOneSecondBackward, InputKey.Q)]
        [TestCase(GlobalAction.SeekReplayOneSecondForward, InputKey.E)]
        [TestCase(GlobalAction.IncreaseReplayPlaybackSpeed, InputKey.W)]
        [TestCase(GlobalAction.DecreaseReplayPlaybackSpeed, InputKey.S)]
        [TestCase(GlobalAction.ResetReplayPlaybackSpeed, InputKey.F)]
        [TestCase(GlobalAction.NextReplayMiss, InputKey.N)]
        [TestCase(GlobalAction.NextReplayIgnored, InputKey.M)]
        [TestCase(GlobalAction.PreviousReplayMiss, InputKey.N, true)]
        [TestCase(GlobalAction.PreviousReplayIgnored, InputKey.M, true)]
        public void TestRequestedDefaults(GlobalAction action, InputKey key, bool shift = false)
            => Assert.That(GlobalActionContainer.GetDefaultBindingsFor(GlobalActionCategory.Replay).Single(b => (GlobalAction)b.Action == action).KeyCombination,
                Is.EqualTo(shift ? new KeyCombination(InputKey.Shift, key) : new KeyCombination(key)));

        [TestCase(GlobalAction.NextReplayMiss, ReplayFailureKind.Miss, 1)]
        [TestCase(GlobalAction.NextReplayIgnored, ReplayFailureKind.Ignored, 1)]
        [TestCase(GlobalAction.PreviousReplayMiss, ReplayFailureKind.Miss, -1)]
        [TestCase(GlobalAction.PreviousReplayIgnored, ReplayFailureKind.Ignored, -1)]
        public void TestFailureActionsDispatchTheRequestedKindAndDirection(GlobalAction action, ReplayFailureKind kind, int direction)
        {
            var transport = new TestTransport { TransportTime = 1500 };
            Assert.That(ReplayTransport.HandleAction(transport, action), Is.True);
            Assert.That(transport.FailureRequest, Is.EqualTo((kind, direction)));
            Assert.That(transport.TransportTime, Is.EqualTo(1500));
        }

        [TestCase(HitResult.Miss, HitResult.Great, ReplayFailureKind.Miss)]
        [TestCase(HitResult.LargeTickMiss, HitResult.LargeTickHit, ReplayFailureKind.Miss)]
        [TestCase(HitResult.ComboBreak, HitResult.IgnoreHit, ReplayFailureKind.Miss)]
        [TestCase(HitResult.IgnoreMiss, HitResult.SliderTailHit, ReplayFailureKind.Ignored)]
        [TestCase(HitResult.IgnoreMiss, HitResult.SmallBonus, null)]
        [TestCase(HitResult.SmallTickMiss, HitResult.SmallTickHit, null)]
        [TestCase(HitResult.IgnoreHit, HitResult.IgnoreHit, null)]
        [TestCase(HitResult.Meh, HitResult.Great, null)]
        [TestCase(HitResult.Ok, HitResult.Great, null)]
        [TestCase(HitResult.Great, HitResult.Great, null)]
        [TestCase(HitResult.None, HitResult.SliderTailHit, null)]
        public void TestFailureClassificationUsesLostComboIncrement(HitResult actual, HitResult maximum, ReplayFailureKind? expected)
        {
            var result = new JudgementResult(new HitCircle(), new TestJudgement(maximum)) { Type = actual };
            Assert.That(ReplayFailureIndex.Classify(result), Is.EqualTo(expected));
        }

        [Test]
        public void TestFailureIndexDeduplicatesNavigationByRootAndSkipsPastEvents()
        {
            var first = new HitCircle { StartTime = 1000, TimePreempt = 1200 };
            var second = new HitCircle { StartTime = 2000, TimePreempt = 1200 };
            var index = new ReplayFailureIndex();
            Assert.That(index.TryBegin(), Is.True);
            Assert.That(index.TryBegin(), Is.False);
            index.BindObjects(new HitObject[] { first, second });
            index.Add(new JudgementResult(first, new TestJudgement(HitResult.Great)) { Type = HitResult.Miss });
            index.Add(new JudgementResult(first, new TestJudgement(HitResult.Great)) { Type = HitResult.Miss });
            index.Add(new JudgementResult(second, new TestJudgement(HitResult.Great)) { Type = HitResult.Miss });
            index.Complete();
            Assert.That(index.TryBegin(), Is.False);
            Assert.That(index.FindNext(ReplayFailureKind.Miss, 0, null), Is.EqualTo(new ReplayObjectSelection(0, -200)));
            Assert.That(index.FindNext(ReplayFailureKind.Miss, -200, 0), Is.EqualTo(new ReplayObjectSelection(1, 800)));
            Assert.That(index.FindNext(ReplayFailureKind.Miss, 2000, null), Is.Null);
            Assert.That(index.FindNext(ReplayFailureKind.Miss, 0, 1), Is.Null);
            Assert.That(index.FindNext(ReplayFailureKind.Ignored, 0, null), Is.Null);
        }

        [Test]
        public void TestCancelledAnalysisCanRestartWithoutPartialResults()
        {
            var obj = new HitCircle { StartTime = 1000 };
            var index = new ReplayFailureIndex();
            index.TryBegin();
            index.BindObjects(new HitObject[] { obj });
            index.Add(new JudgementResult(obj, new TestJudgement(HitResult.Great)) { Type = HitResult.Miss });
            index.Cancel();
            Assert.That(index.IsAnalysing, Is.False);
            Assert.That(index.Failures, Is.Empty);
            Assert.That(index.TryBegin(), Is.True);
            index.Fail();
            Assert.That(index.TryBegin(), Is.False);
            Assert.That(index.Unavailable, Is.True);
        }

        private class TestJudgement(HitResult maximum) : Judgement
        {
            public override HitResult MaxResult => maximum;
        }

        [Test]
        public void TestPlaybackFailureNavigationFollowsJudgementTimeNotOverlappingRootOrder()
        {
            var first = new LateJudgementCircle { StartTime = 1000, TimePreempt = 1200 };
            var second = new HitCircle { StartTime = 2000, TimePreempt = 1200 };
            var index = new ReplayFailureIndex();
            index.TryBegin();
            index.BindObjects(new HitObject[] { first, second });
            index.Add(new JudgementResult(first, new TestJudgement(HitResult.Great)) { Type = HitResult.Miss, RawTime = 3000 });
            index.Add(new JudgementResult(second, new TestJudgement(HitResult.Great)) { Type = HitResult.Miss, RawTime = 2100 });
            index.Complete();
            Assert.That(index.FindNext(ReplayFailureKind.Miss, 0, null), Is.EqualTo(new ReplayObjectSelection(1, 800)));
            Assert.That(index.FindNext(ReplayFailureKind.Miss, 0, 0), Is.EqualTo(new ReplayObjectSelection(1, 800)));
        }

        private class LateJudgementCircle : HitCircle
        {
            public override double MaximumJudgementOffset => 10000;
        }

        [TestCase(ReplayFailureKind.Miss)]
        [TestCase(ReplayFailureKind.Ignored)]
        public void TestPreviousFailureReversesJudgementOrderAndSkipsTheSelectedRoot(ReplayFailureKind kind)
        {
            HitObject[] objects =
            {
                new LateJudgementCircle { StartTime = 1000, TimePreempt = 1200 },
                new LateJudgementCircle { StartTime = 2000, TimePreempt = 1200 },
                new LateJudgementCircle { StartTime = 3000, TimePreempt = 1200 },
            };
            var index = new ReplayFailureIndex();
            Assert.That(index.FindPrevious(kind, 4000, null), Is.Null);
            index.TryBegin();
            index.BindObjects(objects);
            var judgement = new TestJudgement(kind == ReplayFailureKind.Miss ? HitResult.Great : HitResult.SliderTailHit);
            var result = kind == ReplayFailureKind.Miss ? HitResult.Miss : HitResult.IgnoreMiss;
            index.Add(new JudgementResult(objects[0], judgement) { Type = result, RawTime = 3100 });
            index.Add(new JudgementResult(objects[0], judgement) { Type = result, RawTime = 3150 });
            index.Add(new JudgementResult(objects[1], judgement) { Type = result, RawTime = 2100 });
            index.Add(new JudgementResult(objects[2], judgement) { Type = result, RawTime = 3300 });
            index.Complete();
            Assert.That(index.FindPrevious(kind, 2100, null), Is.Null);
            Assert.That(index.FindPrevious(kind, 2100.0000001, null), Is.Null);
            Assert.That(index.FindPrevious(kind, 2500, null), Is.EqualTo(new ReplayObjectSelection(1, 800)));
            Assert.That(index.FindPrevious(kind, 3200, null), Is.EqualTo(new ReplayObjectSelection(0, -200)));
            Assert.That(index.FindPrevious(kind, 4000, null), Is.EqualTo(new ReplayObjectSelection(2, 1800)));
            Assert.That(index.FindPrevious(kind, 1800, 2), Is.EqualTo(new ReplayObjectSelection(1, 800)));
            Assert.That(index.FindPrevious(kind, 800, 1), Is.EqualTo(new ReplayObjectSelection(0, -200)));
            Assert.That(index.FindPrevious(kind, -200, 0), Is.Null);
            Assert.That(index.FindNext(kind, -200, 0), Is.EqualTo(new ReplayObjectSelection(1, 800)));
            Assert.That(index.FindNext(kind, 800, 1), Is.EqualTo(new ReplayObjectSelection(2, 1800)));
            Assert.That(index.FindPrevious(kind == ReplayFailureKind.Miss ? ReplayFailureKind.Ignored : ReplayFailureKind.Miss, 4000, null), Is.Null);
        }

        [TestCase(ReplayPanelSide.Left)]
        [TestCase(ReplayPanelSide.Right)]
        public void TestPanelGuttersExcludePlayfieldAndStayInViewport(ReplayPanelSide side)
        {
            var viewport = new RectangleF(0, 0, 800, 600);
            var playfield = new RectangleF(120, 80, 560, 420);
            var gutter = ReplayPanelGeometry.GetSideBounds(viewport, playfield, side);
            Assert.That(viewport.Contains(gutter), Is.True);
            Assert.That(gutter.IntersectsWith(playfield), Is.False);
            Assert.That(gutter.Width, Is.EqualTo(112));
            Assert.That(gutter.Height, Is.EqualTo(592));
            Assert.That(side == ReplayPanelSide.Left ? playfield.Left - gutter.Right : gutter.Left - playfield.Right, Is.EqualTo(4));
        }

        [TestCase(ReplayPanelSide.Left)]
        [TestCase(ReplayPanelSide.Right)]
        public void TestNoGutterDoesNotBorrowMinimumSizeFromPlayfield(ReplayPanelSide side)
        {
            var viewport = new RectangleF(0, 0, 800, 600);
            Assert.That(ReplayPanelGeometry.GetSideBounds(viewport, viewport, side).Width, Is.Zero);
            var oversizedPlayfield = new RectangleF(-50, -50, 900, 700);
            Assert.That(ReplayPanelGeometry.GetSideBounds(viewport, oversizedPlayfield, side).Width, Is.Zero);
        }
    }
}
