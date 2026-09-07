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
        Task.Run(() => InstalledAppsService.GetInstalledApps(forceRefresh: false));
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

        UpdateLayoutMetrics();

        GridPlacementService.SanitizeAndSnapAll(Tiles, GridPlacementService.MaxCols);
        TilesListBox.ItemsSource = Tiles;
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
            if (_isRubberBanding)
            {
                _isRubberBanding = false;
                if (RubberBandBox != null) RubberBandBox.Visibility = Visibility.Collapsed;
                RootGrid.ReleaseMouseCapture();
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

    private void ClearTileSelection()
    {
        foreach (var t in Tiles)
        {
            t.IsSelected = false;
        }
    }

    public void ExecuteUndo()
    {
        if (_isDragging || _isRubberBanding || !_historyService.CanUndo) return;

        ClearTileSelection();
        string currentSnapshot = LayoutHistoryService.CaptureSnapshot(Tiles);
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
        string currentSnapshot = LayoutHistoryService.CaptureSnapshot(Tiles);
        string? targetSnapshot = _historyService.Redo(currentSnapshot);
        if (!string.IsNullOrWhiteSpace(targetSnapshot))
        {
            RestoreLayoutFromSnapshot(targetSnapshot);
        }
    }

    private void RestoreLayoutFromSnapshot(string snapshot)
    {
        var targetTiles = LayoutHistoryService.ParseSnapshot(snapshot);
        if (targetTiles == null) return;

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

        // Apply coordinates, spans, and styles
        var modifiedList = new List<TileModel>();

        foreach (var target in targetTiles)
        {
            var existing = Tiles.FirstOrDefault(t => t.Id == target.Id);
            if (existing == null) continue;

            bool posChanged = Math.Abs(existing.X - target.X) > 0.5 || Math.Abs(existing.Y - target.Y) > 0.5;
            bool spanChanged = existing.SpanX != target.SpanX || existing.SpanY != target.SpanY;
            bool styleChanged = existing.TileStyle != target.TileStyle || existing.AccentColor != target.AccentColor;

            existing.Col = target.Col;
            existing.Row = target.Row;
            existing.SpanX = target.SpanX;
            existing.SpanY = target.SpanY;
            existing.TileStyle = target.TileStyle;
            existing.AccentColor = target.AccentColor;

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

        UpdateLayoutMetrics();
        UpdateCanvasHeight();
        UpdateExposedAddSlots();
        StorageService.SaveLayout(Tiles);
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

        // If clicking on Top Header, Search, or Footer, don't trigger canvas selection
        if (HeaderGrid != null && HeaderGrid.IsMouseOver) return;
        if (FooterGrid != null && FooterGrid.IsMouseOver) return;

        Point canvasMouse = TilesListBox != null ? e.GetPosition(TilesListBox) : e.GetPosition(this);
        bool isCtrlDown = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        var tileControl = FindParent<Presentation.Controls.TileControl>(dep);

        if (tileControl != null && tileControl.DataContext is TileModel tile)
        {
            _preDragLayoutSnapshot = LayoutHistoryService.CaptureSnapshot(Tiles);
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
            Point current = e.GetPosition(this);
            Vector diff = current - _dragStartPoint;
            if (Math.Abs(diff.X) > 5 || Math.Abs(diff.Y) > 5)
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

            // Calculate snapped indicator position for the whole cluster
            int minAllowedCol = -_clusterRelGridBounds.MinRelCol;
            int maxAllowedCol = Math.Max(minAllowedCol, maxCols - _clusterRelGridBounds.MaxRelCol);
            int anchorCol = Math.Clamp(GridPlacementService.ColFromPixel(clampedAnchorX), minAllowedCol, maxAllowedCol);
            int anchorRow = Math.Max(-_clusterRelGridBounds.MinRelRow, GridPlacementService.RowFromPixel(clampedAnchorY));

            double snappedAnchorX = GridPlacementService.PixelXFromCol(anchorCol);
            double snappedAnchorY = GridPlacementService.PixelYFromRow(anchorRow);

            Canvas.SetLeft(DropSlotIndicator, snappedAnchorX + _clusterRelBounds.MinRelX);
            Canvas.SetTop(DropSlotIndicator, snappedAnchorY + _clusterRelBounds.MinRelY);
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
                // Simple click on empty canvas: deselect all tiles
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
            RootGrid.ReleaseMouseCapture();

            if (_draggedTile != null)
            {
                foreach (var cTile in _draggedCluster)
                {
                    cTile.IsBeingDragged = false;
                }

                UpdateLayoutMetrics();
                int maxCols = GridPlacementService.MaxCols;

                List<TileModel> modifiedTiles;

                if (_draggedCluster.Count <= 1)
                {
                    int maxAllowedCol = Math.Max(0, maxCols - _draggedTile.SpanX);
                    int targetCol = Math.Min(GridPlacementService.ColFromPixel(_draggedTile.X), maxAllowedCol);
                    int targetRow = GridPlacementService.RowFromPixel(_draggedTile.Y);

                    modifiedTiles = GridPlacementService.PlaceAndResolveCollisions(
                        _draggedTile,
                        targetCol,
                        targetRow,
                        _dragOriginalCol,
                        _dragOriginalRow,
                        maxCols,
                        Tiles);
                }
                else
                {
                    int minAllowedCol = -_clusterRelGridBounds.MinRelCol;
                    int maxAllowedCol = Math.Max(minAllowedCol, maxCols - _clusterRelGridBounds.MaxRelCol);
                    int anchorTargetCol = Math.Clamp(GridPlacementService.ColFromPixel(_draggedTile.X), minAllowedCol, maxAllowedCol);
                    int anchorTargetRow = Math.Max(-_clusterRelGridBounds.MinRelRow, GridPlacementService.RowFromPixel(_draggedTile.Y));

                    var origDict = _dragClusterOriginals.ToDictionary(k => k.Key, v => (v.Value.Col, v.Value.Row));

                    modifiedTiles = GridPlacementService.PlaceClusterAndResolveCollisions(
                        _draggedCluster,
                        _draggedTile,
                        anchorTargetCol,
                        anchorTargetRow,
                        _dragOriginalCol,
                        _dragOriginalRow,
                        maxCols,
                        Tiles,
                        origDict);
                }

                // Synchronize visual canvas layout for all affected tiles
                foreach (var tile in modifiedTiles)
                {
                    var container = TilesListBox?.ItemContainerGenerator.ContainerFromItem(tile) as ContentPresenter;
                    if (container != null)
                    {
                        Canvas.SetLeft(container, tile.X);
                        Canvas.SetTop(container, tile.Y);
                    }
                }

                UpdateCanvasHeight();
            }

            // Reset ZIndex and trigger AnimateRelease on all cluster tiles
            foreach (var cTile in _draggedCluster)
            {
                var container = TilesListBox?.ItemContainerGenerator.ContainerFromItem(cTile) as ContentPresenter;
                if (container != null)
                {
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

            UpdateExposedAddSlots();

            // Persist the clean layout immediately to layout.json
            StorageService.SaveLayout(Tiles);

            string postDropSnapshot = LayoutHistoryService.CaptureSnapshot(Tiles);
            if (!string.IsNullOrEmpty(_preDragLayoutSnapshot) && _preDragLayoutSnapshot != postDropSnapshot)
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

            // If user clicked an already selected tile without Ctrl in a multi-selection group,
            // single clicking collapses selection to just this tile
            if (!isCtrlDown && _dragBeganWithSelection && _draggedCluster.Count > 1 && clickedTile != null)
            {
                ClearTileSelection();
                clickedTile.IsSelected = true;
            }

            // Launch only on single unselected/individual click without Ctrl
            if (!isCtrlDown && _draggedCluster.Count <= 1 && controlToLaunch != null)
            {
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

    private void OnTileUnpinned(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TileModel tile)
        {
            string preUnpin = LayoutHistoryService.CaptureSnapshot(Tiles);
            _historyService.PushState(preUnpin);

            Tiles.Remove(tile);
            StorageService.SaveLayout(Tiles);
            UpdateExposedAddSlots();
        }
    }

    private void OnTileModified(object sender, RoutedEventArgs e)
    {
        TileModel? tile = (e is TileModifiedEventArgs args ? args.Tile : e.OriginalSource as TileModel);
        if (tile != null)
        {
            string preModify = LayoutHistoryService.CaptureSnapshot(Tiles);

            UpdateLayoutMetrics();
            int maxCols = GridPlacementService.MaxCols;

            bool isResize = e is TileModifiedEventArgs tmArgs && tmArgs.IsResize;
            int oldSpanX = (e is TileModifiedEventArgs tma) ? tma.OldSpanX : tile.SpanX;
            int oldSpanY = (e is TileModifiedEventArgs tmb) ? tmb.OldSpanY : tile.SpanY;

            if (isResize)
            {
                // Elevate the resizing tile so it morphs on top of sliding neighbors
                var resizingContainer = TilesListBox.ItemContainerGenerator.ContainerFromItem(tile) as ContentPresenter;
                if (resizingContainer != null)
                {
                    Panel.SetZIndex(resizingContainer, 50);
                    Dispatcher.InvokeAsync(async () =>
                    {
                        await Task.Delay(260);
                        Panel.SetZIndex(resizingContainer, 0);
                    });
                }

                // Resolve collision using the Directional Push Engine (Right -> Left -> Down Accordion)
                var modified = GridPlacementService.ResolveResizeExpansion(
                    tile,
                    oldSpanX,
                    oldSpanY,
                    tile.SpanX,
                    tile.SpanY,
                    maxCols,
                    Tiles);

                AnimateModifiedTiles(modified);
            }
            else
            {
                // Non-resize change (e.g. style change or lock position toggle)
                StorageService.SaveLayout(Tiles);
            }

            DropSlotIndicator.Visibility = Visibility.Collapsed;
            UpdateCanvasHeight();
            UpdateExposedAddSlots();
            StorageService.SaveLayout(Tiles);

            string postModify = LayoutHistoryService.CaptureSnapshot(Tiles);
            if (preModify != postModify)
            {
                _historyService.PushState(preModify);
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
                string preAdd = LayoutHistoryService.CaptureSnapshot(Tiles);
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
                string preAdd = LayoutHistoryService.CaptureSnapshot(Tiles);
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

            var (freeCol, freeRow) = GridPlacementService.FindNearestAvailableSlot(col, row, 2, 2, Tiles, null, maxCols);

            var tile = new TileModel
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

            Tiles.Add(tile);
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

        UpdateLayoutMetrics();
        int maxCols = GridPlacementService.MaxCols;

        Point clickPoint = targetCanvasPosition ?? new Point(GridPlacementService.OriginX, GridPlacementService.OriginY);

        int col = GridPlacementService.ColFromPixel(clickPoint.X);
        int row = GridPlacementService.RowFromPixel(clickPoint.Y);

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
                string preDrop = LayoutHistoryService.CaptureSnapshot(Tiles);
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