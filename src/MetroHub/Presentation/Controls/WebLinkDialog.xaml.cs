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

    public WebLinkCreatedEventArgs? WebLinkResult => _webLinkResult;

    public WebLinkDialog()
    {
        InitializeComponent();
        Background = Brushes.Transparent;
    }

    protected override void OnBackdropTypeChanged(WindowBackdropType oldValue, WindowBackdropType newValue)
    {
        // Suppress WPF-UI's default backdrop override
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
