using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MetroHub.Core.Services;
using System.Windows.Interop;
using System.Windows.Shell;
using Wpf.Ui.Controls;

namespace MetroHub.Presentation.Controls;

/// <summary>
/// Dedicated dialog for adding web link shortcuts to the canvas and/or sidebar rail.
/// </summary>
public partial class WebLinkDialog : FluentWindow
{
    private CancellationTokenSource? _debounceCts;
    private string? _resolvedIconPath;
    private bool _userManuallyEditedTitle;
    private WebLinkCreatedEventArgs? _webLinkResult;
    private bool _isFullyActivated;
    private DateTime _shownTime;

    public WebLinkCreatedEventArgs? WebLinkResult => _webLinkResult;

    public WebLinkDialog()
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

    public void Setup(string? initialUrl = null)
    {
        _userManuallyEditedTitle = false;
        _resolvedIconPath = null;
        _webLinkResult = null;

        WebLinkFaviconImage.Source = null;
        WebLinkFaviconImage.Visibility = Visibility.Collapsed;
        WebLinkFallbackIcon.Visibility = Visibility.Visible;
        WebLinkSidebarSpinner.Visibility = Visibility.Collapsed;
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
        if (string.IsNullOrWhiteSpace(normalized))
        {
            WebLinkFaviconImage.Source = null;
            WebLinkFaviconImage.Visibility = Visibility.Collapsed;
            WebLinkFallbackIcon.Visibility = Visibility.Visible;
            WebLinkSidebarSpinner.Visibility = Visibility.Collapsed;
            if (!_userManuallyEditedTitle) WebLinkTitleInput.Text = string.Empty;
            return;
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
        if (e.Key == Key.Enter)
        {
            ApplyWebLink();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
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

    private void OnAddWebLinkClick(object sender, RoutedEventArgs e)
    {
        ApplyWebLink();
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
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Equals("https://www/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("https://www.", StringComparison.OrdinalIgnoreCase))
        {
            WebLinkStatusMessage.Text = "Please enter a valid website URL (e.g. www.google.com).";
            WebLinkStatusMessage.Visibility = Visibility.Visible;
            WebLinkUrlInput.Focus();
            return;
        }

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

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    public static WebLinkCreatedEventArgs? Show(Window? owner, string? initialUrl = null)
    {
        var dlg = new WebLinkDialog
        {
            Owner = owner
        };
        dlg.Setup(initialUrl);
        return dlg.ShowDialog() == true ? dlg.WebLinkResult : null;
    }
}
