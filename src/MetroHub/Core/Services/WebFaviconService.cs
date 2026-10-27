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

    [GeneratedRegex(@"<meta[^>]+(?:property|name)\s*=\s*[""'](?:og:site_name)[""'][^>]+content\s*=\s*[""']([^""']+)[""']|<meta[^>]+content\s*=\s*[""']([^""']+)[""'][^>]+(?:property|name)\s*=\s*[""'](?:og:site_name)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex OgSiteNameRegex();

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LinkTagRegex();

    [GeneratedRegex(@"\brel\s*=\s*[""']?([^""'>\s]+(?:\s+[^""'>\s]+)*)[""']?", RegexOptions.IgnoreCase)]
    private static partial Regex RelAttrRegex();

    [GeneratedRegex(@"\bhref\s*=\s*[""']?([^""'>\s]+)[""']?", RegexOptions.IgnoreCase)]
    private static partial Regex HrefAttrRegex();

    [GeneratedRegex(@"\bsizes\s*=\s*[""']?(\d+)x(\d+)?[""']?", RegexOptions.IgnoreCase)]
    private static partial Regex SizesAttrRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultipleWhitespaceRegex();

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
            host = host.Substring(4);
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
    /// Infers an initial display title from a web URL algorithmically without hardcoded dictionaries.
    /// Handles Google Workspace & Microsoft 365 suites dynamically, followed by capitalized domain extraction.
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
            host = host.Substring(4);
        }

        // 1. Google Workspace suites (docs.google.com/spreadsheets -> Sheets, etc.)
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

        // 2. Microsoft 365 / Office Cloud Apps
        if (host.Equals("office.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".office.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".cloud.microsoft", StringComparison.OrdinalIgnoreCase))
        {
            string app = host.Split('.')[0].ToLowerInvariant();
            if (app is "www" or "portal" or "login" or "office")
            {
                var pathParts = uri.AbsolutePath.Trim('/').Split('/');
                if (pathParts.Length >= 2 && pathParts[0].Equals("launch", StringComparison.OrdinalIgnoreCase))
                {
                    app = pathParts[1].ToLowerInvariant();
                }
            }

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
                _            => "Office"
            };
        }

        // 3. Dynamic domain extraction without any dictionary
        string[] parts = host.Split('.');
        if (parts.Length >= 3 && IsMultiPartTld(parts[^2], parts[^1]))
        {
            // e.g. ["amazon", "co", "uk"] -> brand is "amazon"
            return Capitalize(parts[^3]);
        }

        if (parts.Length >= 2)
        {
            // e.g. "youtube.com" -> "Youtube", "open.spotify.com" -> "Spotify"
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
        return char.ToUpperInvariant(text[0]) + text.Substring(1);
    }

    private static readonly string[] SpacedTitleSeparators = [" | ", " — ", " – ", " - ", " • ", " · ", " » ", " // ", " |", "| ", "|"];

    /// <summary>
    /// Cleans and extracts the true application or page title from raw web titles,
    /// eliminating marketing slogans, taglines, and branding noise.
    /// Works purely algorithmically with zero hardcoded dictionary lookups.
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
                         path.Equals("signin", StringComparison.OrdinalIgnoreCase) ||
                         path.Equals("create", StringComparison.OrdinalIgnoreCase) ||
                         path.Equals("new", StringComparison.OrdinalIgnoreCase);
        }

        // App Suites marketing filter
        if (brand is "Sheets" or "Docs" or "Slides" or "Forms" or "Excel" or "Word" or "PowerPoint" or "Teams" or "Outlook" or "Drive")
        {
            if (trimmed.Contains("Online Spreadsheet Editor", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Online Document Editor", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Online Slideshow Maker", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Online Form Creator", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Google Workspace", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Microsoft 365", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Sign-in", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Untitled", StringComparison.OrdinalIgnoreCase) ||
                isRootPage)
            {
                return brand;
            }
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

        // Split by standard spaced delimiters
        foreach (string sep in SpacedTitleSeparators)
        {
            if (trimmed.Contains(sep))
            {
                var segments = trimmed.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
                        return isRootPage ? brand : last;
                    }

                    if (lastIsBrand && !firstIsBrand)
                    {
                        return isRootPage ? brand : first;
                    }

                    if (isRootPage)
                    {
                        return brand;
                    }

                    return first;
                }
            }
        }

        if (isRootPage && !string.IsNullOrWhiteSpace(brand) && brand != "Web Link")
        {
            return brand;
        }

        return trimmed;
    }

    /// <summary>
    /// Pure in-memory parser for HTML head markup.
    /// Extracts site name, page title, and the highest-quality non-SVG icon URL.
    /// </summary>
    public static HtmlHeadInfo ParseHtmlHead(string htmlHead, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(htmlHead))
        {
            return new HtmlHeadInfo(null, null, null);
        }

        // 1. Extract og:site_name
        string? siteName = null;
        var ogMatch = OgSiteNameRegex().Match(htmlHead);
        if (ogMatch.Success)
        {
            siteName = !string.IsNullOrWhiteSpace(ogMatch.Groups[1].Value)
                ? ogMatch.Groups[1].Value.Trim()
                : ogMatch.Groups[2].Value.Trim();
            siteName = System.Net.WebUtility.HtmlDecode(siteName);
        }

        // 2. Extract <title>
        string? title = null;
        var titleMatch = TitleRegex().Match(htmlHead);
        if (titleMatch.Success)
        {
            title = System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim());
            title = MultipleWhitespaceRegex().Replace(title, " ");
        }

        // 3. Extract highest quality non-SVG icon
        string? bestIconUrl = null;
        int bestScore = -1;

        var linkMatches = LinkTagRegex().Matches(htmlHead);
        foreach (Match linkTag in linkMatches)
        {
            string tag = linkTag.Value;

            var relMatch = RelAttrRegex().Match(tag);
            var hrefMatch = HrefAttrRegex().Match(tag);

            if (!relMatch.Success || !hrefMatch.Success) continue;

            string rel = relMatch.Groups[1].Value.Trim();
            string rawHref = hrefMatch.Groups[1].Value.Trim();

            if (string.IsNullOrWhiteSpace(rawHref)) continue;

            // Skip SVGs
            string hrefWithoutQuery = rawHref.Split('?')[0];
            if (hrefWithoutQuery.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Split rel into space-separated tokens
            var relTokens = rel.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            bool isApple = false;
            bool isIcon = false;

            foreach (var token in relTokens)
            {
                if (token.Equals("apple-touch-icon", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("apple-touch-icon-precomposed", StringComparison.OrdinalIgnoreCase))
                {
                    isApple = true;
                }
                else if (token.Equals("icon", StringComparison.OrdinalIgnoreCase) ||
                         token.Equals("shortcut", StringComparison.OrdinalIgnoreCase))
                {
                    isIcon = true;
                }
            }

            if (!isApple && !isIcon) continue;

            // Resolve relative URI against baseUri
            if (!Uri.TryCreate(baseUri, rawHref, out Uri? resolvedUri) ||
                (resolvedUri.Scheme != Uri.UriSchemeHttp && resolvedUri.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            // Score candidate: apple-touch-icon gets large base score
            int score = isApple ? 1000 : 10;

            // Check sizes="WxH"
            var sizeMatch = SizesAttrRegex().Match(tag);
            if (sizeMatch.Success && int.TryParse(sizeMatch.Groups[1].Value, out int dim))
            {
                score += dim;
            }
            else if (isApple)
            {
                score += 180;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestIconUrl = resolvedUri.AbsoluteUri;
            }
        }

        return new HtmlHeadInfo(siteName, title, bestIconUrl);
    }

    /// <summary>
    /// Streams up to 64 KB of the page HTML head, stops at &lt;/head&gt;, checks for host redirects,
    /// and parses the site name, page title, and the highest-quality non-SVG icon URL without downloading the icon to disk.
    /// </summary>
    public static async Task<HtmlHeadInfo?> ExtractHtmlHeadInfoAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? requestedUri))
            return null;

        using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, requestedUri);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        using var response = await HttpHelper.Client.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        // Content-Type must be text/html or application/xhtml+xml
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType != null &&
            !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.Contains("xhtml", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Host redirect check: if redirected to another host (e.g. login portal), discard
        string requestedHost = requestedUri.Host.ToLowerInvariant().TrimEnd('.');
        string finalHost = (response.RequestMessage?.RequestUri?.Host ?? requestedHost).ToLowerInvariant().TrimEnd('.');
        string reqNoWww = requestedHost.StartsWith("www.") ? requestedHost[4..] : requestedHost;
        string finalNoWww = finalHost.StartsWith("www.") ? finalHost[4..] : finalHost;
        if (!string.Equals(reqNoWww, finalNoWww, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Uri baseUri = response.RequestMessage?.RequestUri ?? requestedUri;

        // Stream up to 64 KB, accumulating into memory and checking for </head>
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        byte[] buffer = new byte[4096];
        int totalRead = 0;
        const int maxHeadBytes = 65536;

        while (totalRead < maxHeadBytes)
        {
            int toRead = Math.Min(buffer.Length, maxHeadBytes - totalRead);
            int read = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
            if (read == 0) break;

            await ms.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            totalRead += read;

            // Check accumulated buffer for </head>
            string currentText = System.Text.Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            if (currentText.Contains("</head>", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        string htmlHead = System.Text.Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        return ParseHtmlHead(htmlHead, baseUri);
    }

    /// <summary>
    /// Streams up to 64 KB of the page HTML head, stops at </head>, checks for host redirects,
    /// parses the highest quality icon and title, and downloads the icon if found.
    /// </summary>
    private static async Task<HtmlHeadInfo?> TryExtractFromHtmlHeadAsync(
        string url,
        string destinationPath,
        CancellationToken ct)
    {
        var headInfo = await ExtractHtmlHeadInfoAsync(url, ct).ConfigureAwait(false);
        if (headInfo != null && !string.IsNullOrWhiteSpace(headInfo.IconUrl))
        {
            bool downloaded = await HttpHelper.DownloadFileAsync(
                headInfo.IconUrl,
                destinationPath,
                ct,
                minimumBytes: 200).ConfigureAwait(false);

            if (downloaded)
            {
                return headInfo;
            }
        }

        return headInfo;
    }

    /// <summary>
    /// Fetches the high-resolution logo and title for a website and caches it locally.
    /// Runs a 3s direct HTML head scrape within an 8s total timeout budget, falling through
    /// to Google S2 and universal fallbacks on redirect or failure.
    /// Returns a WebFaviconResult implicitly convertible to string? (the icon path).
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

        if (!domain.Contains('.'))
        {
            return default;
        }

        string cachedPath = GetCachedIconPath(normalized);
        if (string.IsNullOrWhiteSpace(cachedPath)) return default;

        // Check in-memory memoization
        if (_memoryCache.TryGetValue(scopeKey, out string? memoized) && File.Exists(memoized))
        {
            return new WebFaviconResult(memoized, null);
        }

        // Check on-disk cache
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

        // Overall 8-second timeout budget
        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overallCts.CancelAfter(TimeSpan.FromSeconds(8));

        string? resolvedTitle = null;

        // Tier 1: Direct HTML Head Scraper (3s linked timeout)
        try
        {
            using var tier1Cts = CancellationTokenSource.CreateLinkedTokenSource(overallCts.Token);
            tier1Cts.CancelAfter(TimeSpan.FromSeconds(3));

            var headInfo = await TryExtractFromHtmlHeadAsync(queryUrl, cachedPath, tier1Cts.Token).ConfigureAwait(false);
            if (headInfo != null)
            {
                resolvedTitle = headInfo.SiteName ?? headInfo.Title;
                if (File.Exists(cachedPath) && new FileInfo(cachedPath).Length > 200)
                {
                    _memoryCache[scopeKey] = cachedPath;
                    return new WebFaviconResult(cachedPath, resolvedTitle);
                }
            }
        }
        catch (OperationCanceledException) when (!overallCts.IsCancellationRequested)
        {
            // Tier 1 timed out after 3s; gracefully fall through to Google S2 with remaining 5s
        }
        catch (Exception ex)
        {
            Safe.Log(ex, $"[WebFaviconService] Tier 1 head scraping failed for '{queryUrl}'");
        }

        // Tier 2: Path-Aware Google S2 Dynamic Favicon Service (128x128)
        try
        {
            string googlePathUrl = $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(queryUrl)}&sz=128";
            if (await HttpHelper.DownloadFileAsync(googlePathUrl, cachedPath, overallCts.Token, minimumBytes: 200).ConfigureAwait(false))
            {
                _memoryCache[scopeKey] = cachedPath;
                return new WebFaviconResult(cachedPath, resolvedTitle);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Safe.Log(ex, $"[WebFaviconService] Tier 2 Google S2 path failed for '{queryUrl}'");
        }

        // Tier 3: Google Base Domain Fallback (if queryUrl had a sub-path)
        if (!string.Equals(queryUrl, domain, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(queryUrl, $"https://{domain}/", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string googleDomainUrl = $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(domain)}&sz=128";
                if (await HttpHelper.DownloadFileAsync(googleDomainUrl, cachedPath, overallCts.Token, minimumBytes: 200).ConfigureAwait(false))
                {
                    _memoryCache[scopeKey] = cachedPath;
                    return new WebFaviconResult(cachedPath, resolvedTitle);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Safe.Log(ex, $"[WebFaviconService] Tier 3 Google S2 domain failed for '{domain}'");
            }
        }

        // Tier 4: DuckDuckGo Icons Service
        try
        {
            string ddgUrl = $"https://icons.duckduckgo.com/ip3/{Uri.EscapeDataString(domain)}.ico";
            if (await HttpHelper.DownloadFileAsync(ddgUrl, cachedPath, overallCts.Token, minimumBytes: 200).ConfigureAwait(false))
            {
                _memoryCache[scopeKey] = cachedPath;
                return new WebFaviconResult(cachedPath, resolvedTitle);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Safe.Log(ex, $"[WebFaviconService] Tier 4 DuckDuckGo failed for '{domain}'");
        }

        // Tier 5: Direct website root favicon
        try
        {
            string directUrl = $"https://{domain}/favicon.ico";
            if (await HttpHelper.DownloadFileAsync(directUrl, cachedPath, overallCts.Token, minimumBytes: 200).ConfigureAwait(false))
            {
                _memoryCache[scopeKey] = cachedPath;
                return new WebFaviconResult(cachedPath, resolvedTitle);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Safe.Log(ex, $"[WebFaviconService] Tier 5 direct favicon failed for '{domain}'");
        }

        return new WebFaviconResult(null, resolvedTitle);
    }

    /// <summary>
    /// Downloads remote icon bytes directly into memory without touching the local disk.
    /// </summary>
    public static async Task<byte[]?> DownloadIconBytesAsync(string iconUrl, CancellationToken ct = default, int minimumBytes = 200)
    {
        if (string.IsNullOrWhiteSpace(iconUrl)) return null;

        try
        {
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, iconUrl);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            using var response = await HttpHelper.Client.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (minimumBytes > 0 && bytes.Length < minimumBytes) return null;

            return bytes;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Safe.Log(ex, $"[WebFaviconService] Failed to download icon bytes from '{iconUrl}'");
            return null;
        }
    }

    /// <summary>
    /// Generates candidate favicon source URLs in priority order:
    /// 1. HTML head apple-touch-icon / high-res icon (if provided)
    /// 2. Google S2 path-aware favicon service
    /// 3. Google S2 domain fallback (if queryUrl had a sub-path)
    /// 4. DuckDuckGo icons service
    /// 5. Direct root favicon.ico
    /// </summary>
    public static List<string> GetCandidateSourceUrls(string url, string? htmlHeadIconUrl = null)
    {
        var list = new List<string>(5);
        if (string.IsNullOrWhiteSpace(url)) return list;

        string normalized = NormalizeUrl(url);
        if (string.IsNullOrWhiteSpace(normalized)) return list;

        string domain = ExtractDomain(normalized);
        if (string.IsNullOrWhiteSpace(domain) || !domain.Contains('.')) return list;

        string queryUrl = normalized;
        if (Uri.TryCreate(normalized, UriKind.Absolute, out Uri? parsedUri))
        {
            queryUrl = parsedUri.GetLeftPart(UriPartial.Path);
        }

        void AddCandidate(string candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && !list.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(candidate);
            }
        }

        // 1. HTML head icon
        if (!string.IsNullOrWhiteSpace(htmlHeadIconUrl))
        {
            AddCandidate(htmlHeadIconUrl);
        }

        // 2. Google S2 Path-aware
        string googlePathUrl = $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(queryUrl)}&sz=128";
        AddCandidate(googlePathUrl);

        // 3. Google S2 Domain (if path is distinct from root domain)
        if (!string.Equals(queryUrl, domain, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(queryUrl, $"https://{domain}/", StringComparison.OrdinalIgnoreCase))
        {
            string googleDomainUrl = $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(domain)}&sz=128";
            AddCandidate(googleDomainUrl);
        }

        // 4. DuckDuckGo Icons
        string ddgUrl = $"https://icons.duckduckgo.com/ip3/{Uri.EscapeDataString(domain)}.ico";
        AddCandidate(ddgUrl);

        // 5. Direct root favicon
        string directUrl = $"https://{domain}/favicon.ico";
        AddCandidate(directUrl);

        return list;
    }

    /// <summary>
    /// Saves the selected icon bytes to the single deterministic cache path on disk,
    /// updating in-memory memoization.
    /// </summary>
    public static string? SaveIconToCache(string url, byte[] bytes)
    {
        if (string.IsNullOrWhiteSpace(url) || bytes == null || bytes.Length == 0) return null;

        try
        {
            string cachedPath = GetCachedIconPath(url);
            if (string.IsNullOrWhiteSpace(cachedPath)) return null;

            string? dir = Path.GetDirectoryName(cachedPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(cachedPath, bytes);

            string scopeKey = ComputeCacheScopeKey(url);
            if (!string.IsNullOrWhiteSpace(scopeKey))
            {
                _memoryCache[scopeKey] = cachedPath;
            }

            return cachedPath;
        }
        catch (Exception ex)
        {
            Safe.Log(ex, $"Failed to save favicon to cache for '{url}'");
            return null;
        }
    }
}

/// <summary>
/// Parsed metadata from HTML head.
/// </summary>
public sealed record HtmlHeadInfo(string? SiteName, string? Title, string? IconUrl);

/// <summary>
/// Result of favicon resolution, implicitly convertible to string? (icon path).
/// </summary>
public readonly record struct WebFaviconResult(string? IconPath, string? Title)
{
    public static implicit operator string?(WebFaviconResult res) => res.IconPath;
}
