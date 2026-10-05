// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Localisation;

namespace osu.Game.Localisation
{
    public static class LazerSyncStrings
    {
        private static string getKey(string key) => $@"osu.Game.Localisation.LazerSync:{key}";

        public static LocalisableString Header => new TranslatableString(getKey(@"Header"), @"Official osu!lazer two-way beatmap / replay sync");

        public static LocalisableString Enable => new TranslatableString(getKey(@"Enable"), @"Enable two-way sync (writes to official lazer)");

        public static LocalisableString Path => new TranslatableString(getKey(@"Path"), @"Official lazer data directory (contains client.realm and files)");

        public static LocalisableString Browse => new TranslatableString(getKey(@"Browse"), @"Choose lazer data directory");

        public static LocalisableString Detect => new TranslatableString(getKey(@"Detect"), @"Detect official lazer data directory");

        public static LocalisableString SyncNow => new TranslatableString(getKey(@"SyncNow"), @"Synchronise now (verify all file contents)");

        public static LocalisableString Publish => new TranslatableString(getKey(@"Publish"), @"Link / publish selected beatmap to official lazer");

        public static LocalisableString UseOfficial => new TranslatableString(getKey(@"UseOfficial"), @"Selected conflict: keep official lazer version");

        public static LocalisableString UseLocal => new TranslatableString(getKey(@"UseLocal"), @"Selected conflict: keep this client version");

        public static LocalisableString Help => new TranslatableString(getKey(@"Help"), @"Close official lazer before syncing. In menus, beatmaps and saved local replays sync both ways every 15 seconds. Replays include their score metadata, are deduplicated by content, and are never uploaded. Matching beatmap versions are required. Backups: lazer-sync/backups. Deletions, skins and account settings are not synced.");

        public static LocalisableString PublishHelp => new TranslatableString(getKey(@"PublishHelp"), @"Publish or link the selected local beatmap to official lazer. Unlinked local beatmaps are never published automatically. Close official lazer first.");

        public static LocalisableString ResolveHelp => new TranslatableString(getKey(@"ResolveHelp"), @"Back up both sides, then replace the other beatmap with the selected version. Close official lazer and stop editing in this client first. Raw lazer files are kept without timing or position rounding.");

        public static LocalisableString Disabled => new TranslatableString(getKey(@"Disabled"), @"Two-way sync is disabled.");

        public static LocalisableString Waiting => new TranslatableString(getKey(@"Waiting"), @"Sync waits until you return to the main menu or song select.");

        public static LocalisableString CloseOfficial => new TranslatableString(getKey(@"CloseOfficial"), @"Waiting for official lazer to close. Your changes will sync after it exits.");

        public static LocalisableString Scanning => new TranslatableString(getKey(@"Scanning"), @"Checking official lazer beatmaps and replays…");

        public static LocalisableString NotFound => new TranslatableString(getKey(@"NotFound"), @"Official lazer data was not detected. Select the directory containing client.realm and files, not its installation directory.");

        public static LocalisableString SelectBeatmap => new TranslatableString(getKey(@"SelectBeatmap"), @"Select a local beatmap in song select first.");

        public static LocalisableString Result(int imported, int exported, int issues, int replaysImported = 0, int replaysExported = 0) => new TranslatableString(getKey(@"Result"), @"Beatmaps in/out: {0}/{1}; replays in/out: {3}/{4}; needs attention: {2}.", imported, exported, issues, replaysImported, replaysExported);

        public static LocalisableString Attention(string detail) => new TranslatableString(getKey(@"Attention"), @"Sync needs attention: {0}", detail);

        public static LocalisableString Error(string detail) => new TranslatableString(getKey(@"Error"), @"Sync failed: {0}", detail);

    }
}
