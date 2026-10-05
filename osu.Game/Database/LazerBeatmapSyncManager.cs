// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Localisation;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Localisation;
using osu.Game.Overlays;
using osu.Game.Overlays.Notifications;
using osu.Game.Screens.Menu;
using osu.Game.Screens.Select;

namespace osu.Game.Database
{
    /// <summary>Owns the opt-in background worker. Only idle menu/selection screens may commit updates.</summary>
    public partial class LazerBeatmapSyncManager : Component
    {
        public readonly Bindable<LocalisableString> Status = new Bindable<LocalisableString>(LazerSyncStrings.Disabled);
        public readonly Bindable<bool> Busy = new Bindable<bool>();

        [Resolved]
        private OsuGame game { get; set; } = null!;

        [Resolved]
        private INotificationOverlay notifications { get; set; } = null!;

        [Resolved]
        private IBindable<WorkingBeatmap> beatmap { get; set; } = null!;

        private Bindable<bool> enabled = null!;
        private Bindable<string> path = null!;
        private LazerBeatmapSync synchroniser = null!;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private volatile bool safeToCommit;
        private int generation;
        private double nextScan;
        private string lastIssue = string.Empty;

        [BackgroundDependencyLoader]
        private void load(Storage storage, RealmAccess realm, BeatmapManager beatmaps, OsuConfigManager config)
        {
            synchroniser = new LazerBeatmapSync(storage, new StableBeatmapSyncStore(storage, realm, beatmaps), new LazerReplaySync(storage, realm, b => beatmaps.GetWorkingBeatmap(b)));
            enabled = config.GetBindable<bool>(OsuSetting.LazerSyncEnabled);
            path = config.GetBindable<string>(OsuSetting.LazerSyncPath);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            enabled.BindValueChanged(_ => settingsChanged(), true);
            path.BindValueChanged(_ => settingsChanged());
            if (string.IsNullOrWhiteSpace(path.Value))
                UseDetectedPath();
        }

        private void settingsChanged()
        {
            Interlocked.Increment(ref generation);
            nextScan = 0;
            lastIssue = string.Empty;
            Status.Value = enabled.Value ? LazerSyncStrings.Waiting : LazerSyncStrings.Disabled;
        }

        protected override void Update()
        {
            base.Update();
            safeToCommit = enabled.Value && game.ScreenStack?.CurrentScreen is MainMenu or SongSelect;
            if (!safeToCommit || Busy.Value || Time.Current < nextScan || string.IsNullOrWhiteSpace(path.Value))
                return;
            RequestSync();
        }

        public void UseDetectedPath()
        {
            try
            {
                path.Value = LazerLibraryConnection.DetectDataPath();
            }
            catch (Exception)
            {
                Status.Value = LazerSyncStrings.NotFound;
            }
        }

        public void RequestSync(bool selectedOnly = false, LazerSyncResolution resolution = LazerSyncResolution.None, bool forceHash = false)
        {
            if (Busy.Value)
                return;
            if (!enabled.Value)
            {
                Status.Value = LazerSyncStrings.Disabled;
                return;
            }
            safeToCommit = game.ScreenStack?.CurrentScreen is MainMenu or SongSelect;
            if (!safeToCommit)
            {
                Status.Value = LazerSyncStrings.Waiting;
                return;
            }

            Guid? selected = selectedOnly ? beatmap.Value.BeatmapSetInfo?.ID : null;
            if (selectedOnly && (selected == null || selected == Guid.Empty))
            {
                Status.Value = LazerSyncStrings.SelectBeatmap;
                return;
            }
            string configuredPath = path.Value;
            int requestedGeneration = generation;
            Busy.Value = true;
            Status.Value = LazerSyncStrings.Scanning;
            nextScan = Time.Current + 15000;

            Task.Run(async () =>
            {
                try
                {
                    var result = await synchroniser.Synchronize(configuredPath,
                        () => safeToCommit && requestedGeneration == Volatile.Read(ref generation), selected, resolution, forceHash, lifetime.Token).ConfigureAwait(false);
                    Schedule(() =>
                    {
                        if (requestedGeneration != generation)
                            return;
                        Status.Value = LazerSyncStrings.Result(result.Imported, result.Exported, result.Issues.Count, result.ReplaysImported, result.ReplaysExported);
                        string issue = string.Join("\n", result.Issues);
                        if (issue.Length > 0 && issue != lastIssue)
                        {
                            Logger.Log($"Lazer sync requires attention:\n{issue}", LoggingTarget.Runtime, LogLevel.Important);
                            notifications.Post(new SimpleNotification { Text = LazerSyncStrings.Attention(result.Issues[0]) });
                        }
                        lastIssue = issue;
                    });
                }
                catch (OperationCanceledException)
                {
                    Schedule(() =>
                    {
                        if (requestedGeneration == generation)
                            Status.Value = enabled.Value ? LazerSyncStrings.Waiting : LazerSyncStrings.Disabled;
                    });
                }
                catch (LazerLibraryBusyException)
                {
                    Schedule(() =>
                    {
                        if (requestedGeneration == generation)
                            Status.Value = LazerSyncStrings.CloseOfficial;
                    });
                }
                catch (Exception e)
                {
                    Logger.Error(e, "Lazer library synchronisation failed");
                    Schedule(() =>
                    {
                        if (requestedGeneration == generation)
                            Status.Value = LazerSyncStrings.Error(e.Message);
                    });
                }
                finally
                {
                    Schedule(() => Busy.Value = false);
                }
            });
        }

        protected override void Dispose(bool isDisposing)
        {
            safeToCommit = false;
            lifetime.Cancel();
            synchroniser?.Dispose();
            base.Dispose(isDisposing);
        }
    }
}
