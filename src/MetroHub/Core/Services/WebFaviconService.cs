using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
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
            if (host.StartsWith("www.", StringComparison.Ordinal) && host.Length > 4)
            {
                host = host[4..];
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
            if (host.StartsWith("www.", StringComparison.Ordinal))
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

    [GeneratedRegex(@"<meta[^>]+(?:property|name)\s*=\s*[""'](?:og:site_name)[""'][^>]+content\s*=\s*[""'](?<siteName>[^""']+)[""']|<meta[^>]+content\s*=\s*[""'](?<siteName>[^""']+)[""'][^>]+(?:property|name)\s*=\s*[""'](?:og:site_name)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex OgSiteNameRegex();

    [GeneratedRegex(@"<title[^>]*>(?<title>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LinkTagRegex();

    [GeneratedRegex(@"<base\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BaseTagRegex();

    [GeneratedRegex(@"\brel\s*=\s*(?:""(?<rel>[^""]*)""|'(?<rel>[^']*)'|(?<rel>[^\s>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex RelAttrRegex();

    [GeneratedRegex(@"\bhref\s*=\s*(?:""(?<href>[^""]*)""|'(?<href>[^']*)'|(?<href>[^\s>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex HrefAttrRegex();

    [GeneratedRegex(@"\bsizes\s*=\s*(?:""(?<sizes>[^""]*)""|'(?<sizes>[^']*)'|(?<sizes>[^\s>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex SizesAttrRegex();

    [GeneratedRegex(@"(?<width>\d+)x(?:\d+)?", RegexOptions.IgnoreCase)]
    private static partial Regex DimensionsRegex();

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
        if (host.StartsWith("www.", StringComparison.Ordinal) && host.Length > 4)
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
        if (host.StartsWith("www.", StringComparison.Ordinal) && host.Length > 4)
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
        if (trimmed.Contains(": ", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(brand))
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
    /// Extracts site name, page title, PWA manifest link, and the highest-quality icon URL (including SVGs and data URIs).
    /// </summary>
    public static HtmlHeadInfo ParseHtmlHead(string htmlHead, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(htmlHead))
        {
            return new HtmlHeadInfo(null, null, null);
        }

        // 1. Check for <base href="...">
        foreach (Match baseTag in BaseTagRegex().Matches(htmlHead))
        {
            var baseHrefMatch = HrefAttrRegex().Match(baseTag.Value);
            if (baseHrefMatch.Success)
            {
                string rawBase = System.Net.WebUtility.HtmlDecode(baseHrefMatch.Groups["href"].Value.Trim());
                if (!string.IsNullOrWhiteSpace(rawBase) &&
                    Uri.TryCreate(baseUri, rawBase, out Uri? customBase) &&
                    (customBase.Scheme == Uri.UriSchemeHttp || customBase.Scheme == Uri.UriSchemeHttps))
                {
                    baseUri = customBase;
                    break;
                }
            }
        }

        // 2. Extract og:site_name
        string? siteName = null;
        var ogMatch = OgSiteNameRegex().Match(htmlHead);
        if (ogMatch.Success)
        {
            siteName = System.Net.WebUtility.HtmlDecode(ogMatch.Groups["siteName"].Value.Trim());
        }

        // 3. Extract <title>
        string? title = null;
        var titleMatch = TitleRegex().Match(htmlHead);
        if (titleMatch.Success)
        {
            title = System.Net.WebUtility.HtmlDecode(titleMatch.Groups["title"].Value.Trim());
            title = MultipleWhitespaceRegex().Replace(title, " ");
        }

        // 4. Extract highest quality icon and PWA manifest
        string? bestIconUrl = null;
        int bestScore = -1;
        string? manifestUrl = null;

        var linkMatches = LinkTagRegex().Matches(htmlHead);
        foreach (Match linkTag in linkMatches)
        {
            string tag = linkTag.Value;

            var relMatch = RelAttrRegex().Match(tag);
            var hrefMatch = HrefAttrRegex().Match(tag);

            if (!relMatch.Success || !hrefMatch.Success) continue;

            string rel = relMatch.Groups["rel"].Value.Trim();
            string rawHref = System.Net.WebUtility.HtmlDecode(hrefMatch.Groups["href"].Value.Trim());

            if (string.IsNullOrWhiteSpace(rawHref)) continue;

            // Check for PWA manifest link
            if (rel.Equals("manifest", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate(baseUri, rawHref, out Uri? resolvedManifest) &&
                    (resolvedManifest.Scheme == Uri.UriSchemeHttp || resolvedManifest.Scheme == Uri.UriSchemeHttps))
                {
                    manifestUrl = resolvedManifest.AbsoluteUri;
                }
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

            string resolvedIconUrl;
            bool isSvg = false;

            if (rawHref.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                resolvedIconUrl = rawHref;
                isSvg = rawHref.Contains("image/svg+xml", StringComparison.OrdinalIgnoreCase) ||
                        rawHref.Contains("<svg", StringComparison.OrdinalIgnoreCase) ||
                        rawHref.Contains("%3Csvg", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                if (!Uri.TryCreate(baseUri, rawHref, out Uri? resolvedUri) ||
                    (resolvedUri.Scheme != Uri.UriSchemeHttp && resolvedUri.Scheme != Uri.UriSchemeHttps))
                {
                    continue;
                }

                resolvedIconUrl = resolvedUri.AbsoluteUri;
                string hrefWithoutQuery = resolvedUri.AbsolutePath;
                if (hrefWithoutQuery.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
                    tag.Contains("image/svg+xml", StringComparison.OrdinalIgnoreCase))
                {
                    isSvg = true;
                }
            }

            bool hasDynamicColor = false;
            if (isSvg)
            {
                if (tag.Contains("currentColor", StringComparison.OrdinalIgnoreCase) ||
                    tag.Contains("prefers-color-scheme", StringComparison.OrdinalIgnoreCase))
                {
                    hasDynamicColor = true;
                }
                else if (rawHref.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    if (WebIconRasterizer.TryParseDataUri(rawHref, out _, out bool isBase64, out string payload))
                    {
                        try
                        {
                            string cleanPayload = payload.Trim();
                            string payloadText;
                            if (isBase64)
                            {
                                if (cleanPayload.Contains('%'))
                                {
                                    try { cleanPayload = Uri.UnescapeDataString(cleanPayload); } catch { }
                                }
                                payloadText = Encoding.UTF8.GetString(Convert.FromBase64String(cleanPayload));
                            }
                            else
                            {
                                try { payloadText = Uri.UnescapeDataString(cleanPayload); } catch { payloadText = cleanPayload; }
                            }

                            if (payloadText.Contains("currentColor", StringComparison.OrdinalIgnoreCase) ||
                                payloadText.Contains("prefers-color-scheme", StringComparison.OrdinalIgnoreCase))
                            {
                                hasDynamicColor = true;
                            }
                        }
                        catch
                        {
                            if (rawHref.Contains("currentColor", StringComparison.OrdinalIgnoreCase) ||
                                rawHref.Contains("prefers-color-scheme", StringComparison.OrdinalIgnoreCase))
                            {
                                hasDynamicColor = true;
                            }
                        }
                    }
                    else if (rawHref.Contains("currentColor", StringComparison.OrdinalIgnoreCase) ||
                             rawHref.Contains("prefers-color-scheme", StringComparison.OrdinalIgnoreCase))
                    {
                        hasDynamicColor = true;
                    }
                }
            }

            int score = isSvg ? (hasDynamicColor ? 900 : 2000) : (isApple ? 1000 : 10);

            var sizesMatch = SizesAttrRegex().Match(tag);
            if (sizesMatch.Success)
            {
                string sizesVal = sizesMatch.Groups["sizes"].Value.Trim();
                if (sizesVal.Equals("any", StringComparison.OrdinalIgnoreCase))
                {
                    if (!hasDynamicColor)
                    {
                        score = Math.Max(score, 2500);
                    }
                }
                else
                {
                    var dimMatches = DimensionsRegex().Matches(sizesVal);
                    int maxDim = 0;
                    foreach (Match m in dimMatches)
                    {
                        if (int.TryParse(m.Groups["width"].Value, CultureInfo.InvariantCulture, out int dim) && dim > maxDim)
                        {
                            maxDim = dim;
                        }
                    }

                    if (maxDim > 0)
                    {
                        if (isSvg)
                        {
                            // SVGs are resolution independent vector graphics
                        }
                        else if (maxDim >= 128 && !isApple)
                        {
                            score = 1000 + maxDim;
                        }
                        else
                        {
                            score += maxDim;
                        }
                    }
                    else if (isApple)
                    {
                        score += 180;
                    }
                }
            }
            else if (isApple)
            {
                score += 180;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestIconUrl = resolvedIconUrl;
            }
        }

        return new HtmlHeadInfo(siteName, title, bestIconUrl, manifestUrl, Math.Max(0, bestScore));
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
        string reqNoWww = requestedHost.StartsWith("www.", StringComparison.Ordinal) ? requestedHost[4..] : requestedHost;
        string finalNoWww = finalHost.StartsWith("www.", StringComparison.Ordinal) ? finalHost[4..] : finalHost;
        if (!string.Equals(reqNoWww, finalNoWww, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Uri baseUri = response.RequestMessage?.RequestUri ?? requestedUri;

        // Stream up to 256 KB, accumulating into memory and checking for </head>
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        byte[] buffer = new byte[4096];
        int totalRead = 0;
        const int maxHeadBytes = 262144; // 256 KB

        while (totalRead < maxHeadBytes)
        {
            int toRead = Math.Min(buffer.Length, maxHeadBytes - totalRead);
            int read = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
            if (read == 0) break;

            int prevLength = (int)ms.Length;
            int overlap = Math.Min(16, prevLength);
            int checkOffset = prevLength - overlap;

            await ms.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            totalRead += read;

            // Adjust checkOffset to start on a UTF-8 character boundary (avoid continuation byte 10xxxxxx)
            byte[] msBuffer = ms.GetBuffer();
            while (checkOffset > 0 && (msBuffer[checkOffset] & 0xC0) == 0x80)
            {
                checkOffset--;
            }

            int checkLength = (int)ms.Length - checkOffset;

            // Check newly read slice (plus overlap) for </head> or <body
            string chunkText = System.Text.Encoding.UTF8.GetString(msBuffer, checkOffset, checkLength);
            if (chunkText.Contains("</head>", StringComparison.OrdinalIgnoreCase) ||
                chunkText.Contains("<body", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        string htmlHead = System.Text.Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        return ParseHtmlHead(htmlHead, baseUri);
    }

    private static void DeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch { }
    }

    /// <summary>
    /// Sniffs if a byte sequence represents HTML document markup (starts with &lt;!DOCTYPE, &lt;html, or &lt;head).
    /// </summary>
    public static bool IsHtmlBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 5) return false;

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bytes = bytes[3..];
        }

        int i = 0;
        while (i < bytes.Length && (bytes[i] == ' ' || bytes[i] == '\t' || bytes[i] == '\r' || bytes[i] == '\n'))
        {
            i++;
        }

        if (i >= bytes.Length) return false;
        bytes = bytes[i..];

        int inspectLen = Math.Min(bytes.Length, 512);
        string header = Encoding.UTF8.GetString(bytes[..inspectLen]).TrimStart();
        return header.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
               header.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
               header.StartsWith("<head", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sniffs if a byte sequence represents non-SVG XML (such as AWS S3 error XML or SOAP responses).
    /// </summary>
    public static bool IsNonSvgXmlBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 5) return false;

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bytes = bytes[3..];
        }

        int i = 0;
        while (i < bytes.Length && (bytes[i] == ' ' || bytes[i] == '\t' || bytes[i] == '\r' || bytes[i] == '\n'))
        {
            i++;
        }

        if (i >= bytes.Length) return false;
        bytes = bytes[i..];

        int inspectLen = Math.Min(bytes.Length, 512);
        string header = Encoding.UTF8.GetString(bytes[..inspectLen]).TrimStart();
        return header.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && !WebIconRasterizer.IsSvgBytes(bytes);
    }

    /// <summary>
    /// Inspects a downloaded candidate file (checking first 512 bytes).
    /// If SVG, rasterizes to PNG in-place (or deletes the file if rasterization fails).
    /// Rejects HTML responses starting with &lt;!DOCTYPE, &lt;html, or &lt;head by deleting the file and returning false.
    /// Rejects non-SVG XML documents (e.g. S3 errors) by deleting the file and returning false.
    /// </summary>
    public static async Task<bool> ValidateAndNormalizeDownloadedIconAsync(string filePath, int targetSize = 256)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        try
        {
            byte[] buffer = new byte[512];
            int bytesRead;
            await using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bytesRead = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
            }

            if (bytesRead <= 0)
            {
                DeleteFileQuietly(filePath);
                return false;
            }

            var span = buffer.AsSpan(0, bytesRead);

            // 1. Reject HTML responses starting with <!DOCTYPE, <html, or <head
            if (IsHtmlBytes(span))
            {
                DeleteFileQuietly(filePath);
                return false;
            }

            // 2. If SVG, rasterize to PNG in-place (deletes file if rasterization fails)
            if (WebIconRasterizer.IsSvgBytes(span))
            {
                return await WebIconRasterizer.TryRasterizeFileIfSvgAsync(filePath, targetSize).ConfigureAwait(false);
            }

            // 3. Reject non-SVG XML responses (e.g. S3 error XML)
            if (IsNonSvgXmlBytes(span))
            {
                DeleteFileQuietly(filePath);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Safe.Ignore(ex, "WebFaviconService.ValidateAndNormalizeDownloadedIcon");
            DeleteFileQuietly(filePath);
            return false;
        }
    }

    /// <summary>
    /// Saves an icon candidate (either data: URI or remote URL) to disk, rasterizing SVGs in-place to genuine PNGs.
    /// </summary>
    private static async Task<bool> SaveIconCandidateAsync(string iconUrl, string destinationPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(iconUrl)) return false;

        if (iconUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return await WebIconRasterizer.TryProcessDataUriToFileAsync(iconUrl, destinationPath, 256).ConfigureAwait(false);
        }

        bool downloaded = await HttpHelper.DownloadFileAsync(
            iconUrl,
            destinationPath,
            minimumBytes: 20,
            ct: ct).ConfigureAwait(false);

        if (downloaded)
        {
            return await ValidateAndNormalizeDownloadedIconAsync(destinationPath).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Fetches PWA Web App Manifest JSON and extracts the highest quality transparent icon candidate (192px/256px/512px/SVG).
    /// </summary>
    private static async Task<bool> TryExtractIconFromManifestAsync(
        string manifestUrl,
        string destinationPath,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl)) return false;

        try
        {
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, manifestUrl);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            using var response = await HttpHelper.Client.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;

            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("icons", out var iconsElement) ||
                iconsElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return false;
            }

            string? bestSrc = null;
            int bestManifestScore = -1;

            foreach (var icon in iconsElement.EnumerateArray())
            {
                if (!icon.TryGetProperty("src", out var srcProp)) continue;
                string? src = srcProp.GetString();
                if (string.IsNullOrWhiteSpace(src)) continue;

                string sizes = icon.TryGetProperty("sizes", out var sProp) ? (sProp.GetString() ?? string.Empty) : string.Empty;
                string type = icon.TryGetProperty("type", out var tProp) ? (tProp.GetString() ?? string.Empty) : string.Empty;
                string purpose = icon.TryGetProperty("purpose", out var pProp) ? (pProp.GetString() ?? string.Empty) : string.Empty;

                bool isSvg = type.Contains("svg", StringComparison.OrdinalIgnoreCase) || src.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
                int score = isSvg ? 2000 : 100;

                if (sizes.Contains("any", StringComparison.OrdinalIgnoreCase)) score += 500;
                else if (sizes.Contains("512x512", StringComparison.OrdinalIgnoreCase)) score += 512;
                else if (sizes.Contains("256x256", StringComparison.OrdinalIgnoreCase)) score += 256;
                else if (sizes.Contains("192x192", StringComparison.OrdinalIgnoreCase)) score += 192;
                else
                {
                    int maxW = 0;
                    foreach (Match m in DimensionsRegex().Matches(sizes))
                    {
                        if (int.TryParse(m.Groups["width"].Value, CultureInfo.InvariantCulture, out int w) && w > maxW)
                        {
                            maxW = w;
                        }
                    }

                    if (maxW > 0)
                    {
                        score += maxW;
                    }
                }

                if (purpose.Contains("maskable", StringComparison.OrdinalIgnoreCase) &&
                    !purpose.Contains("any", StringComparison.OrdinalIgnoreCase))
                {
                    score -= 100;
                }

                if (score > bestManifestScore)
                {
                    bestManifestScore = score;
                    bestSrc = src;
                }
            }

            if (string.IsNullOrWhiteSpace(bestSrc)) return false;

            if (bestSrc.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return await SaveIconCandidateAsync(bestSrc, destinationPath, ct).ConfigureAwait(false);
            }

            if (!Uri.TryCreate(new Uri(manifestUrl), bestSrc, out Uri? resolvedManifestIcon))
            {
                return false;
            }

            return await SaveIconCandidateAsync(resolvedManifestIcon.AbsoluteUri, destinationPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Safe.Ignore(ex, "WebFaviconService.TryExtractIconFromManifest");
            return false;
        }
    }

    /// <summary>
    /// Streams up to 64 KB of the page HTML head, stops at </head>, checks for host redirects,
    /// parses the highest quality icon and title, and downloads the icon if found.
    /// Supports inline SVG data URIs, PWA manifests, and post-download disguised SVG sniffing.
    /// </summary>
    private static async Task<HtmlHeadInfo?> TryExtractFromHtmlHeadAsync(
        string url,
        string destinationPath,
        CancellationToken ct)
    {
        var headInfo = await ExtractHtmlHeadInfoAsync(url, ct).ConfigureAwait(false);
        if (headInfo == null) return null;

        bool iconSaved = false;

        // If head icon is high quality (score >= 800), try saving it first
        if (!string.IsNullOrWhiteSpace(headInfo.IconUrl) && headInfo.IconScore >= 800)
        {
            iconSaved = await SaveIconCandidateAsync(headInfo.IconUrl, destinationPath, ct).ConfigureAwait(false);
        }

        // If no high quality icon yet, check PWA manifest
        if (!iconSaved && !string.IsNullOrWhiteSpace(headInfo.ManifestUrl))
        {
            iconSaved = await TryExtractIconFromManifestAsync(headInfo.ManifestUrl, destinationPath, ct).ConfigureAwait(false);
        }

        // If still no icon and head had a low-res icon (< 800), fall back to it
        if (!iconSaved && !string.IsNullOrWhiteSpace(headInfo.IconUrl))
        {
            iconSaved = await SaveIconCandidateAsync(headInfo.IconUrl, destinationPath, ct).ConfigureAwait(false);
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
                Safe.Ignore(ex, "WebFaviconService.InspectCachedFavicon");
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
            Safe.Ignore(ex, "WebFaviconService.Tier1HeadScraping");
        }

        // Tier 2: Path-Aware Google S2 Dynamic Favicon Service (128x128)
        try
        {
            string googlePathUrl = $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(queryUrl)}&sz=128";
            if (await HttpHelper.DownloadFileAsync(googlePathUrl, cachedPath, minimumBytes: 200, ct: overallCts.Token).ConfigureAwait(false))
            {
                if (await ValidateAndNormalizeDownloadedIconAsync(cachedPath).ConfigureAwait(false))
                {
                    _memoryCache[scopeKey] = cachedPath;
                    return new WebFaviconResult(cachedPath, resolvedTitle);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Safe.Ignore(ex, "WebFaviconService.Tier2GoogleS2Path");
        }

        // Tier 3: Google Base Domain Fallback (if queryUrl had a sub-path)
        if (!string.Equals(queryUrl, domain, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(queryUrl, $"https://{domain}/", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string googleDomainUrl = $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(domain)}&sz=128";
                if (await HttpHelper.DownloadFileAsync(googleDomainUrl, cachedPath, minimumBytes: 200, ct: overallCts.Token).ConfigureAwait(false))
                {
                    if (await ValidateAndNormalizeDownloadedIconAsync(cachedPath).ConfigureAwait(false))
                    {
                        _memoryCache[scopeKey] = cachedPath;
                        return new WebFaviconResult(cachedPath, resolvedTitle);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Safe.Ignore(ex, "WebFaviconService.Tier3GoogleS2Domain");
            }
        }

        // Tier 4: DuckDuckGo Icons Service
        try
        {
            string ddgUrl = $"https://icons.duckduckgo.com/ip3/{Uri.EscapeDataString(domain)}.ico";
            if (await HttpHelper.DownloadFileAsync(ddgUrl, cachedPath, minimumBytes: 200, ct: overallCts.Token).ConfigureAwait(false))
            {
                if (await ValidateAndNormalizeDownloadedIconAsync(cachedPath).ConfigureAwait(false))
                {
                    _memoryCache[scopeKey] = cachedPath;
                    return new WebFaviconResult(cachedPath, resolvedTitle);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Safe.Ignore(ex, "WebFaviconService.Tier4DuckDuckGo");
        }

        // Tier 5: Direct website root favicon
        try
        {
            string directUrl = $"https://{domain}/favicon.ico";
            if (await HttpHelper.DownloadFileAsync(directUrl, cachedPath, minimumBytes: 200, ct: overallCts.Token).ConfigureAwait(false))
            {
                if (await ValidateAndNormalizeDownloadedIconAsync(cachedPath).ConfigureAwait(false))
                {
                    _memoryCache[scopeKey] = cachedPath;
                    return new WebFaviconResult(cachedPath, resolvedTitle);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Safe.Ignore(ex, "WebFaviconService.Tier5DirectFavicon");
        }

        return new WebFaviconResult(null, resolvedTitle);
    }

    /// <summary>
    /// Downloads remote icon bytes directly into memory without touching the local disk.
    /// Supports inline data URIs and disguised SVGs (rasterizing to genuine PNG bytes).
    /// </summary>
    public static Task<byte[]?> DownloadIconBytesAsync(string iconUrl, CancellationToken ct = default)
        => DownloadIconBytesAsync(iconUrl, 200, ct);

    /// <summary>
    /// Downloads remote icon bytes directly into memory without touching the local disk.
    /// Supports inline data URIs and disguised SVGs (rasterizing to genuine PNG bytes).
    /// </summary>
    public static async Task<byte[]?> DownloadIconBytesAsync(string iconUrl, int minimumBytes, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(iconUrl)) return null;

        if (iconUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return await WebIconRasterizer.TryProcessDataUriToBytesAsync(iconUrl, 256).ConfigureAwait(false);
        }

        try
        {
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, iconUrl);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            using var response = await HttpHelper.Client.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length == 0) return null;

            if (IsHtmlBytes(bytes) || IsNonSvgXmlBytes(bytes))
            {
                return null;
            }

            if (WebIconRasterizer.IsSvgBytes(bytes))
            {
                string svgText = Encoding.UTF8.GetString(bytes);
                byte[]? pngBytes = await WebIconRasterizer.RasterizeSvgToPngBytesAsync(svgText, 256).ConfigureAwait(false);
                if (pngBytes != null && pngBytes.Length > 0)
                {
                    return pngBytes;
                }
                return null;
            }

            if (minimumBytes > 0 && bytes.Length < minimumBytes) return null;

            return bytes;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Safe.Ignore(ex, "WebFaviconService.DownloadIconBytes");
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
    /// updating in-memory memoization. If bytes represent SVG XML, rasterizes to PNG.
    /// </summary>
    public static string? SaveIconToCache(string url, byte[] bytes)
    {
        if (string.IsNullOrWhiteSpace(url) || bytes == null || bytes.Length == 0) return null;

        try
        {
            string cachedPath = GetCachedIconPath(url);
            if (string.IsNullOrWhiteSpace(cachedPath)) return null;

            if (IsHtmlBytes(bytes) || IsNonSvgXmlBytes(bytes))
            {
                DeleteFileQuietly(cachedPath);
                return null;
            }

            string? dir = Path.GetDirectoryName(cachedPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (WebIconRasterizer.IsSvgBytes(bytes))
            {
                string svgText = Encoding.UTF8.GetString(bytes);
                bool rasterized = WebIconRasterizer.RasterizeSvgToPngFileAsync(svgText, cachedPath, 256).GetAwaiter().GetResult();
                if (!rasterized)
                {
                    DeleteFileQuietly(cachedPath);
                    return null;
                }
            }
            else
            {
                File.WriteAllBytes(cachedPath, bytes);
            }

            string scopeKey = ComputeCacheScopeKey(url);
            if (!string.IsNullOrWhiteSpace(scopeKey))
            {
                _memoryCache[scopeKey] = cachedPath;
            }

            return cachedPath;
        }
        catch (Exception ex)
        {
            Safe.Ignore(ex, "WebFaviconService.SaveIconToCache");
            return null;
        }
    }
}

/// <summary>
/// Parsed metadata from HTML head.
/// </summary>
public sealed record HtmlHeadInfo(string? SiteName, string? Title, string? IconUrl, string? ManifestUrl = null, int IconScore = 0);

/// <summary>
/// Result of favicon resolution, implicitly convertible to string? (icon path).
/// </summary>
public readonly record struct WebFaviconResult(string? IconPath, string? Title)
{
    public static implicit operator string?(WebFaviconResult res) => res.IconPath;
}
