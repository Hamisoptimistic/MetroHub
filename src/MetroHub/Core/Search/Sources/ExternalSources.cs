using System;
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

    private static readonly Dictionary<string, (string DisplayName, string TimeZoneId)> CityTimeZones = new(StringComparer.OrdinalIgnoreCase)
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
    };

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
                    Category: SearchCategory.Other,
                    SourceId: SourceId,
                    Modified: null,
                    Size: null,
                    IsFolder: false,
                    Tag: utcStr)
                {
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
                Category: SearchCategory.Other,
                SourceId: SourceId,
                Modified: null,
                Size: null,
                IsFolder: false,
                Tag: localDisplay)
            {
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
                Category: SearchCategory.Other,
                SourceId: SourceId,
                Modified: null,
                Size: null,
                IsFolder: false,
                Tag: utcDisplay)
            {
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
                Category: SearchCategory.Other,
                SourceId: SourceId,
                Modified: null,
                Size: null,
                IsFolder: false,
                Tag: timeStr)
            {
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
/// Unit and currency converter search source.
/// Evaluates length, mass, temperature, speed, data storage, and daily currency conversion queries.
/// Examples: "50 kg to lbs", "32 f to c", "100 usd to eur".
/// </summary>
public sealed class UnitConverterSource : ISearchSource
{
    public string SourceId => "unit_converter";
    public SourceState State => SourceState.Ready;

    private static readonly Regex QueryRegex = new(
        @"^\s*([+-]?[0-9]+(?:\.[0-9]+)?)\s*([a-zA-Z°/%]+)\s+(?:to|in)\s+([a-zA-Z°/%]+)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Baseline offline currency reference rates (base: EUR)
    private static readonly Dictionary<string, double> DefaultEurCurrencyRates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EUR"] = 1.0,
        ["USD"] = 1.08,
        ["GBP"] = 0.86,
        ["JPY"] = 165.0,
        ["CAD"] = 1.48,
        ["AUD"] = 1.65,
        ["CHF"] = 0.96,
        ["INR"] = 90.0,
        ["CNY"] = 7.8,
        ["NZD"] = 1.78,
        ["BRL"] = 5.95,
        ["MXN"] = 21.5,
        ["KRW"] = 1480.0,
        ["SEK"] = 11.5,
        ["NOK"] = 11.8
    };

    public Task<IReadOnlyList<Candidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || query.IsEmpty)
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        var match = QueryRegex.Match(query.RawText.Trim());
        if (!match.Success)
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        string from = match.Groups[2].Value.ToLowerInvariant();
        string to = match.Groups[3].Value.ToLowerInvariant();

        var converted = TryConvert(val, from, to);
        if (converted is null)
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        var (resValue, resText, category, note) = converted.Value;

        string display = $"≈ {resText}";
        string subtitle = $"{val.ToString("G", CultureInfo.InvariantCulture)} {from} = {resText} ({category})";
        if (!string.IsNullOrEmpty(note))
        {
            subtitle += $" • {note}";
        }

        var candidate = new Candidate(
            Id: $"convert:{from}:{to}:{val}",
            DisplayName: display,
            FullPathOrKey: subtitle,
            Category: SearchCategory.Other,
            SourceId: SourceId,
            Modified: null,
            Size: null,
            IsFolder: false,
            Tag: resText)
        {
            PrimaryAction = new CandidateAction(
                "copy_unit_result",
                "Copy conversion result",
                "Enter",
                _ =>
                {
                    Application.Current?.Dispatcher?.Invoke(() => Clipboard.SetText(resText));
                    return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer, "Copied result to clipboard"));
                })
        };

        return Task.FromResult<IReadOnlyList<Candidate>>([candidate]);
    }

    private static (double Value, string FormattedText, string Category, string? Note)? TryConvert(double val, string from, string to)
    {
        // 1. Temperature
        if (IsTemperature(from) && IsTemperature(to))
        {
            double c = from switch
            {
                "c" or "celsius" or "°c" => val,
                "f" or "fahrenheit" or "°f" => (val - 32.0) * 5.0 / 9.0,
                "k" or "kelvin" => val - 273.15,
                _ => double.NaN
            };

            double res = to switch
            {
                "c" or "celsius" or "°c" => c,
                "f" or "fahrenheit" or "°f" => c * 9.0 / 5.0 + 32.0,
                "k" or "kelvin" => c + 273.15,
                _ => double.NaN
            };

            if (!double.IsNaN(res))
            {
                string unitLabel = to switch
                {
                    "c" or "celsius" or "°c" => "°C",
                    "f" or "fahrenheit" or "°f" => "°F",
                    "k" or "kelvin" => "K",
                    _ => to
                };
                string formatted = Math.Abs(res - Math.Round(res)) < 0.0001
                    ? $"{Math.Round(res)} {unitLabel}"
                    : $"{res:F2} {unitLabel}";
                return (res, formatted, "Temperature", null);
            }
        }

        // 2. Length (Base: Meter)
        var lengthFactors = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["m"] = 1.0, ["meter"] = 1.0, ["meters"] = 1.0,
            ["km"] = 1000.0, ["kilometer"] = 1000.0, ["kilometers"] = 1000.0,
            ["cm"] = 0.01, ["centimeter"] = 0.01, ["centimeters"] = 0.01,
            ["mm"] = 0.001, ["millimeter"] = 0.001, ["millimeters"] = 0.001,
            ["mi"] = 1609.344, ["mile"] = 1609.344, ["miles"] = 1609.344,
            ["yd"] = 0.9144, ["yard"] = 0.9144, ["yards"] = 0.9144,
            ["ft"] = 0.3048, ["foot"] = 0.3048, ["feet"] = 0.3048,
            ["in"] = 0.0254, ["inch"] = 0.0254, ["inches"] = 0.0254,
            ["nm"] = 1852.0, ["nauticalmile"] = 1852.0, ["nauticalmiles"] = 1852.0
        };

        if (lengthFactors.TryGetValue(from, out double fromLen) && lengthFactors.TryGetValue(to, out double toLen))
        {
            double meters = val * fromLen;
            double res = meters / toLen;
            return (res, FormatNumberWithUnit(res, to), "Length", null);
        }

        // 3. Mass / Weight (Base: Kilogram)
        var massFactors = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["kg"] = 1.0, ["kilogram"] = 1.0, ["kilograms"] = 1.0,
            ["g"] = 0.001, ["gram"] = 0.001, ["grams"] = 0.001,
            ["mg"] = 0.000001, ["milligram"] = 0.000001, ["milligrams"] = 0.000001,
            ["lb"] = 0.45359237, ["lbs"] = 0.45359237, ["pound"] = 0.45359237, ["pounds"] = 0.45359237,
            ["oz"] = 0.028349523125, ["ounce"] = 0.028349523125, ["ounces"] = 0.028349523125,
            ["ton"] = 907.18474, ["tons"] = 907.18474,
            ["tonne"] = 1000.0, ["tonnes"] = 1000.0, ["metricton"] = 1000.0
        };

        if (massFactors.TryGetValue(from, out double fromMass) && massFactors.TryGetValue(to, out double toMass))
        {
            double kgs = val * fromMass;
            double res = kgs / toMass;
            return (res, FormatNumberWithUnit(res, to), "Mass", null);
        }

        // 4. Speed (Base: m/s)
        var speedFactors = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["m/s"] = 1.0,
            ["km/h"] = 1.0 / 3.6, ["kph"] = 1.0 / 3.6,
            ["mph"] = 0.44704,
            ["knot"] = 0.514444, ["knots"] = 0.514444
        };

        if (speedFactors.TryGetValue(from, out double fromSpeed) && speedFactors.TryGetValue(to, out double toSpeed))
        {
            double mps = val * fromSpeed;
            double res = mps / toSpeed;
            return (res, FormatNumberWithUnit(res, to), "Speed", null);
        }

        // 5. Data Storage (Base: Byte)
        var dataFactors = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["b"] = 1.0, ["byte"] = 1.0, ["bytes"] = 1.0,
            ["kb"] = 1e3, ["mb"] = 1e6, ["gb"] = 1e9, ["tb"] = 1e12, ["pb"] = 1e15,
            ["kib"] = 1024.0,
            ["mib"] = 1024.0 * 1024.0,
            ["gib"] = 1024.0 * 1024.0 * 1024.0,
            ["tib"] = 1024.0 * 1024.0 * 1024.0 * 1024.0
        };

        if (dataFactors.TryGetValue(from, out double fromData) && dataFactors.TryGetValue(to, out double toData))
        {
            double bytes = val * fromData;
            double res = bytes / toData;
            return (res, FormatNumberWithUnit(res, to), "Digital Storage", null);
        }

        // 6. Currency
        var (rates, asOf) = LoadCurrencyRates();
        if (rates.TryGetValue(from, out double fromRate) && rates.TryGetValue(to, out double toRate))
        {
            double eur = val / fromRate;
            double res = eur * toRate;
            string formatted = $"{res:F2} {to.ToUpperInvariant()}";
            return (res, formatted, "Currency", asOf);
        }

        return null;
    }

    private static bool IsTemperature(string u) =>
        u is "c" or "celsius" or "°c" or "f" or "fahrenheit" or "°f" or "k" or "kelvin";

    private static string FormatNumberWithUnit(double val, string unit)
    {
        if (Math.Abs(val - Math.Round(val)) < 0.0001)
        {
            return $"{Math.Round(val)} {unit}";
        }
        return $"{val:0.###} {unit}";
    }

    private static (Dictionary<string, double> Rates, string AsOf) LoadCurrencyRates()
    {
        string cachePath = Path.Combine(AppPaths.AppDataDir, "fx_rates.json");
        if (File.Exists(cachePath))
        {
            try
            {
                string json = File.ReadAllText(cachePath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("rates", out var ratesEl))
                {
                    var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    foreach (var prop in ratesEl.EnumerateObject())
                    {
                        if (prop.Value.TryGetDouble(out double rate))
                        {
                            map[prop.Name] = rate;
                        }
                    }

                    string asOf = doc.RootElement.TryGetProperty("date", out var dateEl)
                        ? $"as of {dateEl.GetString()}"
                        : "cached reference rate";

                    return (map, asOf);
                }
            }
            catch
            {
                // Degrade gracefully to offline baseline
            }
        }

        return (DefaultEurCurrencyRates, "offline reference rate");
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
                Category: SearchCategory.Other,
                SourceId: SourceId,
                Modified: item.Timestamp,
                Size: item.Text.Length,
                IsFolder: false,
                Tag: item.Text)
            {
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

