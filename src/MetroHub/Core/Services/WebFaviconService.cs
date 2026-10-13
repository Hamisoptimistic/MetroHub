using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    private static string IconCacheDir => AppPaths.IconCacheDir;

    private static readonly ConcurrentDictionary<string, string> _memoryCache = new(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenDictionary<string, string> KnownBrands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "Failed to initialize WebFaviconService cache directory");
        }
    }

    /// <summary>
    /// Normalizes raw user input into a valid HTTPS web URL.
    /// E.g. "www.youtube.com" -> "https://www.youtube.com/"
    /// E.g. "youtube.com" -> "https://youtube.com/"
    /// </summary>
    public static string NormalizeUrl(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        string trimmed = input.Trim().TrimEnd('/');

        // Incomplete prefix checks while typing
        if (trimmed.Equals("www", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("www.", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("http", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("http:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("http:/", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("https", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("https:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("https:/", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        // If it starts with "www.", prepend https://
        if (trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "https://" + trimmed;
        }
        else if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                 !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // Naked domain without protocol (e.g. "google.com" or "music.youtube.com")
            trimmed = "https://" + trimmed;
        }

        // If user typed e.g. "https://www.google" without TLD, check if it matches a known brand
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
        {
            string host = uri.Host.ToLowerInvariant().TrimEnd('.');
            if (host.StartsWith("www.") && host.Length > 4)
            {
                host = host.Substring(4);
            }

            // If no dot in host, but matches known brand (e.g. "google"), append .com
            if (!host.Contains('.') && KnownBrands.ContainsKey(host + ".com"))
            {
                string completed = $"https://{uri.Host}.com{uri.PathAndQuery}";
                if (Uri.TryCreate(completed, UriKind.Absolute, out Uri? completedUri))
                {
                    return completedUri.AbsoluteUri;
                }
            }

            return uri.AbsoluteUri;
        }

        return trimmed;
    }

    /// <summary>
    /// Extracts the clean host/domain name from a URL.
    /// E.g. "https://www.youtube.com/watch?v=..." -> "youtube.com"
    /// E.g. "www.youtube.com" -> "youtube.com"
    /// </summary>
    public static string ExtractDomain(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;

        string normalized = NormalizeUrl(url);
        if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;

        if (Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri))
        {
            string host = uri.Host.ToLowerInvariant().TrimEnd('.');
            if (host.StartsWith("www."))
            {
                host = host.Length > 4 ? host.Substring(4) : string.Empty;
            }

            if (host == "www" || string.IsNullOrWhiteSpace(host))
            {
                return string.Empty;
            }

            return host;
        }

        return string.Empty;
    }

    private static readonly FrozenDictionary<string, string> SuitePathPrefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "docs.google.com/spreadsheets", "docs.google.com_spreadsheets" },
        { "docs.google.com/document", "docs.google.com_document" },
        { "docs.google.com/presentation", "docs.google.com_presentation" },
        { "docs.google.com/forms", "docs.google.com_forms" }
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Computes a clean cache scope key for a given URL.
    /// By default, keys strictly by host (preventing cache file proliferation).
    /// Known multi-app suites (e.g. Google Workspace) use static prefix scoping.
    /// </summary>
    public static string ComputeCacheScopeKey(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;

        string normalized = NormalizeUrl(url);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri))
        {
            return ExtractDomain(url);
        }

        string host = uri.Host.ToLowerInvariant().TrimEnd('.');
        if (host.StartsWith("www.") && host.Length > 4)
        {
            host = host[4..];
        }

        // Check static suite table first (pure data, zero branching)
        string path = uri.AbsolutePath.TrimEnd('/').ToLowerInvariant();
        string hostAndPath = $"{host}{path}";
        foreach (var kvp in SuitePathPrefixes)
        {
            if (hostAndPath.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }

        // Host-only for general websites
        return host;
    }

    /// <summary>
    /// Computes the deterministic absolute local cache path for a website or web app icon.
    /// </summary>
    public static string GetCachedIconPath(string url)
    {
        string scopeKey = ComputeCacheScopeKey(url);
        if (string.IsNullOrWhiteSpace(scopeKey)) return string.Empty;

        string safeScope = SafeDomainRegex().Replace(scopeKey, "_");
        string hash = IconExtractorService.ComputeDeterministicHash(scopeKey);
        return Path.Combine(IconCacheDir, $"web_{safeScope}_{hash}.png");
    }

    /// <summary>
    /// Infers a clean, human-friendly display title from a web URL in 0 milliseconds.
    /// Specifically handles Google Workspace (Docs, Sheets, Slides, Forms, Drive, Gmail),
    /// Microsoft 365 Cloud Apps (Excel, Word, PowerPoint, Teams, Outlook, OneNote),
    /// known curated brands, and international multi-part TLDs.
    /// </summary>
    public static string InferTitleFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "Web Link";

        string normalized = NormalizeUrl(url);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri))
        {
            return "Web Link";
        }

        string host = uri.Host.ToLowerInvariant().TrimEnd('.');
        if (host.StartsWith("www.") && host.Length > 4)
        {
            host = host[4..];
        }

        // 1. Google Workspace (docs.google.com/spreadsheets -> Sheets, document -> Docs, etc.)
        if (host.Equals("docs.google.com", StringComparison.OrdinalIgnoreCase))
        {
            string firstSegment = uri.AbsolutePath.Trim('/').Split('/')[0].ToLowerInvariant();
            return firstSegment switch
            {
                "spreadsheets" => "Sheets",
                "document"     => "Docs",
                "presentation" => "Slides",
                "forms"        => "Forms",
                _              => "Docs"
            };
        }

        if (host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase)) return "Drive";
        if (host.Equals("mail.google.com", StringComparison.OrdinalIgnoreCase) || host.Equals("gmail.com", StringComparison.OrdinalIgnoreCase)) return "Gmail";
        if (host.Equals("meet.google.com", StringComparison.OrdinalIgnoreCase)) return "Meet";
        if (host.Equals("calendar.google.com", StringComparison.OrdinalIgnoreCase)) return "Calendar";

        // 2. Microsoft 365 Cloud Apps (*.cloud.microsoft or *.office.com)
        if (host.EndsWith(".cloud.microsoft", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".office.com", StringComparison.OrdinalIgnoreCase))
        {
            string app = host.Split('.')[0].ToLowerInvariant();
            return app switch
            {
                "excel"      => "Excel",
                "word"       => "Word",
                "powerpoint" => "PowerPoint",
                "teams"      => "Teams",
                "outlook"    => "Outlook",
                "onenote"    => "OneNote",
                "loop"       => "Loop",
                "onedrive"   => "OneDrive",
                _ when app.Length > 0 => Capitalize(app),
                _            => "Microsoft 365"
            };
        }

        // 3. Known Curated Brands (exact host match)
        if (KnownBrands.TryGetValue(host, out string? knownTitle))
        {
            return knownTitle;
        }

        // 4. International multi-part TLD & second-level domain extraction
        string[] parts = host.Split('.');
        if (parts.Length >= 3 && IsMultiPartTld(parts[^2], parts[^1]))
        {
            // e.g. ["amazon", "co", "uk"] -> brand is parts[^3]
            string brand = parts[^3];
            string candidateDomain = $"{brand}.{parts[^2]}.{parts[^1]}";
            if (KnownBrands.TryGetValue(candidateDomain, out string? brandTitle))
            {
                return brandTitle;
            }
            return Capitalize(brand);
        }

        if (parts.Length >= 2)
        {
            string rootDomain = $"{parts[^2]}.{parts[^1]}";
            if (KnownBrands.TryGetValue(rootDomain, out string? rootTitle))
            {
                return rootTitle;
            }

            return Capitalize(parts[^2]);
        }

        if (parts.Length == 1 && parts[0].Length > 0)
        {
            return Capitalize(parts[0]);
        }

        return host;
    }

    private static bool IsMultiPartTld(string secondToLast, string tld)
    {
        return (secondToLast is "co" or "com" or "org" or "net" or "gov" or "edu") && tld.Length == 2;
    }

    private static string Capitalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static readonly char[] TitleSeparators = ['|', '—', '–', '-', '•', '·', '»'];

    /// <summary>
    /// Cleans and extracts the true application or page title from raw web titles,
    /// eliminating marketing slogans, taglines, and branding noise.
    /// Handles both "Slogan | Brand" (e.g. "The AI workspace that works for you. | Notion")
    /// and "Brand - Slogan" (e.g. "Obsidian - Sharpen your thinking").
    /// </summary>
    public static string CleanWebTitle(string? rawTitle, string url)
    {
        string brand = InferTitleFromUrl(url);

        if (string.IsNullOrWhiteSpace(rawTitle) ||
            rawTitle.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return brand;
        }

        string trimmed = rawTitle.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return brand;
        }

        // Check if URL is effectively a root / home page
        bool isRootPage = true;
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            string path = uri.AbsolutePath.Trim('/');
            isRootPage = string.IsNullOrEmpty(path) ||
                         path.Equals("home", StringComparison.OrdinalIgnoreCase) ||
                         path.Equals("index", StringComparison.OrdinalIgnoreCase) ||
                         path.Equals("index.html", StringComparison.OrdinalIgnoreCase) ||
                         path.Equals("app", StringComparison.OrdinalIgnoreCase) ||
                         path.Equals("login", StringComparison.OrdinalIgnoreCase) ||
                         path.Equals("signin", StringComparison.OrdinalIgnoreCase);
        }

        // If it's a home page, always prefer the clean brand name rather than marketing slogans
        if (isRootPage && !string.IsNullOrWhiteSpace(brand) && brand != "Web Link")
        {
            if (trimmed.Contains(brand, StringComparison.OrdinalIgnoreCase))
            {
                return brand;
            }
        }

        // Check for colon separation if preceded by brand (e.g. "Figma: The Collaborative Interface...")
        if (trimmed.Contains(": ") && !string.IsNullOrWhiteSpace(brand))
        {
            var colonParts = trimmed.Split(": ", 2, StringSplitOptions.TrimEntries);
            if (colonParts.Length == 2)
            {
                if (string.Equals(colonParts[0], brand, StringComparison.OrdinalIgnoreCase))
                {
                    return isRootPage ? brand : colonParts[1];
                }
                if (string.Equals(colonParts[1], brand, StringComparison.OrdinalIgnoreCase))
                {
                    return isRootPage ? brand : colonParts[0];
                }
            }
        }

        // Split by standard web title separators
        int sepIndex = trimmed.IndexOfAny(TitleSeparators);
        if (sepIndex >= 0)
        {
            var segments = trimmed.Split(TitleSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length >= 2)
            {
                string first = segments[0];
                string last = segments[^1];

                bool firstIsBrand = first.Contains(brand, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(first, "Google", StringComparison.OrdinalIgnoreCase);
                bool lastIsBrand = last.Contains(brand, StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(last, "Google", StringComparison.OrdinalIgnoreCase);

                if (firstIsBrand && !lastIsBrand)
                {
                    // e.g. "Obsidian - Sharpen your thinking" -> on root page, return "Obsidian". On deep page, return specific title.
                    return isRootPage ? brand : last;
                }

                if (lastIsBrand && !firstIsBrand)
                {
                    // e.g. "The AI workspace that works for you. | Notion" -> on root page, return "Notion". On deep page, return specific title.
                    return isRootPage ? brand : first;
                }

                if (isRootPage)
                {
                    return brand;
                }

                // For deep pages where neither part directly matches brand: take the primary title
                return first;
            }
        }

        if (isRootPage && !string.IsNullOrWhiteSpace(brand) && brand != "Web Link")
        {
            return brand;
        }

        return trimmed;
    }

    /// <summary>
    /// Fetches the high-resolution logo for a website or web app and caches it locally.
    /// Uses a direct, ultra-fast edge CDN pipeline (Google S2 128px PNG -> DuckDuckGo ICO -> direct /favicon.ico)
    /// with streaming magic-byte bitmap validation and host-scoped disk caching.
    /// Zero HTML scraping, zero redirect loops, zero 5-second hangs.
    /// </summary>
    public static async Task<WebFaviconResult> GetFaviconPathAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return default;

        string normalized = NormalizeUrl(url);
        if (string.IsNullOrWhiteSpace(normalized)) return default;

        string scopeKey = ComputeCacheScopeKey(normalized);
        if (string.IsNullOrWhiteSpace(scopeKey)) return default;

        string domain = ExtractDomain(normalized);
        if (string.IsNullOrWhiteSpace(domain)) return default;

        // If domain has no dot, check if it matches a known brand before attempting fetch
        if (!domain.Contains('.'))
        {
            string candidate = domain + ".com";
            if (KnownBrands.ContainsKey(candidate))
            {
                domain = candidate;
            }
            else
            {
                return default;
            }
        }

        string cachedPath = GetCachedIconPath(normalized);
        if (string.IsNullOrWhiteSpace(cachedPath)) return default;

        // 1. Check in-memory memoization
        if (_memoryCache.TryGetValue(scopeKey, out string? memoized) && File.Exists(memoized))
        {
            return new WebFaviconResult(memoized, null);
        }

        // 2. Check on-disk cache
        if (File.Exists(cachedPath))
        {
            try
            {
                var fi = new FileInfo(cachedPath);
                if (fi.Length > 200)
                {
                    _memoryCache[scopeKey] = cachedPath;
                    return new WebFaviconResult(cachedPath, null);
                }
            }
            catch (Exception ex)
            {
                Safe.Log(ex, $"Failed to inspect cached favicon '{cachedPath}'");
            }
        }

        // Clean target URL for query (strip query parameters and fragments)
        string queryUrl = normalized;
        if (Uri.TryCreate(normalized, UriKind.Absolute, out Uri? parsedUri))
        {
            queryUrl = parsedUri.GetLeftPart(UriPartial.Path);
        }

        // 4-second total timeout budget for edge CDN fetching
        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overallCts.CancelAfter(TimeSpan.FromSeconds(4));

        // Tier 1: Path-Aware Google S2 Dynamic Favicon Service (128x128 PNG)
        try
        {
            string googlePathUrl = $"https://t2.gstatic.com/faviconV2?client=SOCIAL&type=FAVICON&fallback_opts=TYPE,SIZE,URL&url={Uri.EscapeDataString(queryUrl)}&size=128";
            if (await HttpHelper.DownloadFileAsync(googlePathUrl, cachedPath, overallCts.Token, minimumBytes: 200, maximumBytes: 524_288, requireValidBitmap: true).ConfigureAwait(false))
            {
                _memoryCache[scopeKey] = cachedPath;
                return new WebFaviconResult(cachedPath, null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Safe.Log(ex, $"[WebFaviconService] Tier 1 Google S2 path failed for '{queryUrl}'");
        }

        // Tier 2: Google Base Domain Fallback (if queryUrl had a sub-path)
        if (!string.Equals(queryUrl, domain, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(queryUrl, $"https://{domain}/", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string googleDomainUrl = $"https://t2.gstatic.com/faviconV2?client=SOCIAL&type=FAVICON&fallback_opts=TYPE,SIZE,URL&url={Uri.EscapeDataString(domain)}&size=128";
                if (await HttpHelper.DownloadFileAsync(googleDomainUrl, cachedPath, overallCts.Token, minimumBytes: 200, maximumBytes: 524_288, requireValidBitmap: true).ConfigureAwait(false))
                {
                    _memoryCache[scopeKey] = cachedPath;
                    return new WebFaviconResult(cachedPath, null);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Safe.Log(ex, $"[WebFaviconService] Tier 2 Google S2 domain failed for '{domain}'");
            }
        }

        // Tier 3: DuckDuckGo Icons Service
        try
        {
            string ddgUrl = $"https://icons.duckduckgo.com/ip3/{Uri.EscapeDataString(domain)}.ico";
            if (await HttpHelper.DownloadFileAsync(ddgUrl, cachedPath, overallCts.Token, minimumBytes: 200, maximumBytes: 524_288, requireValidBitmap: true).ConfigureAwait(false))
            {
                _memoryCache[scopeKey] = cachedPath;
                return new WebFaviconResult(cachedPath, null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Safe.Log(ex, $"[WebFaviconService] Tier 3 DuckDuckGo failed for '{domain}'");
        }

        // Tier 4: Direct website root favicon
        try
        {
            string directUrl = $"https://{domain}/favicon.ico";
            if (await HttpHelper.DownloadFileAsync(directUrl, cachedPath, overallCts.Token, minimumBytes: 200, maximumBytes: 524_288, requireValidBitmap: true).ConfigureAwait(false))
            {
                _memoryCache[scopeKey] = cachedPath;
                return new WebFaviconResult(cachedPath, null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Safe.Log(ex, $"[WebFaviconService] Tier 4 direct favicon failed for '{domain}'");
        }

        return default;
    }
}

/// <summary>
/// Result of favicon resolution, implicitly convertible to string? (icon path).
/// </summary>
public readonly record struct WebFaviconResult(string? IconPath, string? Title)
{
    public static implicit operator string?(WebFaviconResult res) => res.IconPath;
}
