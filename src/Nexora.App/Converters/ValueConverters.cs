using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Nexora.Core.Models;

namespace Nexora.App.Converters
{
    public class BoolToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool b = value is bool val && val;
            if (Invert) b = !b;
            return b ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Visibility v && v == Visibility.Visible;
    }

    public class StateToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is InstallState state)
            {
                return state switch
                {
                    InstallState.Installed => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981")),      // Green
                    InstallState.Downloading => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B82F6")),    // Blue
                    InstallState.Installing => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8B5CF6")),     // Purple
                    InstallState.Verifying => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EC4899")),      // Pink
                    InstallState.UpdateAvailable => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B")),// Amber
                    InstallState.Updating => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B")),       // Amber
                    InstallState.Paused => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6B7280")),         // Gray
                    InstallState.Error => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444")),          // Red
                    _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CA3AF"))                            // Muted
                };
            }
            return new SolidColorBrush(Colors.Gray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class NullToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isNotNull = value != null;
            if (Invert) isNotNull = !isNotNull;
            return isNotNull ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class StringToImageSourceConverter : IValueConverter
    {
        // Artwork bitmaps are decoded at screen resolution (not full size) and cached
        // in a bounded LRU. The old implementation cached every decode forever, which
        // grew with every game the user browsed.
        private const long MaxCacheBytes = 96L * 1024 * 1024;
        private const int MaxCacheEntries = 180;

        private static readonly object CacheLock = new();
        private static readonly Dictionary<string, CacheEntry> ImageCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly LinkedList<string> CacheOrder = new();
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ImageSource, string> SourceKeys = new();

        private static long _cacheBytes;

        private sealed record CacheEntry(ImageSource Source, long Bytes, LinkedListNode<string> Node);

        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string path || string.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                var decodeWidth = string.Equals(parameter?.ToString(), "Card", StringComparison.OrdinalIgnoreCase)
                    ? 400
                    : 1280;
                if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return CreateRemoteBitmapImage(new Uri(path), decodeWidth);
                }

                var fullPath = ResolveLocalPath(path);
                if (fullPath == null)
                    return null;

                var cacheKey = BuildCacheKey(fullPath, decodeWidth);
                var source = GetOrAddLocalImage(fullPath, decodeWidth, cacheKey);
                if (source != null)
                    RememberKey(source, new Uri(fullPath).AbsoluteUri);
                return source;
            }
            catch
            {
                // Return null if the image is unavailable or invalid.
            }
            return null;
        }

        /// <summary>
        /// Returns whether <paramref name="source"/> was produced for <paramref name="expectedKey"/>.
        /// WebP artwork is decoded into a plain <see cref="BitmapSource"/> without a
        /// <see cref="BitmapImage.UriSource"/>, so the binding key is tracked separately.
        /// </summary>
        public static bool MatchesSourceKey(ImageSource? source, string expectedKey)
        {
            if (source == null || string.IsNullOrWhiteSpace(expectedKey))
                return false;

            if (source is System.Windows.Media.Imaging.BitmapImage { UriSource: { } uri }
                && string.Equals(uri.AbsoluteUri, expectedKey, StringComparison.OrdinalIgnoreCase))
                return true;

            return SourceKeys.TryGetValue(source, out var key)
                && string.Equals(key, expectedKey, StringComparison.OrdinalIgnoreCase);
        }

        private static string? ResolveLocalPath(string path)
        {
            if (System.IO.File.Exists(path))
                return System.IO.Path.GetFullPath(path);

            var baseRelative = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
            if (System.IO.File.Exists(baseRelative))
                return System.IO.Path.GetFullPath(baseRelative);

            return null;
        }

        private static string BuildCacheKey(string fullPath, int decodeWidth)
        {
            long stamp = 0;
            try { stamp = System.IO.File.GetLastWriteTimeUtc(fullPath).Ticks; }
            catch { }
            return $"{fullPath}|{decodeWidth}|{stamp}";
        }

        private static ImageSource? GetOrAddLocalImage(string fullPath, int decodeWidth, string cacheKey)
        {
            lock (CacheLock)
            {
                if (ImageCache.TryGetValue(cacheKey, out var cached))
                {
                    CacheOrder.Remove(cached.Node);
                    CacheOrder.AddFirst(cached.Node);
                    return cached.Source;
                }
            }

            // Decoding happens outside the lock; a duplicate decode is harmless.
            var image = IsWebp(fullPath)
                ? DecodeWebp(fullPath, decodeWidth)
                : CreateBitmapImage(new Uri(fullPath), decodeWidth);
            if (image == null)
                return null;

            var bytes = EstimateBytes(image);
            lock (CacheLock)
            {
                if (ImageCache.TryGetValue(cacheKey, out var cached))
                    return cached.Source;

                var node = CacheOrder.AddFirst(cacheKey);
                ImageCache[cacheKey] = new CacheEntry(image, bytes, node);
                _cacheBytes += bytes;

                while ((_cacheBytes > MaxCacheBytes || ImageCache.Count > MaxCacheEntries)
                       && CacheOrder.Last is { } oldest)
                {
                    var key = oldest.Value;
                    CacheOrder.RemoveLast();
                    if (ImageCache.Remove(key, out var entry))
                        _cacheBytes -= entry.Bytes;
                }
            }

            return image;
        }

        private static bool IsWebp(string path)
            => path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);

        /// <summary>Decodes a WebP file at the requested width.</summary>
        private static ImageSource? DecodeWebp(string path, int decodeWidth)
        {
            using var decoded = SkiaSharp.SKBitmap.Decode(path);
            if (decoded == null || decoded.Width <= 0 || decoded.Height <= 0)
                return null;

            var width = decodeWidth > 0 ? Math.Min(decodeWidth, decoded.Width) : decoded.Width;
            var height = Math.Max(1, (int)Math.Round(decoded.Height * (width / (double)decoded.Width)));

            using var target = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(
                width,
                height,
                SkiaSharp.SKColorType.Bgra8888,
                SkiaSharp.SKAlphaType.Premul));
            using (var canvas = new SkiaSharp.SKCanvas(target))
            {
                using var paint = new SkiaSharp.SKPaint
                {
                    FilterQuality = SkiaSharp.SKFilterQuality.High,
                    IsAntialias = true
                };
                canvas.Clear(SkiaSharp.SKColors.Transparent);
                canvas.DrawBitmap(decoded, new SkiaSharp.SKRect(0, 0, width, height), paint);
                canvas.Flush();
            }

            var stride = target.RowBytes;
            var pixels = new byte[stride * height];
            Marshal.Copy(target.GetPixels(), pixels, 0, pixels.Length);

            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
                width,
                height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                pixels,
                stride);
            bitmap.Freeze();
            return bitmap;
        }

        private static void RememberKey(ImageSource source, string key)
            => SourceKeys.AddOrUpdate(source, key);

        private static long EstimateBytes(ImageSource source)
            => Math.Max(1, (long)source.Width * (long)source.Height * 4 + 512);

        private static ImageSource CreateBitmapImage(Uri uri, int decodeWidth)
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.UriSource = uri;
            image.DecodePixelWidth = decodeWidth;
            // OnLoad releases the file handle so the cache maintenance can rewrite or
            // delete artwork while the launcher is running.
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }

        /// <summary>Remote catalog images must stay asynchronous, so they are not loaded eagerly.</summary>
        private static ImageSource CreateRemoteBitmapImage(Uri uri, int decodeWidth)
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.UriSource = uri;
            image.DecodePixelWidth = decodeWidth;
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnDemand;
            image.EndInit();
            return image;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
