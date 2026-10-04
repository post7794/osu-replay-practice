// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Localisation;
using osu.Game.Localisation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Game.Configuration;
using osu.Framework.Extensions;
using osu.Framework.Input.Bindings;
using osu.Game.Input.Bindings;
using osu.Game.Database;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Overlays.Settings.Sections.Input;
using osu.Framework.Screens;
using osu.Framework.Testing;
using osu.Game.Beatmaps;
using osu.Game.IO;
using osu.Game.Online.API;
using osu.Game.Online.Solo;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Replays;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu.Objects.Drawables;
using osu.Game.Rulesets.Osu.UI;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.UI;
using osu.Game.Scoring;
using osu.Game.Screens.Play;
using osu.Game.Screens.Play.HUD;
using osu.Game.Screens.Play.PlayerSettings;
using osuTK;
using osuTK.Input;
using Decoder = osu.Game.Beatmaps.Formats.Decoder;

namespace osu.Game.Tests.Visual.Gameplay
{
    public partial class TestSceneReplayPractice : RateAdjustedBeatmapTestScene
    {
        [Resolved]
        private RealmAccess database { get; set; } = null!;

        [Resolved]
        private OsuConfigManager config { get; set; } = null!;

        [Resolved]
        private FrameworkConfigManager frameworkConfig { get; set; } = null!;

        [Resolved]
        private LocalisationManager localisation { get; set; } = null!;

        private ReplayPlayerLoader loader = null!;
        private AutoplayReplayPlayerLoader? autoplayLoader;
        private int autoplayGenerations;
        private Score original = null!;
        private int scoreRequests;
        private int storedScores;
        private string originalFrames = null!;
        private string originalScoreInfo = null!;
        private RuntimeSnapshot viewerBaseline = null!;
        private bool followSlider;
        private Action? presentationAudit;

        protected override bool UseFreshStoragePerRun => true;
        private ReplayPracticePlayer practice => (ReplayPracticePlayer)Stack.CurrentScreen;
        private DrawableOsuRuleset ruleset => (DrawableOsuRuleset)practice.ChildrenOfType<DrawableRuleset>().Single();
        private MasterGameplayClockContainer clock => practice.ChildrenOfType<MasterGameplayClockContainer>().Single();
        private ScoreProcessor scoreProcessor => practice.ChildrenOfType<ScoreProcessor>().Single();
        private HealthProcessor healthProcessor => practice.ChildrenOfType<HealthProcessor>().Single();

        private record RuntimeSnapshot(long Score, int Combo, double Health, float Rotation, string Judgements);

        private static RuntimeSnapshot snapshot(Player player)
        {
            var osu = player.ChildrenOfType<DrawableOsuRuleset>().Single();
            var score = player.ChildrenOfType<ScoreProcessor>().Single();
            return new RuntimeSnapshot(
                score.TotalScore.Value,
                score.Combo.Value,
                player.ChildrenOfType<HealthProcessor>().Single().Health.Value,
                osu.Playfield.HitObjectContainer.AliveObjects.OfType<DrawableSpinner>().FirstOrDefault()?.Result.TotalRotation ?? 0,
                JsonConvert.SerializeObject(osu.Playfield.HitObjectContainer.AliveObjects.SelectMany(o => o.NestedHitObjects.Prepend(o))
                                              .Select(o => new { Time = o.HitObject.StartTime, Type = o.HitObject.GetType().Name, Judgement = o.Result.Type })));
        }

        private static void assertEquivalent(RuntimeSnapshot actual, RuntimeSnapshot expected)
        {
            Assert.Multiple(() =>
            {
                Assert.That(actual.Score, Is.EqualTo(expected.Score));
                Assert.That(actual.Combo, Is.EqualTo(expected.Combo));
                // Continuous health drain and angle accumulation have sub-frame / float rounding differences.
                Assert.That(actual.Health, Is.EqualTo(expected.Health).Within(0.001));
                Assert.That(actual.Rotation, Is.EqualTo(expected.Rotation).Within(0.01));
                Assert.That(actual.Judgements, Is.EqualTo(expected.Judgements));
            });
        }

        protected override void Update()
        {
            base.Update();
            if (followSlider && Stack.CurrentScreen is ReplayPracticePlayer p)
            {
                var slider = p.ChildrenOfType<DrawableOsuRuleset>().Single().Playfield.HitObjectContainer.AliveObjects.OfType<DrawableSlider>().FirstOrDefault();
                if (slider != null)
                    InputManager.MoveMouseTo(slider.Ball.ScreenSpaceDrawQuad.Centre);
            }
        }

        protected override void UpdateAfterChildren()
        {
            base.UpdateAfterChildren();
            presentationAudit?.Invoke();
        }

        private void loadReplay(double time, Mod[]? mods = null, bool legacy = false, bool mismatchedHash = false, bool overlappingSliders = false, bool missedHead = false, bool droppedTail = false,
                                bool? releaseBeforeTailBoundary = null, bool longReplay = false, bool generatedAutoplay = false)
        {
            AddStep("load isolated replay", () =>
            {
                // OsuGame's ScreenStack has no ancestor PopoverContainer. The standard ScreenTestScene
                // supplies one, which previously hid the missing gameplay popup hosts from these tests.
                if (Stack.FindClosestParent<PopoverContainer>() is { } testPopover)
                {
                    var parent = (Container)testPopover.Parent!;
                    testPopover.Remove(Stack, false);
                    parent.Add(Stack);
                }

                Assert.That(Stack.FindClosestParent<PopoverContainer>(), Is.Null);
                followSlider = false;
                presentationAudit = null;
                autoplayLoader = null;
                autoplayGenerations = 0;
                scoreRequests = 0;
                ((DummyAPIAccess)API).HandleRequest = request =>
                {
                    if (request is CreateSoloScoreRequest or SubmitSoloScoreRequest)
                    {
                        scoreRequests++;
                        return true;
                    }
                    return false;
                };
                storedScores = database.Run(r => r.All<ScoreInfo>().Count());
                var rule = new OsuRuleset();
                Ruleset.Value = rule.RulesetInfo;
                // Raw decoded objects are converted into fresh ruleset objects for every Player load.
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
                    osu file format v14

                    [General]
                    Mode:0

                    [Difficulty]
                    HPDrainRate:5
                    CircleSize:5
                    OverallDifficulty:5
                    ApproachRate:5
                    SliderMultiplier:1.4
                    SliderTickRate:1

                    [TimingPoints]
                    0,1000,4,1,0,100,1,0

                    [HitObjects]
                    100,100,0,1,0,0:0:0:0:
                    100,100,1000,2,0,L|300:100,2,200
                    256,192,4000,8,0,6000
                    100,100,7000,1,0,0:0:0:0:
                    """));
                using var reader = new LineBufferedReader(stream);
                var beatmap = Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
                if (longReplay)
                {
                    var content = new StringBuilder("""
                        osu file format v14

                        [General]
                        Mode:0

                        [Difficulty]
                        HPDrainRate:5
                        CircleSize:5
                        OverallDifficulty:5
                        ApproachRate:5
                        SliderMultiplier:1.4
                        SliderTickRate:1

                        [TimingPoints]
                        0,1000,4,1,0,100,1,0

                        [HitObjects]

                        """);
                    // Force catch-up to span multiple rendered updates, rather than allowing
                    // a tiny fixture to conceal the problem by rebuilding in one CPU slice.
                    for (int i = 0; i < 1000; i++)
                        content.AppendLine($"{100 + i % 2 * 200},100,{i * 250},1,0,0:0:0:0:");
                    using var longStream = new MemoryStream(Encoding.UTF8.GetBytes(content.ToString()));
                    using var longReader = new LineBufferedReader(longStream);
                    beatmap = Decoder.GetDecoder<Beatmap>(longReader).Decode(longReader);
                }
                if (overlappingSliders)
                {
                    using var overlapStream = new MemoryStream(Encoding.UTF8.GetBytes("""
                        osu file format v14

                        [General]
                        Mode:0

                        [Difficulty]
                        HPDrainRate:5
                        CircleSize:5
                        OverallDifficulty:5
                        ApproachRate:5
                        SliderMultiplier:1.4
                        SliderTickRate:1

                        [TimingPoints]
                        0,1000,4,1,0,100,1,0

                        [HitObjects]
                        100,100,1000,2,0,L|380:100,2,280
                        100,240,3000,2,0,L|380:240,2,280
                        100,340,5000,2,0,L|380:340,2,280
                        100,100,10000,1,0,0:0:0:0:
                        """));
                    using var overlapReader = new LineBufferedReader(overlapStream);
                    beatmap = Decoder.GetDecoder<Beatmap>(overlapReader).Decode(overlapReader);
                }
                Beatmap.Value = CreateWorkingBeatmap(beatmap);
                if (generatedAutoplay)
                {
                    // Exercise the real generator after Player conversion, not an already-seeded fixture.
                    SelectedMods.Value = mods ?? new Mod[] { new OsuModAutoplay() };
                    LoadScreen(autoplayLoader = new AutoplayReplayPlayerLoader((runtimeBeatmap, runtimeMods) =>
                    {
                        autoplayGenerations++;
                        var generated = new OsuModAutoplay().CreateScoreFromReplayData(runtimeBeatmap, runtimeMods);
                        generated.ScoreInfo.Mods = runtimeMods.ToArray();
                        generated.ScoreInfo.Ruleset = rule.RulesetInfo;
                        generated.ScoreInfo.BeatmapInfo = Beatmap.Value.BeatmapInfo;
                        generated.ScoreInfo.BeatmapHash = Beatmap.Value.BeatmapInfo.Hash;
                        generated.ScoreInfo.IsLegacyScore = legacy;
                        original = ReplayPracticeSession.CloneSource(generated);
                        originalFrames = JsonConvert.SerializeObject(original.Replay.Frames);
                        originalScoreInfo = JsonConvert.SerializeObject(original.ScoreInfo);
                        return generated;
                    }));
                    return;
                }
                var playable = Beatmap.Value.GetPlayableBeatmap(rule.RulesetInfo, mods ?? Array.Empty<Mod>());
                original = new OsuModAutoplay().CreateScoreFromReplayData(playable, mods ?? Array.Empty<Mod>());
                if (missedHead)
                {
                    foreach (var frame in original.Replay.Frames.OfType<OsuReplayFrame>().Where(f => f.Time >= 0 && f.Time <= 1600))
                        frame.Actions.Clear();
                }
                if (droppedTail)
                {
                    double tailTime = playable.HitObjects.OfType<Slider>().First().EndTime;
                    foreach (var frame in original.Replay.Frames.OfType<OsuReplayFrame>().Where(f => f.Time >= tailTime - 100 && f.Time <= tailTime + 100))
                        frame.Actions.Clear();
                }
                if (releaseBeforeTailBoundary is bool releaseEarly)
                {
                    var slider = playable.HitObjects.OfType<Slider>().First();
                    double boundary = slider.TailCircle.StartTime + SliderEventGenerator.TAIL_LENIENCY;
                    double heldTime = Math.Floor(boundary) - 1;
                    double releaseTime = releaseEarly ? Math.Floor(boundary) : Math.Ceiling(boundary) + 1;
                    original.Replay.Frames.RemoveAll(f => f.Time >= boundary - 100 && f.Time <= boundary + 100);
                    original.Replay.Frames.Add(new OsuReplayFrame(heldTime,
                        slider.StackedPositionAt((heldTime - slider.StartTime) / slider.Duration), OsuAction.LeftButton));
                    original.Replay.Frames.Add(new OsuReplayFrame(releaseTime,
                        slider.StackedPositionAt((releaseTime - slider.StartTime) / slider.Duration)));
                    original.Replay.Frames = original.Replay.Frames.OrderBy(f => f.Time).ToList();
                }
                original.ScoreInfo.Mods = mods ?? Array.Empty<Mod>();
                original.ScoreInfo.Ruleset = rule.RulesetInfo;
                original.ScoreInfo.BeatmapInfo = Beatmap.Value.BeatmapInfo;
                original.ScoreInfo.BeatmapHash = mismatchedHash ? "different-beatmap-hash" : Beatmap.Value.BeatmapInfo.Hash;
                original.ScoreInfo.IsLegacyScore = legacy;
                originalFrames = JsonConvert.SerializeObject(original.Replay.Frames);
                originalScoreInfo = JsonConvert.SerializeObject(original.ScoreInfo);
                LoadScreen(loader = new ReplayPlayerLoader(original));
            });
            AddUntilStep("replay ready", () => Stack.CurrentScreen is ReplayPlayer { IsLoaded: true });
            AddStep("pause and locate", () =>
            {
                var replay = (ReplayPlayer)Stack.CurrentScreen;
                replay.ChildrenOfType<MasterGameplayClockContainer>().Single().Stop();
                replay.Seek(time);
            });
            AddUntilStep("replay seek settled", () => ((IReplayPracticeRuleset)((ReplayPlayer)Stack.CurrentScreen).ChildrenOfType<DrawableRuleset>().Single()).IsReplayPracticeReady(time));
            AddStep("capture original runtime state", () => viewerBaseline = snapshot((Player)Stack.CurrentScreen));
        }

        private void takeOver(bool? safe = null)
        {
            AddStep("take over", () =>
            {
                var replay = (ReplayPlayer)Stack.CurrentScreen;
                Assert.That(safe is bool chooseSafe ? replay.StartReplayPractice(chooseSafe) : replay.StartReplayPractice(), Is.True);
            });
            AddUntilStep("scene restored", () => Stack.CurrentScreen is ReplayPracticePlayer { IsLoaded: true } p && p.Session.State == ReplayPracticeState.Preparing);
        }

        private void assertIsolation()
        {
            AddAssert("no score API requests", () => scoreRequests, () => Is.Zero);
            AddAssert("no replay recorder", () => ((Player)Stack.CurrentScreen).ChildrenOfType<DrawableOsuRuleset>().Single().KeyBindingInputManager.Recorder == null);
            AddAssert("no score database writes", () => database.Run(r => r.All<ScoreInfo>().Count()), () => Is.EqualTo(storedScores));
            AddAssert("source frames unchanged", () => JsonConvert.SerializeObject(original.Replay.Frames), () => Is.EqualTo(originalFrames));
            AddAssert("source score unchanged", () => JsonConvert.SerializeObject(original.ScoreInfo), () => Is.EqualTo(originalScoreInfo));
        }

        [TestCase(1500, false)]
        [TestCase(5000, false)]
        [TestCase(1500, true)]
        [TestCase(5000, true)]
        public void TestAutoplayTakeoverRetrySeekAndReturn(double time, bool generated)
        {
            loadReplay(time, new Mod[] { new OsuModAutoplay() }, generatedAutoplay: generated);
            AddAssert("autoplay viewer is identified", () => ((ReplayPlayer)Stack.CurrentScreen).IsAutoplayBaseline);
            AddAssert("autoplay takeover enabled", () => ((ReplayPlayer)Stack.CurrentScreen).ReplayPracticeUnavailableReason, () => Is.Null);
            takeOver();
            AddStep("original autoplay prefix is restored without AT installed", () =>
            {
                Assert.That(practice.Session.IsAutoplayBaseline, Is.True);
                Assert.That(practice.GameplayState.Mods.OfType<ModAutoplay>(), Is.Empty);
                Assert.That(practice.Score.ScoreInfo.Mods.OfType<ModAutoplay>(), Is.Empty);
                assertEquivalent(snapshot(practice), viewerBaseline);
                practice.Session.UserPlaybackRate.Value = 0.1;
            });
            AddUntilStep("only live input active", () => practice.Session.State == ReplayPracticeState.Playing);
            AddAssert("automatic input handler detached", () => ruleset.KeyBindingInputManager.ReplayInputHandler == null && !ruleset.HasReplayLoaded.Value);
            AddStep("freeze manual branch", () => practice.Pause());
            for (int i = 0; i < 2; i++)
            {
                ReplayPracticePlayer previous = null!;
                AddStep("retry from immutable autoplay prefix", () =>
                {
                    previous = practice;
                    practice.Restart(true);
                });
                AddUntilStep("fresh autoplay practice ready", () => Stack.CurrentScreen is ReplayPracticePlayer p && p != previous
                    && p.Session.State == ReplayPracticeState.Preparing);
                AddStep("no manual state survives retry", () => assertEquivalent(snapshot(practice), viewerBaseline));
                AddAssert("rate and manual mods retained", () => clock.UserPlaybackRate.Value == 0.1 && !practice.GameplayState.Mods.OfType<ModAutoplay>().Any());
            }
            AddStep("seek to a new practice prefix", () => practice.SeekTransport(6990));
            AddUntilStep("new baseline ready and paused", () => Stack.CurrentScreen is ReplayPracticePlayer p
                && p.Session.State == ReplayPracticeState.Paused && p.TransportTime == 6990);
            AddAssert("autoplay is still absent after seek", () => !practice.GameplayState.Mods.OfType<ModAutoplay>().Any());
            assertIsolation();
            AddStep("return to autoplay", () => practice.ExitPractice());
            waitForViewerTime(6990);
            AddAssert("return restores AT and replay input", () => ((ReplayPlayer)Stack.CurrentScreen).IsAutoplayBaseline
                && ((Player)Stack.CurrentScreen).GameplayState.Mods.OfType<ModAutoplay>().Count() == 1
                && ((Player)Stack.CurrentScreen).ChildrenOfType<DrawableOsuRuleset>().Single().KeyBindingInputManager.ReplayInputHandler != null);
            AddAssert("baseline is generated only once", () => autoplayGenerations, () => Is.EqualTo(generated ? 1 : 0));
            if (generated)
                AddAssert("generated practice history discarded", () => autoplayLoader!.PracticeSession == null);
            assertIsolation();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TestAutoplayDoesNotHitAfterTakeover(bool prehold)
        {
            loadReplay(6990, generatedAutoplay: true);
            takeOver();
            if (prehold)
                AddStep("prehold on the circle", () =>
                {
                    InputManager.MoveMouseTo(ruleset.Playfield.ToScreenSpace(new Vector2(100, 100)));
                    InputManager.PressKey(Key.Z);
                });
            AddUntilStep("manual practice completes without hitting the circle", () => practice.Session.State == ReplayPracticeState.Completed);
            AddAssert("future circle genuinely missed", () => practice.AttemptMisses, () => Is.EqualTo(1));
            AddAssert("manual accuracy is not the perfect baseline", () => practice.AttemptAccuracy, () => Is.EqualTo(0));
            AddAssert("no automation remains", () => !ruleset.HasReplayLoaded.Value && !practice.GameplayState.Mods.OfType<ModAutoplay>().Any());
            AddAssert("no normal passed or failed state", () => !practice.GameplayState.HasPassed && !practice.GameplayState.HasFailed);
            AddStep("release input", () => InputManager.ReleaseKey(Key.Z));
            assertIsolation();
            ReplayPracticePlayer previous = null!;
            AddStep("retry completed autoplay practice", () =>
            {
                previous = practice;
                practice.Restart(true);
            });
            AddUntilStep("retry ready", () => Stack.CurrentScreen is ReplayPracticePlayer p && p != previous && p.Session.State == ReplayPracticeState.Preparing);
            AddStep("retry recovered the baseline", () => assertEquivalent(snapshot(practice), viewerBaseline));
            AddAssert("baseline not regenerated", () => autoplayGenerations, () => Is.EqualTo(1));
            assertIsolation();
            AddStep("exit directly", () => practice.QuitPractice());
            AddUntilStep("generated loader exited too", () => Stack.CurrentScreen is not (Player or PlayerLoader));
            AddAssert("no API or database write on quit", () => scoreRequests == 0 && database.Run(r => r.All<ScoreInfo>().Count()) == storedScores);
        }

        [Test]
        public void TestAutoplayFreezesGeneratedRandomSeed()
        {
            loadReplay(1500, new Mod[] { new OsuModAutoplay(), new OsuModRandom() }, generatedAutoplay: true);
            int? seed = null;
            string positions = string.Empty;
            static string getPositions(Player player) => JsonConvert.SerializeObject(player.GameplayState.Beatmap.HitObjects.OfType<OsuHitObject>()
                .Select(o => new { o.StartTime, o.Position, o.StackHeight }));
            AddStep("capture actual converted seed and positions", () =>
            {
                var viewer = (ReplayPlayer)Stack.CurrentScreen;
                seed = viewer.GameplayState.Mods.OfType<OsuModRandom>().Single().Seed.Value;
                Assert.That(seed, Is.Not.Null, "The initial Player must assign an actual seed before freezing its baseline.");
                positions = getPositions(viewer);
            });
            takeOver();
            AddAssert("manual practice retains the generated seed", () => practice.GameplayState.Mods.OfType<OsuModRandom>().Single().Seed.Value, () => Is.EqualTo(seed));
            AddAssert("manual practice retains the converted positions", () => getPositions(practice), () => Is.EqualTo(positions));
            AddStep("retry unseeded autoplay source", () => practice.Restart(true));
            AddUntilStep("retry ready", () => Stack.CurrentScreen is ReplayPracticePlayer { Session.State: ReplayPracticeState.Preparing });
            AddAssert("retry did not rerandomise", () => getPositions(practice), () => Is.EqualTo(positions));
            AddStep("return to the same generated autoplay", () => practice.ExitPractice());
            waitForViewerTime(1500);
            AddAssert("return did not rerandomise", () => getPositions((Player)Stack.CurrentScreen), () => Is.EqualTo(positions));
            AddAssert("autoplay generated once", () => autoplayGenerations, () => Is.EqualTo(1));
            takeOver();
            AddAssert("second takeover still retains the same seed", () => practice.GameplayState.Mods.OfType<OsuModRandom>().Single().Seed.Value, () => Is.EqualTo(seed));
            AddAssert("second takeover retains positions", () => getPositions(practice), () => Is.EqualTo(positions));
            assertIsolation();
            AddStep("quit generated practice", () => practice.QuitPractice());
            AddUntilStep("loader exited", () => Stack.CurrentScreen is not (Player or PlayerLoader));
        }

        [Test]
        public void TestViewerSeekDoesNotRenderHistoricalCatchUp()
        {
            loadReplay(0, longReplay: true);
            ReplayPlayer viewer = null!;
            int hiddenFrames = 0;
            double target = 100000.125;
            AddStep("audit every rendered seek frame", () =>
            {
                viewer = (ReplayPlayer)Stack.CurrentScreen;
                presentationAudit = () =>
                {
                    if (Stack.CurrentScreen != viewer)
                        return;
                    var gameplay = viewer.ChildrenOfType<MasterGameplayClockContainer>().Single();
                    var osu = viewer.ChildrenOfType<DrawableOsuRuleset>().Single();
                    if (!osu.IsReplayPracticeReady(target))
                    {
                        hiddenFrames++;
                        Assert.That(gameplay.DrawColourInfo.Colour.AverageColour.SRGB.A, Is.Zero,
                            $"Historical frame {osu.FrameStableClock.CurrentTime} must not be drawn while seeking to {target}");
                    }
                };
                viewer.SeekTransport(target);
            });
            waitForViewerTime(target);
            AddAssert("long catch-up was actually observed", () => hiddenFrames, () => Is.GreaterThan(0));
            AddAssert("target shown immediately when ready", () => viewer.ChildrenOfType<MasterGameplayClockContainer>().Single()
                .DrawColourInfo.Colour.AverageColour.SRGB.A, () => Is.EqualTo(1));
            AddStep("supersede forward seek with backward seek before rendering", () =>
            {
                target = 625.125;
                viewer.SeekTransport(190000.125);
                viewer.SeekTransport(target);
            });
            waitForViewerTime(625.125);
            AddAssert("last requested target visible without fading", () => viewer.ChildrenOfType<MasterGameplayClockContainer>().Single()
                .DrawColourInfo.Colour.AverageColour.SRGB.A, () => Is.EqualTo(1));
            AddStep("stop audit", () => presentationAudit = null);
            assertIsolation();
        }

        [Test]
        public void TestTakeoverRetryAndReturnDoNotRenderHistoricalCatchUp()
        {
            loadReplay(100000.125, longReplay: true);
            int restoringFrames = 0;
            double returnTarget = 100000.125;
            RuntimeSnapshot returnBaseline = null!;
            AddStep("audit reconstruction on every rendered frame", () =>
            {
                presentationAudit = () =>
                {
                    if (Stack.CurrentScreen is ReplayPracticePlayer { Session.State: ReplayPracticeState.Restoring } p)
                    {
                        restoringFrames++;
                        Assert.That(p.ChildrenOfType<MasterGameplayClockContainer>().Single().DrawColourInfo.Colour.AverageColour.SRGB.A,
                            Is.Zero, "Restoring a new attempt must not show historical objects, judgements or HUD");
                    }
                    else if (Stack.CurrentScreen is ReplayPlayer viewer
                             && !viewer.ChildrenOfType<DrawableOsuRuleset>().Single().IsReplayPracticeReady(returnTarget))
                    {
                        restoringFrames++;
                        Assert.That(viewer.ChildrenOfType<MasterGameplayClockContainer>().Single().DrawColourInfo.Colour.AverageColour.SRGB.A,
                            Is.Zero, "Returning to the source replay must not show its reconstruction");
                    }
                };
            });
            takeOver();
            AddAssert("ready scene visible at full opacity", () => clock.DrawColourInfo.Colour.AverageColour.SRGB.A, () => Is.EqualTo(1));
            AddStep("reconstructed takeover state unchanged", () => assertEquivalent(snapshot(practice), viewerBaseline));
            for (int i = 0; i < 2; i++)
            {
                ReplayPracticePlayer previous = null!;
                AddStep("retry into a fresh graph", () =>
                {
                    previous = practice;
                    practice.Restart(true);
                });
                AddUntilStep("retry ready", () => Stack.CurrentScreen is ReplayPracticePlayer p && p != previous && p.Session.State == ReplayPracticeState.Preparing);
                AddAssert("retry appears without fade", () => clock.DrawColourInfo.Colour.AverageColour.SRGB.A, () => Is.EqualTo(1));
                AddStep("retry restores original state", () => assertEquivalent(snapshot(practice), viewerBaseline));
            }
            AddAssert("restoring frames were observed and hidden", () => restoringFrames, () => Is.GreaterThan(0));
            AddStep("relocate practice and reconstruct another source prefix", () =>
            {
                returnTarget = 62000.125;
                practice.SeekTransport(returnTarget);
            });
            AddUntilStep("new practice target committed", () => Stack.CurrentScreen is ReplayPracticePlayer p
                && p.Session.State == ReplayPracticeState.Paused && Math.Abs(p.TransportTime - returnTarget) < 0.001);
            AddAssert("relocated practice target fully opaque", () => clock.DrawColourInfo.Colour.AverageColour.SRGB.A, () => Is.EqualTo(1));
            AddAssert("source prefix judged despite hidden presentation", () => scoreProcessor.Combo.Value, () => Is.EqualTo(249));
            AddStep("capture relocated source baseline", () => returnBaseline = snapshot(practice));
            AddStep("return to original replay", () => practice.ExitPractice());
            waitForViewerTime(62000.125);
            AddAssert("source replay appears fully opaque", () => ((Player)Stack.CurrentScreen).ChildrenOfType<MasterGameplayClockContainer>().Single()
                .DrawColourInfo.Colour.AverageColour.SRGB.A, () => Is.EqualTo(1));
            AddStep("returned source state unchanged", () => assertEquivalent(snapshot((Player)Stack.CurrentScreen), returnBaseline));
            AddStep("stop audit", () => presentationAudit = null);
            assertIsolation();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TestPauseOrResumeDuringReconstructionDoesNotLeaveReplayHidden(bool resume)
        {
            loadReplay(0, longReplay: true);
            ReplayPlayer viewer = null!;
            AddStep("seek then change pause state before catch-up", () =>
            {
                viewer = (ReplayPlayer)Stack.CurrentScreen;
                viewer.SeekTransport(100000.125);
                viewer.ToggleTransportPause();
                if (!resume)
                    viewer.ToggleTransportPause();
                Assert.That(viewer.ReplayPracticeUnavailableReason, Is.Not.Null);
                presentationAudit = () =>
                {
                    if (Stack.CurrentScreen == viewer && viewer.IsRestoringReplay)
                        Assert.That(viewer.ChildrenOfType<MasterGameplayClockContainer>().Single().DrawColourInfo.Colour.AverageColour.SRGB.A,
                            Is.Zero, "Changing pause state must not expose partially restored history");
                };
            });
            AddUntilStep("reconstruction committed", () => !viewer.IsRestoringReplay);
            AddAssert("target not abandoned for an old historical frame", () => viewer.TransportTime, () => Is.GreaterThanOrEqualTo(100000.125));
            AddAssert("requested pause state retained", () => viewer.IsTransportPaused, () => Is.EqualTo(!resume));
            AddAssert("ready frame visible without fade", () => viewer.ChildrenOfType<MasterGameplayClockContainer>().Single()
                .DrawColourInfo.Colour.AverageColour.SRGB.A, () => Is.EqualTo(1));
            AddStep("stop audit and freeze replay", () =>
            {
                presentationAudit = null;
                viewer.BeginTransportSeek();
            });
            assertIsolation();
        }

        [Test]
        public void TestExitRemainsAvailableDuringReplayReconstruction()
        {
            loadReplay(0, longReplay: true);
            AddStep("seek and exit before catch-up completes", () =>
            {
                var viewer = (ReplayPlayer)Stack.CurrentScreen;
                viewer.SeekTransport(100000.125);
                Assert.That(viewer.IsRestoringReplay, Is.True);
                viewer.ExitReplay();
            });
            AddUntilStep("viewer and loader exited", () => Stack.CurrentScreen is not (Player or ReplayPlayerLoader));
            AddAssert("no score API requests", () => scoreRequests, () => Is.Zero);
            AddAssert("no score database writes", () => database.Run(r => r.All<ScoreInfo>().Count()), () => Is.EqualTo(storedScores));
            AddAssert("source frames unchanged", () => JsonConvert.SerializeObject(original.Replay.Frames), () => Is.EqualTo(originalFrames));
            AddAssert("source score unchanged", () => JsonConvert.SerializeObject(original.ScoreInfo), () => Is.EqualTo(originalScoreInfo));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TestShortcutConfigurationWithoutExternalPopoverHost(bool training)
        {
            loadReplay(1500);
            if (training)
                takeOver();

            ReplayKeyBindingButton button = null!;
            ReplayKeyBindingButton.ReplayKeyBindingPopover popover = null!;
            AddStep("show configuration button", () =>
            {
                config.SetValue(OsuSetting.ReplaySettingsOverlay, true);
                if (!training)
                    InputManager.MoveMouseTo(((ReplayPlayer)Stack.CurrentScreen).ScreenSpaceDrawQuad.TopRight);
            });
            if (!training)
                AddUntilStep("replay settings expanded", () => ((ReplayPlayer)Stack.CurrentScreen).ReplayOverlay.Settings.Expanded.Value);
            AddWaitStep("layout settled", 5);
            AddStep("scroll configuration button into view in the exterior panel", () =>
            {
                button = ((Player)Stack.CurrentScreen).ChildrenOfType<ReplayKeyBindingButton>().Single();
                button.FindClosestParent<OsuScrollContainer>()!.ScrollTo(button);
            });
            AddWaitStep("panel scroll settled", 5);
            AddStep("click actual configuration button", () =>
            {
                InputManager.MoveMouseTo(button);
                InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("popup opened inside player", () => ((Player)Stack.CurrentScreen).ChildrenOfType<ReplayKeyBindingButton.ReplayKeyBindingPopover>().Any(p => p.IsLoaded && p.Alpha > 0));
            AddStep("capture local popup", () => popover = ((Player)Stack.CurrentScreen).ChildrenOfType<ReplayKeyBindingButton.ReplayKeyBindingPopover>().Single());
            AddAssert("host belongs to current player", () => button.FindClosestParent<PopoverContainer>()?.FindClosestParent<Player>() == Stack.CurrentScreen);
            AddAssert("all replay bindings available", () => popover.ChildrenOfType<KeyBindingRow>().Count(),
                () => Is.EqualTo(GlobalActionContainer.GetGlobalActionsFor(GlobalActionCategory.Replay).Count()));
            AddStep("move into popup", () => InputManager.MoveMouseTo(popover));
            AddWaitStep("popup remains usable", 5);
            AddAssert("popup remains visible", () => popover.Alpha > 0);
            AddStep("close popup", () => button.HidePopover());
            AddUntilStep("popup closed", () => popover.Alpha == 0);
            AddStep("open configuration again", () => button.TriggerClick());
            AddUntilStep("popup reopened", () => ((Player)Stack.CurrentScreen).ChildrenOfType<ReplayKeyBindingButton.ReplayKeyBindingPopover>().Any(p => p.IsLoaded && p.Alpha > 0));
            AddStep("close again", () => button.HidePopover());
            assertIsolation();
        }

        private void pressShortcut(Key key, bool shift = false)
        {
            InputManager.PressKey(Key.ControlLeft);
            if (shift) InputManager.PressKey(Key.ShiftLeft);
            InputManager.Key(key);
            if (shift) InputManager.ReleaseKey(Key.ShiftLeft);
            InputManager.ReleaseKey(Key.ControlLeft);
        }

        [Test]
        public void TestVisibleCountdownOnTakeoverAndShortcutRetry()
        {
            loadReplay(1500);
            takeOver();
            ReplayPracticePlayer previous = null!;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                foreach (int seconds in new[] { 3, 2, 1 })
                {
                    AddUntilStep($"visible countdown {seconds}", () => practice.ChildrenOfType<ReplayPracticeCountdown>().Single() is { Alpha: 1 } countdown
                        && countdown.SecondsRemaining == seconds && countdown.DrawWidth > 0 && countdown.ScreenSpaceDrawQuad.Centre.X == practice.ScreenSpaceDrawQuad.Centre.X
                        && countdown.ScreenSpaceDrawQuad.Centre.Y < practice.ScreenSpaceDrawQuad.Centre.Y);
                    AddAssert("countdown leaves playfield centre visible", () => practice.ChildrenOfType<ReplayPracticeCountdown>().Single()
                        .ScreenSpaceDrawQuad.BottomLeft.Y < ruleset.Playfield.ScreenSpaceDrawQuad.Centre.Y);
                    AddAssert("gameplay remains frozen", () => clock.CurrentTime, () => Is.EqualTo(1500).Within(0.001));
                }

                AddUntilStep("countdown ends with live input", () => practice.Session.State == ReplayPracticeState.Playing
                    && practice.ChildrenOfType<ReplayPracticeCountdown>().Single() is { Alpha: 0, SecondsRemaining: 0 });

                if (attempt == 0)
                {
                    AddStep("retry via shortcut", () =>
                    {
                        previous = practice;
                        pressShortcut(Key.Enter, shift: true);
                    });
                    AddUntilStep("new player preparing", () => Stack.CurrentScreen is ReplayPracticePlayer p && p != previous && p.Session.State == ReplayPracticeState.Preparing);
                }
            }

            AddStep("return via shortcut", () => pressShortcut(Key.BackSpace));
            AddUntilStep("original replay paused", () => Stack.CurrentScreen is ReplayPlayer { IsLoaded: true } replay
                && replay.ChildrenOfType<MasterGameplayClockContainer>().Single().IsPaused.Value);
            assertIsolation();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TestTakeoverShortcutUsesSafeStartSetting(bool safe)
        {
            loadReplay(1500);
            AddStep("set shared safe-start switch", () => ((ReplayPlayer)Stack.CurrentScreen).ChildrenOfType<ReplayPracticeSettings>().Single()
                .ChildrenOfType<PlayerCheckbox>().Single(c => c.LabelText == ReplayPracticeStrings.SafeStartOption).Current.Value = safe);
            AddUntilStep("start-mode explanation matches switch", () => ((ReplayPlayer)Stack.CurrentScreen).ChildrenOfType<ReplayPracticeSettings>().Single()
                .ChildrenOfType<OsuTextFlowContainer>().Any(t => t.ChildrenOfType<OsuSpriteText>().Any(s => s.Text.ToString().StartsWith(safe ? "Rewind" : "Freeze", StringComparison.Ordinal))));
            AddStep("practice-only shortcuts do nothing in viewer", () =>
            {
                pressShortcut(Key.Enter, shift: true);
                pressShortcut(Key.BackSpace);
            });
            AddAssert("still watching replay", () => Stack.CurrentScreen is ReplayPlayer);
            AddStep("take over via shortcut", () => pressShortcut(Key.Enter));
            AddUntilStep("practice preparing", () => Stack.CurrentScreen is ReplayPracticePlayer p && p.Session.State == ReplayPracticeState.Preparing);
            AddAssert("safe-start choice honoured", () => safe ? practice.Session.StartTime < 1000 : practice.Session.StartTime == 1500);
            AddStep("return during preparation via shortcut", () => pressShortcut(Key.BackSpace));
            AddUntilStep("back in viewer", () => Stack.CurrentScreen is ReplayPlayer { IsLoaded: true });
            AddAssert("safe-start choice retained", () => ((ReplayPlayer)Stack.CurrentScreen).AutomaticallyChooseSafePracticeStart.Value, () => Is.EqualTo(safe));
            assertIsolation();
        }

        [Test]
        public void TestRebindShortcutInReplayWithoutAccidentalTakeover()
        {
            loadReplay(1500);
            ReplayPlayer viewer = null!;
            ReplayKeyBindingButton.ReplayKeyBindingPopover popover = null!;
            KeyBindingRow row = null!;
            AddStep("open replay key configuration", () =>
            {
                viewer = (ReplayPlayer)Stack.CurrentScreen;
                viewer.ChildrenOfType<ReplayKeyBindingButton>().Single().TriggerClick();
            });
            AddUntilStep("configuration ready", () => this.ChildrenOfType<ReplayKeyBindingButton.ReplayKeyBindingPopover>().Any(p => p.IsLoaded));
            AddStep("scroll to takeover binding", () =>
            {
                popover = this.ChildrenOfType<ReplayKeyBindingButton.ReplayKeyBindingPopover>().Single();
                row = popover.ChildrenOfType<KeyBindingRow>().Single(r => (GlobalAction)r.Action == GlobalAction.TakeOverReplay);
                popover.ChildrenOfType<OsuScrollContainer>().Single().ScrollTo(row);
            });
            AddWaitStep("scroll settled", 5);
            AddStep("capture keys", () =>
            {
                InputManager.MoveMouseTo(row.ChildrenOfType<KeyBindingRow.KeyButton>().Single());
                InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("row capturing", () => row.HasFocus);
            AddStep("bind existing takeover shortcut", () => pressShortcut(Key.Enter));
            AddWaitStep("binding settled", 5);
            AddAssert("capture did not take over", () => Stack.CurrentScreen == viewer);
            AddAssert("capture did not resume playback", () => viewer.ChildrenOfType<MasterGameplayClockContainer>().Single().IsPaused.Value);
            AddStep("capture again", () =>
            {
                InputManager.MoveMouseTo(row.ChildrenOfType<KeyBindingRow.KeyButton>().Single());
                InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("row capturing again", () => row.HasFocus);
            AddStep("bind F7", () => InputManager.Key(Key.F7));
            AddUntilStep("new binding saved", () => database.Run(r => r.All<RealmKeyBinding>().Single(b => b.RulesetName == null && b.ActionInt == (int)GlobalAction.TakeOverReplay)
                .KeyCombination.Equals(new KeyCombination(InputKey.F7))));
            AddStep("close configuration", () => popover.HidePopover());
            AddWaitStep("close settled", 5);
            AddStep("old shortcut is inactive", () => pressShortcut(Key.Enter));
            AddAssert("still watching", () => Stack.CurrentScreen == viewer);
            AddStep("new shortcut takes over", () => InputManager.Key(Key.F7));
            AddUntilStep("practice preparing", () => Stack.CurrentScreen is ReplayPracticePlayer p && p.Session.State == ReplayPracticeState.Preparing);
            AddStep("return", () => pressShortcut(Key.BackSpace));
            AddUntilStep("back in viewer", () => Stack.CurrentScreen is ReplayPlayer { IsLoaded: true });
            AddAssert("custom binding retained after returning", () => database.Run(r => r.All<RealmKeyBinding>().Single(b => b.RulesetName == null && b.ActionInt == (int)GlobalAction.TakeOverReplay)
                .KeyCombination.Equals(new KeyCombination(InputKey.F7))));
            AddStep("restore default binding", () => database.Write(r => r.All<RealmKeyBinding>().Single(b => b.RulesetName == null && b.ActionInt == (int)GlobalAction.TakeOverReplay)
                .KeyCombination = new KeyCombination(new[] { InputKey.Control, InputKey.Enter })));
            assertIsolation();
        }

        [TestCase(GlobalAction.RetryReplayPractice)]
        [TestCase(GlobalAction.ReturnToReplay)]
        public void TestPracticeShortcutCaptureDoesNotChangeScreen(GlobalAction action)
        {
            loadReplay(1500);
            takeOver();
            ReplayPracticePlayer current = null!;
            ReplayKeyBindingButton.ReplayKeyBindingPopover popover = null!;
            KeyBindingRow row = null!;
            AddStep("open practice key configuration", () =>
            {
                current = practice;
                practice.ChildrenOfType<ReplayKeyBindingButton>().Single().TriggerClick();
            });
            AddUntilStep("configuration ready", () => this.ChildrenOfType<ReplayKeyBindingButton.ReplayKeyBindingPopover>().Any(p => p.IsLoaded && p.Alpha > 0));
            AddStep("scroll to practice binding", () =>
            {
                popover = this.ChildrenOfType<ReplayKeyBindingButton.ReplayKeyBindingPopover>().Last(p => p.IsLoaded && p.Alpha > 0);
                row = popover.ChildrenOfType<KeyBindingRow>().Single(r => (GlobalAction)r.Action == action);
                popover.ChildrenOfType<OsuScrollContainer>().Single().ScrollTo(row);
            });
            AddWaitStep("scroll settled", 5);
            AddStep("capture keys", () =>
            {
                InputManager.MoveMouseTo(row.ChildrenOfType<KeyBindingRow.KeyButton>().Single());
                InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("row capturing", () => row.HasFocus);
            AddStep("bind existing shortcut", () =>
            {
                if (action == GlobalAction.RetryReplayPractice)
                    pressShortcut(Key.Enter, shift: true);
                else
                    pressShortcut(Key.BackSpace);
            });
            AddWaitStep("binding settled", 5);
            AddAssert("capture did not retry or return", () => Stack.CurrentScreen == current);
            AddStep("capture again", () =>
            {
                InputManager.MoveMouseTo(row.ChildrenOfType<KeyBindingRow.KeyButton>().Single());
                InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("row capturing again", () => row.HasFocus);
            AddStep("bind F11", () => InputManager.Key(Key.F11));
            AddUntilStep("custom binding saved", () => database.Run(r => r.All<RealmKeyBinding>().Single(b => b.RulesetName == null && b.ActionInt == (int)action)
                .KeyCombination.Equals(new KeyCombination(InputKey.F11))));
            AddStep("close configuration", () => popover.HidePopover());
            AddWaitStep("close settled", 5);
            AddStep("use new shortcut", () => InputManager.Key(Key.F11));
            if (action == GlobalAction.RetryReplayPractice)
            {
                AddUntilStep("new instance restored", () => Stack.CurrentScreen is ReplayPracticePlayer p && p != current && p.Session.State == ReplayPracticeState.Preparing);
                AddAssert("same takeover time", () => practice.Session.StartTime, () => Is.EqualTo(1500));
                AddStep("return", () => pressShortcut(Key.BackSpace));
            }
            AddUntilStep("back in replay", () => Stack.CurrentScreen is ReplayPlayer { IsLoaded: true });
            AddStep("restore binding", () => database.Write(r => r.All<RealmKeyBinding>().Single(b => b.RulesetName == null && b.ActionInt == (int)action)
                .KeyCombination = GlobalActionContainer.GetDefaultBindingsFor(GlobalActionCategory.Replay).Single(b => (GlobalAction)b.Action == action).KeyCombination));
            assertIsolation();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TestPreparationPauseFreezesCountdown(bool button)
        {
            loadReplay(1500);
            takeOver();
            double remaining = 0;
            double pausedAt = 0;
            AddStep("pause preparation", () => togglePause(button));
            AddUntilStep("preparation paused", () => practice.Session.State == ReplayPracticeState.Paused);
            AddStep("capture frozen preparation", () =>
            {
                remaining = practice.PreparationRemaining;
                pausedAt = practice.Clock.CurrentTime;
            });
            AddUntilStep("wait beyond entire countdown", () => practice.Clock.CurrentTime - pausedAt > ReplayPracticePlayer.PREPARATION_DURATION + 1000);
            AddAssert("countdown stayed frozen", () => practice.PreparationRemaining, () => Is.EqualTo(remaining));
            AddAssert("no attempt started while paused", () => practice.Session.AttemptCount, () => Is.Zero);
            AddAssert("beatmap still at takeover frame", () => clock.CurrentTime, () => Is.EqualTo(1500));
            AddAssert("no normal pause menu", () => practice.ChildrenOfType<PauseOverlay>().Single().State.Value == Visibility.Hidden);
            AddAssert("no running countdown while paused", () => practice.ChildrenOfType<ReplayPracticeCountdown>().Single().Alpha == 0);
            AddStep("resume preparation", () => togglePause(button));
            AddUntilStep("countdown continues", () => practice.Session.State == ReplayPracticeState.Preparing);
            AddAssert("remaining time preserved", () => practice.PreparationRemaining, () => Is.GreaterThan(remaining - 500));
            AddUntilStep("same first attempt begins", () => practice.Session.State == ReplayPracticeState.Playing);
            AddAssert("only one attempt", () => practice.Session.AttemptCount, () => Is.EqualTo(1));
            assertIsolation();
            AddStep("return", () => practice.ExitPractice());
            AddUntilStep("back in viewer", () => Stack.CurrentScreen is ReplayPlayer { IsLoaded: true });
        }

        private void togglePause(bool button = false)
        {
            if (!button)
            {
                InputManager.Key(Key.Space);
                return;
            }

            InputManager.MoveMouseTo(practice.ChildrenOfType<ReplayPracticeOverlay>().Single().ChildrenOfType<RoundedButton>()
                .Single(b => b.Text.ToString() == (practice.Session.State == ReplayPracticeState.Paused ? "Resume practice" : "Pause practice")));
            InputManager.Click(MouseButton.Left);
        }

        [TestCase(1500)]
        [TestCase(5000)]
        public void TestPracticePauseKeepsAttemptAndResynchronisesInput(double time)
        {
            loadReplay(time);
            takeOver();
            AddStep("keep long object active across pause cycles", () => practice.Session.UserPlaybackRate.Value = 0.1);
            AddUntilStep("attempt running", () => practice.Session.State == ReplayPracticeState.Playing);
            double pausedTime = 0;
            RuntimeSnapshot pausedState = null!;
            for (int i = 0; i < 3; i++)
            {
                AddStep("pause live attempt without cooldown", () => togglePause());
                AddUntilStep("attempt paused", () => practice.Session.State == ReplayPracticeState.Paused);
                AddStep("capture paused state", () =>
                {
                    pausedTime = ruleset.FrameStableClock.CurrentTime;
                    pausedState = snapshot(practice);
                });
                AddStep("position and change held keys while paused", () =>
                {
                    InputManager.ReleaseKey(Key.X);
                    InputManager.MoveMouseTo(ruleset.Playfield.ToScreenSpace(new Vector2(420, 192)));
                    InputManager.PressKey(Key.Z);
                    InputManager.ReleaseKey(Key.Z);
                    InputManager.PressKey(Key.X);
                });
                AddWaitStep("paused live input must not judge", 5);
                AddStep("all judgement state frozen", () => assertEquivalent(snapshot(practice), pausedState));
                AddAssert("music and objects at same frozen time", () => clock.CurrentTime == pausedTime && ruleset.FrameStableClock.CurrentTime == pausedTime);
                AddAssert("attempt not finished or replaced", () => practice.Session.AttemptCount == 1 && practice.Session.Attempts.Count == 0);
                AddAssert("no normal menu or click-to-resume", () => practice.ChildrenOfType<PauseOverlay>().Single().State.Value == Visibility.Hidden && !practice.IsResuming);
                AddStep("resume in place", () => togglePause());
                AddUntilStep("same attempt running", () => practice.Session.State == ReplayPracticeState.Playing);
                AddAssert("only currently-held key transferred", () => ruleset.KeyBindingInputManager.PressedActions.Contains(OsuAction.RightButton)
                    && !ruleset.KeyBindingInputManager.PressedActions.Contains(OsuAction.LeftButton));
                AddStep("release transferred key", () => InputManager.ReleaseKey(Key.X));
                AddAssert("no stuck keys after resume", () => !ruleset.KeyBindingInputManager.PressedActions.Any());
            }
            AddStep("pause via Escape", () => InputManager.Key(Key.Escape));
            AddUntilStep("Escape pauses without exiting", () => practice.Session.State == ReplayPracticeState.Paused);
            ReplayPracticePlayer previous = null!;
            AddStep("retry while paused", () =>
            {
                previous = practice;
                pressShortcut(Key.Enter, shift: true);
            });
            AddUntilStep("new attempt preparing", () => Stack.CurrentScreen is ReplayPracticePlayer p && p != previous && p.Session.State == ReplayPracticeState.Preparing);
            AddAssert("retry uses original takeover time", () => clock.CurrentTime, () => Is.EqualTo(time));
            AddAssert("partial attempt counted once", () => practice.Session.Attempts.Count, () => Is.EqualTo(1));
            AddStep("pause countdown then return", () => togglePause());
            AddUntilStep("preparation paused again", () => practice.Session.State == ReplayPracticeState.Paused);
            assertIsolation();
            AddStep("return while paused", () => pressShortcut(Key.BackSpace));
            AddUntilStep("original replay returned", () => Stack.CurrentScreen is ReplayPlayer replay
                && replay.ChildrenOfType<MasterGameplayClockContainer>().Single().IsPaused.Value
                && ((IReplayPracticeRuleset)replay.ChildrenOfType<DrawableRuleset>().Single()).IsReplayPracticeReady(time));
            AddAssert("history discarded", () => loader.PracticeSession == null);
            assertIsolation();
        }

        [Test]
        public void TestMiddleMouseTogglesPracticePauseOnce()
        {
            loadReplay(1500);
            takeOver();
            AddStep("pause with middle mouse", () => InputManager.Click(MouseButton.Middle));
            AddUntilStep("paused only once", () => practice.Session.State == ReplayPracticeState.Paused);
            AddStep("resume with middle mouse", () => InputManager.Click(MouseButton.Middle));
            AddUntilStep("preparation resumed only once", () => practice.Session.State == ReplayPracticeState.Preparing);
            AddUntilStep("manual attempt running", () => practice.Session.State == ReplayPracticeState.Playing);
            AddStep("pause live input with middle mouse", () => InputManager.Click(MouseButton.Middle));
            AddUntilStep("live attempt paused once", () => practice.Session.State == ReplayPracticeState.Paused);
            assertIsolation();
            AddStep("return", () => practice.ExitPractice());
            AddUntilStep("back in viewer", () => Stack.CurrentScreen is ReplayPlayer { IsLoaded: true });
        }

        [Test]
        public void TestPausePreholdDoesNotJudgeOnResume()
        {
            loadReplay(6990);
            takeOver();
            AddStep("keep circle hittable for pause", () => practice.Session.UserPlaybackRate.Value = 0.05);
            AddUntilStep("manual input running", () => practice.Session.State == ReplayPracticeState.Playing);
            AddStep("pause on hittable circle", () => togglePause());
            AddUntilStep("paused", () => practice.Session.State == ReplayPracticeState.Paused);
            AddStep("prehold over circle", () =>
            {
                InputManager.MoveMouseTo(ruleset.Playfield.ToScreenSpace(new Vector2(100, 100)));
                InputManager.PressKey(Key.Z);
            });
            AddStep("resume without a new hit press", () => togglePause());
            AddUntilStep("completed without hitting circle", () => practice.Session.State == ReplayPracticeState.Completed);
            AddAssert("paused prehold not counted as hit", () => practice.AttemptMisses, () => Is.EqualTo(1));
            AddStep("release", () => InputManager.ReleaseKey(Key.Z));
            assertIsolation();
            AddStep("return", () => practice.ExitPractice());
            AddUntilStep("back in viewer", () => Stack.CurrentScreen is ReplayPlayer { IsLoaded: true });
        }

        [Test]
        public void TestPauseBindingCaptureAndRemappingInPractice()
        {
            loadReplay(1500);
            takeOver();
            KeyBindingRow row = null!;
            ReplayKeyBindingButton.ReplayKeyBindingPopover popover = null!;
            AddStep("open practice shortcut configuration", () => practice.ChildrenOfType<ReplayKeyBindingButton>().Single().TriggerClick());
            AddUntilStep("editor ready", () => practice.ChildrenOfType<ReplayKeyBindingButton.ReplayKeyBindingPopover>().Any(p => p.IsLoaded));
            AddStep("scroll to shared pause binding", () =>
            {
                popover = practice.ChildrenOfType<ReplayKeyBindingButton.ReplayKeyBindingPopover>().Single();
                row = popover.ChildrenOfType<KeyBindingRow>().Single(r => (GlobalAction)r.Action == GlobalAction.TogglePauseReplay);
                popover.ChildrenOfType<OsuScrollContainer>().Single().ScrollTo(row);
            });
            AddWaitStep("scroll settled", 5);
            AddStep("pause before editing", () => Assert.That(practice.Pause(), Is.True));
            AddStep("capture pause key", () =>
            {
                InputManager.MoveMouseTo(row.ChildrenOfType<KeyBindingRow.KeyButton>()
                    .Single(b => b.KeyBinding.Value.KeyCombination.Equals(new KeyCombination(InputKey.Space))));
                InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("row capturing", () => row.HasFocus);
            AddStep("capture existing Space binding", () => InputManager.Key(Key.Space));
            AddWaitStep("capture completed", 5);
            AddAssert("binding capture did not resume training", () => practice.Session.State == ReplayPracticeState.Paused);
            AddStep("capture replacement key", () =>
            {
                InputManager.MoveMouseTo(row.ChildrenOfType<KeyBindingRow.KeyButton>()
                    .Single(b => b.KeyBinding.Value.KeyCombination.Equals(new KeyCombination(InputKey.Space))));
                InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("row capturing again", () => row.HasFocus);
            AddStep("bind F9", () => InputManager.Key(Key.F9));
            AddUntilStep("pause binding persisted", () => database.Run(r => r.All<RealmKeyBinding>().AsEnumerable()
                .Any(b => b.RulesetName == null && b.ActionInt == (int)GlobalAction.TogglePauseReplay && b.KeyCombination.Equals(new KeyCombination(InputKey.F9)))));
            AddStep("close editor", () => popover.HidePopover());
            AddWaitStep("close settled", 5);
            AddStep("old key is inactive", () => InputManager.Key(Key.Space));
            AddAssert("still paused", () => practice.Session.State == ReplayPracticeState.Paused);
            AddStep("custom key resumes", () => InputManager.Key(Key.F9));
            AddUntilStep("preparation resumed", () => practice.Session.State == ReplayPracticeState.Preparing);
            AddUntilStep("manual attempt running", () => practice.Session.State == ReplayPracticeState.Playing);
            AddStep("custom key pauses manual attempt", () => InputManager.Key(Key.F9));
            AddUntilStep("attempt paused", () => practice.Session.State == ReplayPracticeState.Paused);
            assertIsolation();
            AddStep("return", () => practice.ExitPractice());
            AddUntilStep("back in viewer", () => Stack.CurrentScreen is ReplayPlayer { IsLoaded: true });
            // This fixture deliberately shares its input database across cases. Do not leave F9 for later tests.
            AddStep("restore shared pause binding", () => database.Write(r => r.All<RealmKeyBinding>().AsEnumerable()
                .Single(b => b.RulesetName == null && b.ActionInt == (int)GlobalAction.TogglePauseReplay && b.KeyCombination.Equals(new KeyCombination(InputKey.F9)))
                .KeyCombination = new KeyCombination(InputKey.Space)));
            AddWaitStep("pause binding default restored", 5);
        }

        [Test]
        public void TestSpinnerPausePositioningDoesNotAddRotation()
        {
            loadReplay(5000);
            takeOver();
            AddStep("keep spinner active", () => practice.Session.UserPlaybackRate.Value = 0.05);
            AddUntilStep("manual spinner active", () => practice.Session.State == ReplayPracticeState.Playing);
            AddStep("pause spinner", () => togglePause());
            AddUntilStep("spinner paused", () => practice.Session.State == ReplayPracticeState.Paused);
            float rotation = 0;
            int direction = 1;
            Vector2 centre = Vector2.Zero;
            AddStep("reposition at a different angle", () =>
            {
                var spinner = ruleset.Playfield.HitObjectContainer.AliveObjects.OfType<DrawableSpinner>().Single();
                rotation = spinner.Result.TotalRotation;
                direction = Math.Sign(spinner.RotationTracker.Rotation);
                centre = spinner.RotationTracker.Parent!.ScreenSpaceDrawQuad.Centre;
                InputManager.MoveMouseTo(centre + new Vector2(80, 0));
                InputManager.PressKey(Key.Z);
            });
            AddStep("resume spinner", () => togglePause());
            AddUntilStep("manual spinner resumed", () => practice.Session.State == ReplayPracticeState.Playing);
            AddAssert("paused movement was not rotation", () => ruleset.Playfield.HitObjectContainer.AliveObjects.OfType<DrawableSpinner>().Single().Result.TotalRotation,
                () => Is.EqualTo(rotation).Within(0.01));
            AddStep("perform real quarter turn after resume", () => InputManager.MoveMouseTo(centre + new Vector2(0, 80 * direction)));
            AddUntilStep("real movement rotates", () => ruleset.Playfield.HitObjectContainer.AliveObjects.OfType<DrawableSpinner>().Single().Result.TotalRotation > rotation + 50);
            AddAssert("only physical quarter turn counted", () => ruleset.Playfield.HitObjectContainer.AliveObjects.OfType<DrawableSpinner>().Single().Result.TotalRotation,
                () => Is.EqualTo(rotation + 90).Within(0.01));
            AddStep("release", () => InputManager.ReleaseKey(Key.Z));
            assertIsolation();
            AddStep("return", () => practice.ExitPractice());
            AddUntilStep("back in viewer", () => Stack.CurrentScreen is ReplayPlayer { IsLoaded: true });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TestPendingSeekDoesNotCaptureOldState(bool playing)
        {
            loadReplay(1500);
            AddStep("reject takeover before a new seek has executed", () =>
            {
                var viewer = (ReplayPlayer)Stack.CurrentScreen;
                var viewerClock = viewer.ChildrenOfType<MasterGameplayClockContainer>().Single();
                if (playing)
                    viewerClock.Start();
                viewer.Seek(5000.456);
                Assert.Multiple(() =>
                {
                    Assert.That(viewer.ReplayPracticeUnavailableReason?.ToString(), Does.Contain("Waiting for replay seek"));
                    Assert.That(viewer.StartReplayPractice(), Is.False);
                    Assert.That(loader.PracticeSession, Is.Null);
                    Assert.That(viewerClock.IsPaused.Value, Is.EqualTo(!playing));
                });
                viewerClock.Stop();
            });
            AddUntilStep("new seek settled", () => ((IReplayPracticeRuleset)((ReplayPlayer)Stack.CurrentScreen).ChildrenOfType<DrawableRuleset>().Single())
                .IsReplayPracticeReady(5000.456));
            AddStep("capture settled state", () => viewerBaseline = snapshot((Player)Stack.CurrentScreen));
            takeOver();
            AddAssert("accepted exact settled frame", () => practice.Session.RequestedTime == 5000.456 && practice.Session.StartTime == 5000.456);
            AddStep("no stale-state reconstruction", () => assertEquivalent(snapshot(practice), viewerBaseline));
            assertIsolation();
            AddStep("return", () => practice.ExitPractice());
            AddUntilStep("back in viewer", () => Stack.CurrentScreen is ReplayPlayer { IsLoaded: true });
        }

        [TestCase(1500.123, false)]
        [TestCase(1500.123, true)]
        [TestCase(5000.456, false)]
        [TestCase(5000.456, true)]
        public void TestTakeoverWhilePlayingCapturesVisibleFrame(double time, bool shortcut)
        {
            loadReplay(time);
            if (!shortcut)
            {
                AddStep("show takeover button", () =>
                {
                    config.SetValue(OsuSetting.ReplaySettingsOverlay, true);
                    InputManager.MoveMouseTo(((ReplayPlayer)Stack.CurrentScreen).ScreenSpaceDrawQuad.TopRight);
                });
                AddUntilStep("replay settings expanded", () => ((ReplayPlayer)Stack.CurrentScreen).ReplayOverlay.Settings.Expanded.Value);
                AddWaitStep("button layout settled", 5);
                AddStep("position on takeover button", () => InputManager.MoveMouseTo(((ReplayPlayer)Stack.CurrentScreen).ChildrenOfType<ReplayPracticeSettings>().Single()
                    .ChildrenOfType<RoundedButton>().Single(b => b.Text.ToString() == "Take over now")));
                AddWaitStep("hover settled", 2);
            }

            ReplayPlayer viewer = null!;
            double capturedTime = 0;
            AddStep("observe the exact accepted frame", () =>
            {
                viewer = (ReplayPlayer)Stack.CurrentScreen;
                Action<ReplayPracticeSession> request = viewer.PracticeRequested!;
                viewer.PracticeRequested = session =>
                {
                    capturedTime = viewer.ChildrenOfType<DrawableRuleset>().Single().FrameStableClock.CurrentTime;
                    viewerBaseline = snapshot(viewer);
                    Assert.Multiple(() =>
                    {
                        Assert.That(viewer.ChildrenOfType<MasterGameplayClockContainer>().Single().IsPaused.Value, Is.True);
                        Assert.That(session.RequestedTime, Is.EqualTo(capturedTime));
                        Assert.That(session.StartTime, Is.EqualTo(capturedTime));
                    });
                    request(session);
                };
            });
            AddStep("resume original replay", () => viewer.ChildrenOfType<MasterGameplayClockContainer>().Single().Start());
            AddUntilStep("replay advances inside long object", () => viewer.ChildrenOfType<DrawableRuleset>().Single().FrameStableClock.CurrentTime > time + 50);
            AddStep(shortcut ? "take over directly via shortcut" : "take over directly via button", () =>
            {
                Assert.That(viewer.ChildrenOfType<MasterGameplayClockContainer>().Single().IsPaused.Value, Is.False);
                Assert.That(viewer.ReplayPracticeUnavailableReason, Is.Null);
                if (shortcut)
                    pressShortcut(Key.Enter);
                else
                    InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("independent training restored", () => Stack.CurrentScreen is ReplayPracticePlayer p && p.Session.State == ReplayPracticeState.Preparing);
            AddAssert("captured running frame, not original seek time", () => capturedTime, () => Is.GreaterThan(time + 50));
            AddStep("visible frame state reconstructed", () => assertEquivalent(snapshot(practice), viewerBaseline));
            AddWaitStep("preparation remains at captured frame", 3);
            AddAssert("countdown does not rewind or advance", () => clock.CurrentTime, () => Is.EqualTo(capturedTime).Within(0.001));
            ReplayPracticePlayer previous = null!;
            AddStep("retry captured frame", () =>
            {
                previous = practice;
                practice.Restart(true);
            });
            AddUntilStep("retry restored", () => Stack.CurrentScreen is ReplayPracticePlayer p && p != previous && p.Session.State == ReplayPracticeState.Preparing);
            AddAssert("retry at same precise frame", () => clock.CurrentTime, () => Is.EqualTo(capturedTime).Within(0.001));
            AddStep("retry state is identical", () => assertEquivalent(snapshot(practice), viewerBaseline));
            assertIsolation();
            AddStep("return", () => practice.ExitPractice());
            AddUntilStep("original replay paused at captured frame", () => Stack.CurrentScreen is ReplayPlayer replay
                && replay.ChildrenOfType<MasterGameplayClockContainer>().Single().IsPaused.Value
                && ((IReplayPracticeRuleset)replay.ChildrenOfType<DrawableRuleset>().Single()).IsReplayPracticeReady(capturedTime));
            AddStep("returned replay state is identical", () => assertEquivalent(snapshot((Player)Stack.CurrentScreen), viewerBaseline));
            assertIsolation();
        }

        [TestCase(1500)]
        [TestCase(5000)]
        [TestCase(1500.123)]
        [TestCase(5000.456)]
        public void TestMidObjectRetryAndReturn(double time)
        {
            loadReplay(time);
            AddAssert("exact takeover is the default", () => !((ReplayPlayer)Stack.CurrentScreen).AutomaticallyChooseSafePracticeStart.Value);
            takeOver();
            AddAssert("selected frame retained without rounding or rewind", () => practice.Session.RequestedTime == time && practice.Session.StartTime == time);
            AddStep("original replay state recovered", () => assertEquivalent(snapshot(practice), viewerBaseline));
            long baselineScore = 0;
            float baselineRotation = 0;
            int baselineCombo = 0;
            double baselineHealth = 0;
            ReplayPracticePlayer previous = null!;
            AddStep("capture baseline", () =>
            {
                baselineScore = scoreProcessor.TotalScore.Value;
                baselineCombo = scoreProcessor.Combo.Value;
                baselineHealth = healthProcessor.Health.Value;
                baselineRotation = ruleset.Playfield.HitObjectContainer.AliveObjects.OfType<DrawableSpinner>().FirstOrDefault()?.Result.TotalRotation ?? 0;
            });
            AddStep("move and hold while preparing", () =>
            {
                var slider = ruleset.Playfield.HitObjectContainer.AliveObjects.OfType<DrawableSlider>().FirstOrDefault();
                InputManager.MoveMouseTo(slider?.Ball.ScreenSpaceDrawQuad.Centre ?? ruleset.Playfield.ToScreenSpace(new Vector2(420, 192)));
                InputManager.PressKey(Key.Z);
            });
            AddAssert("preparation does not judge", () => scoreProcessor.TotalScore.Value, () => Is.EqualTo(baselineScore));
            AddAssert("preparation does not drain HP", () => healthProcessor.Health.Value, () => Is.EqualTo(baselineHealth).Within(0.000001));
            AddAssert("preparation is frozen", () => ruleset.FrameStableClock.CurrentTime, () => Is.EqualTo(time).Within(0.001));
            AddUntilStep("manual input active", () => practice.Session.State == ReplayPracticeState.Playing);
            AddAssert("replay input detached", () => ruleset.KeyBindingInputManager.ReplayInputHandler == null);
            AddAssert("held physical key transferred", () => ruleset.KeyBindingInputManager.PressedActions.Contains(OsuAction.LeftButton));
            AddStep("release physical key", () => InputManager.ReleaseKey(Key.Z));
            AddAssert("no stuck key", () => !ruleset.KeyBindingInputManager.PressedActions.Contains(OsuAction.LeftButton));

            for (int i = 0; i < 5; i++)
            {
                int iteration = i;
                AddStep("retry independently while physically holding a key", () =>
                {
                    InputManager.PressKey(Key.Z);
                    previous = practice;
                    practice.Session.UserPlaybackRate.Value = 0.75;
                    practice.Restart(true);
                });
                AddUntilStep("new player restored", () => Stack.CurrentScreen is ReplayPracticePlayer p && p != previous && p.Session.State == ReplayPracticeState.Preparing);
                AddAssert("score reset to replay baseline", () => scoreProcessor.TotalScore.Value, () => Is.EqualTo(baselineScore));
                AddAssert("combo reset", () => scoreProcessor.Combo.Value, () => Is.EqualTo(baselineCombo));
                AddAssert("health reset", () => healthProcessor.Health.Value, () => Is.EqualTo(baselineHealth).Within(0.001));
                AddAssert("rotation reset", () => ruleset.Playfield.HitObjectContainer.AliveObjects.OfType<DrawableSpinner>().FirstOrDefault()?.Result.TotalRotation ?? 0, () => Is.EqualTo(baselineRotation).Within(0.01));
                AddAssert("training feedback reset", () => practice.AttemptMisses == 0 && practice.AttemptAccuracy == null);
                AddAssert("speed retained", () => clock.UserPlaybackRate.Value, () => Is.EqualTo(0.75));
                AddAssert("previous attempt recorded once", () => practice.Session.Attempts.Count, () => Is.EqualTo(iteration + 1));
                AddUntilStep("next manual attempt active", () => practice.Session.State == ReplayPracticeState.Playing);
                AddAssert("key held across reload transfers", () => ruleset.KeyBindingInputManager.PressedActions.Contains(OsuAction.LeftButton));
                AddAssert("attempt count advances", () => practice.Session.AttemptCount, () => Is.EqualTo(iteration + 2));
                AddStep("release key after reload", () => InputManager.ReleaseKey(Key.Z));
                AddAssert("key still releases after retry", () => !ruleset.KeyBindingInputManager.PressedActions.Contains(OsuAction.LeftButton));
            }
            assertIsolation();
            AddStep("return during preparation", () => practice.ExitPractice());
            AddUntilStep("replay returned paused", () => Stack.CurrentScreen is ReplayPlayer replay && replay.ChildrenOfType<MasterGameplayClockContainer>().Single().IsPaused.Value
                                                                       && Math.Abs(replay.ChildrenOfType<DrawableRuleset>().Single().FrameStableClock.CurrentTime - time) < 0.001);
            AddAssert("history discarded", () => loader.PracticeSession == null);
            AddAssert("viewing still works", () => ((ReplayPlayer)Stack.CurrentScreen).ReplayPracticeUnavailableReason == null);
            assertIsolation();
        }

        [TestCase("none")]
        [TestCase("SD")]
        [TestCase("PF")]
        public void TestCompletionNeverShowsResultsAndCanReturn(string failMod)
        {
            loadReplay(1500, failMod switch
            {
                "SD" => new Mod[] { new OsuModSuddenDeath { Restart = { Value = true } } },
                "PF" => new Mod[] { new OsuModPerfect() },
                _ => Array.Empty<Mod>(),
            });
            takeOver();
            AddUntilStep("manual input active", () => practice.Session.State == ReplayPracticeState.Playing);
            AddStep("empty health and finish", () =>
            {
                healthProcessor.Health.Value = 0;
                // Advance the manual attempt in this completion test, not the user-facing source seek.
                clock.Seek(9000);
            });
            AddUntilStep("training completed", () => practice.Session.State == ReplayPracticeState.Completed);
            double endedAt = 0;
            AddStep("forward controls at end do not rewind", () =>
            {
                endedAt = practice.TransportTime;
                InputManager.Key(Key.E);
                InputManager.Key(Key.D);
                InputManager.Key(Key.Period);
            });
            AddAssert("completed graph and start retained", () => Stack.CurrentScreen is ReplayPracticePlayer p
                && p.Session.State == ReplayPracticeState.Completed && p.Session.StartTime == 1500 && p.TransportTime == endedAt);

            AddAssert("failure suppressed", () => !practice.GameplayState.HasFailed && !healthProcessor.HasFailed);
            AddAssert("no normal passed state", () => !practice.GameplayState.HasPassed);
            AddAssert("last judgement counted", () => practice.AttemptMisses, () => Is.GreaterThan(0));
            AddAssert("attempt recorded", () => practice.Session.Attempts.Single().Completed);
            Task<ScoreInfo> saveTask = null!;
            AddStep("explicit save path is inert too", () => saveTask = practice.ChildrenOfType<FailOverlay>().Single().SaveReplay!());
            AddUntilStep("explicit save completed", () => saveTask.IsCompletedSuccessfully);
            assertIsolation();
            ReplayPracticePlayer previous = null!;
            AddStep("retry completed practice", () =>
            {
                previous = practice;
                practice.Restart(true);
            });
            AddUntilStep("completed scene replaced", () => Stack.CurrentScreen is ReplayPracticePlayer p && p != previous && p.Session.State == ReplayPracticeState.Preparing);
            AddAssert("completion history retained", () => practice.Session.Attempts.Count, () => Is.EqualTo(1));
            AddStep("completion baseline recovered", () => assertEquivalent(snapshot(practice), viewerBaseline));
            AddStep("exit completed practice", () => practice.ExitPractice());
            AddUntilStep("back in viewer", () => Stack.CurrentScreen is ReplayPlayer { IsLoaded: true });
            assertIsolation();
        }

        [Test]
        public void TestPreparationPressIsNotAHit()
        {
            loadReplay(6990);
            takeOver();
            AddStep("position and prehold on hittable circle", () =>
            {
                InputManager.MoveMouseTo(ruleset.Playfield.ToScreenSpace(new Vector2(100, 100)));
                InputManager.PressKey(Key.Z);
            });
            AddUntilStep("circle passed without new press", () => practice.Session.State == ReplayPracticeState.Completed);
            AddAssert("prehold did not hit circle", () => practice.AttemptMisses, () => Is.EqualTo(1));
            AddAssert("only takeover judgements counted", () => practice.AttemptAccuracy, () => Is.EqualTo(0));
            AddStep("release", () => InputManager.ReleaseKey(Key.Z));
            assertIsolation();
            AddStep("return", () => practice.ExitPractice());
            AddUntilStep("back in viewer", () => Stack.CurrentScreen is ReplayPlayer { IsLoaded: true });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        public void TestMidSliderAcceptsDifferentHeldKey(bool legacy, bool autoplay)
        {
            var mods = legacy ? new Mod[] { new OsuModClassic() } : Array.Empty<Mod>();
            loadReplay(1500, autoplay ? mods.Append(new OsuModAutoplay()).ToArray() : mods, legacy, generatedAutoplay: autoplay);
            takeOver();
            AddStep("legacy and lazer history restored", () => assertEquivalent(snapshot(practice), viewerBaseline));
            var results = new List<JudgementResult>();
            AddStep("follow slider with other key", () =>
            {
                ruleset.NewResult += result =>
                {
                    if (practice.Session.State == ReplayPracticeState.Playing)
                        results.Add(result);
                };
                followSlider = true;
                InputManager.MoveMouseTo(ruleset.Playfield.HitObjectContainer.AliveObjects.OfType<DrawableSlider>().Single().Ball.ScreenSpaceDrawQuad.Centre);
                InputManager.PressKey(Key.X);
            });
            AddUntilStep("manual input active", () => practice.Session.State == ReplayPracticeState.Playing);
            AddUntilStep("slider fully judged", () => results.Any(r => r.HitObject is Slider));
            AddAssert("all future slider judgements hit", () => results.All(r => r.IsHit));
            AddAssert("held other key actually tracked", () => ruleset.KeyBindingInputManager.PressedActions.Contains(OsuAction.RightButton));
            AddStep("stop following", () =>
            {
                followSlider = false;
                InputManager.ReleaseKey(Key.X);
            });
            assertIsolation();
            AddStep("return", () => practice.ExitPractice());
            AddUntilStep("back in viewer", () => Stack.CurrentScreen is ReplayPlayer { IsLoaded: true });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TestMidSpinnerCursorJumpIsNotRotation(bool autoplay)
        {
            loadReplay(5000, generatedAutoplay: autoplay);
            takeOver();
            float originalRotation = 0;
            Vector2 centre = Vector2.Zero;
            int spinDirection = 1;
            AddStep("prehold at a different angle", () =>
            {
                var spinner = ruleset.Playfield.HitObjectContainer.AliveObjects.OfType<DrawableSpinner>().Single();
                originalRotation = spinner.Result.TotalRotation;
                // Continue in the original spin direction; reversing legitimately spends the inherited partial turn.
                spinDirection = Math.Sign(spinner.RotationTracker.Rotation);
                centre = spinner.RotationTracker.Parent!.ScreenSpaceDrawQuad.Centre;
                InputManager.MoveMouseTo(centre + new Vector2(80, 0));
                InputManager.PressKey(Key.Z);
            });
            AddUntilStep("manual input active", () => practice.Session.State == ReplayPracticeState.Playing);
            AddAssert("positioning does not spin", () => ruleset.Playfield.HitObjectContainer.AliveObjects.OfType<DrawableSpinner>().Single().Result.TotalRotation,
                () => Is.EqualTo(originalRotation).Within(0.01));
            AddStep("perform real quarter turn", () => InputManager.MoveMouseTo(centre + new Vector2(0, 80 * spinDirection)));
            AddUntilStep("physical movement rotates", () => ruleset.Playfield.HitObjectContainer.AliveObjects.OfType<DrawableSpinner>().Single().Result.TotalRotation > originalRotation + 50);
            AddAssert("no replay or cursor-jump rotation added", () => ruleset.Playfield.HitObjectContainer.AliveObjects.OfType<DrawableSpinner>().Single().Result.TotalRotation,
                () => Is.EqualTo(originalRotation + 90).Within(0.01));
            AddStep("release", () => InputManager.ReleaseKey(Key.Z));
            assertIsolation();
            AddStep("return", () => practice.ExitPractice());
            AddUntilStep("back in viewer", () => Stack.CurrentScreen is ReplayPlayer { IsLoaded: true });
        }

        [Test]
        public void TestMismatchedBeatmapDisablesOnlyTakeover()
        {
            loadReplay(1500, mismatchedHash: true);
            AddAssert("mismatch explained", () => ((ReplayPlayer)Stack.CurrentScreen).ReplayPracticeUnavailableReason?.ToString(), () => Does.Contain("differs"));
            AddAssert("takeover denied", () => !((ReplayPlayer)Stack.CurrentScreen).StartReplayPractice());
            assertIsolation();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TestConfiguredModsAndViewingSpeedSurviveRetryAndReturn(bool autoplay)
        {
            var mods = new Mod[] { new OsuModRandom { Seed = { Value = 123456 }, AngleSharpness = { Value = 4.2f } }, new OsuModDoubleTime { SpeedChange = { Value = 1.2 } } };
            loadReplay(1500, autoplay ? mods.Append(new OsuModAutoplay()).ToArray() : mods, generatedAutoplay: autoplay);
            AddStep("set viewing speed", () => ((ReplayPlayer)Stack.CurrentScreen).ChildrenOfType<MasterGameplayClockContainer>().Single().UserPlaybackRate.Value = 0.8);
            AddUntilStep("viewing speed seek settled", () =>
            {
                var replay = (ReplayPlayer)Stack.CurrentScreen;
                return ((IReplayPracticeRuleset)replay.ChildrenOfType<DrawableRuleset>().Single()).IsReplayPracticeReady(replay.ChildrenOfType<MasterGameplayClockContainer>().Single().CurrentTime);
            });
            AddStep("capture adjusted viewer baseline", () => viewerBaseline = snapshot((Player)Stack.CurrentScreen));
            takeOver();
            double takeoverTime = 0;
            AddStep("configured runtime restored", () =>
            {
                takeoverTime = practice.Session.StartTime;
                assertEquivalent(snapshot(practice), viewerBaseline);
            });
            ReplayPracticePlayer previous = null!;
            AddStep("retry with another speed", () =>
            {
                previous = practice;
                practice.Session.UserPlaybackRate.Value = 0.6;
                practice.Restart(true);
            });
            AddUntilStep("new player restored", () => Stack.CurrentScreen is ReplayPracticePlayer p && p != previous && p.Session.State == ReplayPracticeState.Preparing);
            AddStep("configured runtime restored again", () => assertEquivalent(snapshot(practice), viewerBaseline));
            AddAssert("custom random seed", () => practice.GameplayState.Mods.OfType<OsuModRandom>().Single().Seed.Value, () => Is.EqualTo(123456));
            AddAssert("custom random setting", () => practice.GameplayState.Mods.OfType<OsuModRandom>().Single().AngleSharpness.Value, () => Is.EqualTo(4.2f));
            AddAssert("custom mod speed", () => practice.GameplayState.Mods.OfType<OsuModDoubleTime>().Single().SpeedChange.Value, () => Is.EqualTo(1.2));
            AddAssert("retry preserves training speed", () => clock.UserPlaybackRate.Value, () => Is.EqualTo(0.6));
            assertIsolation();
            AddStep("return", () => practice.ExitPractice());
            AddUntilStep("viewer state restored", () => Stack.CurrentScreen is ReplayPlayer replay && ((IReplayPracticeRuleset)replay.ChildrenOfType<DrawableRuleset>().Single()).IsReplayPracticeReady(takeoverTime));
            AddAssert("original viewing speed restored", () => ((ReplayPlayer)Stack.CurrentScreen).ChildrenOfType<MasterGameplayClockContainer>().Single().UserPlaybackRate.Value, () => Is.EqualTo(0.8));
            AddStep("original viewer history restored", () => assertEquivalent(snapshot((Player)Stack.CurrentScreen), viewerBaseline));
            AddAssert("original autoplay setting restored", () => ((ReplayPlayer)Stack.CurrentScreen).IsAutoplayBaseline, () => Is.EqualTo(autoplay));
            assertIsolation();
        }

        [Test]
        public void TestSafeStartMovesBackBeforeTheLongObject()
        {
            loadReplay(1500);
            takeOver(true);
            AddAssert("safe start precedes slider", () => practice.Session.StartTime, () => Is.LessThan(1000));
            AddAssert("requested point retained", () => practice.Session.RequestedTime, () => Is.EqualTo(1500));
            AddStep("return", () => practice.ExitPractice());
            AddUntilStep("back in viewer", () => Stack.CurrentScreen is ReplayPlayer { IsLoaded: true });
        }


        [Test]
        public void TestCompactRightTransportAndViewerShortcuts()
        {
            loadReplay(1500);
            AddAssert("playfield has no reserved strip", () => ((Player)Stack.CurrentScreen).ChildrenOfType<MasterGameplayClockContainer>().Single().Padding, () => Is.EqualTo(new MarginPadding()));
            AddAssert("controls compact and outside on the right", () => ((ReplayPlayer)Stack.CurrentScreen).TransportControls.Width <= 220
                && ((ReplayPlayer)Stack.CurrentScreen).PanelLayout.Dock == ReplayPanelDock.Right && panelOutsidePlayfield());
            AddAssert("native progress HUD is visible and interactive", () => ((Player)Stack.CurrentScreen).ChildrenOfType<SongProgress>().Any(p => p.Alpha > 0 && p.Interactive.Value));
            AddStep("Q seeks exactly one second", () => InputManager.Key(Key.Q));
            waitForViewerTime(500);
            AddStep("E seeks exactly one second", () => InputManager.Key(Key.E));
            waitForViewerTime(1500);
            AddStep("A selects previous object", () => InputManager.Key(Key.A));
            waitForViewerTime(-200);
            AddStep("D selects next object", () => InputManager.Key(Key.D));
            waitForViewerTime(2800);
            double frame = 0;
            AddStep("period retains actual frame navigation", () =>
            {
                frame = ReplayTransport.FindFrameTime(original.Replay.Frames.Select(f => f.Time), 2800, 1);
                InputManager.Key(Key.Period);
            });
            AddUntilStep("frame target reached", () => Math.Abs(((ReplayPlayer)Stack.CurrentScreen).TransportTime - frame) < 0.001);
            AddStep("W raises speed", () => InputManager.Key(Key.W));
            AddAssert("speed increased", () => ((ReplayPlayer)Stack.CurrentScreen).TransportRate.Value, () => Is.EqualTo(1.05));
            AddStep("S lowers speed", () => InputManager.Key(Key.S));
            AddAssert("speed decreased", () => ((ReplayPlayer)Stack.CurrentScreen).TransportRate.Value, () => Is.EqualTo(1));
            AddStep("change speed then F resets", () =>
            {
                ((ReplayPlayer)Stack.CurrentScreen).TransportRate.Value = 0.4;
                InputManager.Key(Key.F);
            });
            AddAssert("speed reset", () => ((ReplayPlayer)Stack.CurrentScreen).TransportRate.Value, () => Is.EqualTo(1));
            assertIsolation();
        }

        private void waitForViewerTime(double time) => AddUntilStep($"viewer settled at {time}", () => Stack.CurrentScreen is ReplayPlayer viewer
            && Math.Abs(viewer.TransportTime - time) < 0.001
            && ((IReplayPracticeRuleset)viewer.ChildrenOfType<DrawableRuleset>().Single()).IsReplayPracticeReady(time));

        private void previousFailureShortcut(ReplayFailureKind kind)
        {
            InputManager.PressKey(Key.ShiftLeft);
            InputManager.Key(kind == ReplayFailureKind.Miss ? Key.N : Key.M);
            InputManager.ReleaseKey(Key.ShiftLeft);
        }

        [TestCase(ReplayFailureKind.Miss, false)]
        [TestCase(ReplayFailureKind.Miss, true)]
        [TestCase(ReplayFailureKind.Ignored, false)]
        [TestCase(ReplayFailureKind.Ignored, true)]
        public void TestPreviousSourceFailureNavigationByShortcutAndRealButton(ReplayFailureKind kind, bool inPractice)
        {
            loadReplay(6900, missedHead: kind == ReplayFailureKind.Miss, droppedTail: kind == ReplayFailureKind.Ignored);
            if (inPractice)
                takeOver();
            ReplayFailureIndex index = null!;
            RoundedButton previousButton = null!;
            AddStep("request earlier source failure using Shift shortcut", () =>
            {
                var transport = (IReplayTransport)Stack.CurrentScreen;
                index = transport.FailureIndex;
                transport.TransportRate.Value = 0.75;
                previousFailureShortcut(kind);
            });
            AddUntilStep("source index completed", () => index.IsReady);
            AddUntilStep("analysis graph disposed", () => Stack.CurrentScreen is Player p && !p.ChildrenOfType<ReplayFailureAnalyser>().Any());
            AddUntilStep("previous slider restored at its appearance", () => Stack.CurrentScreen is IReplayTransport transport
                && transport.SelectedObject == new ReplayObjectSelection(1, -200) && Math.Abs(transport.TransportTime + 200) < 0.001
                && (Stack.CurrentScreen is not ReplayPracticePlayer p || p.Session.State == ReplayPracticeState.Paused));
            AddAssert("Shift navigation preserves configured playback rate", () => ((IReplayTransport)Stack.CurrentScreen).TransportRate.Value == 0.75);
            AddAssert("source index remains shared", () => ReferenceEquals(((IReplayTransport)Stack.CurrentScreen).FailureIndex, index));
            if (inPractice)
                AddAssert("previous source range is ready without old attempt statistics", () => practice.Session.ObjectStartIndex == 1
                    && practice.AttemptMisses == 0 && practice.AttemptAccuracy == null);
            AddStep("scroll previous failure button into view", () =>
            {
                var panel = ((Player)Stack.CurrentScreen).ChildrenOfType<ReplayTransportControls>().Single();
                previousButton = panel.ChildrenOfType<RoundedButton>()
                    .Single(b => b.Text == (kind == ReplayFailureKind.Miss ? ReplayPracticeStrings.PreviousMiss : ReplayPracticeStrings.PreviousIgnored));
                previousButton.FindClosestParent<OsuScrollContainer>()!.ScrollTo(previousButton);
            });
            AddWaitStep("panel scroll settled", 5);
            Player beforeButton = null!;
            AddStep("click real previous failure button", () =>
            {
                beforeButton = (Player)Stack.CurrentScreen;
                InputManager.MoveMouseTo(previousButton);
                InputManager.Click(MouseButton.Left);
            });
            if (kind == ReplayFailureKind.Miss)
            {
                AddUntilStep("previous root selected rather than repeating slider sub-misses", () => Stack.CurrentScreen is IReplayTransport transport
                    && transport.SelectedObject == new ReplayObjectSelection(0, -1200) && Math.Abs(transport.TransportTime + 1200) < 0.001
                    && (Stack.CurrentScreen is not ReplayPracticePlayer p || p.Session.State == ReplayPracticeState.Paused));
                if (inPractice)
                {
                    AddAssert("backward navigation reconstructs a fresh player including earlier objects", () => practice != beforeButton
                        && practice.Session.ObjectStartIndex == 0 && ruleset.Playfield.HitObjectContainer.AliveObjects.Any(d => d.HitObject.StartTime == 0));
                }
            }
            else
            {
                AddUntilStep("no earlier ignored result explained", () => ((IReplayTransport)Stack.CurrentScreen).FailureNavigationMessage?.ToString()
                    == ReplayPracticeStrings.NoPreviousIgnored.ToString());
                AddAssert("no earlier ignored result keeps the same player", () => Stack.CurrentScreen == beforeButton);
            }
            Player atBoundary = null!;
            RuntimeSnapshot boundaryState = null!;
            double boundaryTime = 0;
            AddStep("capture boundary and request previous again", () =>
            {
                atBoundary = (Player)Stack.CurrentScreen;
                boundaryState = snapshot(atBoundary);
                boundaryTime = ((IReplayTransport)atBoundary).TransportTime;
                previousFailureShortcut(kind);
            });
            AddUntilStep("no earlier matching failure explained", () => ((IReplayTransport)Stack.CurrentScreen).FailureNavigationMessage?.ToString()
                == (kind == ReplayFailureKind.Miss ? ReplayPracticeStrings.NoPreviousMiss : ReplayPracticeStrings.NoPreviousIgnored).ToString());
            AddAssert("boundary does not loop or rebuild", () => Stack.CurrentScreen == atBoundary
                && ((IReplayTransport)atBoundary).TransportTime == boundaryTime);
            AddStep("boundary preserves rendered judgement state", () => assertEquivalent(snapshot(atBoundary), boundaryState));
            AddAssert("rate remains unchanged after repeated reverse shortcuts", () => ((IReplayTransport)Stack.CurrentScreen).TransportRate.Value == 0.75);
            assertIsolation();
        }

        [TestCase(ReplayFailureKind.Miss)]
        [TestCase(ReplayFailureKind.Ignored)]
        public void TestChangingPendingFailureNavigationToPreviousKeepsTheLastDirection(ReplayFailureKind kind)
        {
            loadReplay(6900, missedHead: kind == ReplayFailureKind.Miss, droppedTail: kind == ReplayFailureKind.Ignored);
            ReplayFailureIndex index = null!;
            AddStep("request next then previous during the same analysis", () =>
            {
                var viewer = (ReplayPlayer)Stack.CurrentScreen;
                index = viewer.FailureIndex;
                viewer.SeekNextFailure(kind);
                Assert.That(index.IsAnalysing, Is.True);
                viewer.SeekPreviousFailure(kind);
            });
            AddUntilStep("source index completed", () => index.IsReady);
            waitForViewerTime(-200);
            AddAssert("last requested backward direction selected the slider", () => ((ReplayPlayer)Stack.CurrentScreen).SelectedObject
                == new ReplayObjectSelection(1, -200));
            AddUntilStep("analysis graph disposed", () => !((Player)Stack.CurrentScreen).ChildrenOfType<ReplayFailureAnalyser>().Any());
            assertIsolation();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TestSourceMissNavigationIsIndependentAndSharedWithPractice(bool doubleTime)
        {
            loadReplay(-300, doubleTime ? new Mod[] { new OsuModDoubleTime() } : null, overlappingSliders: true, missedHead: true);
            ReplayFailureIndex index = null!;
            RuntimeSnapshot before = null!;
            double actualTime = 0;
            int sourceFailures = 0;
            ReplayObjectSelection? nextMiss = null;
            AddStep("request miss by default shortcut", () =>
            {
                var viewer = (ReplayPlayer)Stack.CurrentScreen;
                index = viewer.FailureIndex;
                before = snapshot(viewer);
                actualTime = viewer.TransportTime;
                InputManager.Key(Key.N);
            });
            AddUntilStep("analysis started without moving the viewer", () => index.IsAnalysing
                && ((ReplayPlayer)Stack.CurrentScreen).TransportTime == actualTime);
            AddStep("original scene remains untouched while analysing", () => assertEquivalent(snapshotWithoutAnalysis((Player)Stack.CurrentScreen), before));
            AddUntilStep("source failure index completed", () => index.IsReady);
            AddUntilStep("analysis graph disposed", () => Stack.CurrentScreen is Player p && !p.ChildrenOfType<ReplayFailureAnalyser>().Any());
            waitForViewerTime(-200);
            AddAssert("source head miss belongs to first root", () => index.Failures.Any(f => f.Kind == ReplayFailureKind.Miss && f.ObjectIndex == 0));
            AddStep("remember complete source outcomes", () => sourceFailures = index.Failures.Count);
            AddAssert("root selected for preview and range practice", () => ((ReplayPlayer)Stack.CurrentScreen).SelectedObject?.Index, () => Is.EqualTo(0));
            assertIsolation();
            takeOver();
            AddAssert("training reuses completed source index", () => ReferenceEquals(practice.FailureIndex, index));
            AddStep("navigate from practice using the shared source index", () =>
            {
                nextMiss = index.FindNext(ReplayFailureKind.Miss, practice.TransportTime, practice.SelectedObject?.Index);
                Assert.That(nextMiss, Is.Not.Null);
                InputManager.Key(Key.N);
            });
            AddUntilStep("next source root restored in practice", () => Stack.CurrentScreen is ReplayPracticePlayer p
                && p.Session.State == ReplayPracticeState.Paused && p.SelectedObject == nextMiss);
            AddStep("resume manual attempt", () => practice.TogglePracticePause());
            AddUntilStep("manual training active", () => practice.Session.State == ReplayPracticeState.Playing);
            AddUntilStep("manual head missed", () => practice.AttemptMisses > 0);
            AddStep("pause after new manual miss", () => practice.Pause());
            AddAssert("manual miss did not add source failures", () => index.Failures.Count, () => Is.EqualTo(sourceFailures));
            assertIsolation();
            AddStep("return", () => practice.ExitPractice());
            AddUntilStep("back in viewer", () => Stack.CurrentScreen is ReplayPlayer { IsLoaded: true });
            AddAssert("index retained after returning", () => ReferenceEquals(((ReplayPlayer)Stack.CurrentScreen).FailureIndex, index));
        }

        private static RuntimeSnapshot snapshotWithoutAnalysis(Player player)
        {
            // The off-screen analyser is deliberately a separate Player with its own processors.
            var osu = player.ChildrenOfType<DrawableOsuRuleset>().First();
            var score = player.ChildrenOfType<ScoreProcessor>().First();
            return new RuntimeSnapshot(score.TotalScore.Value, score.Combo.Value,
                player.ChildrenOfType<HealthProcessor>().First().Health.Value,
                osu.Playfield.HitObjectContainer.AliveObjects.OfType<DrawableSpinner>().FirstOrDefault()?.Result.TotalRotation ?? 0,
                JsonConvert.SerializeObject(osu.Playfield.HitObjectContainer.AliveObjects.SelectMany(o => o.NestedHitObjects.Prepend(o))
                                              .Select(o => new { Time = o.HitObject.StartTime, Type = o.HitObject.GetType().Name, Judgement = o.Result.Type })));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TestDroppedSliderTailNavigationUsesActualSourceJudgement(bool inPractice)
        {
            loadReplay(0, droppedTail: true);
            if (inPractice)
                takeOver();
            ReplayFailureIndex index = null!;
            AddStep("request ignored tail by default shortcut", () =>
            {
                index = ((IReplayTransport)Stack.CurrentScreen).FailureIndex;
                InputManager.Key(Key.M);
            });
            AddUntilStep("source analysed", () => index.IsReady);
            AddUntilStep("analysis graph disposed", () => Stack.CurrentScreen is Player p && !p.ChildrenOfType<ReplayFailureAnalyser>().Any());
            AddUntilStep("selected slider appearance reached", () => Stack.CurrentScreen is IReplayTransport transport
                && transport.SelectedObject == new ReplayObjectSelection(1, -200) && Math.Abs(transport.TransportTime + 200) < 0.001
                && (Stack.CurrentScreen is not ReplayPracticePlayer p || p.Session.State == ReplayPracticeState.Paused));
            AddAssert("tail is ignored, not combo-breaking", () => index.Failures.Single().Kind, () => Is.EqualTo(ReplayFailureKind.Ignored));
            AddAssert("tail mapped to slider, not its nested index", () => index.Failures.Single().ObjectIndex, () => Is.EqualTo(1));
            AddStep("click next ignored button with no later match", () => ((Player)Stack.CurrentScreen).ChildrenOfType<ReplayTransportControls>().Single().ChildrenOfType<RoundedButton>()
                .Single(b => b.Text.ToString() == ReplayPracticeStrings.NextIgnored.ToString()).TriggerClick());
            AddUntilStep("no later ignored result explained", () => ((IReplayTransport)Stack.CurrentScreen).FailureNavigationMessage?.ToString() == ReplayPracticeStrings.NoNextIgnored.ToString());
            AddAssert("no match does not move replay", () => ((IReplayTransport)Stack.CurrentScreen).TransportTime, () => Is.EqualTo(-200));
            assertIsolation();
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void TestTailBoundaryBetweenReplayFramesDoesNotInventIgnored(bool inPractice, bool releaseBeforeBoundary)
        {
            loadReplay(0, releaseBeforeTailBoundary: releaseBeforeBoundary);
            if (inPractice)
                takeOver();
            ReplayFailureIndex index = null!;
            AddStep("analyse sub-frame tail window", () =>
            {
                index = ((IReplayTransport)Stack.CurrentScreen).FailureIndex;
                InputManager.Key(Key.M);
            });
            AddUntilStep("analysis ready", () => index.IsReady);
            AddUntilStep("analysis disposed", () => Stack.CurrentScreen is Player p && !p.ChildrenOfType<ReplayFailureAnalyser>().Any()
                && (!inPractice || p is ReplayPracticePlayer { Session.State: ReplayPracticeState.Paused }));
            if (releaseBeforeBoundary)
            {
                AddAssert("genuine early release stays ignored", () => index.Failures.Count,
                    () => Is.EqualTo(1));
                AddAssert("genuine ignored tail belongs to slider", () => index.Failures.Single() is
                    { ObjectIndex: 1, AppearanceTime: -200, Kind: ReplayFailureKind.Ignored });
            }
            else
            {
                AddAssert("held input at tail boundary is not a failure", () => index.Failures, () => Is.Empty);
                AddAssert("no ignored destination invented", () => ((IReplayTransport)Stack.CurrentScreen).SelectedObject, () => Is.Null);
                AddAssert("no ignored result explained", () => ((IReplayTransport)Stack.CurrentScreen).FailureNavigationMessage?.ToString(),
                    () => Is.EqualTo(ReplayPracticeStrings.NoNextIgnored.ToString()));
            }
            assertIsolation();
        }

        [Test]
        public void TestCompletedManualFailuresDoNotBecomeOriginalReplayFailures()
        {
            loadReplay(1500);
            takeOver();
            AddUntilStep("manual attempt naturally completed", () => practice.Session.State == ReplayPracticeState.Completed);
            double completedAt = 0;
            AddStep("request source miss from completed training", () =>
            {
                Assert.That(practice.AttemptMisses, Is.GreaterThan(0));
                completedAt = practice.TransportTime;
                InputManager.Key(Key.N);
            });
            AddUntilStep("source happy replay has no Miss", () => practice.FailureNavigationMessage?.ToString() == ReplayPracticeStrings.NoNextMiss.ToString());
            AddUntilStep("analysis graph disposed", () => !practice.ChildrenOfType<ReplayFailureAnalyser>().Any());
            AddAssert("completed practice was not repositioned", () => practice.Session.State == ReplayPracticeState.Completed && practice.TransportTime == completedAt);
            AddAssert("no manually generated events in source index", () => practice.FailureIndex.Failures, () => Is.Empty);
            AddStep("query ignored from cached source index", () => InputManager.Key(Key.M));
            AddUntilStep("no ignored result either", () => practice.FailureNavigationMessage?.ToString() == ReplayPracticeStrings.NoNextIgnored.ToString());
            AddStep("query previous Miss from the same source index", () => previousFailureShortcut(ReplayFailureKind.Miss));
            AddUntilStep("manual Miss is not an earlier source Miss", () => practice.FailureNavigationMessage?.ToString() == ReplayPracticeStrings.NoPreviousMiss.ToString());
            AddStep("query previous ignored from the same source index", () => previousFailureShortcut(ReplayFailureKind.Ignored));
            AddUntilStep("no earlier ignored result either", () => practice.FailureNavigationMessage?.ToString() == ReplayPracticeStrings.NoPreviousIgnored.ToString());
            AddAssert("previous queries keep completed manual branch unchanged", () => practice.Session.State == ReplayPracticeState.Completed && practice.TransportTime == completedAt);
            assertIsolation();
            AddStep("exit directly", () => practice.QuitPractice());
            AddUntilStep("loader exited", () => Stack.CurrentScreen is not (Player or ReplayPlayerLoader));
            AddAssert("exit did not submit or write", () => scoreRequests == 0 && database.Run(r => r.All<ScoreInfo>().Count()) == storedScores);
        }

        [Test]
        public void TestTransportPanelDragSnapResizeAndSessionPersistence()
        {
            loadReplay(1500);
            ReplayTransportControls panel = null!;
            ReplayPanelLayout layout = null!;
            Vector2 dragStart = Vector2.Zero;
            Vector2 originalStackSize = Vector2.Zero;
            AddStep("use wide viewport with space to float beside the playfield", () =>
            {
                originalStackSize = Stack.Size;
                Stack.Size = originalStackSize * new Vector2(1, 0.5f);
            });
            AddWaitStep("wide viewport layout settled", 3);
            AddStep("capture panel", () =>
            {
                panel = ((ReplayPlayer)Stack.CurrentScreen).TransportControls;
                layout = ((ReplayPlayer)Stack.CurrentScreen).PanelLayout;
            });
            AddAssert("initial panel inside right gutter and outside playfield", () => panel.AllowedBounds.Contains(panel.BoundingBox) && panelOutsidePlayfield());
            AddStep("shrink width with resize grip to make room for horizontal movement", () =>
            {
                dragStart = panel.ResizeHandle.ScreenSpaceDrawQuad.Centre;
                InputManager.MoveMouseTo(dragStart);
                InputManager.PressButton(MouseButton.Left);
            });
            AddStep("drag resize inward", () => InputManager.MoveMouseTo(dragStart - new Vector2(60, 0)));
            AddAssert("inward resize cannot enter playfield", panelOutsidePlayfield);
            AddStep("release resize", () => InputManager.ReleaseButton(MouseButton.Left));
            AddStep("grab header", () =>
            {
                dragStart = panel.DragHandle.ScreenSpaceDrawQuad.Centre;
                InputManager.MoveMouseTo(dragStart);
                InputManager.PressButton(MouseButton.Left);
            });
            AddStep("drag away from right edge", () => InputManager.MoveMouseTo(dragStart - new Vector2(30, 30)));
            AddStep("release header", () => InputManager.ReleaseButton(MouseButton.Left));
            AddAssert("floating position saved", () => layout.Dock == ReplayPanelDock.None && layout.RelativePosition.X < 0.95);
            AddAssert("floating panel remains outside playfield", () => panel.AllowedBounds.Contains(panel.BoundingBox) && panelOutsidePlayfield());
            AddStep("grab header again", () =>
            {
                dragStart = panel.DragHandle.ScreenSpaceDrawQuad.Centre;
                InputManager.MoveMouseTo(dragStart);
                InputManager.PressButton(MouseButton.Left);
            });
            AddStep("drag into the playfield", () => InputManager.MoveMouseTo(dragStart - new Vector2(200, 40)));
            AddAssert("dragging towards objects is clamped outside", panelOutsidePlayfield);
            AddStep("drag past left boundary", () => InputManager.MoveMouseTo(dragStart - new Vector2(2000, 100)));
            AddAssert("switching gutters never draws across playfield", panelOutsidePlayfield);
            AddStep("release at left", () => InputManager.ReleaseButton(MouseButton.Left));
            AddAssert("left snapped and clamped", () => layout.Dock == ReplayPanelDock.Left && panel.AllowedBounds.Contains(panel.BoundingBox) && panelOutsidePlayfield());
            AddStep("grab resize grip to attempt an oversized panel", () =>
            {
                dragStart = panel.ResizeHandle.ScreenSpaceDrawQuad.Centre;
                InputManager.MoveMouseTo(dragStart);
                InputManager.PressButton(MouseButton.Left);
            });
            AddStep("resize towards objects and beyond viewport", () => InputManager.MoveMouseTo(dragStart + new Vector2(1500, 1500)));
            AddAssert("oversized resize does not cover objects or leave gutter", () => panel.AllowedBounds.Contains(panel.BoundingBox) && panelOutsidePlayfield());
            AddStep("release oversized resize", () => InputManager.ReleaseButton(MouseButton.Left));
            AddStep("grab resize grip", () =>
            {
                dragStart = panel.ResizeHandle.ScreenSpaceDrawQuad.Centre;
                InputManager.MoveMouseTo(dragStart);
                InputManager.PressButton(MouseButton.Left);
            });
            AddStep("resize smaller", () =>
            {
                Vector2 delta = panel.Parent!.ToScreenSpace(new Vector2(150, 160)) - panel.Parent.ToScreenSpace(panel.Size);
                InputManager.MoveMouseTo(dragStart + delta);
            });
            AddStep("release resize", () => InputManager.ReleaseButton(MouseButton.Left));
            AddAssert("size changed by real mouse drag", () => panel.Width < 220 && panel.Height < 390);
            AddAssert("small panel remains outside playfield", () => panel.AllowedBounds.Contains(panel.BoundingBox) && panelOutsidePlayfield());
            AddAssert("small buttons have positive size", () => panel.ChildrenOfType<RoundedButton>().All(b => b.DrawWidth > 0 && b.DrawHeight >= 18));
            AddStep("scroll to exit", () =>
            {
                var exit = panel.ChildrenOfType<RoundedButton>().Single(b => b.Text.ToString() == ReplayPracticeStrings.Exit.ToString());
                panel.ChildrenOfType<OsuScrollContainer>().Single().ScrollTo(exit);
            });
            AddWaitStep("scroll settled", 10);
            AddAssert("exit reachable inside resized panel", () => panel.ScreenSpaceDrawQuad.AABBFloat.Contains(panel.ChildrenOfType<RoundedButton>()
                .Single(b => b.Text.ToString() == ReplayPracticeStrings.Exit.ToString()).ScreenSpaceDrawQuad.AABBFloat));
            takeOver();
            AddAssert("layout shared across takeover", () => ReferenceEquals(practice.PanelLayout, layout));
            AddAssert("practice dock retained", () => practice.PanelLayout.Dock, () => Is.EqualTo(ReplayPanelDock.Left));
            AddAssert("practice still outside playfield", panelOutsidePlayfield);
            AddStep("retry", () => practice.Restart(true));
            AddUntilStep("retry restored", () => Stack.CurrentScreen is ReplayPracticePlayer p && p.Session.State == ReplayPracticeState.Preparing);
            AddAssert("retry retained layout", () => ReferenceEquals(practice.PanelLayout, layout) && layout.PreferredSize.X < 220);
            AddAssert("retry still outside playfield", panelOutsidePlayfield);
            AddStep("return to viewer", () => practice.ExitPractice());
            AddUntilStep("viewer restored", () => Stack.CurrentScreen is ReplayPlayer { IsLoaded: true });
            AddAssert("return retained layout", () => ReferenceEquals(((ReplayPlayer)Stack.CurrentScreen).PanelLayout, layout));
            AddStep("get returned panel", () => panel = ((ReplayPlayer)Stack.CurrentScreen).TransportControls);
            Vector2 stackSize = Vector2.Zero;
            AddStep("simulate smaller viewport with oversized preferred panel", () =>
            {
                stackSize = Stack.Size;
                layout.PreferredSize = new Vector2(220, 390);
                Stack.Size = new Vector2(0.25f, 0.25f);
            });
            AddWaitStep("viewport layout settled", 10);
            AddStep("panel automatically fits gutter without covering the playfield", () => Assert.Multiple(() =>
            {
                Assert.That(panel.Width, Is.LessThan(layout.PreferredSize.X), $"Stack {Stack.DrawSize}, allowed {panel.AllowedBounds}");
                Assert.That(panel.Height, Is.LessThan(layout.PreferredSize.Y), $"Stack {Stack.DrawSize}, allowed {panel.AllowedBounds}");
                Assert.That(panel.AllowedBounds.Contains(panel.BoundingBox), Is.True, $"panel {panel.BoundingBox}, allowed {panel.AllowedBounds}");
                Assert.That(panelOutsidePlayfield(), Is.True);
            }));
            AddAssert("narrow gutter reflows buttons vertically", () =>
            {
                var buttons = panel.ChildrenOfType<RoundedButton>().Where(b => b.Text == ReplayPracticeStrings.PreviousObject || b.Text == ReplayPracticeStrings.NextObject).ToArray();
                return Math.Abs(buttons[0].ScreenSpaceDrawQuad.Centre.X - buttons[1].ScreenSpaceDrawQuad.Centre.X) < 0.1f
                    && Math.Abs(buttons[0].ScreenSpaceDrawQuad.Centre.Y - buttons[1].ScreenSpaceDrawQuad.Centre.Y) > 1;
            });
            AddAssert("preferred size not overwritten by automatic fit", () => layout.PreferredSize, () => Is.EqualTo(new Vector2(220, 390)));
            AddStep("restore viewport", () => Stack.Size = stackSize);
            AddWaitStep("large viewport layout settled", 10);
            AddAssert("panel expanded back within gutter", () => Math.Abs(panel.Height - Math.Min(layout.PreferredSize.Y, panel.AllowedBounds.Height - 0.01f)) < 0.01f
                && Math.Abs(panel.Width - Math.Min(layout.PreferredSize.X, panel.AllowedBounds.Width - 0.01f)) < 0.01f
                && panel.AllowedBounds.Contains(panel.BoundingBox) && panelOutsidePlayfield());
            AddStep("restore original viewport", () => Stack.Size = originalStackSize);
            AddAssert("original viewport still leaves playfield unobscured", panelOutsidePlayfield);
            assertIsolation();
        }

        [TestCase(ReplayPanelDock.Left)]
        [TestCase(ReplayPanelDock.Right)]
        public void TestTransportPanelDisappearingGuttersDoNotCoverPlayfieldOrCaptureMouse(ReplayPanelDock dock)
        {
            loadReplay(1500);
            ReplayTransportControls panel = null!;
            OsuPlayfield playfield = null!;
            Vector2 initialPosition = Vector2.Zero;
            Vector2 initialScale = Vector2.One;
            Vector2 initialSize = Vector2.Zero;
            AddStep("capture playfield and set preferred side", () =>
            {
                panel = ((ReplayPlayer)Stack.CurrentScreen).TransportControls;
                playfield = ((ReplayPlayer)Stack.CurrentScreen).ChildrenOfType<DrawableOsuRuleset>().Single().Playfield;
                initialPosition = playfield.Position;
                initialScale = playfield.Scale;
                initialSize = playfield.Size;
                ((ReplayPlayer)Stack.CurrentScreen).PanelLayout.Dock = dock;
            });
            AddAssert("preferred gutter in use without resizing playfield", () => panel.ActiveSide
                == (dock == ReplayPanelDock.Left ? ReplayPanelSide.Left : ReplayPanelSide.Right)
                && panelOutsidePlayfield() && playfield.Size == initialSize && playfield.Scale == initialScale);
            AddStep("move playfield beyond preferred gutter", () =>
            {
                var viewport = panel.Parent!;
                var playfieldParent = playfield.Parent!;
                Vector2 offset = playfieldParent.ToLocalSpace(viewport.ToScreenSpace(new Vector2(viewport.DrawWidth, 0)))
                                 - playfieldParent.ToLocalSpace(viewport.ToScreenSpace(Vector2.Zero));
                playfield.Position = initialPosition + offset * (dock == ReplayPanelDock.Left ? -1 : 1);
            });
            AddAssert("temporarily use other gutter", () => panel.ActiveSide
                == (dock == ReplayPanelDock.Left ? ReplayPanelSide.Right : ReplayPanelSide.Left)
                && panel.Alpha > 0 && panel.AllowedBounds.Contains(panel.BoundingBox) && panelOutsidePlayfield());
            AddAssert("temporary fallback does not rewrite preference", () => ((ReplayPlayer)Stack.CurrentScreen).PanelLayout.Dock == dock);
            AddStep("remove both gutters with an oversized playfield", () =>
            {
                playfield.Position = initialPosition;
                float factor = 1 + panel.Parent!.ScreenSpaceDrawQuad.AABBFloat.Width / playfield.ScreenSpaceDrawQuad.AABBFloat.Width;
                playfield.Scale = initialScale * factor;
            });
            AddAssert("hidden rather than invading playfield", () => panel.Alpha == 0 && panel.Size == Vector2.Zero);
            AddAssert("hidden panel and children do not capture mouse", () => !panel.HandlePositionalInput && !panel.PropagatePositionalInputSubTree);
            AddStep("hidden controls do not disable shortcuts", () => InputManager.Key(Key.Q));
            waitForViewerTime(500);
            AddStep("restore original playfield", () =>
            {
                playfield.Position = initialPosition;
                playfield.Scale = initialScale;
            });
            AddAssert("visible again on preferred side", () => panel.ActiveSide
                == (dock == ReplayPanelDock.Left ? ReplayPanelSide.Left : ReplayPanelSide.Right)
                && panel.Alpha > 0 && panel.PropagatePositionalInputSubTree && panel.AllowedBounds.Contains(panel.BoundingBox) && panelOutsidePlayfield());
            AddAssert("layout has not resized playfield", () => playfield.Size == initialSize && playfield.Scale == initialScale);
            assertIsolation();
        }

        private bool panelOutsidePlayfield()
        {
            if (Stack.CurrentScreen is not Player player)
                return false;
            var panel = player.ChildrenOfType<ReplayTransportControls>().Single();
            var playfield = player.ChildrenOfType<DrawableOsuRuleset>().Single().Playfield;
            var bounds = panel.ScreenSpaceDrawQuad.AABBFloat;
            return panel.Alpha > 0 && !bounds.IntersectsWith(playfield.ScreenSpaceDrawQuad.AABBFloat)
                && !bounds.IntersectsWith(playfield.SkinnableComponentScreenSpaceDrawQuad.AABBFloat);
        }

        [Test]
        public void TestResumingViewerCancelsPendingFailureJump()
        {
            loadReplay(-300, overlappingSliders: true, missedHead: true);
            ReplayFailureIndex index = null!;
            AddStep("request analysis then resume before it loads", () =>
            {
                var viewer = (ReplayPlayer)Stack.CurrentScreen;
                index = viewer.FailureIndex;
                viewer.SeekNextFailure(ReplayFailureKind.Miss);
                Assert.That(index.IsAnalysing, Is.True);
                viewer.ToggleTransportPause();
            });
            AddUntilStep("background cache completed", () => index.IsReady);
            AddUntilStep("analysis graph disposed", () => !((Player)Stack.CurrentScreen).ChildrenOfType<ReplayFailureAnalyser>().Any());
            AddAssert("viewer keeps playing without selecting a failure", () => Stack.CurrentScreen is ReplayPlayer viewer
                && !viewer.IsTransportPaused && viewer.SelectedObject == null && viewer.FailureNavigationMessage == null);
            AddStep("freeze viewer", () => ((ReplayPlayer)Stack.CurrentScreen).BeginTransportSeek());
            assertIsolation();
        }

        [Test]
        public void TestRetryDuringInitialAnalysisDoesNotLeaveBusyOrPartialIndex()
        {
            loadReplay(0, droppedTail: true);
            takeOver();
            ReplayPracticePlayer previous = null!;
            ReplayFailureIndex index = null!;
            AddStep("request analysis and immediately discard this attempt", () =>
            {
                previous = practice;
                index = practice.FailureIndex;
                practice.SeekNextFailure(ReplayFailureKind.Ignored);
                Assert.That(index.IsAnalysing, Is.True);
                practice.Restart(true);
            });
            AddUntilStep("retry restored", () => Stack.CurrentScreen is ReplayPracticePlayer p && p != previous && p.Session.State == ReplayPracticeState.Preparing);
            AddUntilStep("old analysis completed or cancelled", () => !index.IsAnalysing);
            AddStep("request source tail again", () => practice.SeekNextFailure(ReplayFailureKind.Ignored));
            AddUntilStep("fresh or completed index navigates correctly", () => Stack.CurrentScreen is ReplayPracticePlayer p
                && p.Session.State == ReplayPracticeState.Paused && p.SelectedObject == new ReplayObjectSelection(1, -200));
            AddAssert("only full source outcomes cached", () => index.IsReady && !index.IsAnalysing
                && index.Failures.Count == 1 && index.Failures[0].Kind == ReplayFailureKind.Ignored);
            assertIsolation();
        }

        [Test]
        public void TestClassicTailDoesNotInventLostComboIncrement()
        {
            loadReplay(0, new Mod[] { new OsuModClassic() }, legacy: true, droppedTail: true);
            AddStep("request classic lost combo increment", () => InputManager.Key(Key.M));
            AddUntilStep("no classic tail combo increase was forfeited", () => ((ReplayPlayer)Stack.CurrentScreen).FailureNavigationMessage?.ToString()
                == ReplayPracticeStrings.NoNextIgnored.ToString());
            AddUntilStep("analysis graph disposed", () => !((Player)Stack.CurrentScreen).ChildrenOfType<ReplayFailureAnalyser>().Any());
            AddAssert("source mods retained", () => ((ReplayPlayer)Stack.CurrentScreen).GameplayState.Mods.Single() is OsuModClassic);
            AddAssert("tail loss is not mislabelled", () => ((ReplayPlayer)Stack.CurrentScreen).FailureIndex.Failures.All(f => f.Kind != ReplayFailureKind.Ignored));
            assertIsolation();
        }

        [Test]
        public void TestWaitingNavigatorRestartsAnalysisAfterPreviousOwnerCancels()
        {
            loadReplay(0, droppedTail: true);
            takeOver();
            ReplayFailureIndex index = null!;
            AddStep("simulate a disposing previous owner with an outstanding analysis", () =>
            {
                index = practice.FailureIndex;
                Assert.That(index.TryBegin(), Is.True);
                practice.SeekNextFailure(ReplayFailureKind.Ignored);
                index.Cancel();
            });
            AddUntilStep("waiting navigator claims and completes analysis", () => Stack.CurrentScreen is ReplayPracticePlayer p
                && p.Session.State == ReplayPracticeState.Paused && p.SelectedObject == new ReplayObjectSelection(1, -200));
            AddAssert("no busy state or partial results", () => index.IsReady && !index.IsAnalysing
                && index.Failures.Count == 1 && index.Failures[0].Kind == ReplayFailureKind.Ignored);
            assertIsolation();
        }

        [Test]
        public void TestNextObjectAfterResumingPlaybackDoesNotRewind()
        {
            loadReplay(2400, overlappingSliders: true);
            AddStep("resume replay playback", () => ((ReplayPlayer)Stack.CurrentScreen).ToggleTransportPause());
            AddUntilStep("replay is playing inside second slider's preempt", () =>
            {
                var viewer = (ReplayPlayer)Stack.CurrentScreen;
                return !viewer.IsTransportPaused && viewer.TransportTime > 2400 && viewer.TransportTime < 3000;
            });
            double before = 0;
            AddStep("click actual next-object button while playing", () =>
            {
                var viewer = (ReplayPlayer)Stack.CurrentScreen;
                before = viewer.TransportTime;
                InputManager.MoveMouseTo(viewer.TransportControls.ChildrenOfType<RoundedButton>()
                    .Single(b => b.Text == ReplayPracticeStrings.NextObject));
                InputManager.Click(MouseButton.Left);
            });
            waitForViewerTime(3800);
            AddAssert("navigation did not rewind", () => ((ReplayPlayer)Stack.CurrentScreen).TransportTime > before);
            AddAssert("selected third slider, not already-visible second", () => ((ReplayPlayer)Stack.CurrentScreen).SelectedObject?.Index, () => Is.EqualTo(2));
            AddUntilStep("viewer previews the selected head at exact appearance", () => ((ReplayPlayer)Stack.CurrentScreen).ChildrenOfType<DrawableOsuRuleset>().Single()
                .Playfield.HitObjectContainer.AliveObjects.OfType<DrawableSlider>().Single(d => d.HitObject.StartTime == 5000).HeadCircle.CirclePiece.Alpha >= 0.3f);
            AddStep("resume via original playback settings clock path", () => ((ReplayPlayer)Stack.CurrentScreen).ChildrenOfType<MasterGameplayClockContainer>().Single().Start());
            AddUntilStep("selection cleared by playback", () => ((ReplayPlayer)Stack.CurrentScreen).SelectedObject == null && ((ReplayPlayer)Stack.CurrentScreen).TransportTime > 3800);
            AddStep("next appearance after resume", () => InputManager.Key(Key.D));
            waitForViewerTime(8800);
            AddAssert("navigation advances to fourth object", () => ((ReplayPlayer)Stack.CurrentScreen).SelectedObject?.Index, () => Is.EqualTo(3));
            assertIsolation();
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void TestObjectNavigationShowsFrozenTargetAndRestoresItsNormalFade(bool hidden, bool manual)
        {
            loadReplay(1600, hidden ? new Mod[] { new OsuModHidden() } : null, overlappingSliders: true);
            AddStep("select second slider", () => InputManager.Key(Key.D));
            waitForViewerTime(1800);
            takeOver();
            if (manual)
                AddUntilStep("navigate from active manual training", () => practice.Session.State == ReplayPracticeState.Playing && practice.TransportTime < 3000);
            ReplayPracticePlayer previous = null!;
            foreach (var direction in new[] { 1, -1, -1, 1 })
            {
                int selected = 0;
                double targetTime = 0;
                AddStep($"navigate object {direction}", () =>
                {
                    previous = practice;
                    var selection = ReplayTransport.FindObject(practice.GameplayState.Beatmap.HitObjects, practice.TransportTime,
                        direction, practice.SelectedObject?.Index)!;
                    selected = selection.Index;
                    targetTime = selection.Time;
                    InputManager.MoveMouseTo(practice.ChildrenOfType<ReplayTransportControls>().Single().ChildrenOfType<RoundedButton>()
                        .Single(b => b.Text == (direction > 0 ? ReplayPracticeStrings.NextObject : ReplayPracticeStrings.PreviousObject)));
                    InputManager.Click(MouseButton.Left);
                });
                AddUntilStep("new graph paused at exact appearance", () => Stack.CurrentScreen is ReplayPracticePlayer p && p != previous
                    && p.Session.State == ReplayPracticeState.Paused && Math.Abs(p.TransportTime - targetTime) < 0.001);
                AddUntilStep("target head is visibly present, not only cursor", () =>
                {
                    var slider = ruleset.Playfield.HitObjectContainer.AliveObjects.OfType<DrawableSlider>()
                        .Single(d => d.HitObject == practice.GameplayState.Beatmap.HitObjects[selected]);
                    return slider.Alpha > 0 && slider.HeadCircle.Alpha > 0 && slider.HeadCircle.CirclePiece.Alpha >= 0.3f
                        && (hidden || slider.HeadCircle.ApproachCircle.Alpha >= 0.3f);
                });
                AddAssert("navigation does not use gameplay entry scale animation", () => practice.Scale, () => Is.EqualTo(Vector2.One));
                AddAssert("target stays at appearance during paused preview", () => practice.TransportTime, () => Is.EqualTo(targetTime).Within(0.001));
                AddAssert("preview did not create results", () => scoreProcessor.JudgedHits == 0 && practice.AttemptMisses == 0);
                if (hidden)
                    AddAssert("preview does not reveal hidden approach circle", () => ruleset.Playfield.HitObjectContainer.AliveObjects.OfType<DrawableSlider>()
                        .Single(d => d.HitObject == practice.GameplayState.Beatmap.HitObjects[selected]).HeadCircle.ApproachCircle.Alpha, () => Is.Zero);
            }
            AddStep("resume countdown", () => practice.TogglePracticePause());
            AddUntilStep("real input begins at frozen source time", () =>
            {
                if (practice.Session.State != ReplayPracticeState.Playing)
                    return false;
                clock.Stop();
                return true;
            });
            AddStep("normal fade restored before gameplay advances", () =>
            {
                var slider = ruleset.Playfield.HitObjectContainer.AliveObjects.OfType<DrawableSlider>()
                    .Single(d => d.HitObject == practice.GameplayState.Beatmap.HitObjects[practice.Session.ObjectStartIndex!.Value]);
                // Hidden adjusts the nested head's fade-in, but deliberately leaves the slider root unchanged.
                var head = slider.HeadCircle.HitObject;
                double expected = Math.Clamp((practice.TransportTime - ReplayTransport.ObjectAppearanceTime(head)) / head.TimeFadeIn, 0, 1);
                Assert.That(slider.HeadCircle.CirclePiece.Alpha, Is.EqualTo(expected).Within(0.001), $"Native fade at {practice.TransportTime}");
            });
            assertIsolation();
        }

        [Test]
        public void TestMissedSliderHeadCanBeSelectedAtNegativeAppearance()
        {
            loadReplay(1600, overlappingSliders: true, missedHead: true);
            AddAssert("source slider head really missed", () => ((ReplayPlayer)Stack.CurrentScreen).ChildrenOfType<DrawableOsuRuleset>().Single()
                .Playfield.HitObjectContainer.AliveObjects.OfType<DrawableSlider>().First().HeadCircle.Result.Type, () => Is.EqualTo(HitResult.Miss));
            AddStep("A picks missed first slider, not second slider's earlier appearance", () => InputManager.Key(Key.A));
            waitForViewerTime(-200);
            AddAssert("first slider identity retained", () => ((ReplayPlayer)Stack.CurrentScreen).SelectedObject?.Index, () => Is.Zero);
            AddStep("D picks second slider", () => InputManager.Key(Key.D));
            waitForViewerTime(1800);
            AddAssert("second slider identity retained", () => ((ReplayPlayer)Stack.CurrentScreen).SelectedObject?.Index, () => Is.EqualTo(1));
            AddStep("A returns to first slider", () => InputManager.Key(Key.A));
            waitForViewerTime(-200);
            takeOver();
            AddAssert("negative appearance preserved through takeover", () => practice.Session.StartTime, () => Is.EqualTo(-200));
            assertIsolation();
            AddStep("return to negative appearance", () => practice.ExitPractice());
            waitForViewerTime(-200);
            assertIsolation();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TestObjectRangeRemovesOverlappingPrefixAndCompletesAfterRetry(bool classic)
        {
            loadReplay(1600, classic ? new Mod[] { new OsuModClassic() } : null, legacy: classic, overlappingSliders: true, missedHead: true);
            AddStep("select second slider", () => InputManager.Key(Key.D));
            waitForViewerTime(1800);
            AddStep("capture original overlap scene", () => viewerBaseline = snapshot((Player)Stack.CurrentScreen));
            AddAssert("first slider still active in replay", () => ((ReplayPlayer)Stack.CurrentScreen).ChildrenOfType<DrawableOsuRuleset>().Single()
                .Playfield.HitObjectContainer.AliveObjects.OfType<DrawableSlider>().Any(d => d.HitObject.StartTime == 1000));
            takeOver();
            var results = new List<JudgementResult>();
            HashSet<HitObject> excluded = null!;
            AddStep("record only manual branch results", () =>
            {
                excluded = new HashSet<HitObject>();
                include(practice.GameplayState.Beatmap.HitObjects[0]);
                ruleset.NewResult += result => results.Add(result);
                practice.Session.UserPlaybackRate.Value = 2;

                void include(HitObject obj)
                {
                    excluded.Add(obj);
                    foreach (var nested in obj.NestedHitObjects)
                        include(nested);
                }
            });
            AddUntilStep("previous slider and nested drawables removed", () => ruleset.Playfield.HitObjectContainer.AliveObjects.All(d => d.HitObject.StartTime >= 3000));
            AddAssert("explicit range mode", () => practice.Session.UsesObjectRange && practice.Session.ObjectStartIndex == 1);
            AddAssert("no fabricated perfect score", () => scoreProcessor.TotalScore.Value == 0 && scoreProcessor.JudgedHits == 0 && scoreProcessor.Combo.Value == 0);
            AddAssert("new range starts with full health", () => healthProcessor.Health.Value, () => Is.EqualTo(1));
            AddAssert("no inherited head miss in range", () => scoreProcessor.Statistics.GetValueOrDefault(HitResult.Miss), () => Is.Zero);
            AddUntilStep("manual branch begins", () => practice.Session.State == ReplayPracticeState.Playing);
            AddStep("complete range without physical input", () => clock.Seek(11000));
            AddUntilStep("range naturally completes", () => practice.Session.State == ReplayPracticeState.Completed);
            AddAssert("removed slider produces no later judgements", () => results.All(r => !excluded.Contains(r.HitObject)));
            AddAssert("remaining objects still miss without real input", () => results.Any(r => r.Type == HitResult.Miss));
            AddAssert("attempt misses count only manual range", () => practice.AttemptMisses, () => Is.EqualTo(results.Count(r => r.Type == HitResult.Miss)));
            assertIsolation();
            for (int i = 0; i < 2; i++)
            {
                ReplayPracticePlayer previous = null!;
                AddStep("retry object range", () =>
                {
                    previous = practice;
                    practice.Restart(true);
                });
                AddUntilStep("fresh graph ready", () => Stack.CurrentScreen is ReplayPracticePlayer p && p != previous && p.Session.State == ReplayPracticeState.Preparing);
                AddAssert("range choice survives retry", () => practice.Session.ObjectStartIndex == 1 && practice.Session.StartTime == 1800 && practice.Session.UserPlaybackRate.Value == 2);
                AddAssert("no prefix or score leftovers", () => ruleset.Playfield.HitObjectContainer.AliveObjects.All(d => d.HitObject.StartTime >= 3000)
                    && scoreProcessor.TotalScore.Value == 0 && practice.AttemptMisses == 0);
            }
            AddStep("return to untouched replay", () => practice.ExitPractice());
            waitForViewerTime(1800);
            AddStep("original overlap and missed head restored", () => assertEquivalent(snapshot((Player)Stack.CurrentScreen), viewerBaseline));
            assertIsolation();
        }

        [Test]
        public void TestObjectRangeCanBeDisabledAndArbitrarySeekClearsSelection()
        {
            loadReplay(1600, overlappingSliders: true);
            AddStep("disable range cleanup and select second slider", () =>
            {
                ((ReplayPlayer)Stack.CurrentScreen).IgnorePreviousObjectsDuringObjectPractice.Value = false;
                InputManager.Key(Key.D);
            });
            waitForViewerTime(1800);
            AddStep("capture exact overlap", () => viewerBaseline = snapshot((Player)Stack.CurrentScreen));
            takeOver();
            AddAssert("exact object start retains previous slider", () => !practice.Session.UsesObjectRange && ruleset.Playfield.HitObjectContainer.AliveObjects.Any(d => d.HitObject.StartTime == 1000));
            AddStep("exact mode retains original scene", () => assertEquivalent(snapshot(practice), viewerBaseline));
            AddStep("seek arbitrary time", () => practice.SeekTransport(2000));
            AddUntilStep("arbitrary-time branch ready", () => Stack.CurrentScreen is ReplayPracticePlayer p && p.Session.State == ReplayPracticeState.Paused && p.Session.StartTime == 2000);
            AddAssert("arbitrary seek has no selected-object cutoff", () => practice.Session.ObjectStartIndex == null && practice.SelectedObject == null);
            assertIsolation();
        }

        [Test]
        public void TestPracticeSeekRestoresSourceAndRetainsHistory()
        {
            loadReplay(5000);
            AddStep("move viewer to original takeover point", () => ((ReplayPlayer)Stack.CurrentScreen).SeekTransport(1500));
            waitForViewerTime(1500);
            takeOver();
            AddStep("slow training", () => practice.Session.UserPlaybackRate.Value = 0.5);
            AddUntilStep("training has changed slider state", () => practice.Session.State == ReplayPracticeState.Playing && clock.CurrentTime > 2700);
            ReplayPracticePlayer previous = null!;
            AddStep("seek within live training", () =>
            {
                previous = practice;
                Assert.That(practice.SeekTransport(5000), Is.True);
            });
            AddUntilStep("new independent player paused at target", () => Stack.CurrentScreen is ReplayPracticePlayer p && p != previous
                && p.Session.State == ReplayPracticeState.Paused && p.Session.StartTime == 5000);
            AddStep("source spinner state restored", () => assertEquivalent(snapshot(practice), viewerBaseline));
            AddAssert("previous attempt retained", () => practice.Session.Attempts.Count, () => Is.EqualTo(1));
            AddAssert("old attempt has old start", () => practice.Session.Attempts[0].StartTime, () => Is.EqualTo(1500));
            AddAssert("no attempt counted by seeking", () => practice.Session.AttemptCount, () => Is.EqualTo(1));
            AddAssert("new attempt stats clear", () => practice.AttemptMisses == 0 && practice.AttemptAccuracy == null);
            AddAssert("speed preserved", () => practice.Session.UserPlaybackRate.Value, () => Is.EqualTo(0.5));
            AddAssert("countdown remains ready", () => practice.PreparationRemaining, () => Is.EqualTo(3000));
            AddStep("Q in training", () => InputManager.Key(Key.Q));
            AddUntilStep("seek paused at 4000", () => Stack.CurrentScreen is ReplayPracticePlayer p && p.Session.StartTime == 4000 && p.Session.State == ReplayPracticeState.Paused);
            AddStep("A selects earlier object", () => InputManager.Key(Key.A));
            AddUntilStep("object navigation paused at appearance (2800)", () => Stack.CurrentScreen is ReplayPracticePlayer p && p.Session.StartTime == 2800 && p.Session.State == ReplayPracticeState.Paused);
            AddStep("retry new takeover point", () =>
            {
                previous = practice;
                practice.Restart(true);
            });
            AddUntilStep("retry counts down at new point", () => Stack.CurrentScreen is ReplayPracticePlayer p && p != previous
                && p.Session.StartTime == 2800 && p.Session.State == ReplayPracticeState.Preparing);
            AddAssert("speed survives retry", () => practice.Session.UserPlaybackRate.Value, () => Is.EqualTo(0.5));
            assertIsolation();
            AddStep("return from relocated practice", () => practice.ExitPractice());
            waitForViewerTime(2800);
            AddAssert("history discarded on return", () => loader.PracticeSession == null);
            assertIsolation();
        }

        [TestCase(false, ReplayPracticeState.Preparing)]
        [TestCase(true, ReplayPracticeState.Preparing)]
        [TestCase(true, ReplayPracticeState.Playing)]
        [TestCase(true, ReplayPracticeState.Completed)]
        public void TestNativeProgressDrag(bool training, ReplayPracticeState phase)
        {
            loadReplay(phase == ReplayPracticeState.Completed ? 6900 : 1500);
            if (training)
            {
                takeOver();
                if (phase != ReplayPracticeState.Preparing)
                    AddUntilStep("drag phase ready", () => practice.Session.State == phase);
            }
            double pausedTime = 0;
            int attempts = 0;
            Player before = null!;
            SongProgressBar bar = null!;
            AddStep("press timeline", () =>
            {
                before = (Player)Stack.CurrentScreen;
                bar = before.ChildrenOfType<SongProgressBar>().Single(b => b.IsPresent);
                InputManager.MoveMouseTo(bar.ToScreenSpace(new Vector2(bar.DrawWidth * 0.2f, bar.DrawHeight / 2)));
                InputManager.PressButton(MouseButton.Left);
                pausedTime = ((IReplayTransport)before).TransportTime;
                attempts = training ? practice.Session.AttemptCount : 0;
            });
            AddStep("drag timeline to preview", () => InputManager.MoveMouseTo(bar.ToScreenSpace(new Vector2(bar.DrawWidth * 0.7f, bar.DrawHeight / 2))));
            AddWaitStep("hold preview", 5);
            AddAssert("drag does not restart player", () => Stack.CurrentScreen == before);
            AddAssert("timeline previews new time", () => bar.IsSeeking && Math.Abs(bar.DisplayedTime - 4900) < 1);
            if (training)
                AddAssert("judgement clock stays frozen", () => Math.Abs(((IReplayTransport)before).TransportTime - pausedTime) < 0.001);
            else
                AddUntilStep("viewer seeks live before release", () => Math.Abs(((IReplayTransport)before).TransportTime - 4900) < 1);
            AddStep("release once to commit", () => InputManager.ReleaseButton(MouseButton.Left));
            if (training)
            {
                AddUntilStep("new graph restored once", () => Stack.CurrentScreen is ReplayPracticePlayer p && p != before
                    && p.Session.State == ReplayPracticeState.Paused && Math.Abs(p.Session.StartTime - 4900) < 1);
                AddAssert("drag does not count attempts", () => practice.Session.AttemptCount, () => Is.EqualTo(attempts));
                AddStep("continue preparation", () => InputManager.Key(Key.Space));
                AddUntilStep("preparation resumes", () => practice.Session.State == ReplayPracticeState.Preparing);
                AddStep("return", () => practice.ExitPractice());
                AddUntilStep("back in viewer", () => Stack.CurrentScreen is ReplayPlayer { IsLoaded: true });
            }
            else
                AddUntilStep("viewer paused at chosen time", () => Stack.CurrentScreen == before
                    && ((IReplayTransport)before).IsTransportPaused && Math.Abs(((IReplayTransport)before).TransportTime - 4900) < 1);
            assertIsolation();
        }

        [TestCase(ReplayPracticeState.Preparing, false)]
        [TestCase(ReplayPracticeState.Paused, true)]
        [TestCase(ReplayPracticeState.Playing, false)]
        [TestCase(ReplayPracticeState.Completed, true)]
        public void TestDirectPracticeExitWithoutReturning(ReplayPracticeState phase, bool shortcut)
        {
            loadReplay(phase == ReplayPracticeState.Completed ? 6900 : 1500);
            takeOver();
            if (phase == ReplayPracticeState.Paused)
                AddStep("pause preparation", () => practice.TogglePracticePause());
            if (phase is ReplayPracticeState.Playing or ReplayPracticeState.Completed)
                AddUntilStep("requested exit phase reached", () => practice.Session.State == phase);
            ReplayPracticePlayer exiting = null!;
            RoundedButton exitButton = null!;
            if (!shortcut)
            {
                AddStep("scroll direct exit button into view", () =>
                {
                    exitButton = practice.ChildrenOfType<ReplayTransportControls>().Single().ChildrenOfType<RoundedButton>()
                        .Single(b => b.Text == ReplayPracticeStrings.Exit);
                    exitButton.FindClosestParent<OsuScrollContainer>()!.ScrollTo(exitButton);
                });
                AddWaitStep("panel scroll settled", 5);
            }
            AddStep("exit directly", () =>
            {
                exiting = practice;
                if (shortcut)
                    pressShortcut(Key.BackSpace, true);
                else
                {
                    InputManager.MoveMouseTo(exitButton);
                    InputManager.Click(MouseButton.Left);
                }
            });
            AddUntilStep("left player and replay loader", () => Stack.CurrentScreen is not (Player or PlayerLoader));
            AddAssert("practice session discarded", () => loader.PracticeSession == null);
            AddAssert("no score API requests", () => scoreRequests, () => Is.Zero);
            AddAssert("no score saved", () => database.Run(r => r.All<ScoreInfo>().Count()), () => Is.EqualTo(storedScores));
            AddAssert("exit never installed recorder", () => exiting.ChildrenOfType<DrawableOsuRuleset>().Single().KeyBindingInputManager.Recorder == null);
            AddAssert("source frames untouched", () => JsonConvert.SerializeObject(original.Replay.Frames), () => Is.EqualTo(originalFrames));
            AddAssert("source score untouched", () => JsonConvert.SerializeObject(original.ScoreInfo), () => Is.EqualTo(originalScoreInfo));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void TestBothNativeProgressBarStyles(bool training, bool argon)
        {
            loadReplay(1500);
            if (training)
                takeOver();
            Player before = null!;
            SongProgressBar bar = null!;
            AddStep("add native skin bar", () =>
            {
                before = (Player)Stack.CurrentScreen;
                bar = argon ? new ArgonSongProgressBar(10) : new DefaultSongProgressBar(5, 25, new Vector2(10, 18));
                bar.RelativeSizeAxes = Axes.None;
                bar.Width = 400;
                bar.Y = 180;
                bar.StartTime = 0;
                bar.EndTime = 7000;
                bar.Interactive = true;
                before.ChildrenOfType<MasterGameplayClockContainer>().Single().Add(bar);
            });
            AddUntilStep("native bar loaded", () => bar.IsLoaded);
            AddStep("press native bar", () =>
            {
                InputManager.MoveMouseTo(bar.ToScreenSpace(new Vector2(bar.DrawWidth * 0.25f, bar.DrawHeight / 2)));
                InputManager.PressButton(MouseButton.Left);
            });
            AddStep("drag native bar", () => InputManager.MoveMouseTo(bar.ToScreenSpace(new Vector2(bar.DrawWidth * 0.75f, bar.DrawHeight / 2))));
            AddAssert("target preview updates directly", () => bar.IsSeeking && Math.Abs(bar.DisplayedTime - 5250) < 1);
            AddAssert("native fill has no seek tween", () =>
            {
                var fill = bar.ChildrenOfType<Container>().Single(c => c.Name == (argon ? "Audio bar" : "HandleBar container"));
                return argon
                    ? Math.Abs(fill.Children.Single().Width - bar.DrawWidth * 0.75) < 0.1
                    : Math.Abs(fill.X - bar.DrawWidth * 0.75) < 0.1;
            });
            AddAssert("drag has not rebuilt player", () => Stack.CurrentScreen == before);
            if (!training)
                AddUntilStep("viewer already at target", () => Math.Abs(((IReplayTransport)before).TransportTime - 5250) < 1);
            AddStep("release native bar", () => InputManager.ReleaseButton(MouseButton.Left));
            if (training)
                AddUntilStep("practice restored once", () => Stack.CurrentScreen is ReplayPracticePlayer p && p != before
                    && p.Session.State == ReplayPracticeState.Paused && Math.Abs(p.Session.StartTime - 5250) < 1);
            else
                waitForViewerTime(5250);
            assertIsolation();
        }

        [Test]
        public void TestLanguageSwitchInsideReplayAndPractice()
        {
            loadReplay(1500);
            string previousLocale = string.Empty;
            AddStep("switch to simplified Chinese", () =>
            {
                previousLocale = frameworkConfig.Get<string>(FrameworkSetting.Locale);
                frameworkConfig.SetValue(FrameworkSetting.Locale, "zh");
            });
            AddUntilStep("takeover button translated", () => ((ReplayPlayer)Stack.CurrentScreen).TransportControls.ChildrenOfType<RoundedButton>()
                .Any(b => localisation.GetLocalisedString(b.Text) == "立即接管"));
            AddAssert("official shortcut description translated", () => localisation.GetLocalisedString(GlobalActionKeyBindingStrings.NextReplayObject),
                () => Is.EqualTo("回放 / 练习：下一物件（出现时刻）"));
            takeOver();
            AddAssert("playfield size unchanged in practice", () => clock.Padding, () => Is.EqualTo(new MarginPadding()));
            AddAssert("native progress remains interactive after takeover", () => practice.ChildrenOfType<SongProgress>().Any(p => p.Alpha > 0 && p.Interactive.Value));
            AddAssert("countdown translated", () => practice.ChildrenOfType<ReplayPracticeCountdown>().Single().ChildrenOfType<OsuSpriteText>()
                .Any(t => localisation.GetLocalisedString(t.Text) == "准备接管"));
            AddAssert("statistics translated", () => practice.ChildrenOfType<ReplayTransportControls>().Single().ChildrenOfType<OsuSpriteText>()
                .Any(t => localisation.GetLocalisedString(t.Text).StartsWith("准确率：", StringComparison.Ordinal)));
            AddStep("switch live to English", () => frameworkConfig.SetValue(FrameworkSetting.Locale, "en"));
            AddUntilStep("retry button returns to English", () => practice.ChildrenOfType<ReplayTransportControls>().Single().ChildrenOfType<RoundedButton>()
                .Any(b => localisation.GetLocalisedString(b.Text) == "Retry"));
            AddAssert("countdown returns to English", () => practice.ChildrenOfType<ReplayPracticeCountdown>().Single().ChildrenOfType<OsuSpriteText>()
                .Any(t => localisation.GetLocalisedString(t.Text) == "GET READY"));
            AddStep("restore user's language", () => frameworkConfig.SetValue(FrameworkSetting.Locale, previousLocale));
            assertIsolation();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TestObjectAppearanceUsesModAdjustedPreempt(bool hardRock)
        {
            loadReplay(1500, hardRock ? new Mod[] { new OsuModHardRock() } : Array.Empty<Mod>());
            double appearance = 0;
            AddStep("next object", () =>
            {
                var viewer = (ReplayPlayer)Stack.CurrentScreen;
                var spinner = viewer.GameplayState.Beatmap.HitObjects.OfType<Spinner>().Single();
                appearance = spinner.StartTime - spinner.TimePreempt;
                Assert.That(appearance, Is.EqualTo(hardRock ? 3100 : 2800));
                InputManager.Key(Key.D);
            });
            AddUntilStep("at beginning of mod-adjusted appearance", () => Stack.CurrentScreen is ReplayPlayer viewer
                && Math.Abs(viewer.TransportTime - appearance) < 0.001);
            assertIsolation();
        }

        [Test]
        public void TestAutomaticReplayOnlyDisablesTakeover()
        {
            loadReplay(1500, new Mod[] { new OsuModSpunOut() });
            AddAssert("specific reason", () => ((ReplayPlayer)Stack.CurrentScreen).ReplayPracticeUnavailableReason?.ToString(), () => Does.Contain("Spun Out"));
            AddAssert("cannot take over", () => !((ReplayPlayer)Stack.CurrentScreen).StartReplayPractice());
            AddUntilStep("panel explains blocked automation", () => ((ReplayPlayer)Stack.CurrentScreen).TransportControls.ChildrenOfType<OsuSpriteText>()
                .Any(t => t.Text.ToString().Contains("Spun Out", StringComparison.Ordinal)));
            AddAssert("panel takeover disabled", () => !((ReplayPlayer)Stack.CurrentScreen).TransportControls.ChildrenOfType<RoundedButton>()
                .Single(b => b.Text.ToString() == "Take over now").Enabled.Value);
            AddStep("blocked takeover shortcut", () => pressShortcut(Key.Enter));
            AddAssert("shortcut still leaves viewer paused", () => ((ReplayPlayer)Stack.CurrentScreen).ChildrenOfType<MasterGameplayClockContainer>().Single().IsPaused.Value);
            AddStep("ordinary frame stepping", () => ((ReplayPlayer)Stack.CurrentScreen).StepFrame(1));
            AddAssert("still watching", () => Stack.CurrentScreen is ReplayPlayer);
            assertIsolation();
        }
    }
}
