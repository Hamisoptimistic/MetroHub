using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace MetroHub.Core.Services;

/// <summary>
/// Provides asynchronous, non-blocking daily wallpaper acquisition and disk caching
/// for Bing Wallpaper of the Day and Windows Spotlight (Peapix).
/// All network and image decoding occurs off the main UI thread.
/// </summary>
public sealed class DailyWallpaperService
{
    private static readonly Lazy<DailyWallpaperService> _instance = new(() => new DailyWallpaperService());
    public static DailyWallpaperService Instance => _instance.Value;

    private static readonly HttpClient _httpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(10)
    })
    {
        Timeout = TimeSpan.FromSeconds(25)
    };

    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MetroHub",
        "WallpapersCache");

    static DailyWallpaperService()
    {
        try
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36 MetroHub/2.0");
        }
        catch { }
    }

    private DailyWallpaperService()
    {
        EnsureCacheDirectory();
    }

    private static void EnsureCacheDirectory()
    {
        try
        {
            if (!Directory.Exists(CacheDirectory))
            {
                Directory.CreateDirectory(CacheDirectory);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DailyWallpaperService] Failed to create cache directory: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the current user's culture market name (e.g. "en-US", "en-IN", "de-DE").
    /// </summary>
    public static string GetCurrentMarket()
    {
        try
        {
            var culture = CultureInfo.CurrentCulture;
            if (!string.IsNullOrWhiteSpace(culture.Name) && culture.Name.Contains('-'))
            {
                return culture.Name;
            }
        }
        catch { }
        return "en-US";
    }

    /// <summary>
    /// Returns the current user's two-letter ISO country code (e.g. "us", "in", "gb").
    /// </summary>
    public static string GetCurrentCountryCode()
    {
        try
        {
            var region = RegionInfo.CurrentRegion;
            if (!string.IsNullOrWhiteSpace(region.TwoLetterISORegionName))
            {
                return region.TwoLetterISORegionName.ToLowerInvariant();
            }
        }
        catch { }
        return "us";
    }

    /// <summary>
    /// Asynchronously retrieves today's Bing 4K wallpaper.
    /// Checks local disk cache first; if missing, fetches metadata and downloads 4K UHD off-thread.
    /// </summary>
    public async Task<string?> GetBingDailyWallpaperAsync(CancellationToken ct = default)
    {
        return await Task.Run(async () =>
        {
            EnsureCacheDirectory();
            string market = GetCurrentMarket();
            string dateKey = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            string cachePath = Path.Combine(CacheDirectory, $"bing_{dateKey}_{market}.jpg");

            // 1. Instant cache hit check (size > 20KB ensures non-corrupt download)
            if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 20_000)
            {
                return cachePath;
            }

            // 2. Fetch Bing Wallpaper of the Day metadata
            string? uhdUrl = null;

            try
            {
                string apiUrl = $"https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=1&mkt={market}";
                uhdUrl = await FetchBingImageUrlAsync(apiUrl, ct);

                if (string.IsNullOrEmpty(uhdUrl) && !string.Equals(market, "en-US", StringComparison.OrdinalIgnoreCase))
                {
                    // Fallback to en-US market if localized market yields no result
                    string fallbackApiUrl = "https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=1&mkt=en-US";
                    uhdUrl = await FetchBingImageUrlAsync(fallbackApiUrl, ct);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DailyWallpaperService] Bing metadata request failed: {ex.Message}");
            }

            // 3. Download high-resolution image
            if (!string.IsNullOrEmpty(uhdUrl))
            {
                bool downloaded = await DownloadImageFileAsync(uhdUrl, cachePath, ct);
                if (downloaded)
                {
                    PruneOldCacheFiles();
                    return cachePath;
                }
            }

            // 4. Offline fallback: use newest cached Bing wallpaper if available
            return GetNewestCachedWallpaper("bing_*.jpg") ?? cachePath;
        }, ct).ConfigureAwait(false);
    }

    private static async Task<string?> FetchBingImageUrlAsync(string apiUrl, CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(apiUrl, HttpCompletionOption.ResponseContentRead, ct);
        if (!response.IsSuccessStatusCode) return null;

        string json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("images", out var images) || images.GetArrayLength() == 0)
        {
            return null;
        }

        var firstImage = images[0];
        if (firstImage.TryGetProperty("urlbase", out var urlBaseProp))
        {
            string? urlBase = urlBaseProp.GetString();
            if (!string.IsNullOrWhiteSpace(urlBase))
            {
                // Bing standard 4K UHD image format
                return $"https://www.bing.com{urlBase}_UHD.jpg";
            }
        }

        if (firstImage.TryGetProperty("url", out var urlProp))
        {
            string? url = urlProp.GetString();
            if (!string.IsNullOrWhiteSpace(url))
            {
                return $"https://www.bing.com{url}";
            }
        }

        return null;
    }

    /// <summary>
    /// Asynchronously retrieves today's Windows Spotlight 4K image via Peapix Spotlight API.
    /// Checks local disk cache first; if missing, fetches metadata and downloads 4K master off-thread.
    /// </summary>
    public async Task<string?> GetSpotlightDailyWallpaperAsync(CancellationToken ct = default)
    {
        return await Task.Run(async () =>
        {
            EnsureCacheDirectory();
            string country = GetCurrentCountryCode();
            string dateKey = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            string cachePath = Path.Combine(CacheDirectory, $"spotlight_{dateKey}_{country}.jpg");

            // 1. Instant cache hit check
            if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 20_000)
            {
                return cachePath;
            }

            // 2. Fetch Peapix Spotlight metadata
            string? imageUrl = null;
            try
            {
                string apiUrl = $"https://peapix.com/spotlight/feed?country={country}&n=1";
                imageUrl = await FetchPeapixImageUrlAsync(apiUrl, ct);

                if (string.IsNullOrEmpty(imageUrl) && !string.Equals(country, "us", StringComparison.OrdinalIgnoreCase))
                {
                    // Fallback to "us" if localized country returns empty
                    string fallbackApiUrl = "https://peapix.com/spotlight/feed?country=us&n=1";
                    imageUrl = await FetchPeapixImageUrlAsync(fallbackApiUrl, ct);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DailyWallpaperService] Peapix metadata request failed: {ex.Message}");
            }

            // 3. Download high-resolution master image
            if (!string.IsNullOrEmpty(imageUrl))
            {
                bool downloaded = await DownloadImageFileAsync(imageUrl, cachePath, ct);
                if (downloaded)
                {
                    PruneOldCacheFiles();
                    return cachePath;
                }
            }

            // 4. Offline fallback: use newest cached Spotlight wallpaper if available
            return GetNewestCachedWallpaper("spotlight_*.jpg") ?? cachePath;
        }, ct).ConfigureAwait(false);
    }

    private static async Task<string?> FetchPeapixImageUrlAsync(string apiUrl, CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(apiUrl, HttpCompletionOption.ResponseContentRead, ct);
        if (!response.IsSuccessStatusCode) return null;

        string json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            return null;
        }

        var first = root[0];
        // "imageUrl" contains the uncompressed 4K master asset
        if (first.TryGetProperty("imageUrl", out var imgProp) && !string.IsNullOrWhiteSpace(imgProp.GetString()))
        {
            return imgProp.GetString();
        }

        // Fallback to fullUrl (1080p)
        if (first.TryGetProperty("fullUrl", out var fullProp) && !string.IsNullOrWhiteSpace(fullProp.GetString()))
        {
            return fullProp.GetString();
        }

        return null;
    }

    private static async Task<bool> DownloadImageFileAsync(string url, string destinationPath, CancellationToken ct)
    {
        string tempPath = destinationPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return false;

            await using (var netStream = await response.Content.ReadAsStreamAsync(ct))
            await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await netStream.CopyToAsync(fileStream, ct);
            }

            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            File.Move(tempPath, destinationPath);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DailyWallpaperService] Failed to download image from {url}: {ex.Message}");
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
            return false;
        }
    }

    /// <summary>
    /// Asynchronously decodes an image off the UI thread into a frozen, thread-safe BitmapSource.
    /// This prevents any frame drops or hitching on the main UI thread during 4K image parsing.
    /// </summary>
    public static async Task<BitmapSource?> LoadFrozenBitmapAsync(string filePath, int decodeWidth = 3840)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        return await Task.Run(() =>
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(filePath, UriKind.Absolute);
                if (decodeWidth > 0)
                {
                    bmp.DecodePixelWidth = decodeWidth;
                }
                bmp.EndInit();
                bmp.Freeze(); // Crucial: Freezing allows cross-thread safety and off-thread decoding
                return (BitmapSource)bmp;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DailyWallpaperService] Failed to load/freeze bitmap '{filePath}': {ex.Message}");
                return null;
            }
        }).ConfigureAwait(false);
    }

    private static string? GetNewestCachedWallpaper(string searchPattern)
    {
        try
        {
            if (!Directory.Exists(CacheDirectory)) return null;

            var dir = new DirectoryInfo(CacheDirectory);
            var file = dir.GetFiles(searchPattern)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault(f => f.Length > 20_000);

            return file?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private static void PruneOldCacheFiles()
    {
        Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(CacheDirectory)) return;

                var dir = new DirectoryInfo(CacheDirectory);
                var cutoff = DateTime.UtcNow.AddDays(-5);

                foreach (var file in dir.GetFiles("*.jpg"))
                {
                    if (file.LastWriteTimeUtc < cutoff)
                    {
                        try { file.Delete(); } catch { }
                    }
                }
            }
            catch { }
        });
    }
}
