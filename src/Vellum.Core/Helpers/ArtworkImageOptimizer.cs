using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace Vellum.Core.Helpers
{
    /// <summary>How an artwork file is going to be displayed, which controls the stored resolution.</summary>
    public enum ArtworkImageKind
    {
        /// <summary>Card / poster artwork (a few hundred pixels wide on screen).</summary>
        Cover = 0,

        /// <summary>Wide hero / banner artwork used by the featured row and the details header.</summary>
        Banner = 1
    }

    /// <summary>
    /// Converts downloaded artwork to WebP and downscales it to the resolution the UI
    /// actually needs. Steam ships 3840x1240 heroes and multi-megabyte JPEGs for a
    /// 1280px hero; re-encoding keeps the cache several times smaller and speeds up
    /// decoding. Files are written through a temporary file and moved into place so a
    /// cancelled/interrupted conversion never leaves a half-written image behind.
    /// </summary>
    public static class ArtworkImageOptimizer
    {
        /// <summary>Maximum stored width for poster/card artwork.</summary>
        public const int CoverMaxWidth = 640;

        /// <summary>Maximum stored width for hero/banner artwork.</summary>
        public const int BannerMaxWidth = 1600;

        public const int CoverQuality = 82;
        public const int BannerQuality = 78;

        // Decoding a 4K hero costs tens of MB of native memory. Artwork requests are
        // already parallel, so conversions are limited to a small number at a time.
        private static readonly SemaphoreSlim ConversionGate = new(2, 2);

        private static readonly string[] BannerMarkers = { "banner", "hero", "backdrop", "wide" };

        public static ArtworkImageKind DetectKind(string nameOrPath)
        {
            var name = Path.GetFileNameWithoutExtension(nameOrPath);
            foreach (var marker in BannerMarkers)
            {
                if (name.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return ArtworkImageKind.Banner;
            }

            return ArtworkImageKind.Cover;
        }

        public static int GetMaxWidth(ArtworkImageKind kind)
            => kind == ArtworkImageKind.Banner ? BannerMaxWidth : CoverMaxWidth;

        /// <summary>
        /// Decodes <paramref name="sourcePath"/> and writes an optimized WebP file to
        /// <paramref name="targetPathWithoutExtension"/> + ".webp".
        /// Returns the WebP path on success, or <c>null</c> when the source is not a
        /// decodable image or re-encoding would make a compact source larger.
        /// </summary>
        public static async Task<string?> ConvertToWebpAsync(
            string sourcePath,
            string targetPathWithoutExtension,
            ArtworkImageKind kind,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                return null;

            await ConversionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return ConvertToWebpCore(sourcePath, targetPathWithoutExtension, kind, cancellationToken);
            }
            finally
            {
                ConversionGate.Release();
            }
        }

        private static string? ConvertToWebpCore(
            string sourcePath,
            string targetPathWithoutExtension,
            ArtworkImageKind kind,
            CancellationToken cancellationToken)
        {
            var webpPath = targetPathWithoutExtension + ".webp";
            var temporaryPath = webpPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Already converted from this exact source: keep the existing file.
                // (The same image is often referenced as both cover and banner.)
                try
                {
                    if (File.Exists(webpPath)
                        && new FileInfo(webpPath).Length > 0
                        && File.GetLastWriteTimeUtc(webpPath) >= File.GetLastWriteTimeUtc(sourcePath))
                        return webpPath;
                }
                catch
                {
                    // Fall through and convert.
                }

                using var source = SKBitmap.Decode(sourcePath);
                if (source == null || source.Width <= 0 || source.Height <= 0)
                    return null;

                var maxWidth = GetMaxWidth(kind);
                var targetWidth = Math.Min(source.Width, maxWidth);
                var targetHeight = Math.Max(1, (int)Math.Round(source.Height * (targetWidth / (double)source.Width)));

                using var resized = new SKBitmap(new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
                using (var canvas = new SKCanvas(resized))
                {
                    using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
                    canvas.Clear(SKColors.Transparent);
                    canvas.DrawBitmap(source, new SKRect(0, 0, targetWidth, targetHeight), paint);
                    canvas.Flush();
                }

                cancellationToken.ThrowIfCancellationRequested();

                using var image = SKImage.FromBitmap(resized);
                using var data = image.Encode(
                    SKEncodedImageFormat.Webp,
                    kind == ArtworkImageKind.Banner ? BannerQuality : CoverQuality);
                if (data == null || data.Size == 0)
                    return null;

                using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    data.SaveTo(output);
                }

                if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
                    return null;

                // Keep the smaller of the two when the source is already a compact JPEG
                // (tiny Steam "capsule_231x87" images can grow when re-encoded).
                if (new FileInfo(temporaryPath).Length >= new FileInfo(sourcePath).Length
                    && IsCompactSource(sourcePath))
                {
                    return null;
                }

                File.Move(temporaryPath, webpPath, overwrite: true);
                return webpPath;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }

        /// <summary>
        /// Converts an already cached image in place. Returns the path the caller should
        /// reference (the WebP file when conversion helped, otherwise the original).
        /// </summary>
        public static async Task<string> OptimizeCachedFileAsync(
            string sourcePath,
            ArtworkImageKind kind,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                return sourcePath;

            if (string.Equals(Path.GetExtension(sourcePath), ".webp", StringComparison.OrdinalIgnoreCase))
                return sourcePath;

            var targetBase = Path.Combine(
                Path.GetDirectoryName(sourcePath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(sourcePath));
            var webpPath = await ConvertToWebpAsync(sourcePath, targetBase, kind, cancellationToken).ConfigureAwait(false);
            return webpPath ?? sourcePath;
        }

        private static bool IsCompactSource(string path)
        {
            var extension = Path.GetExtension(path);
            return string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".webp", StringComparison.OrdinalIgnoreCase);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best effort only.
            }
        }
    }
}
