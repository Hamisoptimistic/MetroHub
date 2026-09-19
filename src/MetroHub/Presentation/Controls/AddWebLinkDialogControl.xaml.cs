using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using MetroHub.Core.Services;

namespace MetroHub.Presentation.Controls;

public sealed class WebLinkCreatedEventArgs : EventArgs
{
    public string Url { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? IconPath { get; init; }
    public bool AddToCanvas { get; init; }
    public bool AddToSidebar { get; init; }
}

public sealed partial class AddWebLinkDialogControl : UserControl
{
    public event EventHandler? UrlChanged;
    public event EventHandler? EnterPressed;

    private CancellationTokenSource? _debounceCts;
    private string? _resolvedIconPath;
    private bool _userManuallyEditedTitle;

    public string CurrentUrl => UrlTextBox.Text.Trim();
    public string CurrentTitle => TitleTextBox.Text.Trim();

    public AddWebLinkDialogControl()
    {
        InitializeComponent();
    }

    public void Initialize(string? prefillUrl = null)
    {
        _userManuallyEditedTitle = false;
        _resolvedIconPath = null;
        LivePreviewImage.Source = null;
        LivePreviewImage.Visibility = Visibility.Collapsed;
        LivePreviewFallbackIcon.Visibility = Visibility.Visible;
        LivePreviewSpinner.Visibility = Visibility.Collapsed;
        LivePreviewSpinner.IsIndeterminate = false;
        LivePreviewTitleText.Text = "Website Preview";
        LivePreviewDomainText.Text = "Enter a URL below...";
        AddToCanvasCheck.IsChecked = true;
        AddToSidebarCheck.IsChecked = true;

        if (!string.IsNullOrWhiteSpace(prefillUrl))
        {
            UrlTextBox.Text = prefillUrl;
        }
        else
        {
            UrlTextBox.Text = string.Empty;
            TitleTextBox.Text = string.Empty;
        }
    }

    public void FocusInput(bool selectTitle = false)
    {
        if (selectTitle && !string.IsNullOrWhiteSpace(UrlTextBox.Text))
        {
            TitleTextBox.Focus();
            TitleTextBox.SelectAll();
        }
        else
        {
            UrlTextBox.Focus();
            UrlTextBox.SelectAll();
        }
    }

    public void Cleanup()
    {
        _debounceCts?.Cancel();
        _debounceCts = null;
        LivePreviewImage.Source = null; // release decoded bitmap immediately
        _resolvedIconPath = null;
    }

    public WebLinkCreatedEventArgs? GetResult()
    {
        string raw = UrlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return null;

        string normalized = WebFaviconService.NormalizeUrl(raw);
        string title = TitleTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = WebFaviconService.InferTitleFromUrl(normalized);
        }

        bool addToCanvas = AddToCanvasCheck.IsChecked == true;
        bool addToSidebar = AddToSidebarCheck.IsChecked == true;

        if (!addToCanvas && !addToSidebar)
        {
            addToCanvas = true;
        }

        return new WebLinkCreatedEventArgs
        {
            Url = normalized,
            Title = title,
            IconPath = _resolvedIconPath,
            AddToCanvas = addToCanvas,
            AddToSidebar = addToSidebar
        };
    }

    private void OnPasteClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    UrlTextBox.Text = text.Trim();
                    UrlTextBox.CaretIndex = UrlTextBox.Text.Length;
                }
            }
        }
        catch { }
    }

    private void OnUrlTextChanged(object sender, TextChangedEventArgs e)
    {
        string raw = UrlTextBox.Text.Trim();
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        UrlChanged?.Invoke(this, EventArgs.Empty);

        if (string.IsNullOrWhiteSpace(raw))
        {
            LivePreviewTitleText.Text = "Website Preview";
            LivePreviewDomainText.Text = "Enter a URL below...";
            LivePreviewImage.Visibility = Visibility.Collapsed;
            LivePreviewFallbackIcon.Visibility = Visibility.Visible;
            LivePreviewSpinner.Visibility = Visibility.Collapsed;
            LivePreviewSpinner.IsIndeterminate = false;
            _resolvedIconPath = null;
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                // Debounce keystrokes
                await Task.Delay(220, token);
                if (token.IsCancellationRequested) return;

                string normalized = WebFaviconService.NormalizeUrl(raw);
                string domain = WebFaviconService.ExtractDomain(normalized);
                string inferredTitle = WebFaviconService.InferTitleFromUrl(normalized);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;

                    if (!_userManuallyEditedTitle || string.IsNullOrWhiteSpace(TitleTextBox.Text))
                    {
                        TitleTextBox.Text = inferredTitle;
                        _userManuallyEditedTitle = false;
                    }

                    LivePreviewTitleText.Text = !string.IsNullOrWhiteSpace(TitleTextBox.Text) ? TitleTextBox.Text : inferredTitle;
                    LivePreviewDomainText.Text = domain;
                    LivePreviewSpinner.Visibility = Visibility.Visible;
                    LivePreviewSpinner.IsIndeterminate = true;
                    LivePreviewFallbackIcon.Visibility = Visibility.Collapsed;
                    LivePreviewImage.Visibility = Visibility.Collapsed;
                });

                // Fetch high-resolution favicon
                string? iconPath = await WebFaviconService.GetFaviconPathAsync(normalized, token);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    LivePreviewSpinner.Visibility = Visibility.Collapsed;
                    LivePreviewSpinner.IsIndeterminate = false;

                    if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
                    {
                        try
                        {
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.DecodePixelWidth = 56; // 28x28 rendered at 2x high-DPI; saves ~80% memory per icon
                            bmp.UriSource = new Uri(iconPath, UriKind.Absolute);
                            bmp.EndInit();
                            bmp.Freeze();

                            LivePreviewImage.Source = bmp;
                            LivePreviewImage.Visibility = Visibility.Visible;
                            LivePreviewFallbackIcon.Visibility = Visibility.Collapsed;
                            _resolvedIconPath = iconPath;
                            return;
                        }
                        catch { }
                    }

                    // Fallback to vector globe
                    LivePreviewImage.Visibility = Visibility.Collapsed;
                    LivePreviewFallbackIcon.Visibility = Visibility.Visible;
                    _resolvedIconPath = null;
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AddWebLinkDialog] Error updating preview: {ex.Message}");
            }
        }, token);
    }

    private void OnTitleTextChanged(object sender, TextChangedEventArgs e)
    {
        if (TitleTextBox.IsFocused)
        {
            _userManuallyEditedTitle = true;
        }

        if (!string.IsNullOrWhiteSpace(TitleTextBox.Text))
        {
            LivePreviewTitleText.Text = TitleTextBox.Text;
        }
        else if (!string.IsNullOrWhiteSpace(UrlTextBox.Text))
        {
            LivePreviewTitleText.Text = WebFaviconService.InferTitleFromUrl(UrlTextBox.Text);
        }
        else
        {
            LivePreviewTitleText.Text = "Website Preview";
        }
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            EnterPressed?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }
}
