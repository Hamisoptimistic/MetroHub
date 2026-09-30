using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MetroHub.Core.Radio;
using MetroHub.Core.Services;
using MetroHub.Presentation.Themes;
using System.Windows.Interop;
using System.Windows.Shell;
using Wpf.Ui.Controls;

namespace MetroHub.Presentation.Controls;

/// <summary>
/// Dedicated modal dialog for searching and adding online radio stations or direct stream URLs.
/// </summary>
public partial class RadioStationDialog : FluentWindow
{
    private RadioStation? _radioStationResult;
    private CancellationTokenSource? _radioSearchCts;
    private CancellationTokenSource? _radioProbeCts;
    private string? _initialRadioCategoryId;
    private RadioSearchResultItem? _selectedRadioSearchResult;
    private StreamProbeResult? _lastProbeResult;
    private bool _userManuallyEditedRadioName;
    private bool _isFullyActivated;
    private DateTime _shownTime;

    private static readonly Brush ProbeStatusHintBrush = ThemeTokens.CreateFrozenBrush(Color.FromArgb(0xA5, 0xFF, 0xFF, 0xFF));
    private static readonly Brush ProbeStatusActiveBrush = ThemeTokens.CreateFrozenBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
    private static readonly Brush SearchStatusSelectedBrush = ThemeTokens.CreateFrozenBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF));

    internal ObservableCollection<RadioSearchResultItem> RadioSearchResults { get; } = new();

    public RadioStation? RadioStationResult => _radioStationResult;

    public RadioStationDialog()
    {
        InitializeComponent();
        Background = Brushes.Transparent;
        RadioSearchResultsList.ItemsSource = RadioSearchResults;
        IsVisibleChanged += OnWindowIsVisibleChanged;
    }

    private void OnWindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            CancelPendingSearch();
            CancelPendingProbe();
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
                Height = Math.Min(420, Math.Max(300, workArea.Height * 0.85));
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

    public static RadioStation? Show(Window? owner, string activeCategoryId = "ambient")
    {
        var window = new RadioStationDialog
        {
            Owner = owner
        };
        window.Setup(activeCategoryId);
        bool? result = window.ShowDialog();
        return result == true ? window.RadioStationResult : null;
    }

    public void Setup(string activeCategoryId = "ambient")
    {
        _initialRadioCategoryId = string.IsNullOrWhiteSpace(activeCategoryId) ? "ambient" : activeCategoryId.ToLowerInvariant();
        _radioStationResult = null;
        _selectedRadioSearchResult = null;
        _lastProbeResult = null;
        _userManuallyEditedRadioName = false;

        HeaderTitleText.Text = "Add Radio Station";
        PrimaryActionButton.Content = "Add Station";
        PrimaryActionButton.IsEnabled = false;

        switch (_initialRadioCategoryId)
        {
            case "nature":
                RadioCatNature.IsChecked = true;
                break;
            case "lofi":
                RadioCatLofi.IsChecked = true;
                break;
            case "coding":
                RadioCatCoding.IsChecked = true;
                break;
            default:
                RadioCatAmbient.IsChecked = true;
                break;
        }

        RadioSearchTabRadio.IsChecked = true;
        RadioSearchPanel.Visibility = Visibility.Visible;
        RadioDirectPanel.Visibility = Visibility.Collapsed;

        RadioSearchInput.Text = string.Empty;
        RadioSearchWatermark.Visibility = Visibility.Visible;
        RadioSearchResults.Clear();
        RadioSearchPlaceholderText.Text = "Type 2 or more characters to search 40,000+ online stations";
        RadioSearchPlaceholderText.Visibility = Visibility.Visible;
        RadioSearchStatusMessage.Visibility = Visibility.Collapsed;

        RadioDirectUrlInput.Text = string.Empty;
        RadioDirectNameInput.Text = string.Empty;
        RadioProbeSpinner.Visibility = Visibility.Collapsed;
        RadioProbeIcon.Visibility = Visibility.Collapsed;
        RadioProbeStatusText.Text = "Supports MP3/AAC/OGG streams or single-station .pls/.m3u";
        RadioProbeStatusText.Foreground = ProbeStatusHintBrush;
        RadioDirectStatusMessage.Visibility = Visibility.Collapsed;

        Loaded += (s, e) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                RadioSearchInput.Focus();
                RadioSearchInput.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Input);
        };
    }

    private void OnRadioTabChanged(object sender, RoutedEventArgs e)
    {
        if (RadioSearchTabRadio == null || RadioDirectTabRadio == null || RadioSearchPanel == null || RadioDirectPanel == null) return;

        if (RadioSearchTabRadio.IsChecked == true)
        {
            RadioSearchPanel.Visibility = Visibility.Visible;
            RadioDirectPanel.Visibility = Visibility.Collapsed;
            PrimaryActionButton.IsEnabled = _selectedRadioSearchResult != null;
            RadioSearchInput.Focus();
        }
        else
        {
            RadioSearchPanel.Visibility = Visibility.Collapsed;
            RadioDirectPanel.Visibility = Visibility.Visible;
            UpdateDirectButtonState();
            RadioDirectUrlInput.Focus();
        }
    }

    private void OnRadioSearchInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            if (RadioSearchResults.Count > 0)
            {
                if (RadioSearchResultsList.SelectedIndex < RadioSearchResults.Count - 1)
                {
                    RadioSearchResultsList.SelectedIndex++;
                }
                else
                {
                    RadioSearchResultsList.SelectedIndex = 0;
                }
                RadioSearchResultsList.ScrollIntoView(RadioSearchResultsList.SelectedItem);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Up)
        {
            if (RadioSearchResults.Count > 0)
            {
                if (RadioSearchResultsList.SelectedIndex > 0)
                {
                    RadioSearchResultsList.SelectedIndex--;
                }
                else
                {
                    RadioSearchResultsList.SelectedIndex = RadioSearchResults.Count - 1;
                }
                RadioSearchResultsList.ScrollIntoView(RadioSearchResultsList.SelectedItem);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Enter)
        {
            if (RadioSearchResultsList.SelectedItem != null)
            {
                ApplyRadioStation();
                e.Handled = true;
            }
        }
    }

    private void OnRadioSearchInputTextChanged(object sender, TextChangedEventArgs e)
    {
        string query = RadioSearchInput.Text;
        RadioSearchWatermark.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;
        RadioSearchStatusMessage.Visibility = Visibility.Collapsed;

        CancelPendingSearch();
        _radioSearchCts = new CancellationTokenSource();
        var token = _radioSearchCts.Token;

        string trimmed = query.Trim();
        if (trimmed.Length < 2)
        {
            RadioSearchSpinner.Visibility = Visibility.Collapsed;
            RadioSearchResults.Clear();
            RadioSearchPlaceholderText.Text = "Type 2 or more characters to search 40,000+ online stations";
            RadioSearchPlaceholderText.Visibility = Visibility.Visible;
            _selectedRadioSearchResult = null;
            PrimaryActionButton.IsEnabled = false;
            return;
        }

        RadioSearchPlaceholderText.Visibility = Visibility.Collapsed;
        RadioSearchSpinner.Visibility = Visibility.Visible;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350, token);
                if (token.IsCancellationRequested) return;

                var results = await RadioBrowserClient.Instance.SearchStationsAsync(trimmed, limit: 16, ct: token);
                if (token.IsCancellationRequested) return;

                await Dispatcher.InvokeAsync(() =>
                {
                    RadioSearchSpinner.Visibility = Visibility.Collapsed;
                    RadioSearchResults.Clear();

                    if (results.Count == 0)
                    {
                        RadioSearchPlaceholderText.Text = $"No stations found matching \"{trimmed}\".";
                        RadioSearchPlaceholderText.Visibility = Visibility.Visible;
                        PrimaryActionButton.IsEnabled = false;
                        return;
                    }

                    RadioSearchPlaceholderText.Visibility = Visibility.Collapsed;
                    foreach (var s in results)
                    {
                        var parts = new List<string>(2);
                        if (!string.IsNullOrWhiteSpace(s.CountryCode)) parts.Add(s.CountryCode.Trim());
                        if (!string.IsNullOrWhiteSpace(s.Tags))
                        {
                            var topTags = string.Join(", ", s.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3));
                            if (!string.IsNullOrWhiteSpace(topTags)) parts.Add(topTags);
                        }

                        string subtitle = parts.Count > 0 ? string.Join(" • ", parts) : "Online Radio";
                        string bitrate = s.Bitrate > 0 ? $"{s.Bitrate} kbps" : (!string.IsNullOrWhiteSpace(s.Codec) ? s.Codec.ToUpperInvariant() : "Stream");

                        RadioSearchResults.Add(new RadioSearchResultItem
                        {
                            Name = s.Name.Trim(),
                            Subtitle = subtitle,
                            BitrateDisplay = bitrate,
                            ResolvedStreamUrl = !string.IsNullOrWhiteSpace(s.UrlResolved) ? s.UrlResolved : s.Url,
                            StationUuid = !string.IsNullOrWhiteSpace(s.StationUuid) ? s.StationUuid : null,
                            BitrateKbps = s.Bitrate > 0 ? s.Bitrate : 128,
                            Codec = !string.IsNullOrWhiteSpace(s.Codec) ? s.Codec.ToUpperInvariant() : null
                        });
                    }
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        RadioSearchSpinner.Visibility = Visibility.Collapsed;
                        RadioSearchStatusMessage.Text = $"Search failed: {ex.Message}";
                        RadioSearchStatusMessage.Foreground = ThemeTokens.StatusErrorBrush;
                        RadioSearchStatusMessage.Visibility = Visibility.Visible;
                    });
                }
            }
        }, token);
    }

    private void OnRadioSearchResultsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RadioSearchResultsList.SelectedItem is RadioSearchResultItem item)
        {
            _selectedRadioSearchResult = item;
            PrimaryActionButton.IsEnabled = true;
            RadioSearchStatusMessage.Text = $"Selected: {item.Name}";
            RadioSearchStatusMessage.Foreground = SearchStatusSelectedBrush;
            RadioSearchStatusMessage.Visibility = Visibility.Visible;
        }
        else
        {
            _selectedRadioSearchResult = null;
            PrimaryActionButton.IsEnabled = false;
        }
    }

    private void OnRadioDirectPasteClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText().Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    RadioDirectUrlInput.Text = text;
                    if (string.IsNullOrWhiteSpace(RadioDirectNameInput.Text))
                    {
                        RadioDirectNameInput.Focus();
                    }
                }
            }
        }
        catch { }
    }

    private void OnRadioDirectInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (PrimaryActionButton.IsEnabled)
            {
                ApplyRadioStation();
                e.Handled = true;
            }
        }
    }

    private void OnRadioDirectNameTextChanged(object sender, TextChangedEventArgs e)
    {
        if (RadioDirectNameInput.IsKeyboardFocused)
        {
            _userManuallyEditedRadioName = true;
        }
        UpdateDirectButtonState();
    }

    private void OnRadioDirectUrlTextChanged(object sender, TextChangedEventArgs e)
    {
        RadioDirectStatusMessage.Visibility = Visibility.Collapsed;
        string raw = RadioDirectUrlInput.Text.Trim();

        CancelPendingProbe();
        _radioProbeCts = new CancellationTokenSource();
        var token = _radioProbeCts.Token;

        if (string.IsNullOrWhiteSpace(raw))
        {
            RadioProbeSpinner.Visibility = Visibility.Collapsed;
            RadioProbeIcon.Visibility = Visibility.Collapsed;
            RadioProbeStatusText.Text = "Supports MP3/AAC/OGG streams or single-station .pls/.m3u";
            RadioProbeStatusText.Foreground = ProbeStatusHintBrush;
            _lastProbeResult = null;
            UpdateDirectButtonState();
            return;
        }

        RadioProbeSpinner.Visibility = Visibility.Visible;
        RadioProbeIcon.Visibility = Visibility.Collapsed;
        RadioProbeStatusText.Text = "Validating stream...";
        RadioProbeStatusText.Foreground = ProbeStatusActiveBrush;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350, token);
                if (token.IsCancellationRequested) return;

                var result = await StreamUrlProbeService.Instance.ProbeUrlAsync(raw, token);
                if (token.IsCancellationRequested) return;

                await Dispatcher.InvokeAsync(() =>
                {
                    RadioProbeSpinner.Visibility = Visibility.Collapsed;
                    _lastProbeResult = result;

                    if (result.IsValid)
                    {
                        RadioProbeIcon.Symbol = SymbolRegular.Checkmark24;
                        RadioProbeIcon.Foreground = ThemeTokens.StatusSuccessBrush;
                        RadioProbeIcon.Visibility = Visibility.Visible;

                        string formatText = !string.IsNullOrWhiteSpace(result.ContentType) ? result.ContentType : "Audio stream";
                        RadioProbeStatusText.Text = $"Valid stream detected ({formatText})";
                        RadioProbeStatusText.Foreground = ThemeTokens.StatusSuccessBrush;

                        if (!_userManuallyEditedRadioName && string.IsNullOrWhiteSpace(RadioDirectNameInput.Text))
                        {
                            if (!string.IsNullOrWhiteSpace(result.InferredName))
                            {
                                RadioDirectNameInput.Text = result.InferredName;
                            }
                            else
                            {
                                try
                                {
                                    var uri = new Uri(raw);
                                    string host = uri.Host.Replace("www.", "");
                                    int dot = host.IndexOf('.');
                                    string inferred = dot > 0 ? char.ToUpperInvariant(host[0]) + host[1..dot] : host;
                                    RadioDirectNameInput.Text = inferred;
                                }
                                catch { }
                            }
                        }
                    }
                    else
                    {
                        RadioProbeIcon.Symbol = SymbolRegular.Warning24;
                        RadioProbeIcon.Foreground = ThemeTokens.StatusErrorBrush;
                        RadioProbeIcon.Visibility = Visibility.Visible;

                        RadioProbeStatusText.Text = result.ErrorMessage ?? "Invalid stream";
                        RadioProbeStatusText.Foreground = ThemeTokens.StatusErrorBrush;
                    }

                    UpdateDirectButtonState();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        RadioProbeSpinner.Visibility = Visibility.Collapsed;
                        RadioProbeIcon.Symbol = SymbolRegular.Warning24;
                        RadioProbeIcon.Foreground = ThemeTokens.StatusErrorBrush;
                        RadioProbeIcon.Visibility = Visibility.Visible;
                        RadioProbeStatusText.Text = $"Probe failed: {ex.Message}";
                        RadioProbeStatusText.Foreground = ThemeTokens.StatusErrorBrush;
                        UpdateDirectButtonState();
                    });
                }
            }
        }, token);
    }

    private void UpdateDirectButtonState()
    {
        bool hasUrl = !string.IsNullOrWhiteSpace(RadioDirectUrlInput.Text);
        bool hasName = !string.IsNullOrWhiteSpace(RadioDirectNameInput.Text);
        bool isValid = _lastProbeResult != null && _lastProbeResult.IsValid;

        PrimaryActionButton.IsEnabled = hasUrl && hasName && isValid;
    }

    private string GetSelectedCategoryId()
    {
        if (RadioCatAmbient.IsChecked == true) return "ambient";
        if (RadioCatNature.IsChecked == true) return "nature";
        if (RadioCatLofi.IsChecked == true) return "lofi";
        if (RadioCatCoding.IsChecked == true) return "coding";
        return _initialRadioCategoryId ?? "ambient";
    }

    private async void ApplyRadioStation()
    {
        RadioStation? stationToAdd = null;
        string categoryId = GetSelectedCategoryId();

        if (RadioSearchTabRadio.IsChecked == true)
        {
            if (_selectedRadioSearchResult is not { } selected)
            {
                RadioSearchStatusMessage.Text = "Please select a station from the search results.";
                RadioSearchStatusMessage.Foreground = ThemeTokens.StatusErrorBrush;
                RadioSearchStatusMessage.Visibility = Visibility.Visible;
                return;
            }

            stationToAdd = new RadioStation
            {
                Id = $"custom_{Guid.NewGuid():N}",
                Name = selected.Name.Trim(),
                StreamUrl = selected.ResolvedStreamUrl.Trim(),
                BitrateKbps = selected.BitrateKbps > 0 ? selected.BitrateKbps : 128,
                Category = categoryId,
                Icon = "HeadphonesSoundWave24",
                IsCustom = true,
                ApiStationUuid = selected.StationUuid,
                Codec = selected.Codec
            };
        }
        else
        {
            string rawUrl = RadioDirectUrlInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                RadioDirectStatusMessage.Text = "Please enter an audio stream URL.";
                RadioDirectStatusMessage.Visibility = Visibility.Visible;
                RadioDirectUrlInput.Focus();
                return;
            }

            string name = RadioDirectNameInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                RadioDirectStatusMessage.Text = "Please enter a station name.";
                RadioDirectStatusMessage.Visibility = Visibility.Visible;
                RadioDirectNameInput.Focus();
                return;
            }

            if (_lastProbeResult == null || !_lastProbeResult.IsValid)
            {
                RadioDirectStatusMessage.Text = _lastProbeResult?.ErrorMessage ?? "Please wait for stream validation to complete.";
                RadioDirectStatusMessage.Visibility = Visibility.Visible;
                return;
            }

            stationToAdd = new RadioStation
            {
                Id = $"custom_{Guid.NewGuid():N}",
                Name = name,
                StreamUrl = _lastProbeResult.ResolvedStreamUrl ?? rawUrl,
                BitrateKbps = _lastProbeResult.BitrateKbps > 0 ? _lastProbeResult.BitrateKbps : 128,
                Category = categoryId,
                Icon = "HeadphonesSoundWave24",
                IsCustom = true,
                ApiStationUuid = null,
                Codec = InferCodecFromContentType(_lastProbeResult.ContentType)
            };
        }

        if (stationToAdd != null)
        {
            PrimaryActionButton.IsEnabled = false;
            try
            {
                await RadioCatalogService.Instance.AddCustomStationAsync(stationToAdd, categoryId);
                _radioStationResult = stationToAdd;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                PrimaryActionButton.IsEnabled = true;
                var targetStatus = RadioSearchTabRadio.IsChecked == true ? RadioSearchStatusMessage : RadioDirectStatusMessage;
                targetStatus.Text = $"Failed to save station: {ex.Message}";
                targetStatus.Foreground = ThemeTokens.StatusErrorBrush;
                targetStatus.Visibility = Visibility.Visible;
            }
        }
    }

    private static string? InferCodecFromContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return null;
        string ct = contentType.ToLowerInvariant();
        if (ct.Contains("mpegurl") || ct.Contains("m3u8")) return "HLS";
        if (ct.Contains("aac")) return "AAC";
        if (ct.Contains("ogg") || ct.Contains("opus")) return "OGG";
        if (ct.Contains("flac")) return "FLAC";
        if (ct.Contains("mpeg") || ct.Contains("mp3")) return "MP3";
        return null;
    }

    private void CancelPendingSearch()
    {
        _radioSearchCts?.Cancel();
        _radioSearchCts?.Dispose();
        _radioSearchCts = null;
    }

    private void CancelPendingProbe()
    {
        _radioProbeCts?.Cancel();
        _radioProbeCts?.Dispose();
        _radioProbeCts = null;
    }

    private void OnPrimaryActionButtonClick(object sender, RoutedEventArgs e)
    {
        ApplyRadioStation();
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
