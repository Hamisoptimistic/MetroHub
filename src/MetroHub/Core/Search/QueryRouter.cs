using System;
using MetroHub.Core.Models;

namespace MetroHub.Core.Search;

/// <summary>
/// Target route destination determined by QueryRouter.
/// </summary>
public enum RouteKind
{
    Universal,
    Terminal,
    Settings,
    ProcessKiller,
    WindowWalker,
    WorldClock,
    Volume,
    TextTransform,
    Clipboard,
    WebBangGoogle,
    WebBangGitHub,
    WebBangYouTube,
    WebBangDuckDuckGo,
    Audio,
    Help
}

/// <summary>
/// Normalized query route evaluation result.
/// </summary>
public readonly record struct RouteResult(
    RouteKind Kind,
    string Remainder,
    bool IsHardRoute,
    bool IsAdditive);

/// <summary>
/// Fast zero-allocation prefix router driven by modular SearchSettings.
/// Enforces Hard routes for symbolic sigils, Soft-Exclusive routes with universal fallthrough,
/// and Additive routes for bangs and audio controls.
/// </summary>
public sealed class QueryRouter
{
    private SearchSettings _settings;
    private readonly object _lock = new();

    public QueryRouter(SearchSettings? settings = null)
    {
        _settings = settings ?? new SearchSettings();
    }

    public void Update(SearchSettings? settings)
    {
        lock (_lock)
        {
            _settings = settings ?? new SearchSettings();
        }
    }

    public RouteResult Route(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new RouteResult(RouteKind.Universal, string.Empty, false, false);
        }

        string raw = text.TrimStart();
        SearchSettings s;
        lock (_lock)
        {
            s = _settings;
        }

        // 0. Interactive Help / Shortcuts (e.g. "?", "help", "tips", "shortcuts")
        if (raw.StartsWith('?') ||
            string.Equals(raw, "help", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("help ", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "tips", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("tips ", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "shortcuts", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("shortcuts ", StringComparison.OrdinalIgnoreCase))
        {
            string rem = raw.StartsWith('?')
                ? raw[1..].TrimStart()
                : raw.StartsWith("help ", StringComparison.OrdinalIgnoreCase) ? raw[5..].TrimStart()
                : raw.StartsWith("tips ", StringComparison.OrdinalIgnoreCase) ? raw[5..].TrimStart()
                : raw.StartsWith("shortcuts ", StringComparison.OrdinalIgnoreCase) ? raw[10..].TrimStart()
                : string.Empty;
            return new RouteResult(RouteKind.Help, rem, IsHardRoute: true, IsAdditive: false);
        }

        // 1. Hard Symbolic Sigils (exclusively handled by designated source)
        string termPrefix = GetSafePrefix(s.TerminalPrefix, ">");
        if (raw.StartsWith(termPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new RouteResult(RouteKind.Terminal, raw[termPrefix.Length..].TrimStart(), IsHardRoute: true, IsAdditive: false);
        }
        if (raw.StartsWith("cmd ", StringComparison.OrdinalIgnoreCase))
        {
            return new RouteResult(RouteKind.Terminal, raw[4..].TrimStart(), IsHardRoute: true, IsAdditive: false);
        }

        string setPrefix = GetSafePrefix(s.SettingsPrefix, "set:");
        if (raw.StartsWith(setPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new RouteResult(RouteKind.Settings, raw[setPrefix.Length..].TrimStart(), IsHardRoute: true, IsAdditive: false);
        }

        // 2. Soft-Exclusive Word Triggers (attempted first; fall through to universal if 0 results)
        string killPrefix = GetSafePrefix(s.ProcessKillerPrefix, "kill ");
        if (raw.StartsWith(killPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new RouteResult(RouteKind.ProcessKiller, raw[killPrefix.Length..].TrimStart(), IsHardRoute: false, IsAdditive: false);
        }
        if (string.Equals(raw, "kill", StringComparison.OrdinalIgnoreCase))
        {
            return new RouteResult(RouteKind.ProcessKiller, string.Empty, IsHardRoute: false, IsAdditive: false);
        }
        if (raw.StartsWith("taskkill ", StringComparison.OrdinalIgnoreCase))
        {
            return new RouteResult(RouteKind.ProcessKiller, raw[9..].TrimStart(), IsHardRoute: false, IsAdditive: false);
        }

        string winPrefix = GetSafePrefix(s.WindowWalkerPrefix, "win ");
        if (raw.StartsWith(winPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new RouteResult(RouteKind.WindowWalker, raw[winPrefix.Length..].TrimStart(), IsHardRoute: false, IsAdditive: false);
        }
        if (string.Equals(raw, "win", StringComparison.OrdinalIgnoreCase))
        {
            return new RouteResult(RouteKind.WindowWalker, string.Empty, IsHardRoute: false, IsAdditive: false);
        }
        if (raw.StartsWith("window ", StringComparison.OrdinalIgnoreCase))
        {
            return new RouteResult(RouteKind.WindowWalker, raw[7..].TrimStart(), IsHardRoute: false, IsAdditive: false);
        }
        if (raw.StartsWith("windows ", StringComparison.OrdinalIgnoreCase))
        {
            return new RouteResult(RouteKind.WindowWalker, raw[8..].TrimStart(), IsHardRoute: false, IsAdditive: false);
        }

        string volPrefix = GetSafePrefix(s.VolumePrefix, "vol ");
        if (raw.StartsWith(volPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new RouteResult(RouteKind.Volume, raw[volPrefix.Length..].TrimStart(), IsHardRoute: false, IsAdditive: false);
        }
        if (string.Equals(raw, "vol", StringComparison.OrdinalIgnoreCase))
        {
            return new RouteResult(RouteKind.Volume, string.Empty, IsHardRoute: false, IsAdditive: false);
        }
        if (raw.StartsWith("volume ", StringComparison.OrdinalIgnoreCase))
        {
            return new RouteResult(RouteKind.Volume, raw[7..].TrimStart(), IsHardRoute: false, IsAdditive: false);
        }

        string timePrefix = GetSafePrefix(s.WorldClockPrefix, "time ");
        if (raw.StartsWith(timePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new RouteResult(RouteKind.WorldClock, raw[timePrefix.Length..].TrimStart(), IsHardRoute: false, IsAdditive: false);
        }
        if (raw.StartsWith("clock ", StringComparison.OrdinalIgnoreCase))
        {
            return new RouteResult(RouteKind.WorldClock, raw[6..].TrimStart(), IsHardRoute: false, IsAdditive: false);
        }

        string casePrefix = GetSafePrefix(s.TextTransformPrefix, "case ");
        if (raw.StartsWith(casePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new RouteResult(RouteKind.TextTransform, raw[casePrefix.Length..].TrimStart(), IsHardRoute: false, IsAdditive: false);
        }

        string cbPrefix = GetSafePrefix(s.ClipboardPrefix, "cb ");
        if (raw.StartsWith(cbPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new RouteResult(RouteKind.Clipboard, raw[cbPrefix.Length..].TrimStart(), IsHardRoute: false, IsAdditive: false);
        }
        if (string.Equals(raw, "cb", StringComparison.OrdinalIgnoreCase))
        {
            return new RouteResult(RouteKind.Clipboard, string.Empty, IsHardRoute: false, IsAdditive: false);
        }
        if (raw.StartsWith("clip ", StringComparison.OrdinalIgnoreCase))
        {
            return new RouteResult(RouteKind.Clipboard, raw[5..].TrimStart(), IsHardRoute: false, IsAdditive: false);
        }
        if (raw.StartsWith("clipboard ", StringComparison.OrdinalIgnoreCase))
        {
            return new RouteResult(RouteKind.Clipboard, raw[10..].TrimStart(), IsHardRoute: false, IsAdditive: false);
        }

        // 3. Additive Bang Triggers (augment universal results)
        string gBang = GetSafePrefix(s.GoogleBang, "g ");
        if (raw.StartsWith(gBang, StringComparison.OrdinalIgnoreCase))
        {
            return new RouteResult(RouteKind.WebBangGoogle, raw[gBang.Length..].TrimStart(), IsHardRoute: false, IsAdditive: true);
        }

        string ghBang = GetSafePrefix(s.GitHubBang, "gh ");
        if (raw.StartsWith(ghBang, StringComparison.OrdinalIgnoreCase))
        {
            return new RouteResult(RouteKind.WebBangGitHub, raw[ghBang.Length..].TrimStart(), IsHardRoute: false, IsAdditive: true);
        }

        string ytBang = GetSafePrefix(s.YouTubeBang, "yt ");
        if (raw.StartsWith(ytBang, StringComparison.OrdinalIgnoreCase))
        {
            return new RouteResult(RouteKind.WebBangYouTube, raw[ytBang.Length..].TrimStart(), IsHardRoute: false, IsAdditive: true);
        }

        string ddgBang = GetSafePrefix(s.DuckDuckGoBang, "ddg ");
        if (raw.StartsWith(ddgBang, StringComparison.OrdinalIgnoreCase))
        {
            return new RouteResult(RouteKind.WebBangDuckDuckGo, raw[ddgBang.Length..].TrimStart(), IsHardRoute: false, IsAdditive: true);
        }

        string audioTrig = GetSafePrefix(s.AudioTrigger, "audio");
        if (string.Equals(raw, audioTrig, StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith(audioTrig + " ", StringComparison.OrdinalIgnoreCase))
        {
            string rem = raw.Length > audioTrig.Length ? raw[audioTrig.Length..].TrimStart() : string.Empty;
            return new RouteResult(RouteKind.Audio, rem, IsHardRoute: false, IsAdditive: true);
        }

        return new RouteResult(RouteKind.Universal, raw, IsHardRoute: false, IsAdditive: false);
    }

    private static string GetSafePrefix(string? configured, string fallback)
    {
        return string.IsNullOrWhiteSpace(configured) ? fallback : configured;
    }
}

