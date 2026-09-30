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
using MetroHub.Core.Services;
using MetroHub.Core.Services.Catalog;
using MetroHub.Presentation.Controls;
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
    public bool IsDialogOpen
    {
        get => _dialogOpenCount > 0;
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
    private static int _dialogScopeDepth;

    /// <summary>
    /// Universally keeps MetroHub open and suppresses auto-dismiss when an external modal,
    /// file picker, or UAC elevation prompt is active, then automatically restores foreground focus.
    /// </summary>
    public static IDisposable EnterDialogScope()
    {
        var win = Current;
        if (win == null) return ActionDisposable.Empty;

        Interlocked.Increment(ref _dialogScopeDepth);
        if (win.Dispatcher.CheckAccess())
        {
            win.IsDialogOpen = true;
        }
        else
        {
            win.Dispatcher.Invoke(() => win.IsDialogOpen = true);
        }

        return new ActionDisposable(() =>
        {
            if (win == null) return;
            if (Interlocked.Decrement(ref _dialogScopeDepth) <= 0)
            {
                Interlocked.Exchange(ref _dialogScopeDepth, 0);
                win.Dispatcher.InvokeAsync(() =>
                {
                    win.IsDialogOpen = false;
                    IntPtr foreHwnd = NativeMethods.GetForegroundWindow();
                    IntPtr winHwnd = new WindowInteropHelper(win).Handle;

                    if (foreHwnd != IntPtr.Zero && foreHwnd != winHwnd)
                    {
                        if (win.IsVisible && !win._isDismissing)
                        {
                            win.HideScreen();
                        }
                    }
                    else
                    {
                        try
                        {
                            win.Activate();
                            win.Focus();
                        }
                        catch { }
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
                IntPtr winHwnd = new WindowInteropHelper(win).Handle;

                if (foreHwnd != IntPtr.Zero && foreHwnd != winHwnd)
                {
                    if (win.IsVisible && !win._isDismissing)
                    {
                        win.HideScreen();
                    }
                }
                else
                {
                    try { win.Activate(); win.Focus(); } catch { }
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
    private IntPtr _keyboardHook = IntPtr.Zero;
    private NativeMethods.LowLevelKeyboardProc? _keyboardHookProc;
    private volatile bool _suppressNextAltKeyUp = false;

    public MainWindow()
    {
        Current = this;
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

        PreviewTextInput += OnWindowPreviewTextInput;
        PreviewMouseDown += OnWindowPreviewMouseDown;
        ContentScrollViewer.ScrollChanged += OnContentScrollViewerScrollChanged;

        // Auto-persist layout in background when any widget notifies of settings changes
        WeakReferenceMessenger.Default.Register<MainWindow, WidgetSettingsChangedMessage>(
            this,
            (r, msg) => StorageService.SaveLayout(r.Tiles));

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
        WeakReferenceMessenger.Default.Register<MainWindow, TileToggleSidebarPinMessage>(this, (r, m) => SidebarPinningService.HandleContextMenuClick(m.Tile, r));
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
                    // If the user clicked inside the same text widget (e.g. formatting buttons or todo switches),
                    // allow that widget to manage its own focus.
                    var notepadView = FindParent<MetroHub.Widgets.Catalog.Notepad.NotepadWidgetView>(textInput);
                    var markdownView = notepadView == null
                        ? FindParent<MetroHub.Widgets.Catalog.Markdown.MarkdownWidgetView>(textInput)
                        : null;
                    if (clicked != null && ((notepadView != null && IsDescendantOf(clicked, notepadView)) ||
                        (markdownView != null && IsDescendantOf(clicked, markdownView))))
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

        if (!IsDialogOpen && IsVisible && !_isDismissing)
        {
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
        if (!IsVisible || _isDismissing || IsDialogOpen) return;

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
                if (IsVisible && !_isDismissing && !IsDialogOpen)
                {
                    HideScreen(restorePreviousFocus: false);
                }
            });
        }
    }

    public void PlayOpenAnimation()
    {
        _isDismissing = false;

        if (RootTranslate != null)
        {
            RootTranslate.X = 0.0;
            RootTranslate.Y = 0.0;
        }

        if (TryFindResource("OpenStoryboard") is Storyboard openStoryboard)
        {
            var sb = openStoryboard.Clone();
            Timeline.SetDesiredFrameRate(sb, NativeMethods.GetScreenRefreshRate());
            sb.Completed += (s, e) =>
            {
                if (RootGrid != null)
                {
                    RootGrid.BeginAnimation(UIElement.OpacityProperty, null);
                    RootGrid.Opacity = 1.0;
                }
            };
            sb.Begin(this, isControllable: true);
        }
        else
        {
            if (RootGrid != null) RootGrid.Opacity = 1.0;
            if (RootTranslate != null) { RootTranslate.X = 0.0; RootTranslate.Y = 0.0; }
        }
    }

    public void DismissWithAnimation()
    {
        if (!IsVisible) return;
        _isDismissing = true;

        // Immediately disable hit-testing so tiles/widgets cannot be clicked during exit fade
        if (RootGrid != null) RootGrid.IsHitTestVisible = false;

        if (_isDragging || _isPotentialDrag || _isRubberBanding)
        {
            CancelActiveDrag();
        }

        if (RootTranslate != null)
        {
            RootTranslate.X = 0.0;
            RootTranslate.Y = 0.0;
        }

        if (TryFindResource("ExitStoryboard") is Storyboard exitStoryboard)
        {
            var sb = exitStoryboard.Clone();
            Timeline.SetDesiredFrameRate(sb, NativeMethods.GetScreenRefreshRate());

            bool completedHandled = false;
            EventHandler? completedHandler = null;
            completedHandler = (s, e) =>
            {
                if (completedHandled) return;
                completedHandled = true;
                if (completedHandler != null) sb.Completed -= completedHandler;

                Hide();
                _isDismissing = false;
                _isFullyActivated = false;
                Topmost = false;

                // Reset back cleanly
                if (RootGrid != null)
                {
                    RootGrid.Opacity = 0.0;
                    RootGrid.IsHitTestVisible = true;
                }
                if (RootTranslate != null)
                {
                    RootTranslate.X = 0.0;
                    RootTranslate.Y = 0.0;
                }

                try { sb.Remove(this); } catch { }

                // Log diagnostic snapshot at idle priority after window is hidden (without forced GC or WorkingSet flush)
                Dispatcher.InvokeAsync(() =>
                {
                    var currentMB = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
                    MetroHub.Core.Services.HiddenDiagnosticsLogger.LogMemorySnapshot("HUB HIDE", currentMB);
                }, DispatcherPriority.ApplicationIdle);
            };

            sb.Completed += completedHandler;
            sb.Begin(this, isControllable: true);
        }
        else
        {
            Hide();
            _isDismissing = false;
            _isFullyActivated = false;
            Topmost = false;
            if (RootGrid != null) RootGrid.IsHitTestVisible = true;

            Dispatcher.InvokeAsync(() =>
            {
                var currentMB = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
                MetroHub.Core.Services.HiddenDiagnosticsLogger.LogMemorySnapshot("HUB HIDE", currentMB);
            }, DispatcherPriority.ApplicationIdle);
        }
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
                if (IsVisible)
                {
                    SnapToWorkArea();
                    ApplyConfiguredBackdrop();
                    await Task.Delay(200);
                    SnapToWorkArea();
                }
            });
        }
        else if (msg == WM_SETTINGCHANGE)
        {
            Dispatcher.InvokeAsync(async () =>
            {
                ApplyConfiguredBackdrop();
                await Task.Delay(400);
                ApplyConfiguredBackdrop();
            });
        }

        return IntPtr.Zero;
    }

    public void SnapToWorkArea()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        // 1. Get raw monitor work area in physical pixels
        Rect workAreaPixels = NativeMethods.GetActiveMonitorWorkArea();

        // 2. Query active monitor DPI scaling factor for this window
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double dpiX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double dpiY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        // 3. Convert physical pixels to WPF Device-Independent Units (DIPs)
        Left = workAreaPixels.Left / dpiX;
        Top = workAreaPixels.Top / dpiY;
        Width = workAreaPixels.Width / dpiX;
        Height = workAreaPixels.Height / dpiY;

        // 4. Force Win32 window bounds in physical pixels so the OS shell aligns exactly
        NativeMethods.SetWindowPos(
            hwnd, 
            IntPtr.Zero,
            (int)workAreaPixels.Left, 
            (int)workAreaPixels.Top,
            (int)workAreaPixels.Width, 
            (int)workAreaPixels.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);

        UpdateLayoutMetrics();
        UpdateCanvasHeight();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        SnapToWorkArea();
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelPendingWallpaperLoad();
        TeardownWallpaperVideo();
        base.OnClosed(e);
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
            _previousForegroundWindow = foreHwnd;
        }

        _isDismissing = false;
        _lastShownTime = DateTime.UtcNow;
        _isFullyActivated = false;

        // Phase 1 (Invisible Layout Pre-computation): Keep RootGrid invisible while HWND, DWM backdrop, and layouts initialize
        if (RootGrid != null)
        {
            RootGrid.Opacity = 0.0;
            RootGrid.IsHitTestVisible = true;
        }

        SnapToWorkArea();
        Show();
        WindowState = WindowState.Normal;
        Topmost = true;
        InstallKeyboardHook();

        ApplyConfiguredBackdrop();

        // Resume video wallpaper playback if active
        bool isVideoActive = WallpaperVideo != null && WallpaperVideo.Visibility == Visibility.Visible && WallpaperVideo.Source != null;
        if (isVideoActive)
        {
            try { WallpaperVideo!.Play(); } catch { }
        }

        IntPtr hwnd = myHwnd != IntPtr.Zero ? myHwnd : new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.ForceForeground(hwnd);
        }

        Activate();
        Focus();
        Keyboard.Focus(this);
        UpdateLayoutMetrics();
        UpdateExposedAddSlots();

        // Phase 2 (Cohesive Fluent Entrance): Dispatch once WPF completes Measure, Arrange, and initial GPU render
        Dispatcher.InvokeAsync(() =>
        {
            PlayOpenAnimation();
        }, DispatcherPriority.Render);

        // Defer widget wake-up and service resumption to background priority so UI opens instantly without frame drops
        Dispatcher.InvokeAsync(() =>
        {
            MetroHub.Widgets.Messaging.WidgetMessenger.Send(new MetroHub.Widgets.Messaging.HubVisibilityChangedMessage(true));
            InstalledAppsService.ResumeWatchers();
            ReinstallWinEventHook();
        }, DispatcherPriority.Background);

        MetroHub.Core.Services.HubState.SetVisibility(true);
        MetroHub.Core.Services.HiddenDiagnosticsLogger.LogTransition(true);
    }

    public void HideScreen(bool restorePreviousFocus = true)
    {
        if (_isDismissing || !IsVisible) return;
        _isDismissing = true;
        UninstallKeyboardHook();

        if (restorePreviousFocus && _previousForegroundWindow != IntPtr.Zero)
        {
            IntPtr targetHwnd = _previousForegroundWindow;
            _previousForegroundWindow = IntPtr.Zero;
            if (NativeMethods.IsWindow(targetHwnd))
            {
                if (NativeMethods.IsIconic(targetHwnd))
                {
                    NativeMethods.ShowWindow(targetHwnd, NativeMethods.SW_RESTORE);
                }

                // Synthetically release the Alt key so the incoming restored window
                // does not receive a lingering Alt modifier or activate its menu bar.
                NativeMethods.keybd_event(NativeMethods.VK_MENU, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);

                NativeMethods.ForceForeground(targetHwnd);
            }
        }
        else
        {
            _previousForegroundWindow = IntPtr.Zero;
        }

        MetroHub.Core.Services.HubState.SetVisibility(false);
        MetroHub.Core.Services.HiddenDiagnosticsLogger.LogTransition(false);
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
        try { SaveGroupsAndLayout(); } catch { }
        if (!Settings.SidebarPinned)
        {
            HideSidebarRail(immediate: true);
        }

        // Halt background services that are pointless when hidden
        InstalledAppsService.PauseWatchers();
        UninstallWinEventHook();

        // Auto-pause video wallpaper when hidden to ensure 0.0% CPU and 0.0% GPU decode
        try { WallpaperVideo?.Pause(); } catch { }

        // Dismiss immediately without UI freeze (GC cleanup is deferred to ApplicationIdle in DismissWithAnimation)
        DismissWithAnimation();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
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

        bool isAltPressed = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt || e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Alt);
        bool isTabOrEsc = e.Key == Key.Tab || e.SystemKey == Key.Tab || e.Key == Key.Escape || e.SystemKey == Key.Escape;

        if (isAltPressed && isTabOrEsc)
        {
            if (_isDragging || _isPotentialDrag || _isRubberBanding)
            {
                CancelActiveDrag();
            }

            // Synthetically release the Alt key in Windows so the incoming restored window
            // does not receive a lingering Alt+Tab gesture and double-switch to a second app.
            NativeMethods.keybd_event(NativeMethods.VK_MENU, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);

            HideScreen(restorePreviousFocus: true);
            e.Handled = true;
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
            UninstallWinEventHook();
            UninstallKeyboardHook();
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
        InstalledAppsService.PauseWatchers();
        UninstallWinEventHook();
        UninstallKeyboardHook();
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

    private void InstallKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero) return;
        _keyboardHookProc = LowLevelKeyboardHookCallback;
        _keyboardHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL,
            _keyboardHookProc,
            IntPtr.Zero,
            0);
    }

    private void UninstallKeyboardHook()
    {
        _suppressNextAltKeyUp = false;
        if (_keyboardHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
            _keyboardHookProc = null;
        }
    }

    private IntPtr LowLevelKeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN)
            {
                var kbd = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                bool isAltDown = (kbd.flags & NativeMethods.LLKHF_ALTDOWN) != 0 ||
                                 (NativeMethods.GetAsyncKeyState((int)NativeMethods.VK_MENU) & 0x8000) != 0;
                bool isTab = kbd.vkCode == NativeMethods.VK_TAB;
                bool isEsc = kbd.vkCode == NativeMethods.VK_ESCAPE;

                if (isAltDown && (isTab || isEsc))
                {
                    if (IsVisible)
                    {
                        _suppressNextAltKeyUp = true;

                        if (!_isDismissing)
                        {
                            Dispatcher.InvokeAsync(() =>
                            {
                                HideScreen(restorePreviousFocus: true);
                            });
                        }

                        // Swallows the Alt+Tab / Alt+Esc keystroke at the OS level so Windows Shell
                        // never receives it and never executes the second jump to another app!
                        return (IntPtr)1;
                    }
                }
            }
            else if (msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP)
            {
                var kbd = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                if (kbd.vkCode == NativeMethods.VK_MENU && _suppressNextAltKeyUp)
                {
                    _suppressNextAltKeyUp = false;
                    // Swallow the Alt keyup so the restored target application does not
                    // receive an orphaned Alt press that activates its menu bar or ribbon.
                    return (IntPtr)1;
                }
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }
}