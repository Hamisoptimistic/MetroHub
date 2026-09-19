using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using MetroHub.Core.Services;
using MetroHub.Core.Services.Catalog.Weather;
using MetroHub.Widgets.Catalog.Weather;
using Wpf.Ui.Controls;

namespace MetroHub.Presentation.Controls;

public enum AcrylicModalMode
{
    WeatherLocation,
    AddWebLink
}

public partial class AcrylicModalWindow : FluentWindow
{
    private AcrylicModalMode _mode;
    private WeatherWidgetViewModel? _weatherVm;
    private CancellationTokenSource? _debounceCts;
    private string? _resolvedIconPath;
    private bool _userManuallyEditedTitle;
    private WebLinkCreatedEventArgs? _webLinkResult;
    private bool _isFullyActivated;
    private DateTime _shownTime;

    private const int DebounceMs = 300;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private CancellationTokenSource? _searchCts;
    private int _searchGeneration;
    private bool _suppressSearch;
    private string? _activeKey;
    private bool _errorShown;
    private GeoResult? _selectedLocation;
    private readonly Dictionary<string, IReadOnlyList<GeoResult>> _queryCache = new(50, StringComparer.OrdinalIgnoreCase);
    internal ObservableCollection<GeoResult> Suggestions { get; } = new();

    public WebLinkCreatedEventArgs? WebLinkResult => _webLinkResult;

    public AcrylicModalWindow()
    {
        InitializeComponent();
        Background = Brushes.Transparent;
        WeatherSuggestionsList.ItemsSource = Suggestions;
        IsVisibleChanged += OnWindowIsVisibleChanged;
    }

    private void OnWindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            _errorShown = false;
            _activeKey = null;
        }
        else
        {
            CancelPendingSearch();
            HideSuggestions();
            _queryCache.Clear();
        }
    }

    protected override void OnBackdropTypeChanged(WindowBackdropType oldValue, WindowBackdropType newValue)
    {
        // Suppress WPF-UI's built-in backdrop manager which resets Background to solid #202020
        // or throws if ExtendsContentIntoTitleBar is false. We manage DWM Acrylic directly.
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == BackgroundProperty && Background != Brushes.Transparent)
        {
            SetCurrentValue(BackgroundProperty, Brushes.Transparent);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _shownTime = DateTime.UtcNow;
        Background = Brushes.Transparent;

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var source = HwndSource.FromHwnd(hwnd);
            if (source?.CompositionTarget != null)
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
            }

            ApplyAcrylicBackdrop(hwnd);

            // Completely hide modal window from Windows Alt+Tab switcher
            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_TOOLWINDOW);

            // Responsive scaling: clamp modal to fit comfortably on small displays/high DPI
            var workArea = SystemParameters.WorkArea;
            if (workArea.Width > 0 && workArea.Height > 0)
            {
                Width = Math.Min(620, Math.Max(480, workArea.Width * 0.85));
                Height = Math.Min(360, Math.Max(300, workArea.Height * 0.85));
            }

            if (Owner != null && Owner.ActualWidth > 0 && Owner.ActualHeight > 0)
            {
                Left = Owner.Left + (Owner.ActualWidth - Width) / 2;
                Top = Owner.Top + (Owner.ActualHeight - Height) / 2;
            }
            else if (workArea.Width > 0 && workArea.Height > 0)
            {
                Left = workArea.Left + (workArea.Width - Width) / 2;
                Top = workArea.Top + (workArea.Height - Height) / 2;
            }
        }

        var chrome = WindowChrome.GetWindowChrome(this);
        if (chrome != null)
        {
            chrome.ResizeBorderThickness = new Thickness(0);
            chrome.CaptionHeight = 0;
            chrome.CornerRadius = new CornerRadius(0);
            chrome.GlassFrameThickness = new Thickness(-1);
            chrome.NonClientFrameEdges = NonClientFrameEdges.None;
        }
    }

    private static void ApplyAcrylicBackdrop(IntPtr hwnd)
    {
        try
        {
            int darkVal = 1;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkVal, sizeof(int));

            // Windows 11 rounded corners suppressed for cohesive 2px radius
            int cornerVal = 1; // DWMWCP_DONOTROUND
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerVal, sizeof(int));

            // Suppress harsh OS non-client border
            int borderVal = NativeMethods.DWMWA_COLOR_NONE;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref borderVal, sizeof(int));

            NativeMethods.MARGINS margins = new(-1, -1, -1, -1);
            NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);

            int backdropVal = NativeMethods.DWMSBT_TRANSIENTWINDOW; // 3 = Acrylic
            int res = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropVal, sizeof(int));
            if (res != 0)
            {
                int trueVal = 1;
                NativeMethods.DwmSetWindowAttribute(hwnd, 1029, ref trueVal, sizeof(int));
            }

            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
        }
        catch { }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _isFullyActivated = true;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        // Ignore premature deactivation during show/transition
        if (!_isFullyActivated || (DateTime.UtcNow - _shownTime).TotalMilliseconds < 150)
        {
            return;
        }

        try
        {
            if (IsVisible)
            {
                DialogResult = false;
                Close();
            }
        }
        catch { }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
            e.Handled = true;
            return;
        }

        // Close on Alt+Tab so switching tasks cleanly dismisses the modal
        if ((e.Key == Key.System && e.SystemKey == Key.Tab) ||
            ((Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt && (e.Key == Key.Tab || e.SystemKey == Key.Tab)))
        {
            try
            {
                DialogResult = false;
            }
            catch { }
            Close();
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    #region Weather Location Mode

    public void SetupWeather(WeatherWidgetViewModel weatherVm)
    {
        _mode = AcrylicModalMode.WeatherLocation;
        _weatherVm = weatherVm;

        WeatherSidebarGlyph.Visibility = Visibility.Visible;
        WebLinkSidebarGlyph.Visibility = Visibility.Collapsed;

        HeaderTitleText.Text = "Change Location";
        PrimaryActionButton.Content = "Apply";
        PrimaryActionButton.IsEnabled = true;

        WeatherFormGrid.Visibility = Visibility.Visible;
        WebLinkFormGrid.Visibility = Visibility.Collapsed;

        SetCityText(weatherVm.CustomCity ?? string.Empty);

        string activeLocation = !string.IsNullOrWhiteSpace(weatherVm.CustomCity)
            ? weatherVm.CustomCity
            : (!string.IsNullOrWhiteSpace(weatherVm.CityName) && weatherVm.CityName != "Locating..." ? weatherVm.CityName : "Automatic (GPS / IP)");

        CurrentLocationText.Text = $"Current Location: {activeLocation}";
        CurrentLocationPanel.Visibility = Visibility.Visible;

        WeatherStatusMessage.Text = string.Empty;
        WeatherStatusMessage.Visibility = Visibility.Collapsed;
        WeatherActionSpinner.Visibility = Visibility.Collapsed;
        _selectedLocation = null;

        Loaded += (s, e) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                _suppressSearch = true;
                try
                {
                    WeatherCityInput.Focus();
                    WeatherCityInput.SelectAll();
                }
                finally
                {
                    _suppressSearch = false;
                }
            }, System.Windows.Threading.DispatcherPriority.Input);
        };
    }

    private void SetCityText(string text)
    {
        CancelPendingSearch();
        HideSuggestions();
        _suppressSearch = true;
        try
        {
            WeatherCityInput.Text = text;
        }
        finally
        {
            _suppressSearch = false;
        }
    }

    private static string? NormalizeQuery(string raw)
    {
        try
        {
            var s = raw.Normalize(NormalizationForm.FormC);
            var sb = new StringBuilder(s.Length);
            var pendingSpace = false;
            foreach (var ch in s)
            {
                if (char.IsControl(ch) || char.IsWhiteSpace(ch)) { pendingSpace = sb.Length > 0; continue; }
                if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
                sb.Append(ch);
            }
            if (sb.Length > 100) sb.Length = 100;
            if (sb.Length > 0 && char.IsHighSurrogate(sb[^1])) sb.Length--;
            return sb.ToString();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool LongEnough(string location)
    {
        var n = new StringInfo(location).LengthInTextElements;
        if (n >= 3) return true;
        if (n < 2) return false;
        foreach (var r in location.EnumerateRunes())
        {
            if (Rune.IsLetter(r) && r.Value >= 0x0900) return true;
        }
        return false;
    }

    private static (string Location, string? Qualifier) ParseQuery(string normalized)
    {
        int commaIndex = normalized.IndexOf(',');
        if (commaIndex < 0)
        {
            return (normalized.Trim(), null);
        }

        string loc = normalized[..commaIndex].Trim();
        string remainder = normalized[(commaIndex + 1)..].Trim();
        int nextComma = remainder.IndexOf(',');
        string qual = (nextComma >= 0 ? remainder[..nextComma] : remainder).Trim();
        return (loc, string.IsNullOrEmpty(qual) ? null : qual);
    }

    private void CancelPendingSearch()
    {
        _activeKey = null;
        _searchGeneration++;
        var cts = _searchCts;
        _searchCts = null;
        if (cts is null) return;
        try
        {
            cts.Cancel();
            cts.Dispose();
        }
        catch { }
    }

    private bool IsCurrent(int generation, CancellationToken token)
        => generation == _searchGeneration && !token.IsCancellationRequested;

    private async void OnWeatherInputTextChanged(object sender, TextChangedEventArgs e)
    {
        WeatherStatusMessage.Visibility = Visibility.Collapsed;

        if (_suppressSearch) return;

        var raw = WeatherCityInput.Text;
        var normalized = NormalizeQuery(raw);

        if (string.IsNullOrEmpty(normalized) || !normalized.Any(char.IsLetterOrDigit))
        {
            CancelPendingSearch();
            HideSuggestions();
            _selectedLocation = null;
            return;
        }

        var (location, qualifier) = ParseQuery(normalized);
        if (!LongEnough(location))
        {
            CancelPendingSearch();
            HideSuggestions();
            _selectedLocation = null;
            return;
        }

        // Skip identical work: if the key hasn't changed (e.g. trailing space typed), return before cancelling
        if (string.Equals(normalized, _activeKey, StringComparison.Ordinal))
        {
            return;
        }

        CancelPendingSearch();
        _selectedLocation = null;

        if (_queryCache.TryGetValue(normalized, out var cachedResults))
        {
            ShowSuggestions(cachedResults);
            return;
        }

        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        var generation = _searchGeneration;
        _activeKey = normalized;

        try
        {
            await SearchAsync(normalized, location, qualifier, generation, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Superseded or window closed: expected, ignore
        }
        catch (Exception ex)
        {
            _activeKey = null;
            if (IsCurrent(generation, token))
            {
                HandleSearchException(ex);
            }
        }
    }

    private async Task SearchAsync(string query, string location, string? qualifier, int generation, CancellationToken token)
    {
        await Task.Delay(DebounceMs, token);
        if (!IsCurrent(generation, token)) return;

        (IReadOnlyList<GeoResult> Results, int RawCount) openMeteoResult = ([], 0);
        try
        {
            openMeteoResult = await GeocodeOpenMeteoAsync(location, qualifier, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HandleSearchException(ex);
            return;
        }

        if (!IsCurrent(generation, token)) return;

        IReadOnlyList<GeoResult> results = openMeteoResult.Results;

        // Photon is called ONLY when Open-Meteo returned 0 results before any client-side qualifier filtering
        // and only if the query contains letters
        if (openMeteoResult.RawCount == 0 && query.Any(char.IsLetter))
        {
            try
            {
                results = await GeocodePhotonAsync(query, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                results = [];
            }

            if (!IsCurrent(generation, token)) return;
        }

        results = PostProcessResults(results);

        if (_queryCache.Count >= 50)
        {
            _queryCache.Clear();
        }
        _queryCache[query] = results;

        ShowSuggestions(results);
    }

    private async Task<(IReadOnlyList<GeoResult> Results, int RawCount)> GeocodeOpenMeteoAsync(
        string location, string? qualifier, CancellationToken token)
    {
        string nameParam;
        int count;

        if (string.IsNullOrEmpty(qualifier))
        {
            nameParam = location;
            count = 8;
        }
        else if (qualifier.Length <= 2)
        {
            nameParam = $"{location}, {qualifier}";
            count = 8;
        }
        else
        {
            nameParam = location;
            count = 50;
        }

        var url = "https://geocoding-api.open-meteo.com/v1/search"
                + $"?name={Uri.EscapeDataString(nameParam)}&count={count}&language=en&format=json";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        using var response = await WeatherService.SharedHttpClient.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

        if (!response.IsSuccessStatusCode)
        {
            return ([], 0);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var body = await JsonSerializer.DeserializeAsync<OpenMeteoGeocodingResponse>(
            stream, s_jsonOptions, timeout.Token);

        var rawList = body?.Results ?? [];
        var validated = ValidateAndFilterGeoResults(rawList);
        int rawCount = validated.Count;

        if (qualifier != null && qualifier.Length >= 3)
        {
            var compareInfo = CultureInfo.InvariantCulture.CompareInfo;
            const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

            var filtered = new List<GeoResult>(8);
            foreach (var r in validated)
            {
                bool matchAdmin = r.Admin1 != null && compareInfo.IsPrefix(r.Admin1, qualifier, options);
                bool matchCountry = r.Country != null && compareInfo.IsPrefix(r.Country, qualifier, options);
                if (matchAdmin || matchCountry)
                {
                    filtered.Add(r);
                    if (filtered.Count == 8) break;
                }
            }
            return (filtered, rawCount);
        }

        return (validated.Take(8).ToList(), rawCount);
    }

    private async Task<IReadOnlyList<GeoResult>> GeocodePhotonAsync(string query, CancellationToken token)
    {
        var url = $"https://photon.komoot.io/api/?q={Uri.EscapeDataString(query)}&limit=5&osm_tag=place";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("MetroHub/1.0");

        using var response = await WeatherService.SharedHttpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var body = await JsonSerializer.DeserializeAsync<PhotonResponse>(stream, s_jsonOptions, timeout.Token);

        if (body?.Features == null) return [];

        var list = new List<GeoResult>(body.Features.Count);
        foreach (var feat in body.Features)
        {
            var coords = feat.Geometry?.Coordinates;
            var name = feat.Properties?.Name;
            if (coords == null || coords.Length < 2 || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            double lon = coords[0];
            double lat = coords[1];

            if (lat < -90.0 || lat > 90.0 || lon < -180.0 || lon > 180.0)
            {
                continue;
            }

            list.Add(new GeoResult(
                Name: name.Trim(),
                Latitude: lat,
                Longitude: lon,
                Admin1: feat.Properties?.State,
                Admin2: null,
                Country: feat.Properties?.Country,
                Timezone: null));
        }

        return list;
    }

    private static List<GeoResult> ValidateAndFilterGeoResults(IEnumerable<GeoResult> source)
    {
        var valid = new List<GeoResult>();
        foreach (var r in source)
        {
            if (string.IsNullOrWhiteSpace(r.Name) || !r.Latitude.HasValue || !r.Longitude.HasValue)
            {
                continue;
            }

            double lat = r.Latitude.Value;
            double lon = r.Longitude.Value;
            if (lat < -90.0 || lat > 90.0 || lon < -180.0 || lon > 180.0)
            {
                continue;
            }

            valid.Add(r);
        }
        return valid;
    }

    private static IReadOnlyList<GeoResult> PostProcessResults(IReadOnlyList<GeoResult> list)
    {
        if (list.Count == 0) return list;

        var deduped = new List<GeoResult>(list.Count);
        foreach (var item in list)
        {
            bool isDuplicate = false;
            foreach (var existing in deduped)
            {
                if (string.Equals(item.Name, existing.Name, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Admin1, existing.Admin1, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Country, existing.Country, StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs(item.Latitude!.Value - existing.Latitude!.Value) < 0.01 &&
                    Math.Abs(item.Longitude!.Value - existing.Longitude!.Value) < 0.01)
                {
                    isDuplicate = true;
                    break;
                }
            }
            if (!isDuplicate)
            {
                deduped.Add(item);
            }
        }

        for (int i = 0; i < deduped.Count; i++)
        {
            var cur = deduped[i];
            bool hasCollision = false;
            for (int j = 0; j < deduped.Count; j++)
            {
                if (i != j)
                {
                    var other = deduped[j];
                    if (string.Equals(cur.Name, other.Name, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(cur.Admin1, other.Admin1, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(cur.Country, other.Country, StringComparison.OrdinalIgnoreCase))
                    {
                        hasCollision = true;
                        break;
                    }
                }
            }

            var parts = new List<string>(3);
            if (hasCollision && !string.IsNullOrWhiteSpace(cur.Admin2))
            {
                parts.Add(cur.Admin2.Trim());
            }
            if (!string.IsNullOrWhiteSpace(cur.Admin1))
            {
                parts.Add(cur.Admin1.Trim());
            }
            if (!string.IsNullOrWhiteSpace(cur.Country) && !string.Equals(cur.Country.Trim(), cur.Admin1?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(cur.Country.Trim());
            }

            cur.DisplaySubtitle = string.Join(", ", parts);
        }

        return deduped;
    }

    private void ShowSuggestions(IReadOnlyList<GeoResult> results)
    {
        if (!WeatherCityInput.IsKeyboardFocused && !WeatherSuggestionsList.IsKeyboardFocused && !WeatherSuggestionsList.IsKeyboardFocusWithin)
        {
            return;
        }

        if (results.Count == 0)
        {
            HideSuggestions();
            return;
        }

        Suggestions.Clear();
        foreach (var r in results)
        {
            Suggestions.Add(r);
        }

        WeatherSuggestionsList.SelectedIndex = -1;
        WeatherSuggestionsPopup.IsOpen = true;
    }

    private void HideSuggestions()
    {
        WeatherSuggestionsPopup.IsOpen = false;
        WeatherSuggestionsList.SelectedIndex = -1;
        Suggestions.Clear();
    }

    private void HandleSearchException(Exception ex)
    {
        try
        {
            if (ex is OperationCanceledException or TimeoutException or JsonException or UriFormatException)
            {
                return;
            }

            if (ex is HttpRequestException)
            {
                if (_errorShown) return;
                _errorShown = true;
                WeatherStatusMessage.Text = "Network unreachable. Check your internet connection.";
                WeatherStatusMessage.Visibility = Visibility.Visible;
            }
        }
        catch { }
    }

    private void OnWeatherInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            if (WeatherSuggestionsPopup.IsOpen && Suggestions.Count > 0)
            {
                CancelPendingSearch();
                if (WeatherSuggestionsList.SelectedIndex < Suggestions.Count - 1)
                {
                    WeatherSuggestionsList.SelectedIndex++;
                }
                else
                {
                    WeatherSuggestionsList.SelectedIndex = 0;
                }

                WeatherSuggestionsList.ScrollIntoView(WeatherSuggestionsList.SelectedItem);
                e.Handled = true;
                return;
            }
        }
        else if (e.Key == Key.Up)
        {
            if (WeatherSuggestionsPopup.IsOpen && Suggestions.Count > 0)
            {
                CancelPendingSearch();
                if (WeatherSuggestionsList.SelectedIndex > 0)
                {
                    WeatherSuggestionsList.SelectedIndex--;
                    WeatherSuggestionsList.ScrollIntoView(WeatherSuggestionsList.SelectedItem);
                }
                else if (WeatherSuggestionsList.SelectedIndex == 0)
                {
                    WeatherSuggestionsList.SelectedIndex = -1;
                }
                else if (WeatherSuggestionsList.SelectedIndex < 0)
                {
                    WeatherSuggestionsList.SelectedIndex = Suggestions.Count - 1;
                    WeatherSuggestionsList.ScrollIntoView(WeatherSuggestionsList.SelectedItem);
                }

                e.Handled = true;
                return;
            }
        }
        else if (e.Key == Key.Enter)
        {
            CancelPendingSearch();
            if (WeatherSuggestionsPopup.IsOpen && WeatherSuggestionsList.SelectedItem is GeoResult selected)
            {
                SelectSuggestion(selected);
            }
            HideSuggestions();
            ApplyWeatherLocation();
            e.Handled = true;
            return;
        }
        else if (e.Key == Key.Escape)
        {
            if (WeatherSuggestionsPopup.IsOpen)
            {
                CancelPendingSearch();
                HideSuggestions();
                e.Handled = true;
                return;
            }
        }
        else if (e.Key == Key.Tab)
        {
            if (WeatherSuggestionsPopup.IsOpen)
            {
                CancelPendingSearch();
                if (WeatherSuggestionsList.SelectedItem is GeoResult selected)
                {
                    SelectSuggestion(selected);
                }
                HideSuggestions();
            }
        }
    }

    private void OnSuggestionsListPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            if (WeatherSuggestionsList.SelectedIndex < Suggestions.Count - 1)
            {
                WeatherSuggestionsList.SelectedIndex++;
                WeatherSuggestionsList.ScrollIntoView(WeatherSuggestionsList.SelectedItem);
            }
            else
            {
                WeatherSuggestionsList.SelectedIndex = 0;
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            if (WeatherSuggestionsList.SelectedIndex > 0)
            {
                WeatherSuggestionsList.SelectedIndex--;
                WeatherSuggestionsList.ScrollIntoView(WeatherSuggestionsList.SelectedItem);
                e.Handled = true;
            }
            else
            {
                WeatherSuggestionsList.SelectedIndex = -1;
                WeatherCityInput.Focus();
                WeatherCityInput.CaretIndex = WeatherCityInput.Text.Length;
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Enter)
        {
            CancelPendingSearch();
            if (WeatherSuggestionsList.SelectedItem is GeoResult selected)
            {
                SelectSuggestion(selected);
            }
            HideSuggestions();
            ApplyWeatherLocation();
            e.Handled = true;
        }
        else if (e.Key is Key.Escape or Key.Tab)
        {
            CancelPendingSearch();
            HideSuggestions();
            WeatherCityInput.Focus();
            e.Handled = true;
        }
    }

    private void OnSuggestionListMouseUp(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not ListBoxItem && dep != WeatherSuggestionsList)
        {
            dep = VisualTreeHelper.GetParent(dep);
        }

        if (dep is ListBoxItem lbi && lbi.DataContext is GeoResult clicked)
        {
            CancelPendingSearch();
            SelectSuggestion(clicked);
            e.Handled = true;
            return;
        }

        if (WeatherSuggestionsList.SelectedItem is GeoResult selected)
        {
            CancelPendingSearch();
            SelectSuggestion(selected);
            e.Handled = true;
        }
    }

    private void SelectSuggestion(GeoResult selected)
    {
        _selectedLocation = selected;
        SetCityText(selected.Name);
        WeatherCityInput.CaretIndex = WeatherCityInput.Text.Length;
        WeatherCityInput.Focus();
    }

    private async void OnWeatherAutoLocationClick(object sender, RoutedEventArgs e)
    {
        if (_weatherVm == null) return;

        HideSuggestions();
        CancelPendingSearch();

        WeatherActionSpinner.Visibility = Visibility.Visible;
        WeatherStatusMessage.Visibility = Visibility.Collapsed;
        PrimaryActionButton.IsEnabled = false;

        try
        {
            await _weatherVm.UseAutoLocationAsync();
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            WeatherActionSpinner.Visibility = Visibility.Collapsed;
            WeatherStatusMessage.Text = $"Error: {ex.Message}";
            WeatherStatusMessage.Visibility = Visibility.Visible;
            PrimaryActionButton.IsEnabled = true;
        }
    }

    private async void ApplyWeatherLocation()
    {
        if (_weatherVm == null) return;
        string query = WeatherCityInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            WeatherStatusMessage.Text = "Please enter a city or location name.";
            WeatherStatusMessage.Visibility = Visibility.Visible;
            WeatherCityInput.Focus();
            return;
        }

        HideSuggestions();
        CancelPendingSearch();

        WeatherActionSpinner.Visibility = Visibility.Visible;
        WeatherStatusMessage.Visibility = Visibility.Collapsed;
        PrimaryActionButton.IsEnabled = false;

        try
        {
            bool success;
            if (_selectedLocation != null &&
                _selectedLocation.Latitude.HasValue &&
                _selectedLocation.Longitude.HasValue &&
                string.Equals(_selectedLocation.Name.Trim(), query, StringComparison.OrdinalIgnoreCase))
            {
                success = await _weatherVm.SetCustomLocationAsync(_selectedLocation.Name, _selectedLocation.Latitude.Value, _selectedLocation.Longitude.Value);
            }
            else
            {
                success = await _weatherVm.SetCustomCityAsync(query);
            }

            WeatherActionSpinner.Visibility = Visibility.Collapsed;

            if (success)
            {
                DialogResult = true;
                Close();
            }
            else
            {
                WeatherStatusMessage.Text = $"City \"{query}\" not found. Try including region/country.";
                WeatherStatusMessage.Visibility = Visibility.Visible;
                PrimaryActionButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            WeatherActionSpinner.Visibility = Visibility.Collapsed;
            WeatherStatusMessage.Text = $"Search failed: {ex.Message}";
            WeatherStatusMessage.Visibility = Visibility.Visible;
            PrimaryActionButton.IsEnabled = true;
        }
    }

    #endregion

    #region Web Link Mode

    public void SetupWebLink(string? initialUrl = null)
    {
        _mode = AcrylicModalMode.AddWebLink;
        _userManuallyEditedTitle = false;
        _resolvedIconPath = null;
        _webLinkResult = null;

        WeatherSidebarGlyph.Visibility = Visibility.Collapsed;
        WebLinkSidebarGlyph.Visibility = Visibility.Visible;

        WebLinkFaviconImage.Source = null;
        WebLinkFaviconImage.Visibility = Visibility.Collapsed;
        WebLinkFallbackIcon.Visibility = Visibility.Visible;
        WebLinkSidebarSpinner.Visibility = Visibility.Collapsed;

        HeaderTitleText.Text = "Add Web Shortcut";
        PrimaryActionButton.Content = "Add Shortcut";

        WeatherFormGrid.Visibility = Visibility.Collapsed;
        WebLinkFormGrid.Visibility = Visibility.Visible;
        CurrentLocationPanel.Visibility = Visibility.Collapsed;

        WebLinkDestBothRadio.IsChecked = true;

        if (!string.IsNullOrWhiteSpace(initialUrl))
        {
            WebLinkUrlInput.Text = initialUrl;
        }
        else
        {
            WebLinkUrlInput.Text = string.Empty;
            WebLinkTitleInput.Text = string.Empty;
        }

        PrimaryActionButton.IsEnabled = true;

        Loaded += (s, e) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (!string.IsNullOrWhiteSpace(initialUrl))
                {
                    WebLinkTitleInput.Focus();
                    WebLinkTitleInput.SelectAll();
                }
                else
                {
                    WebLinkUrlInput.Focus();
                    WebLinkUrlInput.SelectAll();
                }
            }, System.Windows.Threading.DispatcherPriority.Input);
        };
    }

    private void OnWebLinkUrlTextChanged(object sender, TextChangedEventArgs e)
    {
        WebLinkStatusMessage.Visibility = Visibility.Collapsed;
        string raw = WebLinkUrlInput.Text.Trim();

        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        if (string.IsNullOrWhiteSpace(raw))
        {
            WebLinkFaviconImage.Source = null;
            WebLinkFaviconImage.Visibility = Visibility.Collapsed;
            WebLinkFallbackIcon.Visibility = Visibility.Visible;
            WebLinkSidebarSpinner.Visibility = Visibility.Collapsed;
            if (!_userManuallyEditedTitle) WebLinkTitleInput.Text = string.Empty;
            return;
        }

        string normalized = WebFaviconService.NormalizeUrl(raw);

        if (!_userManuallyEditedTitle)
        {
            string inferred = WebFaviconService.InferTitleFromUrl(normalized);
            WebLinkTitleInput.Text = inferred;
        }

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350, token);
                if (token.IsCancellationRequested) return;

                Dispatcher.Invoke(() =>
                {
                    WebLinkSidebarSpinner.Visibility = Visibility.Visible;
                    WebLinkFallbackIcon.Visibility = Visibility.Collapsed;
                });

                string? iconPath = await WebFaviconService.GetFaviconPathAsync(normalized, token);
                if (token.IsCancellationRequested) return;

                Dispatcher.Invoke(() =>
                {
                    WebLinkSidebarSpinner.Visibility = Visibility.Collapsed;

                    if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
                    {
                        try
                        {
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.UriSource = new Uri(iconPath);
                            bmp.EndInit();
                            bmp.Freeze();

                            _resolvedIconPath = iconPath;
                            WebLinkFaviconImage.Source = bmp;
                            WebLinkFaviconImage.Visibility = Visibility.Visible;
                            WebLinkFallbackIcon.Visibility = Visibility.Collapsed;
                            return;
                        }
                        catch { }
                    }

                    _resolvedIconPath = null;
                    WebLinkFaviconImage.Source = null;
                    WebLinkFaviconImage.Visibility = Visibility.Collapsed;
                    WebLinkFallbackIcon.Visibility = Visibility.Visible;
                });
            }
            catch (OperationCanceledException) { }
            catch
            {
                Dispatcher.Invoke(() =>
                {
                    WebLinkSidebarSpinner.Visibility = Visibility.Collapsed;
                    WebLinkFallbackIcon.Visibility = Visibility.Visible;
                });
            }
        }, token);
    }

    private void OnWebLinkTitleTextChanged(object sender, TextChangedEventArgs e)
    {
        if (WebLinkTitleInput.IsKeyboardFocused)
        {
            _userManuallyEditedTitle = true;
        }
    }

    private void OnWebLinkInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyWebLink();
            e.Handled = true;
        }
    }

    private void OnWebLinkPasteClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText().Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    WebLinkUrlInput.Text = text;
                    WebLinkTitleInput.Focus();
                }
            }
        }
        catch { }
    }

    private void ApplyWebLink()
    {
        string raw = WebLinkUrlInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            WebLinkStatusMessage.Text = "Please enter a website URL.";
            WebLinkStatusMessage.Visibility = Visibility.Visible;
            WebLinkUrlInput.Focus();
            return;
        }

        string normalized = WebFaviconService.NormalizeUrl(raw);
        string title = WebLinkTitleInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = WebFaviconService.InferTitleFromUrl(normalized);
        }

        bool addToCanvas = WebLinkDestBothRadio.IsChecked == true || WebLinkDestCanvasRadio.IsChecked == true;
        bool addToSidebar = WebLinkDestBothRadio.IsChecked == true || WebLinkDestSidebarRadio.IsChecked == true;

        _webLinkResult = new WebLinkCreatedEventArgs
        {
            Url = normalized,
            Title = title,
            IconPath = _resolvedIconPath,
            AddToCanvas = addToCanvas,
            AddToSidebar = addToSidebar
        };

        DialogResult = true;
        Close();
    }

    #endregion

    private void OnPrimaryActionButtonClick(object sender, RoutedEventArgs e)
    {
        if (_mode == AcrylicModalMode.WeatherLocation)
        {
            ApplyWeatherLocation();
        }
        else if (_mode == AcrylicModalMode.AddWebLink)
        {
            ApplyWebLink();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        CancelPendingSearch();
        Suggestions.Clear();
        _queryCache.Clear();

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = null;

        WebLinkFaviconImage.Source = null;
        _resolvedIconPath = null;
        _weatherVm = null;
    }

    #region Geocoding DTOs

    internal sealed record GeoResult(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("latitude")] double? Latitude,
        [property: JsonPropertyName("longitude")] double? Longitude,
        [property: JsonPropertyName("admin1")] string? Admin1,
        [property: JsonPropertyName("admin2")] string? Admin2,
        [property: JsonPropertyName("country")] string? Country,
        [property: JsonPropertyName("timezone")] string? Timezone)
    {
        [JsonIgnore]
        public string DisplaySubtitle { get; set; } = string.Empty;
    }

    internal sealed record OpenMeteoGeocodingResponse(
        [property: JsonPropertyName("results")] List<GeoResult>? Results);

    internal sealed record PhotonResponse(
        [property: JsonPropertyName("features")] List<PhotonFeature>? Features);

    internal sealed record PhotonFeature(
        [property: JsonPropertyName("geometry")] PhotonGeometry? Geometry,
        [property: JsonPropertyName("properties")] PhotonProperties? Properties);

    internal sealed record PhotonGeometry(
        [property: JsonPropertyName("coordinates")] double[]? Coordinates);

    internal sealed record PhotonProperties(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("country")] string? Country);

    #endregion

    #region Static Helper Methods

    public static bool ShowWeatherLocation(Window owner, WeatherWidgetViewModel weatherVm)
    {
        var window = new AcrylicModalWindow
        {
            Owner = owner
        };
        window.SetupWeather(weatherVm);
        bool? result = window.ShowDialog();
        return result == true;
    }

    public static WebLinkCreatedEventArgs? ShowAddWebLink(Window owner, string? initialUrl = null)
    {
        var window = new AcrylicModalWindow
        {
            Owner = owner
        };
        window.SetupWebLink(initialUrl);
        bool? result = window.ShowDialog();
        return result == true ? window.WebLinkResult : null;
    }

    #endregion
}
