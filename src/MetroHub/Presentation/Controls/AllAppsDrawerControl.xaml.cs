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
            if (e.Key == Key.Up && SearchResultsListBox.SelectedIndex == 0)
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

        private void OnContextMenuPinClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is CatalogItemModel item)
            {
                AppPinRequested?.Invoke(this, item);
            }
        }

        private void OnContextMenuOpenClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is CatalogItemModel item)
            {
                AppLaunchRequested?.Invoke(this, item);
            }
        }
    }
}
