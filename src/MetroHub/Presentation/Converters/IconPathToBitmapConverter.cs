using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace MetroHub.Presentation.Converters;

/// <summary>
/// High-performance icon loader converter for tile and sidebar icons.
/// Clamps DecodePixelWidth to prevent uncompressed bitmaps from exceeding the 85 KB LOH threshold,
/// freezes bitmaps for zero-overhead DirectX rendering, and caches instances across identical shortcuts.
/// </summary>
public sealed class IconPathToBitmapConverter : IValueConverter
{
    public const int DefaultDecodePixelWidth = 96;
    private const int MaxCachedIcons = 250;

    private static readonly ConcurrentDictionary<string, BitmapImage> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    public static BitmapImage? LoadClampedIcon(string? path, int decodeWidth = DefaultDecodePixelWidth)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        string cacheKey = $"{path}|{decodeWidth}";
        if (_iconCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        try
        {
            Uri uri;
            if (path.StartsWith("pack://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                uri = new Uri(path, UriKind.Absolute);
            }
            else
            {
                if (!File.Exists(path)) return null;
                uri = new Uri(path, UriKind.Absolute);
            }

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            if (decodeWidth > 0)
            {
                bmp.DecodePixelWidth = decodeWidth;
            }
            bmp.EndInit();
            bmp.Freeze();

            if (_iconCache.Count >= MaxCachedIcons)
            {
                var keysToRemove = _iconCache.Keys.Take(MaxCachedIcons / 5).ToList();
                foreach (var k in keysToRemove)
                {
                    _iconCache.TryRemove(k, out _);
                }
            }

            _iconCache[cacheKey] = bmp;
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        int decodeWidth = DefaultDecodePixelWidth;
        if (parameter is int intParam && intParam > 0)
        {
            decodeWidth = intParam;
        }
        else if (parameter is string strParam && int.TryParse(strParam, out int parsed) && parsed > 0)
        {
            decodeWidth = parsed;
        }

        return LoadClampedIcon(path, decodeWidth);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
