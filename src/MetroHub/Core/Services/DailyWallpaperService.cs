using System;
using System.Collections.Frozen;
using System.Collections.Generic;
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

    private static readonly Lazy<bool> _isWebpSupported = new(() =>
    {
        try
        {
            // Probe for WIC WebP decoder using a minimal 1x1 WebP RIFF byte array (30 bytes)
            byte[] dummyWebp = Convert.FromBase64String("UklGRiQAAABXRUJQVlA4IBgAAAAwAQCdASoBAAEAAQAcJaQAA3AA/v3AgAA=");
            using var ms = new MemoryStream(dummyWebp);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.None);
            return decoder != null;
        }
        catch
        {
            return false;
        }
    });

    /// <summary>
    /// Indicates whether Windows has an active WIC WebP decoder registered.
    /// </summary>
    public static bool IsWebpSupported => _isWebpSupported.Value;

    private static readonly FrozenSet<string> _videoExtensions = new[]
    {
        ".mp4", ".m4v", ".mp4v",
        ".webm",
        ".mkv",
        ".mov", ".qt",
        ".avi", ".divx", ".xvid",
        ".wmv", ".asf",
        ".flv", ".f4v",
        ".ogv", ".ogg", ".ogm",
        ".ts", ".mts", ".m2ts",
        ".vob",
        ".mpg", ".mpeg", ".m1v", ".m2v", ".mpv",
        ".3gp", ".3g2",
        ".rm", ".rmvb"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> _videoExtensionsLookup =
        _videoExtensions.GetAlternateLookup<ReadOnlySpan<char>>();

    public const string VideoExtensionsFilter =
        "*.mp4;*.m4v;*.mp4v;*.webm;*.mkv;*.mov;*.qt;*.avi;*.divx;*.xvid;*.wmv;*.asf;*.flv;*.f4v;*.ogv;*.ogg;*.ogm;*.ts;*.mts;*.m2ts;*.vob;*.mpg;*.mpeg;*.m1v;*.m2v;*.mpv;*.3gp;*.3g2;*.rm;*.rmvb";

    /// <summary>
    /// Builds the OpenFileDialog filter string, dynamically enabling .webp if supported by the OS.
    /// </summary>
    public static string GetWallpaperFileDialogFilter()
    {
        string imgExts = IsWebpSupported
            ? "*.png;*.jpg;*.jpeg;*.bmp;*.webp"
            : "*.png;*.jpg;*.jpeg;*.bmp";

        return $"All Supported Media ({imgExts};{VideoExtensionsFilter})|{imgExts};{VideoExtensionsFilter}|" +
               $"Video Files ({VideoExtensionsFilter})|{VideoExtensionsFilter}|" +
               $"Image Files ({imgExts})|{imgExts}|" +
               "All Files (*.*)|*.*";
    }

    /// <summary>
    /// Checks if a file path is any known video wallpaper format.
    /// Zero-allocation extension check using FrozenSet AlternateLookup on ReadOnlySpan.
    /// </summary>
    public static bool IsVideoWallpaper(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        ReadOnlySpan<char> span = path.AsSpan();
        int dotIndex = span.LastIndexOf('.');
        if (dotIndex < 0) return false;

        return _videoExtensionsLookup.Contains(span[dotIndex..]);
    }

    /// <summary>
    /// Decodes a wallpaper off-thread with capped display resolution and granular error classification.
    /// Only downscales if original image width exceeds screen target budget; never upscales smaller images.
    /// </summary>
    public static BitmapImage? TryLoadWallpaper(string path, double screenWidth, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            error = "File not found.";
            return null;
        }

        // Cap decode width: decode at screen width (e.g. 1920, 2560, or 3840), never upscale if smaller.
        int targetBudget = (int)Math.Clamp(screenWidth, 1280, 3840);

        try
        {
            // 1. Read header dimensions first (fast, negligible memory, no full pixel decode).
            int originalWidth = 0;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var frame = BitmapFrame.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                originalWidth = frame.PixelWidth;
            }
            catch
            {
                // If header probing fails, fall back to standard decode
            }

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad; // Release file handle immediately
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache; // Don't hold hidden ref in WPF cache

            // Only downscale if original is wider than screen target budget.
            // Never upscale smaller images (e.g. 720p or 1080p), preventing memory bloat.
            if (originalWidth > targetBudget)
            {
                bmp.DecodePixelWidth = targetBudget;
            }

            bmp.EndInit();
            bmp.Freeze(); // Immutable, thread-safe Freezable per Rule 3
            return bmp;
        }
        catch (NotSupportedException)
        {
            error = path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                ? "WebP isn't supported on this Windows install. Convert to PNG or JPG."
                : "That image format isn't supported on this Windows install.";
            return null;
        }
        catch (FileFormatException)
        {
            error = "That file appears to be corrupt.";
            return null;
        }
        catch (OutOfMemoryException)
        {
            error = "That image is too large to use as a wallpaper.";
            return null;
        }
        catch (IOException)
        {
            error = "Can't read that file. Is it open in another app?";
            return null;
        }
        catch (Exception ex)
        {
            error = $"Couldn't load that image: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Asynchronously decodes wallpaper off-thread with specific error messaging and cancellation support.
    /// </summary>
    public static async Task<(BitmapImage? Image, string? Error)> TryLoadWallpaperAsync(
        string filePath,
        double screenWidth = 1920,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var bmp = TryLoadWallpaper(filePath, screenWidth, out var err);
            ct.ThrowIfCancellationRequested();
            return (bmp, err);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously decodes an image off the UI thread into a frozen, thread-safe BitmapSource.
    /// This prevents any frame drops or hitching on the main UI thread during image parsing.
    /// </summary>
    public static async Task<BitmapSource?> LoadFrozenBitmapAsync(
        string filePath,
        int decodeWidth = 1920,
        CancellationToken ct = default)
    {
        var (bmp, _) = await TryLoadWallpaperAsync(filePath, decodeWidth, ct).ConfigureAwait(false);
        return bmp;
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
