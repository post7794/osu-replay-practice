// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using osu.Game.Online.Spectator;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Osu.Replays;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Screens.Play;
using osuTK;

namespace osu.Game.Tests.NonVisual
{
    [TestFixture]
    public class ReplayPracticeSessionTest
    {
        private static Score createScore()
        {
            var score = new Score { ScoreInfo = { Ruleset = new OsuRuleset().RulesetInfo } };
            var frame = new OsuReplayFrame(1000, new Vector2(100, 100), OsuAction.LeftButton)
            {
                Header = new FrameHeader(score.ScoreInfo, new ScoreProcessorStatistics { BaseScore = 300, MaximumBaseScore = 300 }),
            };
            frame.Header.Statistics[HitResult.Great] = 1;
            score.Replay.Frames.Add(frame);
            return score;
        }

        private static ReplayPracticeSession createSession(Score? score = null) => new ReplayPracticeSession(score ?? createScore(), Array.Empty<Mod>(), 1500, 1000, 0.75);

        [Test]
        public void TestMutableReplayDataIsNotShared()
        {
            var original = createScore();
            var session = createSession(original);
            var first = session.CreateReplayScore();
            var frame = (OsuReplayFrame)first.Replay.Frames.Single();
            frame.Time = 50;
            frame.Position = Vector2.Zero;
            frame.Actions.Clear();
            frame.Header!.Statistics.Clear();
            frame.Header.ScoreProcessorStatistics.BaseScore = 0;
            first.ScoreInfo.Accuracy = 0;

            var second = session.CreateReplayScore();
            var restored = (OsuReplayFrame)second.Replay.Frames.Single();
            Assert.Multiple(() =>
            {
                Assert.That(original.Replay.Frames.Single().Time, Is.EqualTo(1000));
                Assert.That(restored.Time, Is.EqualTo(1000));
                Assert.That(restored.Position, Is.EqualTo(new Vector2(100, 100)));
                Assert.That(restored.Actions, Is.EquivalentTo(new[] { OsuAction.LeftButton }));
                Assert.That(restored.Header!.Statistics[HitResult.Great], Is.EqualTo(1));
                Assert.That(restored.Header.ScoreProcessorStatistics.BaseScore, Is.EqualTo(300));
                Assert.That(restored, Is.Not.SameAs(original.Replay.Frames.Single()));
                Assert.That(second.ScoreInfo, Is.Not.SameAs(original.ScoreInfo));
            });
        }

        [TestCase(typeof(OsuModCinema))]
        [TestCase(typeof(OsuModRelax))]
        [TestCase(typeof(OsuModAutopilot))]
        [TestCase(typeof(OsuModSpunOut))]
        public void TestAutomationIsRejected(Type modType)
        {
            var mod = (Mod)Activator.CreateInstance(modType)!;
            Assert.That(ReplayPracticeSession.GetUnsupportedReason(0, new[] { mod })?.ToString(), Does.Contain(mod.Name));
            Assert.Throws<ArgumentException>(() => new ReplayPracticeSession(createScore(), new[] { mod }, 1000, 1000, 1));
            Assert.That(ReplayPracticeSession.GetUnsupportedReason(0, new Mod[] { new OsuModAutoplay(), mod })?.ToString(), Does.Contain(mod.Name));
            Assert.Throws<ArgumentException>(() => new ReplayPracticeSession(createScore(), new Mod[] { new OsuModAutoplay(), mod }, 1000, 1000, 1));
        }

        [Test]
        public void TestAutoplayIsHistoricalInputOnly()
        {
            var source = createScore();
            var random = new OsuModRandom { Seed = { Value = 123456 }, AngleSharpness = { Value = 4.2f } };
            var speed = new OsuModDoubleTime { SpeedChange = { Value = 1.2 } };
            source.ScoreInfo.Mods = new Mod[] { new OsuModAutoplay(), random, speed, new OsuModHidden() };
            var session = new ReplayPracticeSession(source, source.ScoreInfo.Mods, 1000, 1000, 0.75);
            Assert.Multiple(() =>
            {
                Assert.That(ReplayPracticeSession.GetUnsupportedReason(0, source.ScoreInfo.Mods), Is.Null);
                Assert.That(session.IsAutoplayBaseline, Is.True);
                Assert.That(session.Mods.Select(m => m.Acronym), Is.EquivalentTo(new[] { "RD", "DT", "HD" }));
                Assert.That(session.CreateReplayScore().ScoreInfo.Mods.OfType<ModAutoplay>(), Is.Empty);
                Assert.That(source.ScoreInfo.Mods.OfType<ModAutoplay>().Count(), Is.EqualTo(1));
                Assert.That(session.CreateReplayScore().Replay.Frames[0], Is.Not.SameAs(source.Replay.Frames[0]));
            });
            random.Seed.Value = 987654;
            speed.SpeedChange.Value = 1.8;
            Assert.That(session.Mods.OfType<OsuModRandom>().Single().Seed.Value, Is.EqualTo(123456));
            Assert.That(session.Mods.OfType<OsuModDoubleTime>().Single().SpeedChange.Value, Is.EqualTo(1.2));
            var attempt = session.CreateReplayScore();
            ((OsuReplayFrame)attempt.Replay.Frames[0]).Actions.Clear();
            attempt.ScoreInfo.Mods.OfType<OsuModRandom>().Single().Seed.Value = 1;
            Assert.That(((OsuReplayFrame)session.CreateReplayScore().Replay.Frames[0]).Actions, Is.Not.Empty);
            Assert.That(session.CreateReplayScore().ScoreInfo.Mods.OfType<OsuModRandom>().Single().Seed.Value, Is.EqualTo(123456));
        }

        [Test]
        public void TestOtherRulesetsAreRejected() => Assert.That(ReplayPracticeSession.GetUnsupportedReason(1, Array.Empty<Mod>())?.ToString(), Does.Contain("osu!standard"));

        [Test]
        public void TestAttemptsAreRecordedOnceAndSpeedIsRetained()
        {
            var session = createSession();
            session.FinishAttempt(1000, null, 0, false);
            Assert.That(session.AttemptCount, Is.Zero, "Cancelling preparation must not count as an attempt.");
            for (int i = 0; i < 20; i++)
            {
                session.BeginAttempt();
                session.FinishAttempt(2000, 0.9, i, false);
                session.FinishAttempt(2000, 0.9, i, false);
                var replay = session.CreateReplayScore();
                ((OsuReplayFrame)replay.Replay.Frames[0]).Actions.Clear();
            }
            Assert.Multiple(() =>
            {
                Assert.That(session.AttemptCount, Is.EqualTo(20));
                Assert.That(session.Attempts.Count, Is.EqualTo(20));
                Assert.That(session.Attempts.Last().Misses, Is.EqualTo(19));
                Assert.That(session.UserPlaybackRate.Value, Is.EqualTo(0.75));
                Assert.That(((OsuReplayFrame)session.CreateReplayScore().Replay.Frames[0]).Actions, Is.Not.Empty);
            });
        }

        [Test]
        public void TestRepositionPreservesHistoryAndRate()
        {
            var session = createSession();
            session.BeginAttempt();
            Assert.Throws<InvalidOperationException>(() => session.Reposition(2500));
            session.FinishAttempt(1800, 0.8, 2, false);
            session.Reposition(2500.125);
            Assert.Multiple(() =>
            {
                Assert.That(session.StartTime, Is.EqualTo(2500.125));
                Assert.That(session.RequestedTime, Is.EqualTo(2500.125));
                Assert.That(session.AttemptCount, Is.EqualTo(1));
                Assert.That(session.Attempts.Single().StartTime, Is.EqualTo(1000));
                Assert.That(session.UserPlaybackRate.Value, Is.EqualTo(0.75));
                Assert.That(session.ConsumePauseAfterRestore(), Is.True);
                Assert.That(session.ConsumePauseAfterRestore(), Is.False);
            });
            session.BeginAttempt();
            session.FinishAttempt(3000, 1, 0, false);
            Assert.That(session.Attempts.Last().StartTime, Is.EqualTo(2500.125));
        }

        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        public void TestInvalidRepositionIsRejected(double time)
            => Assert.Throws<ArgumentOutOfRangeException>(() => createSession().Reposition(time));

        [Test]
        public void TestIncompleteReplayIsRejected()
        {
            var score = createScore();
            score.Replay.HasReceivedAllFrames = false;
            Assert.Throws<ArgumentException>(() => createSession(score));
            score.Replay.HasReceivedAllFrames = true;
            score.Replay.Frames.Clear();
            Assert.Throws<ArgumentException>(() => createSession(score));
        }

        [Test]
        public void TestLoaderNullReplayIsRejected()
        {
            Assert.Throws<ArgumentException>(() => new ReplayPlayerLoader(new Score { Replay = null! }));
        }

        [Test]
        public void TestObjectRangeChoiceSurvivesRetryAndIsClearedByTimeSeek()
        {
            var session = new ReplayPracticeSession(createScore(), Array.Empty<Mod>(), -200, -200, 1, 1);
            Assert.That(session.UsesObjectRange, Is.True);
            Assert.That(session.NavigationSelection, Is.EqualTo(new ReplayObjectSelection(1, -200)));
            session.BeginAttempt();
            Assert.That(session.NavigationSelection, Is.Null);
            session.FinishAttempt(1500, null, 0, false);
            Assert.That(session.ObjectStartIndex, Is.EqualTo(1));
            session.Reposition(800, new ReplayObjectSelection(2, 800));
            Assert.That(session.ObjectStartIndex, Is.EqualTo(2));
            session.Reposition(1500);
            Assert.That(session.UsesObjectRange, Is.False);
            Assert.That(session.ObjectStartIndex, Is.Null);
        }

        [Test]
        public void TestFullModConfigurationIsCopied()
        {
            var random = new OsuModRandom { Seed = { Value = 123456 }, AngleSharpness = { Value = 4.2f } };
            var speed = new OsuModDoubleTime { SpeedChange = { Value = 1.2 } };
            var session = new ReplayPracticeSession(createScore(), new Mod[] { random, speed }, 1500, 1000, 1);
            random.Seed.Value = 987654;
            random.AngleSharpness.Value = 9;
            speed.SpeedChange.Value = 1.8;
            Assert.Multiple(() =>
            {
                Assert.That(session.Mods.OfType<OsuModRandom>().Single().Seed.Value, Is.EqualTo(123456));
                Assert.That(session.Mods.OfType<OsuModRandom>().Single().AngleSharpness.Value, Is.EqualTo(4.2f));
                Assert.That(session.Mods.OfType<OsuModDoubleTime>().Single().SpeedChange.Value, Is.EqualTo(1.2));
            });
        }

        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(0)]
        [TestCase(3)]
        public void TestInvalidRateIsRejected(double rate) => Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayPracticeSession(createScore(), Array.Empty<Mod>(), 1500, 1000, rate));

        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(1600)]
        public void TestInvalidStartIsRejected(double start) => Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayPracticeSession(createScore(), Array.Empty<Mod>(), 1500, start, 1));
    }
}
