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

    public static readonly DependencyProperty CurrentScaleProperty =
        DependencyProperty.Register(nameof(CurrentScale), typeof(double), typeof(MainWindow), new PropertyMetadata(1.0));

    public double CurrentScale
    {
        get => (double)GetValue(CurrentScaleProperty);
        set => SetValue(CurrentScaleProperty, value);
    }

    private readonly HotkeyService _hotkeyService = new();
    private DispatcherTimer? _hudTimer;
    private DispatcherTimer? _autoScrollTimer;
    private double _autoScrollVelocityY;
    private bool _isClosingToExit = false;

    private bool _isDismissing
    {
        get => IsDismissing;
        set => IsDismissing = value;
    }

    private Point _canvasRightClickPoint;
    private bool _isAppsLoaded = false;
    private bool _isLoadingApps = false;
    private DateTime _lastAppsRefreshTime = DateTime.UtcNow;

    private IntPtr _winEventHook = IntPtr.Zero;
    private NativeMethods.WinEventDelegate? _winEventDelegate;

    public MainWindow()
    {
        Current = this;
        InitializeComponent();
        DataContext = this;

        LoadData();
        SetupHudTimer();
        SetupAutoScrollTimer();

        Activated += OnWindowActivated;
        RootGrid.LostMouseCapture += OnRootGridLostMouseCapture;
        LostMouseCapture += OnRootGridLostMouseCapture;
        SizeChanged += (s, e) =>
        {
            UpdateLayoutMetrics();
            UpdateCanvasHeight();
            UpdateExposedAddSlots();
            if (AllAppsDrawer != null && AllAppsDrawer.IsOpen && MainContentAreaGrid?.Clip is RectangleGeometry rg)
            {
                rg.Rect = new Rect(320, 0, 50000, 50000);
            }
        };

        InstalledAppsService.AppsCatalogChanged += OnAppsCatalogChanged;
        StartBackgroundAppWarmup();
        _ = Task.Delay(30000).ContinueWith(_ => TriggerBackgroundAppsCatalogRefresh());

        PreviewTextInput += OnWindowPreviewTextInput;
        ContentScrollViewer.ScrollChanged += OnContentScrollViewerScrollChanged;
    }

    private void OnRootGridLostMouseCapture(object sender, MouseEventArgs e)
    {
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
            HideScreen();
        }
    }

    private void LoadData()
    {
        Settings = StorageService.LoadSettings();
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

        // Temporary sample stub widget tile for Phase 0.2 visual verification
        if (!Tiles.Any(t => t.TileType == TileType.Widget))
        {
            var testWidget = new TileModel
            {
                Title = "Widget Preview",
                TargetPath = "stub",
                TileType = TileType.Widget,
                Col = 12,
                Row = 1,
                SpanX = 2,
                SpanY = 2,
                X = GridPlacementService.PixelXFromCol(12),
                Y = GridPlacementService.PixelYFromRow(1)
            };
            Tiles.Add(testWidget);
        }

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

    private void OnBackdropMicaClick(object sender, RoutedEventArgs e)
    {
        Settings.BackdropType = "Mica";
        StorageService.SaveSettings(Settings);
        ApplyConfiguredBackdrop();
    }

    private void OnBackdropAcrylicClick(object sender, RoutedEventArgs e)
    {
        Settings.BackdropType = "Acrylic";
        StorageService.SaveSettings(Settings);
        ApplyConfiguredBackdrop();
    }

    private void OnBackdropDesktopWallpaperClick(object sender, RoutedEventArgs e)
    {
        Settings.BackdropType = "DesktopWallpaper";
        StorageService.SaveSettings(Settings);
        ApplyConfiguredBackdrop();
    }

    private void OnBackdropCustomImageClick(object sender, RoutedEventArgs e)
    {
        IsDialogOpen = true;
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Custom Wallpaper",
                Filter = "Image Files (*.png;*.jpg;*.jpeg;*.webp;*.bmp)|*.png;*.jpg;*.jpeg;*.webp;*.bmp|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) == true)
            {
                Settings.BackdropType = "Wallpaper";
                Settings.CustomWallpaperPath = dialog.FileName;
                StorageService.SaveSettings(Settings);
                ApplyConfiguredBackdrop();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Failed to open custom wallpaper dialog: {ex.Message}");
        }
        finally
        {
            IsDialogOpen = false;
            Activate();
        }
    }

    private void OnWallpaperDimLightClick(object sender, RoutedEventArgs e) => SetWallpaperDim(0.35);
    private void OnWallpaperDimBalancedClick(object sender, RoutedEventArgs e) => SetWallpaperDim(0.50);
    private void OnWallpaperDimHeavyClick(object sender, RoutedEventArgs e) => SetWallpaperDim(0.65);

    private void SetWallpaperDim(double opacity)
    {
        Settings.WallpaperDimOpacity = opacity;
        StorageService.SaveSettings(Settings);
        ApplyConfiguredBackdrop();
    }

    private void OnParallaxEnabledClick(object sender, RoutedEventArgs e)
    {
        Settings.WallpaperParallax = true;
        StorageService.SaveSettings(Settings);
        UpdateBackdropMenuChecks();
        UpdateWallpaperParallax();
    }

    private void OnParallaxDisabledClick(object sender, RoutedEventArgs e)
    {
        Settings.WallpaperParallax = false;
        StorageService.SaveSettings(Settings);
        UpdateBackdropMenuChecks();
        UpdateWallpaperParallax();
    }

    private const double BaseReferenceWidth = 1920.0;

    private void UpdateScaleFactor(double viewportWidth)
    {
        // 1.0 native pixel scale for 1080p, 768p, and 720p to keep text crisp.
        // Scales up only on 4K monitors (viewport > 2500)
        double scale = viewportWidth > 2500 ? Math.Clamp(viewportWidth / BaseReferenceWidth, 1.0, 1.75) : 1.0;
        CurrentScale = scale;
    }

    private void UpdateLayoutMetrics()
    {
        double viewportWidth = ContentScrollViewer?.ActualWidth > 0
            ? ContentScrollViewer.ActualWidth
            : (Width > 0 ? Width : BaseReferenceWidth);

        UpdateScaleFactor(viewportWidth);

        GridPlacementService.UpdateMetrics(viewportWidth, Groups);
        double margin = GridPlacementService.OriginX;

        if (FooterGrid != null)
        {
            FooterGrid.Margin = new Thickness(margin, 0, margin, 0);
        }
    }

    private void UpdateCanvasHeight()
    {
        if (MainCanvasGrid == null) return;
        double maxTileBottom = (Tiles != null && Tiles.Any()) ? Tiles.Max(t => t.Y + t.HeightPixels) + 120 : 600;
        double maxGroupBottom = (Groups != null && Groups.Any()) ? Groups.Max(g => g.Y + 120) : 600;
        double maxBottom = Math.Max(maxTileBottom, maxGroupBottom);
        double viewportHeight = ContentScrollViewer?.ActualHeight > 0 ? ContentScrollViewer.ActualHeight : 600;
        MainCanvasGrid.MinHeight = Math.Max(viewportHeight, maxBottom);
    }

    private void SetupAutoScrollTimer()
    {
        _autoScrollTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _autoScrollTimer.Tick += OnAutoScrollTick;
    }

    private void StopAutoScroll()
    {
        _autoScrollVelocityY = 0;
        if (_autoScrollTimer != null && _autoScrollTimer.IsEnabled)
        {
            _autoScrollTimer.Stop();
        }
    }

    private void UpdateAutoScrollVelocity()
    {
        if (!_isDragging || ContentScrollViewer == null)
        {
            StopAutoScroll();
            return;
        }

        Point svMouse = Mouse.GetPosition(ContentScrollViewer);
        double svHeight = ContentScrollViewer.ActualHeight;
        if (svHeight <= 0)
        {
            StopAutoScroll();
            return;
        }

        const double edgeThreshold = 80.0;
        const double maxVelocity = 24.0;

        if (svMouse.Y > svHeight - edgeThreshold && svMouse.Y <= svHeight + 120)
        {
            double excess = svMouse.Y - (svHeight - edgeThreshold);
            double fraction = Math.Clamp(excess / edgeThreshold, 0.15, 2.0);
            _autoScrollVelocityY = fraction * maxVelocity;
            if (_autoScrollTimer != null && !_autoScrollTimer.IsEnabled)
            {
                _autoScrollTimer.Start();
            }
        }
        else if (svMouse.Y < edgeThreshold && svMouse.Y >= -120)
        {
            double excess = edgeThreshold - svMouse.Y;
            double fraction = Math.Clamp(excess / edgeThreshold, 0.15, 2.0);
            _autoScrollVelocityY = -fraction * maxVelocity;
            if (_autoScrollTimer != null && !_autoScrollTimer.IsEnabled)
            {
                _autoScrollTimer.Start();
            }
        }
        else
        {
            StopAutoScroll();
        }
    }

    private void OnAutoScrollTick(object? sender, EventArgs e)
    {
        if (!_isDragging || ContentScrollViewer == null || Math.Abs(_autoScrollVelocityY) < 0.1)
        {
            StopAutoScroll();
            return;
        }

        UpdateAutoScrollVelocity();
        if (Math.Abs(_autoScrollVelocityY) < 0.1) return;

        if (_autoScrollVelocityY > 0 && MainCanvasGrid != null)
        {
            double currentBottom = ContentScrollViewer.VerticalOffset + ContentScrollViewer.ActualHeight;
            if (currentBottom + 300 > MainCanvasGrid.MinHeight)
            {
                MainCanvasGrid.MinHeight = currentBottom + 600;
                ContentScrollViewer.UpdateLayout();
            }
        }

        double newOffset = Math.Clamp(
            ContentScrollViewer.VerticalOffset + _autoScrollVelocityY,
            0,
            Math.Max(0, ContentScrollViewer.ScrollableHeight));

        ContentScrollViewer.ScrollToVerticalOffset(newOffset);
        ContentScrollViewer.UpdateLayout();

        Point currentCanvasMouse = TilesListBox != null 
            ? Mouse.GetPosition(TilesListBox) 
            : (MainCanvasGrid != null ? Mouse.GetPosition(MainCanvasGrid) : Mouse.GetPosition(this));
        ProcessDragMovement(currentCanvasMouse);
    }

    private void SetupHudTimer()
    {
        _hudTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _hudTimer.Tick += (s, e) =>
        {
            if (IsVisible)
            {
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

        bool isWallpaper = string.Equals(Settings.BackdropType, "Wallpaper", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(Settings.BackdropType, "DesktopWallpaper", StringComparison.OrdinalIgnoreCase);

        if (isWallpaper)
        {
            NativeMethods.ApplyMica(hwnd, dark: true, NativeMethods.DWMSBT_NONE);
            if (RootGrid != null)
            {
                RootGrid.Background = System.Windows.Media.Brushes.Transparent;
            }
            UpdateWallpaperDisplay();
        }
        else
        {
            if (CustomWallpaperHost != null)
            {
                CustomWallpaperHost.Visibility = Visibility.Collapsed;
            }

            if (string.Equals(Settings.BackdropType, "Acrylic", StringComparison.OrdinalIgnoreCase))
            {
                NativeMethods.ApplyMica(hwnd, dark: true, NativeMethods.DWMSBT_TRANSIENTWINDOW);
                if (RootGrid != null)
                {
                    RootGrid.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x99, 0x0D, 0x0D, 0x11));
                }
            }
            else if (string.Equals(Settings.BackdropType, "MicaAlt", StringComparison.OrdinalIgnoreCase))
            {
                NativeMethods.ApplyMica(hwnd, dark: true, NativeMethods.DWMSBT_TABBEDWINDOW);
                if (RootGrid != null)
                {
                    RootGrid.Background = System.Windows.Media.Brushes.Transparent;
                }
            }
            else
            {
                NativeMethods.ApplyMica(hwnd, dark: true, NativeMethods.DWMSBT_MAINWINDOW);
                if (RootGrid != null)
                {
                    RootGrid.Background = System.Windows.Media.Brushes.Transparent;
                }
            }
        }

        UpdateBackdropMenuChecks();
    }

    public static BitmapImage? LoadOptimizedBitmap(string path, int decodeWidth = 1920)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0)
            {
                bitmap.DecodePixelWidth = decodeWidth;
            }
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public static string? GetActiveDesktopWallpaperPath()
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string transcoded = Path.Combine(appData, @"Microsoft\Windows\Themes\TranscodedWallpaper");
            if (File.Exists(transcoded))
            {
                return transcoded;
            }

            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            string? regPath = key?.GetValue("WallPaper") as string;
            if (!string.IsNullOrEmpty(regPath) && File.Exists(regPath))
            {
                return regPath;
            }
        }
        catch { }

        return null;
    }

    private void UpdateWallpaperDisplay()
    {
        if (CustomWallpaperHost == null || WallpaperImage == null || WallpaperScrim == null) return;

        string? imagePath = null;
        if (string.Equals(Settings.BackdropType, "DesktopWallpaper", StringComparison.OrdinalIgnoreCase))
        {
            imagePath = GetActiveDesktopWallpaperPath();
        }
        else if (string.Equals(Settings.BackdropType, "Wallpaper", StringComparison.OrdinalIgnoreCase))
        {
            imagePath = Settings.CustomWallpaperPath;
        }

        if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
        {
            int decodeWidth = (int)Math.Max(1280, ActualWidth > 0 ? ActualWidth : 1920);
            var bmp = LoadOptimizedBitmap(imagePath, decodeWidth);
            if (bmp != null)
            {
                WallpaperImage.Source = bmp;
                CustomWallpaperHost.Visibility = Visibility.Visible;

                double dim = Math.Clamp(Settings.WallpaperDimOpacity, 0.1, 0.9);
                byte alpha = (byte)(255 * dim);
                WallpaperScrim.Background = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
                UpdateWallpaperParallax();
                return;
            }
        }

        // Fallback to clean dark background if image is missing
        CustomWallpaperHost.Visibility = Visibility.Collapsed;
        UpdateWallpaperParallax();
        if (RootGrid != null)
        {
            RootGrid.Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x10, 0x10, 0x14));
        }
    }

    private void OnContentScrollViewerScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateWallpaperParallax();
    }

    private void UpdateWallpaperParallax()
    {
        if (WallpaperTranslateTransform == null || WallpaperImage == null) return;

        bool isWallpaper = string.Equals(Settings.BackdropType, "Wallpaper", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(Settings.BackdropType, "DesktopWallpaper", StringComparison.OrdinalIgnoreCase);

        if (!isWallpaper || !Settings.WallpaperParallax || ContentScrollViewer == null)
        {
            WallpaperTranslateTransform.Y = 0;
            WallpaperImage.Margin = new Thickness(0);
            return;
        }

        const double maxShift = 90.0;
        WallpaperImage.Margin = new Thickness(0, 0, 0, -maxShift);

        if (ContentScrollViewer.ScrollableHeight > 0)
        {
            double ratio = Math.Clamp(ContentScrollViewer.VerticalOffset / ContentScrollViewer.ScrollableHeight, 0.0, 1.0);
            WallpaperTranslateTransform.Y = -ratio * maxShift;
        }
        else
        {
            WallpaperTranslateTransform.Y = 0;
        }
    }

    private void UpdateBackdropMenuChecks()
    {
        if (BackdropMicaItem != null)
            BackdropMicaItem.IsChecked = string.Equals(Settings.BackdropType, "Mica", StringComparison.OrdinalIgnoreCase);
        if (BackdropAcrylicItem != null)
            BackdropAcrylicItem.IsChecked = string.Equals(Settings.BackdropType, "Acrylic", StringComparison.OrdinalIgnoreCase);
        if (BackdropDesktopWallpaperItem != null)
            BackdropDesktopWallpaperItem.IsChecked = string.Equals(Settings.BackdropType, "DesktopWallpaper", StringComparison.OrdinalIgnoreCase);
        if (BackdropCustomImageItem != null)
            BackdropCustomImageItem.IsChecked = string.Equals(Settings.BackdropType, "Wallpaper", StringComparison.OrdinalIgnoreCase);

        double dim = Settings.WallpaperDimOpacity;
        if (DimLightItem != null) DimLightItem.IsChecked = Math.Abs(dim - 0.35) < 0.05;
        if (DimBalancedItem != null) DimBalancedItem.IsChecked = Math.Abs(dim - 0.50) < 0.05;
        if (DimHeavyItem != null) DimHeavyItem.IsChecked = Math.Abs(dim - 0.65) < 0.05;

        bool isWallpaper = string.Equals(Settings.BackdropType, "Wallpaper", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(Settings.BackdropType, "DesktopWallpaper", StringComparison.OrdinalIgnoreCase);

        if (ParallaxEnabledItem != null)
        {
            ParallaxEnabledItem.IsChecked = Settings.WallpaperParallax;
            ParallaxEnabledItem.IsEnabled = isWallpaper;
        }
        if (ParallaxDisabledItem != null)
        {
            ParallaxDisabledItem.IsChecked = !Settings.WallpaperParallax;
            ParallaxDisabledItem.IsEnabled = isWallpaper;
        }
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
                    HideScreen();
                }
            });
        }
    }

    public void PlayOpenAnimation()
    {
        _isDismissing = false;

        var edge = NativeMethods.GetActiveMonitorTaskbarEdge();
        double startX = 0.0;
        double startY = 0.0;

        switch (edge)
        {
            case NativeMethods.TaskbarEdge.Bottom:
                startY = 36.0;
                break;
            case NativeMethods.TaskbarEdge.Top:
                startY = -36.0;
                break;
            case NativeMethods.TaskbarEdge.Left:
                startX = -36.0;
                break;
            case NativeMethods.TaskbarEdge.Right:
                startX = 36.0;
                break;
        }

        if (RootTranslate != null)
        {
            RootTranslate.X = startX;
            RootTranslate.Y = startY;
        }

        if (TryFindResource("OpenStoryboard") is Storyboard openStoryboard)
        {
            var sb = openStoryboard.Clone();

            foreach (var child in sb.Children)
            {
                if (child is DoubleAnimationUsingKeyFrames kf)
                {
                    var prop = Storyboard.GetTargetProperty(kf)?.Path;
                    if (prop?.EndsWith("Y", StringComparison.OrdinalIgnoreCase) == true && kf.KeyFrames.Count >= 2)
                    {
                        kf.KeyFrames[0].Value = startY;
                        kf.KeyFrames[1].Value = 0.0;
                    }
                    else if (prop?.EndsWith("X", StringComparison.OrdinalIgnoreCase) == true && kf.KeyFrames.Count >= 2)
                    {
                        kf.KeyFrames[0].Value = startX;
                        kf.KeyFrames[1].Value = 0.0;
                    }
                }
            }

            sb.Begin(this);
        }
        else
        {
            if (RootGrid != null) RootGrid.Opacity = 1.0;
            if (RootTranslate != null) { RootTranslate.X = 0.0; RootTranslate.Y = 0.0; }
        }
    }

    public void DismissWithAnimation()
    {
        if (_isDismissing || !IsVisible) return;

        if (_isDragging || _isPotentialDrag || _isRubberBanding)
        {
            CancelActiveDrag();
        }

        var edge = NativeMethods.GetActiveMonitorTaskbarEdge();
        double endX = 0.0;
        double endY = 0.0;

        switch (edge)
        {
            case NativeMethods.TaskbarEdge.Bottom:
                endY = 24.0;
                break;
            case NativeMethods.TaskbarEdge.Top:
                endY = -24.0;
                break;
            case NativeMethods.TaskbarEdge.Left:
                endX = -24.0;
                break;
            case NativeMethods.TaskbarEdge.Right:
                endX = 24.0;
                break;
        }

        if (TryFindResource("ExitStoryboard") is Storyboard exitStoryboard)
        {
            _isDismissing = true;
            var sb = exitStoryboard.Clone();

            foreach (var child in sb.Children)
            {
                if (child is DoubleAnimationUsingKeyFrames kf)
                {
                    var prop = Storyboard.GetTargetProperty(kf)?.Path;
                    if (prop?.EndsWith("Y", StringComparison.OrdinalIgnoreCase) == true && kf.KeyFrames.Count >= 2)
                    {
                        kf.KeyFrames[0].Value = 0.0;
                        kf.KeyFrames[1].Value = endY;
                    }
                    else if (prop?.EndsWith("X", StringComparison.OrdinalIgnoreCase) == true && kf.KeyFrames.Count >= 2)
                    {
                        kf.KeyFrames[0].Value = 0.0;
                        kf.KeyFrames[1].Value = endX;
                    }
                }
            }

            sb.Completed += (s, e) =>
            {
                Hide();
                _isDismissing = false;
                _isFullyActivated = false;
                Topmost = false;

                // Reset back cleanly
                if (RootGrid != null) RootGrid.Opacity = 0.0;
                if (RootTranslate != null)
                {
                    RootTranslate.X = 0.0;
                    RootTranslate.Y = 0.0;
                }

                NativeMethods.FlushMemory();
            };
            sb.Begin(this);
        }
        else
        {
            Hide();
            _isDismissing = false;
            _isFullyActivated = false;
            Topmost = false;
            NativeMethods.FlushMemory();
        }
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

    private void OnHotkeyPressed()
    {
        Dispatcher.Invoke(ToggleVisibility);
    }

    public void ToggleVisibility()
    {
        if (IsVisible && !_isDismissing)
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
        if (_isDragging || _isPotentialDrag || _isRubberBanding)
        {
            CancelActiveDrag();
        }

        _isDismissing = false;
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

        Dispatcher.InvokeAsync(() =>
        {
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.ForceForeground(hwnd);
            }
            ApplyConfiguredBackdrop();
            Activate();
            Focus();
        }, DispatcherPriority.Render);

        PlayOpenAnimation();
        MetroHub.Widgets.Messaging.WidgetMessenger.Send(new MetroHub.Widgets.Messaging.HubVisibilityChangedMessage(true));
    }

    public void HideScreen()
    {
        if (AllAppsDrawer != null && AllAppsDrawer.IsOpen)
        {
            AllAppsDrawer.Close();
            SidebarRail?.SetAppsDrawerActive(false);
            if (MainContentAreaGrid != null) MainContentAreaGrid.Clip = null;
        }
        MetroHub.Widgets.Messaging.WidgetMessenger.Send(new MetroHub.Widgets.Messaging.HubVisibilityChangedMessage(false));
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

            HideScreen();
            e.Handled = true;
            return;
        }

        if ((e.Key == Key.Tab || e.SystemKey == Key.Tab) && ((Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt || e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Alt)))
        {
            if (_isDragging || _isPotentialDrag || _isRubberBanding)
            {
                CancelActiveDrag();
            }
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
    private double _dragOffsetX;
    private double _dragOffsetY;
    private int _dragOriginalCol;
    private int _dragOriginalRow;
    private bool _isPotentialDrag;
    private bool _isDragging;
    private bool _isLaunchingTile;
    private DateTime _lastTileLaunchTime = DateTime.MinValue;
    private const int GroupColWidth = 8;
    private TileGroupModel? _hoveredTargetGroup;
    private bool _isGroupDrag;
    private TileGroupModel? _draggedGroupModel;
    private double _draggedGroupOffsetX;
    private double _draggedGroupOffsetY;
    private double _draggedPlateOffsetX;
    private double _draggedPlateOffsetY;
    private int _groupDragTargetColIndex;
    private int _groupDragTargetRow;

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

        var toRemove = currentTiles.Where(t => !targetDict.ContainsKey(t.Id)).ToList();
        foreach (var t in toRemove)
        {
            t.Teardown();
            Tiles.Remove(t);
        }

        var toAdd = targetTiles.Where(t => !currentDict.ContainsKey(t.Id)).ToList();
        foreach (var t in toAdd)
        {
            Tiles.Add(t);
        }

        var targetGroupDict = targetGroups.ToDictionary(g => g.Id);
        var currentGroups = Groups.ToList();
        var currentGroupDict = currentGroups.ToDictionary(g => g.Id);

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
                existingG.IsLocked = tg.IsLocked;
                existingG.TintColor = tg.TintColor;
                existingG.ColumnIndex = tg.ColumnIndex;
                existingG.OrderIndex = tg.OrderIndex;
            }
        }

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
            existing.IsLocked = target.IsLocked;

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
        if (_isDragging)
        {
            CancelActiveDrag();
        }

        if (e.LeftButton != MouseButtonState.Pressed) return;

        DependencyObject? dep = e.OriginalSource as DependencyObject;

        if (FindParent<System.Windows.Controls.Primitives.Thumb>(dep) != null)
        {
            _isPotentialDrag = false;
            _isDragging = false;
            _draggedTile = null;
            return;
        }

        if (FindParent<Presentation.Controls.GroupHeaderControl>(dep) != null)
        {
            _isPotentialDrag = false;
            _isDragging = false;
            _draggedTile = null;
            return;
        }

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

        if (FooterGrid != null && FooterGrid.IsMouseOver) return;

        Point canvasMouse = TilesListBox != null ? e.GetPosition(TilesListBox) : e.GetPosition(this);
        bool isCtrlDown = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        if (FindParent<System.Windows.Controls.Button>(dep) != null ||
            FindParent<System.Windows.Controls.Primitives.Thumb>(dep) != null ||
            FindParent<System.Windows.Controls.Primitives.RangeBase>(dep) != null ||
            FindParent<System.Windows.Controls.Slider>(dep) != null ||
            FindParent<System.Windows.Controls.ProgressBar>(dep) != null ||
            FindParent<FrameworkElement>(dep)?.Name == "SeekbarContainer" ||
            FindParent<FrameworkElement>(dep)?.Name == "SeekThumb" ||
            FindParent<FrameworkElement>(dep)?.Name == "SeekTrackBg" ||
            FindParent<FrameworkElement>(dep)?.Name == "SeekProgressFill" ||
            FindParent<FrameworkElement>(dep)?.Tag as string == "InteractiveControl")
        {
            _isPotentialDrag = false;
            _isDragging = false;
            _draggedTile = null;
            return;
        }

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
                tile.IsSelected = !tile.IsSelected;
                _dragBeganWithSelection = tile.IsSelected;
            }
            else
            {
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

            if (tile.IsSelected)
            {
                _draggedCluster = Tiles.Where(t => t.IsSelected).ToList();
            }
            else
            {
                _draggedCluster = new List<TileModel> { tile };
            }

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
            if (!(tile.TileType == TileType.Widget && (tile.TargetPath == "calendar" || tile.TargetPath == "media" || tile.TargetPath == "pomodoro" || tile.TargetPath == "photos")))
            {
                _draggedControl.AnimatePressDown();
            }
        }
        else
        {
            _isPotentialDrag = false;
            _isDragging = false;
            _draggedTile = null;
            _draggedControl = null;
            _draggedCluster.Clear();

            if (FindParent<System.Windows.Controls.Button>(dep) != null ||
                FindParent<System.Windows.Controls.Primitives.Thumb>(dep) != null ||
                FindParent<ContextMenu>(dep) != null ||
                FindParent<FrameworkElement>(dep)?.Name == "SeekbarContainer" ||
                FindParent<FrameworkElement>(dep)?.Name == "SeekThumb" ||
                FindParent<FrameworkElement>(dep)?.Name == "SeekTrackBg" ||
                FindParent<FrameworkElement>(dep)?.Name == "SeekProgressFill" ||
                FindParent<FrameworkElement>(dep)?.Tag as string == "InteractiveControl")
            {
                return;
            }

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
                        t.IsSelected = intersects
                            ? !_preRubberBandSelected.Contains(t)
                            : _preRubberBandSelected.Contains(t);
                    }
                    else
                    {
                        t.IsSelected = intersects;
                    }
                }
            }

            return;
        }

        if (_isPotentialDrag && !_isDragging)
        {
            if (e.LeftButton != MouseButtonState.Pressed && Mouse.LeftButton != MouseButtonState.Pressed)
            {
                CancelActiveDrag();
                return;
            }

            if (_draggedTile != null)
            {
                bool lockedTile = _draggedCluster.Any(t =>
                    t.IsLocked || (!string.IsNullOrEmpty(t.Group) &&
                                   Groups.FirstOrDefault(g => g.Id == t.Group)?.IsLocked == true));

                Point current = e.GetPosition(this);
                Vector diff = current - _dragStartPoint;
                if (lockedTile && (Math.Abs(diff.X) > 5 || Math.Abs(diff.Y) > 5))
                {
                    _isPotentialDrag = false;
                    var g = Groups.FirstOrDefault(gr => gr.Id == _draggedTile?.Group);
                    if (g != null && g.IsLocked)
                    {
                        FlashLockedGroupPerimeter(g);
                    }

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

                    DropSlotIndicator.Width = Math.Max(56, _clusterRelBounds.MaxRelX - _clusterRelBounds.MinRelX);
                    DropSlotIndicator.Height = Math.Max(56, _clusterRelBounds.MaxRelY - _clusterRelBounds.MinRelY);
                    DropSlotIndicator.Visibility = Visibility.Visible;
                }
            }
        }

        if (!_isDragging && !_isRubberBanding && TilesListBox != null)
        {
            UpdateAmbientReveal(canvasMouse);
        }

        if (_isDragging)
        {
            if (e.LeftButton != MouseButtonState.Pressed && Mouse.LeftButton != MouseButtonState.Pressed)
            {
                CancelActiveDrag();
                return;
            }

            if (_draggedTile != null || (_isGroupDrag && _draggedGroupModel != null))
            {
                UpdateAutoScrollVelocity();
                ProcessDragMovement(canvasMouse);
            }
            else
            {
                StopAutoScroll();
            }
        }
        else
        {
            StopAutoScroll();
        }
    }

    private void ProcessDragMovement(Point canvasMouse)
    {
        if (!_isDragging || (_draggedTile == null && (!_isGroupDrag || _draggedGroupModel == null)))
        {
            return;
        }

        UpdateLayoutMetrics();
        int maxCols = GridPlacementService.MaxCols;

        double rawAnchorX = canvasMouse.X - _dragOffsetX;
        double rawAnchorY = canvasMouse.Y - _dragOffsetY;

        double minAllowedAnchorX = GridPlacementService.OriginX - _clusterRelBounds.MinRelX;
        double maxAllowedAnchorX = GridPlacementService.PixelXFromCol(maxCols) - _clusterRelBounds.MaxRelX;
        double minAllowedAnchorY = GridPlacementService.OriginY - _clusterRelBounds.MinRelY;

        if (_isGroupDrag && _draggedGroupModel != null)
        {
            double minGroupAnchorY = GridPlacementService.OriginY + 8 - _draggedGroupOffsetY;
            minAllowedAnchorY = Math.Max(minAllowedAnchorY, minGroupAnchorY);
        }

        if (maxAllowedAnchorX < minAllowedAnchorX) maxAllowedAnchorX = minAllowedAnchorX;

        double maxAllowedAnchorY = Math.Max(3000, (MainCanvasGrid?.MinHeight ?? 3000) + 1000);
        double clampedAnchorX = Math.Max(minAllowedAnchorX, Math.Min(rawAnchorX, maxAllowedAnchorX));
        double clampedAnchorY = Math.Max(minAllowedAnchorY, Math.Min(rawAnchorY, maxAllowedAnchorY));

        if (MainCanvasGrid != null && clampedAnchorY + 400 > MainCanvasGrid.MinHeight)
        {
            MainCanvasGrid.MinHeight = clampedAnchorY + 600;
        }

        if (_draggedTile != null)
        {
            foreach (var cTile in _draggedCluster)
            {
                double relX = _dragClusterOriginals.TryGetValue(cTile, out var orig)
                    ? orig.X - _dragClusterOriginals[_draggedTile].X
                    : 0;
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
        }

        int minAllowedCol = Math.Max(0, -_clusterRelGridBounds.MinRelCol);
        int maxAllowedCol = Math.Max(minAllowedCol, maxCols - _clusterRelGridBounds.MaxRelCol);
        int rawCol = Math.Max(0, Math.Clamp(GridPlacementService.ColFromPixel(clampedAnchorX), minAllowedCol, maxAllowedCol));
        int clusterSpanX = Math.Max(1, _clusterRelGridBounds.MaxRelCol - _clusterRelGridBounds.MinRelCol);
        int anchorCol = GridPlacementService.SnapColToValidTrackSlot(rawCol, clusterSpanX, maxCols, clampedAnchorX);

        int minAllowedRow = _isGroupDrag
            ? (_draggedTile != null
                ? Math.Max(1, Math.Max(-_clusterRelGridBounds.MinRelRow, 1 - _clusterRelGridBounds.MinRelRow))
                : 0)
            : Math.Max(1, 1 - _clusterRelGridBounds.MinRelRow);
        int anchorRow = Math.Max(minAllowedRow, GridPlacementService.RowFromPixel(clampedAnchorY));

        int rawAnchorCol = anchorCol;
        int rawAnchorRow = anchorRow;

        if (!_isGroupDrag && Groups.Count > 0)
        {
            UpdateGroupDropHighlight(canvasMouse, clampedAnchorX, clampedAnchorY);
        }
        else
        {
            HideGroupDropHighlight();
        }

        if (_isGroupDrag && _draggedGroupModel != null)
        {
            _draggedGroupModel.X = clampedAnchorX + _draggedGroupOffsetX;
            _draggedGroupModel.Y = Math.Max(GridPlacementService.OriginY + 8, clampedAnchorY + _draggedGroupOffsetY);

            var gContainer = GroupsListBox?.ItemContainerGenerator.ContainerFromItem(_draggedGroupModel) as ContentPresenter;
            if (gContainer != null)
            {
                Canvas.SetLeft(gContainer, _draggedGroupModel.X);
                Canvas.SetTop(gContainer, _draggedGroupModel.Y);
            }

            _draggedGroupModel.PlateX = clampedAnchorX + _draggedPlateOffsetX;
            _draggedGroupModel.PlateY = clampedAnchorY + _draggedPlateOffsetY;

            var plateContainer = GroupTintBackplates?.ItemContainerGenerator.ContainerFromItem(_draggedGroupModel) as ContentPresenter;
            if (plateContainer != null)
            {
                Canvas.SetLeft(plateContainer, _draggedGroupModel.PlateX);
                Canvas.SetTop(plateContainer, _draggedGroupModel.PlateY);
            }

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
        else if (_hoveredTargetGroup != null && _draggedTile != null)
        {
            if (GroupInsertionLine != null) GroupInsertionLine.Visibility = Visibility.Collapsed;
            HideGapDropHighlight();

            if (_hoveredTargetGroup.IsLocked)
            {
                DropSlotIndicator.Visibility = Visibility.Collapsed;
            }
            else
            {
                var allMembers = Tiles.Where(t => t.Group == _hoveredTargetGroup.Id).ToList();
                int gMaxR = allMembers.Count > 0 ? allMembers.Max(t => t.Row + t.SpanY) : _hoveredTargetGroup.Row + 1;
                double memberMaxY = allMembers.Count > 0 ? allMembers.Max(t => t.Y + t.HeightPixels) : _hoveredTargetGroup.Y + 60;
                if (_hoveredTargetGroup.PlateHeight > 0)
                {
                    memberMaxY = Math.Max(memberMaxY, _hoveredTargetGroup.PlateY + _hoveredTargetGroup.PlateHeight);
                }

                int queryRow = rawAnchorRow;
                int mouseRow = GridPlacementService.RowFromPixel(canvasMouse.Y);
                if (canvasMouse.Y >= memberMaxY - 12 || mouseRow >= gMaxR)
                {
                    queryRow = Math.Max(queryRow, gMaxR);
                }

                var (wrapCol, wrapRow) = FindGroupWrapPosition(_hoveredTargetGroup, _draggedTile, rawAnchorCol, queryRow);
                double wrapX = GridPlacementService.PixelXFromCol(wrapCol);
                double wrapY = GridPlacementService.PixelYFromRow(wrapRow);

                DropSlotIndicator.Width = _draggedTile.WidthPixels;
                DropSlotIndicator.Height = _draggedTile.HeightPixels;
                Canvas.SetLeft(DropSlotIndicator, wrapX);
                Canvas.SetTop(DropSlotIndicator, wrapY);
                DropSlotIndicator.Visibility = Visibility.Visible;
            }
        }
        else
        {
            if (GroupInsertionLine != null) GroupInsertionLine.Visibility = Visibility.Collapsed;

            string? originGroupId = _draggedTile?.Group;
            int clusterMinCol = anchorCol + _clusterRelGridBounds.MinRelCol;
            int clusterMaxCol = anchorCol + _clusterRelGridBounds.MaxRelCol;
            int clusterStartRow = anchorRow + _clusterRelGridBounds.MinRelRow;
            int clusterEndRow = anchorRow + _clusterRelGridBounds.MaxRelRow;

            foreach (var g in Groups)
            {
                if (originGroupId != null && g.Id == originGroupId) continue;

                var members = Tiles.Where(t => t.Group == g.Id).ToList();
                if (members.Count == 0 && string.IsNullOrWhiteSpace(g.Title)) continue;

                int gMinC = (g.IsLocked && members.Count > 0) ? Math.Min(g.Col, members.Min(t => t.Col)) : g.Col;
                int gMaxC = (g.IsLocked && members.Count > 0) ? members.Max(t => t.Col + t.SpanX) : g.Col + GridPlacementService.GroupColWidth;
                int gMinR = g.Row;
                int gMaxR = members.Count > 0 ? members.Max(t => t.Row + t.SpanY) : g.Row + 1;

                if (clusterMinCol < gMaxC && clusterMaxCol > gMinC)
                {
                    if (clusterStartRow <= gMaxR && clusterEndRow > gMinR)
                    {
                        anchorRow = (gMaxR + 1) - _clusterRelGridBounds.MinRelRow;
                    }
                }
            }

            double snappedAnchorX = GridPlacementService.PixelXFromCol(anchorCol);
            double snappedAnchorY = GridPlacementService.PixelYFromRow(anchorRow);

            DropSlotIndicator.Width = Math.Max(56, _clusterRelBounds.MaxRelX - _clusterRelBounds.MinRelX);
            DropSlotIndicator.Height = Math.Max(56, _clusterRelBounds.MaxRelY - _clusterRelBounds.MinRelY);
            Canvas.SetLeft(DropSlotIndicator, snappedAnchorX + _clusterRelBounds.MinRelX);
            Canvas.SetTop(DropSlotIndicator, snappedAnchorY + _clusterRelBounds.MinRelY);
            DropSlotIndicator.Visibility = Visibility.Visible;

            HideGapDropHighlight();
        }
    }

    private void OnCanvasPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
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

        if (_isDragging)
        {
            StopAutoScroll();
            _isDragging = false;
            _isPotentialDrag = false;
            DropSlotIndicator.Visibility = Visibility.Collapsed;
            if (GroupInsertionLine != null) GroupInsertionLine.Visibility = Visibility.Collapsed;
            RootGrid.ReleaseMouseCapture();

            var targetGroup = _hoveredTargetGroup;
            HideGroupDropHighlight();
            HideGapDropHighlight();

            if (_isGroupDrag && _draggedGroupModel != null)
            {
                _isGroupDrag = false;
                var movedGroup = _draggedGroupModel;
                _draggedGroupModel = null;

                int requestedCol = _groupDragTargetColIndex;
                int requestedRow = _groupDragTargetRow;
                int groupH = GridPlacementService.CalculateGroupHeightRows(movedGroup, Tiles);
                GridPlacementService.WouldDisplaceLockedGroup(requestedCol, requestedRow, groupH, Groups, Tiles,
                    movedGroup, out var conflictingLocked);

                var modified = GridPlacementService.InsertGroupAndResolveCollisions(
                    movedGroup,
                    requestedCol,
                    requestedRow,
                    Groups,
                    Tiles);

                if (conflictingLocked != null)
                {
                    FlashLockedGroupPerimeter(conflictingLocked);
                }

                AnimateModifiedTiles(modified);
                UpdateGroupHeaderPositions(animate: true);
                UpdateCanvasHeight();
                SaveGroupsAndLayout();

                var gc = GroupsListBox?.ItemContainerGenerator.ContainerFromItem(movedGroup) as ContentPresenter;
                if (gc != null) Panel.SetZIndex(gc, 0);

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

            if (_draggedTile != null)
            {
                UpdateLayoutMetrics();
                int maxCols = GridPlacementService.MaxCols;

                var originGroups = _dragClusterOriginals.Keys
                    .Where(t => !string.IsNullOrEmpty(t.Group))
                    .Select(t => Groups.FirstOrDefault(g => g.Id == t.Group))
                    .Where(g => g != null)
                    .Distinct()
                    .ToList();

                var originOldBottoms = originGroups.ToDictionary(
                    g => g!,
                    g => GridPlacementService.GetGroupBoundingBox(g!, Tiles).MaxRow);

                if (targetGroup != null)
                {
                    if (targetGroup.IsLocked)
                    {
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
                        int dropCol = GridPlacementService.ColFromPixel(_draggedTile.X);
                        int dropRow = GridPlacementService.RowFromPixel(_draggedTile.Y);

                        var members = Tiles.Where(t => t.Group == targetGroup.Id).ToList();
                        int gMaxR = members.Count > 0 ? members.Max(t => t.Row + t.SpanY) : targetGroup.Row + 1;
                        double memberMaxY = members.Count > 0 ? members.Max(t => t.Y + t.HeightPixels) : targetGroup.Y + 60;
                        if (targetGroup.PlateHeight > 0)
                        {
                            memberMaxY = Math.Max(memberMaxY, targetGroup.PlateY + targetGroup.PlateHeight);
                        }

                        Point dropMouse = e.GetPosition(MainCanvasGrid);
                        int mouseRow = GridPlacementService.RowFromPixel(dropMouse.Y);
                        if (dropMouse.Y >= memberMaxY - 12 || mouseRow >= gMaxR)
                        {
                            dropRow = Math.Max(dropRow, gMaxR);
                        }

                        var (finalCol, finalRow) = FindGroupWrapPosition(targetGroup, _draggedTile, dropCol, dropRow);
                        dropCol = finalCol;
                        dropRow = finalRow;

                        bool isSameGroup = _draggedTile.Group == targetGroup.Id;

                        foreach (var cTile in _draggedCluster)
                        {
                            cTile.Group = targetGroup.Id;
                            cTile.SectionHeader = targetGroup.Title;
                        }

                        CleanEmptyGroupsAndReflow();

                        List<TileModel> mod;
                        if (_draggedCluster.Count == 1)
                        {
                            mod = GridPlacementService.PlaceTileInGroup(
                                _draggedTile,
                                dropCol,
                                dropRow,
                                _dragOriginalCol,
                                _dragOriginalRow,
                                targetGroup,
                                Tiles,
                                Groups,
                                isSameGroup);
                        }
                        else
                        {
                            mod = GridPlacementService.PlaceTilesInGroup(
                                _draggedCluster,
                                targetGroup,
                                Tiles,
                                _draggedTile,
                                dropCol,
                                dropRow,
                                Groups);
                        }

                        AnimateModifiedTiles(mod);
                        UpdateGroupHeaderPositions(animate: true);
                        UpdateCanvasHeight();
                    }
                }
                else
                {
                    foreach (var cTile in _draggedCluster)
                    {
                        cTile.Group = null;
                        cTile.SectionHeader = null;
                    }

                    int dropCol = GridPlacementService.ColFromPixel(_draggedTile.X);
                    int dropRow = GridPlacementService.RowFromPixel(_draggedTile.Y);

                    int clusterSpanX = _draggedCluster.Count > 1
                        ? Math.Max(1, _clusterRelGridBounds.MaxRelCol - _clusterRelGridBounds.MinRelCol)
                        : _draggedTile.SpanX;
                    int targetCol = GridPlacementService.SnapColToValidTrackSlot(dropCol, clusterSpanX, maxCols, _draggedTile.X);
                    int targetRow = Math.Max(1, dropRow);

                    var origDict = _dragClusterOriginals.ToDictionary(kvp => kvp.Key, kvp => (kvp.Value.Col, kvp.Value.Row));

                    foreach (var g in Groups)
                    {
                        var gMembers = Tiles.Where(t => t.Group == g.Id && !_draggedCluster.Contains(t)).ToList();
                        if (gMembers.Count == 0 && string.IsNullOrWhiteSpace(g.Title)) continue;

                        int gMinC = (g.IsLocked && gMembers.Count > 0) ? Math.Min(g.Col, gMembers.Min(t => t.Col)) : g.Col;
                        int gMaxC = (g.IsLocked && gMembers.Count > 0) ? gMembers.Max(t => t.Col + t.SpanX) : g.Col + GridPlacementService.GroupColWidth;
                        int gMinR = g.Row;
                        int gMaxR = membersCount(gMembers, g);

                        bool isBottomGap = (targetRow == gMaxR) && (targetCol < gMaxC && (targetCol + _draggedTile.SpanX) > gMinC);
                        bool isInsideGroup = (targetCol < gMaxC && (targetCol + _draggedTile.SpanX) > gMinC &&
                                              targetRow < gMaxR && (targetRow + _draggedTile.SpanY) > gMinR);
                        bool isTopGap = (gMinR > 0 && (targetRow + _draggedTile.SpanY) == gMinR) &&
                                        (targetCol < gMaxC && (targetCol + _draggedTile.SpanX) > gMinC);

                        if (isBottomGap || isInsideGroup)
                        {
                            targetRow = gMaxR + 1;
                        }
                        else if (isTopGap)
                        {
                            targetRow = Math.Max(1, gMinR - 1 - _draggedTile.SpanY);
                        }
                    }



                    CleanEmptyGroupsAndReflow();

                    List<TileModel> modifiedTiles;
                    if (_draggedCluster.Count > 1)
                    {
                        modifiedTiles = GridPlacementService.PlaceClusterAndResolveCollisions(
                            _draggedCluster, _draggedTile, targetCol, targetRow, _dragOriginalCol, _dragOriginalRow,
                            maxCols, Tiles, origDict);
                    }
                    else
                    {
                        modifiedTiles = GridPlacementService.PlaceAndResolveCollisions(
                            _draggedTile, targetCol, targetRow, _dragOriginalCol, _dragOriginalRow, maxCols, Tiles);
                    }

                    if (Groups != null && Groups.Count > 0)
                    {
                        var looseTiles = Tiles.Where(t => string.IsNullOrEmpty(t.Group)).ToList();
                        var pushedGroupTiles = GridPlacementService.PushGroupsDownFromLooseTiles(looseTiles, Groups, Tiles);
                        foreach (var pt in pushedGroupTiles)
                        {
                            if (!modifiedTiles.Contains(pt)) modifiedTiles.Add(pt);
                        }
                    }

                    AnimateModifiedTiles(modifiedTiles);
                    UpdateGroupHeaderPositions(animate: true);
                    UpdateCanvasHeight();
                }

                foreach (var cTile in _draggedCluster)
                {
                    cTile.IsBeingDragged = false;
                }

                bool anyPulled = false;
                foreach (var og in originGroups)
                {
                    int oldBottom = originOldBottoms[og!];
                    int newBottom = GridPlacementService.GetGroupBoundingBox(og!, Tiles).MaxRow;
                    int shrink = oldBottom - newBottom;
                    if (shrink > 0)
                    {
                        var pulled = GridPlacementService.PullLowerGroupsUp(og!, Groups, Tiles, shrink);
                        AnimateModifiedTiles(pulled);
                        anyPulled = true;
                    }
                }

                if (anyPulled)
                {
                    UpdateGroupHeaderPositions(animate: true);
                    SaveGroupsAndLayout();
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
            if (Groups != null) foreach (var g in Groups) g.IsBeingDragged = false;
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

        if (_isPotentialDrag)
        {
            _isPotentialDrag = false;
            var controlToLaunch = _draggedControl;
            _draggedTile = null;
            _draggedControl = null;
            _draggedContainer = null;
            _draggedCluster.Clear();

            bool isCtrlDown = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

            if (!isCtrlDown && controlToLaunch != null)
            {
                bool isWidget = controlToLaunch.DataContext is TileModel t && t.TileType == TileType.Widget;
                var now = DateTime.UtcNow;
                if (!isWidget && (_isLaunchingTile || (now - _lastTileLaunchTime).TotalMilliseconds < 800))
                {
                    controlToLaunch.AnimateRelease();
                    return;
                }

                if (!isWidget)
                {
                    _isLaunchingTile = true;
                    _lastTileLaunchTime = now;
                }

                ClearTileSelection();
                controlToLaunch.AnimateRelease(() =>
                {
                    try
                    {
                        controlToLaunch.LaunchTile();
                        if (!isWidget && Settings.CloseOnLaunch)
                        {
                            HideScreen();
                        }
                    }
                    finally
                    {
                        if (!isWidget)
                        {
                            Dispatcher.InvokeAsync(async () =>
                            {
                                await Task.Delay(500);
                                _isLaunchingTile = false;
                            });
                        }
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

    private static int membersCount(List<TileModel> members, TileGroupModel g) =>
        members.Count > 0 ? members.Max(t => t.Row + t.SpanY) : g.Row + 1;

    public void CancelActiveDrag()
    {
        if (!_isDragging && !_isPotentialDrag && !_isRubberBanding) return;

        if (_isRubberBanding)
        {
            _isRubberBanding = false;
            if (RubberBandBox != null) RubberBandBox.Visibility = Visibility.Collapsed;
        }

        if (DropSlotIndicator != null) DropSlotIndicator.Visibility = Visibility.Collapsed;
        if (GroupInsertionLine != null) GroupInsertionLine.Visibility = Visibility.Collapsed;
        HideGroupDropHighlight();
        HideGapDropHighlight();
        _hoveredTargetGroup = null;
        _groupDragTargetColIndex = -1;

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

        foreach (var t in Tiles)
        {
            if (t.IsBeingDragged)
            {
                t.IsBeingDragged = false;
                var control = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, t));
                control?.AnimateRelease();
                var c = TilesListBox?.ItemContainerGenerator.ContainerFromItem(t) as ContentPresenter;
                if (c != null) Panel.SetZIndex(c, 0);
            }
        }

        foreach (var g in Groups)
        {
            g.IsBeingDragged = false;
            var gc = GroupsListBox?.ItemContainerGenerator.ContainerFromItem(g) as ContentPresenter;
            if (gc != null) Panel.SetZIndex(gc, 0);
        }

        StopAutoScroll();
        _draggedCluster.Clear();
        _dragClusterOriginals.Clear();
        _isDragging = false;
        _isPotentialDrag = false;
        _isGroupDrag = false;
        _draggedGroupModel = null;

        UpdateCanvasHeight();
        UpdateGroupHeaderPositions();
        ClearAllAmbientReveals();

        try
        {
            if (RootGrid.IsMouseCaptured)
            {
                RootGrid.ReleaseMouseCapture();
            }
        }
        catch
        {
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

        if (!_isDragging)
        {
            _isGroupDrag = false;
            _draggedGroupModel = null;
        }
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

    private static bool IsTextInputFocused()
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        if (focused == null) return false;
        if (focused is System.Windows.Controls.Primitives.TextBoxBase || focused is System.Windows.Controls.PasswordBox)
            return true;
        return FindParent<System.Windows.Controls.Primitives.TextBoxBase>(focused) != null
               || FindParent<System.Windows.Controls.PasswordBox>(focused) != null;
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
        List<TileModel> targets = (anchorTile.IsSelected && SelectedTiles.Count > 1)
            ? SelectedTiles.ToList()
            : new List<TileModel> { anchorTile };

        if (targets.All(t => t.SpanX == newSpanX && t.SpanY == newSpanY))
        {
            return;
        }

        string preModify = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);

        var oldSpans = targets.ToDictionary(t => t, t => (t.SpanX, t.SpanY));

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

        var affectedGroups = targets
            .Where(t => !string.IsNullOrEmpty(t.Group))
            .Select(t => Groups.FirstOrDefault(g => g.Id == t.Group))
            .Where(g => g != null)
            .Distinct()
            .ToList();

        var oldGroupBottoms = affectedGroups.ToDictionary(
            g => g!,
            g => GridPlacementService.GetGroupBoundingBox(g!, Tiles).MaxRow);

        var modified = GridPlacementService.ResolveBatchResizeExpansion(
            targets,
            newSpanX,
            newSpanY,
            maxCols,
            Tiles,
            Groups);

        foreach (var t in targets)
        {
            var (oldX, oldY) = oldSpans[t];
            var control = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, t));
            control?.AnimateResize(oldX, oldY, newSpanX, newSpanY);
        }

        foreach (var ag in affectedGroups)
        {
            int oldBottom = oldGroupBottoms[ag!];
            int newBottom = GridPlacementService.GetGroupBoundingBox(ag!, Tiles).MaxRow;
            if (newBottom > oldBottom)
            {
                var pushed = GridPlacementService.PushLowerGroupsDown(ag!, Groups, Tiles);
                foreach (var pt in pushed)
                {
                    if (!modified.Contains(pt)) modified.Add(pt);
                }
            }
            else if (newBottom < oldBottom)
            {
                int shrink = oldBottom - newBottom;
                var pulled = GridPlacementService.PullLowerGroupsUp(ag!, Groups, Tiles, shrink);
                foreach (var pt in pulled)
                {
                    if (!modified.Contains(pt)) modified.Add(pt);
                }
            }
        }

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
        List<TileModel> targets = (anchorTile.IsSelected && SelectedTiles.Count > 1)
            ? SelectedTiles.ToList()
            : new List<TileModel> { anchorTile };

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

    public void DeleteSelectedTiles()
    {
        var targets = SelectedTiles.ToList();
        if (targets.Count == 0) return;
        BatchUnpinTiles(targets);
    }

    public void BatchUnpinSelectedTiles(TileModel anchorTile)
    {
        List<TileModel> targets = (anchorTile.IsSelected && SelectedTiles.Count > 1)
            ? SelectedTiles.ToList()
            : new List<TileModel> { anchorTile };

        BatchUnpinTiles(targets);
    }

    public void BatchUnpinTiles(IList<TileModel> targets)
    {
        if (targets == null || targets.Count == 0) return;

        var eligible = targets
            .Where(t => !t.IsLocked &&
                        !(t.Group != null && Groups.FirstOrDefault(g => g.Id == t.Group)?.IsLocked == true))
            .ToList();

        if (eligible.Count == 0)
        {
            var lockedGroup = targets
                .Where(t => t.Group != null)
                .Select(t => Groups.FirstOrDefault(g => g.Id == t.Group))
                .FirstOrDefault(g => g != null && g.IsLocked);
            if (lockedGroup != null)
            {
                FlashLockedGroupPerimeter(lockedGroup);
            }

            return;
        }

        string preUnpin = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
        _historyService.PushState(preUnpin);

        var affectedGroups = eligible
            .Where(t => !string.IsNullOrEmpty(t.Group))
            .Select(t => Groups.FirstOrDefault(g => g.Id == t.Group))
            .Where(g => g != null)
            .Distinct()
            .ToList();

        var oldBottoms = affectedGroups.ToDictionary(
            g => g!,
            g => GridPlacementService.GetGroupBoundingBox(g!, Tiles).MaxRow);

        foreach (var t in eligible)
        {
            t.Teardown();
            Tiles.Remove(t);
        }

        var modified = new List<TileModel>();
        foreach (var g in affectedGroups)
        {
            int oldBottom = oldBottoms[g!];
            int newBottom = GridPlacementService.GetGroupBoundingBox(g!, Tiles).MaxRow;
            int shrink = oldBottom - newBottom;
            if (shrink > 0)
            {
                var pulled = GridPlacementService.PullLowerGroupsUp(g!, Groups, Tiles, shrink);
                foreach (var pt in pulled)
                {
                    if (!modified.Contains(pt)) modified.Add(pt);
                }
            }
        }

        AnimateModifiedTiles(modified);
        CleanEmptyGroupsAndReflow();
        UpdateGroupHeaderPositions(animate: true);
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
            else
            {
                double expectedX = GridPlacementService.PixelXFromCol(group.Col);
                if (Math.Abs(group.X - expectedX) > 0.5)
                {
                    group.X = expectedX;
                    changed = true;
                }
                var members = Tiles.Where(t => t.Group == group.Id).ToList();
                foreach (var t in members)
                {
                    double expX = GridPlacementService.PixelXFromCol(t.Col);
                    if (Math.Abs(t.X - expX) > 0.5)
                    {
                        t.X = expX;
                        changed = true;
                    }
                }
            }

            var groupMembers = Tiles.Where(t => t.Group == group.Id).ToList();
            if (groupMembers.Count > 0)
            {
                int gMinR = group.Row;
                int gMaxRow = groupMembers.Max(t => t.Row + t.SpanY);
                int gMinCol = Math.Min(group.Col, groupMembers.Min(t => t.Col));
                int gMaxCol = groupMembers.Max(t => t.Col + t.SpanX);

                var trappedLoose = Tiles.Where(t =>
                    t.Group == null && t.Col < gMaxCol && (t.Col + t.SpanX) > gMinCol && t.Row < gMaxRow &&
                    (t.Row + t.SpanY) > gMinR).ToList();
                foreach (var lt in trappedLoose)
                {
                    var (freeCol, freeRow) = GridPlacementService.FindNearestAvailableSlot(gMaxCol, lt.Row,
                        lt.SpanX, lt.SpanY, Tiles, lt, GridPlacementService.MaxCols, Groups);
                    lt.Col = freeCol;
                    lt.Row = freeRow;
                    lt.X = GridPlacementService.PixelXFromCol(freeCol);
                    lt.Y = GridPlacementService.PixelYFromRow(freeRow);
                    changed = true;
                }
            }
        }

        // Compact loose tiles that were artificially pushed by old +1 or +2 sideways gap buffers
        foreach (var lt in Tiles.Where(t => string.IsNullOrEmpty(t.Group)).ToList())
        {
            foreach (var g in Groups)
            {
                var gMembers = Tiles.Where(t => t.Group == g.Id).ToList();
                if (gMembers.Count == 0 && string.IsNullOrWhiteSpace(g.Title)) continue;
                int gMaxC = (g.IsLocked && gMembers.Count > 0) ? gMembers.Max(t => t.Col + t.SpanX) : g.Col + GridPlacementService.GroupColWidth;
                for (int offset = 2; offset >= 1; offset--)
                {
                    if (lt.Col == gMaxC + offset)
                    {
                        if (GridPlacementService.IsRegionFree(gMaxC, lt.Row, lt.SpanX, lt.SpanY, Tiles, lt, GridPlacementService.MaxCols, Groups))
                        {
                            lt.Col = gMaxC;
                            lt.X = GridPlacementService.PixelXFromCol(gMaxC);
                            changed = true;
                            break;
                        }
                    }
                }
            }
        }

        // Auto-heal any loose tiles that currently straddle 8-column track boundaries or overlap
        var looseTilesToHeal = Tiles.Where(t => string.IsNullOrEmpty(t.Group)).OrderBy(t => t.Row).ThenBy(t => t.Col).ToList();
        var placedLooseTiles = new List<TileModel>();
        foreach (var lt in looseTilesToHeal)
        {
            int validCol = GridPlacementService.SnapColToValidTrackSlot(lt.Col, lt.SpanX, GridPlacementService.MaxCols, lt.X);
            if (validCol != lt.Col)
            {
                lt.Col = validCol;
                lt.X = GridPlacementService.PixelXFromCol(validCol);
                changed = true;
            }

            bool hasOverlap = placedLooseTiles.Any(other =>
                GridPlacementService.DoTilesOverlap(lt.Col, lt.Row, lt.SpanX, lt.SpanY, other.Col, other.Row, other.SpanX, other.SpanY));

            if (hasOverlap)
            {
                var (freeC, freeR) = GridPlacementService.FindNearestAvailableSlot(
                    lt.Col, lt.Row, lt.SpanX, lt.SpanY,
                    placedLooseTiles.Concat(Tiles.Where(t => !string.IsNullOrEmpty(t.Group))),
                    lt, GridPlacementService.MaxCols, Groups);

                lt.Col = freeC;
                lt.Row = freeR;
                lt.X = GridPlacementService.PixelXFromCol(freeC);
                lt.Y = GridPlacementService.PixelYFromRow(freeR);
                changed = true;
            }
            placedLooseTiles.Add(lt);
        }

        foreach (var lt in Tiles.Where(t => string.IsNullOrEmpty(t.Group)))
        {
            double expX = GridPlacementService.PixelXFromCol(lt.Col);
            if (Math.Abs(lt.X - expX) > 0.5)
            {
                lt.X = expX;
                changed = true;
            }
        }

        if (changed)
        {
            SaveGroupsAndLayout();
        }
    }

    public void CompactGroupGaps()
    {
        if (Groups.Count == 0) return;
        bool changed = false;
        var colGroups = Groups.GroupBy(g => g.ColumnIndex).ToList();
        foreach (var col in colGroups)
        {
            var ordered = col.OrderBy(g => g.Row).ToList();
            foreach (var g in ordered)
            {
                var pulled = GridPlacementService.PullLowerGroupsUp(g, Groups, Tiles);
                if (pulled.Count > 0) changed = true;
            }
        }

        if (changed)
        {
            UpdateGroupHeaderPositions();
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

        string? originGroupId = _draggedTile?.Group;
        bool isClusterFromOriginGroup = originGroupId != null &&
                                        (_draggedCluster.Count == 0 ||
                                         _draggedCluster.All(t => t.Group == originGroupId));

        var sortedGroups = isClusterFromOriginGroup
            ? Groups.OrderByDescending(g => g.Id == originGroupId).ToList()
            : Groups.ToList();

        int mouseCol = GridPlacementService.ColFromPixel(mousePos.X);
        int mouseRow = GridPlacementService.RowFromPixel(mousePos.Y);

        foreach (var group in sortedGroups)
        {
            var allMembers = Tiles.Where(t => t.Group == group.Id).ToList();
            if (allMembers.Count == 0 && string.IsNullOrWhiteSpace(group.Title)) continue;

            int gMinC, gMaxC;
            double blockWidth, minX;

            if (group.IsLocked)
            {
                // Locked group: strictly bounded to actual member tiles so adjacent canvas is free for loose tiles
                gMinC = allMembers.Count > 0 ? Math.Min(group.Col, allMembers.Min(t => t.Col)) : group.Col;
                gMaxC = allMembers.Count > 0 ? allMembers.Max(t => t.Col + t.SpanX) : group.Col + 2;
                int groupSpan = Math.Max(1, gMaxC - gMinC);
                blockWidth = (groupSpan * GridPlacementService.GridStep) - GridPlacementService.Gap;
                minX = GridPlacementService.PixelXFromCol(gMinC);
            }
            else
            {
                // Unlocked group: exact sensitivity rules from commit f49106d73f713e7bc1e8b86252ce40159e7dce23
                // Full 8-column boundary allows hover detection across the track so tint expands to accommodate new tiles
                gMinC = group.Col;
                gMaxC = group.Col + GridPlacementService.GroupColWidth;
                blockWidth = GridPlacementService.GroupColWidth * GridPlacementService.GridStep - GridPlacementService.Gap;
                minX = GridPlacementService.PixelXFromCol(group.Col);
            }

            int gMinR = group.Row;
            int gMaxR = allMembers.Count > 0 ? allMembers.Max(t => t.Row + t.SpanY) : group.Row + 1;

            // 1. Strict column and gap guard:
            // Mouse MUST be within the group's boundary [gMinC, gMaxC - 1].
            // Any mouse position outside this column range is open canvas.
            if (mouseCol < gMinC || mouseCol >= gMaxC) continue;

            // 2. Vertical row and gap guard:
            // For locked groups, strictly bound to member tiles [gMinR, gMaxR - 1] (or group.Row + 2 if empty).
            // For unlocked groups, allow hover detection down into the append zone (gMaxR + tileSpanY) so the tint
            // can smoothly expand vertically to accommodate new rows, while preserving exact horizontal and top sensitivity.
            int tileSpanY = _draggedTile != null
                ? _draggedTile.SpanY
                : Math.Max(1, _clusterRelGridBounds.MaxRelRow - _clusterRelGridBounds.MinRelRow + 1);
            double tileHeight = _draggedTile != null
                ? _draggedTile.HeightPixels
                : Math.Max(60, _clusterRelBounds.MaxRelY - _clusterRelBounds.MinRelY);

            int effectiveMaxRow = group.IsLocked
                ? (allMembers.Count > 0 ? gMaxR : group.Row + 2)
                : (allMembers.Count > 0 ? (gMaxR + tileSpanY + 1) : (group.Row + 1 + tileSpanY + 1));
            if (mouseRow < gMinR || mouseRow >= effectiveMaxRow) continue;

            // 3. Pixel-exact boundary check:
            double minY = group.PlateHeight > 0 ? Math.Min(group.Y, group.PlateY) : group.Y;

            double memberMaxY = allMembers.Count > 0 ? allMembers.Max(t => t.Y + t.HeightPixels) : group.Y + 60;
            if (group.PlateHeight > 0)
            {
                memberMaxY = Math.Max(memberMaxY, group.PlateY + group.PlateHeight);
            }

            double detectMaxY = group.IsLocked
                ? memberMaxY
                : (allMembers.Count > 0 ? (memberMaxY + tileHeight) : (group.Y + 60 + tileHeight));

            // Bounded to group interior and append area - zero outward pad into adjacent column tracks
            Rect groupDetectRect = new Rect(minX, minY, blockWidth, Math.Max(60, detectMaxY - minY));

            if (groupDetectRect.Contains(mousePos))
            {
                targetGroup = group;

                if (allMembers.Count > 0)
                {
                    int minCol = allMembers.Min(t => t.Col);
                    int maxCol = allMembers.Max(t => t.Col + t.SpanX);
                    int minRow = allMembers.Min(t => t.Row);
                    int maxRow = allMembers.Max(t => t.Row + t.SpanY);

                    if (_draggedTile != null && !group.IsLocked)
                    {
                        int rawCol = GridPlacementService.ColFromPixel(anchorX);
                        int rawRow = GridPlacementService.RowFromPixel(anchorY);
                        int queryRow = rawRow;
                        if (mousePos.Y >= memberMaxY - 12 || mouseRow >= gMaxR)
                        {
                            queryRow = Math.Max(queryRow, gMaxR);
                        }
                        var (wrapCol, wrapRow) = FindGroupWrapPosition(group, _draggedTile, rawCol, queryRow);
                        minCol = Math.Min(minCol, wrapCol);
                        maxCol = Math.Max(maxCol, wrapCol + _draggedTile.SpanX);
                        minRow = Math.Min(minRow, wrapRow);
                        maxRow = Math.Max(maxRow, wrapRow + _draggedTile.SpanY);
                    }

                    int colSpan = Math.Max(1, maxCol - minCol);
                    int rowSpan = Math.Max(1, maxRow - minRow);

                    double rectX = GridPlacementService.PixelXFromCol(minCol) - 8;
                    double rectY = GridPlacementService.PixelYFromRow(minRow) - 8;
                    double rectW = (colSpan * GridPlacementService.GridStep) - GridPlacementService.Gap + 16;
                    double rectH = (rowSpan * GridPlacementService.GridStep) - GridPlacementService.Gap + 16;

                    bestGroupRect = new Rect(rectX, rectY, rectW, rectH);
                }
                else
                {
                    if (group.IsLocked)
                    {
                        int col = group.Col >= 0
                            ? group.Col
                            : GridPlacementService.GetColumnStartCol(group.ColumnIndex);
                        int row = GridPlacementService.RowFromPixel(group.Y) + 1;
                        double rectX = GridPlacementService.PixelXFromCol(col) - 8;
                        double rectY = GridPlacementService.PixelYFromRow(row) - 8;
                        double rectW = (GridPlacementService.GroupColWidth * GridPlacementService.GridStep) -
                            GridPlacementService.Gap + 16;
                        double rectH = 56;
                        bestGroupRect = new Rect(rectX, rectY, rectW, rectH);
                    }
                    else
                    {
                        int rawCol = GridPlacementService.ColFromPixel(anchorX);
                        int rawRow = GridPlacementService.RowFromPixel(anchorY);
                        int wrapCol = group.Col >= 0
                            ? group.Col
                            : GridPlacementService.GetColumnStartCol(group.ColumnIndex);
                        int wrapRow = GridPlacementService.RowFromPixel(group.Y) + 1;
                        if (_draggedTile != null)
                        {
                            var (wc, wr) = FindGroupWrapPosition(group, _draggedTile, rawCol, rawRow);
                            wrapCol = wc;
                            wrapRow = wr;
                        }

                        int spanX = _draggedTile?.SpanX ?? 2;
                        int spanY = _draggedTile?.SpanY ?? 2;
                        double rectX = GridPlacementService.PixelXFromCol(wrapCol) - 8;
                        double rectY = GridPlacementService.PixelYFromRow(wrapRow) - 8;
                        double rectW = (spanX * GridPlacementService.GridStep) - GridPlacementService.Gap + 16;
                        double rectH = (spanY * GridPlacementService.GridStep) - GridPlacementService.Gap + 16;
                        bestGroupRect = new Rect(rectX, rectY, rectW, rectH);
                    }
                }

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
                catch
                {
                }
            }

            var solidBrush = new SolidColorBrush(groupColor);
            var tintBrush = new SolidColorBrush(Color.FromArgb(isLocked ? (byte)36 : (byte)22, groupColor.R,
                groupColor.G, groupColor.B));

            if (GroupDropPerimeterBorder != null)
            {
                GroupDropPerimeterBorder.BeginAnimation(UIElement.OpacityProperty, null);
                if (GroupDropPerimeterTranslate != null)
                {
                    GroupDropPerimeterTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                    GroupDropPerimeterTranslate.X = 0;
                }

                GroupDropPerimeterBorder.CornerRadius = new CornerRadius(6);
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
        if (GroupDropPerimeterBorder != null)
        {
            GroupDropPerimeterBorder.BeginAnimation(UIElement.OpacityProperty, null);
            if (GroupDropPerimeterTranslate != null)
            {
                GroupDropPerimeterTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                GroupDropPerimeterTranslate.X = 0;
            }

            GroupDropPerimeterBorder.Visibility = Visibility.Collapsed;
        }

        if (GroupDropFloatingBadge != null) GroupDropFloatingBadge.Visibility = Visibility.Collapsed;
    }

    private (int Col, int Row) FindGroupWrapPosition(TileGroupModel group, TileModel tile, int rawCol, int rawRow)
    {
        int groupMinCol = group.Col;
        int groupMaxCol = group.Col + GroupColWidth;
        int minRow = group.Row + 1;
        int baseRow = Math.Max(minRow, rawRow);

        int col;
        int row;

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

    public void FlashLockedGroupPerimeter(TileGroupModel group)
    {
        if (GroupDropPerimeterBorder == null) return;

        var members = Tiles.Where(t => t.Group == group.Id).ToList();
        if (members.Count > 0)
        {
            int minMemberCol = members.Min(t => t.Col);
            int maxMemberCol = members.Max(t => t.Col + t.SpanX);
            int colSpan = Math.Max(1, maxMemberCol - minMemberCol);

            int minMemberRow = members.Min(t => t.Row);
            int maxMemberBottom = members.Max(t => t.Row + t.SpanY);
            int rowSpan = Math.Max(1, maxMemberBottom - minMemberRow);

            Canvas.SetLeft(GroupDropPerimeterBorder, GridPlacementService.PixelXFromCol(minMemberCol) - 8);
            Canvas.SetTop(GroupDropPerimeterBorder, GridPlacementService.PixelYFromRow(minMemberRow) - 8);
            GroupDropPerimeterBorder.Width = (colSpan * GridPlacementService.GridStep) - GridPlacementService.Gap + 16;
            GroupDropPerimeterBorder.Height = (rowSpan * GridPlacementService.GridStep) - GridPlacementService.Gap + 16;
        }
        else
        {
            int col = group.Col >= 0 ? group.Col : GridPlacementService.GetColumnStartCol(group.ColumnIndex);
            Canvas.SetLeft(GroupDropPerimeterBorder, GridPlacementService.PixelXFromCol(col) - 8);
            Canvas.SetTop(GroupDropPerimeterBorder, group.Y);
            GroupDropPerimeterBorder.Width = (GridPlacementService.GroupColWidth * GridPlacementService.GridStep) -
                GridPlacementService.Gap + 16;
            GroupDropPerimeterBorder.Height = 36;
        }

        GroupDropPerimeterBorder.CornerRadius = new CornerRadius(6);

        var redColor = Color.FromRgb(0xFF, 0x43, 0x43);
        GroupDropPerimeterBorder.BorderBrush = new SolidColorBrush(redColor);
        GroupDropPerimeterBorder.Background =
            new SolidColorBrush(Color.FromArgb(45, redColor.R, redColor.G, redColor.B));
        if (GroupDropGlowEffect != null)
        {
            GroupDropGlowEffect.Color = redColor;
            GroupDropGlowEffect.Opacity = 0.95;
        }

        GroupDropPerimeterBorder.Opacity = 1.0;
        GroupDropPerimeterBorder.Visibility = Visibility.Visible;

        var translate = GroupDropPerimeterTranslate ?? new TranslateTransform();
        GroupDropPerimeterBorder.RenderTransform = translate;

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

    private void FlashGapDropPerimeter(double pixelX, double pixelY, double width = 56, double height = 56)
    {
    }

    private void ShowGapDropHighlight(int gapCol, int gapRow, Point mousePos)
    {
    }

    private void HideGapDropHighlight()
    {
        if (GapDropWarningBorder != null && GapDropWarningBorder.Visibility == Visibility.Visible)
        {
            GapDropWarningBorder.BeginAnimation(UIElement.OpacityProperty, null);
            GapDropWarningBorder.Visibility = Visibility.Collapsed;
            GapDropWarningBorder.Opacity = 1.0;
        }

        if (_hoveredTargetGroup == null && GroupDropFloatingBadge != null &&
            GroupDropFloatingBadge.Visibility == Visibility.Visible)
        {
            GroupDropFloatingBadge.Visibility = Visibility.Collapsed;
        }
    }

    private bool Check1x1GapHover(
        Point mousePos,
        int rawAnchorCol,
        int rawAnchorRow,
        int tileSpanX,
        int tileSpanY,
        out int gapCol,
        out int gapRow)
    {
        gapCol = -1;
        gapRow = -1;

        if (_isGroupDrag || Groups.Count == 0) return false;

        string? originGroupId = _draggedTile?.Group;

        // Check if the current snap target (rawAnchorCol, rawAnchorRow) is actually in or straddling a 1x1 gap buffer
        if (GridPlacementService.IsIn1x1Gap(rawAnchorCol, rawAnchorRow, tileSpanX, tileSpanY, Groups, Tiles, out gapCol, out gapRow, originGroupId))
        {
            return true;
        }

        return false;
    }

    public void EnsureGroupsHaveHeaderSpace()
    {
        UpdateGroupHeaderPositions();
    }

    public void UpdateGroupHeaderPositions(bool animate = false)
    {
        foreach (var group in Groups)
        {
            group.X = GridPlacementService.PixelXFromCol(group.Col);
            group.Y = GridPlacementService.PixelYFromRow(group.Row) + 8;

            var container = GroupsListBox?.ItemContainerGenerator.ContainerFromItem(group) as ContentPresenter;
            if (container != null)
            {
                if (animate)
                {
                    double curL = Canvas.GetLeft(container);
                    double curT = Canvas.GetTop(container);
                    if (double.IsNaN(curL)) curL = group.X;
                    if (double.IsNaN(curT)) curT = group.Y;

                    if (Math.Abs(curL - group.X) > 0.5 || Math.Abs(curT - group.Y) > 0.5)
                    {
                        var animX = new DoubleAnimation(curL, group.X, TimeSpan.FromMilliseconds(220))
                        {
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                        };
                        var animY = new DoubleAnimation(curT, group.Y, TimeSpan.FromMilliseconds(220))
                        {
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                        };
                        animX.Completed += (s, ev) =>
                        {
                            Canvas.SetLeft(container, group.X);
                            container.BeginAnimation(Canvas.LeftProperty, null);
                        };
                        animY.Completed += (s, ev) =>
                        {
                            Canvas.SetTop(container, group.Y);
                            container.BeginAnimation(Canvas.TopProperty, null);
                        };
                        container.BeginAnimation(Canvas.LeftProperty, animX);
                        container.BeginAnimation(Canvas.TopProperty, animY);
                    }
                    else
                    {
                        Canvas.SetLeft(container, group.X);
                        Canvas.SetTop(container, group.Y);
                    }
                }
                else
                {
                    container.BeginAnimation(Canvas.LeftProperty, null);
                    container.BeginAnimation(Canvas.TopProperty, null);
                    Canvas.SetLeft(container, group.X);
                    Canvas.SetTop(container, group.Y);
                }
            }

            var members = Tiles.Where(t => t.Group == group.Id).ToList();
            if (members.Count > 0)
            {
                int minMemberCol = members.Min(t => t.Col);
                int maxMemberCol = members.Max(t => t.Col + t.SpanX);
                int colSpan = Math.Max(1, maxMemberCol - minMemberCol);

                int minMemberRow = members.Min(t => t.Row);
                int maxMemberBottom = members.Max(t => t.Row + t.SpanY);
                int rowSpan = Math.Max(1, maxMemberBottom - minMemberRow);

                const double PlatePadding = GridPlacementService.Gap * 0.5;

                group.PlateX = GridPlacementService.PixelXFromCol(minMemberCol) - PlatePadding;
                group.PlateY = GridPlacementService.PixelYFromRow(minMemberRow) - PlatePadding;
                group.PlateWidth = (colSpan * GridPlacementService.GridStep) - GridPlacementService.Gap + (PlatePadding * 2);
                group.PlateHeight = (rowSpan * GridPlacementService.GridStep) - GridPlacementService.Gap + (PlatePadding * 2);
            }
            else
            {
                group.PlateWidth = 0;
                group.PlateHeight = 0;
            }

            var plateContainer = GroupTintBackplates?.ItemContainerGenerator.ContainerFromItem(group) as ContentPresenter;
            if (plateContainer != null)
            {
                if (animate)
                {
                    double curL = Canvas.GetLeft(plateContainer);
                    double curT = Canvas.GetTop(plateContainer);
                    if (double.IsNaN(curL)) curL = group.PlateX;
                    if (double.IsNaN(curT)) curT = group.PlateY;

                    if (Math.Abs(curL - group.PlateX) > 0.5 || Math.Abs(curT - group.PlateY) > 0.5)
                    {
                        var animX = new DoubleAnimation(curL, group.PlateX, TimeSpan.FromMilliseconds(220))
                        {
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                        };
                        var animY = new DoubleAnimation(curT, group.PlateY, TimeSpan.FromMilliseconds(220))
                        {
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                        };
                        animX.Completed += (s, ev) =>
                        {
                            Canvas.SetLeft(plateContainer, group.PlateX);
                            plateContainer.BeginAnimation(Canvas.LeftProperty, null);
                        };
                        animY.Completed += (s, ev) =>
                        {
                            Canvas.SetTop(plateContainer, group.PlateY);
                            plateContainer.BeginAnimation(Canvas.TopProperty, null);
                        };
                        plateContainer.BeginAnimation(Canvas.LeftProperty, animX);
                        plateContainer.BeginAnimation(Canvas.TopProperty, animY);
                    }
                    else
                    {
                        Canvas.SetLeft(plateContainer, group.PlateX);
                        Canvas.SetTop(plateContainer, group.PlateY);
                    }
                }
                else
                {
                    plateContainer.BeginAnimation(Canvas.LeftProperty, null);
                    plateContainer.BeginAnimation(Canvas.TopProperty, null);
                    Canvas.SetLeft(plateContainer, group.PlateX);
                    Canvas.SetTop(plateContainer, group.PlateY);
                }
            }
        }
    }

    public void SaveGroupsAndLayout()
    {
        StorageService.SaveLayout(Tiles);
        StorageService.SaveGroups(Groups);
        GridPlacementService.SetActiveGroups(Groups);
        UpdateCanvasHeight();
    }

    public void CreateGroupFromSelectedTiles(TileModel anchorTile)
    {
        if (anchorTile.TileType == TileType.Widget) return;

        List<TileModel> targets = (anchorTile.IsSelected && SelectedTiles.Count > 1)
            ? SelectedTiles.Where(t => t.TileType != TileType.Widget).ToList()
            : new List<TileModel> { anchorTile };

        if (targets.Count == 0) return;

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

        var originGroups = targets
            .Where(t => !string.IsNullOrEmpty(t.Group))
            .Select(t => Groups.FirstOrDefault(g => g.Id == t.Group))
            .Where(g => g != null)
            .Distinct()
            .ToList();

        var originOldBottoms = originGroups.ToDictionary(
            g => g!,
            g => GridPlacementService.GetGroupBoundingBox(g!, Tiles).MaxRow);

        var arranged = GridPlacementService.ArrangeTilesInNewGroup(group, targets, targetColStart, targetRow, defaultTitle);

        Groups.Add(group);

        var modified = GridPlacementService.InsertGroupAndResolveCollisions(group, targetColIndex, targetRow, Groups, Tiles);
        foreach (var at in arranged)
        {
            if (!modified.Contains(at)) modified.Add(at);
        }

        ClearTileSelection();

        foreach (var og in originGroups)
        {
            int oldBottom = originOldBottoms[og!];
            int newBottom = GridPlacementService.GetGroupBoundingBox(og!, Tiles).MaxRow;
            int shrink = oldBottom - newBottom;
            if (shrink > 0)
            {
                var pulled = GridPlacementService.PullLowerGroupsUp(og!, Groups, Tiles, shrink);
                foreach (var pt in pulled)
                {
                    if (!modified.Contains(pt)) modified.Add(pt);
                }
            }
        }

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

        var originGroups = tilesToAdd
            .Where(t => !string.IsNullOrEmpty(t.Group))
            .Select(t => Groups.FirstOrDefault(g => g.Id == t.Group))
            .Where(g => g != null)
            .Distinct()
            .ToList();

        var originOldBottoms = originGroups.ToDictionary(
            g => g!,
            g => GridPlacementService.GetGroupBoundingBox(g!, Tiles).MaxRow);

        var modified = GridPlacementService.PlaceTilesInGroup(
            tilesToAdd,
            targetGroup,
            Tiles,
            anchorTile: null,
            dropAnchorCol: targetGroup.Col,
            dropAnchorRow: targetGroup.Row + 1,
            groups: Groups);

        ClearTileSelection();

        foreach (var og in originGroups)
        {
            int oldBottom = originOldBottoms[og!];
            int newBottom = GridPlacementService.GetGroupBoundingBox(og!, Tiles).MaxRow;
            int shrink = oldBottom - newBottom;
            if (shrink > 0)
            {
                var pulled = GridPlacementService.PullLowerGroupsUp(og!, Groups, Tiles, shrink);
                foreach (var pt in pulled)
                {
                    if (!modified.Contains(pt)) modified.Add(pt);
                }
            }
        }

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

    public void SetGroupTintColor(TileGroupModel group, string? hex)
    {
        string pre = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
        group.TintColor = hex;
        UpdateGroupHeaderPositions();
        SaveGroupsAndLayout();
        _historyService.PushState(pre);
    }

    public void ToggleGroupLock(TileGroupModel group)
    {
        string pre = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
        group.IsLocked = !group.IsLocked;
        SaveGroupsAndLayout();
        _historyService.PushState(pre);
    }

    public void RenameGroup(TileGroupModel group, string newTitle)
    {
        if (group.Title == newTitle) return;
        string pre = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
        group.Title = newTitle;
        SaveGroupsAndLayout();
        _historyService.PushState(pre);
    }

    public LayoutHistoryService HistoryService => _historyService;

    public void UngroupTiles(TileGroupModel group)
    {
        if (group.IsLocked)
        {
            FlashLockedGroupPerimeter(group);
            return;
        }

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
        if (group.IsLocked)
        {
            FlashLockedGroupPerimeter(group);
            return;
        }

        string pre = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
        var memberTiles = Tiles.Where(t => t.Group == group.Id).ToList();
        foreach (var t in memberTiles)
        {
            t.Teardown();
            Tiles.Remove(t);
        }

        Groups.Remove(group);

        var modifiedTiles = GridPlacementService.PullLowerGroupsUp(group, Groups, Tiles);
        AnimateModifiedTiles(modifiedTiles);
        UpdateGroupHeaderPositions(animate: true);
        CompactGroupGaps();
        SaveGroupsAndLayout();
        UpdateCanvasHeight();
        UpdateExposedAddSlots();
        _historyService.PushState(pre);
    }

    public void StartGroupDrag(TileGroupModel group, MouseEventArgs e)
    {
        if (group.IsLocked)
        {
            FlashLockedGroupPerimeter(group);
            return;
        }

        ClearTileSelection();
        _preDragLayoutSnapshot = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);

        var members = Tiles.Where(t => t.Group == group.Id).ToList();
        Point canvasMouse = TilesListBox != null 
            ? e.GetPosition(TilesListBox) 
            : (MainCanvasGrid != null ? e.GetPosition(MainCanvasGrid) : e.GetPosition(this));

        var gc = GroupsListBox?.ItemContainerGenerator.ContainerFromItem(group) as ContentPresenter;
        if (gc != null) Panel.SetZIndex(gc, 9999);

        if (members.Count == 0)
        {
            _draggedTile = null;
            _draggedControl = null;
            _draggedContainer = null;
            _dragStartPoint = e.GetPosition(this);

            int gCol = group.Col >= 0 ? group.Col : GridPlacementService.GetColumnStartCol(group.ColumnIndex);
            int gRow = Math.Max(0, group.Row);
            double gX = GridPlacementService.PixelXFromCol(gCol);
            double gY = GridPlacementService.PixelYFromRow(gRow) + 8;

            _dragOriginalCol = gCol;
            _dragOriginalRow = gRow;
            _dragOffsetX = canvasMouse.X - gX;
            _dragOffsetY = canvasMouse.Y - gY;
            _dragBeganWithSelection = false;

            _draggedCluster = members;
            _dragClusterOriginals.Clear();

            _clusterRelBounds = (0, GridPlacementService.GroupColWidth * GridPlacementService.GridStep - GridPlacementService.Gap, 0, GridPlacementService.GridStep);
            _clusterRelGridBounds = (0, GridPlacementService.GroupColWidth, 0, 1);

            _isPotentialDrag = false;
            _isDragging = true;
            _isGroupDrag = true;
            _draggedGroupModel = group;
            _draggedGroupOffsetX = 0;
            _draggedGroupOffsetY = 0;
            _draggedPlateOffsetX = 0;
            _draggedPlateOffsetY = 0;
            group.IsBeingDragged = true;

            UpdateAutoScrollVelocity();
            RootGrid.CaptureMouse();
            DropSlotIndicator.Visibility = Visibility.Collapsed;
            if (GroupInsertionLine != null)
            {
                GroupInsertionLine.Width = GridPlacementService.GroupColWidth * GridPlacementService.GridStep - GridPlacementService.Gap;
                Canvas.SetLeft(GroupInsertionLine, GridPlacementService.PixelXFromCol(gCol));
                Canvas.SetTop(GroupInsertionLine, group.Y);
                GroupInsertionLine.Visibility = Visibility.Visible;
            }

            return;
        }

        foreach (var m in members)
        {
            m.IsSelected = true;
        }

        var anchor = members.OrderBy(t => t.Row).ThenBy(t => t.Col).First();

        _draggedTile = anchor;
        _draggedControl = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, anchor));
        _draggedContainer = TilesListBox?.ItemContainerGenerator.ContainerFromItem(anchor) as ContentPresenter;
        _dragStartPoint = e.GetPosition(this);

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
        _draggedPlateOffsetX = group.PlateX - anchor.X;
        _draggedPlateOffsetY = group.PlateY - anchor.Y;
        group.IsBeingDragged = true;

        UpdateAutoScrollVelocity();
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

            var (freeCol, freeRow) = GridPlacementService.FindNearestAvailableSlot(
                col, Math.Max(1, row), 2, 2, Tiles, null, maxCols, Groups);

            var tileUngrouped = new TileModel
            {
                Title = title,
                TargetPath = filePath,
                IconPath = iconPath,
                TileType = TileType.App,
                SpanX = 2,
                SpanY = 2,
                Col = freeCol,
                Row = freeRow,
                X = GridPlacementService.PixelXFromCol(freeCol),
                Y = GridPlacementService.PixelYFromRow(freeRow)
            };

            Tiles.Add(tileUngrouped);

            if (Groups != null && Groups.Count > 0)
            {
                var looseTiles = Tiles.Where(t => string.IsNullOrEmpty(t.Group)).ToList();
                var pushedGroupTiles = GridPlacementService.PushGroupsDownFromLooseTiles(looseTiles, Groups, Tiles);
                AnimateModifiedTiles(pushedGroupTiles);
                UpdateGroupHeaderPositions(animate: true);
                CompactGroupGaps();
                SaveGroupsAndLayout();
            }

            StorageService.SaveLayout(Tiles);
            UpdateCanvasHeight();
            UpdateExposedAddSlots();
        }
    }

    #region Canvas Context Menu & Modular Catalog Methods

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

    private TileGroupModel? _activeContextMenuGroup;

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild) return typedChild;
            var desc = FindVisualChild<T>(child);
            if (desc != null) return desc;
        }
        return null;
    }

    private TileGroupModel? GetGroupAtCanvasPoint(Point pt)
    {
        int col = GridPlacementService.ColFromPixel(pt.X);
        int row = GridPlacementService.RowFromPixel(pt.Y);

        foreach (var g in Groups)
        {
            var (minC, maxC, minR, maxR) = GridPlacementService.GetGroupBoundingBox(g, Tiles);

            // 1. Check logical grid cell bounds
            if (col >= minC && col < maxC && row >= minR && row <= maxR)
            {
                return g;
            }

            // 2. Check acrylic backplate bounds
            if (g.PlateWidth > 0 && g.PlateHeight > 0)
            {
                Rect plateRect = new Rect(g.PlateX, g.PlateY, g.PlateWidth, g.PlateHeight);
                if (plateRect.Contains(pt))
                {
                    return g;
                }
            }

            // 3. Check group header area [g.X, g.Y, headerWidth, 36]
            var gMembers = Tiles.Where(t => t.Group == g.Id).ToList();
            int gSpan = (g.IsLocked && gMembers.Count > 0) ? (gMembers.Max(t => t.Col + t.SpanX) - g.Col) : GridPlacementService.GroupColWidth;
            double headerWidth = (Math.Max(2, gSpan) * GridPlacementService.GridStep) - GridPlacementService.Gap;
            Rect headerRect = new Rect(g.X, g.Y, headerWidth, 36);
            if (headerRect.Contains(pt))
            {
                return g;
            }
        }

        return null;
    }

    private void OnCanvasContextMenuOpened(object sender, RoutedEventArgs e)
    {
        _activeContextMenuGroup = GetGroupAtCanvasPoint(_canvasRightClickPoint);

        bool isInsideGroup = _activeContextMenuGroup != null;
        UpdateBackdropMenuChecks();

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

    private void OnCanvasRenameGroupClick(object sender, RoutedEventArgs e)
    {
        if (_activeContextMenuGroup == null) return;
        if (_activeContextMenuGroup.IsLocked)
        {
            FlashLockedGroupPerimeter(_activeContextMenuGroup);
            return;
        }

        var group = _activeContextMenuGroup;
        var container = GroupsListBox?.ItemContainerGenerator.ContainerFromItem(group) as ContentPresenter;
        if (container != null)
        {
            var headerCtrl = FindVisualChild<Presentation.Controls.GroupHeaderControl>(container);
            if (headerCtrl != null)
            {
                headerCtrl.BeginEdit();
                return;
            }
        }
        group.IsEditing = true;
    }

    private void OnCanvasGroupColorSelectClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Tag is string hex && _activeContextMenuGroup != null)
        {
            SetGroupColor(_activeContextMenuGroup, hex);
        }
    }

    private void OnCanvasGroupTintColorSelectClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && _activeContextMenuGroup != null)
        {
            string? hex = item.Tag as string;
            if (string.Equals(hex, "None", StringComparison.OrdinalIgnoreCase))
            {
                hex = null;
            }
            SetGroupTintColor(_activeContextMenuGroup, hex);
        }
    }

    private void OnCanvasLockGroupClick(object sender, RoutedEventArgs e)
    {
        if (_activeContextMenuGroup != null)
        {
            ToggleGroupLock(_activeContextMenuGroup);
        }
    }

    private void OnCanvasUngroupClick(object sender, RoutedEventArgs e)
    {
        if (_activeContextMenuGroup != null)
        {
            UngroupTiles(_activeContextMenuGroup);
        }
    }

    private void OnCanvasDeleteGroupClick(object sender, RoutedEventArgs e)
    {
        if (_activeContextMenuGroup != null)
        {
            DeleteGroupAndTiles(_activeContextMenuGroup);
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

        if (GridPlacementService.IsRegionFree(col, row, 1, 1, Tiles, groups: Groups))
        {
            if (!GridPlacementService.IsRegionFree(col, row, spanX, spanY, Tiles, groups: Groups))
            {
                spanX = 1;
                spanY = 1;
            }
        }

        int clampedCol = Math.Max(0, Math.Min(col, maxCols - spanX));
        int clampedRow = Math.Max(1, row);

        var (freeCol, freeRow) = GridPlacementService.FindNearestAvailableSlot(
            clampedCol,
            clampedRow,
            spanX,
            spanY,
            Tiles,
            null,
            maxCols,
            Groups);

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
            Col = freeCol,
            Row = freeRow,
            X = GridPlacementService.PixelXFromCol(freeCol),
            Y = GridPlacementService.PixelYFromRow(freeRow)
        };

        Tiles.Add(tile);

        if (Groups != null && Groups.Count > 0)
        {
            var looseTiles = Tiles.Where(t => string.IsNullOrEmpty(t.Group)).ToList();
            var pushedGroupTiles = GridPlacementService.PushGroupsDownFromLooseTiles(looseTiles, Groups, Tiles);
            AnimateModifiedTiles(pushedGroupTiles);
            UpdateGroupHeaderPositions(animate: true);
            CompactGroupGaps();
            SaveGroupsAndLayout();
        }

        StorageService.SaveLayout(Tiles);
        UpdateCanvasHeight();
        UpdateExposedAddSlots();
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

        foreach (var def in widgets)
        {
            var mi = new MenuItem
            {
                Header = def.DisplayName,
                Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = def.Icon, FontSize = 18, Foreground = new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)) },
                Tag = def,
                Cursor = Cursors.Hand,
                MinHeight = 32,
                Padding = new Thickness(10, 4, 14, 4)
            };
            mi.Click += OnWidgetMenuItemClick;
            widgetsMenu.Items.Add(mi);
        }
    }

    private void OnWidgetMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is MetroHub.Widgets.Registry.WidgetDefinition def)
        {
            PinWidget(def, _canvasRightClickPoint);
        }
    }

    public void PinWidget(MetroHub.Widgets.Registry.WidgetDefinition def, Point? targetCanvasPosition = null)
    {
        if (def == null) return;

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

        int spanX = def.InitialSize.SpanX;
        int spanY = def.InitialSize.SpanY;

        if (targetGroup != null)
        {
            var groupTile = new TileModel
            {
                Title = def.DisplayName,
                TargetPath = def.Id,
                TileType = TileType.Widget,
                SpanX = spanX,
                SpanY = spanY,
                Group = targetGroup.Id,
                SectionHeader = targetGroup.Title
            };

            int pinRelCol = col - targetGroup.Col;
            int pinRelRow = row - (targetGroup.Row + 1);
            var existingPinGroupTiles = Tiles.Where(t => t.Group == targetGroup.Id).ToList();
            var (pinSlotCol, pinSlotRow) = GridPlacementService.FindFreeSlotInGroup(
                targetGroup, pinRelCol, pinRelRow, spanX, spanY, existingPinGroupTiles);
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

        int clampedCol = Math.Max(0, Math.Min(col, maxCols - spanX));
        int clampedRow = Math.Max(1, row);

        var (freeCol, freeRow) = GridPlacementService.FindNearestAvailableSlot(
            clampedCol,
            clampedRow,
            spanX,
            spanY,
            Tiles,
            null,
            maxCols,
            Groups);

        var tile = new TileModel
        {
            Title = def.DisplayName,
            TargetPath = def.Id,
            TileType = TileType.Widget,
            SpanX = spanX,
            SpanY = spanY,
            Col = freeCol,
            Row = freeRow,
            X = GridPlacementService.PixelXFromCol(freeCol),
            Y = GridPlacementService.PixelYFromRow(freeRow)
        };

        Tiles.Add(tile);

        if (Groups != null && Groups.Count > 0)
        {
            var looseTiles = Tiles.Where(t => string.IsNullOrEmpty(t.Group)).ToList();
            var pushedGroupTiles = GridPlacementService.PushGroupsDownFromLooseTiles(looseTiles, Groups, Tiles);
            AnimateModifiedTiles(pushedGroupTiles);
            UpdateGroupHeaderPositions(animate: true);
            CompactGroupGaps();
            SaveGroupsAndLayout();
        }

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

            if (sfd.ShowDialog(this) == true)
            {
                if (StorageService.ExportLayout(Tiles, sfd.FileName))
                {
                    System.Windows.MessageBox.Show(this, "Layout exported successfully!", "MetroHub",
                        System.Windows.MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Failed to export layout: {ex.Message}");
        }
        finally
        {
            IsDialogOpen = false;
            Activate();
        }
    }

    private void OnCloseToTrayClick(object sender, RoutedEventArgs e)
    {
        HideScreen();
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(typeof(CatalogItemModel)))
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
        if (e.Data.GetDataPresent(typeof(CatalogItemModel)))
        {
            var item = e.Data.GetData(typeof(CatalogItemModel)) as CatalogItemModel;
            if (item != null)
            {
                Point pos = e.GetPosition(TilesListBox);
                PinCatalogItem(item, pos);
                e.Handled = true;
            }
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
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
                    currentX += 70;
                    currentY += 70;
                }

                e.Handled = true;
            }
        }

        UpdateExposedAddSlots();
    }

    #region Sidebar Rail and All Apps Drawer Handlers

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

    private void OnSidebarAppsToggleRequested(object? sender, EventArgs e)
    {
        AllAppsDrawer?.Toggle();
    }

    private void OnDrawerOpened(object? sender, EventArgs e)
    {
        SidebarRail?.SetAppsDrawerActive(true);
        AnimateCanvasMask(true);
    }

    private void OnDrawerClosing(object? sender, EventArgs e)
    {
        SidebarRail?.SetAppsDrawerActive(false);
        AnimateCanvasMask(false);
    }

    private void OnDrawerClosed(object? sender, EventArgs e)
    {
        SidebarRail?.SetAppsDrawerActive(false);
    }

    private void AnimateCanvasMask(bool drawerOpening)
    {
        if (MainContentAreaGrid == null) return;

        double targetWidth = drawerOpening ? 320 : 0;
        double fromWidth = drawerOpening ? 0 : 320;

        var areaGeom = new RectangleGeometry();
        MainContentAreaGrid.Clip = areaGeom;

        var areaAnim = new RectAnimation
        {
            From = new Rect(fromWidth, 0, 50000, 50000),
            To = new Rect(targetWidth, 0, 50000, 50000),
            Duration = TimeSpan.FromMilliseconds(drawerOpening ? 220 : 180),
            EasingFunction = drawerOpening
                ? new CubicEase { EasingMode = EasingMode.EaseOut }
                : new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        if (!drawerOpening)
        {
            areaAnim.Completed += (s, e) =>
            {
                if (AllAppsDrawer == null || !AllAppsDrawer.IsOpen)
                {
                    MainContentAreaGrid.Clip = null;
                }
            };
        }

        areaGeom.BeginAnimation(RectangleGeometry.RectProperty, areaAnim);
    }

    private void OnDrawerAppPinRequested(object? sender, CatalogItemModel item)
    {
        PinCatalogItem(item, null);
    }

    private void OnDrawerAppLaunchRequested(object? sender, CatalogItemModel item)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(item.TargetPath))
            {
                NativeMethods.LaunchTarget(item.TargetPath, item.Arguments);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] Failed to launch app: {ex.Message}");
        }
    }

    #endregion

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_isClosingToExit)
        {
            e.Cancel = true;
            HideScreen();
        }
        else
        {
            if (_winEventHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_winEventHook);
                _winEventHook = IntPtr.Zero;
            }
            _hotkeyService.Dispose();
            base.OnClosing(e);
        }
    }

    public void ExitApplication()
    {
        _isClosingToExit = true;
        if (_winEventHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
        _hotkeyService.Dispose();
        Close();
        Application.Current.Shutdown();
    }
}