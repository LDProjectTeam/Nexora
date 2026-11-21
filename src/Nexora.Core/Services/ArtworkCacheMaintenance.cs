using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexora.Core.Helpers;
using Nexora.Core.Models;

namespace Nexora.Core.Services
{
    public sealed record ArtworkCacheMaintenanceReport(
        bool Skipped,
        int ConvertedFiles,
        int RemovedFiles,
        int EvictedEntries,
        long BytesBefore,
        long BytesAfter);

    /// <summary>
    /// Keeps <c>%APPDATA%\Nexora\artwork</c> small and tidy:
    /// <list type="bullet">
    /// <item>rewrites images cached as full-size JPEG/PNG by older versions into downscaled WebP;</item>
    /// <item>drops images that no metadata entry points at (old cache naming versions);</item>
    /// <item>removes stale negative-cache markers and half-written downloads;</item>
    /// <item>enforces a total size cap by evicting the oldest metadata entries.</item>
    /// </list>
    /// Safe to run on every start: it is a no-op when there is nothing to do.
    /// </summary>
    public static class ArtworkCacheMaintenance
    {
        public const long DefaultMaxCacheBytes = 200L * 1024 * 1024;

        // Do not touch files that a download or conversion may still be writing.
        private static readonly TimeSpan MinimumFileAge = TimeSpan.FromHours(1);
        private static readonly TimeSpan OrphanMinimumAge = TimeSpan.FromHours(12);
        private static readonly TimeSpan MissRetention = TimeSpan.FromDays(7);
        private static readonly TimeSpan DanglingMetadataRetention = TimeSpan.FromDays(30);
        private static readonly TimeSpan MaximumEntryAge = TimeSpan.FromDays(150);

        private static readonly SemaphoreSlim Gate = new(1, 1);

        private static readonly string[] ImageExtensions =
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tga"
        };

        public static async Task<ArtworkCacheMaintenanceReport> RunAsync(
            long maxCacheBytes = DefaultMaxCacheBytes,
            CancellationToken cancellationToken = default)
        {
            var directory = PathHelper.ArtworkCacheDir;
            if (!Directory.Exists(directory))
                return new ArtworkCacheMaintenanceReport(false, 0, 0, 0, 0, 0);

            return await RunForDirectoryAsync(directory, maxCacheBytes, cancellationToken).ConfigureAwait(false);
        }

        public static async Task<ArtworkCacheMaintenanceReport> RunForDirectoryAsync(
            string directory,
            long maxCacheBytes,
            CancellationToken cancellationToken = default)
        {
            if (!await Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                return new ArtworkCacheMaintenanceReport(true, 0, 0, 0, 0, 0);

            try
            {
                return await RunCoreAsync(directory, maxCacheBytes, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }

        private static async Task<ArtworkCacheMaintenanceReport> RunCoreAsync(
            string directory,
            long maxCacheBytes,
            CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            var before = GetDirectorySize(directory);
            var converted = 0;
            var removed = 0;

            var metadataFiles = Directory.EnumerateFiles(directory, "*.json").ToList();
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Originals converted during this run stay on disk until the next start:
            // a card may already hold the old path in memory for the current session.
            var convertedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var metadataPath in metadataFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsOlderThan(metadataPath, now, MaximumEntryAge))
                {
                    RemoveFile(metadataPath, ref removed);
                    RemoveFile(metadataPath + ".miss", ref removed);
                    continue;
                }

                GameArtwork? artwork;
                try
                {
                    artwork = JsonSerializer.Deserialize<GameArtwork>(
                        await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    artwork = null;
                }

                if (artwork == null)
                {
                    if (IsOlderThan(metadataPath, now, DanglingMetadataRetention))
                    {
                        RemoveFile(metadataPath, ref removed);
                        RemoveFile(metadataPath + ".miss", ref removed);
                    }
                    continue;
                }

                var coverPath = artwork.CoverPath ?? string.Empty;
                var bannerPath = artwork.BannerPath ?? string.Empty;

                var optimizedCover = await MigrateAsync(coverPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(optimizedCover, coverPath, StringComparison.OrdinalIgnoreCase))
                {
                    converted++;
                    AddReference(convertedSources, coverPath);
                    coverPath = optimizedCover;
                }

                var optimizedBanner = string.Equals(bannerPath, coverPath, StringComparison.OrdinalIgnoreCase)
                    ? optimizedCover
                    : await MigrateAsync(bannerPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(optimizedBanner, bannerPath, StringComparison.OrdinalIgnoreCase))
                {
                    converted++;
                    AddReference(convertedSources, bannerPath);
                    bannerPath = optimizedBanner;
                }

                if (!string.Equals(coverPath, artwork.CoverPath, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(bannerPath, artwork.BannerPath, StringComparison.OrdinalIgnoreCase))
                {
                    var updated = new GameArtwork
                    {
                        CoverPath = coverPath,
                        BannerPath = bannerPath,
                        OfficialReleaseDate = artwork.OfficialReleaseDate ?? string.Empty,
                        MatchedTitle = artwork.MatchedTitle ?? string.Empty
                    };

                    try
                    {
                        var json = JsonSerializer.Serialize(updated);
                        var temporary = metadataPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);
                        File.Move(temporary, metadataPath, overwrite: true);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // Keep the old metadata; the image still works through its old path.
                    }
                }

                AddReference(referenced, coverPath);
                AddReference(referenced, bannerPath);
            }

            // Remove images that no metadata entry references (these are usually
            // leftovers from older cache naming versions such as _v1 -> _v3).
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var extension = Path.GetExtension(file);
                var isTemporary = extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".download", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
                if (isTemporary)
                {
                    if (IsOlderThan(file, now, MinimumFileAge))
                        RemoveFile(file, ref removed);
                    continue;
                }

                if (extension.Equals(".miss", StringComparison.OrdinalIgnoreCase))
                {
                    if (IsOlderThan(file, now, MissRetention))
                        RemoveFile(file, ref removed);
                    continue;
                }

                if (!IsImageFile(extension) || referenced.Contains(Path.GetFullPath(file)))
                    continue;

                if (convertedSources.Contains(Path.GetFullPath(file)))
                    continue;

                if (IsOlderThan(file, now, OrphanMinimumAge))
                    RemoveFile(file, ref removed);
            }

            // Enforce the total size cap by dropping the least recently written
            // metadata entries and their images.
            var evicted = 0;
            var total = GetDirectorySize(directory);
            if (total > maxCacheBytes)
            {
                evicted = await EvictOldestEntriesAsync(directory, maxCacheBytes, cancellationToken).ConfigureAwait(false);
                total = GetDirectorySize(directory);
            }

            return new ArtworkCacheMaintenanceReport(
                false,
                converted,
                removed,
                evicted,
                before,
                total);
        }

        private static async Task<string> MigrateAsync(string path, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return path;

            if (Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase))
                return path;

            var optimized = await ArtworkImageOptimizer.OptimizeCachedFileAsync(
                path,
                ArtworkImageOptimizer.DetectKind(path),
                cancellationToken).ConfigureAwait(false);
            return optimized;
        }

        private static async Task<int> EvictOldestEntriesAsync(
            string directory,
            long maxCacheBytes,
            CancellationToken cancellationToken)
        {
            var entries = new List<(string JsonPath, DateTime LastWrite, long Size, List<string> Images)>();
            foreach (var jsonPath in Directory.EnumerateFiles(directory, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var artwork = JsonSerializer.Deserialize<GameArtwork>(
                        await File.ReadAllTextAsync(jsonPath, cancellationToken).ConfigureAwait(false));
                    var images = new List<string>();
                    if (artwork != null)
                    {
                        AddExistingImage(images, artwork.CoverPath);
                        AddExistingImage(images, artwork.BannerPath);
                    }

                    var size = SafeLength(jsonPath) + SafeLength(jsonPath + ".miss");
                    foreach (var image in images)
                        size += SafeLength(image);

                    entries.Add((jsonPath, File.GetLastWriteTimeUtc(jsonPath), size, images));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Ignore unreadable entries; the orphan pass handles their files later.
                }
            }

            entries.Sort((left, right) => left.LastWrite.CompareTo(right.LastWrite));
            var total = GetDirectorySize(directory);
            var target = Math.Max(1, (long)(maxCacheBytes * 0.8));
            var excess = total - target;
            var evicted = 0;

            foreach (var entry in entries)
            {
                if (excess <= 0)
                    break;

                try
                {
                    File.Delete(entry.JsonPath);
                }
                catch
                {
                    continue;
                }

                var ignored = 0;
                RemoveFile(entry.JsonPath + ".miss", ref ignored);
                excess -= entry.Size;
                evicted++;
            }

            if (evicted == 0)
                return 0;

            // Recompute references after removing metadata so images shared by a
            // remaining entry (same Steam AppID) are never deleted.
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var jsonPath in Directory.EnumerateFiles(directory, "*.json"))
            {
                try
                {
                    var artwork = JsonSerializer.Deserialize<GameArtwork>(
                        await File.ReadAllTextAsync(jsonPath, cancellationToken).ConfigureAwait(false));
                    if (artwork == null)
                        continue;
                    AddReference(referenced, artwork.CoverPath);
                    AddReference(referenced, artwork.BannerPath);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Ignore unreadable entries.
                }
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var extension = Path.GetExtension(file);
                if (!IsImageFile(extension) || referenced.Contains(Path.GetFullPath(file)))
                    continue;

                // The cap is a hard limit: images belonging only to the evicted
                // entries are removed even when fresh, so the space is actually freed.
                try { File.Delete(file); }
                catch { }
            }

            return evicted;
        }

        private static void AddExistingImage(ICollection<string> images, string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;

            try
            {
                images.Add(Path.GetFullPath(path));
            }
            catch
            {
                // Ignore malformed paths.
            }
        }

        private static void AddReference(ISet<string> referenced, string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;

            try
            {
                referenced.Add(Path.GetFullPath(path));
            }
            catch
            {
                // Ignore malformed paths.
            }
        }

        private static bool IsImageFile(string extension)
            => ImageExtensions.Any(candidate =>
                string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase));

        private static bool IsOlderThan(string path, DateTime now, TimeSpan age)
        {
            try
            {
                return now - File.GetLastWriteTimeUtc(path) > age
                    && now - File.GetCreationTimeUtc(path) > age;
            }
            catch
            {
                return false;
            }
        }

        private static void RemoveFile(string path, ref int removed)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                if (!File.Exists(path))
                    return;
                File.Delete(path);
                removed++;
            }
            catch
            {
                // The file may be open; try again on the next start.
            }
        }

        private static long SafeLength(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        public static long GetDirectorySize(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return 0;

            long total = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    total += SafeLength(file);
            }
            catch
            {
                // Best effort.
            }

            return total;
        }
    }
}
