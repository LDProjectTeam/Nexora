using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vellum.Core.Helpers;
using Vellum.Core.Models;
using Vellum.Core.Services;
using SkiaSharp;
using Xunit;

namespace Vellum.Tests;

public class ArtworkStorageTests
{
    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VellumArtworkTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Windows can still hold a handle on a freshly written image (antivirus, indexer)
    /// for a moment, which makes a plain recursive delete flaky.
    /// </summary>
    private static void DeleteTempDirectory(string directory)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 9)
                    return; // best effort in tests
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }

    private static Task<string> CreateSourceImageAsync(string path, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var pixels = new SKColor[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
                pixels[y * width + x] = new SKColor((byte)(x % 256), (byte)(y % 256), 90);
        }
        bitmap.Pixels = pixels;

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        using var stream = File.Create(path);
        data.SaveTo(stream);
        return Task.FromResult(path);
    }

    [Fact]
    public async Task Banner_Is_Downscaled_And_Stored_As_Webp()
    {
        var directory = CreateTempDirectory();
        try
        {
            var source = await CreateSourceImageAsync(Path.Combine(directory, "asset.jpg"), 2560, 900);
            var targetBase = Path.Combine(directory, "123_library_hero_v1");

            var result = await ArtworkImageOptimizer.ConvertToWebpAsync(
                source,
                targetBase,
                ArtworkImageOptimizer.DetectKind(targetBase));

            Assert.Equal(targetBase + ".webp", result);
            Assert.True(File.Exists(result));

            using var converted = SKBitmap.Decode(result);
            Assert.Equal(ArtworkImageOptimizer.BannerMaxWidth, converted.Width);
            Assert.True(converted.Height < 900);
            Assert.True(new FileInfo(result!).Length < new FileInfo(source).Length);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task Cover_Keeps_Smaller_Resolution()
    {
        var directory = CreateTempDirectory();
        try
        {
            var source = await CreateSourceImageAsync(Path.Combine(directory, "cover.jpg"), 460, 215);
            var targetBase = Path.Combine(directory, "570940_cover_v3");

            var result = await ArtworkImageOptimizer.ConvertToWebpAsync(
                source,
                targetBase,
                ArtworkImageOptimizer.DetectKind(targetBase));

            Assert.NotNull(result);
            using var converted = SKBitmap.Decode(result);
            Assert.Equal(460, converted.Width);
            Assert.Equal(215, converted.Height);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task Maintenance_Migrates_Legacy_Jpeg_And_Updates_Metadata()
    {
        var directory = CreateTempDirectory();
        try
        {
            var jpegPath = await CreateSourceImageAsync(Path.Combine(directory, "42_banner_v3.jpg"), 2200, 700);
            var metadataPath = Path.Combine(directory, "cache_entry_v21.json");
            await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(new GameArtwork
            {
                CoverPath = jpegPath,
                BannerPath = string.Empty,
                MatchedTitle = "Test Game"
            }));

            var report = await ArtworkCacheMaintenance.RunForDirectoryAsync(directory, 200L * 1024 * 1024);

            Assert.False(report.Skipped);
            Assert.Equal(1, report.ConvertedFiles);

            var updated = JsonSerializer.Deserialize<GameArtwork>(await File.ReadAllTextAsync(metadataPath));
            Assert.NotNull(updated);
            Assert.EndsWith(".webp", updated!.CoverPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(updated.CoverPath));

            using var converted = SKBitmap.Decode(updated.CoverPath);
            Assert.Equal(ArtworkImageOptimizer.BannerMaxWidth, converted.Width);

            // The original is kept for one session so already-loaded cards keep working.
            Assert.True(File.Exists(jpegPath));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task Maintenance_Removes_Old_Orphaned_Images_And_Temp_Files()
    {
        var directory = CreateTempDirectory();
        try
        {
            var orphan = Path.Combine(directory, "old_orphan_v1.jpg");
            var temporary = Path.Combine(directory, "half.download");
            var recent = Path.Combine(directory, "recent_orphan.jpg");
            await CreateSourceImageAsync(orphan, 320, 180);
            await CreateSourceImageAsync(recent, 320, 180);
            await File.WriteAllBytesAsync(temporary, new byte[] { 1, 2, 3 });

            var old = DateTime.UtcNow.AddDays(-3);
            foreach (var path in new[] { orphan, temporary })
            {
                File.SetLastWriteTimeUtc(path, old);
                File.SetCreationTimeUtc(path, old);
            }

            var report = await ArtworkCacheMaintenance.RunForDirectoryAsync(directory, 200L * 1024 * 1024);

            Assert.False(report.Skipped);
            Assert.False(File.Exists(orphan));
            Assert.False(File.Exists(temporary));
            Assert.True(File.Exists(recent), "Recent files may belong to an in-flight download.");
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task Maintenance_Evicts_Oldest_Entries_When_Cache_Is_Too_Large()
    {
        var directory = CreateTempDirectory();
        try
        {
            for (var index = 0; index < 4; index++)
            {
                var image = await CreateSourceImageAsync(Path.Combine(directory, $"asset_{index}.jpg"), 400, 200);
                var metadataPath = Path.Combine(directory, $"entry_{index}.json");
                await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(new GameArtwork
                {
                    CoverPath = image,
                    BannerPath = string.Empty
                }));
                var timestamp = DateTime.UtcNow.AddDays(-10 + index);
                File.SetLastWriteTimeUtc(metadataPath, timestamp);
            }

            var total = ArtworkCacheMaintenance.GetDirectorySize(directory);
            var report = await ArtworkCacheMaintenance.RunForDirectoryAsync(
                directory,
                Math.Max(1, total - 1),
                default);

            Assert.True(report.EvictedEntries > 0);
            Assert.True(report.BytesAfter < report.BytesBefore);
            Assert.True(Directory.GetFiles(directory, "*.json").Length < 4);        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }
}
