using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Core.Services.Catalog;
using MetroHub.Presentation.Controls;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Image = System.Windows.Controls.Image;

namespace MetroHub;

public partial class MainWindow : BorderlessFluentWindow
{
    public ObservableCollection<TileModel> Tiles { get; set; } = new();
    public ObservableCollection<TileGroupModel> Groups { get; set; } = new();
    public AppSettings Settings { get; set; } = new();

    public static MainWindow? Current { get; private set; }
    public bool IsDialogOpen { get; set; } = false;

    private readonly HotkeyService _hotkeyService = new();
    private DispatcherTimer? _hudTimer;
    private bool _isClosingToExit = false;

    private Point _canvasRightClickPoint;
    private bool _isAppsLoaded = false;
    private bool _isLoadingApps = false;
    private DateTime _lastAppsRefreshTime = DateTime.MinValue;

    public MainWindow()
    {
        Current = this;
        InitializeComponent();
        DataContext = this;

        LoadData();
        SetupHudTimer();

        Activated += OnWindowActivated;
        Deactivated += OnWindowDeactivated;
        SizeChanged += (s, e) => { UpdateLayoutMetrics(); UpdateCanvasHeight(); UpdateExposedAddSlots(); };

        InstalledAppsService.AppsCatalogChanged += OnAppsCatalogChanged;
        Task.Run(() =>
        {
            var apps = InstalledAppsService.GetInstalledApps(forceRefresh: false);
            CatalogItemModel.PrewarmMemoryCache(apps);
        });
    }

    private DateTime _lastShownTime = DateTime.MinValue;
    private bool _isFullyActivated = false;
    private bool _isHiding = false;

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        _isFullyActivated = true;
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        // Must be fully activated first, and ignore premature deactivation within 150ms of opening
        if (!_isFullyActivated)
        {
            return;
        }

        if ((DateTime.UtcNow - _lastShownTime).TotalMilliseconds < 150)
        {
            return;
        }

        if (!IsDialogOpen && IsVisible && !_isHiding)
        {
            HideScreen();
        }
    }

    private void LoadData()
    {
        Settings = StorageService.LoadSettings();
        Tiles = StorageService.LoadLayout();
        Groups = StorageService.LoadGroups();

        DiscoverGroupsFromTiles();
        EnsureGroupIndices();
        MigrateGroupColumnOffsets();
        UpdateLayoutMetrics();
        bool anyCleaned = GridPlacementService.CleanEmptyGroups(Groups, Tiles);
        UpdateGroupHeaderPositions();

        if (anyCleaned)
        {
            SaveGroupsAndLayout();
        }

        TilesListBox.ItemsSource = Tiles;
        if (GroupsListBox != null) GroupsListBox.ItemsSource = Groups;
        UpdateCanvasHeight();
        UpdateExposedAddSlots();

        if (BackdropToggleSwitch != null)
        {
            BackdropToggleSwitch.IsChecked = string.Equals(Settings.BackdropType, "Acrylic", StringComparison.OrdinalIgnoreCase);
        }
    }

    private void OnBackdropToggleClick(object sender, RoutedEventArgs e)
    {
        Settings.BackdropType = BackdropToggleSwitch.IsChecked == true ? "Acrylic" : "Mica";
        StorageService.SaveSettings(Settings);
        ApplyConfiguredBackdrop();
    }

    private void UpdateLayoutMetrics()
    {
        double viewportWidth = ContentScrollViewer?.ActualWidth > 0 
            ? ContentScrollViewer.ActualWidth 
            : (Width > 0 ? Width : 1920);

        GridPlacementService.UpdateMetrics(viewportWidth);
        double margin = GridPlacementService.OriginX;

        if (HeaderGrid != null)
        {
            HeaderGrid.Margin = new Thickness(margin, 0, margin, 0);
        }
        if (FooterGrid != null)
        {
            FooterGrid.Margin = new Thickness(margin, 0, margin, 0);
        }
    }

    private void UpdateCanvasHeight()
    {
        if (MainCanvasGrid == null) return;
        double maxBottom = (Tiles != null && Tiles.Any()) ? Tiles.Max(t => t.Y + t.HeightPixels) + 120 : 600;
        double viewportHeight = ContentScrollViewer?.ActualHeight > 0 ? ContentScrollViewer.ActualHeight : 600;
        MainCanvasGrid.MinHeight = Math.Max(viewportHeight, maxBottom);
    }

    private void SetupHudTimer()
    {
        _hudTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _hudTimer.Tick += (s, e) =>
        {
            if (IsVisible)
            {
                // Measures purely MetroHub's internal app data and features, excluding external Windows/DirectX shared memory
                long bytes = GC.GetTotalMemory(forceFullCollection: false);
                double mb = Math.Round(bytes / (1024.0 * 1024.0), 1);
                RamHudTextBlock.Text = $"App RAM: {mb} MB";
            }
        };
        _hudTimer.Start();
    }

    public void ApplyConfiguredBackdrop()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        Background = System.Windows.Media.Brushes.Transparent;
        var hs = HwndSource.FromHwnd(hwnd);
        if (hs?.CompositionTarget != null)
        {
            hs.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
        }

        if (string.Equals(Settings.BackdropType, "Acrylic", StringComparison.OrdinalIgnoreCase))
        {
            // Acrylic backdrop + Smoked Obsidian tint overlay
            NativeMethods.ApplyMica(hwnd, dark: true, NativeMethods.DWMSBT_TRANSIENTWINDOW);
            if (RootGrid != null)
            {
                RootGrid.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x99, 0x0D, 0x0D, 0x11));
            }
        }
        else if (string.Equals(Settings.BackdropType, "MicaAlt", StringComparison.OrdinalIgnoreCase))
        {
            // Mica Alt (Tabbed) variant
            NativeMethods.ApplyMica(hwnd, dark: true, NativeMethods.DWMSBT_TABBEDWINDOW);
            if (RootGrid != null)
            {
                RootGrid.Background = System.Windows.Media.Brushes.Transparent;
            }
        }
        else
        {
            // Default: Pure Windows 11 Mica backdrop
            NativeMethods.ApplyMica(hwnd, dark: true, NativeMethods.DWMSBT_MAINWINDOW);
            if (RootGrid != null)
            {
                RootGrid.Background = System.Windows.Media.Brushes.Transparent;
            }
        }
    }

    private void OnSourceInitialized(object sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;

        ApplyConfiguredBackdrop();

        // Hook Win32 window messages for instant broadcast IPC wake
        var hwndSource = HwndSource.FromHwnd(hwnd);
        hwndSource?.AddHook(WndProc);

        // Register the global hotkey
        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        _hotkeyService.Register(hwnd, Settings);

        // Snap to active monitor work area (never cover the Windows Taskbar)
        SnapToWorkArea();
    }

    private const int WM_SETTINGCHANGE = 0x001A;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg == NativeMethods.WM_SHOW_METROHUB)
        {
            Dispatcher.Invoke(ShowScreen);
            handled = true;
        }
        else if (msg == WM_SETTINGCHANGE)
        {
            Dispatcher.InvokeAsync(async () =>
            {
                ApplyConfiguredBackdrop();

                // Wait for Windows DWM wallpaper transition cross-fade to complete
                await Task.Delay(400);
                ApplyConfiguredBackdrop();
            });
        }
        return IntPtr.Zero;
    }

    public void SnapToWorkArea()
    {
        Rect workArea = NativeMethods.GetActiveMonitorWorkArea();
        Left = workArea.Left;
        Top = workArea.Top;
        Width = workArea.Width;
        Height = workArea.Height;

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 
                (int)workArea.Left, (int)workArea.Top, 
                (int)workArea.Width, (int)workArea.Height, 
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
        }

        UpdateLayoutMetrics();
        UpdateCanvasHeight();
    }

    private void OnHotkeyPressed()
    {
        Dispatcher.Invoke(ToggleVisibility);
    }

    public void ToggleVisibility()
    {
        if (IsVisible && !_isHiding)
        {
            HideScreen();
        }
        else
        {
            ShowScreen();
        }
    }

    public void ShowScreen()
    {
        _isHiding = false;
        _lastShownTime = DateTime.UtcNow;
        _isFullyActivated = false;
        SnapToWorkArea();
        Show();
        WindowState = WindowState.Normal;
        Topmost = true;

        ApplyConfiguredBackdrop();

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.ForceForeground(hwnd);
        }

        Activate();
        Focus();
        UpdateLayoutMetrics();
        UpdateExposedAddSlots();

        PlayEntranceAnimation();
        TriggerBackgroundAppsCatalogRefresh();
    }

    public void HideScreen()
    {
        if (_isHiding || !IsVisible) return;
        _isHiding = true;
        _isFullyActivated = false;

        PlayExitAnimation(() =>
        {
            if (_isHiding)
            {
                Hide();
                _isHiding = false;
                NativeMethods.FlushMemory();
            }
        });
    }

    private void PlayEntranceAnimation()
    {
        if (RootGrid == null) return;

        var cubicEase = new CubicEase { EasingMode = EasingMode.EaseOut };

        // 1. Fluent Opacity Fade-In (0.0 -> 1.0)
        var opacityAnim = new DoubleAnimation
        {
            From = RootGrid.Opacity < 0.1 ? 0.0 : RootGrid.Opacity,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = cubicEase
        };

        // 2. Gentle upward rise (Y: 16 -> 0)
        var translateAnim = new DoubleAnimation
        {
            From = RootTranslate != null ? (RootTranslate.Y > 0 ? RootTranslate.Y : 16.0) : 16.0,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = cubicEase
        };

        // 3. Subtle scale lift (0.985 -> 1.0)
        var scaleAnim = new DoubleAnimation
        {
            From = RootScale != null ? (RootScale.ScaleX < 1.0 ? RootScale.ScaleX : 0.985) : 0.985,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = cubicEase
        };

        RootGrid.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        RootTranslate?.BeginAnimation(TranslateTransform.YProperty, translateAnim);
        RootScale?.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        RootScale?.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
    }

    private void PlayExitAnimation(Action onCompleted)
    {
        if (RootGrid == null)
        {
            onCompleted();
            return;
        }

        var quadEase = new QuadraticEase { EasingMode = EasingMode.EaseIn };

        // 1. Snappy Opacity Fade-Out (1.0 -> 0.0) in 110ms
        var opacityAnim = new DoubleAnimation
        {
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(110),
            EasingFunction = quadEase
        };

        // 2. Subtle settle downwards (Y: 0 -> 12)
        var translateAnim = new DoubleAnimation
        {
            To = 12.0,
            Duration = TimeSpan.FromMilliseconds(110),
            EasingFunction = quadEase
        };

        // 3. Subtle scale settle (1.0 -> 0.985)
        var scaleAnim = new DoubleAnimation
        {
            To = 0.985,
            Duration = TimeSpan.FromMilliseconds(110),
            EasingFunction = quadEase
        };

        opacityAnim.Completed += (s, e) => onCompleted();

        RootGrid.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        RootTranslate?.BeginAnimation(TranslateTransform.YProperty, translateAnim);
        RootScale?.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        RootScale?.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool isShift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (isCtrl && e.Key == Key.Z)
        {
            if (isShift)
            {
                ExecuteRedo();
            }
            else
            {
                ExecuteUndo();
            }
            e.Handled = true;
            return;
        }

        if (isCtrl && e.Key == Key.Y)
        {
            ExecuteRedo();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_isDragging || _isPotentialDrag || _isRubberBanding)
            {
                CancelActiveDrag();
                e.Handled = true;
                return;
            }

            if (Tiles.Any(t => t.IsSelected))
            {
                ClearTileSelection();
                e.Handled = true;
                return;
            }

            HideScreen();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
        {
            HideScreen();
            e.Handled = true;
        }
    }

    private readonly LayoutHistoryService _historyService = new();
    private string? _preDragLayoutSnapshot;

    private TileModel? _draggedTile;
    private Presentation.Controls.TileControl? _draggedControl;
    private ContentPresenter? _draggedContainer;
    private Point _dragStartPoint;
    private double _dragOffsetX;  // offset from mouse to anchor tile's left edge
    private double _dragOffsetY;  // offset from mouse to anchor tile's top edge
    private int _dragOriginalCol;
    private int _dragOriginalRow;
    private bool _isPotentialDrag;
    private bool _isDragging;
    private const int GroupColWidth = 8;
    private TileGroupModel? _hoveredTargetGroup;
    private bool _isGroupDrag;
    private TileGroupModel? _draggedGroupModel;
    private double _draggedGroupOffsetX;
    private double _draggedGroupOffsetY;
    private int _groupDragTargetColIndex;
    private int _groupDragTargetRow;

    // Multi-tile Selection & Cluster Drag State
    private bool _isRubberBanding;
    private Point _rubberBandStartPoint;
    private bool _rubberBandHasMoved;
    private HashSet<TileModel> _preRubberBandSelected = new();
    private List<TileModel> _draggedCluster = new();
    private Dictionary<TileModel, (double X, double Y, int Col, int Row)> _dragClusterOriginals = new();
    private (double MinRelX, double MaxRelX, double MinRelY, double MaxRelY) _clusterRelBounds;
    private (int MinRelCol, int MaxRelCol, int MinRelRow, int MaxRelRow) _clusterRelGridBounds;
    private bool _dragBeganWithSelection;

    public void ClearTileSelection()
    {
        foreach (var t in Tiles)
        {
            t.IsSelected = false;
        }
    }

    public List<TileModel> SelectedTiles => Tiles.Where(t => t.IsSelected).ToList();

    public void ExecuteUndo()
    {
        if (_isDragging || _isRubberBanding || !_historyService.CanUndo) return;

        ClearTileSelection();
        string currentSnapshot = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
        string? targetSnapshot = _historyService.Undo(currentSnapshot);
        if (!string.IsNullOrWhiteSpace(targetSnapshot))
        {
            RestoreLayoutFromSnapshot(targetSnapshot);
        }
    }

    public void ExecuteRedo()
    {
        if (_isDragging || _isRubberBanding || !_historyService.CanRedo) return;

        ClearTileSelection();
        string currentSnapshot = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
        string? targetSnapshot = _historyService.Redo(currentSnapshot);
        if (!string.IsNullOrWhiteSpace(targetSnapshot))
        {
            RestoreLayoutFromSnapshot(targetSnapshot);
        }
    }

    private void RestoreLayoutFromSnapshot(string snapshot)
    {
        var snapshotModel = LayoutHistoryService.ParseSnapshot(snapshot);
        if (snapshotModel == null) return;

        var targetTiles = snapshotModel.Tiles;
        var targetGroups = snapshotModel.Groups;

        var targetDict = targetTiles.ToDictionary(t => t.Id);
        var currentTiles = Tiles.ToList();
        var currentDict = currentTiles.ToDictionary(t => t.Id);

        // Remove tiles that are not in target snapshot (e.g. undo an add)
        var toRemove = currentTiles.Where(t => !targetDict.ContainsKey(t.Id)).ToList();
        foreach (var t in toRemove)
        {
            Tiles.Remove(t);
        }

        // Re-add tiles that were previously deleted (e.g. undo an unpin)
        var toAdd = targetTiles.Where(t => !currentDict.ContainsKey(t.Id)).ToList();
        foreach (var t in toAdd)
        {
            Tiles.Add(t);
        }

        // Restore Groups
        var targetGroupDict = targetGroups.ToDictionary(g => g.Id);
        var currentGroups = Groups.ToList();
        var currentGroupDict = currentGroups.ToDictionary(g => g.Id);

        // Defensive: If snapshot has no groups, but current layout has groups and tiles belong to groups, do NOT wipe groups!
        bool shouldRestoreGroups = targetGroups.Count > 0 || !targetTiles.Any(t => !string.IsNullOrEmpty(t.Group));
        if (shouldRestoreGroups)
        {
            var groupsToRemove = currentGroups.Where(g => !targetGroupDict.ContainsKey(g.Id)).ToList();
            foreach (var g in groupsToRemove)
            {
                Groups.Remove(g);
            }

            var groupsToAdd = targetGroups.Where(g => !currentGroupDict.ContainsKey(g.Id)).ToList();
            foreach (var g in groupsToAdd)
            {
                Groups.Add(g);
            }

            foreach (var tg in targetGroups)
            {
                var existingG = Groups.FirstOrDefault(g => g.Id == tg.Id);
                if (existingG == null) continue;
                existingG.Title = tg.Title;
                existingG.HeaderColor = tg.HeaderColor;
                existingG.Col = tg.Col;
                existingG.Row = tg.Row;
                existingG.X = tg.X;
                existingG.Y = tg.Y;
                existingG.IsEditing = false;
            }
        }

        // Apply coordinates, spans, and styles
        var modifiedList = new List<TileModel>();

        foreach (var target in targetTiles)
        {
            var existing = Tiles.FirstOrDefault(t => t.Id == target.Id);
            if (existing == null) continue;

            bool posChanged = Math.Abs(existing.X - target.X) > 0.5 || Math.Abs(existing.Y - target.Y) > 0.5;
            bool spanChanged = existing.SpanX != target.SpanX || existing.SpanY != target.SpanY;
            bool styleChanged = existing.TileStyle != target.TileStyle || existing.AccentColor != target.AccentColor;

            int oldSpanX = existing.SpanX;
            int oldSpanY = existing.SpanY;

            existing.Col = target.Col;
            existing.Row = target.Row;
            existing.SpanX = target.SpanX;
            existing.SpanY = target.SpanY;
            existing.TileStyle = target.TileStyle;
            existing.AccentColor = target.AccentColor;
            existing.Group = target.Group;
            existing.SectionHeader = target.SectionHeader;

            if (spanChanged)
            {
                var control = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, existing));
                control?.AnimateResize(oldSpanX, oldSpanY, target.SpanX, target.SpanY);
            }

            if (posChanged)
            {
                existing.X = target.X;
                existing.Y = target.Y;
                modifiedList.Add(existing);
            }
            else
            {
                var container = TilesListBox?.ItemContainerGenerator.ContainerFromItem(existing) as ContentPresenter;
                if (container != null)
                {
                    Canvas.SetLeft(container, target.X);
                    Canvas.SetTop(container, target.Y);
                }
            }

            if (styleChanged)
            {
                var control = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, existing));
                control?.ApplyTileStyle(animate: true);
            }
        }

        if (modifiedList.Count > 0)
        {
            AnimateModifiedTiles(modifiedList);
        }

        UpdateGroupHeaderPositions();
        UpdateLayoutMetrics();
        UpdateCanvasHeight();
        UpdateExposedAddSlots();
        SaveGroupsAndLayout();
    }

    private void OnCanvasPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        DependencyObject? dep = e.OriginalSource as DependencyObject;

        // Bypass if user clicked on a Thumb (ResizeGrip) or its children
        if (FindParent<System.Windows.Controls.Primitives.Thumb>(dep) != null)
        {
            _isPotentialDrag = false;
            _isDragging = false;
            _draggedTile = null;
            return;
        }

        // Bypass if user clicked on a GroupHeaderControl or its children
        if (FindParent<Presentation.Controls.GroupHeaderControl>(dep) != null)
        {
            _isPotentialDrag = false;
            _isDragging = false;
            _draggedTile = null;
            return;
        }

        // If clicking on Top Header, Search, or Footer, don't trigger canvas selection
        if (HeaderGrid != null && HeaderGrid.IsMouseOver) return;
        if (FooterGrid != null && FooterGrid.IsMouseOver) return;

        Point canvasMouse = TilesListBox != null ? e.GetPosition(TilesListBox) : e.GetPosition(this);
        bool isCtrlDown = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        var tileControl = FindParent<Presentation.Controls.TileControl>(dep);

        if (tileControl != null && tileControl.DataContext is TileModel tile)
        {
            _preDragLayoutSnapshot = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
            _isRubberBanding = false;
            _draggedTile = tile;

            _draggedControl = tileControl;
            _draggedContainer = FindParent<ContentPresenter>(tileControl)
                ?? TilesListBox?.ItemContainerGenerator.ContainerFromItem(tile) as ContentPresenter;
            _dragStartPoint = e.GetPosition(this);

            _dragOriginalCol = GridPlacementService.ColFromPixel(tile.X);
            _dragOriginalRow = GridPlacementService.RowFromPixel(tile.Y);

            _dragOffsetX = canvasMouse.X - tile.X;
            _dragOffsetY = canvasMouse.Y - tile.Y;

            if (isCtrlDown)
            {
                // Ctrl + Click toggles individual tile selection
                tile.IsSelected = !tile.IsSelected;
                _dragBeganWithSelection = tile.IsSelected;
            }
            else
            {
                // Normal Click:
                // If clicked tile is already part of a multi-selection, preserve group for cluster dragging!
                if (!tile.IsSelected)
                {
                    ClearTileSelection();
                    tile.IsSelected = true;
                    _dragBeganWithSelection = false;
                }
                else
                {
                    _dragBeganWithSelection = true;
                }
            }

            // Gather all selected tiles into the cluster
            if (tile.IsSelected)
            {
                _draggedCluster = Tiles.Where(t => t.IsSelected).ToList();
            }
            else
            {
                _draggedCluster = new List<TileModel> { tile };
            }

            // Record pre-drag coordinates and compute relative bounding box of the entire cluster
            _dragClusterOriginals.Clear();
            double minRelX = 0, maxRelX = tile.WidthPixels, minRelY = 0, maxRelY = tile.HeightPixels;
            int minRelCol = 0, maxRelCol = tile.SpanX, minRelRow = 0, maxRelRow = tile.SpanY;

            foreach (var cTile in _draggedCluster)
            {
                int cCol = GridPlacementService.ColFromPixel(cTile.X);
                int cRow = GridPlacementService.RowFromPixel(cTile.Y);
                _dragClusterOriginals[cTile] = (cTile.X, cTile.Y, cCol, cRow);

                double relX = cTile.X - tile.X;
                double relY = cTile.Y - tile.Y;
                int relCol = cCol - _dragOriginalCol;
                int relRow = cRow - _dragOriginalRow;

                minRelX = Math.Min(minRelX, relX);
                maxRelX = Math.Max(maxRelX, relX + cTile.WidthPixels);
                minRelY = Math.Min(minRelY, relY);
                maxRelY = Math.Max(maxRelY, relY + cTile.HeightPixels);

                minRelCol = Math.Min(minRelCol, relCol);
                maxRelCol = Math.Max(maxRelCol, relCol + cTile.SpanX);
                minRelRow = Math.Min(minRelRow, relRow);
                maxRelRow = Math.Max(maxRelRow, relRow + cTile.SpanY);
            }

            _clusterRelBounds = (minRelX, maxRelX, minRelY, maxRelY);
            _clusterRelGridBounds = (minRelCol, maxRelCol, minRelRow, maxRelRow);

            _isPotentialDrag = true;
            _isDragging = false;
            _draggedControl.AnimatePressDown();
        }
        else
        {
            // Clicked on empty canvas
            _isPotentialDrag = false;
            _isDragging = false;
            _draggedTile = null;
            _draggedControl = null;
            _draggedCluster.Clear();

            if (FindParent<System.Windows.Controls.Button>(dep) != null ||
                FindParent<ContextMenu>(dep) != null)
            {
                return;
            }

            // Begin rubber-band (marquee) box selection
            _isRubberBanding = true;
            _rubberBandHasMoved = false;
            _rubberBandStartPoint = canvasMouse;
            _preRubberBandSelected = new HashSet<TileModel>(Tiles.Where(t => t.IsSelected));

            if (!isCtrlDown)
            {
                ClearTileSelection();
                _preRubberBandSelected.Clear();
            }

            if (RubberBandBox != null)
            {
                Canvas.SetLeft(RubberBandBox, canvasMouse.X);
                Canvas.SetTop(RubberBandBox, canvasMouse.Y);
                RubberBandBox.Width = 0;
                RubberBandBox.Height = 0;
                RubberBandBox.Visibility = Visibility.Collapsed;
            }

            RootGrid.CaptureMouse();
        }
    }

    private void OnCanvasPreviewMouseMove(object sender, MouseEventArgs e)
    {
        Point canvasMouse = TilesListBox != null ? e.GetPosition(TilesListBox) : e.GetPosition(this);

        // 1. Rubber-Band Marquee Selection
        if (_isRubberBanding)
        {
            Vector diff = canvasMouse - _rubberBandStartPoint;
            if (Math.Abs(diff.X) > 3 || Math.Abs(diff.Y) > 3)
            {
                _rubberBandHasMoved = true;
            }

            if (_rubberBandHasMoved && RubberBandBox != null)
            {
                double left = Math.Min(_rubberBandStartPoint.X, canvasMouse.X);
                double top = Math.Min(_rubberBandStartPoint.Y, canvasMouse.Y);
                double width = Math.Abs(canvasMouse.X - _rubberBandStartPoint.X);
                double height = Math.Abs(canvasMouse.Y - _rubberBandStartPoint.Y);

                Canvas.SetLeft(RubberBandBox, left);
                Canvas.SetTop(RubberBandBox, top);
                RubberBandBox.Width = width;
                RubberBandBox.Height = height;
                RubberBandBox.Visibility = Visibility.Visible;

                var marqueeRect = new Rect(left, top, width, height);
                bool isCtrlDown = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

                foreach (var t in Tiles)
                {
                    var tileRect = new Rect(t.X, t.Y, t.WidthPixels, t.HeightPixels);
                    bool intersects = marqueeRect.IntersectsWith(tileRect);

                    if (isCtrlDown)
                    {
                        t.IsSelected = intersects ? !_preRubberBandSelected.Contains(t) : _preRubberBandSelected.Contains(t);
                    }
                    else
                    {
                        t.IsSelected = intersects;
                    }
                }
            }
            return;
        }

        // 2. Drag threshold check (transition from press to active drag)
        if (_isPotentialDrag && !_isDragging && e.LeftButton == MouseButtonState.Pressed && _draggedTile != null)
        {
            // Never promote to drag if the tile belongs to a locked group
            bool lockedTile = !string.IsNullOrEmpty(_draggedTile.Group) &&
                Groups.FirstOrDefault(g => g.Id == _draggedTile.Group)?.IsLocked == true;

            Point current = e.GetPosition(this);
            Vector diff = current - _dragStartPoint;
            if (lockedTile && (Math.Abs(diff.X) > 5 || Math.Abs(diff.Y) > 5))
            {
                // Attempted to drag a tile in a locked group: cancel drag & click so app does not launch
                _isPotentialDrag = false;
                _draggedControl?.AnimateRelease();
                _draggedTile = null;
                _draggedControl = null;
                _draggedContainer = null;
                return;
            }

            if (!lockedTile && (Math.Abs(diff.X) > 5 || Math.Abs(diff.Y) > 5))
            {
                _isDragging = true;
                ClearAllAmbientReveals();
                RootGrid.CaptureMouse();

                foreach (var cTile in _draggedCluster)
                {
                    cTile.IsBeingDragged = true;
                    var control = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, cTile));
                    control?.AnimateElevationLift();

                    var container = TilesListBox?.ItemContainerGenerator.ContainerFromItem(cTile) as ContentPresenter;
                    if (container != null)
                    {
                        Panel.SetZIndex(container, 9999);
                    }
                }

                // Show DropSlotIndicator matching cluster dimensions
                DropSlotIndicator.Width = Math.Max(56, _clusterRelBounds.MaxRelX - _clusterRelBounds.MinRelX);
                DropSlotIndicator.Height = Math.Max(56, _clusterRelBounds.MaxRelY - _clusterRelBounds.MinRelY);
                DropSlotIndicator.Visibility = Visibility.Visible;
            }
        }

        // 3. Ambient reveal when idle
        if (!_isDragging && !_isRubberBanding && TilesListBox != null)
        {
            UpdateAmbientReveal(canvasMouse);
        }

        // 4. Rigid Multi-Tile Cluster Dragging
        if (_isDragging && _draggedTile != null)
        {
            UpdateLayoutMetrics();
            int maxCols = GridPlacementService.MaxCols;

            double rawAnchorX = canvasMouse.X - _dragOffsetX;
            double rawAnchorY = canvasMouse.Y - _dragOffsetY;

            // Clamping bounds so the ENTIRE cluster stays within grid and window limits
            double minAllowedAnchorX = GridPlacementService.OriginX - _clusterRelBounds.MinRelX;
            double maxAllowedAnchorX = GridPlacementService.PixelXFromCol(maxCols) - _clusterRelBounds.MaxRelX;
            double minAllowedAnchorY = GridPlacementService.OriginY - _clusterRelBounds.MinRelY;

            if (_isGroupDrag && _draggedGroupModel != null)
            {
                // Ensure group header never exceeds the top boundary
                double minGroupAnchorY = GridPlacementService.OriginY + 8 - _draggedGroupOffsetY;
                minAllowedAnchorY = Math.Max(minAllowedAnchorY, minGroupAnchorY);
            }

            if (maxAllowedAnchorX < minAllowedAnchorX) maxAllowedAnchorX = minAllowedAnchorX;

            double clampedAnchorX = Math.Max(minAllowedAnchorX, Math.Min(rawAnchorX, maxAllowedAnchorX));
            double clampedAnchorY = Math.Max(minAllowedAnchorY, Math.Min(rawAnchorY, 3000));

            // Move every tile in the cluster rigidly preserving relative offsets
            foreach (var cTile in _draggedCluster)
            {
                double relX = _dragClusterOriginals.TryGetValue(cTile, out var orig) ? orig.X - _dragClusterOriginals[_draggedTile].X : 0;
                double relY = orig.Y - _dragClusterOriginals[_draggedTile].Y;

                cTile.X = clampedAnchorX + relX;
                cTile.Y = clampedAnchorY + relY;

                var container = TilesListBox?.ItemContainerGenerator.ContainerFromItem(cTile) as ContentPresenter;
                if (container != null)
                {
                    Canvas.SetLeft(container, cTile.X);
                    Canvas.SetTop(container, cTile.Y);
                }
            }

            // Calculate snapped indicator position for the whole cluster or group
            int minAllowedCol = Math.Max(0, -_clusterRelGridBounds.MinRelCol);
            int maxAllowedCol = Math.Max(minAllowedCol, maxCols - _clusterRelGridBounds.MaxRelCol);
            int anchorCol = Math.Max(0, Math.Clamp(GridPlacementService.ColFromPixel(clampedAnchorX), minAllowedCol, maxAllowedCol));

            int minAllowedRow = _isGroupDrag
                ? Math.Max(1, Math.Max(-_clusterRelGridBounds.MinRelRow, 1 - _clusterRelGridBounds.MinRelRow))
                : Math.Max(0, -_clusterRelGridBounds.MinRelRow);
            int anchorRow = Math.Max(minAllowedRow, GridPlacementService.RowFromPixel(clampedAnchorY));

            double snappedAnchorX = GridPlacementService.PixelXFromCol(anchorCol);
            double snappedAnchorY = GridPlacementService.PixelYFromRow(anchorRow);

            DropSlotIndicator.Width = Math.Max(56, _clusterRelBounds.MaxRelX - _clusterRelBounds.MinRelX);
            DropSlotIndicator.Height = Math.Max(56, _clusterRelBounds.MaxRelY - _clusterRelBounds.MinRelY);
            Canvas.SetLeft(DropSlotIndicator, snappedAnchorX + _clusterRelBounds.MinRelX);
            Canvas.SetTop(DropSlotIndicator, snappedAnchorY + _clusterRelBounds.MinRelY);
            DropSlotIndicator.Visibility = Visibility.Visible;

            if (_isGroupDrag && _draggedGroupModel != null)
            {
                // Move the group header control in real-time right along with the moving tiles!
                _draggedGroupModel.X = clampedAnchorX + _draggedGroupOffsetX;
                _draggedGroupModel.Y = Math.Max(GridPlacementService.OriginY + 8, clampedAnchorY + _draggedGroupOffsetY);

                var gContainer = GroupsListBox?.ItemContainerGenerator.ContainerFromItem(_draggedGroupModel) as ContentPresenter;
                if (gContainer != null)
                {
                    Canvas.SetLeft(gContainer, _draggedGroupModel.X);
                    Canvas.SetTop(gContainer, _draggedGroupModel.Y);
                }

                // Hide generic tile drop indicator during group drag
                DropSlotIndicator.Visibility = Visibility.Collapsed;

                int targetColIndex = Math.Max(0, GridPlacementService.GetColumnIndexFromCol(GridPlacementService.ColFromPixel(clampedAnchorX)));
                int colStartCol = GridPlacementService.GetColumnStartCol(targetColIndex);
                double colLeft = GridPlacementService.PixelXFromCol(colStartCol);
                double colWidth = GridPlacementService.GroupColWidth * GridPlacementService.GridStep - GridPlacementService.Gap;

                int targetRow = Math.Max(0, GridPlacementService.FindInsertionRow(targetColIndex, canvasMouse.Y, Groups, Tiles, _draggedGroupModel));
                double insertionY = GridPlacementService.PixelYFromRow(targetRow) - 2;

                _groupDragTargetColIndex = targetColIndex;
                _groupDragTargetRow = targetRow;

                if (GroupInsertionLine != null)
                {
                    GroupInsertionLine.Width = colWidth;
                    Canvas.SetLeft(GroupInsertionLine, colLeft);
                    Canvas.SetTop(GroupInsertionLine, Math.Max(GridPlacementService.OriginY + 6, insertionY));
                    GroupInsertionLine.Visibility = Visibility.Visible;
                }
            }
            else
            {
                if (GroupInsertionLine != null) GroupInsertionLine.Visibility = Visibility.Collapsed;
            }

            // 5. Detect Hovering over Existing Groups (to show glowing perimeter & floating badge)
            if (!_isGroupDrag && Groups.Count > 0)
            {
                UpdateGroupDropHighlight(canvasMouse, clampedAnchorX, clampedAnchorY);
            }
            else
            {
                HideGroupDropHighlight();
            }

            // 6. Live preview: auto-wrap inside group bounds when hovering
            if (_hoveredTargetGroup != null && _draggedTile != null && !_isGroupDrag)
            {
                if (_hoveredTargetGroup.IsLocked)
                {
                    DropSlotIndicator.Visibility = Visibility.Collapsed;
                }
                else
                {
                    var (wrapCol, wrapRow) = FindGroupWrapPosition(_hoveredTargetGroup, _draggedTile, anchorCol, anchorRow);
                    double wrapX = GridPlacementService.PixelXFromCol(wrapCol);
                    double wrapY = GridPlacementService.PixelYFromRow(wrapRow);

                    DropSlotIndicator.Width = _draggedTile.WidthPixels;
                    DropSlotIndicator.Height = _draggedTile.HeightPixels;
                    Canvas.SetLeft(DropSlotIndicator, wrapX);
                    Canvas.SetTop(DropSlotIndicator, wrapY);
                    DropSlotIndicator.Visibility = Visibility.Visible;
                }
            }
        }
    }

    private void OnCanvasPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        // 1. Finishing Rubber-band Marquee Selection
        if (_isRubberBanding)
        {
            _isRubberBanding = false;
            RootGrid.ReleaseMouseCapture();

            if (RubberBandBox != null)
            {
                RubberBandBox.Visibility = Visibility.Collapsed;
            }

            if (!_rubberBandHasMoved)
            {
                bool isCtrlDown = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                if (!isCtrlDown)
                {
                    ClearTileSelection();
                }
            }

            e.Handled = true;
            return;
        }

        // 2. Finishing Drag & Drop Placement
        if (_isDragging)
        {
            _isDragging = false;
            _isPotentialDrag = false;
            DropSlotIndicator.Visibility = Visibility.Collapsed;
            if (GroupInsertionLine != null) GroupInsertionLine.Visibility = Visibility.Collapsed;
            RootGrid.ReleaseMouseCapture();

            var targetGroup = _hoveredTargetGroup;
            HideGroupDropHighlight();

            // A. GROUP HEADER DRAG REORDERING
            if (_isGroupDrag && _draggedGroupModel != null)
            {
                _isGroupDrag = false;
                var movedGroup = _draggedGroupModel;
                _draggedGroupModel = null;

                var modified = GridPlacementService.InsertGroupAndResolveCollisions(
                    movedGroup,
                    _groupDragTargetColIndex,
                    _groupDragTargetRow,
                    Groups,
                    Tiles);

                AnimateModifiedTiles(modified);
                UpdateGroupHeaderPositions();
                UpdateCanvasHeight();
                SaveGroupsAndLayout();

                foreach (var cTile in _draggedCluster)
                {
                    cTile.IsBeingDragged = false;
                    var container = TilesListBox?.ItemContainerGenerator.ContainerFromItem(cTile) as ContentPresenter;
                    if (container != null) Panel.SetZIndex(container, 0);
                    var control = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, cTile));
                    control?.AnimateRelease();
                }

                if (_draggedContainer != null)
                {
                    Panel.SetZIndex(_draggedContainer, 0);
                    _draggedContainer = null;
                }

                _draggedTile = null;
                _draggedControl = null;
                _draggedCluster.Clear();
                ClearTileSelection();
                UpdateCanvasHeight();

                string postDropSnapshot = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
                if (!string.IsNullOrEmpty(_preDragLayoutSnapshot) && _preDragLayoutSnapshot != postDropSnapshot)
                {
                    _historyService.PushState(_preDragLayoutSnapshot);
                }
                _preDragLayoutSnapshot = null;

                e.Handled = true;
                return;
            }

            _isGroupDrag = false;
            _draggedGroupModel = null;

            // B. TILE / CLUSTER DRAG HANDLING
            if (_draggedTile != null)
            {
                UpdateLayoutMetrics();
                int maxCols = GridPlacementService.MaxCols;

                if (targetGroup != null)
                {
                    if (targetGroup.IsLocked)
                    {
                        // 1. LOCKED GROUP: DENY DROP WITH RED FLASH & SHAKE
                        FlashLockedGroupPerimeter(targetGroup);

                        foreach (var cTile in _draggedCluster)
                        {
                            cTile.IsBeingDragged = false;

                            if (_dragClusterOriginals.TryGetValue(cTile, out var orig))
                            {
                                cTile.Col = orig.Col;
                                cTile.Row = orig.Row;
                                cTile.X = orig.X;
                                cTile.Y = orig.Y;
                            }
                            else
                            {
                                cTile.Col = _dragOriginalCol;
                                cTile.Row = _dragOriginalRow;
                                cTile.X = GridPlacementService.PixelXFromCol(_dragOriginalCol);
                                cTile.Y = GridPlacementService.PixelYFromRow(_dragOriginalRow);
                            }

                            var container = TilesListBox?.ItemContainerGenerator.ContainerFromItem(cTile) as ContentPresenter;
                            if (container != null)
                            {
                                Canvas.SetLeft(container, cTile.X);
                                Canvas.SetTop(container, cTile.Y);
                                Panel.SetZIndex(container, 0);
                            }

                            var control = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, cTile));
                            control?.AnimateRelease();
                        }

                        if (_draggedContainer != null)
                        {
                            Panel.SetZIndex(_draggedContainer, 0);
                            _draggedContainer = null;
                        }

                        _draggedTile = null;
                        _draggedControl = null;
                        ClearTileSelection();
                        _preDragLayoutSnapshot = null;
                        e.Handled = true;
                        return;
                    }
                    else
                    {
                        // 2. UNLOCKED GROUP: place tile(s) at drop position, preserving existing tile layout
                        int dropCol = GridPlacementService.ColFromPixel(_draggedTile.X);
                        int dropRow = GridPlacementService.RowFromPixel(_draggedTile.Y);

                        foreach (var cTile in _draggedCluster)
                        {
                            cTile.Group = targetGroup.Id;
                            cTile.SectionHeader = targetGroup.Title;
                        }

                        CleanEmptyGroupsAndReflow();

                        var mod = GridPlacementService.PlaceTilesInGroup(
                            _draggedCluster,
                            targetGroup,
                            Tiles,
                            _draggedTile,
                            dropCol,
                            dropRow,
                            Groups);

                        AnimateModifiedTiles(mod);
                        UpdateGroupHeaderPositions();
                        UpdateCanvasHeight();
                    }
                }
                else
                {
                    // Dropped on canvas outside targetGroup
                    // Decision 9: A never: Loose tiles cannot occupy space inside a group's bounding box.
                    int dropCol = GridPlacementService.ColFromPixel(_draggedTile.X);
                    int dropRow = GridPlacementService.RowFromPixel(_draggedTile.Y);

                    TileGroupModel? overlappedGroup = null;
                    foreach (var g in Groups)
                    {
                        var (minC, maxC, minR, maxR) = GridPlacementService.GetGroupBoundingBox(g, Tiles);
                        if (GridPlacementService.DoTilesOverlap(dropCol, dropRow, _draggedTile.SpanX, _draggedTile.SpanY, minC, minR, maxC - minC, maxR - minR))
                        {
                            overlappedGroup = g;
                            break;
                        }
                    }

                    if (overlappedGroup != null)
                    {
                        if (overlappedGroup.IsLocked)
                        {
                            FlashLockedGroupPerimeter(overlappedGroup);
                            foreach (var cTile in _draggedCluster)
                            {
                                cTile.IsBeingDragged = false;
                                if (_dragClusterOriginals.TryGetValue(cTile, out var orig))
                                {
                                    cTile.Col = orig.Col; cTile.Row = orig.Row; cTile.X = orig.X; cTile.Y = orig.Y;
                                }
                            }
                            CleanEmptyGroupsAndReflow();
                            UpdateGroupHeaderPositions();
                        }
                        else
                        {
                            // Join the overlapped group – place tile(s) at drop position preserving existing layout
                            int dropCol2 = GridPlacementService.ColFromPixel(_draggedTile.X);
                            int dropRow2 = GridPlacementService.RowFromPixel(_draggedTile.Y);

                            foreach (var cTile in _draggedCluster)
                            {
                                cTile.Group = overlappedGroup.Id;
                                cTile.SectionHeader = overlappedGroup.Title;
                            }

                            CleanEmptyGroupsAndReflow();

                            var mod2 = GridPlacementService.PlaceTilesInGroup(
                                _draggedCluster,
                                overlappedGroup,
                                Tiles,
                                _draggedTile,
                                dropCol2,
                                dropRow2,
                                Groups);

                            AnimateModifiedTiles(mod2);
                            UpdateGroupHeaderPositions();
                            UpdateCanvasHeight();
                        }
                    }
                    else
                    {
                        // Genuine ungrouped placement
                        foreach (var cTile in _draggedCluster)
                        {
                            cTile.Group = null;
                            cTile.SectionHeader = null;
                        }
                        CleanEmptyGroupsAndReflow();

                        int targetCol = Math.Min(dropCol, Math.Max(0, maxCols - _draggedTile.SpanX));
                        int targetRow = Math.Max(0, dropRow);

                        List<TileModel> modifiedTiles;
                        if (_draggedCluster.Count > 1)
                        {
                            modifiedTiles = GridPlacementService.PlaceClusterAndResolveCollisions(
                                _draggedCluster, _draggedTile, targetCol, targetRow, _dragOriginalCol, _dragOriginalRow, maxCols, Tiles);
                        }
                        else
                        {
                            modifiedTiles = GridPlacementService.PlaceAndResolveCollisions(
                                _draggedTile, targetCol, targetRow, _dragOriginalCol, _dragOriginalRow, maxCols, Tiles);
                        }
                        AnimateModifiedTiles(modifiedTiles);
                    }
                }

                foreach (var cTile in _draggedCluster)
                {
                    cTile.IsBeingDragged = false;
                }

                UpdateCanvasHeight();
            }

            foreach (var cTile in _draggedCluster)
            {
                var container = TilesListBox?.ItemContainerGenerator.ContainerFromItem(cTile) as ContentPresenter;
                if (container != null) Panel.SetZIndex(container, 0);
                var control = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, cTile));
                control?.AnimateRelease();
            }

            if (_draggedContainer != null)
            {
                Panel.SetZIndex(_draggedContainer, 0);
                _draggedContainer = null;
            }

            _draggedTile = null;
            _draggedControl = null;
            _draggedCluster.Clear();

            UpdateExposedAddSlots();
            foreach (var g in Groups) g.IsBeingDragged = false;
            UpdateGroupHeaderPositions();
            ClearTileSelection();
            SaveGroupsAndLayout();

            string postDropSnapshot2 = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
            if (!string.IsNullOrEmpty(_preDragLayoutSnapshot) && _preDragLayoutSnapshot != postDropSnapshot2)
            {
                _historyService.PushState(_preDragLayoutSnapshot);
            }
            _preDragLayoutSnapshot = null;

            e.Handled = true;
            return;
        }

        // 3. Potential Drag that was just a click (no drag distance exceeded)
        if (_isPotentialDrag)
        {
            _isPotentialDrag = false;
            var controlToLaunch = _draggedControl;
            var clickedTile = _draggedTile;
            _draggedTile = null;
            _draggedControl = null;
            _draggedContainer = null;

            bool isCtrlDown = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

            // Normal left click on any tile launches it immediately and clears selection
            if (!isCtrlDown && controlToLaunch != null)
            {
                ClearTileSelection();
                controlToLaunch.AnimateRelease(() =>
                {
                    controlToLaunch.LaunchTile();
                    if (Settings.CloseOnLaunch)
                    {
                        HideScreen();
                    }
                });
            }
            else
            {
                controlToLaunch?.AnimateRelease();
            }
            return;
        }
    }

    public void CancelActiveDrag()
    {
        if (!_isDragging && !_isPotentialDrag && !_isRubberBanding) return;

        if (_isRubberBanding)
        {
            _isRubberBanding = false;
            if (RubberBandBox != null) RubberBandBox.Visibility = Visibility.Collapsed;
        }

        if (DropSlotIndicator != null) DropSlotIndicator.Visibility = Visibility.Collapsed;
        HideGroupDropHighlight();

        if (_preDragLayoutSnapshot != null)
        {
            var snap = LayoutHistoryService.ParseSnapshot(_preDragLayoutSnapshot);
            if (snap != null)
            {
                var dict = snap.Tiles.ToDictionary(t => t.Id);
                foreach (var t in Tiles)
                {
                    if (dict.TryGetValue(t.Id, out var orig))
                    {
                        t.Col = orig.Col;
                        t.Row = orig.Row;
                        t.X = orig.X;
                        t.Y = orig.Y;
                        t.Group = orig.Group;
                        t.SectionHeader = orig.SectionHeader;
                        var c = TilesListBox?.ItemContainerGenerator.ContainerFromItem(t) as ContentPresenter;
                        if (c != null)
                        {
                            Canvas.SetLeft(c, t.X);
                            Canvas.SetTop(c, t.Y);
                            Panel.SetZIndex(c, 0);
                        }
                    }
                }

                if (snap.Groups != null && snap.Groups.Count > 0)
                {
                    var gDict = snap.Groups.ToDictionary(g => g.Id);
                    foreach (var g in Groups)
                    {
                        if (gDict.TryGetValue(g.Id, out var origG))
                        {
                            g.Col = origG.Col;
                            g.Row = origG.Row;
                            g.X = origG.X;
                            g.Y = origG.Y;
                            var gc = GroupsListBox?.ItemContainerGenerator.ContainerFromItem(g) as ContentPresenter;
                            if (gc != null)
                            {
                                Canvas.SetLeft(gc, g.X);
                                Canvas.SetTop(gc, g.Y);
                            }
                        }
                    }
                }
            }
            _preDragLayoutSnapshot = null;
        }
        else if (_dragClusterOriginals.Count > 0)
        {
            foreach (var kvp in _dragClusterOriginals)
            {
                var t = kvp.Key;
                var orig = kvp.Value;
                t.X = orig.X;
                t.Y = orig.Y;
                t.Col = orig.Col;
                t.Row = orig.Row;
                var c = TilesListBox?.ItemContainerGenerator.ContainerFromItem(t) as ContentPresenter;
                if (c != null)
                {
                    Canvas.SetLeft(c, t.X);
                    Canvas.SetTop(c, t.Y);
                    Panel.SetZIndex(c, 0);
                }
            }
        }

        foreach (var cTile in _draggedCluster)
        {
            cTile.IsBeingDragged = false;
            var control = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, cTile));
            control?.AnimateRelease();
            var c = TilesListBox?.ItemContainerGenerator.ContainerFromItem(cTile) as ContentPresenter;
            if (c != null) Panel.SetZIndex(c, 0);
        }

        if (_draggedTile != null)
        {
            _draggedTile.IsBeingDragged = false;
            _draggedControl?.AnimateRelease();
            if (_draggedContainer != null)
            {
                Panel.SetZIndex(_draggedContainer, 0);
                _draggedContainer = null;
            }
            _draggedTile = null;
            _draggedControl = null;
        }

        foreach (var g in Groups)
        {
            g.IsBeingDragged = false;
        }

        _draggedCluster.Clear();
        _dragClusterOriginals.Clear();
        _isDragging = false;
        _isPotentialDrag = false;
        _isGroupDrag = false;
        _draggedGroupModel = null;

        UpdateGroupHeaderPositions();
        ClearAllAmbientReveals();

        try { RootGrid.ReleaseMouseCapture(); } catch { }
    }

    private void OnCanvasMouseLeave(object sender, MouseEventArgs e)
    {
        if (_isRubberBanding)
        {
            _isRubberBanding = false;
            if (RubberBandBox != null) RubberBandBox.Visibility = Visibility.Collapsed;
            RootGrid.ReleaseMouseCapture();
        }

        if (_isPotentialDrag && !_isDragging)
        {
            foreach (var cTile in _draggedCluster)
            {
                var control = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, cTile));
                control?.AnimateRelease();
            }
            _draggedControl?.AnimateRelease();
            _draggedControl = null;
            _isPotentialDrag = false;
        }
        _isGroupDrag = false;
        _draggedGroupModel = null;
        HideGroupDropHighlight();
        ClearAllAmbientReveals();
    }

    private void UpdateAmbientReveal(Point mouseOnCanvas)
    {
        if (Presentation.Controls.TileControl.ActiveTiles.Count == 0) return;

        foreach (var control in Presentation.Controls.TileControl.ActiveTiles)
        {
            if (control.DataContext is TileModel tile)
            {
                double right = tile.X + tile.WidthPixels;
                double bottom = tile.Y + tile.HeightPixels;

                double dx = Math.Max(0, Math.Max(tile.X - mouseOnCanvas.X, mouseOnCanvas.X - right));
                double dy = Math.Max(0, Math.Max(tile.Y - mouseOnCanvas.Y, mouseOnCanvas.Y - bottom));
                double distance = Math.Sqrt(dx * dx + dy * dy);

                if (distance <= 160)
                {
                    control.UpdateAmbientReveal(new Point(mouseOnCanvas.X - tile.X, mouseOnCanvas.Y - tile.Y), distance);
                }
                else
                {
                    control.ClearAmbientReveal();
                }
            }
        }
    }

    private void ClearAllAmbientReveals()
    {
        foreach (var control in Presentation.Controls.TileControl.ActiveTiles)
        {
            control.ClearAmbientReveal();
        }
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T parent) return parent;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private bool IsInteractive(DependencyObject? obj)
    {
        while (obj != null && obj != this)
        {
            if (obj is Presentation.Controls.TileControl ||
                obj is System.Windows.Controls.Button ||
                obj is System.Windows.Controls.TextBox ||
                obj is ContextMenu ||
                obj is System.Windows.Controls.MenuItem ||
                (obj is Border b && b.ToolTip != null))
            {
                return true;
            }
            obj = VisualTreeHelper.GetParent(obj);
        }
        return false;
    }

    private void OnTileActivated(object sender, RoutedEventArgs e)
    {
        if (Settings.CloseOnLaunch)
        {
            HideScreen();
        }
    }

    public void BatchResizeSelectedTiles(int newSpanX, int newSpanY, TileModel anchorTile)
    {
        List<TileModel> targets;
        if (anchorTile.IsSelected && SelectedTiles.Count > 1)
        {
            targets = SelectedTiles.ToList();
        }
        else
        {
            targets = new List<TileModel> { anchorTile };
        }

        // If all selected tiles are already the desired size, do nothing
        if (targets.All(t => t.SpanX == newSpanX && t.SpanY == newSpanY))
        {
            return;
        }

        string preModify = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);

        // Capture previous spans for animation
        var oldSpans = targets.ToDictionary(t => t, t => (t.SpanX, t.SpanY));

        // Elevate resizing tiles so they morph on top of sliding neighbors
        foreach (var t in targets)
        {
            var container = TilesListBox?.ItemContainerGenerator.ContainerFromItem(t) as ContentPresenter;
            if (container != null)
            {
                Panel.SetZIndex(container, 50);
                Dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(260);
                    Panel.SetZIndex(container, 0);
                });
            }
        }

        UpdateLayoutMetrics();
        int maxCols = GridPlacementService.MaxCols;

        // Resolve collision using topological batch expansion
        var modified = GridPlacementService.ResolveBatchResizeExpansion(
            targets,
            newSpanX,
            newSpanY,
            maxCols,
            Tiles);

        // Trigger resize animations across all resizing tile controls
        foreach (var t in targets)
        {
            var (oldX, oldY) = oldSpans[t];
            var control = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, t));
            control?.AnimateResize(oldX, oldY, newSpanX, newSpanY);
        }

        // Animate any displaced neighbor tiles (and self if clamped/shifted)
        AnimateModifiedTiles(modified);

        DropSlotIndicator.Visibility = Visibility.Collapsed;
        UpdateCanvasHeight();
        UpdateExposedAddSlots();
        UpdateGroupHeaderPositions();
        SaveGroupsAndLayout();

        string postModify = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
        if (preModify != postModify)
        {
            _historyService.PushState(preModify);
        }
    }

    public void BatchStyleSelectedTiles(string newStyle, TileModel anchorTile)
    {
        List<TileModel> targets;
        if (anchorTile.IsSelected && SelectedTiles.Count > 1)
        {
            targets = SelectedTiles.ToList();
        }
        else
        {
            targets = new List<TileModel> { anchorTile };
        }

        string preModify = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);

        foreach (var t in targets)
        {
            t.TileStyle = newStyle;
            if (string.Equals(newStyle, "Colourful", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(t.AccentColor))
                {
                    t.AccentColor = ColorExtractorService.ExtractAccentColor(t.IconPath);
                }
            }

            var control = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, t));
            control?.ApplyTileStyle(animate: true);
        }

        SaveGroupsAndLayout();

        string postModify = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
        if (preModify != postModify)
        {
            _historyService.PushState(preModify);
        }
    }

    public void BatchUnpinSelectedTiles(TileModel anchorTile)
    {
        List<TileModel> targets;
        if (anchorTile.IsSelected && SelectedTiles.Count > 1)
        {
            targets = SelectedTiles.ToList();
        }
        else
        {
            targets = new List<TileModel> { anchorTile };
        }

        string preUnpin = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
        _historyService.PushState(preUnpin);

        foreach (var t in targets)
        {
            Tiles.Remove(t);
        }

        CleanEmptyGroupsAndReflow();
        UpdateGroupHeaderPositions();
        SaveGroupsAndLayout();
        UpdateCanvasHeight();
        UpdateExposedAddSlots();
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

    #region Named Groups & Sections Engine

    private void DiscoverGroupsFromTiles()
    {
        if (Groups.Count > 0) return;

        var grouped = Tiles
            .Where(t => !string.IsNullOrWhiteSpace(t.SectionHeader) || !string.IsNullOrWhiteSpace(t.Group))
            .GroupBy(t => !string.IsNullOrWhiteSpace(t.Group) ? t.Group : t.SectionHeader!)
            .ToList();

        foreach (var grp in grouped)
        {
            string title = grp.First().SectionHeader ?? "Group";
            string id = grp.First().Group ?? Guid.NewGuid().ToString("N");

            var groupModel = new TileGroupModel
            {
                Id = id,
                Title = title
            };

            foreach (var t in grp)
            {
                t.Group = id;
                t.SectionHeader = title;
            }

            Groups.Add(groupModel);
        }

        if (Groups.Count > 0)
        {
            SaveGroupsAndLayout();
        }
    }

    private void EnsureGroupIndices()
    {
        if (Groups.Count == 0) return;

        var groupedByCol = Groups.GroupBy(g => g.ColumnIndex).ToList();
        foreach (var colGroup in groupedByCol)
        {
            var ordered = colGroup.OrderBy(g => g.OrderIndex).ThenBy(g => g.Row).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                ordered[i].OrderIndex = i;
            }
        }
    }

    private void MigrateGroupColumnOffsets()
    {
        if (Groups.Count == 0) return;

        bool changed = false;
        foreach (var group in Groups)
        {
            int expectedCol = GridPlacementService.GetColumnStartCol(group.ColumnIndex);
            if (group.Col != expectedCol)
            {
                int delta = expectedCol - group.Col;
                group.Col = expectedCol;
                group.X = GridPlacementService.PixelXFromCol(expectedCol);
                changed = true;

                var members = Tiles.Where(t => t.Group == group.Id).ToList();
                foreach (var t in members)
                {
                    t.Col += delta;
                    t.X = GridPlacementService.PixelXFromCol(t.Col);
                }
            }
        }

        if (changed)
        {
            SaveGroupsAndLayout();
        }
    }

    public void CleanEmptyGroupsAndReflow()
    {
        bool anyCleaned = GridPlacementService.CleanEmptyGroups(Groups, Tiles);
        if (anyCleaned)
        {
            UpdateGroupHeaderPositions();
            SaveGroupsAndLayout();
        }
    }

    private void UpdateGroupDropHighlight(Point mousePos, double anchorX, double anchorY)
    {
        TileGroupModel? targetGroup = null;
        Rect bestGroupRect = Rect.Empty;

        foreach (var group in Groups)
        {
            var members = Tiles.Where(t => t.Group == group.Id && !_draggedCluster.Contains(t)).ToList();
            if (members.Count == 0 && string.IsNullOrWhiteSpace(group.Title)) continue;

            // Calculate the outer bounding perimeter of the group across all its member tiles
            double blockWidth = GroupColWidth * GridPlacementService.GridStep - GridPlacementService.Gap;
            double minX = GridPlacementService.PixelXFromCol(group.Col);
            double maxX = minX + blockWidth;
            double minY = group.Y;
            double maxY = members.Count > 0 ? members.Max(t => t.Y + t.HeightPixels) : group.Y + 120;

            // Generous outer padding around group perimeter so moving past column limit keeps group active
            Rect groupDetectRect = new Rect(minX - 16, minY - 10, blockWidth + 64, (maxY - minY) + 40);

            // Check if mouse cursor OR center of the dragged cluster intersects the group bounds
            double clusterCenterX = anchorX + (_clusterRelBounds.MinRelX + _clusterRelBounds.MaxRelX) / 2.0;
            double clusterCenterY = anchorY + (_clusterRelBounds.MinRelY + _clusterRelBounds.MaxRelY) / 2.0;
            Point clusterCenter = new Point(clusterCenterX, clusterCenterY);

            if (groupDetectRect.Contains(mousePos) || groupDetectRect.Contains(clusterCenter))
            {
                targetGroup = group;

                double visualMaxY = maxY;
                if (_draggedTile != null && !group.IsLocked)
                {
                    int rawCol = GridPlacementService.ColFromPixel(anchorX);
                    int rawRow = GridPlacementService.RowFromPixel(anchorY);
                    var (_, wrapRow) = FindGroupWrapPosition(group, _draggedTile, rawCol, rawRow);
                    double previewBottom = GridPlacementService.PixelYFromRow(wrapRow + _draggedTile.SpanY);
                    visualMaxY = Math.Max(maxY, previewBottom);
                }

                bestGroupRect = new Rect(minX - 6, minY - 6, blockWidth + 12, (visualMaxY - minY) + 12);
                break;
            }
        }

        if (targetGroup != null)
        {
            _hoveredTargetGroup = targetGroup;
            bool isLocked = targetGroup.IsLocked;

            Color groupColor = isLocked
                ? Color.FromRgb(0xFF, 0x43, 0x43)
                : (Color)ColorConverter.ConvertFromString("#60CDFF");

            if (!isLocked)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(targetGroup.HeaderColor))
                    {
                        groupColor = (Color)ColorConverter.ConvertFromString(targetGroup.HeaderColor);
                    }
                }
                catch { }
            }

            var solidBrush = new SolidColorBrush(groupColor);
            var tintBrush = new SolidColorBrush(Color.FromArgb(isLocked ? (byte)36 : (byte)22, groupColor.R, groupColor.G, groupColor.B));

            if (GroupDropPerimeterBorder != null)
            {
                Canvas.SetLeft(GroupDropPerimeterBorder, bestGroupRect.Left);
                Canvas.SetTop(GroupDropPerimeterBorder, bestGroupRect.Top);
                GroupDropPerimeterBorder.Width = bestGroupRect.Width;
                GroupDropPerimeterBorder.Height = bestGroupRect.Height;
                GroupDropPerimeterBorder.BorderBrush = solidBrush;
                GroupDropPerimeterBorder.Background = tintBrush;
                if (GroupDropGlowEffect != null)
                {
                    GroupDropGlowEffect.Color = groupColor;
                    GroupDropGlowEffect.Opacity = isLocked ? 0.85 : 0.65;
                }
                GroupDropPerimeterBorder.Opacity = 1.0;
                GroupDropPerimeterBorder.Visibility = Visibility.Visible;
            }

            if (GroupDropFloatingBadge != null)
            {
                if (GroupDropBadgeIcon != null)
                {
                    GroupDropBadgeIcon.Foreground = solidBrush;
                    GroupDropBadgeIcon.Symbol = isLocked
                        ? Wpf.Ui.Controls.SymbolRegular.LockClosed24
                        : (_draggedCluster.Count > 0 && _draggedCluster.All(t => t.Group == targetGroup.Id)
                            ? Wpf.Ui.Controls.SymbolRegular.ReOrder24
                            : Wpf.Ui.Controls.SymbolRegular.Add24);
                }

                if (GroupDropBadgeText != null)
                {
                    GroupDropBadgeText.Text = isLocked
                        ? "🔒 Locked (Drop Denied)"
                        : (_draggedCluster.Count > 0 && _draggedCluster.All(t => t.Group == targetGroup.Id)
                            ? $"Reorder in {targetGroup.Title}"
                            : $"Add to {targetGroup.Title}");
                }

                Canvas.SetLeft(GroupDropFloatingBadge, mousePos.X + 16);
                Canvas.SetTop(GroupDropFloatingBadge, Math.Max(10, mousePos.Y - 38));
                GroupDropFloatingBadge.Visibility = Visibility.Visible;
            }
        }
        else
        {
            HideGroupDropHighlight();
        }
    }

    private void HideGroupDropHighlight()
    {
        _hoveredTargetGroup = null;
        if (GroupDropPerimeterBorder != null) GroupDropPerimeterBorder.Visibility = Visibility.Collapsed;
        if (GroupDropFloatingBadge != null) GroupDropFloatingBadge.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Calculates the snapped (col, row) position for a tile within a 4-column group block.
    /// Clamps within the 4-column group block, and auto-wraps down to the next row if dragged past the right edge.
    /// </summary>
    private (int Col, int Row) FindGroupWrapPosition(TileGroupModel group, TileModel tile, int rawCol, int rawRow)
    {
        int groupMinCol = group.Col;
        int groupMaxCol = group.Col + GroupColWidth; // Exactly 4 units wide
        int minRow = group.Row + 1; // Row immediately below the group header
        int baseRow = Math.Max(minRow, rawRow);

        int col;
        int row;

        // Auto-wrap: if dragged past the right edge (column limit) of the group,
        // bump down to the next row and wrap to the beginning of the group
        if (rawCol + tile.SpanX > groupMaxCol)
        {
            col = groupMinCol;
            row = baseRow + (tile.SpanY > 1 ? tile.SpanY : 1);
        }
        else
        {
            col = Math.Clamp(rawCol, groupMinCol, Math.Max(groupMinCol, groupMaxCol - tile.SpanX));
            row = baseRow;
        }

        return (col, row);
    }

    /// <summary>
    /// Shows a red glowing perimeter border with a horizontal shake and smooth fade out
    /// when a user attempts to drop a tile into a locked group.
    /// </summary>
    private void FlashLockedGroupPerimeter(TileGroupModel group)
    {
        if (GroupDropPerimeterBorder == null) return;

        var members = Tiles.Where(t => t.Group == group.Id).ToList();
        double blockWidth = GroupColWidth * GridPlacementService.GridStep - GridPlacementService.Gap;
        double minX = GridPlacementService.PixelXFromCol(group.Col);
        double minY = group.Y;
        double maxY = members.Count > 0 ? members.Max(t => t.Y + t.HeightPixels) : group.Y + 120;

        Canvas.SetLeft(GroupDropPerimeterBorder, minX - 6);
        Canvas.SetTop(GroupDropPerimeterBorder, minY - 6);
        GroupDropPerimeterBorder.Width = blockWidth + 12;
        GroupDropPerimeterBorder.Height = (maxY - minY) + 12;

        var redColor = Color.FromRgb(0xFF, 0x43, 0x43);
        GroupDropPerimeterBorder.BorderBrush = new SolidColorBrush(redColor);
        GroupDropPerimeterBorder.Background = new SolidColorBrush(Color.FromArgb(45, redColor.R, redColor.G, redColor.B));
        if (GroupDropGlowEffect != null)
        {
            GroupDropGlowEffect.Color = redColor;
            GroupDropGlowEffect.Opacity = 0.95;
        }

        GroupDropPerimeterBorder.Opacity = 1.0;
        GroupDropPerimeterBorder.Visibility = Visibility.Visible;

        var translate = GroupDropPerimeterTranslate ?? new TranslateTransform();
        GroupDropPerimeterBorder.RenderTransform = translate;

        // 300ms horizontal shake animation
        var shakeAnimation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(300)
        };
        shakeAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))));
        shakeAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(-6, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(50))));
        shakeAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(6, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100))));
        shakeAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(-4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
        shakeAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200))));
        shakeAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(-2, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250))));
        shakeAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300))));

        // Smooth fade out over 350ms (finishing at ~650ms)
        var fadeAnimation = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            BeginTime = TimeSpan.FromMilliseconds(300),
            Duration = TimeSpan.FromMilliseconds(350),
            FillBehavior = FillBehavior.Stop
        };

        fadeAnimation.Completed += (s, e) =>
        {
            GroupDropPerimeterBorder.Visibility = Visibility.Collapsed;
            GroupDropPerimeterBorder.Opacity = 1.0;
            translate.X = 0;
        };

        translate.BeginAnimation(TranslateTransform.XProperty, shakeAnimation);
        GroupDropPerimeterBorder.BeginAnimation(UIElement.OpacityProperty, fadeAnimation);
    }

    /// <summary>
    /// Migrates existing group layouts by reflowing all group member tiles into 4-column blocks.
    /// </summary>
    private void ReflowGroupsToFixedColumnWidth()
    {
        bool changed = false;

        foreach (var group in Groups)
        {
            var members = Tiles.Where(t => t.Group == group.Id).ToList();
            if (members.Count == 0) continue;

            int groupOriginCol = group.Col;
            int groupMaxCol = groupOriginCol + GroupColWidth;

            // Check if any member tile exceeds the 4-column group width or starts before groupOriginCol
            bool needsReflow = members.Any(t => t.Col < groupOriginCol || (t.Col + t.SpanX) > groupMaxCol);
            if (!needsReflow) continue;

            // Sort members in reading order (row first, then col)
            var sortedMembers = members
                .OrderBy(t => t.Row)
                .ThenBy(t => t.Col)
                .ToList();

            // Reflow tiles into rows within the 4-unit column constraint
            int startRow = Math.Max(1, group.Row + 1);
            int curCol = groupOriginCol;
            int curRow = startRow;
            int maxRowInCurrentLine = 1;

            foreach (var t in sortedMembers)
            {
                // If tile doesn't fit horizontally on current row, wrap to next line
                if (curCol + t.SpanX > groupMaxCol)
                {
                    curCol = groupOriginCol;
                    curRow += maxRowInCurrentLine;
                    maxRowInCurrentLine = 1;
                }

                t.Col = curCol;
                t.Row = curRow;
                t.X = GridPlacementService.PixelXFromCol(curCol);
                t.Y = GridPlacementService.PixelYFromRow(curRow);

                curCol += t.SpanX;
                maxRowInCurrentLine = Math.Max(maxRowInCurrentLine, t.SpanY);
            }

            changed = true;
        }

        if (changed)
        {
            StorageService.SaveLayout(Tiles);
        }
    }

    public void EnsureGroupsHaveHeaderSpace()
    {
        UpdateGroupHeaderPositions();
    }

    public void UpdateGroupHeaderPositions()
    {
        foreach (var group in Groups)
        {
            group.X = GridPlacementService.PixelXFromCol(group.Col);
            group.Y = GridPlacementService.PixelYFromRow(group.Row) + 8;

            var container = GroupsListBox?.ItemContainerGenerator.ContainerFromItem(group) as ContentPresenter;
            if (container != null)
            {
                Canvas.SetLeft(container, group.X);
                Canvas.SetTop(container, group.Y);
            }
        }
    }

    public void SaveGroupsAndLayout()
    {
        StorageService.SaveLayout(Tiles);
        StorageService.SaveGroups(Groups);
        UpdateCanvasHeight();
    }

    public void CreateGroupFromSelectedTiles(TileModel anchorTile)
    {
        List<TileModel> targets;
        if (anchorTile.IsSelected && SelectedTiles.Count > 1)
        {
            targets = SelectedTiles.ToList();
        }
        else
        {
            targets = new List<TileModel> { anchorTile };
        }

        string pre = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);

        string newGroupId = Guid.NewGuid().ToString("N");
        string defaultTitle = "New Section";

        int minCol = targets.Min(t => GridPlacementService.ColFromPixel(t.X));
        int targetColIndex = GridPlacementService.GetColumnIndexFromCol(minCol);
        int targetColStart = GridPlacementService.GetColumnStartCol(targetColIndex);
        
        int minRow = targets.Min(t => GridPlacementService.RowFromPixel(t.Y));
        int targetRow = Math.Max(0, minRow - 1);

        int nextOrder = Groups.Where(g => g.ColumnIndex == targetColIndex)
                              .Select(g => g.OrderIndex)
                              .DefaultIfEmpty(-1)
                              .Max() + 1;

        var group = new TileGroupModel
        {
            Id = newGroupId,
            Title = defaultTitle,
            ColumnIndex = targetColIndex,
            OrderIndex = nextOrder,
            Col = targetColStart,
            Row = targetRow,
            IsEditing = true
        };

        foreach (var t in targets)
        {
            t.Group = newGroupId;
            t.SectionHeader = defaultTitle;
        }

        Groups.Add(group);

        var modified = GridPlacementService.InsertGroupAndResolveCollisions(group, targetColIndex, targetRow, Groups, Tiles);
        AnimateModifiedTiles(modified);
        UpdateGroupHeaderPositions();
        SaveGroupsAndLayout();
        UpdateExposedAddSlots();

        _historyService.PushState(pre);
    }

    public void AddTilesToExistingGroup(IList<TileModel> incomingTiles, TileGroupModel targetGroup)
    {
        if (incomingTiles == null || incomingTiles.Count == 0 || targetGroup == null) return;

        if (targetGroup.IsLocked)
        {
            FlashLockedGroupPerimeter(targetGroup);
            return;
        }

        var tilesToAdd = incomingTiles.Where(t => t.Group != targetGroup.Id).ToList();
        if (tilesToAdd.Count == 0) return;

        string pre = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);

        var modified = GridPlacementService.PlaceTilesInGroup(
            tilesToAdd,
            targetGroup,
            Tiles,
            anchorTile: null,
            dropAnchorCol: targetGroup.Col,
            dropAnchorRow: targetGroup.Row + 1,
            groups: Groups);

        ClearTileSelection();

        AnimateModifiedTiles(modified);
        UpdateGroupHeaderPositions();
        SaveGroupsAndLayout();
        UpdateExposedAddSlots();
        UpdateCanvasHeight();

        _historyService.PushState(pre);
    }

    public void SetGroupColor(TileGroupModel group, string hex)
    {
        string pre = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
        group.HeaderColor = hex;
        SaveGroupsAndLayout();
        _historyService.PushState(pre);
    }

    public void UngroupTiles(TileGroupModel group)
    {
        string pre = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);

        var memberTiles = Tiles.Where(t => t.Group == group.Id).ToList();
        foreach (var t in memberTiles)
        {
            t.Group = null;
            t.SectionHeader = null;
        }

        Groups.Remove(group);

        UpdateGroupHeaderPositions();
        SaveGroupsAndLayout();
        UpdateExposedAddSlots();
        _historyService.PushState(pre);
    }

    public void DeleteGroupAndTiles(TileGroupModel group)
    {
        string pre = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
        var memberTiles = Tiles.Where(t => t.Group == group.Id).ToList();
        foreach (var t in memberTiles)
        {
            Tiles.Remove(t);
        }
        int colIdx = group.ColumnIndex;
        Groups.Remove(group);

        var modifiedTiles = GridPlacementService.ReflowColumnGroups(colIdx, Groups, Tiles);
        AnimateModifiedTiles(modifiedTiles);
        UpdateGroupHeaderPositions();
        SaveGroupsAndLayout();
        _historyService.PushState(pre);
    }

    public void StartGroupDrag(TileGroupModel group, MouseEventArgs e)
    {
        var members = Tiles.Where(t => t.Group == group.Id).ToList();
        if (members.Count == 0) return;

        ClearTileSelection();
        foreach (var m in members)
        {
            m.IsSelected = true;
        }

        var anchor = members.OrderBy(t => t.Row).ThenBy(t => t.Col).First();

        _preDragLayoutSnapshot = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
        _draggedTile = anchor;
        _draggedControl = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, anchor));
        _draggedContainer = TilesListBox?.ItemContainerGenerator.ContainerFromItem(anchor) as ContentPresenter;
        _dragStartPoint = e.GetPosition(this);

        Point canvasMouse = TilesListBox != null ? e.GetPosition(TilesListBox) : e.GetPosition(this);
        _dragOriginalCol = GridPlacementService.ColFromPixel(anchor.X);
        _dragOriginalRow = GridPlacementService.RowFromPixel(anchor.Y);
        _dragOffsetX = canvasMouse.X - anchor.X;
        _dragOffsetY = canvasMouse.Y - anchor.Y;
        _dragBeganWithSelection = true;

        _draggedCluster = members;
        _dragClusterOriginals.Clear();

        double minRelX = 0, maxRelX = anchor.WidthPixels, minRelY = 0, maxRelY = anchor.HeightPixels;
        int minRelCol = 0, maxRelCol = anchor.SpanX, minRelRow = 0, maxRelRow = anchor.SpanY;

        foreach (var cTile in _draggedCluster)
        {
            cTile.IsBeingDragged = true;
            var control = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, cTile));
            control?.AnimateElevationLift();

            var container = TilesListBox?.ItemContainerGenerator.ContainerFromItem(cTile) as ContentPresenter;
            if (container != null)
            {
                Panel.SetZIndex(container, 9999);
            }

            int cCol = GridPlacementService.ColFromPixel(cTile.X);
            int cRow = GridPlacementService.RowFromPixel(cTile.Y);
            _dragClusterOriginals[cTile] = (cTile.X, cTile.Y, cCol, cRow);

            double relX = cTile.X - anchor.X;
            double relY = cTile.Y - anchor.Y;
            int relCol = cCol - _dragOriginalCol;
            int relRow = cRow - _dragOriginalRow;

            minRelX = Math.Min(minRelX, relX);
            maxRelX = Math.Max(maxRelX, relX + cTile.WidthPixels);
            minRelY = Math.Min(minRelY, relY);
            maxRelY = Math.Max(maxRelY, relY + cTile.HeightPixels);

            minRelCol = Math.Min(minRelCol, relCol);
            maxRelCol = Math.Max(maxRelCol, relCol + cTile.SpanX);
            minRelRow = Math.Min(minRelRow, relRow);
            maxRelRow = Math.Max(maxRelRow, relRow + cTile.SpanY);
        }

        _clusterRelBounds = (minRelX, maxRelX, minRelY, maxRelY);
        _clusterRelGridBounds = (minRelCol, maxRelCol, minRelRow, maxRelRow);

        _isPotentialDrag = false;
        _isDragging = true;
        _isGroupDrag = true;
        _draggedGroupModel = group;
        _draggedGroupOffsetX = group.X - anchor.X;
        _draggedGroupOffsetY = group.Y - anchor.Y;
        group.IsBeingDragged = true;

        RootGrid.CaptureMouse();
        DropSlotIndicator.Visibility = Visibility.Collapsed;
        if (GroupInsertionLine != null)
        {
            GroupInsertionLine.Width = GridPlacementService.GroupColWidth * GridPlacementService.GridStep - GridPlacementService.Gap;
            Canvas.SetLeft(GroupInsertionLine, GridPlacementService.PixelXFromCol(group.Col));
            Canvas.SetTop(GroupInsertionLine, group.Y);
            GroupInsertionLine.Visibility = Visibility.Visible;
        }
    }



    #endregion

    private void OnCreateGroupCanvasClick(object sender, RoutedEventArgs e)
    {
        CreateGroupAtPosition(_canvasRightClickPoint);
    }

    public void CreateGroupAtPosition(Point canvasPoint)
    {
        string pre = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);

        int col = GridPlacementService.ColFromPixel(canvasPoint.X);
        int targetColIndex = GridPlacementService.GetColumnIndexFromCol(col);
        int targetColStart = GridPlacementService.GetColumnStartCol(targetColIndex);
        int targetRow = GridPlacementService.RowFromPixel(canvasPoint.Y);

        int nextOrder = Groups.Where(g => g.ColumnIndex == targetColIndex)
                              .Select(g => g.OrderIndex)
                              .DefaultIfEmpty(-1)
                              .Max() + 1;

        var group = new TileGroupModel
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = "New Section",
            ColumnIndex = targetColIndex,
            OrderIndex = nextOrder,
            Col = targetColStart,
            Row = targetRow,
            IsEditing = true
        };

        Groups.Add(group);

        var modified = GridPlacementService.InsertGroupAndResolveCollisions(group, targetColIndex, targetRow, Groups, Tiles);
        AnimateModifiedTiles(modified);
        UpdateGroupHeaderPositions();
        SaveGroupsAndLayout();
        UpdateExposedAddSlots();

        _historyService.PushState(pre);
    }

    private void OnAddTileClick(object sender, RoutedEventArgs e)
    {
        PromptAddTile();
    }

    public void UpdateExposedAddSlots()
    {
        // Exposed [+] slot button removed completely per user request
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

            if (dialog.ShowDialog() == true && dialog.FileNames.Length > 0)
            {
                string preAdd = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
                _historyService.PushState(preAdd);

                foreach (string file in dialog.FileNames)
                {
                    AddFileAsTile(file, _canvasRightClickPoint.X, _canvasRightClickPoint.Y, recordHistory: false);
                }
            }
        }
        finally
        {
            IsDialogOpen = false;
        }
    }

    public void AddFileAsTile(string filePath, double x = 0, double y = 0, bool recordHistory = true)
    {
        if (File.Exists(filePath) || Directory.Exists(filePath))
        {
            if (recordHistory)
            {
                string preAdd = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
                _historyService.PushState(preAdd);
            }
            string title = Path.GetFileNameWithoutExtension(filePath);
            string? iconPath = IconExtractorService.ExtractAndCacheIcon(filePath);

            double viewportWidth = ContentScrollViewer?.ActualWidth > 0 
                ? ContentScrollViewer.ActualWidth 
                : (Width > 0 ? Width : 1920);
            int maxCols = GridPlacementService.GetMaxCols(viewportWidth);

            int col = x > 0 ? GridPlacementService.ColFromPixel(x) : 0;
            int row = y > 0 ? GridPlacementService.RowFromPixel(y) : 0;

            TileGroupModel? targetGroup = null;
            if (x > 0 || y > 0)
            {
                foreach (var g in Groups)
                {
                    var (minC, maxC, minR, maxR) = GridPlacementService.GetGroupBoundingBox(g, Tiles);
                    if (col >= minC && col < maxC && row >= minR && row <= maxR)
                    {
                        targetGroup = g;
                        break;
                    }
                }
            }

            if (targetGroup != null)
            {
                var tile = new TileModel
                {
                    Title = title,
                    TargetPath = filePath,
                    IconPath = iconPath,
                    TileType = TileType.App,
                    SpanX = 2,
                    SpanY = 2,
                    Group = targetGroup.Id,
                    SectionHeader = targetGroup.Title
                };

                // Place tile at click position within the group (preserves existing tile layout)
                int clickRelCol = col - targetGroup.Col;
                int clickRelRow = row - (targetGroup.Row + 1);
                var existingGroupTiles = Tiles.Where(t => t.Group == targetGroup.Id).ToList();
                var (slotCol, slotRow) = GridPlacementService.FindFreeSlotInGroup(
                    targetGroup, clickRelCol, clickRelRow, tile.SpanX, tile.SpanY, existingGroupTiles);
                tile.Col = slotCol;
                tile.Row = slotRow;
                tile.X = GridPlacementService.PixelXFromCol(slotCol);
                tile.Y = GridPlacementService.PixelYFromRow(slotRow);

                Tiles.Add(tile);
                var mod = GridPlacementService.PlaceTileInGroup(
                    tile, slotCol, slotRow, slotCol, slotRow, targetGroup, Tiles);
                var pushed = GridPlacementService.PushLowerGroupsDown(targetGroup, Groups, Tiles);
                foreach (var pt in pushed)
                {
                    if (!mod.Contains(pt)) mod.Add(pt);
                }
                AnimateModifiedTiles(mod);
                UpdateGroupHeaderPositions();
                StorageService.SaveLayout(Tiles);
                SaveGroupsAndLayout();
                UpdateCanvasHeight();
                UpdateExposedAddSlots();
                return;
            }

            var (freeCol, freeRow) = GridPlacementService.FindNearestAvailableSlot(col, row, 2, 2, Tiles, null, maxCols);

            var tileUngrouped = new TileModel
            {
                Title = title,
                TargetPath = filePath,
                IconPath = iconPath,
                TileType = TileType.App,
                SpanX = 2,
                SpanY = 2,
                X = GridPlacementService.PixelXFromCol(freeCol),
                Y = GridPlacementService.PixelYFromRow(freeRow)
            };

            Tiles.Add(tileUngrouped);
            StorageService.SaveLayout(Tiles);
            UpdateCanvasHeight();
            UpdateExposedAddSlots();
        }
    }

    #region Canvas Context Menu & Modular Catalog Methods

    private void OnCanvasPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
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
                // If clicked tile is already part of a multi-tile selection, keep the group!
                // If not, clear others and select this tile (like Windows Explorer)
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

            // Right-clicked empty canvas: deselect tiles unless Ctrl is held
            bool isCtrlDown = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            if (!isCtrlDown)
            {
                ClearTileSelection();
            }
        }
    }

    private void OnCanvasContextMenuOpened(object sender, RoutedEventArgs e)
    {
        // Pre-heat apps catalog in background when context menu opens so hovering over "Apps" is instantaneous
        if (!_isAppsLoaded && !_isLoadingApps)
        {
            _ = LoadAppsSubmenuAsync();
        }
    }

    private async void OnAppsSubmenuOpened(object sender, RoutedEventArgs e)
    {
        await LoadAppsSubmenuAsync();
    }

    private async Task LoadAppsSubmenuAsync()
    {
        if (_isAppsLoaded || _isLoadingApps) return;
        _isLoadingApps = true;

        try
        {
            var provider = CatalogService.GetProvider("installed_apps");
            if (provider != null)
            {
                var items = await provider.GetItemsAsync();

                _ = Task.Run(() => CatalogItemModel.PrewarmMemoryCache(items));

                AppsMenuItem.Items.Clear();
                foreach (var item in items)
                {
                    var menuItem = CreateCatalogMenuItem(item);
                    AppsMenuItem.Items.Add(menuItem);
                }
                _isAppsLoaded = true;
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
                UpdateAppsMenuItems(freshApps);
            }
        }, DispatcherPriority.Background);
    }

    private void TriggerBackgroundAppsCatalogRefresh()
    {
        // Throttle COM enumeration queries to at most once every 10 seconds
        if ((DateTime.UtcNow - _lastAppsRefreshTime).TotalSeconds < 10)
        {
            return;
        }

        _lastAppsRefreshTime = DateTime.UtcNow;

        // Multi-threaded background execution (never blocks UI or animations)
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
                        UpdateAppsMenuItems(freshItems);
                    }, DispatcherPriority.Background);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppsCatalog] Background refresh error: {ex.Message}");
            }
        });
    }

    private void UpdateAppsMenuItems(IReadOnlyList<CatalogItemModel> freshItems)
    {
        if (AppsMenuItem == null) return;

        bool hasChanged = AppsMenuItem.Items.Count != freshItems.Count;
        if (!hasChanged)
        {
            for (int i = 0; i < freshItems.Count; i++)
            {
                if (AppsMenuItem.Items[i] is MenuItem mi && mi.DataContext is CatalogItemModel existing)
                {
                    if (!string.Equals(existing.TargetPath, freshItems[i].TargetPath, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(existing.Name, freshItems[i].Name, StringComparison.Ordinal))
                    {
                        hasChanged = true;
                        break;
                    }
                }
                else
                {
                    hasChanged = true;
                    break;
                }
            }
        }

        if (hasChanged)
        {
            AppsMenuItem.Items.Clear();
            foreach (var item in freshItems)
            {
                AppsMenuItem.Items.Add(CreateCatalogMenuItem(item));
            }
        }
    }

    private MenuItem CreateCatalogMenuItem(CatalogItemModel item)
    {
        var menuItem = new MenuItem
        {
            Header = item.Name,
            DataContext = item,
            Cursor = Cursors.Hand
        };

        var img = new Image
        {
            Width = 18,
            Height = 18,
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

        var binding = new Binding("Icon")
        {
            Source = item,
            Mode = BindingMode.OneWay
        };
        img.SetBinding(Image.SourceProperty, binding);
        menuItem.Icon = img;

        menuItem.Click += (s, e) =>
        {
            PinCatalogItem(item, _canvasRightClickPoint);
        };

        return menuItem;
    }

    public void PinCatalogItem(CatalogItemModel item, Point? targetCanvasPosition = null)
    {
        if (item == null) return;

        string prePin = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
        _historyService.PushState(prePin);

        UpdateLayoutMetrics();
        int maxCols = GridPlacementService.MaxCols;

        Point clickPoint = targetCanvasPosition ?? new Point(GridPlacementService.OriginX, GridPlacementService.OriginY);

        int col = GridPlacementService.ColFromPixel(clickPoint.X);
        int row = GridPlacementService.RowFromPixel(clickPoint.Y);

        TileGroupModel? targetGroup = null;
        if (targetCanvasPosition != null)
        {
            foreach (var g in Groups)
            {
                var (minC, maxC, minR, maxR) = GridPlacementService.GetGroupBoundingBox(g, Tiles);
                if (col >= minC && col < maxC && row >= minR && row <= maxR)
                {
                    targetGroup = g;
                    break;
                }
            }
        }

        if (targetGroup != null)
        {
            string? iconPathG = IconExtractorService.ExtractAndCacheIcon(item.TargetPath);
            int pinSpanX = Math.Min(item.SpanX > 0 ? item.SpanX : 2, 4);
            int pinSpanY = item.SpanY > 0 ? item.SpanY : 2;
            var groupTile = new TileModel
            {
                Title = item.Name,
                TargetPath = item.TargetPath,
                Arguments = item.Arguments,
                IconPath = iconPathG,
                TileType = item.TileType,
                SpanX = pinSpanX,
                SpanY = pinSpanY,
                Group = targetGroup.Id,
                SectionHeader = targetGroup.Title
            };

            // Place at click position within the group (preserves existing tile layout)
            int pinRelCol = col - targetGroup.Col;
            int pinRelRow = row - (targetGroup.Row + 1);
            var existingPinGroupTiles = Tiles.Where(t => t.Group == targetGroup.Id).ToList();
            var (pinSlotCol, pinSlotRow) = GridPlacementService.FindFreeSlotInGroup(
                targetGroup, pinRelCol, pinRelRow, pinSpanX, pinSpanY, existingPinGroupTiles);
            groupTile.Col = pinSlotCol;
            groupTile.Row = pinSlotRow;
            groupTile.X = GridPlacementService.PixelXFromCol(pinSlotCol);
            groupTile.Y = GridPlacementService.PixelYFromRow(pinSlotRow);

            Tiles.Add(groupTile);
            var mod = GridPlacementService.PlaceTileInGroup(
                groupTile, pinSlotCol, pinSlotRow, pinSlotCol, pinSlotRow, targetGroup, Tiles);
            var pushed = GridPlacementService.PushLowerGroupsDown(targetGroup, Groups, Tiles);
            foreach (var pt in pushed)
            {
                if (!mod.Contains(pt)) mod.Add(pt);
            }
            AnimateModifiedTiles(mod);
            UpdateGroupHeaderPositions();
            StorageService.SaveLayout(Tiles);
            SaveGroupsAndLayout();
            UpdateCanvasHeight();
            UpdateExposedAddSlots();
            return;
        }

        int spanX = item.SpanX > 0 ? item.SpanX : 2;
        int spanY = item.SpanY > 0 ? item.SpanY : 2;

        // Context-aware sizing: If target slot has room for 1x1 but not full 2x2, adapt down to 1x1
        if (GridPlacementService.IsRegionFree(col, row, 1, 1, Tiles))
        {
            if (!GridPlacementService.IsRegionFree(col, row, spanX, spanY, Tiles))
            {
                spanX = 1;
                spanY = 1;
            }
        }

        int clampedCol = Math.Max(0, Math.Min(col, maxCols - spanX));
        int clampedRow = Math.Max(0, row);

        var (freeCol, freeRow) = GridPlacementService.FindNearestAvailableSlot(
            clampedCol,
            clampedRow,
            spanX,
            spanY,
            Tiles,
            null,
            maxCols);

        string? iconPath = IconExtractorService.ExtractAndCacheIcon(item.TargetPath);

        var tile = new TileModel
        {
            Title = item.Name,
            TargetPath = item.TargetPath,
            Arguments = item.Arguments,
            IconPath = iconPath,
            TileType = item.TileType,
            SpanX = spanX,
            SpanY = spanY,
            X = GridPlacementService.PixelXFromCol(freeCol),
            Y = GridPlacementService.PixelYFromRow(freeRow)
        };

        Tiles.Add(tile);
        StorageService.SaveLayout(Tiles);
        UpdateCanvasHeight();

        UpdateExposedAddSlots();
    }

    #endregion

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

            if (sfd.ShowDialog() == true)
            {
                if (StorageService.ExportLayout(Tiles, sfd.FileName))
                {
                    System.Windows.MessageBox.Show("Layout exported successfully!", "MetroHub", System.Windows.MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
        finally
        {
            IsDialogOpen = false;
        }
    }

    private void OnCloseToTrayClick(object sender, RoutedEventArgs e)
    {
        HideScreen();
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnWindowDragLeave(object sender, DragEventArgs e)
    {
        UpdateExposedAddSlots();
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string[]? files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0)
            {
                string preDrop = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
                _historyService.PushState(preDrop);

                Point pos = e.GetPosition(TilesListBox);
                double currentX = Math.Max(0, pos.X);
                double currentY = Math.Max(0, pos.Y);
                foreach (string file in files)
                {
                    AddFileAsTile(file, currentX, currentY, recordHistory: false);
                    currentX += 70; // offset slightly for multiple files
                    currentY += 70;
                }
                e.Handled = true;
            }
        }
        UpdateExposedAddSlots();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_isClosingToExit)
        {
            e.Cancel = true;
            HideScreen();
        }
        else
        {
            _hotkeyService.Dispose();
            base.OnClosing(e);
        }
    }

    public void ExitApplication()
    {
        _isClosingToExit = true;
        _hotkeyService.Dispose();
        Close();
        Application.Current.Shutdown();
    }
}