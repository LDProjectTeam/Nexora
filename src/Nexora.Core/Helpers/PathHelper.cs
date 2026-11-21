using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Nexora.Core.Services;

namespace Nexora.Core.Helpers
{
    public static class PathHelper
    {
        private static readonly Lazy<string> AppDataDirectory = new(ResolveAppDataDirectory);

        public static string AppDataDir => AppDataDirectory.Value;

        private static string ResolveAppDataDirectory()
        {
            // Test runs and portable profiles can point the launcher at an isolated
            // data directory so they never overwrite the real library/downloads.
            var dataOverride = Environment.GetEnvironmentVariable("NEXORA_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(dataOverride))
                return Path.GetFullPath(dataOverride);

            var roamingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var appDataDirectory = Path.Combine(roamingDirectory, "Nexora");
            var previousDirectory = Path.Combine(roamingDirectory, string.Concat("Game", "Vault"));

            if (!Directory.Exists(previousDirectory))
                return appDataDirectory;

            try
            {
                if (!Directory.Exists(appDataDirectory))
                    Directory.Move(previousDirectory, appDataDirectory);
                else
                    MergeMissingFiles(previousDirectory, appDataDirectory);

                return appDataDirectory;
            }
            catch (IOException)
            {
                return previousDirectory;
            }
            catch (UnauthorizedAccessException)
            {
                return previousDirectory;
            }
        }

        private static void MergeMissingFiles(string sourceDirectory, string targetDirectory)
        {
            Directory.CreateDirectory(targetDirectory);

            foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory).ToArray())
            {
                var targetFile = Path.Combine(targetDirectory, Path.GetFileName(sourceFile));
                var destinationFile = targetFile;
                var backupIndex = 0;
                while (File.Exists(destinationFile))
                {
                    backupIndex++;
                    destinationFile = $"{targetFile}.previous{backupIndex}";
                }

                File.Move(sourceFile, destinationFile);
            }

            foreach (var sourceSubdirectory in Directory.EnumerateDirectories(sourceDirectory).ToArray())
                MergeMissingFiles(
                    sourceSubdirectory,
                    Path.Combine(targetDirectory, Path.GetFileName(sourceSubdirectory)));

            if (!Directory.EnumerateFileSystemEntries(sourceDirectory).Any())
                Directory.Delete(sourceDirectory);
        }

        public static string SettingsFile => Path.Combine(AppDataDir, "settings.json");
        public static string LibraryFile => Path.Combine(AppDataDir, "library.json");
        public static string LogsDir => Path.Combine(AppDataDir, "logs");
        public static string TorrentDataDir => Path.Combine(AppDataDir, "torrents");
        public static string LegacyTempDir => Path.Combine(AppDataDir, "staging");
        public static string ArtworkCacheDir => Path.Combine(AppDataDir, "artwork");
        public static string SteamMetadataCacheDir => Path.Combine(AppDataDir, "steam-metadata");
        public static string DefaultLibraryPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Library");

        /// <summary>Uses a named Library folder when the user selects a drive root.</summary>
        public static string GetLibraryRoot(string selectedPath)
        {
            if (string.IsNullOrWhiteSpace(selectedPath))
                throw new ArgumentException("A library path is required.", nameof(selectedPath));

            var fullPath = Path.GetFullPath(selectedPath);
            var driveRoot = Path.GetPathRoot(fullPath);
            if (driveRoot != null && string.Equals(
                    fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    driveRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                return Path.Combine(driveRoot, "Library");

            return fullPath;
        }

        /// <summary>Temporary download directory on the same drive as the selected game library.</summary>
        public static string GetStagingPath(string libraryPath, string gameId)
        {
            if (string.IsNullOrWhiteSpace(libraryPath))
                throw new ArgumentException("A library path is required.", nameof(libraryPath));

            var safeGameId = SanitizeFileName(gameId);
            if (string.IsNullOrWhiteSpace(safeGameId))
                throw new ArgumentException("A valid game ID is required.", nameof(gameId));

            return Path.Combine(Path.GetFullPath(libraryPath), "temp", safeGameId);
        }

        /// <summary>
        /// Payload bytes that already occupy the target drive. Returns the larger of the
        /// logical size of the files in <paramref name="directoryPath"/> and the persisted
        /// downloaded-byte counter.
        /// A torrent engine preallocates every file when a torrent is added, so a paused
        /// download can already occupy its full final size on disk while only part of the
        /// pieces have arrived. The remaining pieces are then written into the allocated
        /// files and need no additional free space — which is why the resume disk-space
        /// check must use this value instead of the verified-pieces counter alone.
        /// </summary>
        public static long GetPayloadBytesOnDisk(string directoryPath, long persistedDownloadedBytes)
        {
            long onDisk = 0;
            try
            {
                if (!string.IsNullOrWhiteSpace(directoryPath) && Directory.Exists(directoryPath))
                {
                    foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
                    {
                        try { onDisk += new FileInfo(file).Length; }
                        catch (FileNotFoundException) { }
                        catch (DirectoryNotFoundException) { }
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            return Math.Max(onDisk, Math.Max(0, persistedDownloadedBytes));
        }

        /// <summary>Removes only the staging directory belonging to this game, never other downloads.</summary>
        public static bool DeleteGameStagingDirectory(string stagingPath, string gameId)
        {
            try
            {
                var fullPath = Path.GetFullPath(stagingPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var stagingRoot = Path.GetDirectoryName(fullPath);
                var expectedName = SanitizeFileName(gameId);
                var stagingName = Path.GetFileName(fullPath);
                var isGameStaging = string.Equals(stagingName, expectedName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(stagingName, $"{expectedName}_update", StringComparison.OrdinalIgnoreCase);
                if (stagingRoot == null
                    || !string.Equals(Path.GetFileName(stagingRoot), "temp", StringComparison.OrdinalIgnoreCase)
                    || !isGameStaging)
                    return false;

                if (!Directory.Exists(fullPath))
                {
                    DeleteEmptyStagingRoot(fullPath);
                    return true;
                }

                for (var attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
                            File.SetAttributes(file, FileAttributes.Normal);

                        Directory.Delete(fullPath, recursive: true);
                        DeleteEmptyStagingRoot(fullPath);
                        return true;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        if (attempt == 4)
                            return false;
                        Thread.Sleep(200 * (attempt + 1));
                    }
                }

                return !Directory.Exists(fullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
        }

        public static void DeleteEmptyStagingRoot(string stagingPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(stagingPath))
                    return;

                var full = Path.GetFullPath(stagingPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string? stagingRoot;
                if (string.Equals(Path.GetFileName(full), "temp", StringComparison.OrdinalIgnoreCase))
                {
                    stagingRoot = full;
                }
                else
                {
                    stagingRoot = Path.GetDirectoryName(full);
                }

                if (stagingRoot != null && string.Equals(Path.GetFileName(stagingRoot), "temp", StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(stagingRoot) && !Directory.EnumerateFileSystemEntries(stagingRoot).Any())
                {
                    Directory.Delete(stagingRoot);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Deletes the directory only if it exists and contains no files or subdirectories.</summary>
        public static bool DeleteEmptyDirectory(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                    return false;

                if (!Directory.EnumerateFileSystemEntries(path).Any())
                {
                    Directory.Delete(path, recursive: false);
                    return true;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return false;
        }

        /// <summary>
        /// Returns false for paths that must never be deleted recursively. Guards uninstall,
        /// "remove download" and any future cleanup against a corrupted library.json or a
        /// wrong install path (drive roots, user profile, Windows, the launcher itself).
        /// When <paramref name="managedRoot"/> is provided, the path must be strictly inside
        /// it — the library root itself is never removable.
        /// </summary>
        public static bool IsSafeToDeleteTree(string? path, string? managedRoot = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string full;
            try
            {
                full = Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }

            // "C:" / "C:\" and anything shorter than a real folder.
            if (full.Length <= 3)
                return false;

            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)
                || string.Equals(full, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                return false;

            // Never delete the launcher folder or any of its parents.
            var appDirectory = AppDomain.CurrentDomain.BaseDirectory
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (IsSameOrInside(appDirectory, full))
                return false;

            var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (IsSameOrInside(full, windowsDirectory) || IsSameOrInside(full, systemDirectory))
                return false;

            // These folders themselves are dangerous, but installing a game into a
            // subfolder is legitimate, so only an exact match is refused.
            var exactProtectedFolders = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86)
            };

            foreach (var protectedFolder in exactProtectedFolders)
            {
                if (string.IsNullOrWhiteSpace(protectedFolder))
                    continue;

                var normalized = protectedFolder
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(full, normalized, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (!string.IsNullOrWhiteSpace(managedRoot))
            {
                try
                {
                    var managed = Path.GetFullPath(managedRoot)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                    if (string.Equals(full, managed, StringComparison.OrdinalIgnoreCase)
                        || !IsSameOrInside(full, managed))
                        return false;
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsSameOrInside(string path, string parent)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(parent))
                return false;

            var normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedParent = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return string.Equals(normalizedPath, normalizedParent, StringComparison.OrdinalIgnoreCase)
                || normalizedPath.StartsWith(normalizedParent + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Finds a file by a relative path next to the application or in one of its
        /// parent folders. Replaces the old absolute "d:\launcher\..." development
        /// fallbacks, so a published build works from any location.
        /// </summary>
        public static string? FindFileWalkingUp(string relativePath, int maxParentLevels = 6)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return null;

            if (Path.IsPathRooted(relativePath))
                return File.Exists(relativePath) ? Path.GetFullPath(relativePath) : null;

            var candidates = new List<string>();
            try
            {
                candidates.Add(Path.Combine(Environment.CurrentDirectory, relativePath));
            }
            catch
            {
                // CurrentDirectory can be unavailable; the app folder is the main source.
            }

            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            for (var level = 0; level < maxParentLevels && directory != null; level++)
            {
                candidates.Add(Path.Combine(directory.FullName, relativePath));
                directory = directory.Parent;
            }

            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate))
                        return Path.GetFullPath(candidate);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // Skip malformed candidates.
                }
            }

            return null;
        }

        /// <summary>Built-in catalog path (next to the executable or walking up to repo root).</summary>
        public static string BuiltInCatalogPath
        {
            get
            {
                var candidate1 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "games.json");
                if (File.Exists(candidate1)) return candidate1;

                var candidate2 = Path.Combine(Directory.GetCurrentDirectory(), "data", "games.json");
                if (File.Exists(candidate2)) return candidate2;

                var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                while (dir != null && dir.Exists)
                {
                    var check = Path.Combine(dir.FullName, "data", "games.json");
                    if (File.Exists(check)) return check;
                    dir = dir.Parent;
                }

                return candidate1;
            }
        }

        public static void EnsureDirectory(string path)
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
        }

        /// <summary>Format file size in human-readable form.</summary>
        public static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.#} {sizes[order]}";
        }

        /// <summary>Format seconds into a clean human-readable duration.</summary>
        public static string FormatTimeRemaining(long seconds)
        {
            if (seconds <= 0) return "—";
            if (seconds > 86400 * 30) return "∞"; // Over 30 days is effectively infinite/stalled

            var ts = TimeSpan.FromSeconds(seconds);
            if (ts.TotalDays >= 1)
                return Localizer.T("TimeRemainingDaysFormat", "{0} д {1} ч")
                    .Replace("{0}", ((int)ts.TotalDays).ToString()).Replace("{1}", ts.Hours.ToString());
            if (ts.TotalHours >= 1)
                return Localizer.T("TimeRemainingHoursFormat", "{0} ч {1} мин")
                    .Replace("{0}", ((int)ts.TotalHours).ToString()).Replace("{1}", ts.Minutes.ToString());
            if (ts.TotalMinutes >= 1)
                return Localizer.T("TimeRemainingMinutesFormat", "{0} мин {1} с")
                    .Replace("{0}", ((int)ts.TotalMinutes).ToString()).Replace("{1}", ts.Seconds.ToString());

            return Localizer.T("TimeRemainingSecondsFormat", "{0} с")
                .Replace("{0}", ((int)ts.TotalSeconds).ToString());
        }

        /// <summary>Strip characters that are invalid in file/folder names.</summary>
        public static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim(' ', '.', '_');
        }
    }
}
