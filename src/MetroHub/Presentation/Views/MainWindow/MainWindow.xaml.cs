using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using MetroHub.Core.Models;
using MetroHub.Core.Messaging;
using MetroHub.Core.Services;
using MetroHub.Core.Services.Catalog;
using MetroHub.Presentation.Controls;
using MetroHub.Presentation.Views;
using MetroHub.Presentation.Messaging;
using MetroHub.Widgets.Messaging;
using CommunityToolkit.Mvvm.Messaging;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Image = System.Windows.Controls.Image;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MetroHub;

public partial class MainWindow : BorderlessFluentWindow
{
    public ObservableCollection<TileModel> Tiles { get; set; } = new();
    public ObservableCollection<TileGroupModel> Groups { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
    public string HotkeyDisplayString => Settings?.HotkeyDisplayString ?? "Ctrl + `";

    public static MainWindow? Current { get; private set; }
    
    private int _dialogOpenCount = 0;
    private static int _dialogScopeDepth;

    public bool IsDialogOpen
    {
        get => _dialogOpenCount > 0 || _dialogScopeDepth > 0;
        set
        {
            if (value)
                System.Threading.Interlocked.Increment(ref _dialogOpenCount);
            else
            {
                int current;
                do
                {
                    current = _dialogOpenCount;
                    if (current <= 0) break;
                } while (System.Threading.Interlocked.CompareExchange(ref _dialogOpenCount, current - 1, current) != current);
            }
        }
    }

    /// <summary>
    /// Universally keeps MetroHub open and suppresses auto-dismiss when an external modal,
    /// file picker, or UAC elevation prompt is active, then automatically restores foreground focus.
    /// Supports re-entrant/nested dialog scopes safely.
    /// </summary>
    public static IDisposable EnterDialogScope()
    {
        var win = Current;
        if (win == null) return ActionDisposable.Empty;

        Interlocked.Increment(ref _dialogScopeDepth);

        return new ActionDisposable(() =>
        {
            if (win == null) return;
            if (Interlocked.Decrement(ref _dialogScopeDepth) <= 0)
            {
                Interlocked.Exchange(ref _dialogScopeDepth, 0);
                win.Dispatcher.InvokeAsync(() =>
                {
                    IntPtr foreHwnd = NativeMethods.GetForegroundWindow();
                    uint ourPid = (uint)Environment.ProcessId;
                    uint forePid = 0;
                    if (foreHwnd != IntPtr.Zero)
                    {
                        NativeMethods.GetWindowThreadProcessId(foreHwnd, out forePid);
                    }

                    if (forePid != 0 && forePid != ourPid)
                    {
                        if (win.IsVisible && !win._isDismissing)
                        {
                            win.HideScreen(restorePreviousFocus: false);
                        }
                    }
                    else
                    {
                        try
                        {
                            win.Activate();
                            win.Focus();
                            Keyboard.Focus(win);
                        }
                        catch (Exception ex)
                        {
                            Safe.Log("MainWindow.Activate", ex);
                        }
                    }
                });
            }
        });
    }

    /// <summary>
    /// Explicitly sets the dialog state and brings MetroHub to foreground upon closing.
    /// </summary>
    public static void SetDialogOpen(bool isOpen)
    {
        var win = Current;
        if (win == null) return;

        Action action = () =>
        {
            win.IsDialogOpen = isOpen;
            if (!isOpen)
            {
                IntPtr foreHwnd = NativeMethods.GetForegroundWindow();
                uint ourPid = (uint)Environment.ProcessId;
                uint forePid = 0;
                if (foreHwnd != IntPtr.Zero)
                {
                    NativeMethods.GetWindowThreadProcessId(foreHwnd, out forePid);
                }

                if (forePid != 0 && forePid != ourPid)
                {
                    if (win.IsVisible && !win._isDismissing)
                    {
                        win.HideScreen(restorePreviousFocus: false);
                    }
                }
                else
                {
                    try { win.Activate(); win.Focus(); Keyboard.Focus(win); } catch (Exception ex) { Safe.Log("MainWindow.Activate", ex); }
                }
            }
        };

        if (win.Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            win.Dispatcher.InvokeAsync(action);
        }
    }

    private sealed class ActionDisposable : IDisposable
    {
        public static readonly ActionDisposable Empty = new(null);
        private Action? _action;
        public ActionDisposable(Action? action) => _action = action;
        public void Dispose()
        {
            var a = System.Threading.Interlocked.Exchange(ref _action, null);
            a?.Invoke();
        }
    }

    public static readonly DependencyProperty CurrentScaleProperty =
        DependencyProperty.Register(nameof(CurrentScale), typeof(double), typeof(MainWindow),
            new PropertyMetadata(1.0, OnCurrentScaleChanged));

    private static void OnCurrentScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is double scale && Application.Current != null)
        {
            Application.Current.Resources["AppUiScale"] = scale;
        }
    }

    public double CurrentScale
    {
        get => (double)GetValue(CurrentScaleProperty);
        set => SetValue(CurrentScaleProperty, value);
    }

    private readonly HotkeyService _hotkeyService = new();
    private bool _isClosingToExit = false;
    private IntPtr _previousForegroundWindow = IntPtr.Zero;

    private bool _isDismissing
    {
        get => IsDismissing;
        set => IsDismissing = value;
    }

    private IntPtr _winEventHook = IntPtr.Zero;
    private NativeMethods.WinEventDelegate? _winEventDelegate;
    private System.Threading.Timer? _keepWarmTimer;

    public MainWindow()
    {
        Current = this;
        TileModel.WidgetViewModelFactory ??= MetroHub.Widgets.Registry.WidgetRegistry.CreateViewModelForTile;
        InitializeComponent();
        DataContext = this;

        LoadData();
        SetupAutoScrollTimer();
        SetupSidebarTimers();

        SidebarRail.InitializeSettings(Settings);
        SidebarRail.PinToggled += OnSidebarPinToggled;
        SidebarRail.ShortcutsChanged += OnSidebarShortcutsChanged;
        UpdateSidebarVisibilityInitial();

        if (AllAppsDrawer != null)
        {
            AllAppsDrawer.RefreshRequested += (s, e) => TriggerBackgroundAppsCatalogRefresh();
            AllAppsDrawer.IsAppPinnedPredicate = IsCatalogItemPinned;
        }

        Activated += OnWindowActivated;
        RootGrid.LostMouseCapture += OnRootGridLostMouseCapture;
        LostMouseCapture += OnRootGridLostMouseCapture;
        SizeChanged += (s, e) =>
        {
            UpdateLayoutMetrics();
            UpdateCanvasHeight();
            UpdateExposedAddSlots();
            if (AllAppsDrawer != null && AllAppsDrawer.IsOpen && _canvasScissorTranslate != null)
            {
                _canvasScissorTranslate.X = (AllAppsDrawer.DrawerWidth > 0 ? AllAppsDrawer.DrawerWidth : 320.0);
            }
        };

        InstalledAppsService.AppsCatalogChanged += OnAppsCatalogChanged;
        StartBackgroundAppWarmup();
        InitializeKeepWarmTimer();

        PreviewTextInput += OnWindowPreviewTextInput;
        PreviewMouseDown += OnWindowPreviewMouseDown;
        ContentScrollViewer.ScrollChanged += OnContentScrollViewerScrollChanged;
        ScrollDiagnosticsLogger.AttachMainWindow(this, ContentScrollViewer);

        // Auto-persist layout in background when any widget notifies of settings changes
        WeakReferenceMessenger.Default.Register<MainWindow, WidgetSettingsChangedMessage>(
            this,
            (r, msg) => StorageService.SaveLayout(r.Tiles));

        WeakReferenceMessenger.Default.Register<MainWindow, TargetLaunchFailedMessage>(
            this,
            (r, msg) =>
            {
                r.Dispatcher.InvokeAsync(() =>
                {
                    if (!r.IsVisible)
                    {
                        r.ShowScreen();
                    }
                    r.ShowToast($"Could not launch {msg.Title}: {msg.Exception.Message}", isError: true);
                });
            });

        RegisterCanvasMessageHandlers();
    }

    private void RegisterCanvasMessageHandlers()
    {
        WeakReferenceMessenger.Default.Register<MainWindow, QuerySelectedTilesMessage>(this, (r, m) => m.Reply(r.SelectedTiles));
        WeakReferenceMessenger.Default.Register<MainWindow, QueryGroupsMessage>(this, (r, m) => m.Reply(r.Groups));
        WeakReferenceMessenger.Default.Register<MainWindow, QuerySidebarRailMessage>(this, (r, m) => m.Reply(r.SidebarRail));

        WeakReferenceMessenger.Default.Register<MainWindow, TileClearSelectionMessage>(this, (r, m) => r.ClearTileSelection());
        WeakReferenceMessenger.Default.Register<MainWindow, TileBatchResizeMessage>(this, (r, m) => r.BatchResizeSelectedTiles(m.SpanX, m.SpanY, m.SourceTile));
        WeakReferenceMessenger.Default.Register<MainWindow, TileBatchStyleMessage>(this, (r, m) => r.BatchStyleSelectedTiles(m.Style, m.SourceTile));
        WeakReferenceMessenger.Default.Register<MainWindow, TileCreateGroupMessage>(this, (r, m) => r.CreateGroupFromSelectedTiles(m.SourceTile));
        WeakReferenceMessenger.Default.Register<MainWindow, TileAddToGroupMessage>(this, (r, m) => r.AddTilesToExistingGroup(m.Targets as IList<TileModel> ?? m.Targets.ToList(), m.TargetGroup));
        WeakReferenceMessenger.Default.Register<MainWindow, TileBatchUnpinMessage>(this, (r, m) => r.BatchUnpinSelectedTiles(m.SourceTile));
        WeakReferenceMessenger.Default.Register<MainWindow, TileToggleSidebarPinMessage>(this, (r, m) => SidebarPinningService.HandleTogglePin(r.SidebarRail, m.Tile, r.SelectedTiles));
        WeakReferenceMessenger.Default.Register<MainWindow, TileShowWeatherLocationDialogMessage>(this, (r, m) => r.ShowSetWeatherLocationDialog(m.WeatherVm));

        WeakReferenceMessenger.Default.Register<MainWindow, GroupFlashLockedMessage>(this, (r, m) => r.FlashLockedGroupPerimeter(m.Group));
        WeakReferenceMessenger.Default.Register<MainWindow, GroupRenameMessage>(this, (r, m) =>
        {
            r.RenameGroup(m.Group, m.NewTitle);
            m.Reply(true);
        });
        WeakReferenceMessenger.Default.Register<MainWindow, GroupStartDragMessage>(this, (r, m) => r.StartGroupDrag(m.Group, m.MouseArgs));
        WeakReferenceMessenger.Default.Register<MainWindow, GroupToggleLockMessage>(this, (r, m) =>
        {
            r.ToggleGroupLock(m.Group);
            m.Reply(true);
        });
        WeakReferenceMessenger.Default.Register<MainWindow, GroupSetColorMessage>(this, (r, m) =>
        {
            if (m.HexColor != null) r.SetGroupColor(m.Group, m.HexColor);
        });
        WeakReferenceMessenger.Default.Register<MainWindow, GroupSetTintColorMessage>(this, (r, m) => r.SetGroupTintColor(m.Group, m.HexTint));
        WeakReferenceMessenger.Default.Register<MainWindow, GroupUngroupMessage>(this, (r, m) => r.UngroupTiles(m.Group));
        WeakReferenceMessenger.Default.Register<MainWindow, GroupDeleteMessage>(this, (r, m) => r.DeleteGroupAndTiles(m.Group));
    }

    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.FocusedElement is DependencyObject focused)
        {
            var textInput = (focused as System.Windows.Controls.Primitives.TextBoxBase)
                         ?? (DependencyObject?)(focused as System.Windows.Controls.PasswordBox)
                         ?? FindParent<System.Windows.Controls.Primitives.TextBoxBase>(focused)
                         ?? (DependencyObject?)FindParent<System.Windows.Controls.PasswordBox>(focused);

            if (textInput != null)
            {
                var clicked = e.OriginalSource as DependencyObject;
                if (clicked == null || !IsDescendantOf(clicked, textInput))
                {
                    // If the user clicked inside the same widget card, allow that widget to manage its own focus.
                    var parentCard = FindParent<MetroHub.Widgets.WidgetCard>(textInput);
                    if (clicked != null && parentCard != null && IsDescendantOf(clicked, parentCard))
                    {
                        return;
                    }

                    Focus();
                    Keyboard.Focus(this);
                    FocusManager.SetFocusedElement(this, this);
                }
            }
        }
    }

    private static DependencyObject? GetVisualOrLogicalParent(DependencyObject? node)
    {
        if (node == null) return null;

        if (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
        {
            var parent = VisualTreeHelper.GetParent(node);
            if (parent != null) return parent;
        }

        if (node is FrameworkElement fe)
        {
            return fe.Parent ?? fe.TemplatedParent ?? LogicalTreeHelper.GetParent(node);
        }

        if (node is FrameworkContentElement fce)
        {
            return fce.Parent ?? fce.TemplatedParent ?? LogicalTreeHelper.GetParent(node);
        }

        return LogicalTreeHelper.GetParent(node);
    }

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject root)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, root))
            {
                return true;
            }

            if (node is System.Windows.Controls.Primitives.Popup popup)
            {
                node = popup.PlacementTarget ?? popup.Parent;
                continue;
            }

            node = GetVisualOrLogicalParent(node);
        }

        return false;
    }

    private void OnRootGridLostMouseCapture(object sender, MouseEventArgs e)
    {
        // LostMouseCapture bubbles, so losing capture on a control INSIDE a tile also lands
        // here. That happens normally on every widget that opts into Tag="AllowTileDrag":
        // ButtonBase captures the mouse on press, then the hub takes capture away from it when
        // the press is promoted to a drag. Reacting to that would cancel the drag the hub just
        // started, so only the hub's own capture holders may trigger cleanup.
        if (e.OriginalSource is DependencyObject source
            && !ReferenceEquals(source, RootGrid)
            && !ReferenceEquals(source, this)
            && FindParent<Presentation.Controls.TileControl>(source) != null)
        {
            return;
        }

        if (_isDragging || _isPotentialDrag || _isRubberBanding)
        {
            CancelActiveDrag();
        }
    }

    private DateTime _lastShownTime = DateTime.MinValue;
    private bool _isFullyActivated = false;

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        _isFullyActivated = true;
        if ((_isDragging || _isPotentialDrag || _isRubberBanding) && Mouse.LeftButton != MouseButtonState.Pressed)
        {
            CancelActiveDrag();
        }

        if (AllAppsDrawer == null || !AllAppsDrawer.IsOpen)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (AllAppsDrawer == null || !AllAppsDrawer.IsOpen)
                {
                    // Give the window real keyboard focus so WPF routes PreviewTextInput
                    // through the window — without this, Keyboard.FocusedElement stays null
                    // and typing never reaches OnWindowPreviewTextInput on first launch.
                    Focus();
                    Keyboard.Focus(this);
                }
            }, DispatcherPriority.Input);
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_isDragging || _isPotentialDrag || _isRubberBanding)
        {
            CancelActiveDrag();
        }

        // Must be fully activated first, and ignore premature deactivation within 150ms of opening
        if (!_isFullyActivated)
        {
            return;
        }

        if ((DateTime.UtcNow - _lastShownTime).TotalMilliseconds < 150)
        {
            return;
        }

        IntPtr foreHwnd = NativeMethods.GetForegroundWindow();
        uint currentProcessId = (uint)Environment.ProcessId;
        uint foreProcessId = 0;
        if (foreHwnd != IntPtr.Zero)
        {
            NativeMethods.GetWindowThreadProcessId(foreHwnd, out foreProcessId);
        }

        // If focus shifted internally to one of our own windows (like a MetroDialog), do not dismiss
        if (foreProcessId == currentProcessId)
        {
            return;
        }

        // If a dialog is open or opening and foreground window is transitioning (0), do not dismiss prematurely
        if (IsDialogOpen && foreProcessId == 0)
        {
            return;
        }

        if (IsVisible && !_isDismissing)
        {
            DismissOpenDialogs();
            HideScreen(restorePreviousFocus: false);
        }
    }

    private void LoadData()
    {
        Settings = StorageService.LoadSettings();
        Application.Current.Resources["TileCornerRadius"] = new CornerRadius(Settings.TileCornerRadius);
        Tiles = StorageService.LoadLayout();
        Groups = StorageService.LoadGroups();
        GridPlacementService.SetActiveGroups(Groups);

        // Auto-migrate: ensure Row 0 is the dedicated header zone.
        // Shift loose tiles that start at Row 0 down to Row >= 1, preserving relative spacing.
        var looseTilesAtZero = Tiles.Where(t => string.IsNullOrEmpty(t.Group) && t.Row == 0).ToList();
        if (looseTilesAtZero.Count > 0)
        {
            foreach (var t in Tiles.Where(t => string.IsNullOrEmpty(t.Group)))
            {
                t.Row += 1;
            }
            StorageService.SaveLayout(Tiles);
        }

        // Re-sync all tiles pixel positions with current GridPlacementService metrics
        foreach (var t in Tiles)
        {
            t.Col = Math.Max(0, t.Col);
            t.Row = Math.Max(1, t.Row);
            t.X = GridPlacementService.PixelXFromCol(t.Col);
        }

        // Housekeeping: widget state files are keyed by tile id, so files whose tile no longer
        // exists are dead weight. Pruned at startup only — same-session undo/unpin stays recoverable.
        WidgetStateStore.Default.PruneAllExcept(Tiles.Select(t => t.Id).ToHashSet());

        DiscoverGroupsFromTiles();
        EnsureGroupIndices();
        MigrateGroupColumnOffsets();
        CompactGroupGaps();
        UpdateLayoutMetrics();
        bool anyCleaned = GridPlacementService.CleanEmptyGroups(Groups, Tiles);
        UpdateGroupHeaderPositions();

        if (anyCleaned)
        {
            SaveGroupsAndLayout();
        }

        TilesListBox.ItemsSource = Tiles;
        if (GroupsListBox != null) GroupsListBox.ItemsSource = Groups;
        if (GroupTintBackplates != null) GroupTintBackplates.ItemsSource = Groups;
        UpdateCanvasHeight();
        UpdateExposedAddSlots();
    }

    private void OnSourceInitialized(object sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;

        ApplyConfiguredBackdrop();

        var hwndSource = HwndSource.FromHwnd(hwnd);
        hwndSource?.AddHook(WndProc);

        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        _hotkeyService.Register(hwnd, Settings);

        _winEventDelegate = OnSystemForegroundChanged;
        _winEventHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _winEventDelegate,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);

        SnapToWorkArea();
    }

    private void OnSystemForegroundChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero) return;
        if (!IsVisible || _isDismissing) return;

        // Prevent premature dismissal in first 150ms of opening
        if ((DateTime.UtcNow - _lastShownTime).TotalMilliseconds < 150) return;

        // If user is currently dragging a tile, don't dismiss
        if (_isDragging || _isPotentialDrag || _isRubberBanding) return;

        // Check if the foreground window belongs to an external process (taskbar, clock, other app, desktop)
        uint currentProcessId = (uint)Environment.ProcessId;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint foreProcessId);

        if (foreProcessId != 0 && foreProcessId != currentProcessId)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (IsVisible && !_isDismissing)
                {
                    DismissOpenDialogs();
                    HideScreen(restorePreviousFocus: false);
                }
            });
        }
    }

    private void DismissOpenDialogs()
    {
        if (Application.Current == null) return;
        var openWindows = Application.Current.Windows.OfType<Window>().ToList();
        foreach (Window window in openWindows)
        {
            if (window is MetroHub.Presentation.Dialogs.MetroDialog dialog)
            {
                dialog.DismissDialog();
            }
            else if (window != this)
            {
                try { window.Close(); } catch { }
            }
        }
    }

    public void PlayOpenAnimation()
    {
        _isDismissing = false;

        if (RootGrid != null)
        {
            RootGrid.IsHitTestVisible = true;
        }
    }

    public void DismissWithAnimation()
    {
        if (!IsVisible) return;

        if (_isDragging || _isPotentialDrag || _isRubberBanding)
        {
            CancelActiveDrag();
        }

        Hide();
        _isDismissing = false;
        _isFullyActivated = false;
        Topmost = false;

        if (RootGrid != null) RootGrid.IsHitTestVisible = true;

        // Log diagnostic snapshot at idle priority after window is hidden (without forced GC or WorkingSet flush)
        Dispatcher.InvokeAsync(() =>
        {
            var currentMB = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
            MetroHub.Core.Services.HiddenDiagnosticsLogger.LogMemorySnapshot("HUB HIDE", currentMB);
        }, DispatcherPriority.ApplicationIdle);
    }

    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_GETOBJECT = 0x003D;
    private const int WM_DISPLAYCHANGE = 0x007E;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETOBJECT)
        {
            handled = true;
            return IntPtr.Zero;
        }

        if ((uint)msg == NativeMethods.WM_SHOW_METROHUB)
        {
            Dispatcher.Invoke(ShowScreen);
            handled = true;
        }
        else if (msg == WM_DISPLAYCHANGE)
        {
            Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    _cachedWorkArea = Rect.Empty;
                    _cachedDpiX = 0;
                    _cachedDpiY = 0;
                    if (IsVisible)
                    {
                        SnapToWorkArea(force: true);
                        ApplyConfiguredBackdrop(force: true);
                        await Task.Delay(200);
                        SnapToWorkArea(force: true);
                    }
                }
                catch (Exception ex)
                {
                    Safe.Log("MainWindow.WndProc.WM_DISPLAYCHANGE", ex);
                }
            });
        }
        else if (msg == WM_SETTINGCHANGE)
        {
            Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    ApplyConfiguredBackdrop();
                    await Task.Delay(400);
                    ApplyConfiguredBackdrop();
                }
                catch (Exception ex)
                {
                    Safe.Log("MainWindow.WndProc.WM_SETTINGCHANGE", ex);
                }
            });
        }

        return IntPtr.Zero;
    }

    private Rect _cachedWorkArea = Rect.Empty;
    private double _cachedDpiX = 0.0;
    private double _cachedDpiY = 0.0;

    public void SnapToWorkArea(bool force = false)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        // 1. Get raw monitor work area in physical pixels
        Rect workAreaPixels = NativeMethods.GetActiveMonitorWorkArea();

        // 2. Query active monitor DPI scaling factor for this window
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double dpiX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double dpiY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        // If bounds and DPI have not changed, skip redundant layout invalidations and Win32 frame resets
        if (!force &&
            _cachedWorkArea == workAreaPixels &&
            Math.Abs(_cachedDpiX - dpiX) < 0.001 &&
            Math.Abs(_cachedDpiY - dpiY) < 0.001)
        {
            return;
        }

        _cachedWorkArea = workAreaPixels;
        _cachedDpiX = dpiX;
        _cachedDpiY = dpiY;

        // 3. Convert physical pixels to WPF Device-Independent Units (DIPs)
        Left = workAreaPixels.Left / dpiX;
        Top = workAreaPixels.Top / dpiY;
        Width = workAreaPixels.Width / dpiX;
        Height = workAreaPixels.Height / dpiY;

        // 4. Force Win32 window bounds in physical pixels so the OS shell aligns exactly
        // NOTE: Dropped SWP_FRAMECHANGED to prevent WM_NCCALCSIZE frame buffer destruction
        NativeMethods.SetWindowPos(
            hwnd, 
            IntPtr.Zero,
            (int)workAreaPixels.Left, 
            (int)workAreaPixels.Top,
            (int)workAreaPixels.Width, 
            (int)workAreaPixels.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        UpdateLayoutMetrics();
        UpdateCanvasHeight();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        SnapToWorkArea(force: true);
    }

    protected override void OnClosed(EventArgs e)
    {
        _keepWarmTimer?.Dispose();
        _keepWarmTimer = null;
        CancelPendingWallpaperLoad();
        TeardownWallpaperVideo();
        base.OnClosed(e);
    }

    private void InitializeKeepWarmTimer()
    {
        // Gentle 10-minute idle pulse to keep the 40-50 MB core WPF/.NET runtime resident in physical RAM
        _keepWarmTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!IsVisible)
                    {
                        // Microscopic read to touch the framework page tables without doing heavy work
                        _ = this.IsLoaded;
                    }
                }), DispatcherPriority.SystemIdle);
            }
            catch { }
        }, null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
    }

    private void OnHotkeyPressed()
    {
        Dispatcher.Invoke(ToggleVisibility);
    }

    public void ToggleVisibility()
    {
        if (IsVisible && !_isDismissing)
        {
            HideScreen(restorePreviousFocus: true);
        }
        else
        {
            ShowScreen();
        }
    }

    public void ShowScreen()
    {
        if (_isDragging || _isPotentialDrag || _isRubberBanding)
        {
            CancelActiveDrag();
        }

        // Capture previous active foreground window BEFORE MetroHub takes focus (resolving to root owner for Electron/Chromium apps)
        IntPtr foreHwnd = NativeMethods.GetForegroundWindow();
        if (foreHwnd != IntPtr.Zero)
        {
            IntPtr root = NativeMethods.GetAncestor(foreHwnd, NativeMethods.GA_ROOTOWNER);
            if (root != IntPtr.Zero) foreHwnd = root;
        }

        IntPtr myHwnd = new WindowInteropHelper(this).Handle;
        if (foreHwnd != IntPtr.Zero && foreHwnd != myHwnd)
        {
            NativeMethods.GetWindowThreadProcessId(foreHwnd, out uint forePid);
            if (forePid != (uint)Environment.ProcessId)
            {
                _previousForegroundWindow = foreHwnd;
            }
        }

        _isDismissing = false;
        _lastShownTime = DateTime.UtcNow;
        _isFullyActivated = false;

        SnapToWorkArea(force: false);
        ApplyBorderlessAttributes();
        ApplyConfiguredBackdrop(force: false);

        Show();
        WindowState = WindowState.Normal;
        Topmost = true;
        ReinstallWinEventHook();

        // Resume video wallpaper playback if active
        bool isVideoActive = WallpaperVideo != null && WallpaperVideo.Visibility == Visibility.Visible && WallpaperVideo.Source != null;
        if (isVideoActive)
        {
            try { WallpaperVideo!.Play(); } catch (Exception ex) { Safe.Log("MainWindow.WallpaperPlay", ex); }
        }

        if (myHwnd != IntPtr.Zero)
        {
            NativeMethods.ForceForeground(myHwnd);
        }

        Activate();
        Focus();
        Keyboard.Focus(this);
        UpdateExposedAddSlots();

        // Immediate cohesive entrance animation without artificial Render-delay gating
        PlayOpenAnimation();

        // Defer widget wake-up and service resumption to background priority so UI opens instantly without frame drops
        Dispatcher.InvokeAsync(() =>
        {
            MetroHub.Widgets.Messaging.WidgetMessenger.Send(new MetroHub.Widgets.Messaging.HubVisibilityChangedMessage(true));
            WidgetHeartbeatService.SetHubVisibility(true);
            InstalledAppsService.ResumeWatchers();
        }, DispatcherPriority.Background);

        MetroHub.Core.Services.HubState.SetVisibility(true);
        MetroHub.Core.Services.HiddenDiagnosticsLogger.LogTransition(true);
    }

    public void HideScreen(bool restorePreviousFocus = true)
    {
        if (_isDismissing || !IsVisible) return;
        _isDismissing = true;

        if (restorePreviousFocus && _previousForegroundWindow != IntPtr.Zero)
        {
            IntPtr targetHwnd = _previousForegroundWindow;
            _previousForegroundWindow = IntPtr.Zero;
            if (NativeMethods.IsWindow(targetHwnd))
            {
                NativeMethods.SetForegroundWindow(targetHwnd);
            }
        }
        else
        {
            _previousForegroundWindow = IntPtr.Zero;
        }

        MetroHub.Core.Services.HubState.SetVisibility(false);
        MetroHub.Core.Services.HiddenDiagnosticsLogger.LogTransition(false);
        Interlocked.Exchange(ref _dialogScopeDepth, 0);
        Interlocked.Exchange(ref _dialogOpenCount, 0);
        if (AllAppsDrawer != null && AllAppsDrawer.IsOpen)
        {
            AllAppsDrawer.Close();
            SidebarRail?.SetAppsDrawerActive(false);
            if (MainContentAreaGrid != null)
            {
                MainContentAreaGrid.Clip = null;
                MainContentAreaGrid.BeginAnimation(UIElement.OpacityProperty, null);
                MainContentAreaGrid.Opacity = 1.0;
            }
            if (ContentScrollViewer != null) ContentScrollViewer.Clip = null;
        }
        Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(this, this);
        MetroHub.Widgets.Messaging.WidgetMessenger.Send(new MetroHub.Widgets.Messaging.HubVisibilityChangedMessage(false));
        WidgetHeartbeatService.SetHubVisibility(false);
        Safe.Try(SaveGroupsAndLayout, "MainWindow.HideScreen.SaveGroupsAndLayout");
        if (!Settings.SidebarPinned)
        {
            HideSidebarRail(immediate: true);
        }

        // Halt background services that are pointless when hidden
        InstalledAppsService.PauseWatchers();
        UninstallWinEventHook();

        // Auto-pause video wallpaper when hidden to ensure 0.0% CPU and 0.0% GPU decode
        try { WallpaperVideo?.Pause(); } catch (Exception ex) { Safe.Log("MainWindow.WallpaperPause", ex); }

        // Dismiss immediately without UI freeze (GC cleanup is deferred to ApplicationIdle in DismissWithAnimation)
        DismissWithAnimation();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F12)
        {
            if (ScrollDiagnosticHud != null)
            {
                ScrollDiagnosticHud.Visibility = ScrollDiagnosticHud.Visibility == Visibility.Visible
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
            e.Handled = true;
            return;
        }

        bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool isShift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (isCtrl && (e.Key == Key.Z || e.Key == Key.Y))
        {
            if (IsTextInputFocused()) return;

            if (e.Key == Key.Z)
            {
                if (isShift) ExecuteRedo();
                else ExecuteUndo();
            }
            else if (e.Key == Key.Y)
            {
                ExecuteRedo();
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete)
        {
            if (IsTextInputFocused()) return;

            if (Tiles.Any(t => t.IsSelected))
            {
                DeleteSelectedTiles();
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Escape)
        {
            if (AllAppsDrawer != null && AllAppsDrawer.IsOpen)
            {
                AllAppsDrawer.Close();
                SidebarRail?.SetAppsDrawerActive(false);
                e.Handled = true;
                return;
            }

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

            HideScreen(restorePreviousFocus: true);
            e.Handled = true;
            return;
        }

    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T parent) return parent;
            child = GetVisualOrLogicalParent(child);
        }

        return null;
    }

    private static bool IsTextInputFocused()
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        if (focused == null) return false;
        if (focused is System.Windows.Controls.Primitives.TextBoxBase || focused is System.Windows.Controls.PasswordBox)
            return true;
        return FindParent<System.Windows.Controls.Primitives.TextBoxBase>(focused) != null
               || FindParent<System.Windows.Controls.PasswordBox>(focused) != null;
    }

    private void OnCloseToTrayClick(object sender, RoutedEventArgs e)
    {
        HideScreen(restorePreviousFocus: true);
    }

    private void OnWindowPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Don't trigger if a dialog is open, or during active drag / rubberbanding
        if (IsDialogOpen || _isDragging || _isPotentialDrag || _isRubberBanding)
        {
            return;
        }

        // Don't trigger if modifier keys (Ctrl, Alt, Win) are held down
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
        {
            return;
        }

        // Don't intercept if user is already typing in an existing input element
        if (IsTextInputFocused())
        {
            return;
        }

        if (string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        char c = e.Text[0];
        // Ignore control characters (\b, \r, \n) and standalone whitespace
        if (char.IsControl(c) || char.IsWhiteSpace(c))
        {
            return;
        }

        if (AllAppsDrawer != null)
        {
            if (!AllAppsDrawer.IsOpen)
            {
                AllAppsDrawer.StartSearch(e.Text);
                e.Handled = true;
            }
            else
            {
                AllAppsDrawer.AppendSearch(e.Text);
                e.Handled = true;
            }
        }
    }

    private void CleanupEventSubscriptions()
    {
        if (SidebarRail != null)
        {
            SidebarRail.PinToggled -= OnSidebarPinToggled;
            SidebarRail.ShortcutsChanged -= OnSidebarShortcutsChanged;
        }

        InstalledAppsService.AppsCatalogChanged -= OnAppsCatalogChanged;

        if (ContentScrollViewer != null)
        {
            ContentScrollViewer.ScrollChanged -= OnContentScrollViewerScrollChanged;
        }

        WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        Safe.Try(SaveGroupsAndLayout, "MainWindow.OnClosing.SaveGroupsAndLayout");

        if (!_isClosingToExit)
        {
            e.Cancel = true;
            HideScreen(restorePreviousFocus: true);
        }
        else
        {
            CleanupEventSubscriptions();
            Safe.Try(StorageService.Flush, "MainWindow.OnClosing.StorageFlush");
            Safe.Try(InstalledAppsService.Shutdown, "MainWindow.OnClosing.InstalledAppsServiceShutdown");
            UninstallWinEventHook();
            _hotkeyService.Dispose();
            base.OnClosing(e);
        }
    }

    public void ExitApplication()
    {
        _isClosingToExit = true;
        Safe.Try(SaveGroupsAndLayout, "MainWindow.ExitApplication.SaveGroupsAndLayout");
        Safe.Try(StorageService.Flush, "MainWindow.ExitApplication.StorageFlush");

        // Cleanly dispose and tear down all active widget models (audio endpoints, media sessions, timers)
        Safe.Try(() =>
        {
            foreach (var tile in Tiles)
            {
                tile.Teardown();
            }
        }, "MainWindow.ExitApplication.TeardownTiles");

        CleanupEventSubscriptions();
        Safe.Try(InstalledAppsService.Shutdown, "MainWindow.ExitApplication.InstalledAppsServiceShutdown");
        UninstallWinEventHook();
        _hotkeyService.Dispose();
        Close();
        Application.Current?.Shutdown();
    }

    private void UninstallWinEventHook()
    {
        if (_winEventHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
    }

    private void ReinstallWinEventHook()
    {
        if (_winEventHook != IntPtr.Zero) return; // Already installed
        if (_winEventDelegate == null) return;

        _winEventHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _winEventDelegate,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);
    }
}