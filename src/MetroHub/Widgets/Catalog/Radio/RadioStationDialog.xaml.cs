using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MetroHub.Widgets.Catalog.Radio;
using MetroHub.Widgets.Catalog.Radio.Services;
using MetroHub.Core.Services;
using MetroHub.Presentation.Dialogs;
using MetroHub.Presentation.Themes;
using System.Windows.Interop;
using System.Windows.Shell;
using Wpf.Ui.Controls;
using TextBox = System.Windows.Controls.TextBox;

namespace MetroHub.Presentation.Controls;

/// <summary>
/// Dedicated modal dialog for searching and adding online radio stations or direct stream URLs.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "WPF Window lifecycle manages disposable fields via OnDialogClosed.")]
public sealed partial class RadioStationDialog : MetroDialog
{
    private RadioStation? _radioStationResult;
    private CancellationTokenSource? _radioSearchCts;
    private CancellationTokenSource? _radioProbeCts;
    private string? _initialRadioCategoryId;
    private RadioSearchResultItem? _selectedRadioSearchResult;
    private StreamProbeResult? _lastProbeResult;
    private bool _userManuallyEditedRadioName;

    private static readonly Brush ProbeStatusHintBrush = ThemeTokens.TextSecondaryBrush;
    private static readonly Brush ProbeStatusActiveBrush = ThemeTokens.TextPrimaryBrush;
    private static readonly Brush SearchStatusSelectedBrush = ThemeTokens.TextPrimaryBrush;

    internal ObservableCollection<RadioSearchResultItem> RadioSearchResults { get; } = new();
    internal ObservableCollection<RadioCategory> RadioCategories { get; } = new();
    public RadioCategoryManagementViewModel CategoryViewModel { get; } = new();

    public RadioStation? RadioStationResult => _radioStationResult;

    public RadioStationDialog()
    {
        InitializeComponent();
        Background = Brushes.Transparent;
        RadioSearchResultsList.ItemsSource = RadioSearchResults;
        RadioCategoryComboBox.ItemsSource = RadioCategories;
        RadioCategoriesPanel.DataContext = CategoryViewModel;
        RadioCatalogService.Instance.CatalogChanged += OnCatalogChanged;
        Closed += OnDialogClosed;
        IsVisibleChanged += OnWindowIsVisibleChanged;
        PreviewKeyDown += OnDialogPreviewKeyDown;
        PreviewMouseDown += OnDialogPreviewMouseDown;
        RowIconPickerPopup.Closed += (s, e) => RowIconPickerPopup.Tag = null;
    }

    private void OnDialogClosed(object? sender, EventArgs e)
    {
        RadioCatalogService.Instance.CatalogChanged -= OnCatalogChanged;
        CancelPendingSearch();
        CancelPendingProbe();
        CategoryViewModel.Dispose();
    }

    private void OnWindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            CancelPendingSearch();
            CancelPendingProbe();
        }
    }

    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            string currentSelectedId = GetSelectedCategoryId();
            LoadCategories();
            var match = RadioCategories.FirstOrDefault(c => string.Equals(c.Id, currentSelectedId, StringComparison.OrdinalIgnoreCase))
                        ?? RadioCategories.FirstOrDefault();
            if (match != null)
            {
                RadioCategoryComboBox.SelectedItem = match;
            }
        }).FireAndForget("RadioStationDialog.OnCatalogChanged");
    }

    private void LoadCategories()
    {
        RadioCategories.Clear();
        var catalog = RadioCatalogService.Instance.GetCatalog();
        if (catalog?.Categories != null)
        {
            foreach (var cat in catalog.Categories)
            {
                RadioCategories.Add(cat);
            }
        }

        var match = RadioCategories.FirstOrDefault(c => string.Equals(c.Id, _initialRadioCategoryId, StringComparison.OrdinalIgnoreCase))
                    ?? RadioCategories.FirstOrDefault();

        if (match != null)
        {
            RadioCategoryComboBox.SelectedItem = match;
        }
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

        Title = "Add Radio Station";
        PrimaryActionButton.Content = "Add Station";
        PrimaryActionButton.IsEnabled = false;
        PrimaryActionButton.IsDefault = true;
        DialogFooterPanel.Visibility = Visibility.Visible;

        LoadCategories();
        CategoryViewModel.RefreshCategories();

        RadioModeTabSelector.SelectedIndex = 0;
        RadioSearchPanel.Visibility = Visibility.Visible;
        RadioDirectPanel.Visibility = Visibility.Collapsed;
        RadioCategoriesPanel.Visibility = Visibility.Collapsed;

        RadioSearchInput.Text = string.Empty;
        RadioSearchWatermark.Visibility = Visibility.Visible;
        RadioSearchResults.Clear();
        RadioSearchPlaceholderText.Text = "Type 2 or more characters to search 40,000+ online stations";
        RadioSearchPlaceholderText.Visibility = Visibility.Visible;
        RadioSearchStatusMessage.Visibility = Visibility.Collapsed;
        RadioSearchBrowseView.Visibility = Visibility.Visible;
        RadioSearchSelectionPanel.Visibility = Visibility.Collapsed;
        RadioSearchNameInput.Text = string.Empty;
        RadioSearchEditorStatusMessage.Visibility = Visibility.Collapsed;

        RadioDirectUrlInput.Text = string.Empty;
        RadioDirectNameInput.Text = string.Empty;
        RadioProbeSpinner.Visibility = Visibility.Collapsed;
        RadioProbeIcon.Visibility = Visibility.Collapsed;
        RadioProbeStatusText.Text = "Supports MP3/AAC/OGG streams or single-station .pls/.m3u";
        RadioProbeStatusText.Foreground = ProbeStatusHintBrush;
        RadioDirectStatusMessage.Visibility = Visibility.Collapsed;
    }

    private void OnRadioTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RadioModeTabSelector == null || RadioSearchPanel == null || RadioDirectPanel == null || RadioCategoriesPanel == null || DialogFooterPanel == null) return;

        if (RadioModeTabSelector.SelectedIndex == 0)
        {
            Title = "Add Radio Station";
            DialogFooterPanel.Visibility = Visibility.Visible;
            PrimaryActionButton.IsDefault = true;

            RadioSearchPanel.Visibility = Visibility.Visible;
            RadioDirectPanel.Visibility = Visibility.Collapsed;
            RadioCategoriesPanel.Visibility = Visibility.Collapsed;

            SyncCategoryComboBox();

            if (RadioSearchSelectionPanel.Visibility == Visibility.Visible)
            {
                UpdateSearchButtonState();
                RadioSearchNameInput.Focus();
            }
            else
            {
                PrimaryActionButton.IsEnabled = false;
                RadioSearchInput.Focus();
            }
        }
        else if (RadioModeTabSelector.SelectedIndex == 1)
        {
            Title = "Add Radio Station";
            DialogFooterPanel.Visibility = Visibility.Visible;
            PrimaryActionButton.IsDefault = true;

            RadioSearchPanel.Visibility = Visibility.Collapsed;
            RadioDirectPanel.Visibility = Visibility.Visible;
            RadioCategoriesPanel.Visibility = Visibility.Collapsed;

            SyncCategoryComboBox();

            UpdateDirectButtonState();
            RadioDirectUrlInput.Focus();
        }
        else if (RadioModeTabSelector.SelectedIndex == 2)
        {
            Title = "Manage Categories";
            DialogFooterPanel.Visibility = Visibility.Collapsed;
            PrimaryActionButton.IsDefault = false;

            RadioSearchPanel.Visibility = Visibility.Collapsed;
            RadioDirectPanel.Visibility = Visibility.Collapsed;
            RadioCategoriesPanel.Visibility = Visibility.Visible;

            CategoryViewModel.RefreshCategories();
            NewCategoryNameTextBox.Focus();
        }
    }

    private void SyncCategoryComboBox()
    {
        string currentSelectedId = GetSelectedCategoryId();
        LoadCategories();
        var match = RadioCategories.FirstOrDefault(c => string.Equals(c.Id, currentSelectedId, StringComparison.OrdinalIgnoreCase))
                    ?? RadioCategories.FirstOrDefault();
        if (match != null)
        {
            RadioCategoryComboBox.SelectedItem = match;
        }
    }

    private void OnDialogPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            var renamingItem = CategoryViewModel.Categories.FirstOrDefault(c => c.IsRenaming);
            if (renamingItem != null)
            {
                CategoryViewModel.CancelRenameCommand.Execute(renamingItem);
                e.Handled = true;
                return;
            }

            var pendingDeleteItem = CategoryViewModel.Categories.FirstOrDefault(c => c.IsPendingDelete);
            if (pendingDeleteItem != null)
            {
                CategoryViewModel.CancelDeleteCommand.Execute(pendingDeleteItem);
                e.Handled = true;
                return;
            }
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
            if (RadioSearchResultsList.SelectedItem is RadioSearchResultItem item)
            {
                SelectRadioSearchResult(item);
                e.Handled = true;
            }
            else if (RadioSearchResults.Count > 0)
            {
                RadioSearchResultsList.SelectedIndex = 0;
                SelectRadioSearchResult(RadioSearchResults[0]);
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
                        string displayQuery = trimmed.Length > 36 ? trimmed[..36] + "..." : trimmed;
                        RadioSearchPlaceholderText.Text = $"No stations found matching \"{displayQuery}\".";
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
                            Codec = !string.IsNullOrWhiteSpace(s.Codec) ? s.Codec.ToUpperInvariant() : null,
                            CountryCode = !string.IsNullOrWhiteSpace(s.CountryCode) ? s.CountryCode.Trim().ToUpperInvariant() : null,
                            Tags = parts.Count > 1 ? parts[1] : (!string.IsNullOrWhiteSpace(s.Tags) ? s.Tags.Trim() : null)
                        });
                    }
                });
            }
            catch (OperationCanceledException ex)
            {
                Safe.Ignore(ex, "RadioStationDialog.SearchStationsCancelled");
            }
            catch (Exception ex)
            {
                Safe.Log("RadioStationDialog.SearchStations", ex);
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
        }, token).FireAndForget("RadioStationDialog.SearchStations");
    }

    private void OnRadioSearchResultsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RadioSearchResultsList.SelectedItem is RadioSearchResultItem item)
        {
            SelectRadioSearchResult(item);
        }
    }

    internal void SelectRadioSearchResult(RadioSearchResultItem item)
    {
        _selectedRadioSearchResult = item;
        RadioSearchBrowseView.Visibility = Visibility.Collapsed;
        RadioSearchSelectionPanel.Visibility = Visibility.Visible;

        RadioSelectedStationOriginalName.Text = item.Name;
        RadioSelectedStationStreamUrl.Text = item.ResolvedStreamUrl;
        RadioSelectedStationStreamUrl.ToolTip = item.ResolvedStreamUrl;

        RadioSelectedStationCountryText.Text = !string.IsNullOrWhiteSpace(item.CountryCode)
            ? (item.CountryCode.Length <= 3 ? item.CountryCode.ToUpperInvariant() : item.CountryCode)
            : "Global";

        bool hasKbps = item.BitrateDisplay.Contains("kbps", StringComparison.OrdinalIgnoreCase);
        bool hasCodec = !string.IsNullOrWhiteSpace(item.Codec);
        string formatText;

        if (hasKbps && hasCodec)
        {
            formatText = $"{item.BitrateDisplay} • {item.Codec!.Trim().ToUpperInvariant()}";
        }
        else if (hasKbps)
        {
            formatText = item.BitrateDisplay;
        }
        else if (hasCodec)
        {
            formatText = item.Codec!.Trim().ToUpperInvariant();
        }
        else
        {
            formatText = "Audio Stream";
        }

        RadioSelectedStationFormatText.Text = formatText;

        RadioSearchNameInput.Text = CleanStationName(item.Name);
        RadioSearchNameInput.Focus();
        RadioSearchNameInput.SelectAll();
        RadioSearchEditorStatusMessage.Visibility = Visibility.Collapsed;
        UpdateSearchButtonState();
    }

    private void OnChangeSelectedStationClick(object sender, RoutedEventArgs e)
    {
        ReturnToSearchResults();
    }

    private void ReturnToSearchResults()
    {
        RadioSearchSelectionPanel.Visibility = Visibility.Collapsed;
        RadioSearchBrowseView.Visibility = Visibility.Visible;
        RadioSearchResultsList.SelectedItem = null;
        _selectedRadioSearchResult = null;
        PrimaryActionButton.IsEnabled = false;
        RadioSearchInput.Focus();
        RadioSearchInput.SelectAll();
    }

    private void OnRadioSearchNameInputTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSearchButtonState();
    }

    private void OnRadioSearchNameInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (PrimaryActionButton.IsEnabled)
            {
                ApplyRadioStationAsync().FireAndForget("RadioStationDialog.ApplyStationOnNameEnter");
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            ReturnToSearchResults();
            e.Handled = true;
        }
    }

    private void UpdateSearchButtonState()
    {
        bool hasSelection = _selectedRadioSearchResult != null;
        bool hasName = !string.IsNullOrWhiteSpace(RadioSearchNameInput.Text);
        PrimaryActionButton.IsEnabled = hasSelection && hasName;
    }


    private void OnRadioDirectInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (PrimaryActionButton.IsEnabled)
            {
                ApplyRadioStationAsync().FireAndForget("RadioStationDialog.ApplyStationOnDirectEnter");
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
                                catch (Exception ex)
                                {
                                    Safe.Log(ex, "RadioStationDialog.InferHostName");
                                }
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
            catch (OperationCanceledException ex)
            {
                Safe.Ignore(ex, "RadioStationDialog.ProbeUrlCancelled");
            }
            catch (Exception ex)
            {
                Safe.Log("RadioStationDialog.ProbeUrl", ex);
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
        }, token).FireAndForget("RadioStationDialog.ProbeUrl");
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
        if (RadioCategoryComboBox.SelectedItem is RadioCategory cat && !string.IsNullOrWhiteSpace(cat.Id))
        {
            return cat.Id;
        }

        if (RadioCategoryComboBox.SelectedValue is string val && !string.IsNullOrWhiteSpace(val))
        {
            return val;
        }

        return _initialRadioCategoryId ?? "ambient";
    }

    private async Task ApplyRadioStationAsync()
    {
        RadioStation? stationToAdd = null;
        string categoryId = GetSelectedCategoryId();

        if (RadioModeTabSelector.SelectedIndex == 0)
        {
            if (_selectedRadioSearchResult is not { } selected)
            {
                RadioSearchEditorStatusMessage.Text = "Please select a station from the search results.";
                RadioSearchEditorStatusMessage.Foreground = ThemeTokens.StatusErrorBrush;
                RadioSearchEditorStatusMessage.Visibility = Visibility.Visible;
                return;
            }

            string chosenName = RadioSearchNameInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(chosenName))
            {
                RadioSearchEditorStatusMessage.Text = "Please enter a station name.";
                RadioSearchEditorStatusMessage.Foreground = ThemeTokens.StatusErrorBrush;
                RadioSearchEditorStatusMessage.Visibility = Visibility.Visible;
                RadioSearchNameInput.Focus();
                return;
            }

            stationToAdd = new RadioStation
            {
                Id = $"custom_{Guid.NewGuid():N}",
                Name = chosenName,
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
                Safe.Log(ex, "RadioStationDialog.AddCustomStation");
                PrimaryActionButton.IsEnabled = true;
                var targetStatus = RadioModeTabSelector.SelectedIndex == 0 ? RadioSearchEditorStatusMessage : RadioDirectStatusMessage;
                targetStatus.Text = $"Failed to save station: {ex.Message}";
                targetStatus.Foreground = ThemeTokens.StatusErrorBrush;
                targetStatus.Visibility = Visibility.Visible;
            }
        }
    }

    private static readonly SearchValues<string> s_codecHlsKeywords = SearchValues.Create(["mpegurl", "m3u8"], StringComparison.OrdinalIgnoreCase);
    private static readonly SearchValues<string> s_codecOggKeywords = SearchValues.Create(["ogg", "opus"], StringComparison.OrdinalIgnoreCase);
    private static readonly SearchValues<string> s_codecMp3Keywords = SearchValues.Create(["mpeg", "mp3"], StringComparison.OrdinalIgnoreCase);

    private static string? InferCodecFromContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return null;
        ReadOnlySpan<char> ct = contentType.AsSpan();
        if (ct.ContainsAny(s_codecHlsKeywords)) return "HLS";
        if (ct.Contains("aac", StringComparison.OrdinalIgnoreCase)) return "AAC";
        if (ct.ContainsAny(s_codecOggKeywords)) return "OGG";
        if (ct.Contains("flac", StringComparison.OrdinalIgnoreCase)) return "FLAC";
        if (ct.ContainsAny(s_codecMp3Keywords)) return "MP3";
        return null;
    }

    [GeneratedRegex(@"\s*[\(\[\{]\s*(?:\d+\s*[kK](?:bps)?)?\s*(?:mp3|aac\+?|ogg|flac|opus|hd|hq|audio|stream)?\s*[\)\]\}]", RegexOptions.IgnoreCase)]
    private static partial Regex BitrateBracketRegex();

    [GeneratedRegex(@"\s*\|\s*\d+\s*(?:k|kbps|kbit|aac|mp3|ogg|opus).*$", RegexOptions.IgnoreCase)]
    private static partial Regex PipeBitrateRegex();

    [GeneratedRegex(@"\s*-\s*(?:online|live stream|stream|webradio)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex StreamSuffixRegex();

    [GeneratedRegex(@"^[+#]\s*")]
    private static partial Regex LeadingSymbolRegex();

    internal static string CleanStationName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string s = raw.Trim();
        s = LeadingSymbolRegex().Replace(s, string.Empty);
        s = BitrateBracketRegex().Replace(s, string.Empty);
        s = PipeBitrateRegex().Replace(s, string.Empty);
        s = StreamSuffixRegex().Replace(s, string.Empty);
        s = s.Trim();

        return s.Length > 0 ? s : raw.Trim();
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

    private async void OnPrimaryActionButtonClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ApplyRadioStationAsync();
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "RadioStationDialog.OnPrimaryActionButtonClick");
        }
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    #region Categories Management & Drag-Drop Reordering

    private void OnNewCategoryTextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CategoryViewModel.AddCategoryCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnRowCategoryIconButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement button && button.DataContext is RadioCategoryItemViewModel item)
        {
            RowIconPickerPopup.PlacementTarget = button;
            RowIconPickerPopup.Tag = item;
            RowIconPickerPopup.IsOpen = true;
        }
    }

    private void OnRowIconPickerItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is RadioSymbolItem symbolItem && RowIconPickerPopup.Tag is RadioCategoryItemViewModel targetItem)
        {
            CategoryViewModel.UpdateRowIconAsync(targetItem, symbolItem).FireAndForget("RadioStationDialog.UpdateRowIcon");
            RowIconPickerPopup.IsOpen = false;
            RowIconPickerPopup.Tag = null;
        }
    }

    private void OnCategoryNameTextBlockMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement elem && elem.DataContext is RadioCategoryItemViewModel item)
        {
            CategoryViewModel.BeginRenameCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void OnInlineRenameTextBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.IsVisible)
        {
            tb.Focus();
            tb.SelectAll();
        }
    }

    private void OnInlineRenameTextBoxIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox tb && tb.IsVisible)
        {
            Dispatcher.InvokeAsync(() =>
            {
                tb.Focus();
                tb.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Input).FireAndForget("RadioStationDialog.FocusRename");
        }
    }

    private void OnInlineRenameTextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is RadioCategoryItemViewModel item)
        {
            if (e.Key == Key.Enter)
            {
                CategoryViewModel.CommitRenameCommand.Execute(item);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CategoryViewModel.CancelRenameCommand.Execute(item);
                e.Handled = true;
            }
        }
    }

    private void OnInlineRenameTextBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is RadioCategoryItemViewModel item && item.IsRenaming)
        {
            CategoryViewModel.CommitRenameCommand.Execute(item);
        }
    }

    private void OnDialogPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var renamingItem = CategoryViewModel.Categories.FirstOrDefault(c => c.IsRenaming);
        if (renamingItem == null) return;

        var current = e.OriginalSource as DependencyObject;
        bool isInsideRenameTextBox = false;
        bool isCancelButton = false;

        while (current != null && current is not Window)
        {
            if (current is TextBox tb && ReferenceEquals(tb.DataContext, renamingItem))
            {
                isInsideRenameTextBox = true;
                break;
            }

            if (current is FrameworkElement fe && fe.Name == "InlineRenameCancelButton")
            {
                isCancelButton = true;
                break;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : (current is FrameworkContentElement fce ? fce.Parent : LogicalTreeHelper.GetParent(current));
        }

        if (isInsideRenameTextBox)
        {
            return;
        }

        if (isCancelButton)
        {
            CategoryViewModel.CancelRenameCommand.Execute(renamingItem);
            return;
        }

        CategoryViewModel.CommitRenameCommand.Execute(renamingItem);
    }

    private Point _dragStartPoint;
    private FrameworkElement? _draggedElement;
    private RadioCategoryItemViewModel? _draggedItem;

    private void OnRowPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveElement(e.OriginalSource as DependencyObject))
        {
            _draggedElement = null;
            _draggedItem = null;
            return;
        }

        if (sender is FrameworkElement row && row.DataContext is RadioCategoryItemViewModel item)
        {
            if (item.IsPendingDelete || item.IsRenaming)
            {
                _draggedElement = null;
                _draggedItem = null;
                return;
            }

            _dragStartPoint = e.GetPosition(null);
            _draggedElement = row;
            _draggedItem = item;
        }
    }

    private void OnRowPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedElement == null || _draggedItem == null)
        {
            return;
        }

        Point currentPos = e.GetPosition(null);
        Vector diff = currentPos - _dragStartPoint;

        if (Math.Abs(diff.Y) >= SystemParameters.MinimumVerticalDragDistance ||
            Math.Abs(diff.X) >= SystemParameters.MinimumHorizontalDragDistance)
        {
            var element = _draggedElement;
            var item = _draggedItem;
            element.Opacity = 0.35;

            try
            {
                DragDrop.DoDragDrop(element, item, DragDropEffects.Move);
            }
            finally
            {
                element.Opacity = 1.0;
                _draggedElement = null;
                _draggedItem = null;
                ClearDropIndicators(element);
            }
        }
    }

    private void OnRowPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _draggedElement = null;
        _draggedItem = null;
    }

    private void OnRowDragOver(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement targetRow &&
            targetRow.DataContext is RadioCategoryItemViewModel targetItem &&
            e.Data.GetData(typeof(RadioCategoryItemViewModel)) is RadioCategoryItemViewModel droppedItem &&
            droppedItem != targetItem)
        {
            Point pt = e.GetPosition(targetRow);
            bool dropAbove = pt.Y < (targetRow.ActualHeight / 2.0);

            SetRowIndicators(targetRow, dropAbove);

            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        if (sender is FrameworkElement row)
        {
            ClearDropIndicators(row);
        }

        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void OnRowDragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement row)
        {
            ClearDropIndicators(row);
        }
    }

    private void OnRowDrop(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement targetRow)
        {
            ClearDropIndicators(targetRow);

            if (targetRow.DataContext is RadioCategoryItemViewModel targetItem &&
                e.Data.GetData(typeof(RadioCategoryItemViewModel)) is RadioCategoryItemViewModel droppedItem &&
                droppedItem != targetItem)
            {
                int oldIndex = CategoryViewModel.Categories.IndexOf(droppedItem);
                int targetIndex = CategoryViewModel.Categories.IndexOf(targetItem);

                if (oldIndex >= 0 && targetIndex >= 0)
                {
                    Point pt = e.GetPosition(targetRow);
                    bool dropAbove = pt.Y < (targetRow.ActualHeight / 2.0);
                    int newIndex = dropAbove
                        ? (oldIndex < targetIndex ? targetIndex - 1 : targetIndex)
                        : (oldIndex < targetIndex ? targetIndex : targetIndex + 1);

                    if (newIndex >= 0 && newIndex < CategoryViewModel.Categories.Count && newIndex != oldIndex)
                    {
                        CategoryViewModel.ReorderCategory(oldIndex, newIndex).FireAndForget("RadioStationDialog.OnRowDrop");
                    }
                }
            }
        }
        e.Handled = true;
    }

    private static bool IsInteractiveElement(DependencyObject? element)
    {
        while (element != null && element is not Window)
        {
            if (element is System.Windows.Controls.Primitives.ButtonBase ||
                element is System.Windows.Controls.Primitives.TextBoxBase ||
                element is System.Windows.Controls.Primitives.Popup)
            {
                return true;
            }

            if (element is FrameworkElement fe && fe.Name == "RowRootBorder")
            {
                break;
            }

            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : (element is FrameworkContentElement fce ? fce.Parent : LogicalTreeHelper.GetParent(element));
        }
        return false;
    }

    private static void SetRowIndicators(FrameworkElement row, bool topActive)
    {
        if (row.FindName("PART_TopIndicator") is FrameworkElement top)
        {
            top.Visibility = topActive ? Visibility.Visible : Visibility.Collapsed;
        }
        if (row.FindName("PART_BottomIndicator") is FrameworkElement bottom)
        {
            bottom.Visibility = topActive ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private static void ClearDropIndicators(FrameworkElement row)
    {
        if (row.FindName("PART_TopIndicator") is FrameworkElement top)
        {
            top.Visibility = Visibility.Collapsed;
        }
        if (row.FindName("PART_BottomIndicator") is FrameworkElement bottom)
        {
            bottom.Visibility = Visibility.Collapsed;
        }
    }

    #endregion
}
