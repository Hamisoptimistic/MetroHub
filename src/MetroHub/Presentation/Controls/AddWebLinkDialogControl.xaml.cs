using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
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
    public event EventHandler<WebLinkCreatedEventArgs>? WebLinkCreated;
    public event EventHandler? DialogClosed;

    private IDisposable? _dialogScope;
    private CancellationTokenSource? _debounceCts;
    private string? _resolvedIconPath;
    private bool _userManuallyEditedTitle;

    public AddWebLinkDialogControl()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;
    }

    public void ShowDialog(string? prefillUrl = null)
    {
        _dialogScope?.Dispose();
        _dialogScope = MainWindow.EnterDialogScope();

        // Reset state
        _userManuallyEditedTitle = false;
        _resolvedIconPath = null;
        LivePreviewImage.Source = null;
        LivePreviewImage.Visibility = Visibility.Collapsed;
        LivePreviewFallbackIcon.Visibility = Visibility.Visible;
        LivePreviewSpinner.Visibility = Visibility.Collapsed;
        LivePreviewTitleText.Text = "Website Preview";
        LivePreviewDomainText.Text = "Enter a URL below...";
        AddButton.IsEnabled = false;
        AddToCanvasCheck.IsChecked = true;
        AddToSidebarCheck.IsChecked = true;

        Visibility = Visibility.Visible;
        Opacity = 0.0;

        if (!string.IsNullOrWhiteSpace(prefillUrl))
        {
            UrlTextBox.Text = prefillUrl;
        }
        else
        {
            UrlTextBox.Text = string.Empty;
            TitleTextBox.Text = string.Empty;
        }

        var anim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        // Defer focus to anim.Completed per WPF_PERFORMANCE.md Rule #4 to prevent IME pump hitch
        anim.Completed += (s, e) =>
        {
            if (!string.IsNullOrWhiteSpace(prefillUrl))
            {
                TitleTextBox.Focus();
                TitleTextBox.SelectAll();
            }
            else
            {
                UrlTextBox.Focus();
            }
        };
        BeginAnimation(OpacityProperty, anim);
    }

    public void HideDialog()
    {
        _debounceCts?.Cancel();
        _debounceCts = null;

        var anim = new DoubleAnimation(Opacity, 0.0, TimeSpan.FromMilliseconds(100))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        anim.Completed += (s, e) =>
        {
            Visibility = Visibility.Collapsed;
            LivePreviewImage.Source = null; // Instantly release decoded bitmap from memory
            _resolvedIconPath = null;
            UrlTextBox.Text = string.Empty;
            TitleTextBox.Text = string.Empty;
            _dialogScope?.Dispose();
            _dialogScope = null;
            DialogClosed?.Invoke(this, EventArgs.Empty);
        };
        BeginAnimation(OpacityProperty, anim);
    }

    private void OnBackdropMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == OverlayRoot)
        {
            HideDialog();
        }
    }

    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Stop click from bubbling to backdrop
        e.Handled = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        HideDialog();
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

        if (string.IsNullOrWhiteSpace(raw))
        {
            AddButton.IsEnabled = false;
            LivePreviewTitleText.Text = "Website Preview";
            LivePreviewDomainText.Text = "Enter a URL below...";
            LivePreviewImage.Visibility = Visibility.Collapsed;
            LivePreviewFallbackIcon.Visibility = Visibility.Visible;
            LivePreviewSpinner.Visibility = Visibility.Collapsed;
            _resolvedIconPath = null;
            return;
        }

        AddButton.IsEnabled = true;

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
                    LivePreviewFallbackIcon.Visibility = Visibility.Collapsed;
                    LivePreviewImage.Visibility = Visibility.Collapsed;
                });

                // Fetch high-resolution favicon
                string? iconPath = await WebFaviconService.GetFaviconPathAsync(normalized, token);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    LivePreviewSpinner.Visibility = Visibility.Collapsed;

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
            if (AddButton.IsEnabled)
            {
                OnAddConfirmClick(sender, e);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HideDialog();
            e.Handled = true;
        }
    }

    private void OnAddConfirmClick(object sender, RoutedEventArgs e)
    {
        string raw = UrlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return;

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
            // If neither checked, default to Canvas
            addToCanvas = true;
        }

        WebLinkCreated?.Invoke(this, new WebLinkCreatedEventArgs
        {
            Url = normalized,
            Title = title,
            IconPath = _resolvedIconPath,
            AddToCanvas = addToCanvas,
            AddToSidebar = addToSidebar
        });

        HideDialog();
    }
}
