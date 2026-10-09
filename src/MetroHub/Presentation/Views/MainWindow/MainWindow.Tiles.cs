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
using System.Windows.Threading;
using Microsoft.Win32;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Core.Services.Catalog;
using MetroHub.Presentation.Controllers;
using MetroHub.Presentation.Controls;
using MetroHub.Presentation.Dialogs;
using MetroHub.Presentation.Themes;
using MenuItem = System.Windows.Controls.MenuItem;
using ContextMenu = System.Windows.Controls.ContextMenu;

namespace MetroHub;

public partial class MainWindow
{
    private static readonly Brush WidgetCategoryHeaderBrush = ThemeTokens.CreateFrozenBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF));
    private static readonly Brush WidgetItemIconBrush = ThemeTokens.CreateFrozenBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF));
    private Point _canvasRightClickPoint;
    private bool _isAppsLoaded = false;
    private bool _isLoadingApps = false;
    private DateTime _lastAppsRefreshTime = DateTime.UtcNow;

    private readonly LayoutHistoryService _historyService = new();

    private TileManager? _tileManager;
    public TileManager TileManager => _tileManager ??= new TileManager(
        () => Tiles,
        () => Groups,
        () => TilesListBox,
        () => ContentScrollViewer,
        () => Width,
        AnimateModifiedTiles,
        () => UpdateGroupHeaderPositions(animate: true),
        UpdateLayoutMetrics,
        UpdateCanvasHeight,
        UpdateExposedAddSlots,
        SaveGroupsAndLayout,
        CleanEmptyGroupsAndReflow,
        CompactGroupGaps,
        FlashLockedGroupPerimeter,
        () =>
        {
            if (DropSlotIndicator != null)
            {
                DropSlotIndicator.Visibility = Visibility.Collapsed;
            }
        },
        _historyService,
        Dispatcher);

    public void ClearTileSelection() => TileManager.ClearSelection();

    public List<TileModel> SelectedTiles => TileManager.GetSelectedTiles();

    public void ExecuteUndo() => TileManager.ExecuteUndo(_isDragging, _isRubberBanding);

    public void ExecuteRedo() => TileManager.ExecuteRedo(_isDragging, _isRubberBanding);

    private void RestoreLayoutFromSnapshot(string snapshot) => TileManager.RestoreLayoutFromSnapshot(snapshot);

    public void BatchResizeSelectedTiles(int newSpanX, int newSpanY, TileModel anchorTile) =>
        TileManager.BatchResizeSelectedTiles(newSpanX, newSpanY, anchorTile);

    public void BatchStyleSelectedTiles(string newStyle, TileModel anchorTile) =>
        TileManager.BatchStyleSelectedTiles(newStyle, anchorTile);

    public void DeleteSelectedTiles() => TileManager.DeleteSelectedTiles();

    public void BatchUnpinSelectedTiles(TileModel anchorTile) =>
        TileManager.BatchUnpinSelectedTiles(anchorTile);

    public void BatchUnpinTiles(IList<TileModel> targets) =>
        TileManager.BatchUnpinTiles(targets);

    public void AddFileAsTile(string filePath, double x = 0, double y = 0, bool recordHistory = true) =>
        TileManager.AddFileAsTile(filePath, x, y, recordHistory);

    public void AddWebLinkTile(string title, string url, string? iconPath, double x = 0, double y = 0, bool recordHistory = true) =>
        TileManager.AddWebLinkTile(title, url, iconPath, x, y, recordHistory);

    public void PinCatalogItem(CatalogItemModel item, Point? targetCanvasPosition = null) =>
        TileManager.PinCatalogItem(item, targetCanvasPosition);

    public void PinWidget(MetroHub.Widgets.Registry.WidgetDefinition def, Point? targetCanvasPosition = null) =>
        TileManager.PinWidget(def, targetCanvasPosition);

    private void OnTileActivated(object sender, RoutedEventArgs e)
    {
        if (Settings.CloseOnLaunch)
        {
            HideScreen(restorePreviousFocus: false);
        }
    }

    private void OnTileUnpinned(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TileModel tile)
        {
            BatchUnpinSelectedTiles(tile);
        }
    }

    private void OnTileModified(object sender, RoutedEventArgs e)
    {
        TileModel? tile = (e is TileModifiedEventArgs args ? args.Tile : e.OriginalSource as TileModel);
        if (tile != null)
        {
            bool isResize = e is TileModifiedEventArgs tmArgs && tmArgs.IsResize;
            if (isResize)
            {
                BatchResizeSelectedTiles(tile.SpanX, tile.SpanY, tile);
            }
            else
            {
                string preModify = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
                SaveGroupsAndLayout();
                string postModify = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
                if (preModify != postModify)
                {
                    _historyService.PushState(preModify);
                }
            }
        }
    }

    private void AnimateModifiedTiles(IList<TileModel> modified)
    {
        if (TilesListBox == null) return;

        foreach (var t in modified)
        {
            var container = TilesListBox.ItemContainerGenerator.ContainerFromItem(t) as ContentPresenter;
            if (container != null)
            {
                double currentLeft = Canvas.GetLeft(container);
                double currentTop = Canvas.GetTop(container);
                if (double.IsNaN(currentLeft)) currentLeft = t.X;
                if (double.IsNaN(currentTop)) currentTop = t.Y;

                if (Math.Abs(currentLeft - t.X) > 0.5 || Math.Abs(currentTop - t.Y) > 0.5)
                {
                    var animX = new DoubleAnimation(currentLeft, t.X, TimeSpan.FromMilliseconds(220))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    var animY = new DoubleAnimation(currentTop, t.Y, TimeSpan.FromMilliseconds(220))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    animX.Completed += (s, ev) =>
                    {
                        Canvas.SetLeft(container, t.X);
                        container.BeginAnimation(Canvas.LeftProperty, null);
                    };
                    animY.Completed += (s, ev) =>
                    {
                        Canvas.SetTop(container, t.Y);
                        container.BeginAnimation(Canvas.TopProperty, null);
                    };
                    container.BeginAnimation(Canvas.LeftProperty, animX);
                    container.BeginAnimation(Canvas.TopProperty, animY);
                }
                else
                {
                    Canvas.SetLeft(container, t.X);
                    Canvas.SetTop(container, t.Y);
                }
            }
        }

        UpdateGroupHeaderPositions();
    }

    private void OnAddTileClick(object sender, RoutedEventArgs e)
    {
        PromptAddTile();
    }

    public void UpdateExposedAddSlots()
    {
    }

    private void OnCanvasContextMenuClosed(object sender, RoutedEventArgs e)
    {
    }

    public void PromptAddTile()
    {
        IsDialogOpen = true;
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select Application or Shortcut to Pin",
                Filter = "Executables & Shortcuts (*.exe;*.lnk)|*.exe;*.lnk|All Files (*.*)|*.*",
                Multiselect = true
            };

            if (dialog.ShowDialog(this) == true && dialog.FileNames.Length > 0)
            {
                string preAdd = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
                _historyService.PushState(preAdd);

                foreach (string file in dialog.FileNames)
                {
                    AddFileAsTile(file, _canvasRightClickPoint.X, _canvasRightClickPoint.Y, recordHistory: false);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Failed to open add tile file dialog: {ex.Message}");
        }
        finally
        {
            IsDialogOpen = false;
            Activate();
        }
    }

    public void ShowSetWeatherLocationDialog(Widgets.Catalog.Weather.WeatherWidgetViewModel weatherVm)
    {
        if (weatherVm == null) return;

        using (EnterDialogScope())
        {
            Presentation.Controls.WeatherLocationDialog.Show(this, weatherVm);
        }
    }

    public void ShowAddWebLinkDialog(string? initialUrl = null)
    {
        using (EnterDialogScope())
        {
            var result = WebLinkDialog.Show(this, initialUrl);
            if (result != null)
            {
                OnWebLinkCreated(this, result);
            }
        }
    }

    private void OnSidebarAddWebLinkRequested(object? sender, string? initialUrl)
    {
        ShowAddWebLinkDialog(initialUrl);
    }

    private void OnCanvasAddWebLinkClick(object sender, RoutedEventArgs e)
    {
        ShowAddWebLinkDialog();
    }

    private void OnWebLinkCreated(object? sender, WebLinkCreatedEventArgs e)
    {
        if (e.AddToCanvas)
        {
            AddWebLinkTile(e.Title, e.Url, e.IconPath, _canvasRightClickPoint.X, _canvasRightClickPoint.Y, recordHistory: true);
        }

        if (e.AddToSidebar)
        {
            SidebarRail?.AddWebLinkShortcut(e.Title, e.Url, e.IconPath);
        }
    }

    private void OnCanvasPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (AllAppsDrawer != null && AllAppsDrawer.IsOpen)
        {
            if (!AllAppsDrawer.IsMouseOver && (SidebarRail == null || !SidebarRail.IsMouseOver))
            {
                AllAppsDrawer.Close();
                SidebarRail?.SetAppsDrawerActive(false);
            }
        }

        if (SidebarRail != null && SidebarRail.IsMouseOver) return;
        if (AllAppsDrawer != null && AllAppsDrawer.IsMouseOver) return;

        DependencyObject? dep = e.OriginalSource as DependencyObject;
        var tileControl = FindParent<Presentation.Controls.TileControl>(dep);

        if (tileControl != null && tileControl.DataContext is TileModel tile)
        {
            bool isCtrlDown = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

            if (isCtrlDown)
            {
                tile.IsSelected = !tile.IsSelected;
            }
            else
            {
                if (!tile.IsSelected)
                {
                    ClearTileSelection();
                    tile.IsSelected = true;
                }
            }
        }
        else
        {
            if (TilesListBox != null)
            {
                _canvasRightClickPoint = e.GetPosition(TilesListBox);
            }

            bool isCtrlDown = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            if (!isCtrlDown)
            {
                ClearTileSelection();
            }
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is not Visual && parent is not System.Windows.Media.Media3D.Visual3D) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild) return typedChild;
            var desc = FindVisualChild<T>(child);
            if (desc != null) return desc;
        }
        return null;
    }

    private void OnCanvasContextMenuOpened(object sender, RoutedEventArgs e)
    {
        _activeContextMenuGroup = GetGroupAtCanvasPoint(_canvasRightClickPoint);

        bool isInsideGroup = _activeContextMenuGroup != null;
        UpdateBackdropMenuChecks();
        UpdateTileCornerMenuChecks();

        if (GroupMenuSeparator != null)
            GroupMenuSeparator.Visibility = isInsideGroup ? Visibility.Visible : Visibility.Collapsed;

        if (RenameGroupCanvasMenuItem != null)
        {
            RenameGroupCanvasMenuItem.Visibility = isInsideGroup ? Visibility.Visible : Visibility.Collapsed;
            RenameGroupCanvasMenuItem.IsEnabled = isInsideGroup && !_activeContextMenuGroup!.IsLocked;
        }

        if (GroupHeaderColorCanvasMenuItem != null)
            GroupHeaderColorCanvasMenuItem.Visibility = isInsideGroup ? Visibility.Visible : Visibility.Collapsed;

        if (GroupTintColorCanvasMenuItem != null)
            GroupTintColorCanvasMenuItem.Visibility = isInsideGroup ? Visibility.Visible : Visibility.Collapsed;

        if (LockGroupCanvasMenuItem != null)
        {
            LockGroupCanvasMenuItem.Visibility = isInsideGroup ? Visibility.Visible : Visibility.Collapsed;
            if (isInsideGroup)
            {
                LockGroupCanvasMenuItem.Header = _activeContextMenuGroup!.IsLocked ? "Unlock Group" : "Lock Group";
                if (LockGroupCanvasIcon != null)
                {
                    LockGroupCanvasIcon.Symbol = _activeContextMenuGroup.IsLocked
                        ? Wpf.Ui.Controls.SymbolRegular.LockOpen24
                        : Wpf.Ui.Controls.SymbolRegular.LockClosed24;
                }
            }
        }

        if (UngroupCanvasMenuItem != null)
        {
            UngroupCanvasMenuItem.Visibility = isInsideGroup ? Visibility.Visible : Visibility.Collapsed;
            UngroupCanvasMenuItem.IsEnabled = isInsideGroup && !_activeContextMenuGroup!.IsLocked;
        }

        if (DeleteGroupCanvasMenuItem != null)
        {
            DeleteGroupCanvasMenuItem.Visibility = isInsideGroup ? Visibility.Visible : Visibility.Collapsed;
            DeleteGroupCanvasMenuItem.IsEnabled = isInsideGroup && !_activeContextMenuGroup!.IsLocked;
        }

        if (CreateGroupCanvasMenuItem != null)
        {
            CreateGroupCanvasMenuItem.Visibility = isInsideGroup ? Visibility.Collapsed : Visibility.Visible;
        }

        if (!_isAppsLoaded && !_isLoadingApps)
        {
            _ = LoadAppsSubmenuAsync();
        }
    }

    private async void OnAppsSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!_isAppsLoaded)
        {
            await LoadAppsSubmenuAsync();
        }
    }

    private void StartBackgroundAppWarmup()
    {
        Task.Run(async () =>
        {
            try
            {
                var apps = InstalledAppsService.GetInstalledApps(forceRefresh: false);
                if (apps == null || apps.Count == 0) return;

                var groups = Presentation.Controls.AllAppsDrawerControl.CreateAlphabeticalGroups(apps);
                var ordered = apps.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();

                await Dispatcher.InvokeAsync(() =>
                {
                    AllAppsDrawer?.SetPreloadedApps(apps, groups);
                    if (AppsMenuItem != null)
                    {
                        AppsMenuItem.ItemsSource = null;
                        AppsMenuItem.Items.Clear();
                        AppsMenuItem.ItemsSource = ordered;
                    }
                    _isAppsLoaded = true;
                }, DispatcherPriority.ApplicationIdle);

                // Defer non-critical icon prewarming by 500ms so startup completes with 0% CPU/disk contention
                await Task.Delay(500);
                CatalogItemModel.PrewarmMemoryCache(apps);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppWarmup] Background warmup error: {ex.Message}");
            }
        });
    }

    private async Task LoadAppsSubmenuAsync()
    {
        if (_isAppsLoaded || _isLoadingApps) return;
        _isLoadingApps = true;

        try
        {
            var provider = CatalogService.GetProvider("installed_apps");
            if (provider != null && AppsMenuItem != null)
            {
                var items = await provider.GetItemsAsync();
                var ordered = items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();

                AppsMenuItem.ItemsSource = null;
                AppsMenuItem.Items.Clear();
                AppsMenuItem.ItemsSource = ordered;

                _isAppsLoaded = true;

                CatalogItemModel.PrewarmMemoryCache(ordered);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppsMenu] Error loading apps: {ex.Message}");
        }
        finally
        {
            _isLoadingApps = false;
        }
    }

    private void OnAppsCatalogChanged(List<CatalogItemModel> freshApps)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_isAppsLoaded && AppsMenuItem != null)
            {
                var ordered = freshApps.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
                AppsMenuItem.ItemsSource = null;
                AppsMenuItem.Items.Clear();
                AppsMenuItem.ItemsSource = ordered;
            }
            AllAppsDrawer?.LoadApps(freshApps);
        }, DispatcherPriority.Background);
    }

    private void TriggerBackgroundAppsCatalogRefresh()
    {
        if ((DateTime.UtcNow - _lastAppsRefreshTime).TotalSeconds < 10)
        {
            return;
        }

        _lastAppsRefreshTime = DateTime.UtcNow;

        Task.Run(async () =>
        {
            try
            {
                var provider = CatalogService.GetProvider("installed_apps");
                if (provider == null) return;

                var freshItems = await provider.GetItemsAsync(forceRefresh: true);

                if (_isAppsLoaded)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (AppsMenuItem != null)
                        {
                            var ordered = freshItems.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
                            AppsMenuItem.ItemsSource = null;
                            AppsMenuItem.Items.Clear();
                            AppsMenuItem.ItemsSource = ordered;
                        }
                    }, DispatcherPriority.Background);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppsCatalog] Background refresh error: {ex.Message}");
            }
        });
    }

    private void OnAppMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is CatalogItemModel item)
        {
            PinCatalogItem(item, _canvasRightClickPoint);
        }
    }

    private void OnWidgetsSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem widgetsMenu) return;
        widgetsMenu.Items.Clear();

        var widgets = MetroHub.Widgets.Registry.WidgetRegistry.GetAll();
        if (widgets.Count == 0)
        {
            widgetsMenu.Items.Add(new MenuItem { Header = "No widgets available", IsEnabled = false });
            return;
        }

        var categories = new[]
        {
            "Quick Actions",
            "Sound",
            "Display",
            "Productivity",
            "Lifestyle"
        };

        bool isFirstGroup = true;

        foreach (var categoryName in categories)
        {
            var categoryWidgets = widgets
                .Where(w => string.Equals(w.Category, categoryName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (categoryWidgets.Count == 0) continue;

            if (!isFirstGroup)
            {
                widgetsMenu.Items.Add(new Separator());
            }
            isFirstGroup = false;

            // Section Header (non-selectable, subtle typography)
            var headerItem = new MenuItem
            {
                Header = categoryName.ToUpperInvariant(),
                IsEnabled = false,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = WidgetCategoryHeaderBrush,
                Padding = new Thickness(10, 4, 14, 2),
                MinHeight = 22,
                Focusable = false
            };
            widgetsMenu.Items.Add(headerItem);

            foreach (var def in categoryWidgets)
            {
                var mi = new MenuItem
                {
                    Header = def.DisplayName,
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = def.Icon,
                        FontSize = 18,
                        Foreground = WidgetItemIconBrush
                    },
                    Tag = def,
                    Cursor = Cursors.Hand,
                    MinHeight = 32,
                    Padding = new Thickness(10, 4, 14, 4)
                };
                mi.Click += OnWidgetMenuItemClick;
                widgetsMenu.Items.Add(mi);
            }
        }
    }

    private void OnWidgetMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is MetroHub.Widgets.Registry.WidgetDefinition def)
        {
            PinWidget(def, _canvasRightClickPoint);
        }
    }

    private void OnExportLayoutClick(object sender, RoutedEventArgs e)
    {
        IsDialogOpen = true;
        try
        {
            var sfd = new SaveFileDialog
            {
                Title = "Export MetroHub Layout",
                Filter = "JSON Files (*.json)|*.json",
                FileName = "MetroHub_Layout.json"
            };

            if (sfd.ShowDialog(this) == true)
            {
                if (StorageService.ExportLayout(Tiles, sfd.FileName))
                {
                    System.Windows.MessageBox.Show(this, "Layout exported successfully!", "MetroHub",
                        System.Windows.MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    ShowToast("Failed to export layout.", isError: true);
                }
            }
        }
        catch (Exception ex)
        {
            Safe.Log("MainWindow.ExportLayout", ex);
            ShowToast($"Failed to export layout: {ex.Message}", isError: true);
        }
        finally
        {
            IsDialogOpen = false;
            Activate();
        }
    }

    private void OnTileCornerSharpClick(object sender, RoutedEventArgs e) => ApplyTileCornerRadius(0);
    private void OnTileCornerSubtleClick(object sender, RoutedEventArgs e) => ApplyTileCornerRadius(2);
    private void OnTileCornerMediumClick(object sender, RoutedEventArgs e) => ApplyTileCornerRadius(4);
    private void OnTileCornerRoundedClick(object sender, RoutedEventArgs e) => ApplyTileCornerRadius(8);

    public void ApplyTileCornerRadius(int radius)
    {
        Settings.TileCornerRadius = radius;
        StorageService.SaveSettings(Settings);
        Application.Current.Resources["TileCornerRadius"] = new CornerRadius(radius);
        UpdateTileCornerMenuChecks();
    }

    private void UpdateTileCornerMenuChecks()
    {
        int r = Settings.TileCornerRadius;
        if (TileCornerSharpItem != null) TileCornerSharpItem.IsChecked = r == 0;
        if (TileCornerSubtleItem != null) TileCornerSubtleItem.IsChecked = r == 2;
        if (TileCornerMediumItem != null) TileCornerMediumItem.IsChecked = r == 4;
        if (TileCornerRoundedItem != null) TileCornerRoundedItem.IsChecked = r == 8;
    }

    public async Task ExecuteCanvasPasteAsync()
    {
        try
        {
            Point? anchorPoint = null;
            if (TilesListBox != null && TilesListBox.IsMouseOver)
            {
                Point mousePos = Mouse.GetPosition(TilesListBox);
                if (mousePos.X >= 0 && mousePos.Y >= 0)
                {
                    anchorPoint = mousePos;
                }
            }

            var specs = await CanvasPasteService.ExtractPasteItemsAsync().ConfigureAwait(true);
            if (specs != null && specs.Count > 0)
            {
                TileManager.BatchAddPastedTiles(specs, anchorPoint);
            }
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "MainWindow.ExecuteCanvasPasteAsync");
        }
    }
}
