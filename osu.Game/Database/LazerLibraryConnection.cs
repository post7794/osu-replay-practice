// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using osu.Framework.Platform;
using osu.Game.Beatmaps;
using Realms;

namespace osu.Game.Database
{
    public sealed class LazerLibraryBusyException : IOException
    {
        public LazerLibraryBusyException(Exception? inner = null)
            : base("Close official lazer before syncing. The database is currently in use.", inner)
        {
        }
    }

    /// <summary>
    /// An offline connection to another lazer library. Only an isolated database copy is opened by
    /// Realm; exclusive file leases block the official client for the duration of this connection.
    /// </summary>
    public sealed class LazerLibraryConnection : IStableBeatmapSyncStore, IDisposable
    {
        private readonly Storage storage;
        private readonly string root;
        private readonly string work;
        private readonly Storage staging;
        private readonly string journal;
        private readonly FileStream databaseLease;
        private readonly FileStream lockLease;
        private readonly RealmAccess realm;
        private readonly StableBeatmapSyncStore store;
        internal LazerReplaySyncStore Replays { get; } = null!;
        private bool commitFailed;

        public static string ResolveDataPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new IOException("Select an absolute lazer data directory containing client.realm and files, not the installation or files directory.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < 8; i++)
            {
                path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                ValidateDirectory(path);
                if (!seen.Add(path))
                    throw new IOException("Cyclic lazer storage.ini redirection.");
                string ini = StableBeatmapSync.SafePath(path, "storage.ini");
                string? redirected = null;
                if (File.Exists(ini))
                {
                    foreach (string line in File.ReadLines(ini))
                    {
                        int separator = line.IndexOf('=');
                        if (separator >= 0 && line[..separator].Trim().Equals("FullPath", StringComparison.OrdinalIgnoreCase))
                        {
                            redirected = line[(separator + 1)..].Trim();
                            break;
                        }
                    }
                }
                if (string.IsNullOrWhiteSpace(redirected))
                {
                    if (!File.Exists(StableBeatmapSync.SafePath(path, "client.realm")) || !Directory.Exists(StableBeatmapSync.SafePath(path, "files")))
                        throw new IOException("This is not a lazer data directory. Select the parent of client.realm and files.");
                    return path;
                }
                if (!Path.IsPathFullyQualified(redirected))
                    throw new IOException("The lazer storage.ini path must be absolute.");
                path = redirected;
            }
            throw new IOException("Too many lazer storage.ini redirects.");
        }

        public static string DetectDataPath() => ResolveDataPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osu"));

        internal static void ValidateDirectory(string path)
        {
            var current = new DirectoryInfo(path);
            if (!current.Exists)
                throw new DirectoryNotFoundException(path);
            while (current != null)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Sync paths must not traverse a symbolic link or junction.");
                current = current.Parent;
            }
        }

        internal static bool Within(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        internal static string RootKey(string root) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant()))).ToLowerInvariant();

        public LazerLibraryConnection(Storage storage, string dataPath)
        {
            this.storage = storage;
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Offline lazer database synchronization is currently supported on Windows only.");
            root = ResolveDataPath(dataPath);
            string own = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storage.GetFullPath(string.Empty)));
            ValidateDirectory(own);
            if (Within(root, own) || Within(own, root))
                throw new IOException("The two lazer data directories must be separate and must not contain one another.");
            journal = $"lazer-sync/transactions/{RootKey(root)}.json";
            StableBeatmapSync.SafePath(own, journal);
            if (commitFailed || storage.Exists(journal))
                throw new IOException($"A previous database write was interrupted. Keep official lazer closed and restore the database backup recorded in {storage.GetFullPath(journal)} before removing this marker.");

            foreach (var process in Process.GetProcessesByName("osu!"))
            {
                using (process)
                {
                    if (process.Id != Environment.ProcessId)
                        throw new LazerLibraryBusyException();
                }
            }

            work = $"lazer-sync/staging/{Guid.NewGuid():N}";
            StableBeatmapSync.SafePath(own, work);
            staging = storage.GetStorageForDirectory(work);
            try
            {
                try
                {
                    lockLease = new FileStream(StableBeatmapSync.SafePath(root, "client.realm.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    databaseLease = new FileStream(StableBeatmapSync.SafePath(root, "client.realm"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException e)
                {
                    throw new LazerLibraryBusyException(e);
                }
                using (var output = staging.GetStream("client.realm", FileAccess.Write, FileMode.CreateNew))
                    databaseLease.CopyTo(output);
                realm = RealmAccess.OpenSyncSnapshot(staging, "client.realm");
                store = new StableBeatmapSyncStore(staging, realm);
                Replays = new LazerReplaySyncStore(staging, realm, root);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public IReadOnlyList<StableSyncSet> GetAll() => realm.Run(r => r.All<BeatmapSetInfo>().Where(s => !s.DeletePending && !s.Protected).AsEnumerable()
            .Select(s => new StableSyncSet(s.ID, s.Files.ToDictionary(f => f.Filename, f => f.File.Hash, StringComparer.OrdinalIgnoreCase))).ToArray());

        public StableSyncSet? Get(Guid id) => store.Get(id);

        public Stream OpenFile(StableSyncSet expected, string filename, bool forStable)
        {
            var actual = Get(expected.ID);
            if (actual == null || !StableBeatmapSync.SameFiles(actual.Files, expected.Files) || !expected.Files.TryGetValue(filename, out string? hash))
                throw new IOException("The official beatmap changed while syncing.");
            string relative = FilePath(hash);
            string staged = StableBeatmapSync.SafePath(staging.GetFullPath(string.Empty), relative);
            return File.OpenRead(File.Exists(staged) ? staged : StableBeatmapSync.SafePath(root, relative));
        }

        internal static string FilePath(string hash)
        {
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                throw new IOException("Invalid content-addressed file hash.");
            return $"files/{hash[..1]}/{hash[..2]}/{hash}";
        }

        public async Task<StableSyncSet> Import(string directory, Guid? original, Func<bool> canCommit, CancellationToken cancellationToken)
        {
            if (commitFailed || storage.Exists(journal))
                throw new IOException("A previous official database commit needs recovery before another write.");
            var result = await store.Import(directory, original, canCommit, cancellationToken).ConfigureAwait(false);
            CommitFiles(result.Files.Values, canCommit, cancellationToken);
            return result;
        }

        internal void CommitFiles(IEnumerable<string> hashes, Func<bool> canCommit, CancellationToken cancellationToken)
        {
            if (commitFailed || storage.Exists(journal))
                throw new IOException("A previous official database commit failed; reopen the connection after resolving recovery requirements.");
            commitFailed = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (!canCommit()) throw new OperationCanceledException();

            // WriteCopy produces a consistent database image without copying live mmap state.
            string image = staging.GetFullPath($"commit-{Guid.NewGuid():N}.realm");
            realm.Run(r => r.WriteCopy(new RealmConfiguration(image)));
            string backup = $"lazer-sync/backups/database-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.realm";
            StableBeatmapSync.SafePath(storage.GetFullPath(string.Empty), backup);
            databaseLease.Position = 0;
            using (var output = storage.GetStream(backup, FileAccess.Write, FileMode.CreateNew))
            {
                databaseLease.CopyTo(output);
                if (output is FileStream fs)
                    fs.Flush(true);
            }
            // Add immutable files only. Never remove/overwrite content referenced by scores or skins.
            foreach (string hash in hashes.Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = FilePath(hash);
                string destination = StableBeatmapSync.SafePath(root, relative);
                if (File.Exists(destination))
                {
                    using var existing = File.OpenRead(destination);
                    if (!HashMatches(existing, hash))
                        throw new IOException("An official content-addressed file is corrupt; it was not overwritten.");
                    continue;
                }
                string source = StableBeatmapSync.SafePath(staging.GetFullPath(string.Empty), relative);
                using (var input = File.OpenRead(source))
                {
                    if (!HashMatches(input, hash))
                        throw new IOException("A staged file failed checksum validation.");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                string temporary = destination + $".{Guid.NewGuid():N}.tmp";
                try
                {
                    File.Copy(source, temporary);
                    File.Move(temporary, destination);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!canCommit()) throw new OperationCanceledException();
            // Immutable file additions are harmless before this point. Only record a database
            // recovery requirement once an actual database installation is about to start.
            using (var output = new StreamWriter(storage.CreateFileSafely(journal)))
                output.Write(JsonConvert.SerializeObject(new { Source = root, Backup = storage.GetFullPath(backup) }));
            // Keep both exclusive leases held throughout installation and rollback.
            try
            {
                using var input = File.OpenRead(image);
                replaceDatabase(input);
            }
            catch
            {
                using var originalDatabase = storage.GetStream(backup);
                replaceDatabase(originalDatabase);
                throw;
            }
            storage.Delete(journal);
            commitFailed = false;
        }

        private void replaceDatabase(Stream input)
        {
            databaseLease.Position = 0;
            input.CopyTo(databaseLease);
            databaseLease.SetLength(databaseLease.Position);
            databaseLease.Flush(true);
        }

        internal static bool HashMatches(Stream input, string hash) => Convert.ToHexString(SHA256.HashData(input)).Equals(hash, StringComparison.OrdinalIgnoreCase);

        public void Dispose()
        {
            try
            {
                realm?.Dispose();
            }
            finally
            {
                databaseLease?.Dispose();
                lockLease?.Dispose();
            }
            if (work != null)
            {
                string full = storage.GetFullPath(work);
                if (Within(storage.GetFullPath("lazer-sync/staging"), full) && Directory.Exists(full))
                    storage.DeleteDirectory(work);
            }
        }
    }
}
