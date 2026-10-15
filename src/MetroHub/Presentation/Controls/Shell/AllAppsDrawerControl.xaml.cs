using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MetroHub.Core.Models;
using MetroHub.Core.Search;
using MetroHub.Core.Services;
using MetroHub.Presentation.Themes;
using MetroHub.Presentation.Messaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MetroHub.Presentation.Controls
{
    public abstract class DrawerItem
    {
        public virtual bool IsHeader => false;
    }

    public sealed class DrawerGroupHeader(string letter) : DrawerItem
    {
        public override bool IsHeader => true;
        public string Header => letter;
    }

    public sealed class DrawerAppRow(CatalogItemModel app) : DrawerItem
    {
        public CatalogItemModel App => app;
    }

    public class SearchItemRowViewModel : ObservableObject
    {
        public Candidate Candidate { get; }
        public string DisplayName => Candidate.DisplayName;

        public string Subtitle
        {
            get
            {
                if (Candidate.Category == SearchCategory.Apps) return string.Empty;
                if (!Candidate.IsFolder)
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(Candidate.FullPathOrKey);
                        if (!string.IsNullOrEmpty(dir)) return dir;
                    }
                    catch { }
                }
                return Candidate.FullPathOrKey;
            }
        }

        public string FullPath => Candidate.FullPathOrKey;
        public bool IsFolder => Candidate.IsFolder || (!string.IsNullOrEmpty(FullPath) && Directory.Exists(FullPath));

        public bool IsExecutableOrScript
        {
            get
            {
                if (IsFolder || string.IsNullOrEmpty(FullPath)) return false;
                string ext = Path.GetExtension(FullPath).ToLowerInvariant();
                return ext is ".exe" or ".bat" or ".cmd" or ".ps1" or ".msc";
            }
        }

        public Visibility SubtitleVisibility => string.IsNullOrWhiteSpace(Subtitle) ? Visibility.Collapsed : Visibility.Visible;

        private ImageSource? _fileIcon;

        public ImageSource? Icon
        {
            get
            {
                if (Candidate.Tag is CatalogItemModel appModel)
                {
                    return appModel.Icon;
                }

                return _fileIcon;
            }
            private set
            {
                if (SetProperty(ref _fileIcon, value))
                {
                    NotifyIconProperties();
                }
            }
        }

        public Visibility CustomIconVisibility => Icon is not null ? Visibility.Visible : Visibility.Collapsed;
        public Wpf.Ui.Controls.SymbolRegular Symbol { get; }
        public Visibility SymbolVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;
        public string CategoryHeader { get; set; } = string.Empty;
        public Visibility CategoryHeaderVisibility => !string.IsNullOrEmpty(CategoryHeader) ? Visibility.Visible : Visibility.Collapsed;

        public bool IsApp => Candidate.Category == SearchCategory.Apps;
        public CatalogItemModel? AppModel => Candidate.Tag as CatalogItemModel;

        public SearchItemRowViewModel(Candidate candidate, string categoryHeader = "")
        {
            Candidate = candidate;
            CategoryHeader = categoryHeader;
            Symbol = candidate.Category switch
            {
                SearchCategory.Apps => Wpf.Ui.Controls.SymbolRegular.Apps24,
                SearchCategory.Folders => Wpf.Ui.Controls.SymbolRegular.Folder24,
                SearchCategory.Documents => Wpf.Ui.Controls.SymbolRegular.Document24,
                SearchCategory.Images => Wpf.Ui.Controls.SymbolRegular.Image24,
                SearchCategory.Media => Wpf.Ui.Controls.SymbolRegular.MusicNote224,
                SearchCategory.Code => Wpf.Ui.Controls.SymbolRegular.Code24,
                _ => candidate.IsFolder ? Wpf.Ui.Controls.SymbolRegular.Folder24 : Wpf.Ui.Controls.SymbolRegular.Document24
            };

            if (candidate.Tag is CatalogItemModel appModel)
            {
                System.ComponentModel.PropertyChangedEventManager.AddHandler(appModel, OnAppModelPropertyChanged, nameof(CatalogItemModel.Icon));
                // Kick off background extraction immediately if not already cached
                _ = appModel.Icon;
            }
            else
            {
                // Resolve Shell file/extension icon (synchronous for extensions/folders, async update for executables)
                _fileIcon = IconExtractorService.GetFileOrExtensionIcon(
                    candidate.FullPathOrKey,
                    candidate.IsFolder,
                    onAsyncIconLoaded: updatedIcon =>
                    {
                        var dispatcher = Application.Current?.Dispatcher;
                        if (dispatcher is null || dispatcher.CheckAccess())
                        {
                            Icon = updatedIcon;
                        }
                        else
                        {
                            dispatcher.InvokeAsync(() => Icon = updatedIcon, System.Windows.Threading.DispatcherPriority.Background);
                        }
                    });
            }
        }

        private void OnAppModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CatalogItemModel.Icon))
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null || dispatcher.CheckAccess())
                {
                    NotifyIconProperties();
                }
                else
                {
                    dispatcher.InvokeAsync(NotifyIconProperties, System.Windows.Threading.DispatcherPriority.Background);
                }
            }
        }

        private void NotifyIconProperties()
        {
            OnPropertyChanged(nameof(Icon));
            OnPropertyChanged(nameof(CustomIconVisibility));
            OnPropertyChanged(nameof(SymbolVisibility));
        }
    }

    public partial class AllAppsDrawerControl : UserControl
    {
        public event EventHandler<CatalogItemModel>? AppPinRequested;
        public event EventHandler<CatalogItemModel>? AppUnpinRequested;
        public event EventHandler<string>? FilePinRequested;
        public event EventHandler<CatalogItemModel>? AppLaunchRequested;
        public event EventHandler? ShellHideRequested;
        public event EventHandler? Opened;
        public event EventHandler? Closing;
        public event EventHandler? Closed;
        public event EventHandler? RefreshRequested;

        public Func<CatalogItemModel, bool>? IsAppPinnedPredicate { get; set; }
        public Func<string, bool>? IsFilePinnedPredicate { get; set; }
        public event EventHandler<string>? FileUnpinRequested;

        public bool IsOpen { get; private set; } = false;

        public double DrawerWidth => ActualWidth > 0 ? ActualWidth : (Width > 0 && !double.IsNaN(Width) ? Width : 320);

        private List<CatalogItemModel> _allApps = new();
        private Point _dragStartPoint;
        private CatalogItemModel? _draggedItem;
        private bool _isAppDragPotential = false;
        private bool _isAppsLoaded = false;
        private CatalogItemModel? _activeContextMenuItem;

        private readonly SearchOrchestrator _searchOrchestrator;
        private SearchItemRowViewModel? _activeSearchFileItem;
        private SearchItemRowViewModel? _draggedSearchRow;
        private bool _isSearchRowDragPotential = false;

        private static readonly Brush SearchBorderFocusedBrush = CreateFrozenBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        private static readonly Brush SearchBorderUnfocusedBrush = CreateFrozenBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        private static readonly Brush UnpinRedBrush = ThemeTokens.StatusErrorBrush;
        private static readonly QuadraticEase TileRevealEasing = new() { EasingMode = EasingMode.EaseOut };

        private static Brush CreateFrozenBrush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private ScrollViewer? _groupedScrollViewer;
        public ScrollViewer? GroupedScrollViewerControl => GroupedScrollViewer;
        private ScrollViewer? GroupedScrollViewer
        {
            get
            {
                if (_groupedScrollViewer != null) return _groupedScrollViewer;
                if (GroupedItemsControl == null) return null;
                GroupedItemsControl.ApplyTemplate();
                _groupedScrollViewer = FindVisualChild<ScrollViewer>(GroupedItemsControl);
                if (_groupedScrollViewer != null)
                {
                    AutoHideScrollBehavior.SetIsEnabled(_groupedScrollViewer, true);
                }
                return _groupedScrollViewer;
            }
        }

        private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
        {
            if (parent == null) return null;
            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild) return typedChild;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        private void OnRecentSuggestionsPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (GroupedScrollViewer is { } sv)
            {
                double newOffset = Math.Clamp(sv.VerticalOffset - e.Delta, 0, sv.ScrollableHeight);
                sv.ScrollToVerticalOffset(newOffset);
                e.Handled = true;
            }
        }
        public ScrollViewer? SearchResultsScrollViewerControl => SearchResultsScrollViewer;
        public ListBox? RecentSuggestionsListBoxControl => RecentSuggestionsListBox;
        public Grid? NoResultsPanelControl => NoResultsPanel;
        public const int MaxZeroStateSuggestions = 15;
        public const int MaxGridZeroStateSuggestions = 15;
        public const int MaxListZeroStateSuggestions = 6;

        public RecentItemsViewMode RecentViewMode { get; set; } = RecentItemsViewMode.Grid;

        public void InitializeSettings(AppSettings? settings)
        {
            if (settings != null)
            {
                RecentViewMode = settings.RecentViewMode;
                ApplyRecentViewMode(RecentViewMode);
            }
        }

        public void ApplyRecentViewMode(RecentItemsViewMode mode)
        {
            RecentViewMode = mode;
            if (RecentSuggestionsListBox == null) return;

            if (mode == RecentItemsViewMode.Grid)
            {
                if (Resources["RecentGridTileTemplate"] is DataTemplate gridTemplate)
                    RecentSuggestionsListBox.ItemTemplate = gridTemplate;
                if (Resources["RecentGridItemsPanelTemplate"] is ItemsPanelTemplate gridPanel)
                    RecentSuggestionsListBox.ItemsPanel = gridPanel;
                RecentSuggestionsListBox.Margin = Resources["DrawerGutterRecentGridMargin"] is Thickness gridMargin
                    ? gridMargin
                    : new Thickness(12, 0, 12, 0);
                RecentSuggestionsListBox.Padding = new Thickness(0);
            }
            else
            {
                if (Resources["SearchItemRowTemplate"] is DataTemplate listTemplate)
                    RecentSuggestionsListBox.ItemTemplate = listTemplate;
                if (Resources["RecentListItemsPanelTemplate"] is ItemsPanelTemplate listPanel)
                    RecentSuggestionsListBox.ItemsPanel = listPanel;
                RecentSuggestionsListBox.Margin = new Thickness(0);
                RecentSuggestionsListBox.Padding = new Thickness(0);
            }
        }

        public AllAppsDrawerControl()
        {
            InitializeComponent();
            Visibility = Visibility.Collapsed;
            DrawerTranslate.X = 0;

            try
            {
                var settings = StorageService.LoadSettings();
                if (settings != null)
                {
                    RecentViewMode = settings.RecentViewMode;
                }
            }
            catch { }

            _searchOrchestrator = new SearchOrchestrator(
                tier1Sources: new[] { new AppSearchSource(() => _allApps) },
                tier2Sources: new[] { new EverythingSearchSource() });
            _searchOrchestrator.SnapshotUpdated += OnSearchSnapshotUpdated;
            Unloaded += (s, e) => _searchOrchestrator.Dispose();
        }

        public void Open()
        {
            if (IsOpen && Visibility == Visibility.Visible) return;

            IsOpen = true;
            DrawerTranslate.X = 0;
            Visibility = Visibility.Visible;

            if (!_isAppsLoaded || _allApps.Count == 0)
            {
                LoadApps();
            }
            else
            {
                RefreshRecentSuggestions(MaxZeroStateSuggestions);
            }

            PlayMicroDrift();

            Opened?.Invoke(this, EventArgs.Empty);
            SearchBox.Focus();
            _ = GroupedScrollViewer;
        }

        public void Close()
        {
            if (!IsOpen && Visibility != Visibility.Visible) return;

            IsOpen = false;
            Closing?.Invoke(this, EventArgs.Empty);

            ResetMicroDrift();

            Visibility = Visibility.Collapsed;
            ClearSearch();
            Closed?.Invoke(this, EventArgs.Empty);
        }

        private void PlayMicroDrift()
        {
            // Animate the whole drawer as ONE unit — no per-element stagger.
            // Animating X-axis individual elements kills ClearType subpixel rendering and
            // makes text look blurry/jumpy. A unified 8px Y-lift on the UserControl itself
            // keeps all text pixel-snapped and crisp throughout.
            ResetMicroDrift();

            int refreshRate = NativeMethods.GetScreenRefreshRate();
            if (refreshRate <= 0) refreshRate = 120;

            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            const double DriftPx = 8.0;
            const double DriftMs = 140.0;

            // Y-lift: drawer slides up 8px from below, settles at 0
            var yAnim = new DoubleAnimation
            {
                From = DriftPx,
                To = 0.0,
                Duration = TimeSpan.FromMilliseconds(DriftMs),
                EasingFunction = easing
            };
            Timeline.SetDesiredFrameRate(yAnim, refreshRate);

            // Opacity: from 0.6 (already mostly visible) to 1.0 — no jarring pop-in
            var opAnim = new DoubleAnimation
            {
                From = 0.6,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(DriftMs),
                EasingFunction = easing
            };
            Timeline.SetDesiredFrameRate(opAnim, refreshRate);

            // Once settled, clear animation clocks to restore ClearType pixel-snapping
            yAnim.Completed += (s, e) => ResetMicroDrift();

            DrawerTranslate.BeginAnimation(TranslateTransform.YProperty, yAnim);
            BeginAnimation(UIElement.OpacityProperty, opAnim);
        }

        private void ResetMicroDrift()
        {
            // Clear animation clocks → WPF reverts to ClearType subpixel text rendering
            DrawerTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            DrawerTranslate.Y = 0.0;
            DrawerTranslate.X = 0.0;

            BeginAnimation(UIElement.OpacityProperty, null);
            Opacity = 1.0;
        }


        public void Toggle()
        {
            if (IsOpen)
            {
                Close();
            }
            else
            {
                Open();
            }
        }

        public void StartSearch(string initialText)
        {
            if (!IsOpen)
            {
                Open();
            }

            SearchBox.Text = initialText;
            SearchBox.CaretIndex = SearchBox.Text.Length;
            SearchBox.Focus();
        }

        public void AppendSearch(string text)
        {
            SearchBox.Focus();
            SearchBox.Text += text;
            SearchBox.CaretIndex = SearchBox.Text.Length;
        }

        public static List<DrawerItem> CreateFlattenedDrawerItems(IEnumerable<CatalogItemModel> apps)
        {
            var orderedApps = apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var groups = new Dictionary<string, List<CatalogItemModel>>(StringComparer.OrdinalIgnoreCase);

            foreach (var app in orderedApps)
            {
                if (string.IsNullOrWhiteSpace(app.Name)) continue;

                char first = app.Name.TrimStart()[0];
                string header = char.IsLetter(first) ? char.ToUpperInvariant(first).ToString() : "#";

                if (!groups.TryGetValue(header, out var list))
                {
                    list = [];
                    groups[header] = list;
                }
                list.Add(app);
            }

            var result = new List<DrawerItem>(orderedApps.Count + groups.Count);
            foreach (var group in groups.OrderBy(g => g.Key == "#" ? "!" : g.Key))
            {
                result.Add(new DrawerGroupHeader(group.Key));
                foreach (var app in group.Value)
                {
                    result.Add(new DrawerAppRow(app));
                }
            }

            return result;
        }

        public void LoadApps(List<CatalogItemModel>? preloaded = null)
        {
            try
            {
                var apps = preloaded ?? InstalledAppsService.GetInstalledApps(forceRefresh: false);
                _allApps = apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var flatItems = CreateFlattenedDrawerItems(_allApps);

                GroupedItemsControl.ItemsSource = flatItems;
                _isAppsLoaded = true;

                // Prewarm icons in memory background
                CatalogItemModel.PrewarmMemoryCache(_allApps);

                RefreshRecentSuggestions(MaxZeroStateSuggestions);
            }
            catch (Exception ex)
            {
                Safe.Log("AllAppsDrawer.LoadApps", ex);
            }
        }

        public void RefreshRecentSuggestions(int maxCount = -1)
        {
            try
            {
                ApplyRecentViewMode(RecentViewMode);

                int targetCount = (maxCount > 0 && maxCount != MaxZeroStateSuggestions)
                    ? maxCount
                    : (RecentViewMode == RecentItemsViewMode.Grid ? MaxGridZeroStateSuggestions : MaxListZeroStateSuggestions);

                var candidates = SearchOrchestrator.GetZeroStateSuggestions(_allApps, targetCount);
                if (candidates != null && candidates.Count > 0)
                {
                    // Prewarm icons for recent app candidates so they resolve in parallel before drawer slide-in finishes
                    var recentAppModels = candidates
                        .Select(c => c.Tag as CatalogItemModel)
                        .Where(a => a != null && !string.IsNullOrWhiteSpace(a.TargetPath))
                        .ToList();
                    if (recentAppModels.Count > 0)
                    {
                        CatalogItemModel.PrewarmMemoryCache(recentAppModels!, recentAppModels.Count);
                    }

                    var rows = new List<SearchItemRowViewModel>(candidates.Count);
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        rows.Add(new SearchItemRowViewModel(candidates[i]));
                    }

                    RecentSuggestionsListBox.ItemsSource = rows;
                    RecentSuggestionsPanel.Visibility = Visibility.Visible;
                }
                else
                {
                    RecentSuggestionsListBox.ItemsSource = null;
                    RecentSuggestionsPanel.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                Safe.Log("AllAppsDrawer.RefreshRecentSuggestions", ex);
                RecentSuggestionsListBox.ItemsSource = null;
                RecentSuggestionsPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            PlayRefreshAnimation();
            RefreshRequested?.Invoke(this, EventArgs.Empty);
        }

        private void PlayRefreshAnimation()
        {
            if (RefreshIconRotation != null)
            {
                var anim = new DoubleAnimation
                {
                    From = 0,
                    To = 360,
                    Duration = TimeSpan.FromMilliseconds(750)
                };
                RefreshIconRotation.BeginAnimation(RotateTransform.AngleProperty, anim);
            }
        }

        private void UpdatePlaceholderVisibility()
        {
            bool hasText = !string.IsNullOrWhiteSpace(SearchBox.Text);
            bool isFocused = SearchBox.IsFocused;
            SearchPlaceholder.Visibility = (hasText || isFocused) ? Visibility.Collapsed : Visibility.Visible;
            ClearSearchButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnSearchBoxGotFocus(object sender, RoutedEventArgs e)
        {
            SearchBorder.Background = SearchBorderFocusedBrush;
            UpdatePlaceholderVisibility();
        }

        private void OnSearchBoxLostFocus(object sender, RoutedEventArgs e)
        {
            SearchBorder.Background = SearchBorderUnfocusedBrush;
            UpdatePlaceholderVisibility();
        }

        private void OnSearchBoxTextChanged(object sender, TextChangedEventArgs e)
        {
            string query = SearchBox.Text.Trim();
            bool hasText = !string.IsNullOrEmpty(query);

            UpdatePlaceholderVisibility();

            if (!hasText)
            {
                _searchOrchestrator.SetQuery(string.Empty);
                GroupedViewContainer.Visibility = Visibility.Visible;
                SearchResultsScrollViewer.Visibility = Visibility.Collapsed;
                SearchResultsListBox.ItemsSource = null;
                if (NoResultsPanel != null) NoResultsPanel.Visibility = Visibility.Collapsed;
                RefreshRecentSuggestions(MaxZeroStateSuggestions);
            }
            else
            {
                GroupedViewContainer.Visibility = Visibility.Collapsed;
                SearchResultsScrollViewer.Visibility = Visibility.Visible;
                _searchOrchestrator.SetQuery(query);
            }
        }

        private void OnSearchSnapshotUpdated(SearchSnapshot snapshot)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, () =>
            {
                if (string.IsNullOrWhiteSpace(SearchBox.Text) || snapshot.IsEmpty)
                {
                    if (string.IsNullOrWhiteSpace(SearchBox.Text))
                    {
                        SearchResultsListBox.ItemsSource = null;
                        if (NoResultsPanel != null) NoResultsPanel.Visibility = Visibility.Collapsed;
                    }
                    else if (snapshot.IsFinal)
                    {
                        SearchResultsListBox.ItemsSource = null;
                        UpdateNoResultsText(SearchBox.Text);
                        if (NoResultsPanel != null) NoResultsPanel.Visibility = Visibility.Visible;
                    }
                    return;
                }

                if (snapshot.SessionId != 0 && snapshot.SessionId != _searchOrchestrator.CurrentSessionId)
                {
                    return;
                }

                var flatRows = new List<SearchItemRowViewModel>();
                foreach (var group in snapshot.Groups)
                {
                    bool isFirstInGroup = true;
                    foreach (var scoredResult in group.Items)
                    {
                        string header = isFirstInGroup ? group.Title : string.Empty;
                        isFirstInGroup = false;
                        flatRows.Add(new SearchItemRowViewModel(scoredResult.Candidate, header));
                    }
                }

                string? selectedId = (SearchResultsListBox.SelectedItem as SearchItemRowViewModel)?.Candidate.Id;

                SearchResultsListBox.ItemsSource = flatRows;
                bool showNoResults = flatRows.Count == 0 && snapshot.IsFinal;
                if (showNoResults)
                {
                    UpdateNoResultsText(SearchBox.Text);
                }
                if (NoResultsPanel != null)
                {
                    NoResultsPanel.Visibility = showNoResults ? Visibility.Visible : Visibility.Collapsed;
                }

                if (flatRows.Count > 0)
                {
                    int newIndex = -1;
                    if (!string.IsNullOrEmpty(selectedId))
                    {
                        newIndex = flatRows.FindIndex(r => r.Candidate.Id == selectedId);
                    }

                    SearchResultsListBox.SelectedIndex = newIndex >= 0 ? newIndex : 0;
                }
            });
        }

        private void UpdateNoResultsText(string? query)
        {
            if (NoResultsDetailTextBlock == null) return;

            string trimmed = query?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(trimmed))
            {
                NoResultsDetailTextBlock.Text = "Check your spelling or try another search term.";
            }
            else
            {
                string displayQuery = trimmed.Length > 24 ? $"{trimmed[..24]}..." : trimmed;
                NoResultsDetailTextBlock.Text = $"No matches found for \"{displayQuery}\". Check your spelling or try another keyword.";
            }
        }

        private void LaunchSearchItem(SearchItemRowViewModel row)
        {
            StorageService.RecordSearchLaunch(row.Candidate.Id);

            if (row.IsApp && row.AppModel != null)
            {
                AppLaunchRequested?.Invoke(this, row.AppModel);
                Close();
                ShellHideRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (!string.IsNullOrWhiteSpace(row.Candidate.FullPathOrKey))
            {
                try
                {
                    var psi = new ProcessStartInfo(row.Candidate.FullPathOrKey)
                    {
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                    Close();
                    ShellHideRequested?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    Safe.Log("AllAppsDrawer.LaunchSearchItem", ex);
                }
            }
        }

        private void OnSearchBoxPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down)
            {
                if (string.IsNullOrEmpty(SearchBox.Text))
                {
                    if (RecentSuggestionsListBox != null && RecentSuggestionsListBox.Items.Count > 0 && RecentSuggestionsPanel.Visibility == Visibility.Visible)
                    {
                        int targetIndex = RecentSuggestionsListBox.SelectedIndex;
                        if (targetIndex < 0)
                        {
                            targetIndex = 0;
                        }
                        else if (targetIndex < RecentSuggestionsListBox.Items.Count - 1)
                        {
                            targetIndex++;
                        }
                        else
                        {
                            GroupedScrollViewer?.LineDown();
                            e.Handled = true;
                            return;
                        }

                        FocusRecentSuggestionItem(targetIndex);
                        e.Handled = true;
                        return;
                    }
                    else
                    {
                        GroupedScrollViewer?.LineDown();
                        e.Handled = true;
                        return;
                    }
                }
                else if (SearchResultsListBox.Items.Count > 0)
                {
                    int targetIndex = SearchResultsListBox.SelectedIndex;
                    if (targetIndex < 0)
                    {
                        targetIndex = 0;
                    }
                    else if (targetIndex < SearchResultsListBox.Items.Count - 1)
                    {
                        targetIndex++;
                    }
                    else
                    {
                        targetIndex = 0;
                    }

                    FocusSearchItem(targetIndex);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Up)
            {
                if (string.IsNullOrEmpty(SearchBox.Text))
                {
                    if (RecentSuggestionsListBox != null && RecentSuggestionsListBox.Items.Count > 0 && RecentSuggestionsPanel.Visibility == Visibility.Visible)
                    {
                        int targetIndex = RecentSuggestionsListBox.SelectedIndex;
                        if (targetIndex <= 0)
                        {
                            targetIndex = RecentSuggestionsListBox.Items.Count - 1;
                        }
                        else
                        {
                            targetIndex--;
                        }

                        FocusRecentSuggestionItem(targetIndex);
                        e.Handled = true;
                        return;
                    }
                    else
                    {
                        GroupedScrollViewer?.LineUp();
                        e.Handled = true;
                        return;
                    }
                }
                else if (SearchResultsListBox.Items.Count > 0)
                {
                    int targetIndex = SearchResultsListBox.SelectedIndex;
                    if (targetIndex <= 0)
                    {
                        targetIndex = SearchResultsListBox.Items.Count - 1;
                    }
                    else
                    {
                        targetIndex--;
                    }

                    FocusSearchItem(targetIndex);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.PageDown)
            {
                if (string.IsNullOrEmpty(SearchBox.Text))
                {
                    GroupedScrollViewer?.PageDown();
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.PageUp)
            {
                if (string.IsNullOrEmpty(SearchBox.Text))
                {
                    GroupedScrollViewer?.PageUp();
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Enter)
            {
                if (string.IsNullOrEmpty(SearchBox.Text))
                {
                    if (RecentSuggestionsListBox != null && RecentSuggestionsListBox.SelectedItem is SearchItemRowViewModel recentRow)
                    {
                        LaunchSearchItem(recentRow);
                        e.Handled = true;
                        return;
                    }
                    else if (RecentSuggestionsListBox != null && RecentSuggestionsListBox.Items.Count > 0 && RecentSuggestionsPanel.Visibility == Visibility.Visible)
                    {
                        RecentSuggestionsListBox.SelectedIndex = 0;
                        if (RecentSuggestionsListBox.SelectedItem is SearchItemRowViewModel topRecent)
                        {
                            LaunchSearchItem(topRecent);
                            e.Handled = true;
                            return;
                        }
                    }
                    else if (_allApps.Count > 0)
                    {
                        AppLaunchRequested?.Invoke(this, _allApps[0]);
                        Close();
                        ShellHideRequested?.Invoke(this, EventArgs.Empty);
                        e.Handled = true;
                        return;
                    }
                }
                else if (SearchResultsListBox.SelectedItem is SearchItemRowViewModel row)
                {
                    LaunchSearchItem(row);
                    e.Handled = true;
                }
                else if (SearchResultsListBox.Items.Count > 0)
                {
                    SearchResultsListBox.SelectedIndex = 0;
                    if (SearchResultsListBox.SelectedItem is SearchItemRowViewModel topRow)
                    {
                        LaunchSearchItem(topRow);
                        e.Handled = true;
                    }
                }
            }
            else if (e.Key == Key.Right)
            {
                if (string.IsNullOrEmpty(SearchBox.Text) && RecentSuggestionsListBox != null && RecentSuggestionsListBox.Items.Count > 0)
                {
                    OpenContextMenuForRecentSuggestionItem();
                    e.Handled = true;
                }
                else if (SearchBox.CaretIndex == SearchBox.Text.Length && SearchResultsListBox.Items.Count > 0)
                {
                    OpenContextMenuForCurrentSearchItem();
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Back && string.IsNullOrEmpty(SearchBox.Text))
            {
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (!string.IsNullOrEmpty(SearchBox.Text))
                {
                    ClearSearch();
                    e.Handled = true;
                }
                else
                {
                    Close();
                    e.Handled = true;
                }
            }
        }

        private void FocusRecentSuggestionItem(int index)
        {
            if (RecentSuggestionsListBox != null && index >= 0 && index < RecentSuggestionsListBox.Items.Count)
            {
                RecentSuggestionsListBox.SelectedIndex = index;
                RecentSuggestionsListBox.ScrollIntoView(RecentSuggestionsListBox.SelectedItem);
                RecentSuggestionsListBox.UpdateLayout();

                if (RecentSuggestionsListBox.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem lbi)
                {
                    lbi.Focus();
                }
                else
                {
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                    {
                        var container = RecentSuggestionsListBox.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
                        container?.Focus();
                    }));
                }
            }
        }

        private void OnRecentSuggestionsKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (RecentSuggestionsListBox.SelectedItem is SearchItemRowViewModel row)
                {
                    LaunchSearchItem(row);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        }

        private void OnRecentSuggestionsPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (RecentViewMode == RecentItemsViewMode.List)
            {
                if (e.Key == Key.Right || e.Key == Key.Apps || (e.Key == Key.F10 && (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift))
                {
                    OpenContextMenuForRecentSuggestionItem();
                    e.Handled = true;
                }
                else if (e.Key == Key.Left)
                {
                    SearchBox.Focus();
                    SearchBox.CaretIndex = SearchBox.Text.Length;
                    e.Handled = true;
                }
                else if (e.Key == Key.Up && RecentSuggestionsListBox.SelectedIndex == 0)
                {
                    SearchBox.Focus();
                    SearchBox.CaretIndex = SearchBox.Text.Length;
                    e.Handled = true;
                }
                else if (e.Key == Key.Down && RecentSuggestionsListBox.SelectedIndex >= RecentSuggestionsListBox.Items.Count - 1)
                {
                    GroupedScrollViewer?.LineDown();
                    e.Handled = true;
                }
                else if (e.Key == Key.Back)
                {
                    SearchBox.Focus();
                    if (!string.IsNullOrEmpty(SearchBox.Text))
                    {
                        SearchBox.Text = SearchBox.Text[..^1];
                        SearchBox.CaretIndex = SearchBox.Text.Length;
                    }
                    e.Handled = true;
                }
            }
            else
            {
                if (e.Key == Key.Apps || (e.Key == Key.F10 && (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift))
                {
                    OpenContextMenuForRecentSuggestionItem();
                    e.Handled = true;
                }
                else if (e.Key == Key.Down && RecentSuggestionsListBox.SelectedIndex >= RecentSuggestionsListBox.Items.Count - 5)
                {
                    GroupedScrollViewer?.LineDown();
                    e.Handled = true;
                }
                else if (e.Key == Key.Up && RecentSuggestionsListBox.SelectedIndex >= 0 && RecentSuggestionsListBox.SelectedIndex < 5)
                {
                    SearchBox.Focus();
                    SearchBox.CaretIndex = SearchBox.Text.Length;
                    e.Handled = true;
                }
                else if (e.Key == Key.Left && RecentSuggestionsListBox.SelectedIndex == 0)
                {
                    SearchBox.Focus();
                    SearchBox.CaretIndex = SearchBox.Text.Length;
                    e.Handled = true;
                }
                else if (e.Key == Key.Back)
                {
                    SearchBox.Focus();
                    if (!string.IsNullOrEmpty(SearchBox.Text))
                    {
                        SearchBox.Text = SearchBox.Text[..^1];
                        SearchBox.CaretIndex = SearchBox.Text.Length;
                    }
                    e.Handled = true;
                }
            }
        }

        private void OnRecentSuggestionsPreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Text))
            {
                SearchBox.Focus();
                SearchBox.Text += e.Text;
                SearchBox.CaretIndex = SearchBox.Text.Length;
                e.Handled = true;
            }
        }

        private void FocusSearchItem(int index)
        {
            if (index >= 0 && index < SearchResultsListBox.Items.Count)
            {
                SearchResultsListBox.SelectedIndex = index;
                SearchResultsListBox.ScrollIntoView(SearchResultsListBox.SelectedItem);
                SearchResultsListBox.UpdateLayout();

                if (SearchResultsListBox.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem lbi)
                {
                    lbi.Focus();
                }
                else
                {
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                    {
                        var container = SearchResultsListBox.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
                        container?.Focus();
                    }));
                }
            }
        }

        private void OnSearchResultsKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (SearchResultsListBox.SelectedItem is SearchItemRowViewModel row)
                {
                    LaunchSearchItem(row);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Escape)
            {
                ClearSearch();
                SearchBox.Focus();
                e.Handled = true;
            }
        }

        private void OnSearchResultsPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Right || e.Key == Key.Apps || (e.Key == Key.F10 && (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift))
            {
                OpenContextMenuForCurrentSearchItem();
                e.Handled = true;
            }
            else if (e.Key == Key.Left)
            {
                SearchBox.Focus();
                SearchBox.CaretIndex = SearchBox.Text.Length;
                e.Handled = true;
            }
            else if (e.Key == Key.Up && SearchResultsListBox.SelectedIndex == 0)
            {
                // Smoothly return focus back to the search box when pressing Up from the top item
                SearchBox.Focus();
                SearchBox.CaretIndex = SearchBox.Text.Length;
                e.Handled = true;
            }
            else if (e.Key == Key.Back)
            {
                // Backspace while focused on results immediately moves focus back to search box
                SearchBox.Focus();
                if (!string.IsNullOrEmpty(SearchBox.Text))
                {
                    SearchBox.Text = SearchBox.Text[..^1];
                    SearchBox.CaretIndex = SearchBox.Text.Length;
                }
                e.Handled = true;
            }
        }

        private void OnSearchResultsPreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Text))
            {
                SearchBox.Focus();
                SearchBox.Text += e.Text;
                SearchBox.CaretIndex = SearchBox.Text.Length;
                e.Handled = true;
            }
        }

        private void OnClearSearchClick(object sender, RoutedEventArgs e)
        {
            ClearSearch();
        }

        private void ClearSearch()
        {
            SearchBox.Text = string.Empty;
            _searchOrchestrator?.SetQuery(string.Empty);
            UpdatePlaceholderVisibility();
            GroupedViewContainer.Visibility = Visibility.Visible;
            SearchResultsScrollViewer.Visibility = Visibility.Collapsed;
            SearchResultsListBox.ItemsSource = null;
            if (NoResultsPanel != null) NoResultsPanel.Visibility = Visibility.Collapsed;
            RefreshRecentSuggestions(MaxZeroStateSuggestions);
        }

        private void OnAppRowPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(this);
            _draggedItem = (sender as FrameworkElement)?.Tag as CatalogItemModel 
                           ?? ((sender as FrameworkElement)?.DataContext as DrawerAppRow)?.App
                           ?? (sender as FrameworkElement)?.DataContext as CatalogItemModel;
            _isAppDragPotential = _draggedItem != null;
        }

        private void OnAppRowPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isAppDragPotential && e.LeftButton == MouseButtonState.Pressed && _draggedItem != null)
            {
                Point current = e.GetPosition(this);
                if (Math.Abs(current.X - _dragStartPoint.X) > 6 || Math.Abs(current.Y - _dragStartPoint.Y) > 6)
                {
                    _isAppDragPotential = false;
                    var itemToDrag = _draggedItem;
                    _draggedItem = null;

                    var data = new DataObject(typeof(CatalogItemModel), itemToDrag);
                    DragDrop.DoDragDrop(sender as DependencyObject ?? this, data, DragDropEffects.Copy);
                }
            }
        }

        private void OnAppRowMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isAppDragPotential && _draggedItem != null)
            {
                // Single click to launch app
                StorageService.RecordSearchLaunch(_draggedItem.TargetPath ?? _draggedItem.Name);
                AppLaunchRequested?.Invoke(this, _draggedItem);
                _isAppDragPotential = false;
                _draggedItem = null;
            }
        }

        private void OnQuickPinClick(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var item = (sender as FrameworkElement)?.Tag as CatalogItemModel
                       ?? ((sender as FrameworkElement)?.DataContext as DrawerAppRow)?.App
                       ?? (sender as FrameworkElement)?.DataContext as CatalogItemModel;

            if (item != null)
            {
                AppPinRequested?.Invoke(this, item);
            }
        }

        private void OnCategoryHeaderPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Category headers are non-interactive section labels; prevent clicking from selecting or launching items
            e.Handled = true;
        }

        private void OnSearchRowPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(this);
            _draggedSearchRow = (sender as FrameworkElement)?.Tag as SearchItemRowViewModel
                             ?? (sender as FrameworkElement)?.DataContext as SearchItemRowViewModel;
            _isSearchRowDragPotential = _draggedSearchRow != null;
        }

        private void OnRecentGridTileMouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is not Border card) return;

            Point pos = e.GetPosition(card);
            UpdateTileRevealBrushes(card, pos);

            if (card.Child is Grid grid)
            {
                if (grid.Children.Count > 0 && grid.Children[0] is Border fillBorder)
                {
                    var anim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(120)) { EasingFunction = TileRevealEasing };
                    fillBorder.BeginAnimation(UIElement.OpacityProperty, anim);
                }
                if (grid.Children.Count > 2 && grid.Children[2] is Border edgeBorder)
                {
                    var anim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(120)) { EasingFunction = TileRevealEasing };
                    edgeBorder.BeginAnimation(UIElement.OpacityProperty, anim);
                }
            }

            if (card.RenderTransform is ScaleTransform scale)
            {
                if (scale.IsFrozen)
                {
                    scale = scale.Clone();
                    card.RenderTransform = scale;
                }
                var scaleAnim = new DoubleAnimation(1.03, TimeSpan.FromMilliseconds(120)) { EasingFunction = TileRevealEasing };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
            }
        }

        private void OnRecentGridTilePreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (sender is Border card)
            {
                Point pos = e.GetPosition(card);
                UpdateTileRevealBrushes(card, pos);
            }

            OnSearchRowPreviewMouseMove(sender, e);
        }

        private void OnRecentGridTileMouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is not Border card) return;

            if (card.Child is Grid grid)
            {
                if (grid.Children.Count > 0 && grid.Children[0] is Border fillBorder)
                {
                    var anim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(180)) { EasingFunction = TileRevealEasing };
                    fillBorder.BeginAnimation(UIElement.OpacityProperty, anim);
                }
                if (grid.Children.Count > 2 && grid.Children[2] is Border edgeBorder)
                {
                    var anim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(180)) { EasingFunction = TileRevealEasing };
                    edgeBorder.BeginAnimation(UIElement.OpacityProperty, anim);
                }
            }

            if (card.RenderTransform is ScaleTransform scale)
            {
                if (scale.IsFrozen)
                {
                    scale = scale.Clone();
                    card.RenderTransform = scale;
                }
                var scaleAnim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(180)) { EasingFunction = TileRevealEasing };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
            }
        }

        private static void UpdateTileRevealBrushes(Border card, Point pos)
        {
            if (card.Child is not Grid grid) return;

            if (grid.Children.Count > 0 && grid.Children[0] is Border fillBorder)
            {
                if (fillBorder.Background is RadialGradientBrush fillBrush)
                {
                    if (fillBrush.IsFrozen)
                    {
                        fillBrush = fillBrush.Clone();
                        fillBorder.Background = fillBrush;
                    }
                    fillBrush.Center = pos;
                    fillBrush.GradientOrigin = pos;
                }
            }

            if (grid.Children.Count > 2 && grid.Children[2] is Border edgeBorder)
            {
                if (edgeBorder.BorderBrush is RadialGradientBrush edgeBrush)
                {
                    if (edgeBrush.IsFrozen)
                    {
                        edgeBrush = edgeBrush.Clone();
                        edgeBorder.BorderBrush = edgeBrush;
                    }
                    edgeBrush.Center = pos;
                    edgeBrush.GradientOrigin = pos;
                }
            }
        }

        private void OnSearchRowPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isSearchRowDragPotential && e.LeftButton == MouseButtonState.Pressed && _draggedSearchRow != null)
            {
                Point current = e.GetPosition(this);
                if (Math.Abs(current.X - _dragStartPoint.X) > 6 || Math.Abs(current.Y - _dragStartPoint.Y) > 6)
                {
                    _isSearchRowDragPotential = false;
                    var itemToDrag = _draggedSearchRow;
                    _draggedSearchRow = null;

                    if (itemToDrag.IsApp && itemToDrag.AppModel != null)
                    {
                        var data = new DataObject(typeof(CatalogItemModel), itemToDrag.AppModel);
                        DragDrop.DoDragDrop(sender as DependencyObject ?? this, data, DragDropEffects.Copy);
                    }
                    else if (!string.IsNullOrWhiteSpace(itemToDrag.Candidate.FullPathOrKey))
                    {
                        var fileDropList = new System.Collections.Specialized.StringCollection { itemToDrag.Candidate.FullPathOrKey };
                        var data = new DataObject();
                        data.SetFileDropList(fileDropList);
                        DragDrop.DoDragDrop(sender as DependencyObject ?? this, data, DragDropEffects.Copy);
                    }
                }
            }
        }

        private void OnSearchRowMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isSearchRowDragPotential && _draggedSearchRow != null)
            {
                var row = _draggedSearchRow;
                _isSearchRowDragPotential = false;
                _draggedSearchRow = null;
                if (RecentSuggestionsListBoxControl != null && RecentSuggestionsListBoxControl.Items.Contains(row))
                {
                    RecentSuggestionsListBoxControl.SelectedItem = row;
                }
                else
                {
                    SearchResultsListBox.SelectedItem = row;
                }
                LaunchSearchItem(row);
            }
        }

        private void OnSearchRowContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var row = (sender as FrameworkElement)?.Tag as SearchItemRowViewModel
                   ?? (sender as FrameworkElement)?.DataContext as SearchItemRowViewModel
                   ?? (RecentSuggestionsListBoxControl != null ? RecentSuggestionsListBoxControl.SelectedItem as SearchItemRowViewModel : null)
                   ?? SearchResultsListBox.SelectedItem as SearchItemRowViewModel;

            if (row == null) return;
            if (RecentSuggestionsListBoxControl != null && RecentSuggestionsListBoxControl.Items.Contains(row))
            {
                RecentSuggestionsListBoxControl.SelectedItem = row;
            }
            else
            {
                SearchResultsListBox.SelectedItem = row;
            }

            var target = sender as UIElement
                         ?? (RecentSuggestionsListBoxControl != null && RecentSuggestionsListBoxControl.Items.Contains(row)
                             ? (UIElement)RecentSuggestionsListBoxControl
                             : (UIElement)SearchResultsListBox);

            OpenContextMenuForRow(row, target);
            e.Handled = true;
        }

        private void OnFileContextMenuOpenClick(object sender, RoutedEventArgs e)
        {
            var row = _activeSearchFileItem ?? (sender as FrameworkElement)?.DataContext as SearchItemRowViewModel;
            if (row != null)
            {
                LaunchSearchItem(row);
            }
        }

        private void OnFileContextMenuOpenLocationClick(object sender, RoutedEventArgs e)
        {
            var row = _activeSearchFileItem ?? (sender as FrameworkElement)?.DataContext as SearchItemRowViewModel;
            if (row != null && !string.IsNullOrWhiteSpace(row.Candidate.FullPathOrKey))
            {
                try
                {
                    string targetPath = row.Candidate.FullPathOrKey;
                    if (File.Exists(targetPath))
                    {
                        Process.Start("explorer.exe", $"/select,\"{targetPath}\"");
                    }
                    else if (Directory.Exists(targetPath))
                    {
                        Process.Start("explorer.exe", $"\"{targetPath}\"");
                    }
                    Close();
                    ShellHideRequested?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    Safe.Log("AllAppsDrawer.OpenLocation", ex);
                }
            }
        }

        private void OnFileContextMenuCopyPathClick(object sender, RoutedEventArgs e)
        {
            var row = _activeSearchFileItem ?? (sender as FrameworkElement)?.DataContext as SearchItemRowViewModel;
            if (row != null && !string.IsNullOrWhiteSpace(row.Candidate.FullPathOrKey))
            {
                try
                {
                    Clipboard.SetText(row.Candidate.FullPathOrKey);
                }
                catch (Exception ex)
                {
                    Safe.Log("AllAppsDrawer.CopyPath", ex);
                }
            }
        }

        private void OnFileContextMenuRunAsAdminClick(object sender, RoutedEventArgs e)
        {
            var row = _activeSearchFileItem ?? (sender as FrameworkElement)?.DataContext as SearchItemRowViewModel;
            if (row != null && !string.IsNullOrWhiteSpace(row.Candidate.FullPathOrKey))
            {
                try
                {
                    ProcessLauncherService.LaunchTargetAsync(row.Candidate.FullPathOrKey, null, runAsAdmin: true, displayName: row.Candidate.DisplayName);
                    Close();
                    ShellHideRequested?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    Safe.Log("AllAppsDrawer.SearchFileRunAsAdmin", ex);
                }
            }
        }

        private void OnFileContextMenuOpenTerminalClick(object sender, RoutedEventArgs e)
        {
            var row = _activeSearchFileItem ?? (sender as FrameworkElement)?.DataContext as SearchItemRowViewModel;
            if (row != null && !string.IsNullOrWhiteSpace(row.Candidate.FullPathOrKey))
            {
                string folder = row.Candidate.FullPathOrKey;
                if (Directory.Exists(folder))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo("wt.exe") { WorkingDirectory = folder, UseShellExecute = true });
                    }
                    catch
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo("powershell.exe", "-NoExit") { WorkingDirectory = folder, UseShellExecute = true });
                        }
                        catch
                        {
                            Process.Start(new ProcessStartInfo("cmd.exe", "/K") { WorkingDirectory = folder, UseShellExecute = true });
                        }
                    }
                }
            }
        }

        private void OnFileContextMenuPinToMetroHubClick(object sender, RoutedEventArgs e)
        {
            var row = _activeSearchFileItem ?? (sender as FrameworkElement)?.DataContext as SearchItemRowViewModel;
            if (row != null && !string.IsNullOrWhiteSpace(row.Candidate.FullPathOrKey))
            {
                string path = row.Candidate.FullPathOrKey;
                bool isPinned = IsFilePinnedPredicate?.Invoke(path) ?? false;
                if (isPinned)
                {
                    FileUnpinRequested?.Invoke(this, path);
                }
                else
                {
                    FilePinRequested?.Invoke(this, path);
                }
            }
        }

        private void OnFileContextMenuPinToSidebarClick(object sender, RoutedEventArgs e)
        {
            var row = _activeSearchFileItem ?? (sender as FrameworkElement)?.DataContext as SearchItemRowViewModel;
            if (row != null && !string.IsNullOrWhiteSpace(row.Candidate.FullPathOrKey))
            {
                var rail = CanvasMessenger.GetSidebarRail();
                if (rail != null)
                {
                    string path = row.Candidate.FullPathOrKey;
                    if (SidebarPinningService.IsPathPinnedToSidebar(rail, path))
                    {
                        SidebarPinningService.UnpinPathFromSidebar(rail, path);
                    }
                    else
                    {
                        bool isFolder = row.IsFolder;
                        string title = row.Candidate.DisplayName;
                        string smartIcon = isFolder
                            ? SidebarShortcutItem.DetectSmartIcon(path)
                            : SidebarPinningService.GetDefaultFileSymbol(Path.GetExtension(path).ToLowerInvariant());
                        string? iconPath = null;
                        if (!isFolder)
                        {
                            try
                            {
                                iconPath = IconExtractorService.ExtractAndCacheIcon(path);
                            }
                            catch { }
                        }

                        var shortcut = new SidebarShortcutItem
                        {
                            Title = title,
                            Target = path,
                            TargetType = isFolder ? SidebarShortcutType.CustomFolder : SidebarShortcutType.Application,
                            IconSymbol = smartIcon,
                            CustomIconPath = iconPath,
                            SortOrder = rail.Shortcuts.Count,
                            IsRemovable = true
                        };
                        rail.AddShortcutItem(shortcut);
                    }
                }
            }
        }

        private void OpenContextMenuForRow(SearchItemRowViewModel row, UIElement target)
        {
            if (row == null) return;

            if (row.IsApp && row.AppModel != null)
            {
                if (Resources["AppItemContextMenu"] is ContextMenu contextMenu)
                {
                    _activeContextMenuItem = row.AppModel;
                    contextMenu.PlacementTarget = target;
                    contextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Right;
                    contextMenu.HorizontalOffset = 4;
                    contextMenu.VerticalOffset = 0;
                    contextMenu.DataContext = row.AppModel;

                    UpdatePinMenuItemState(contextMenu, row.AppModel);
                    contextMenu.IsOpen = true;

                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
                    {
                        if (contextMenu.Items.Count > 0 && contextMenu.Items[0] is MenuItem firstItem)
                        {
                            firstItem.Focus();
                        }
                    }));
                }
            }
            else
            {
                if (Resources["FileItemContextMenu"] is ContextMenu fileMenu)
                {
                    _activeSearchFileItem = row;
                    fileMenu.PlacementTarget = target;
                    fileMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Right;
                    fileMenu.HorizontalOffset = 4;
                    fileMenu.VerticalOffset = 0;
                    fileMenu.DataContext = row;
                    UpdateFilePinMenuItemState(fileMenu, row);
                    fileMenu.IsOpen = true;

                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
                    {
                        if (fileMenu.Items.Count > 0 && fileMenu.Items[0] is MenuItem firstItem)
                        {
                            firstItem.Focus();
                        }
                    }));
                }
            }
        }

        private void OpenContextMenuForCurrentSearchItem()
        {
            if (SearchResultsListBox.Items.Count == 0) return;

            int selIdx = SearchResultsListBox.SelectedIndex;
            if (selIdx < 0)
            {
                selIdx = 0;
                SearchResultsListBox.SelectedIndex = 0;
            }

            if (SearchResultsListBox.SelectedItem is not SearchItemRowViewModel row) return;

            var container = SearchResultsListBox.ItemContainerGenerator.ContainerFromIndex(selIdx) as ListBoxItem;
            if (container == null)
            {
                SearchResultsListBox.ScrollIntoView(row);
                SearchResultsListBox.UpdateLayout();
                container = SearchResultsListBox.ItemContainerGenerator.ContainerFromIndex(selIdx) as ListBoxItem;
            }

            OpenContextMenuForRow(row, container ?? (UIElement)SearchResultsListBox);
        }

        private void OpenContextMenuForRecentSuggestionItem()
        {
            if (RecentSuggestionsListBoxControl == null || RecentSuggestionsListBoxControl.Items.Count == 0) return;

            int selIdx = RecentSuggestionsListBoxControl.SelectedIndex;
            if (selIdx < 0)
            {
                selIdx = 0;
                RecentSuggestionsListBoxControl.SelectedIndex = 0;
            }

            if (RecentSuggestionsListBoxControl.SelectedItem is not SearchItemRowViewModel row) return;

            var container = RecentSuggestionsListBoxControl.ItemContainerGenerator.ContainerFromIndex(selIdx) as ListBoxItem;
            if (container == null)
            {
                RecentSuggestionsListBoxControl.ScrollIntoView(row);
                RecentSuggestionsListBoxControl.UpdateLayout();
                container = RecentSuggestionsListBoxControl.ItemContainerGenerator.ContainerFromIndex(selIdx) as ListBoxItem;
            }

            OpenContextMenuForRow(row, container ?? (UIElement)RecentSuggestionsListBoxControl);
        }

        private void OnAppContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (Resources["AppItemContextMenu"] is ContextMenu menu)
            {
                var item = (sender as FrameworkElement)?.Tag as CatalogItemModel
                           ?? ((sender as FrameworkElement)?.DataContext as DrawerAppRow)?.App
                           ?? (sender as FrameworkElement)?.DataContext as CatalogItemModel
                           ?? (SearchResultsListBox.SelectedItem as SearchItemRowViewModel)?.AppModel;
                if (item != null)
                {
                    _activeContextMenuItem = item;
                    menu.DataContext = item;
                    UpdatePinMenuItemState(menu, item);
                }
            }
        }

        private void OnAppContextMenuOpened(object sender, RoutedEventArgs e)
        {
            if (sender is ContextMenu menu)
            {
                var item = GetCatalogItemFromMenu(menu) ?? _activeContextMenuItem;
                if (item != null)
                {
                    _activeContextMenuItem = item;
                    UpdatePinMenuItemState(menu, item);
                }
            }
        }

        private void OnFileContextMenuOpened(object sender, RoutedEventArgs e)
        {
            if (sender is ContextMenu menu)
            {
                var row = _activeSearchFileItem ?? menu.DataContext as SearchItemRowViewModel;
                if (row != null)
                {
                    _activeSearchFileItem = row;
                    UpdateFilePinMenuItemState(menu, row);
                }
            }
        }

        private void OnAppContextMenuPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Left)
            {
                if (sender is ContextMenu menu)
                {
                    menu.IsOpen = false;
                    e.Handled = true;

                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
                    {
                        if (SearchResultsListBox.SelectedItem != null && SearchResultsListBox.SelectedIndex >= 0)
                        {
                            FocusSearchItem(SearchResultsListBox.SelectedIndex);
                        }
                        else
                        {
                            SearchBox.Focus();
                            SearchBox.CaretIndex = SearchBox.Text.Length;
                        }
                    }));
                }
            }
            else if (e.Key == Key.Escape)
            {
                if (sender is ContextMenu menu)
                {
                    menu.IsOpen = false;
                    e.Handled = true;

                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
                    {
                        if (SearchResultsListBox.SelectedItem != null && SearchResultsListBox.SelectedIndex >= 0)
                        {
                            FocusSearchItem(SearchResultsListBox.SelectedIndex);
                        }
                        else
                        {
                            SearchBox.Focus();
                        }
                    }));
                }
            }
        }

        private static MenuItem? FindMenuItemByName(ContextMenu menu, string name)
        {
            foreach (var item in menu.Items)
            {
                if (item is MenuItem mi && (mi.Name == name || mi.Header?.ToString()?.Equals(name, StringComparison.OrdinalIgnoreCase) == true))
                {
                    return mi;
                }
            }
            return null;
        }

        private static MenuItem? FindPinMenuItem(ContextMenu menu)
        {
            return FindMenuItemByName(menu, "PinMenuItem");
        }

        private void UpdatePinMenuItemState(ContextMenu menu, CatalogItemModel item)
        {
            var pinItem = FindPinMenuItem(menu);
            if (pinItem != null)
            {
                bool isPinned = IsAppPinned(item);
                if (isPinned)
                {
                    pinItem.Header = "Unpin from MetroHub";
                    if (pinItem.Icon is Wpf.Ui.Controls.SymbolIcon sym)
                    {
                        sym.Symbol = Wpf.Ui.Controls.SymbolRegular.PinOff24;
                        sym.Foreground = UnpinRedBrush;
                    }
                    pinItem.Foreground = UnpinRedBrush;
                }
                else
                {
                    pinItem.Header = "Pin to MetroHub";
                    if (pinItem.Icon is Wpf.Ui.Controls.SymbolIcon sym)
                    {
                        sym.Symbol = Wpf.Ui.Controls.SymbolRegular.Pin24;
                        sym.ClearValue(Wpf.Ui.Controls.SymbolIcon.ForegroundProperty);
                    }
                    pinItem.ClearValue(MenuItem.ForegroundProperty);
                }
            }

            var pinSidebarItem = FindMenuItemByName(menu, "PinSidebarMenuItem");
            if (pinSidebarItem != null)
            {
                var rail = CanvasMessenger.GetSidebarRail();
                bool isPinnedSidebar = SidebarPinningService.IsCatalogItemPinnedToSidebar(rail, item);
                if (isPinnedSidebar)
                {
                    pinSidebarItem.Header = "Unpin from Sidebar";
                    if (pinSidebarItem.Icon is Wpf.Ui.Controls.SymbolIcon sym)
                    {
                        sym.Symbol = Wpf.Ui.Controls.SymbolRegular.PinOff24;
                        sym.Foreground = UnpinRedBrush;
                    }
                    pinSidebarItem.Foreground = UnpinRedBrush;
                }
                else
                {
                    pinSidebarItem.Header = "Pin to Sidebar";
                    if (pinSidebarItem.Icon is Wpf.Ui.Controls.SymbolIcon sym)
                    {
                        sym.Symbol = Wpf.Ui.Controls.SymbolRegular.Pin24;
                        sym.ClearValue(Wpf.Ui.Controls.SymbolIcon.ForegroundProperty);
                    }
                    pinSidebarItem.ClearValue(MenuItem.ForegroundProperty);
                }
            }
        }

        private void UpdateFilePinMenuItemState(ContextMenu menu, SearchItemRowViewModel row)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.Candidate.FullPathOrKey)) return;

            string path = row.Candidate.FullPathOrKey;
            var pinItem = FindMenuItemByName(menu, "FilePinMenuItem");
            if (pinItem != null)
            {
                bool isPinned = IsFilePinnedPredicate?.Invoke(path) ?? false;
                if (isPinned)
                {
                    pinItem.Header = "Unpin from MetroHub";
                    if (pinItem.Icon is Wpf.Ui.Controls.SymbolIcon sym)
                    {
                        sym.Symbol = Wpf.Ui.Controls.SymbolRegular.PinOff24;
                        sym.Foreground = UnpinRedBrush;
                    }
                    pinItem.Foreground = UnpinRedBrush;
                }
                else
                {
                    pinItem.Header = "Pin to MetroHub";
                    if (pinItem.Icon is Wpf.Ui.Controls.SymbolIcon sym)
                    {
                        sym.Symbol = Wpf.Ui.Controls.SymbolRegular.Pin24;
                        sym.ClearValue(Wpf.Ui.Controls.SymbolIcon.ForegroundProperty);
                    }
                    pinItem.ClearValue(MenuItem.ForegroundProperty);
                }
            }

            var pinSidebarItem = FindMenuItemByName(menu, "FilePinSidebarMenuItem");
            if (pinSidebarItem != null)
            {
                var rail = CanvasMessenger.GetSidebarRail();
                bool isPinnedSidebar = SidebarPinningService.IsPathPinnedToSidebar(rail, path);
                if (isPinnedSidebar)
                {
                    pinSidebarItem.Header = "Unpin from Sidebar";
                    if (pinSidebarItem.Icon is Wpf.Ui.Controls.SymbolIcon sym)
                    {
                        sym.Symbol = Wpf.Ui.Controls.SymbolRegular.PinOff24;
                        sym.Foreground = UnpinRedBrush;
                    }
                    pinSidebarItem.Foreground = UnpinRedBrush;
                }
                else
                {
                    pinSidebarItem.Header = "Pin to Sidebar";
                    if (pinSidebarItem.Icon is Wpf.Ui.Controls.SymbolIcon sym)
                    {
                        sym.Symbol = Wpf.Ui.Controls.SymbolRegular.Pin24;
                        sym.ClearValue(Wpf.Ui.Controls.SymbolIcon.ForegroundProperty);
                    }
                    pinSidebarItem.ClearValue(MenuItem.ForegroundProperty);
                }
            }
        }

        private bool IsAppPinned(CatalogItemModel? item)
        {
            if (item == null) return false;
            return IsAppPinnedPredicate?.Invoke(item) ?? false;
        }

        private CatalogItemModel? GetCatalogItemFromMenu(object? sender)
        {
            if (_activeContextMenuItem != null) return _activeContextMenuItem;

            if (sender is MenuItem menuItem)
            {
                if (menuItem.DataContext is CatalogItemModel directItem) return directItem;
                if (ItemsControl.ItemsControlFromItemContainer(menuItem) is ContextMenu icMenu && icMenu.DataContext is CatalogItemModel icItem) return icItem;
                if (menuItem.Parent is ContextMenu parentMenu && parentMenu.DataContext is CatalogItemModel pmItem) return pmItem;
            }
            else if (sender is ContextMenu menu)
            {
                if (menu.DataContext is CatalogItemModel cmItem) return cmItem;
                if (menu.PlacementTarget is FrameworkElement targetFe)
                {
                    if (targetFe.Tag is CatalogItemModel tagItem) return tagItem;
                    if (targetFe.DataContext is CatalogItemModel dcItem) return dcItem;
                }
            }
            return (RecentSuggestionsListBoxControl?.SelectedItem as SearchItemRowViewModel)?.AppModel
                   ?? (SearchResultsListBox.SelectedItem as SearchItemRowViewModel)?.AppModel;
        }

        private void OnContextMenuOpenClick(object sender, RoutedEventArgs e)
        {
            var item = _activeContextMenuItem ?? GetCatalogItemFromMenu(sender);
            if (item != null)
            {
                StorageService.RecordSearchLaunch(item.TargetPath ?? item.Name);
                AppLaunchRequested?.Invoke(this, item);
                Close();
                ShellHideRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        private void OnContextMenuRunAsAdminClick(object sender, RoutedEventArgs e)
        {
            var item = _activeContextMenuItem ?? GetCatalogItemFromMenu(sender);
            if (item == null || string.IsNullOrWhiteSpace(item.TargetPath)) return;

            try
            {
                StorageService.RecordSearchLaunch(item.TargetPath ?? item.Name);
                string rawPath = item.TargetPath;
                string execPath = IconExtractorService.ResolveExecutableTarget(rawPath);

                if (rawPath.Contains("WindowsTerminal", StringComparison.OrdinalIgnoreCase) ||
                    item.Name.Contains("Terminal", StringComparison.OrdinalIgnoreCase))
                {
                    execPath = "wt.exe";
                }
                else if (rawPath.Contains("WindowsNotepad", StringComparison.OrdinalIgnoreCase))
                {
                    execPath = "notepad.exe";
                }
                else if (rawPath.Contains("Microsoft.Paint", StringComparison.OrdinalIgnoreCase))
                {
                    execPath = "mspaint.exe";
                }

                ProcessLauncherService.LaunchTargetAsync(execPath, item.Arguments, runAsAdmin: true, displayName: item.Name);

                // Close drawer and hide MetroHub on successful launch so elevated prompt and window come forward
                Close();
                ShellHideRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Safe.Log("AllAppsDrawer.RunAsAdmin", ex);
            }
        }

        private void OnContextMenuPinClick(object sender, RoutedEventArgs e)
        {
            OnContextMenuPinToggleClick(sender, e);
        }

        private void OnContextMenuPinToggleClick(object sender, RoutedEventArgs e)
        {
            var item = _activeContextMenuItem ?? GetCatalogItemFromMenu(sender);
            if (item == null) return;

            if (IsAppPinned(item))
            {
                AppUnpinRequested?.Invoke(this, item);
            }
            else
            {
                AppPinRequested?.Invoke(this, item);
            }
        }

        private void OnContextMenuPinToSidebarClick(object sender, RoutedEventArgs e)
        {
            var item = _activeContextMenuItem ?? GetCatalogItemFromMenu(sender);
            if (item == null) return;

            var rail = CanvasMessenger.GetSidebarRail();
            if (rail != null)
            {
                if (SidebarPinningService.IsCatalogItemPinnedToSidebar(rail, item))
                {
                    SidebarPinningService.UnpinCatalogItemFromSidebar(rail, item);
                }
                else
                {
                    SidebarPinningService.PinCatalogItem(rail, item);
                }
            }
        }

        private void OnContextMenuUninstallClick(object sender, RoutedEventArgs e)
        {
            var item = _activeContextMenuItem ?? GetCatalogItemFromMenu(sender);
            if (item != null)
            {
                AppUninstallService.RequestUninstall(item);
            }
        }
    }
}
