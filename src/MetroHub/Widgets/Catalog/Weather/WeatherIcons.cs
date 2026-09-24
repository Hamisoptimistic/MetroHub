using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Media.Imaging;

namespace MetroHub.Widgets.Catalog.Weather;

/// <summary>
/// Shared, frozen BitmapImage cache for Meteocons "Fill" weather icons.
/// One bitmap per icon, reused across all Image controls with zero GC churn on refresh.
/// </summary>
public static class WeatherIcons
{
    private static readonly Dictionary<string, BitmapImage> Cache = new(StringComparer.Ordinal);
    private static readonly object SyncLock = new();

    public static BitmapImage Get(string? iconName)
    {
        if (string.IsNullOrWhiteSpace(iconName))
            return Get("not-available");

        lock (SyncLock)
        {
            if (Cache.TryGetValue(iconName, out var cached))
                return cached;

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(
                    $"pack://application:,,,/MetroHub;component/Assets/Weather/Glassmorphic/{iconName}.png",
                    UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad; // load eagerly, not on first render
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.DecodePixelWidth = 200; // Optimal 200x200 downsampling (saves ~85% bitmap memory vs raw 512x512)
                bmp.EndInit();
                bmp.Freeze(); // shareable across threads, skips change-notification overhead

                Cache[iconName] = bmp;
                return bmp;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WeatherIcons] Failed to load icon '{iconName}': {ex.Message}");
                if (iconName != "not-available")
                {
                    return Get("not-available");
                }
                throw;
            }
        }
    }
}
