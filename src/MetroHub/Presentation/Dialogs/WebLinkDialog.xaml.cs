using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using MetroHub.Core.Services;

namespace MetroHub.Presentation.Dialogs;

/// <summary>
/// Dedicated dialog for adding web link shortcuts to the canvas and/or sidebar rail.
/// Inherits from MetroDialog for automatic acrylic backdrop, window dragging, and styling.
/// </summary>
public partial class WebLinkDialog : MetroDialog
{
    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _cycleCts;
    private string? _resolvedIconPath;
    private bool _userManuallyEditedTitle;
    private WebLinkCreatedEventArgs? _webLinkResult;

    // In-memory icon session cache for previewing and cycling without disk writes
    private readonly Dictionary<string, byte[]> _sourceBytesCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _candidateUrls = [];
    private string? _htmlHeadIconUrl;
    private string? _lastNormalizedUrl;
    private int _currentSourceIndex = -1;
    private byte[]? _pendingIconBytes;

    public WebLinkCreatedEventArgs? WebLinkResult => _webLinkResult;

    public WebLinkDialog()
    {
        InitializeComponent();
    }

    public void Setup(string? initialUrl = null, string? initialTitle = null, bool defaultCanvasOnly = false)
    {
        _debounceCts?.Cancel();
        _cycleCts?.Cancel();
        _userManuallyEditedTitle = false;
        _resolvedIconPath = null;
        _webLinkResult = null;
        _pendingIconBytes = null;
        _currentSourceIndex = -1;
        _candidateUrls.Clear();
        _sourceBytesCache.Clear();
        _htmlHeadIconUrl = null;
        _lastNormalizedUrl = null;

        Title = "Add Web Shortcut";
        if (WebLinkDestStackPanel != null) WebLinkDestStackPanel.Visibility = Visibility.Visible;
        if (WebLinkActionButton != null) WebLinkActionButton.Content = "Add Shortcut";
        if (WebLinkChangeIconButton != null) WebLinkChangeIconButton.IsEnabled = true;

        WebLinkFaviconImage.Source = null;
        WebLinkFaviconImage.Visibility = Visibility.Collapsed;
        WebLinkFallbackIcon.Visibility = Visibility.Visible;
        WebLinkSidebarSpinner.Visibility = Visibility.Collapsed;

        if (defaultCanvasOnly)
        {
            WebLinkDestCanvasRadio.IsChecked = true;
        }
        else
        {
            WebLinkDestBothRadio.IsChecked = true;
        }

        if (!string.IsNullOrWhiteSpace(initialUrl))
        {
            WebLinkUrlInput.Text = initialUrl;
            if (!string.IsNullOrWhiteSpace(initialTitle))
            {
                WebLinkTitleInput.Text = initialTitle;
            }
        }
        else
        {
            WebLinkUrlInput.Text = string.Empty;
            WebLinkTitleInput.Text = string.Empty;
        }
    }

    public void SetupEdit(string initialUrl, string initialTitle, string? initialIconPath = null)
    {
        _debounceCts?.Cancel();
        _cycleCts?.Cancel();
        _userManuallyEditedTitle = true;
        _resolvedIconPath = initialIconPath;
        _webLinkResult = null;
        _pendingIconBytes = null;
        _currentSourceIndex = -1;
        _candidateUrls.Clear();
        _sourceBytesCache.Clear();
        _htmlHeadIconUrl = null;
        _lastNormalizedUrl = null;

        Title = "Edit Web Shortcut";
        if (WebLinkDestStackPanel != null) WebLinkDestStackPanel.Visibility = Visibility.Collapsed;
        if (WebLinkActionButton != null) WebLinkActionButton.Content = "Save Changes";
        if (WebLinkChangeIconButton != null) WebLinkChangeIconButton.IsEnabled = true;

        WebLinkSidebarSpinner.Visibility = Visibility.Collapsed;
        WebLinkUrlInput.Text = initialUrl ?? string.Empty;
        WebLinkTitleInput.Text = initialTitle ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(initialIconPath) && File.Exists(initialIconPath))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(initialIconPath);
                bmp.EndInit();
                bmp.Freeze();

                WebLinkFaviconImage.Source = bmp;
                WebLinkFaviconImage.Visibility = Visibility.Visible;
                WebLinkFallbackIcon.Visibility = Visibility.Collapsed;
            }
            catch
            {
                WebLinkFaviconImage.Source = null;
                WebLinkFaviconImage.Visibility = Visibility.Collapsed;
                WebLinkFallbackIcon.Visibility = Visibility.Visible;
            }
        }
        else
        {
            WebLinkFaviconImage.Source = null;
            WebLinkFaviconImage.Visibility = Visibility.Collapsed;
            WebLinkFallbackIcon.Visibility = Visibility.Visible;
        }
    }

    protected override IInputElement? InitialFocusedElement =>
        !string.IsNullOrWhiteSpace(WebLinkUrlInput?.Text)
            ? WebLinkTitleInput
            : WebLinkUrlInput;

    private void OnWebLinkUrlTextChanged(object sender, TextChangedEventArgs e)
    {
        WebLinkStatusMessage.Visibility = Visibility.Collapsed;
        string raw = WebLinkUrlInput.Text.Trim();

        _debounceCts?.Cancel();
        _cycleCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        if (string.IsNullOrWhiteSpace(raw))
        {
            _pendingIconBytes = null;
            _currentSourceIndex = -1;
            _candidateUrls.Clear();
            _sourceBytesCache.Clear();
            _htmlHeadIconUrl = null;
            _lastNormalizedUrl = null;

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
            _pendingIconBytes = null;
            _currentSourceIndex = -1;
            _candidateUrls.Clear();
            _sourceBytesCache.Clear();
            _htmlHeadIconUrl = null;
            _lastNormalizedUrl = null;

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

                await Dispatcher.InvokeAsync(() =>
                {
                    WebLinkSidebarSpinner.Visibility = Visibility.Visible;
                    WebLinkFallbackIcon.Visibility = Visibility.Collapsed;
                });

                if (!string.Equals(_lastNormalizedUrl, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    _lastNormalizedUrl = normalized;
                    _candidateUrls.Clear();
                    _sourceBytesCache.Clear();
                    _currentSourceIndex = -1;
                    _pendingIconBytes = null;
                    _htmlHeadIconUrl = null;
                }

                // Check existing on-disk cache for read-only preview
                string cachedPath = WebFaviconService.GetCachedIconPath(normalized);
                if (!string.IsNullOrWhiteSpace(cachedPath) && File.Exists(cachedPath))
                {
                    try
                    {
                        var fi = new FileInfo(cachedPath);
                        if (fi.Length > 200)
                        {
                            byte[] diskBytes = await File.ReadAllBytesAsync(cachedPath, token).ConfigureAwait(false);
                            var diskBmp = MetroHub.Presentation.Converters.IconPathToBitmapConverter.CreateBitmapFromBytes(diskBytes);
                            if (diskBmp != null)
                            {
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    _resolvedIconPath = cachedPath;
                                    _pendingIconBytes = null;
                                    WebLinkSidebarSpinner.Visibility = Visibility.Collapsed;
                                    WebLinkFaviconImage.Source = diskBmp;
                                    WebLinkFaviconImage.Visibility = Visibility.Visible;
                                    WebLinkFallbackIcon.Visibility = Visibility.Collapsed;
                                });
                                return;
                            }
                        }
                    }
                    catch { }
                }

                string queryUrl = normalized;
                if (Uri.TryCreate(normalized, UriKind.Absolute, out Uri? parsedUri))
                {
                    queryUrl = parsedUri.GetLeftPart(UriPartial.Path);
                }

                string? resolvedTitle = null;
                try
                {
                    using var headCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    headCts.CancelAfter(TimeSpan.FromSeconds(3));
                    var headInfo = await WebFaviconService.ExtractHtmlHeadInfoAsync(queryUrl, headCts.Token).ConfigureAwait(false);
                    if (headInfo != null)
                    {
                        resolvedTitle = headInfo.SiteName ?? headInfo.Title;
                        _htmlHeadIconUrl = headInfo.IconUrl;
                    }
                }
                catch { }

                if (token.IsCancellationRequested) return;

                if (!string.IsNullOrWhiteSpace(resolvedTitle))
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!_userManuallyEditedTitle)
                        {
                            WebLinkTitleInput.Text = resolvedTitle;
                        }
                    });
                }

                var candidateUrls = WebFaviconService.GetCandidateSourceUrls(normalized, _htmlHeadIconUrl);
                _candidateUrls.Clear();
                _candidateUrls.AddRange(candidateUrls);

                byte[]? chosenBytes = null;
                int chosenIndex = -1;
                BitmapImage? chosenBmp = null;

                for (int i = 0; i < candidateUrls.Count; i++)
                {
                    if (token.IsCancellationRequested) return;
                    string candidateUrl = candidateUrls[i];
                    byte[]? bytes;
                    if (_sourceBytesCache.TryGetValue(candidateUrl, out var cachedBytes))
                    {
                        bytes = cachedBytes;
                    }
                    else
                    {
                        bytes = await WebFaviconService.DownloadIconBytesAsync(candidateUrl, token).ConfigureAwait(false);
                        if (bytes != null && bytes.Length >= 200)
                        {
                            _sourceBytesCache[candidateUrl] = bytes;
                        }
                    }

                    if (bytes != null && bytes.Length >= 200)
                    {
                        var bmp = MetroHub.Presentation.Converters.IconPathToBitmapConverter.CreateBitmapFromBytes(bytes);
                        if (bmp != null)
                        {
                            chosenBytes = bytes;
                            chosenIndex = i;
                            chosenBmp = bmp;
                            break;
                        }
                    }
                }

                if (token.IsCancellationRequested) return;

                await Dispatcher.InvokeAsync(() =>
                {
                    WebLinkSidebarSpinner.Visibility = Visibility.Collapsed;

                    if (chosenBmp != null && chosenBytes != null)
                    {
                        _currentSourceIndex = chosenIndex;
                        _pendingIconBytes = chosenBytes;
                        WebLinkFaviconImage.Source = chosenBmp;
                        WebLinkFaviconImage.Visibility = Visibility.Visible;
                        WebLinkFallbackIcon.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        _currentSourceIndex = -1;
                        _pendingIconBytes = null;
                        WebLinkFaviconImage.Source = null;
                        WebLinkFaviconImage.Visibility = Visibility.Collapsed;
                        WebLinkFallbackIcon.Visibility = Visibility.Visible;
                    }
                });
            }
            catch (OperationCanceledException) { }
            catch
            {
                if (!token.IsCancellationRequested)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        WebLinkSidebarSpinner.Visibility = Visibility.Collapsed;
                        WebLinkFallbackIcon.Visibility = Visibility.Visible;
                    });
                }
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

    private async void OnChangeIconClick(object sender, RoutedEventArgs e)
    {
        string raw = WebLinkUrlInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            WebLinkStatusMessage.Text = "Please enter a website URL first.";
            WebLinkStatusMessage.Visibility = Visibility.Visible;
            WebLinkUrlInput.Focus();
            return;
        }

        string normalized = WebFaviconService.NormalizeUrl(raw);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            WebLinkStatusMessage.Text = "Please enter a valid website URL (e.g. www.google.com).";
            WebLinkStatusMessage.Visibility = Visibility.Visible;
            WebLinkUrlInput.Focus();
            return;
        }

        WebLinkStatusMessage.Visibility = Visibility.Collapsed;

        _debounceCts?.Cancel();
        _cycleCts?.Cancel();
        _cycleCts = new CancellationTokenSource();
        var token = _cycleCts.Token;

        try
        {
            WebLinkChangeIconButton.IsEnabled = false;
            WebLinkSidebarSpinner.Visibility = Visibility.Visible;

            if (_candidateUrls.Count == 0 || !string.Equals(_lastNormalizedUrl, normalized, StringComparison.OrdinalIgnoreCase))
            {
                _lastNormalizedUrl = normalized;
                _candidateUrls.Clear();
                _sourceBytesCache.Clear();

                string queryUrl = normalized;
                if (Uri.TryCreate(normalized, UriKind.Absolute, out Uri? parsedUri))
                {
                    queryUrl = parsedUri.GetLeftPart(UriPartial.Path);
                }

                if (_htmlHeadIconUrl == null)
                {
                    try
                    {
                        using var headCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        headCts.CancelAfter(TimeSpan.FromSeconds(3));
                        var headInfo = await WebFaviconService.ExtractHtmlHeadInfoAsync(queryUrl, headCts.Token).ConfigureAwait(true);
                        _htmlHeadIconUrl = headInfo?.IconUrl;
                    }
                    catch { }
                }

                var urls = WebFaviconService.GetCandidateSourceUrls(normalized, _htmlHeadIconUrl);
                _candidateUrls.AddRange(urls);
            }

            if (_candidateUrls.Count == 0)
            {
                WebLinkStatusMessage.Text = "No icon sources found for this URL.";
                WebLinkStatusMessage.Visibility = Visibility.Visible;
                return;
            }

            byte[]? existingBytes = null;
            if (_currentSourceIndex == -1 && !string.IsNullOrWhiteSpace(_resolvedIconPath) && File.Exists(_resolvedIconPath))
            {
                try
                {
                    existingBytes = await File.ReadAllBytesAsync(_resolvedIconPath, token).ConfigureAwait(true);
                }
                catch { }
            }

            byte[]? currentBytes = _pendingIconBytes ?? existingBytes;

            int count = _candidateUrls.Count;
            int startIndex = _currentSourceIndex >= 0 ? (_currentSourceIndex + 1) % count : 0;
            bool foundDifferent = false;

            for (int attempt = 0; attempt < count; attempt++)
            {
                if (token.IsCancellationRequested) return;

                int candidateIdx = (startIndex + attempt) % count;
                string candidateUrl = _candidateUrls[candidateIdx];

                byte[]? bytes;
                if (_sourceBytesCache.TryGetValue(candidateUrl, out var cachedBytes))
                {
                    bytes = cachedBytes;
                }
                else
                {
                    bytes = await WebFaviconService.DownloadIconBytesAsync(candidateUrl, token).ConfigureAwait(true);
                    if (bytes != null && bytes.Length >= 200)
                    {
                        _sourceBytesCache[candidateUrl] = bytes;
                    }
                }

                if (bytes != null && bytes.Length >= 200)
                {
                    if (currentBytes != null && bytes.AsSpan().SequenceEqual(currentBytes))
                    {
                        continue;
                    }

                    var bmp = MetroHub.Presentation.Converters.IconPathToBitmapConverter.CreateBitmapFromBytes(bytes);
                    if (bmp != null)
                    {
                        _currentSourceIndex = candidateIdx;
                        _pendingIconBytes = bytes;
                        WebLinkFaviconImage.Source = bmp;
                        WebLinkFaviconImage.Visibility = Visibility.Visible;
                        WebLinkFallbackIcon.Visibility = Visibility.Collapsed;
                        foundDifferent = true;
                        break;
                    }
                }
            }

            if (!foundDifferent && currentBytes != null)
            {
                WebLinkStatusMessage.Text = "No alternative icons found for this website.";
                WebLinkStatusMessage.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Safe.Log(ex, "Failed to cycle favicon source");
        }
        finally
        {
            WebLinkSidebarSpinner.Visibility = Visibility.Collapsed;
            WebLinkChangeIconButton.IsEnabled = true;
        }
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

        if (_pendingIconBytes != null)
        {
            string? savedPath = WebFaviconService.SaveIconToCache(normalized, _pendingIconBytes);
            if (!string.IsNullOrWhiteSpace(savedPath))
            {
                MetroHub.Presentation.Converters.IconPathToBitmapConverter.InvalidateCache(savedPath);
                _resolvedIconPath = savedPath;
            }
        }

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

    public static WebLinkCreatedEventArgs? Show(
        Window? owner,
        string? initialUrl = null,
        string? initialTitle = null,
        bool defaultCanvasOnly = false)
    {
        var dlg = new WebLinkDialog
        {
            Owner = owner
        };
        dlg.Setup(initialUrl, initialTitle, defaultCanvasOnly);
        return dlg.ShowDialog() == true ? dlg.WebLinkResult : null;
    }

    public static WebLinkCreatedEventArgs? ShowEdit(
        Window? owner,
        string initialUrl,
        string initialTitle,
        string? initialIconPath = null)
    {
        var dlg = new WebLinkDialog
        {
            Owner = owner
        };
        dlg.SetupEdit(initialUrl, initialTitle, initialIconPath);
        return dlg.ShowDialog() == true ? dlg.WebLinkResult : null;
    }
}

/// <summary>
/// Event arguments capturing user choices when creating a web link shortcut.
/// </summary>
public sealed class WebLinkCreatedEventArgs : EventArgs
{
    public string Url { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? IconPath { get; init; }
    public bool AddToCanvas { get; init; }
    public bool AddToSidebar { get; init; }
}
