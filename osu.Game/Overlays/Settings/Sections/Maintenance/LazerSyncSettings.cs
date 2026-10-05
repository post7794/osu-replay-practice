// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Localisation;
using osu.Framework.Screens;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Localisation;
using osu.Game.Overlays.Dialog;
using osu.Game.Screens;

namespace osu.Game.Overlays.Settings.Sections.Maintenance
{
    public partial class LazerSyncSettings : SettingsSubsection
    {
        protected override LocalisableString Header => LazerSyncStrings.Header;

        private IBindable<LocalisableString>? statusBinding;

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager config, LazerBeatmapSyncManager? sync, IPerformFromScreenRunner? performer, IDialogOverlay? dialogs)
        {
            if (sync == null)
                return;

            var status = new SettingsNote
            {
                RelativeSizeAxes = Axes.X
            };
            statusBinding = sync.Status.GetBoundCopy();
            statusBinding.BindValueChanged(e => status.Current.Value = new SettingsNote.Data(e.NewValue, SettingsNote.Type.Informational), true);

            AddRange(new Drawable[]
            {
                new SettingsNote
                {
                    RelativeSizeAxes = Axes.X,
                    Current = { Value = new SettingsNote.Data(LazerSyncStrings.Help, SettingsNote.Type.Warning) }
                },
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = LazerSyncStrings.Enable,
                    Current = config.GetBindable<bool>(OsuSetting.LazerSyncEnabled)
                }),
                new SettingsItemV2(new FormTextBox
                {
                    Caption = LazerSyncStrings.Path,
                    Current = config.GetBindable<string>(OsuSetting.LazerSyncPath)
                }),
                new SettingsButtonV2
                {
                    Text = LazerSyncStrings.Browse,
                    Action = () => performer?.PerformFromScreen(screen => screen.Push(new SyncDirectorySelectScreen(
                        directory => config.SetValue(OsuSetting.LazerSyncPath, directory))))
                },
                new SettingsButtonV2 { Text = LazerSyncStrings.Detect, Action = sync.UseDetectedPath },
                new SettingsButtonV2 { Text = LazerSyncStrings.SyncNow, Action = () => sync.RequestSync(forceHash: true) },
                new SettingsButtonV2
                {
                    Text = LazerSyncStrings.Publish,
                    Action = () => confirm(LazerSyncStrings.PublishHelp, () => sync.RequestSync(selectedOnly: true, forceHash: true))
                },
                new DangerousSettingsButtonV2
                {
                    Text = LazerSyncStrings.UseOfficial,
                    Action = () => confirm(LazerSyncStrings.ResolveHelp, () => sync.RequestSync(true, LazerSyncResolution.UseOfficial, true))
                },
                new DangerousSettingsButtonV2
                {
                    Text = LazerSyncStrings.UseLocal,
                    Action = () => confirm(LazerSyncStrings.ResolveHelp, () => sync.RequestSync(true, LazerSyncResolution.UseLocal, true))
                },
                status
            });

            void confirm(LocalisableString text, Action action) => dialogs?.Push(new SyncConfirmationDialog(text, action));
        }

        protected override void Dispose(bool isDisposing)
        {
            statusBinding?.UnbindAll();
            base.Dispose(isDisposing);
        }

        private partial class SyncConfirmationDialog : DangerousActionDialog
        {
            public SyncConfirmationDialog(LocalisableString text, Action action)
            {
                BodyText = text;
                DangerousAction = action;
            }
        }

        private partial class SyncDirectorySelectScreen : DirectorySelectScreen
        {
            private readonly Action<string> selected;
            public override LocalisableString HeaderText => LazerSyncStrings.Path;

            public SyncDirectorySelectScreen(Action<string> selected)
            {
                this.selected = selected;
            }

            protected override bool IsValidDirectory(DirectoryInfo? info)
            {
                try
                {
                    return info != null && Directory.Exists(LazerLibraryConnection.ResolveDataPath(info.FullName));
                }
                catch (Exception)
                {
                    return false;
                }
            }

            protected override void OnSelection(DirectoryInfo directory)
            {
                selected(LazerLibraryConnection.ResolveDataPath(directory.FullName));
                this.Exit();
            }
        }
    }
}
