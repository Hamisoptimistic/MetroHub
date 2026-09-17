using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MetroHub.Core.Services;

/// <summary>
/// High-performance website favicon and logo extractor service.
/// Fetches crisp 128x128 official logos for sites like YouTube, Spotify, GitHub, etc.,
/// and caches them locally to ensure zero memory churn and instant offline loading.
/// </summary>
public static partial class WebFaviconService
{
    private static readonly string IconCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MetroHub", "icons");

    private static readonly ConcurrentDictionary<string, string> _memoryCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HttpClient _httpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private static readonly FrozenDictionary<string, string> KnownBrands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "youtube.com", "YouTube" },
        { "youtu.be", "YouTube" },
        { "music.youtube.com", "YouTube Music" },
        { "spotify.com", "Spotify" },
        { "github.com", "GitHub" },
        { "reddit.com", "Reddit" },
        { "twitter.com", "X (Twitter)" },
        { "x.com", "X" },
        { "twitch.tv", "Twitch" },
        { "netflix.com", "Netflix" },
        { "discord.com", "Discord" },
        { "google.com", "Google" },
        { "mail.google.com", "Gmail" },
        { "gmail.com", "Gmail" },
        { "chatgpt.com", "ChatGPT" },
        { "openai.com", "OpenAI" },
        { "amazon.com", "Amazon" },
        { "wikipedia.org", "Wikipedia" },
        { "facebook.com", "Facebook" },
        { "instagram.com", "Instagram" },
        { "linkedin.com", "LinkedIn" },
        { "notion.so", "Notion" },
        { "figma.com", "Figma" },
        { "stackoverflow.com", "Stack Overflow" },
        { "medium.com", "Medium" },
        { "dropbox.com", "Dropbox" },
        { "soundcloud.com", "SoundCloud" },
        { "pinterest.com", "Pinterest" },
        { "steampowered.com", "Steam" },
        { "steamcommunity.com", "Steam" },
        { "microsoft.com", "Microsoft" },
        { "apple.com", "Apple" }
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"[^a-zA-Z0-9_\-\.]")]
    private static partial Regex SafeDomainRegex();

    static WebFaviconService()
    {
        try
        {
            if (!Directory.Exists(IconCacheDir))
            {
                Directory.CreateDirectory(IconCacheDir);
            }
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) MetroHub/1.0");
        }
        catch { }
    }

    /// <summary>
    /// Normalizes raw user input into a valid HTTPS web URL.
    /// E.g. "youtube.com" -> "https://youtube.com/"
    /// </summary>
    public static string NormalizeUrl(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        string trimmed = input.Trim();

        // Check if user entered an explicit scheme
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "https://" + trimmed;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
        {
            return uri.AbsoluteUri;
        }

        return trimmed;
    }

    /// <summary>
    /// Extracts the clean host/domain name from a URL.
    /// E.g. "https://www.youtube.com/watch?v=..." -> "youtube.com"
    /// </summary>
    public static string ExtractDomain(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;

        string normalized = NormalizeUrl(url);
        if (Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri))
        {
            string host = uri.Host.ToLowerInvariant();
            if (host.StartsWith("www."))
            {
                host = host.Substring(4);
            }
            return host;
        }

        return string.Empty;
    }

    /// <summary>
    /// Automatically infers a clean display title from the URL.
    /// E.g. "https://open.spotify.com/..." -> "Spotify"
    /// </summary>
    public static string InferTitleFromUrl(string url)
    {
        string domain = ExtractDomain(url);
        if (string.IsNullOrWhiteSpace(domain)) return "Web Link";

        if (KnownBrands.TryGetValue(domain, out string? knownTitle))
        {
            return knownTitle;
        }

        // Try second-level domain match (e.g. "open.spotify.com" -> "spotify.com")
        var parts = domain.Split('.');
        if (parts.Length >= 2)
        {
            string rootDomain = parts[^2] + "." + parts[^1];
            if (KnownBrands.TryGetValue(rootDomain, out string? rootTitle))
            {
                return rootTitle;
            }

            // Fallback: capitalize the main brand part
            string brand = parts[^2];
            if (brand.Length > 0)
            {
                return char.ToUpperInvariant(brand[0]) + brand.Substring(1);
            }
        }

        return domain;
    }

    /// <summary>
    /// Fetches the high-resolution logo for a website and caches it locally.
    /// Returns the absolute path to the cached PNG file on disk, or null if unreachable.
    /// </summary>
    public static async Task<string?> GetFaviconPathAsync(string url, CancellationToken ct = default)
    {
        string domain = ExtractDomain(url);
        if (string.IsNullOrWhiteSpace(domain)) return null;

        // Check in-memory memoization
        if (_memoryCache.TryGetValue(domain, out string? memoized) && File.Exists(memoized))
        {
            return memoized;
        }

        // Compute deterministic hash for the cache filename using compiled regex
        string safeDomain = SafeDomainRegex().Replace(domain, "_");
        string hash = Math.Abs(domain.GetHashCode()).ToString("X8");
        string cachedPath = Path.Combine(IconCacheDir, $"web_{safeDomain}_{hash}.png");

        // Check on-disk cache
        if (File.Exists(cachedPath))
        {
            try
            {
                var fi = new FileInfo(cachedPath);
                if (fi.Length > 200)
                {
                    _memoryCache[domain] = cachedPath;
                    return cachedPath;
                }
            }
            catch { }
        }

        // Tier 1: Google High-Resolution Favicon Service (128x128 crisp transparent PNG)
        string googleUrl = $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(domain)}&sz=128";
        if (await TryDownloadAndSaveAsync(googleUrl, cachedPath, ct).ConfigureAwait(false))
        {
            _memoryCache[domain] = cachedPath;
            return cachedPath;
        }

        // Tier 2: DuckDuckGo Icons Service
        string ddgUrl = $"https://icons.duckduckgo.com/ip3/{Uri.EscapeDataString(domain)}.ico";
        if (await TryDownloadAndSaveAsync(ddgUrl, cachedPath, ct).ConfigureAwait(false))
        {
            _memoryCache[domain] = cachedPath;
            return cachedPath;
        }

        // Tier 3: Direct website root favicon
        string directUrl = $"https://{domain}/favicon.ico";
        if (await TryDownloadAndSaveAsync(directUrl, cachedPath, ct).ConfigureAwait(false))
        {
            _memoryCache[domain] = cachedPath;
            return cachedPath;
        }

        return null;
    }

    private static async Task<bool> TryDownloadAndSaveAsync(string requestUrl, string destinationPath, CancellationToken ct)
    {
        try
        {
            using var response = await _httpClient.GetAsync(requestUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;

            string tempFile = destinationPath + ".tmp";
            await using (var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await responseStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
            }

            var fi = new FileInfo(tempFile);
            if (fi.Length < 200)
            {
                try { File.Delete(tempFile); } catch { }
                return false;
            }

            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
            File.Move(tempFile, destinationPath);

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebFaviconService] Failed download from {requestUrl}: {ex.Message}");
            return false;
        }
    }
}
