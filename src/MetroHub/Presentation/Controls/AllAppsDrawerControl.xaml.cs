using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MetroHub.Core.Models;
using MetroHub.Core.Services;

namespace MetroHub.Presentation.Controls
{
    public class AlphabeticalAppGroup
    {
        public string Header { get; set; } = string.Empty;
        public List<CatalogItemModel> Items { get; set; } = new();
    }

    public partial class AllAppsDrawerControl : UserControl
    {
        public event EventHandler<CatalogItemModel>? AppPinRequested;
        public event EventHandler<CatalogItemModel>? AppLaunchRequested;
        public event EventHandler? Opened;
        public event EventHandler? Closing;
        public event EventHandler? Closed;

        public bool IsOpen { get; private set; } = false;

        private List<CatalogItemModel> _allApps = new();
        private Point _dragStartPoint;
        private CatalogItemModel? _draggedItem;
        private bool _isAppDragPotential = false;
        private bool _isAppsLoaded = false;
        private CatalogItemModel? _activeContextMenuItem;

        public AllAppsDrawerControl()
        {
            InitializeComponent();
            Visibility = Visibility.Collapsed;
        }

        public void Open()
        {
            if (IsOpen && Visibility == Visibility.Visible) return;

            IsOpen = true;
            Visibility = Visibility.Visible;
            Opened?.Invoke(this, EventArgs.Empty);

            if (!_isAppsLoaded || _allApps.Count == 0)
            {
                LoadApps();
            }

            // Animate smooth slide in
            var anim = new DoubleAnimation
            {
                From = DrawerTranslate.X,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            DrawerTranslate.BeginAnimation(TranslateTransform.XProperty, anim);

            // Focus search box smoothly when opening
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
            {
                SearchBox.Focus();
            }));
        }

        public void Close()
        {
            if (!IsOpen && Visibility != Visibility.Visible) return;

            IsOpen = false;
            Closing?.Invoke(this, EventArgs.Empty);
            Closed?.Invoke(this, EventArgs.Empty);

            // Animate smooth slide out
            var anim = new DoubleAnimation
            {
                From = DrawerTranslate.X,
                To = -320,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };

            anim.Completed += (s, e) =>
            {
                if (!IsOpen)
                {
                    Visibility = Visibility.Collapsed;
                    ClearSearch();
                }
            };
            DrawerTranslate.BeginAnimation(TranslateTransform.XProperty, anim);
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

        public static List<AlphabeticalAppGroup> CreateAlphabeticalGroups(IEnumerable<CatalogItemModel> apps)
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
                    list = new List<CatalogItemModel>();
                    groups[header] = list;
                }
                list.Add(app);
            }

            return groups
                .OrderBy(g => g.Key == "#" ? "!" : g.Key)
                .Select(g => new AlphabeticalAppGroup
                {
                    Header = g.Key,
                    Items = g.Value
                })
                .ToList();
        }

        public void SetPreloadedApps(List<CatalogItemModel> apps, List<AlphabeticalAppGroup> groupedApps)
        {
            _allApps = apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
            GroupedItemsControl.ItemsSource = groupedApps;
            _isAppsLoaded = true;
        }

        public void LoadApps(List<CatalogItemModel>? preloaded = null)
        {
            try
            {
                var apps = preloaded ?? InstalledAppsService.GetInstalledApps(forceRefresh: false);
                _allApps = apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var orderedGroups = CreateAlphabeticalGroups(_allApps);

                GroupedItemsControl.ItemsSource = orderedGroups;
                _isAppsLoaded = true;

                // Prewarm icons in memory background
                CatalogItemModel.PrewarmMemoryCache(_allApps);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AllAppsDrawer] Error loading apps: {ex.Message}");
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
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
            SearchBorder.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            UpdatePlaceholderVisibility();
        }

        private void OnSearchBoxLostFocus(object sender, RoutedEventArgs e)
        {
            SearchBorder.Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            UpdatePlaceholderVisibility();
        }

        private void OnSearchBoxTextChanged(object sender, TextChangedEventArgs e)
        {
            string query = SearchBox.Text.Trim();
            bool hasText = !string.IsNullOrEmpty(query);

            UpdatePlaceholderVisibility();

            if (!hasText)
            {
                GroupedScrollViewer.Visibility = Visibility.Visible;
                SearchResultsScrollViewer.Visibility = Visibility.Collapsed;
                SearchResultsListBox.ItemsSource = null;
            }
            else
            {
                var matches = _allApps
                    .Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                SearchResultsListBox.ItemsSource = matches;
                NoResultsTextBlock.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                GroupedScrollViewer.Visibility = Visibility.Collapsed;
                SearchResultsScrollViewer.Visibility = Visibility.Visible;
                SearchResultsScrollViewer.ScrollToTop();

                if (matches.Count > 0)
                {
                    SearchResultsListBox.SelectedIndex = 0;
                }
            }
        }

        private void OnSearchBoxPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down)
            {
                if (SearchResultsListBox.Items.Count > 0)
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
                if (SearchResultsListBox.Items.Count > 0)
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
            else if (e.Key == Key.Enter)
            {
                if (SearchResultsListBox.SelectedItem is CatalogItemModel app)
                {
                    AppLaunchRequested?.Invoke(this, app);
                    e.Handled = true;
                }
                else if (SearchResultsListBox.Items.Count > 0)
                {
                    SearchResultsListBox.SelectedIndex = 0;
                    if (SearchResultsListBox.SelectedItem is CatalogItemModel topApp)
                    {
                        AppLaunchRequested?.Invoke(this, topApp);
                        e.Handled = true;
                    }
                }
            }
            else if (e.Key == Key.Right)
            {
                if (SearchBox.CaretIndex == SearchBox.Text.Length && SearchResultsListBox.Items.Count > 0)
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
                if (SearchResultsListBox.SelectedItem is CatalogItemModel app)
                {
                    AppLaunchRequested?.Invoke(this, app);
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
            UpdatePlaceholderVisibility();
            GroupedScrollViewer.Visibility = Visibility.Visible;
            SearchResultsScrollViewer.Visibility = Visibility.Collapsed;
            SearchResultsListBox.ItemsSource = null;
        }

        private void OnAppRowPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(this);
            _draggedItem = (sender as FrameworkElement)?.Tag as CatalogItemModel 
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
                AppLaunchRequested?.Invoke(this, _draggedItem);
                _isAppDragPotential = false;
                _draggedItem = null;
            }
        }

        private void OnQuickPinClick(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var item = (sender as FrameworkElement)?.Tag as CatalogItemModel
                       ?? (sender as FrameworkElement)?.DataContext as CatalogItemModel;

            if (item != null)
            {
                AppPinRequested?.Invoke(this, item);
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

            if (SearchResultsListBox.SelectedItem is not CatalogItemModel item) return;

            var container = SearchResultsListBox.ItemContainerGenerator.ContainerFromIndex(selIdx) as ListBoxItem;
            if (container == null)
            {
                SearchResultsListBox.ScrollIntoView(item);
                SearchResultsListBox.UpdateLayout();
                container = SearchResultsListBox.ItemContainerGenerator.ContainerFromIndex(selIdx) as ListBoxItem;
            }

            if (Resources["AppItemContextMenu"] is ContextMenu contextMenu)
            {
                _activeContextMenuItem = item;
                contextMenu.PlacementTarget = container ?? (UIElement)SearchResultsListBox;
                contextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Right;
                contextMenu.HorizontalOffset = 4;
                contextMenu.VerticalOffset = 0;
                contextMenu.DataContext = item;

                UpdatePinMenuItemState(contextMenu, item);

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

        private void OnAppContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (Resources["AppItemContextMenu"] is ContextMenu menu)
            {
                var item = (sender as FrameworkElement)?.Tag as CatalogItemModel
                           ?? (sender as FrameworkElement)?.DataContext as CatalogItemModel
                           ?? SearchResultsListBox.SelectedItem as CatalogItemModel;
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

        private static MenuItem? FindPinMenuItem(ContextMenu menu)
        {
            foreach (var item in menu.Items)
            {
                if (item is MenuItem mi && (mi.Name == "PinMenuItem" || mi.Header?.ToString()?.Contains("Pin") == true))
                {
                    return mi;
                }
            }
            return null;
        }

        private void UpdatePinMenuItemState(ContextMenu menu, CatalogItemModel item)
        {
            var pinItem = FindPinMenuItem(menu);
            if (pinItem == null) return;

            bool isPinned = IsAppPinned(item, out _);
            if (isPinned)
            {
                pinItem.Header = "Unpin from Start";
                if (pinItem.Icon is Wpf.Ui.Controls.SymbolIcon sym)
                {
                    sym.Symbol = Wpf.Ui.Controls.SymbolRegular.Pin24;
                    sym.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
                }
                pinItem.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
            }
            else
            {
                pinItem.Header = "Pin to Start";
                if (pinItem.Icon is Wpf.Ui.Controls.SymbolIcon sym)
                {
                    sym.Symbol = Wpf.Ui.Controls.SymbolRegular.Pin24;
                    sym.ClearValue(Wpf.Ui.Controls.SymbolIcon.ForegroundProperty);
                }
                pinItem.ClearValue(MenuItem.ForegroundProperty);
            }
        }

        private static bool IsAppPinned(CatalogItemModel item, out List<TileModel> matchedTiles)
        {
            matchedTiles = new List<TileModel>();
            var current = MainWindow.Current;
            if (current == null || item == null) return false;

            foreach (var tile in current.Tiles)
            {
                bool targetMatch = !string.IsNullOrEmpty(item.TargetPath) &&
                    string.Equals(tile.TargetPath, item.TargetPath, StringComparison.OrdinalIgnoreCase);
                bool nameMatch = !string.IsNullOrEmpty(item.Name) &&
                    string.Equals(tile.Title, item.Name, StringComparison.OrdinalIgnoreCase);

                if (targetMatch || nameMatch)
                {
                    matchedTiles.Add(tile);
                }
            }

            return matchedTiles.Count > 0;
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
            return SearchResultsListBox.SelectedItem as CatalogItemModel;
        }

        private void OnContextMenuOpenClick(object sender, RoutedEventArgs e)
        {
            var item = _activeContextMenuItem ?? GetCatalogItemFromMenu(sender);
            if (item != null)
            {
                AppLaunchRequested?.Invoke(this, item);
                Close();
                MainWindow.Current?.HideScreen();
            }
        }

        private void OnContextMenuRunAsAdminClick(object sender, RoutedEventArgs e)
        {
            var item = _activeContextMenuItem ?? GetCatalogItemFromMenu(sender);
            if (item == null || string.IsNullOrWhiteSpace(item.TargetPath)) return;

            try
            {
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

                NativeMethods.LaunchTarget(execPath, item.Arguments, runAsAdmin: true);

                // Close drawer and hide MetroHub on successful launch so elevated prompt and window come forward
                Close();
                MainWindow.Current?.HideScreen();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AllAppsDrawer] Run as admin failed: {ex.Message}");
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

            if (IsAppPinned(item, out var matchedTiles))
            {
                MainWindow.Current?.BatchUnpinTiles(matchedTiles);
            }
            else
            {
                AppPinRequested?.Invoke(this, item);
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
