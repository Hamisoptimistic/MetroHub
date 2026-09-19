using System;
using System.IO;
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

    public WebLinkCreatedEventArgs? WebLinkResult => _webLinkResult;

    public AcrylicModalWindow()
    {
        InitializeComponent();
        Background = Brushes.Transparent;
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

            // Windows 11 rounded corners
            int cornerVal = 2; // DWMWCP_ROUND
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

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
            e.Handled = true;
        }
    }

    private void OnWindowDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnCancelButtonClick(object sender, RoutedEventArgs e)
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

        SidebarTitleText.Text = "Weather";
        SidebarSubtitleText.Text = "Location";
        SidebarBadgeCategoryText.Text = "CURRENT";

        string currentCity = !string.IsNullOrWhiteSpace(weatherVm.CityName) ? weatherVm.CityName : "Local";
        SidebarBadgeValueText.Text = currentCity;

        HeaderTitleText.Text = "Change Location";
        PrimaryActionButton.Content = "Apply";

        WeatherFormGrid.Visibility = Visibility.Visible;
        WebLinkFormGrid.Visibility = Visibility.Collapsed;

        WeatherCityInput.Text = weatherVm.CustomCity ?? string.Empty;
        WeatherStatusMessage.Text = string.Empty;
        WeatherStatusMessage.Visibility = Visibility.Collapsed;
        WeatherActionSpinner.Visibility = Visibility.Collapsed;

        PrimaryActionButton.IsEnabled = !string.IsNullOrWhiteSpace(WeatherCityInput.Text);

        Loaded += (s, e) =>
        {
            WeatherCityInput.Focus();
            WeatherCityInput.SelectAll();
        };
    }

    private void OnWeatherInputTextChanged(object sender, TextChangedEventArgs e)
    {
        PrimaryActionButton.IsEnabled = !string.IsNullOrWhiteSpace(WeatherCityInput.Text);
        WeatherStatusMessage.Visibility = Visibility.Collapsed;
    }

    private void OnWeatherInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && PrimaryActionButton.IsEnabled)
        {
            ApplyWeatherLocation();
            e.Handled = true;
        }
    }

    private async void OnWeatherAutoLocationClick(object sender, RoutedEventArgs e)
    {
        if (_weatherVm == null) return;

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
        if (string.IsNullOrWhiteSpace(query)) return;

        WeatherActionSpinner.Visibility = Visibility.Visible;
        WeatherStatusMessage.Visibility = Visibility.Collapsed;
        PrimaryActionButton.IsEnabled = false;

        try
        {
            bool success = await _weatherVm.SetCustomCityAsync(query);
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

        SidebarTitleText.Text = "Web Link";
        SidebarSubtitleText.Text = "Quick Access";
        SidebarBadgeCategoryText.Text = "DOMAIN";
        SidebarBadgeValueText.Text = "Website";

        WebLinkFaviconImage.Source = null;
        WebLinkFaviconImage.Visibility = Visibility.Collapsed;
        WebLinkFallbackIcon.Visibility = Visibility.Visible;
        WebLinkSidebarSpinner.Visibility = Visibility.Collapsed;

        HeaderTitleText.Text = "Add Web Shortcut";
        PrimaryActionButton.Content = "Add Shortcut";

        WeatherFormGrid.Visibility = Visibility.Collapsed;
        WebLinkFormGrid.Visibility = Visibility.Visible;

        WebLinkAddToCanvasCheck.IsChecked = true;
        WebLinkAddToSidebarCheck.IsChecked = true;

        if (!string.IsNullOrWhiteSpace(initialUrl))
        {
            WebLinkUrlInput.Text = initialUrl;
        }
        else
        {
            WebLinkUrlInput.Text = string.Empty;
            WebLinkTitleInput.Text = string.Empty;
        }

        PrimaryActionButton.IsEnabled = !string.IsNullOrWhiteSpace(WebLinkUrlInput.Text);

        Loaded += (s, e) =>
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
        };
    }

    private void OnWebLinkUrlTextChanged(object sender, TextChangedEventArgs e)
    {
        string raw = WebLinkUrlInput.Text.Trim();
        PrimaryActionButton.IsEnabled = !string.IsNullOrWhiteSpace(raw);

        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        if (string.IsNullOrWhiteSpace(raw))
        {
            SidebarBadgeValueText.Text = "Website";
            WebLinkFaviconImage.Source = null;
            WebLinkFaviconImage.Visibility = Visibility.Collapsed;
            WebLinkFallbackIcon.Visibility = Visibility.Visible;
            WebLinkSidebarSpinner.Visibility = Visibility.Collapsed;
            if (!_userManuallyEditedTitle) WebLinkTitleInput.Text = string.Empty;
            return;
        }

        string normalized = WebFaviconService.NormalizeUrl(raw);

        try
        {
            var uri = new Uri(normalized);
            SidebarBadgeValueText.Text = uri.Host.Replace("www.", "");
        }
        catch
        {
            SidebarBadgeValueText.Text = "Website";
        }

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
        if (e.Key == Key.Enter && PrimaryActionButton.IsEnabled)
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
        if (string.IsNullOrWhiteSpace(raw)) return;

        string normalized = WebFaviconService.NormalizeUrl(raw);
        string title = WebLinkTitleInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = WebFaviconService.InferTitleFromUrl(normalized);
        }

        _webLinkResult = new WebLinkCreatedEventArgs
        {
            Url = normalized,
            Title = title,
            IconPath = _resolvedIconPath,
            AddToCanvas = WebLinkAddToCanvasCheck.IsChecked == true,
            AddToSidebar = WebLinkAddToSidebarCheck.IsChecked == true
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

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = null;

        WebLinkFaviconImage.Source = null;
        _resolvedIconPath = null;
        _weatherVm = null;
    }

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
