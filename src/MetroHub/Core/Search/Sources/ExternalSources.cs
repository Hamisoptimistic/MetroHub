using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MetroHub.Core.Common;
using MetroHub.Core.Search.Native;
using MetroHub.Core.Services;
using Wpf.Ui.Controls;

namespace MetroHub.Core.Search;

/// <summary>
/// World clock and Unix epoch timestamp search source.
/// Evaluates queries like "time in tokyo", "time london", "time", and "epoch 1700000000".
/// </summary>
public sealed class WorldClockSource : ISearchSource
{
    public string SourceId => "world_clock";
    public SourceState State => SourceState.Ready;

    private readonly QueryRouter _router;

    private static readonly FrozenDictionary<string, (string DisplayName, string TimeZoneId)> CityTimeZones = new Dictionary<string, (string DisplayName, string TimeZoneId)>(StringComparer.OrdinalIgnoreCase)
    {
        ["utc"] = ("UTC (Coordinated Universal Time)", "UTC"),
        ["gmt"] = ("London, United Kingdom (GMT)", "GMT Standard Time"),
        ["london"] = ("London, United Kingdom", "GMT Standard Time"),
        ["tokyo"] = ("Tokyo, Japan", "Tokyo Standard Time"),
        ["japan"] = ("Tokyo, Japan", "Tokyo Standard Time"),
        ["jst"] = ("Japan Standard Time (JST)", "Tokyo Standard Time"),
        ["new york"] = ("New York, United States", "Eastern Standard Time"),
        ["nyc"] = ("New York, United States", "Eastern Standard Time"),
        ["est"] = ("Eastern Standard Time (EST)", "Eastern Standard Time"),
        ["edt"] = ("Eastern Daylight Time (EDT)", "Eastern Standard Time"),
        ["los angeles"] = ("Los Angeles, United States", "Pacific Standard Time"),
        ["la"] = ("Los Angeles, United States", "Pacific Standard Time"),
        ["san francisco"] = ("San Francisco, United States", "Pacific Standard Time"),
        ["sf"] = ("San Francisco, United States", "Pacific Standard Time"),
        ["seattle"] = ("Seattle, United States", "Pacific Standard Time"),
        ["pst"] = ("Pacific Standard Time (PST)", "Pacific Standard Time"),
        ["pdt"] = ("Pacific Daylight Time (PDT)", "Pacific Standard Time"),
        ["chicago"] = ("Chicago, United States", "Central Standard Time"),
        ["cst"] = ("Central Standard Time (CST)", "Central Standard Time"),
        ["cdt"] = ("Central Daylight Time (CDT)", "Central Standard Time"),
        ["denver"] = ("Denver, United States", "Mountain Standard Time"),
        ["mst"] = ("Mountain Standard Time (MST)", "Mountain Standard Time"),
        ["mdt"] = ("Mountain Daylight Time (MDT)", "Mountain Standard Time"),
        ["paris"] = ("Paris, France", "Romance Standard Time"),
        ["berlin"] = ("Berlin, Germany", "W. Europe Standard Time"),
        ["rome"] = ("Rome, Italy", "W. Europe Standard Time"),
        ["madrid"] = ("Madrid, Spain", "Romance Standard Time"),
        ["amsterdam"] = ("Amsterdam, Netherlands", "W. Europe Standard Time"),
        ["brussels"] = ("Brussels, Belgium", "Romance Standard Time"),
        ["cet"] = ("Central European Time (CET)", "Central Europe Standard Time"),
        ["sydney"] = ("Sydney, Australia", "AUS Eastern Standard Time"),
        ["melbourne"] = ("Melbourne, Australia", "AUS Eastern Standard Time"),
        ["aest"] = ("Australian Eastern Standard Time (AEST)", "AUS Eastern Standard Time"),
        ["singapore"] = ("Singapore", "Singapore Standard Time"),
        ["sgt"] = ("Singapore Standard Time (SGT)", "Singapore Standard Time"),
        ["hong kong"] = ("Hong Kong", "China Standard Time"),
        ["hkt"] = ("Hong Kong Time (HKT)", "China Standard Time"),
        ["beijing"] = ("Beijing, China", "China Standard Time"),
        ["shanghai"] = ("Shanghai, China", "China Standard Time"),
        ["dubai"] = ("Dubai, United Arab Emirates", "Arabian Standard Time"),
        ["uae"] = ("Dubai, United Arab Emirates", "Arabian Standard Time"),
        ["gst"] = ("Gulf Standard Time (GST)", "Arabian Standard Time"),
        ["mumbai"] = ("Mumbai, India", "India Standard Time"),
        ["delhi"] = ("New Delhi, India", "India Standard Time"),
        ["bangalore"] = ("Bangalore, India", "India Standard Time"),
        ["india"] = ("India", "India Standard Time"),
        ["ist"] = ("India Standard Time (IST)", "India Standard Time"),
        ["toronto"] = ("Toronto, Canada", "Eastern Standard Time"),
        ["vancouver"] = ("Vancouver, Canada", "Pacific Standard Time"),
        ["seoul"] = ("Seoul, South Korea", "Korea Standard Time"),
        ["korea"] = ("Seoul, South Korea", "Korea Standard Time"),
        ["kst"] = ("Korea Standard Time (KST)", "Korea Standard Time"),
        ["auckland"] = ("Auckland, New Zealand", "New Zealand Standard Time"),
        ["new zealand"] = ("Auckland, New Zealand", "New Zealand Standard Time"),
        ["nzst"] = ("New Zealand Standard Time", "New Zealand Standard Time"),
        ["cairo"] = ("Cairo, Egypt", "Egypt Standard Time"),
        ["moscow"] = ("Moscow, Russia", "Russian Standard Time"),
        ["sao paulo"] = ("São Paulo, Brazil", "E. South America Standard Time"),
        ["honolulu"] = ("Honolulu, Hawaii", "Hawaiian Standard Time"),
        ["hawaii"] = ("Honolulu, Hawaii", "Hawaiian Standard Time"),
        ["hst"] = ("Hawaii Standard Time (HST)", "Hawaiian Standard Time")
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public WorldClockSource(QueryRouter? router = null)
    {
        _router = router ?? new QueryRouter();
    }

    public Task<IReadOnlyList<Candidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || query.IsEmpty)
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        string raw = query.RawText.Trim();

        // 1. Check for epoch timestamp query
        if (raw.StartsWith("epoch ", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("epoch:", StringComparison.OrdinalIgnoreCase))
        {
            string numStr = raw[5..].TrimStart(':', ' ').Trim();
            if (long.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ts))
            {
                DateTimeOffset dto = ts < 100_000_000_000L
                    ? DateTimeOffset.FromUnixTimeSeconds(ts)
                    : DateTimeOffset.FromUnixTimeMilliseconds(ts);

                string utcStr = dto.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
                string localStr = dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss (zzz)", CultureInfo.InvariantCulture);

                var candidate = new Candidate(
                    Id: $"epoch:{ts}",
                    DisplayName: utcStr,
                    FullPathOrKey: $"Local: {localStr} • Timestamp: {ts}",
                    Category: SearchCategory.Actions,
                    SourceId: SourceId,
                    Modified: null,
                    Size: null,
                    IsFolder: false,
                    Tag: utcStr)
                {
                    Icon = SymbolRegular.Clock24,
                    KindLabel = "World Clock",
                    PrimaryAction = new CandidateAction(
                        "copy_epoch",
                        "Copy UTC timestamp",
                        "Enter",
                        _ =>
                        {
                            Application.Current?.Dispatcher?.Invoke(() => Clipboard.SetText(utcStr));
                            return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer, "Timestamp copied to clipboard"));
                        })
                };

                return Task.FromResult<IReadOnlyList<Candidate>>([candidate]);
            }
        }

        // 2. Check for world clock query ("time in <city>", "time <city>", or "time")
        var route = _router.Route(raw);
        bool isTriggered = route.Kind == RouteKind.WorldClock ||
                           raw.StartsWith("time in ", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(raw, "time", StringComparison.OrdinalIgnoreCase);

        if (!isTriggered)
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        string target = string.Empty;
        if (raw.StartsWith("time in ", StringComparison.OrdinalIgnoreCase))
        {
            target = raw[8..].Trim();
        }
        else if (route.Kind == RouteKind.WorldClock)
        {
            target = route.Remainder.Trim();
            if (target.StartsWith("in ", StringComparison.OrdinalIgnoreCase))
            {
                target = target[3..].Trim();
            }
        }

        var results = new List<Candidate>();
        var nowUtc = DateTimeOffset.UtcNow;

        if (string.IsNullOrEmpty(target))
        {
            // Just "time": Show local time and UTC
            var localTime = DateTimeOffset.Now;
            string localDisplay = localTime.ToString("h:mm tt", CultureInfo.InvariantCulture);
            string localFull = localTime.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture);

            results.Add(new Candidate(
                Id: "time:local",
                DisplayName: $"Local Time: {localDisplay}",
                FullPathOrKey: $"{localFull} • {TimeZoneInfo.Local.DisplayName}",
                Category: SearchCategory.Actions,
                SourceId: SourceId,
                Modified: null,
                Size: null,
                IsFolder: false,
                Tag: localDisplay)
            {
                Icon = SymbolRegular.Clock24,
                KindLabel = "World Clock",
                PrimaryAction = new CandidateAction(
                    "copy_time_local",
                    "Copy local time",
                    "Enter",
                    _ =>
                    {
                        Application.Current?.Dispatcher?.Invoke(() => Clipboard.SetText(localDisplay));
                        return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer, "Time copied to clipboard"));
                    })
            });

            string utcDisplay = nowUtc.ToString("HH:mm 'UTC'", CultureInfo.InvariantCulture);
            results.Add(new Candidate(
                Id: "time:utc",
                DisplayName: $"UTC: {utcDisplay}",
                FullPathOrKey: nowUtc.ToString("dddd, MMMM d, yyyy 'UTC'", CultureInfo.InvariantCulture),
                Category: SearchCategory.Actions,
                SourceId: SourceId,
                Modified: null,
                Size: null,
                IsFolder: false,
                Tag: utcDisplay)
            {
                Icon = SymbolRegular.Clock24,
                KindLabel = "World Clock",
                PrimaryAction = new CandidateAction(
                    "copy_time_utc",
                    "Copy UTC time",
                    "Enter",
                    _ =>
                    {
                        Application.Current?.Dispatcher?.Invoke(() => Clipboard.SetText(utcDisplay));
                        return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer, "Time copied to clipboard"));
                    })
            });

            return Task.FromResult<IReadOnlyList<Candidate>>(results);
        }

        // Resolve city to TimeZoneInfo
        (string DisplayName, TimeZoneInfo? Zone) resolved = ResolveTimeZone(target);
        if (resolved.Zone != null)
        {
            var targetTime = TimeZoneInfo.ConvertTime(nowUtc, resolved.Zone);
            string timeStr = targetTime.ToString("h:mm tt", CultureInfo.InvariantCulture);
            string dateStr = targetTime.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture);
            var offset = resolved.Zone.GetUtcOffset(targetTime);
            string offsetStr = $"UTC{(offset >= TimeSpan.Zero ? "+" : "")}{offset.Hours:D2}:{Math.Abs(offset.Minutes):D2}";

            string fullDisplay = $"{resolved.DisplayName}: {timeStr}";
            string subtitle = $"{dateStr} • {offsetStr}";

            var candidate = new Candidate(
                Id: $"time:{resolved.Zone.Id}",
                DisplayName: fullDisplay,
                FullPathOrKey: subtitle,
                Category: SearchCategory.Actions,
                SourceId: SourceId,
                Modified: null,
                Size: null,
                IsFolder: false,
                Tag: timeStr)
            {
                Icon = SymbolRegular.Clock24,
                KindLabel = "World Clock",
                PrimaryAction = new CandidateAction(
                    "copy_time",
                    "Copy time",
                    "Enter",
                    _ =>
                    {
                        Application.Current?.Dispatcher?.Invoke(() => Clipboard.SetText($"{fullDisplay} ({offsetStr})"));
                        return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer, "Time copied to clipboard"));
                    })
            };

            results.Add(candidate);
        }

        return Task.FromResult<IReadOnlyList<Candidate>>(results);
    }

    private static (string DisplayName, TimeZoneInfo? Zone) ResolveTimeZone(string query)
    {
        if (CityTimeZones.TryGetValue(query, out var match))
        {
            try
            {
                return (match.DisplayName, TimeZoneInfo.FindSystemTimeZoneById(match.TimeZoneId));
            }
            catch
            {
                // Fall back to system scan below
            }
        }

        // Scan system time zones for partial match on ID or DisplayName
        foreach (var z in TimeZoneInfo.GetSystemTimeZones())
        {
            if (z.Id.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                z.DisplayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return (z.DisplayName, z);
            }
        }

        return (query, null);
    }
}


/// <summary>
/// Clipboard history search source with Win32 format listening,
/// 16 KB snippet cap, 50-item ring buffer, and sensitive password manager exclusion.
/// </summary>
public sealed class ClipboardSource : ISearchSource, IDisposable
{
    public string SourceId => "clipboard";
    public SourceState State => SourceState.Ready;

    public static ClipboardSource? Instance { get; private set; }

    private readonly QueryRouter _router;
    private readonly object _lock = new();
    private readonly List<ClipboardItem> _history = new(50);
    private uint _lastSequenceNumber = 0;
    private IntPtr _hwnd = IntPtr.Zero;
    private bool _isListening = false;
    private bool _isDisposed = false;

    public sealed record ClipboardItem(
        string Text,
        DateTimeOffset Timestamp);

    public ClipboardSource(QueryRouter? router = null)
    {
        _router = router ?? new QueryRouter();
        Instance = this;
    }

    public void AttachHwnd(IntPtr hwnd)
    {
        if (_hwnd != IntPtr.Zero && _isListening)
        {
            NativeMethodsSearch.RemoveClipboardFormatListener(_hwnd);
            _isListening = false;
        }

        _hwnd = hwnd;
        if (_hwnd != IntPtr.Zero)
        {
            try
            {
                _isListening = NativeMethodsSearch.AddClipboardFormatListener(_hwnd);
            }
            catch (Exception ex)
            {
                Safe.Log(ex, "ClipboardSource.AttachHwnd");
            }
        }
    }

    public void OnClipboardUpdate()
    {
        if (_isDisposed) return;

        try
        {
            uint seq = NativeMethodsSearch.GetClipboardSequenceNumber();
            if (seq != 0 && seq == _lastSequenceNumber) return;

            IDataObject? dataObject = SafeClipboard.GetDataObject();
            if (dataObject == null) return;

            if (SafeClipboard.IsSensitiveData(dataObject)) return;

            if (dataObject.GetDataPresent(DataFormats.UnicodeText) || dataObject.GetDataPresent(DataFormats.Text))
            {
                string? text = dataObject.GetData(DataFormats.UnicodeText) as string ??
                               dataObject.GetData(DataFormats.Text) as string;

                if (!string.IsNullOrWhiteSpace(text))
                {
                    RecordClip(text);
                }
            }
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "ClipboardSource.OnClipboardUpdate");
        }
    }

    /// <summary>
    /// Thread-safely records a text snippet into the 50-item ring buffer.
    /// Deduplicates consecutive identical entries and enforces a 16 KB snippet limit.
    /// </summary>
    public void RecordClip(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        if (text.Length > 16384)
        {
            text = text[..16384];
        }

        lock (_lock)
        {
            if (_history.Count > 0 && string.Equals(_history[0].Text, text, StringComparison.Ordinal))
            {
                _history[0] = new ClipboardItem(text, DateTimeOffset.UtcNow);
                return;
            }

            _history.RemoveAll(x => string.Equals(x.Text, text, StringComparison.Ordinal));
            _history.Insert(0, new ClipboardItem(text, DateTimeOffset.UtcNow));

            if (_history.Count > 50)
            {
                _history.RemoveAt(_history.Count - 1);
            }
        }
    }

    public Task<IReadOnlyList<Candidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        string raw = query.RawText.Trim();
        var route = _router.Route(raw);

        bool isCb = route.Kind == RouteKind.Clipboard ||
                    string.Equals(raw, "cb", StringComparison.OrdinalIgnoreCase);

        if (!isCb)
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        string filter = route.Kind == RouteKind.Clipboard ? route.Remainder.Trim() : string.Empty;

        List<ClipboardItem> snapshot;
        lock (_lock)
        {
            snapshot = new List<ClipboardItem>(_history);
        }

        var candidates = new List<Candidate>(Math.Min(15, snapshot.Count));
        for (int i = 0; i < snapshot.Count && candidates.Count < 15; i++)
        {
            var item = snapshot[i];
            if (!string.IsNullOrEmpty(filter) &&
                item.Text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            string preview = item.Text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (preview.Length > 80)
            {
                preview = string.Concat(preview.AsSpan(0, 77), "...");
            }

            string subtitle = $"{item.Text.Length} chars • {FormatTimeAgo(item.Timestamp)}";

            candidates.Add(new Candidate(
                Id: $"clipboard:{item.Timestamp.ToUnixTimeMilliseconds()}:{item.Text.GetHashCode()}",
                DisplayName: preview,
                FullPathOrKey: subtitle,
                Category: SearchCategory.Actions,
                SourceId: SourceId,
                Modified: item.Timestamp,
                Size: item.Text.Length,
                IsFolder: false,
                Tag: item.Text)
            {
                Icon = SymbolRegular.ClipboardPaste24,
                KindLabel = "Clipboard",
                PrimaryAction = new CandidateAction(
                    "paste_clipboard",
                    "Copy to clipboard",
                    "Enter",
                    _ =>
                    {
                        Application.Current?.Dispatcher?.Invoke(() =>
                        {
                            _lastSequenceNumber = NativeMethodsSearch.GetClipboardSequenceNumber();
                            Clipboard.SetText(item.Text);
                            _lastSequenceNumber = NativeMethodsSearch.GetClipboardSequenceNumber();
                        });
                        return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer, "Copied to clipboard"));
                    })
            });
        }

        return Task.FromResult<IReadOnlyList<Candidate>>(candidates);
    }

    private static string FormatTimeAgo(DateTimeOffset time)
    {
        var diff = DateTimeOffset.UtcNow - time;
        if (diff.TotalSeconds < 60) return "just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
        return $"{(int)diff.TotalDays}d ago";
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_hwnd != IntPtr.Zero && _isListening)
        {
            try
            {
                NativeMethodsSearch.RemoveClipboardFormatListener(_hwnd);
            }
            catch (Exception ex)
            {
                Safe.Log(ex, "ClipboardSource.Dispose");
            }
            _isListening = false;
        }

        _hwnd = IntPtr.Zero;
        if (Instance == this) Instance = null;
    }
}

