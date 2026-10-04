// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Localisation;
using osu.Framework.Screens;
using osu.Game.Localisation;
using osu.Game.Scoring;

namespace osu.Game.Screens.Play
{
    internal partial class ReplayFailureNavigator : CompositeDrawable
    {
        private readonly IReplayTransport transport;
        private readonly Func<Score> source;
        private readonly Action<ReplayObjectSelection> seek;
        private ReplayFailureKind? pending;
        private bool backwards;
        private double requestedTime;
        private int? selectedIndex;
        private ScreenStack? analysisStack;
        private int analysisGeneration;
        public LocalisableString? Message { get; private set; }

        public ReplayFailureNavigator(IReplayTransport transport, Func<Score> source, Action<ReplayObjectSelection> seek)
        {
            this.transport = transport;
            this.source = source;
            this.seek = seek;
            RelativeSizeAxes = Axes.Both;
            Alpha = 0;
            AlwaysPresent = true;
        }

        public void Request(ReplayFailureKind kind, bool backwards = false)
        {
            if (!transport.CanSeekTransport)
                return;
            transport.BeginTransportSeek();
            requestedTime = transport.TransportTime;
            selectedIndex = transport.SelectedObject?.Index;
            pending = kind;
            this.backwards = backwards;
            Message = ReplayPracticeStrings.AnalysingReplay;
            ensureAnalysis();
        }

        private void ensureAnalysis()
        {
            if (transport.FailureIndex.TryBegin())
            {
                analysisGeneration = transport.FailureIndex.Generation;
                AddInternal(analysisStack = new ScreenStack { RelativeSizeAxes = Axes.Both, Alpha = 0, AlwaysPresent = true });
                analysisStack.Push(new ReplayFailureAnalyser(source(), transport.FailureIndex));
            }
        }

        public void Cancel()
        {
            pending = null;
            Message = null;
        }

        protected override void Update()
        {
            base.Update();
            if (analysisStack != null && !transport.FailureIndex.IsAnalysing)
            {
                analysisStack.RemoveAndDisposeImmediately();
                analysisStack = null;
            }
            if (pending is not ReplayFailureKind kind)
                return;
            if (transport.FailureIndex.Unavailable)
            {
                pending = null;
                Message = ReplayPracticeStrings.FailureAnalysisUnavailable;
            }
            else if (transport.FailureIndex.IsReady)
            {
                pending = null;
                var selection = backwards
                    ? transport.FailureIndex.FindPrevious(kind, requestedTime, selectedIndex)
                    : transport.FailureIndex.FindNext(kind, requestedTime, selectedIndex);
                if (selection == null)
                    Message = backwards
                        ? kind == ReplayFailureKind.Miss ? ReplayPracticeStrings.NoPreviousMiss : ReplayPracticeStrings.NoPreviousIgnored
                        : kind == ReplayFailureKind.Miss ? ReplayPracticeStrings.NoNextMiss : ReplayPracticeStrings.NoNextIgnored;
                else
                {
                    Message = null;
                    seek(selection);
                }
            }
            else
                // A previous Player may finish disposing after this request. Claim the cancelled
                // analysis here rather than leaving a new navigator waiting on an owner that is gone.
                ensureAnalysis();
        }

        protected override void Dispose(bool isDisposing)
        {
            if (isDisposing && analysisStack != null && transport.FailureIndex.Generation == analysisGeneration)
                transport.FailureIndex.Cancel();
            base.Dispose(isDisposing);
        }
    }
}
