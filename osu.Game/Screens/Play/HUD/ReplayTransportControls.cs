// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osu.Framework.Platform;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Input.Bindings;
using osu.Game.Localisation;
using osu.Game.Rulesets.UI;
using osu.Game.Screens.Play.PlayerSettings;
using osuTK;
using osuTK.Input;

namespace osu.Game.Screens.Play.HUD
{
    /// <summary>
    /// Draggable controls constrained to the viewport's side gutters, never covering or resizing the playfield.
    /// </summary>
    public partial class ReplayTransportControls : CompositeDrawable
    {
        private readonly Player player;
        [Resolved]
        private DrawableRuleset ruleset { get; set; } = null!;
        private readonly IReplayTransport transport;
        private readonly OsuSpriteText status;
        private readonly OsuSpriteText phase;
        private readonly OsuSpriteText position;
        private readonly OsuSpriteText statistics;
        private readonly OsuSpriteText start;
        private readonly OsuSpriteText history;
        private readonly RoundedButton pause;
        private readonly RoundedButton primary;
        private readonly RoundedButton[] navigation;
        private readonly HoldFocusButton focus;
        private readonly TransportKeyBindingButton shortcuts;
        private readonly List<ButtonRow> rows = new List<ButtonRow>();
        private readonly List<TransportButton> buttons = new List<TransportButton>();
        public PanelHandle DragHandle { get; }
        public PanelHandle ResizeHandle { get; }
        public RectangleF AllowedBounds { get; private set; }
        public ReplayPanelSide ActiveSide { get; private set; } = ReplayPanelSide.Right;
        private RectangleF leftBounds;
        private RectangleF rightBounds;
        private float playfieldCentre;
        public override bool HandlePositionalInput => Alpha > 0 && base.HandlePositionalInput;
        public override bool PropagatePositionalInputSubTree => Alpha > 0 && base.PropagatePositionalInputSubTree;

        public ReplayTransportControls(Player player)
        {
            this.player = player;
            transport = (IReplayTransport)player;
            Anchor = Anchor.TopLeft;
            Origin = Anchor.TopLeft;
            Size = transport.PanelLayout.PreferredSize;
            Masking = true;
            CornerRadius = 5;
            Depth = float.MinValue;
            AlwaysPresent = true;

            pause = button(ReplayPracticeStrings.PausePractice, () => transport.ToggleTransportPause());
            primary = button(player is ReplayPracticePlayer ? ReplayPracticeStrings.Retry : ReplayPracticeStrings.TakeOver, () =>
            {
                if (player is ReplayPracticePlayer practice)
                    practice.Restart(true);
                else
                    ((ReplayPlayer)player).StartReplayPractice();
            });
            navigation = new[]
            {
                button(ReplayPracticeStrings.PreviousObject, () => transport.StepTransportObject(-1)),
                button(ReplayPracticeStrings.NextObject, () => transport.StepTransportObject(1)),
                button(ReplayPracticeStrings.BackwardSecond, () => ReplayTransport.SeekRelative(transport, -1000)),
                button(ReplayPracticeStrings.ForwardSecond, () => ReplayTransport.SeekRelative(transport, 1000)),
                button(ReplayPracticeStrings.PreviousFrame, () => transport.StepTransportFrame(-1)),
                button(ReplayPracticeStrings.NextFrame, () => transport.StepTransportFrame(1)),
                button(ReplayPracticeStrings.PreviousMiss, () => transport.SeekPreviousFailure(ReplayFailureKind.Miss)),
                button(ReplayPracticeStrings.NextMiss, () => transport.SeekNextFailure(ReplayFailureKind.Miss)),
                button(ReplayPracticeStrings.PreviousIgnored, () => transport.SeekPreviousFailure(ReplayFailureKind.Ignored)),
                button(ReplayPracticeStrings.NextIgnored, () => transport.SeekNextFailure(ReplayFailureKind.Ignored)),
            };
            focus = new HoldFocusButton
            {
                Text = ReplayPracticeStrings.HoldObjectFocus,
                TooltipText = ReplayPracticeStrings.HoldObjectFocusHelp,
                RelativeSizeAxes = Axes.Both,
                Size = Vector2.One,
            };
            buttons.Add(focus);
            navigation[6].TooltipText = ReplayPracticeStrings.PreviousMissHelp;
            navigation[7].TooltipText = ReplayPracticeStrings.NextMissHelp;
            navigation[8].TooltipText = ReplayPracticeStrings.PreviousIgnoredHelp;
            navigation[9].TooltipText = ReplayPracticeStrings.NextIgnoredHelp;
            shortcuts = new TransportKeyBindingButton
            {
                Text = ReplayPracticeStrings.Shortcuts,
                TooltipText = ReplayPracticeStrings.ConfigureShortcuts,
                RelativeSizeAxes = Axes.Both,
                Size = Vector2.One,
            };
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = Colour4.Black, Alpha = 0.78f },
                DragHandle = new PanelHandle(this, false)
                {
                    Depth = -1,
                    RelativeSizeAxes = Axes.X,
                    Height = 24,
                    Child = new TruncatingSpriteText
                    {
                        RelativeSizeAxes = Axes.X,
                        Text = ReplayPracticeStrings.DragPanel,
                        Font = OsuFont.GetFont(size: 11),
                        Margin = new MarginPadding { Left = 8, Top = 5 },
                    },
                },
                new OsuScrollContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Top = 24, Bottom = 16 },
                    Child = new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Vertical,
                        Padding = new MarginPadding(8),
                        Spacing = new Vector2(0, 3),
                        Children = new Drawable[]
                        {
                            status = text(13),
                            phase = text(12),
                            position = text(12),
                            row(pause, primary),
                            row(navigation.Take(2).Cast<Drawable>().ToArray()),
                            row(focus),
                            row(navigation.Skip(2).Take(2).Cast<Drawable>().ToArray()),
                            row(navigation.Skip(4).Take(2).Cast<Drawable>().ToArray()),
                            row(navigation.Skip(6).Take(2).Cast<Drawable>().ToArray()),
                            row(navigation.Skip(8).Cast<Drawable>().ToArray()),
                            row(button(ReplayPracticeStrings.Slower, () => ReplayTransport.HandleAction(transport, GlobalAction.DecreaseReplayPlaybackSpeed)),
                                button(ReplayPracticeStrings.ResetSpeed, () => ReplayTransport.HandleAction(transport, GlobalAction.ResetReplayPlaybackSpeed)),
                                button(ReplayPracticeStrings.Faster, () => ReplayTransport.HandleAction(transport, GlobalAction.IncreaseReplayPlaybackSpeed))),
                            row(player is ReplayPracticePlayer p
                                    ? button(ReplayPracticeStrings.Return, p.ExitPractice)
                                    : button(ReplayPracticeStrings.Settings, () => ((ReplayPlayer)player).ReplayOverlay.Settings.Expanded.Toggle()),
                                shortcuts),
                            row(button(ReplayPracticeStrings.Exit, () =>
                            {
                                if (player is ReplayPracticePlayer practice)
                                    practice.QuitPractice();
                                else
                                    ((ReplayPlayer)player).ExitReplay();
                            })),
                            statistics = text(12),
                            start = text(11),
                            history = text(11),
                        },
                    },
                },
                ResizeHandle = new PanelHandle(this, true)
                {
                    Depth = -2,
                    Anchor = Anchor.BottomRight,
                    Origin = Anchor.BottomRight,
                    Size = new Vector2(18),
                    Child = new OsuSpriteText { Text = "◢", Font = OsuFont.GetFont(size: 15) },
                },
            };
        }

        private static OsuSpriteText text(float size) => new TruncatingSpriteText { Font = OsuFont.GetFont(size: size), RelativeSizeAxes = Axes.X };

        private ButtonRow row(params Drawable[] children)
        {
            var row = new ButtonRow
            {
                RelativeSizeAxes = Axes.X,
                Height = 25,
                Children = children.Select(b => (Drawable)new Container
                {
                    Padding = new MarginPadding { Horizontal = 1 },
                    Child = b,
                }).ToArray(),
            };
            rows.Add(row);
            return row;
        }

        private RoundedButton button(LocalisableString label, Action action)
        {
            var button = new TransportButton
            {
                Text = label,
                TooltipText = label,
                Action = action,
                RelativeSizeAxes = Axes.Both,
                Size = Vector2.One,
            };
            buttons.Add(button);
            return button;
        }

        protected override void UpdateAfterChildren()
        {
            base.UpdateAfterChildren();
            if (Parent == null || !player.LoadedBeatmapSuccessfully)
                return;
            var playfield = Parent.ToLocalSpace(ruleset.Playfield.ScreenSpaceDrawQuad).AABBFloat;
            // osu!'s border includes the radius of circles centred at the playfield edges.
            var border = Parent.ToLocalSpace(ruleset.Playfield.SkinnableComponentScreenSpaceDrawQuad).AABBFloat;
            float left = Math.Min(playfield.Left, border.Left);
            float right = Math.Max(playfield.Right, border.Right);
            float top = Math.Min(playfield.Top, border.Top);
            float bottom = Math.Max(playfield.Bottom, border.Bottom);
            var viewport = new RectangleF(0, 0, Parent.DrawWidth, Parent.DrawHeight);
            var exclusion = new RectangleF(left, top, right - left, bottom - top);
            leftBounds = ReplayPanelGeometry.GetSideBounds(viewport, exclusion, ReplayPanelSide.Left);
            rightBounds = ReplayPanelGeometry.GetSideBounds(viewport, exclusion, ReplayPanelSide.Right);
            playfieldCentre = (left + right) / 2;
            updatePanelLayout();
        }

        private RectangleF boundsFor(ReplayPanelSide side) => side == ReplayPanelSide.Left ? leftBounds : rightBounds;

        private void updatePanelLayout()
        {
            var layout = transport.PanelLayout;
            ActiveSide = layout.Dock switch
            {
                ReplayPanelDock.Left => ReplayPanelSide.Left,
                ReplayPanelDock.Right => ReplayPanelSide.Right,
                _ => layout.Side,
            };
            // Temporarily use the other gutter if the preferred side disappears. Keep the preference
            // so expanding the window restores it, rather than borrowing space from the playfield.
            if (boundsFor(ActiveSide).Width < 1)
                ActiveSide = ActiveSide == ReplayPanelSide.Left ? ReplayPanelSide.Right : ReplayPanelSide.Left;
            AllowedBounds = boundsFor(ActiveSide);
            if (AllowedBounds.Width < 1 || AllowedBounds.Height < 1)
            {
                Alpha = 0;
                Size = Vector2.Zero;
                return;
            }
            Alpha = 1;
            // Avoid a floating-point round trip placing the panel's far edge just outside the bounds.
            Size = Vector2.ComponentMin(transport.PanelLayout.PreferredSize, AllowedBounds.Size - new Vector2(0.01f));
            Vector2 space = Vector2.ComponentMax(Vector2.Zero, AllowedBounds.Size - Size);
            float x = layout.Dock == ReplayPanelDock.None
                ? space.X * Math.Clamp(layout.RelativePosition.X, 0, 1)
                : ActiveSide == ReplayPanelSide.Left ? 0 : space.X;
            Position = new Vector2(AllowedBounds.Left + x, AllowedBounds.Top + space.Y * Math.Clamp(layout.RelativePosition.Y, 0, 1));
            foreach (var row in rows)
                row.LayoutButtons(Math.Clamp((Height - 55) / 14, 20, 32));
            foreach (var button in buttons)
                button.FontSize = 11 * Math.Clamp(Width / 220, 0.75f, 1.2f);
            shortcuts.FontSize = 11 * Math.Clamp(Width / 220, 0.75f, 1.2f);
        }

        private void movePanel(Vector2 target, bool finish)
        {
            var layout = transport.PanelLayout;
            layout.Dock = ReplayPanelDock.None;
            layout.Side = target.X + Width / 2 < playfieldCentre ? ReplayPanelSide.Left : ReplayPanelSide.Right;
            updatePanelLayout();
            var space = Vector2.ComponentMax(Vector2.Zero, AllowedBounds.Size - Size);
            float x = Math.Clamp(target.X - AllowedBounds.X, 0, space.X);
            float y = Math.Clamp(target.Y - AllowedBounds.Y, 0, space.Y);
            if (finish && ActiveSide == ReplayPanelSide.Left && x <= 20)
                transport.PanelLayout.Dock = ReplayPanelDock.Left;
            else if (finish && ActiveSide == ReplayPanelSide.Right && space.X - x <= 20)
                transport.PanelLayout.Dock = ReplayPanelDock.Right;
            transport.PanelLayout.RelativePosition = new Vector2(space.X > 0 ? x / space.X : 0, space.Y > 0 ? y / space.Y : 0);
        }

        private void resizePanel(Vector2 target)
        {
            // A resize already in progress may outlive the disappearance of both gutters.
            if (AllowedBounds.Width < 1 || AllowedBounds.Height < 1)
                return;
            transport.PanelLayout.PreferredSize = new Vector2(
                Math.Clamp(target.X, Math.Min(140, AllowedBounds.Width), AllowedBounds.Width),
                Math.Clamp(target.Y, Math.Min(160, AllowedBounds.Height), AllowedBounds.Height));
        }

        protected override void Update()
        {
            base.Update();
            foreach (var b in navigation)
                b.Enabled.Value = transport.CanSeekTransport;
            focus.Enabled.Value = transport.CanSeekTransport && transport.IsTransportPaused && transport.SelectedObject != null
                                  && (player is not ReplayPracticePlayer p || p.Session.State == ReplayPracticeState.Paused);
            (ruleset as IReplayPracticeRuleset)?.SetReplayObjectFocus(focus.IsHeld && focus.Enabled.Value && IsPresent);
            double displayedTime = player switch
            {
                ReplayPracticePlayer { Session.State: ReplayPracticeState.Restoring } restoring => restoring.Session.StartTime,
                ReplayPlayer { PendingReplaySeekTime: double target } => target,
                _ => transport.TransportTime,
            };
            position.Text = $"{formatTime(displayedTime)} / {formatTime(transport.TransportEndTime)}  |  {transport.TransportRate.Value:0.00}x";
            if (player is ReplayPracticePlayer practice)
            {
                pause.Enabled.Value = practice.CanTogglePracticePause;
                pause.Text = practice.Session.State == ReplayPracticeState.Paused ? ReplayPracticeStrings.ResumePractice : ReplayPracticeStrings.PausePractice;
                status.Text = practice.Session.IsAutoplayBaseline ? ReplayPracticeStrings.AutoplayPractice : ReplayPracticeStrings.NotSaved;
                phase.Text = practice.Session.State switch
                {
                    ReplayPracticeState.Restoring => ReplayPracticeStrings.Restoring,
                    ReplayPracticeState.Preparing => ReplayPracticeStrings.Preparing((int)Math.Ceiling(practice.PreparationRemaining / 1000)),
                    ReplayPracticeState.Paused => practice.PreparationRemaining > 0 ? ReplayPracticeStrings.PausedPreparation : ReplayPracticeStrings.Paused,
                    ReplayPracticeState.Completed => ReplayPracticeStrings.Finished,
                    _ => ReplayPracticeStrings.Attempt(practice.Session.AttemptCount),
                };
                statistics.Text = ReplayPracticeStrings.Statistics(practice.AttemptAccuracy is double accuracy ? accuracy.ToString("P2") : "—", practice.AttemptMisses);
                primary.Enabled.Value = transport.CanSeekTransport;
                LocalisableString startTime = practice.Session.StartTime == practice.Session.RequestedTime
                    ? ReplayPracticeStrings.Start(practice.Session.StartTime / 1000)
                    : ReplayPracticeStrings.SafeStart(practice.Session.RequestedTime / 1000, practice.Session.StartTime / 1000);
                start.Text = practice.Session.ObjectStartIndex is int objectIndex
                    ? LocalisableString.Format("{0} | {1}", startTime, practice.Session.UsesObjectRange
                        ? ReplayPracticeStrings.ObjectSelected(objectIndex + 1) : ReplayPracticeStrings.ObjectSelectedExact(objectIndex + 1))
                    : startTime;
                history.Text = ReplayPracticeStrings.History(practice.Session.AttemptCount,
                    string.Join("   ", practice.Session.Attempts.TakeLast(8).Select(a => $"#{a.Number}: {(a.Accuracy is double result ? result.ToString("P2") : "—")} / {a.Misses} Miss")));
            }
            else
            {
                var replay = (ReplayPlayer)player;
                bool paused = transport.IsTransportPaused;
                pause.Text = paused ? ReplayPracticeStrings.ResumeReplay : ReplayPracticeStrings.PauseReplay;
                primary.Text = replay.SelectedObject != null && replay.IgnorePreviousObjectsDuringObjectPractice.Value
                    ? ReplayPracticeStrings.TakeOverObject : ReplayPracticeStrings.TakeOver;
                start.Text = replay.SelectedObject is { } selection
                    ? (replay.IgnorePreviousObjectsDuringObjectPractice.Value
                        ? ReplayPracticeStrings.ObjectSelected(selection.Index + 1) : ReplayPracticeStrings.ObjectSelectedExact(selection.Index + 1))
                    : string.Empty;
                primary.Enabled.Value = replay.ReplayPracticeUnavailableReason == null;
                primary.TooltipText = replay.ReplayPracticeUnavailableReason ?? (replay.SelectedObject != null && replay.IgnorePreviousObjectsDuringObjectPractice.Value
                    ? ReplayPracticeStrings.ObjectRangeHelp : ReplayPracticeStrings.FreezeFrame);
                status.Text = replay.IsAutoplayBaseline ? ReplayPracticeStrings.AutoplayBaseline : ReplayPracticeStrings.Replay;
                phase.Text = replay.IsRestoringReplay ? ReplayPracticeStrings.Restoring : paused ? ReplayPracticeStrings.Paused : ReplayPracticeStrings.Playing;
                history.Text = replay.ReplayPracticeUnavailableReason ?? (replay.IsAutoplayBaseline ? ReplayPracticeStrings.AutoplayBaselineHelp : ReplayPracticeStrings.NavigationHint);
            }
            if (transport.FailureNavigationMessage is { } message)
                phase.Text = message;
        }

        private static string formatTime(double time)
        {
            var span = TimeSpan.FromMilliseconds(Math.Abs(time));
            return $"{(time < 0 ? "-" : "")}{(int)span.TotalMinutes}:{span.Seconds:00}.{span.Milliseconds:000}";
        }

        private partial class TransportButton : RoundedButton
        {
            public TransportButton() => SpriteText.Font = OsuFont.GetFont(size: 11);
            public float FontSize
            {
                set
                {
                    SpriteText.Font = SpriteText.Font.With(size: value);
                    SpriteText.MaxWidth = Math.Max(1, DrawWidth * 0.94f);
                }
            }
            protected override SpriteText CreateText() => createButtonText();
        }

        private partial class HoldFocusButton : TransportButton
        {
            private bool held;

            [Resolved]
            private GameHost host { get; set; } = null!;

            public bool IsHeld => held && Enabled.Value && IsHovered && host.IsActive.Value;

            protected override bool OnMouseDown(MouseDownEvent e)
            {
                if (e.Button != MouseButton.Left || !Enabled.Value)
                    return false;
                held = true;
                base.OnMouseDown(e);
                return true;
            }

            protected override void OnMouseUp(MouseUpEvent e)
            {
                if (e.Button == MouseButton.Left) held = false;
                base.OnMouseUp(e);
            }

            protected override void OnHoverLost(HoverLostEvent e)
            {
                held = false;
                base.OnHoverLost(e);
            }

            protected override void Update()
            {
                base.Update();
                if (!Enabled.Value || !host.IsActive.Value || !IsPresent)
                    held = false;
            }

            protected override bool OnClick(ClickEvent e) => true;
        }

        private partial class TransportKeyBindingButton : ReplayKeyBindingButton
        {
            public TransportKeyBindingButton() => SpriteText.Font = OsuFont.GetFont(size: 11);
            public float FontSize
            {
                set
                {
                    SpriteText.Font = SpriteText.Font.With(size: value);
                    SpriteText.MaxWidth = Math.Max(1, DrawWidth * 0.94f);
                }
            }
            protected override SpriteText CreateText() => createButtonText();
        }

        private static SpriteText createButtonText() => new TruncatingSpriteText
        {
            ShowTooltip = false,
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
        };

        private partial class ButtonRow : Container
        {
            public void LayoutButtons(float buttonHeight)
            {
                int columns = Math.Clamp((int)(DrawWidth / 75), 1, Children.Count);
                int count = (Children.Count + columns - 1) / columns;
                Height = count * buttonHeight + (count - 1) * 3;
                for (int i = 0; i < Children.Count; i++)
                {
                    int row = i / columns;
                    int rowColumns = Math.Min(columns, Children.Count - row * columns);
                    float width = Math.Max(0, DrawWidth) / rowColumns;
                    Children[i].Position = new Vector2(i % columns * width, row * (buttonHeight + 3));
                    Children[i].Size = new Vector2(width, buttonHeight);
                }
            }
        }

        public partial class PanelHandle : Container
        {
            private readonly ReplayTransportControls panel;
            private readonly bool resize;
            private Vector2 initial;
            private Vector2 mouse;

            public PanelHandle(ReplayTransportControls panel, bool resize)
            {
                this.panel = panel;
                this.resize = resize;
            }

            protected override bool OnMouseDown(MouseDownEvent e) => e.Button == MouseButton.Left;
            protected override bool OnClick(ClickEvent e) => true;
            protected override bool OnDragStart(DragStartEvent e)
            {
                if (e.Button != MouseButton.Left)
                    return false;
                initial = resize ? panel.Size : panel.Position;
                mouse = panel.Parent!.ToLocalSpace(e.ScreenSpaceMouseDownPosition);
                return true;
            }

            protected override void OnDrag(DragEvent e)
            {
                var target = initial + panel.Parent!.ToLocalSpace(e.ScreenSpaceMousePosition) - mouse;
                if (resize)
                    panel.resizePanel(target);
                else
                    panel.movePanel(target, false);
            }

            protected override void OnDragEnd(DragEndEvent e)
            {
                if (!resize)
                    panel.movePanel(initial + panel.Parent!.ToLocalSpace(e.ScreenSpaceMousePosition) - mouse, true);
                base.OnDragEnd(e);
            }
        }
    }
}
