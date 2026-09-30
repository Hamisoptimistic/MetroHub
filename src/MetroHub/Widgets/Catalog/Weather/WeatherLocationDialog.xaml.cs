using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MetroHub.Core.Models.Geocoding;
using MetroHub.Core.Services;
using MetroHub.Widgets.Catalog.Weather;
using System.Windows.Interop;
using System.Windows.Shell;
using Wpf.Ui.Controls;

namespace MetroHub.Presentation.Controls;

/// <summary>
/// Dedicated dialog for searching, previewing, and setting weather location coordinates.
/// </summary>
public partial class WeatherLocationDialog : FluentWindow
{
    private WeatherWidgetViewModel? _weatherVm;
    private CancellationTokenSource? _searchCts;
    private int _searchGeneration;
    private bool _suppressSearch;
    private string? _activeKey;
    private GeoResult? _selectedLocation;

    private bool _isFullyActivated;
    private DateTime _shownTime;

    internal ObservableCollection<GeoResult> Suggestions { get; } = new();

    public WeatherLocationDialog()
    {
        InitializeComponent();
        Background = Brushes.Transparent;
        WeatherSuggestionsList.ItemsSource = Suggestions;
        IsVisibleChanged += (s, e) =>
        {
            if (!IsVisible)
            {
                CancelPendingSearch();
                HideSuggestions();
                WeatherLocationService.Instance.ClearCache();
            }
        };
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
            int cornerVal = NativeMethods.DWMWCP_DONOTROUND;
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

    public void Setup(WeatherWidgetViewModel weatherVm)
    {
        _weatherVm = weatherVm;
        _suppressSearch = true;
        _selectedLocation = null;

        string currentCity = weatherVm?.CityName ?? string.Empty;
        WeatherCityInput.Text = currentCity;
        _suppressSearch = false;

        WeatherStatusMessage.Visibility = Visibility.Collapsed;
        WeatherActionSpinner.Visibility = Visibility.Collapsed;

        Dispatcher.InvokeAsync(() =>
        {
            WeatherCityInput.Focus();
            WeatherCityInput.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private async void OnWeatherInputTextChanged(object sender, TextChangedEventArgs e)
    {
        WeatherStatusMessage.Visibility = Visibility.Collapsed;
        if (_suppressSearch) return;

        var raw = WeatherCityInput.Text;
        string? normalized = WeatherLocationService.NormalizeQuery(raw);

        if (string.IsNullOrEmpty(normalized))
        {
            CancelPendingSearch();
            HideSuggestions();
            _selectedLocation = null;
            return;
        }

        var (location, _) = WeatherLocationService.ParseQuery(normalized);
        if (!WeatherLocationService.LongEnough(location))
        {
            CancelPendingSearch();
            HideSuggestions();
            _selectedLocation = null;
            return;
        }

        if (string.Equals(normalized, _activeKey, StringComparison.Ordinal))
        {
            return;
        }

        CancelPendingSearch();
        _selectedLocation = null;

        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        int generation = ++_searchGeneration;
        _activeKey = normalized;

        try
        {
            await Task.Delay(300, token);
            if (generation != _searchGeneration || token.IsCancellationRequested) return;

            var results = await WeatherLocationService.Instance.SearchLocationsAsync(normalized, token);
            if (generation != _searchGeneration || token.IsCancellationRequested) return;

            ShowSuggestions(results);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            WeatherStatusMessage.Text = $"Location search failed: {ex.Message}";
            WeatherStatusMessage.Visibility = Visibility.Visible;
        }
    }

    private void ShowSuggestions(IReadOnlyList<GeoResult> results)
    {
        Suggestions.Clear();
        foreach (var r in results)
        {
            Suggestions.Add(r);
        }

        if (Suggestions.Count > 0)
        {
            WeatherSuggestionsPopup.IsOpen = true;
            WeatherSuggestionsList.SelectedIndex = 0;
        }
        else
        {
            HideSuggestions();
        }
    }

    private void HideSuggestions()
    {
        WeatherSuggestionsPopup.IsOpen = false;
        Suggestions.Clear();
    }

    private void CancelPendingSearch()
    {
        _activeKey = null;
        _searchGeneration++;
        var cts = _searchCts;
        _searchCts = null;
        try
        {
            cts?.Cancel();
            cts?.Dispose();
        }
        catch { }
    }

    private void OnWeatherInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && WeatherSuggestionsPopup.IsOpen)
        {
            WeatherSuggestionsList.Focus();
            if (WeatherSuggestionsList.SelectedIndex < 0)
            {
                WeatherSuggestionsList.SelectedIndex = 0;
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (WeatherSuggestionsPopup.IsOpen && WeatherSuggestionsList.SelectedItem is GeoResult selected)
            {
                SelectSuggestion(selected);
                e.Handled = true;
            }
            else
            {
                OnSaveLocationClick(sender, e);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            if (WeatherSuggestionsPopup.IsOpen)
            {
                HideSuggestions();
                e.Handled = true;
            }
            else
            {
                DialogResult = false;
                Close();
            }
        }
    }

    private void OnSuggestionsListPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && WeatherSuggestionsList.SelectedItem is GeoResult selected)
        {
            SelectSuggestion(selected);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HideSuggestions();
            WeatherCityInput.Focus();
            e.Handled = true;
        }
    }

    private void OnSuggestionListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (WeatherSuggestionsList.SelectedItem is GeoResult selected)
        {
            SelectSuggestion(selected);
        }
    }

    private void SelectSuggestion(GeoResult selected)
    {
        _suppressSearch = true;
        _selectedLocation = selected;

        string display = !string.IsNullOrWhiteSpace(selected.DisplaySubtitle)
            ? $"{selected.Name}, {selected.DisplaySubtitle}"
            : selected.Name;

        WeatherCityInput.Text = display;
        WeatherCityInput.CaretIndex = display.Length;
        HideSuggestions();
        _suppressSearch = false;
    }

    private async void OnWeatherAutoLocationClick(object sender, RoutedEventArgs e)
    {
        if (_weatherVm == null) return;

        HideSuggestions();
        CancelPendingSearch();

        WeatherActionSpinner.Visibility = Visibility.Visible;
        WeatherStatusMessage.Visibility = Visibility.Collapsed;

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
        }
    }

    private async void OnSaveLocationClick(object sender, RoutedEventArgs e)
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
            }
        }
        catch (Exception ex)
        {
            WeatherActionSpinner.Visibility = Visibility.Collapsed;
            WeatherStatusMessage.Text = $"Search failed: {ex.Message}";
            WeatherStatusMessage.Visibility = Visibility.Visible;
        }
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    public static bool Show(Window? owner, WeatherWidgetViewModel weatherVm)
    {
        var dlg = new WeatherLocationDialog
        {
            Owner = owner
        };
        dlg.Setup(weatherVm);
        return dlg.ShowDialog() == true;
    }
}
