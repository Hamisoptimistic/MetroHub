using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Presentation.Controllers;
using MetroHub.Presentation.Messaging;
using MetroHub.Presentation.Themes;
using MetroHub.Widgets;

namespace MetroHub.Presentation.Controls;

public partial class TileControl : UserControl
{
    public static readonly RoutedEvent TileActivatedEvent = EventManager.RegisterRoutedEvent(
        nameof(TileActivated), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(TileControl));

    public static readonly RoutedEvent TileUnpinnedEvent = EventManager.RegisterRoutedEvent(
        nameof(TileUnpinned), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(TileControl));

    public static readonly RoutedEvent TileModifiedEvent = EventManager.RegisterRoutedEvent(
        nameof(TileModified), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(TileControl));

    public event RoutedEventHandler TileActivated
    {
        add => AddHandler(TileActivatedEvent, value);
        remove => RemoveHandler(TileActivatedEvent, value);
    }

    public event RoutedEventHandler TileUnpinned
    {
        add => AddHandler(TileUnpinnedEvent, value);
        remove => RemoveHandler(TileUnpinnedEvent, value);
    }

    public event RoutedEventHandler TileModified
    {
        add => AddHandler(TileModifiedEvent, value);
        remove => RemoveHandler(TileModifiedEvent, value);
    }



    private static readonly Brush MenuIconForegroundBrush = ThemeTokens.MenuIconForegroundBrush;
    private static readonly Brush RedMutedBrush = ThemeTokens.StatusErrorBrush;

    private static Brush CreateFrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static readonly List<TileControl> ActiveTiles = new();

    private static DispatcherTimer? _revealSettleTimer;

    /// <summary>
    /// When true, ambient and hover reveal radial gradient animations/updates are suppressed
    /// during scrolling to eliminate animation allocation and GPU state churn.
    /// </summary>
    public static bool IsRevealSuppressed { get; private set; }

    /// <summary>
    /// Notifies TileControl that a scroll gesture or offset update is occurring.
    /// Immediately mutes all active reveals and schedules an un-suppress when scrolling settles.
    /// </summary>
    public static void SuppressRevealForScrolling()
    {
        if (_revealSettleTimer == null)
        {
            _revealSettleTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(130)
            };
            _revealSettleTimer.Tick += OnRevealSettleTimerTick;
        }

        _revealSettleTimer.Stop();
        _revealSettleTimer.Start();

        if (!IsRevealSuppressed)
        {
            SetRevealSuppressed(true);
        }
    }

    private static void OnRevealSettleTimerTick(object? sender, EventArgs e)
    {
        _revealSettleTimer?.Stop();
        SetRevealSuppressed(false);
    }

    public static void SetRevealSuppressed(bool suppressed)
    {
        if (IsRevealSuppressed == suppressed) return;
        IsRevealSuppressed = suppressed;

        if (suppressed)
        {
            for (int i = ActiveTiles.Count - 1; i >= 0; i--)
            {
                if (i < ActiveTiles.Count)
                {
                    ActiveTiles[i].SilenceReveals();
                }
            }
        }
        else
        {
            for (int i = ActiveTiles.Count - 1; i >= 0; i--)
            {
                if (i < ActiveTiles.Count && ActiveTiles[i].IsMouseOver)
                {
                    ActiveTiles[i].RestoreHoverReveal();
                    break;
                }
            }
        }
    }

    public void SilenceReveals()
    {
        if (RevealFillBorder != null && RevealFillBorder.Opacity > 0)
        {
            RevealFillBorder.BeginAnimation(UIElement.OpacityProperty, null);
            RevealFillBorder.Opacity = 0.0;
        }

        if (RevealEdgeBorder != null && RevealEdgeBorder.Opacity > 0)
        {
            RevealEdgeBorder.BeginAnimation(UIElement.OpacityProperty, null);
            RevealEdgeBorder.Opacity = 0.0;
        }
    }

    private void RestoreHoverReveal()
    {
        if (!IsLoaded || RootBorder == null) return;

        Point pos = Mouse.GetPosition(RootBorder);
        UpdateRevealPositions(pos);

        if (DataContext is not TileModel { TileType: TileType.Widget })
        {
            AnimateRevealFill(1.0, 150);
        }

        if (RevealEdgeBorder != null)
        {
            RevealEdgeBorder.BeginAnimation(UIElement.OpacityProperty, null);
            RevealEdgeBorder.Opacity = 1.0;
        }
    }

    public TileControl()
    {
        InitializeComponent();
        Loaded += (s, e) =>
        {
            ActiveTiles.RemoveAll(tc => (!tc.IsLoaded || !tc.IsVisible) && !ReferenceEquals(tc, this));
            if (IsVisible && !ActiveTiles.Contains(this)) ActiveTiles.Add(this);
            ApplyTileStyle(animate: false);
        };
        Unloaded += (s, e) =>
        {
            if (s_activeTargetTile == this)
            {
                DetachSharedContextMenu();
            }
            ActiveTiles.Remove(this);
            ActiveTiles.RemoveAll(tc => !tc.IsLoaded || !tc.IsVisible);
        };
        IsVisibleChanged += (s, e) =>
        {
            if (IsVisible)
            {
                if (!ActiveTiles.Contains(this)) ActiveTiles.Add(this);
                ApplyTileStyle(animate: false);
            }
            else
            {
                ActiveTiles.Remove(this);
            }
        };
        DataContextChanged += (s, e) => ApplyTileStyle(animate: false);

        MouseEnter += OnMouseEnter;
        MouseLeave += OnMouseLeave;
        MouseMove += OnMouseMove;
        PreviewMouseRightButtonDown += OnPreviewMouseRightButtonDown;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (IsRevealSuppressed) return;

        Point pos = e.GetPosition(RootBorder);
        UpdateRevealPositions(pos);
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (IsRevealSuppressed) return;

        Point pos = e.GetPosition(RootBorder);
        UpdateRevealPositions(pos);

        if (DataContext is not TileModel { TileType: TileType.Widget })
        {
            AnimateRevealFill(1.0, 100);
        }

        RevealEdgeBorder.BeginAnimation(UIElement.OpacityProperty, null);
        RevealEdgeBorder.Opacity = 1.0;
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (IsRevealSuppressed)
        {
            RevealFillBorder?.BeginAnimation(UIElement.OpacityProperty, null);
            if (RevealFillBorder != null) RevealFillBorder.Opacity = 0.0;
            RevealEdgeBorder?.BeginAnimation(UIElement.OpacityProperty, null);
            if (RevealEdgeBorder != null) RevealEdgeBorder.Opacity = 0.0;
            return;
        }

        if (DataContext is not TileModel { TileType: TileType.Widget })
        {
            AnimateRevealFill(0.0, 200);
        }

        var anim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        RevealEdgeBorder?.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void UpdateRevealPositions(Point pos)
    {
        if (RevealFillBrush != null)
        {
            RevealFillBrush.Center = pos;
            RevealFillBrush.GradientOrigin = pos;
        }
        if (RevealEdgeBrush != null)
        {
            RevealEdgeBrush.Center = pos;
            RevealEdgeBrush.GradientOrigin = pos;
        }
    }

    private void AnimateRevealFill(double targetOpacity, int durationMs)
    {
        if (DataContext is TileModel { TileType: TileType.Widget })
        {
            RevealFillBorder.Opacity = 0.0;
            return;
        }
        var anim = new DoubleAnimation(targetOpacity, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        RevealFillBorder?.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    public void UpdateAmbientReveal(Point mouseOnTile, double distance)
    {
        if (IsRevealSuppressed) return;

        if (IsMouseOver)
        {
            RevealEdgeBorder.Opacity = 1.0;
            return;
        }

        if (RevealEdgeBrush != null)
        {
            RevealEdgeBrush.Center = mouseOnTile;
            RevealEdgeBrush.GradientOrigin = mouseOnTile;
        }

        // Quadratic proximity falloff
        double factor = Math.Clamp(1.0 - (distance / 160.0), 0.0, 1.0);
        double targetOpacity = factor * factor;

        RevealEdgeBorder.BeginAnimation(UIElement.OpacityProperty, null);
        RevealEdgeBorder.Opacity = targetOpacity;
    }

    public void ClearAmbientReveal()
    {
        if (RevealEdgeBorder != null && RevealEdgeBorder.Opacity > 0 && (IsRevealSuppressed || !IsMouseOver))
        {
            RevealEdgeBorder.BeginAnimation(UIElement.OpacityProperty, null);
            RevealEdgeBorder.Opacity = 0.0;
        }
    }

    private static DateTime _lastControlLaunchTime = DateTime.MinValue;
    private static string? _lastControlLaunchPath;
    private static readonly object _controlLaunchLock = new();

    public static bool IsNoteTile(TileModel? tile)
    {
        if (tile == null || string.IsNullOrWhiteSpace(tile.TargetPath)) return false;
        try
        {
            string noteDir = AppPaths.PastedNotesDir;
            return tile.TargetPath.StartsWith(noteDir, StringComparison.OrdinalIgnoreCase) &&
                   tile.TargetPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public void LaunchTile()
    {
        if (DataContext is TileModel tile)
        {
            if (tile.TileType == TileType.Widget)
            {
                if (tile.TileContent is IWidgetActionHandler actionHandler)
                {
                    actionHandler.OnPrimaryAction();
                }
                return;
            }

            if (IsNoteTile(tile))
            {
                Dialogs.NoteViewerDialog.Show(Window.GetWindow(this), tile);
                RaiseEvent(new RoutedEventArgs(TileActivatedEvent, tile));
                return;
            }

            if (string.IsNullOrWhiteSpace(tile.TargetPath)) return;

            lock (_controlLaunchLock)
            {
                var now = DateTime.UtcNow;
                if (string.Equals(_lastControlLaunchPath, tile.TargetPath, StringComparison.OrdinalIgnoreCase) &&
                    (now - _lastControlLaunchTime).TotalMilliseconds < 800)
                {
                    return;
                }
                _lastControlLaunchPath = tile.TargetPath;
                _lastControlLaunchTime = now;
            }

            ProcessLauncherService.LaunchTargetAsync(tile.TargetPath, tile.Arguments, tile.RunAsAdmin, tile.Title);
            RaiseEvent(new RoutedEventArgs(TileActivatedEvent, tile));
        }
    }

    public void AnimatePressDown()
    {
        if (DataContext is TileModel { TileType: TileType.Widget } tm && !string.Equals(tm.TargetPath, "stub", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var anim = new DoubleAnimation(TileScale.ScaleX, 0.95, TimeSpan.FromMilliseconds(50))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        TileScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        TileScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    public void AnimateElevationLift()
    {
        var anim = new DoubleAnimation(TileScale.ScaleX, 1.04, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        TileScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        TileScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);

        var shadow = new DropShadowEffect
        {
            BlurRadius = 24,
            ShadowDepth = 6,
            Direction = 270,
            Opacity = 0.45,
            Color = Colors.Black
        };
        // Rasterise the in-flight tile at the monitor's true DPI scale with ClearType enabled.
        // A plain BitmapCache renders at 1x with ClearType off, so on a scaled display the
        // cached text and icons get upscaled and read as pixelated for the whole drag.
        double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        RootBorder.CacheMode = new BitmapCache
        {
            RenderAtScale = dpiScale > 0 ? dpiScale : 1.0,
            EnableClearType = true,
            SnapsToDevicePixels = true
        };
        RootBorder.Effect = shadow;
    }

    public void AnimateRelease(Action? onCompleted = null)
    {
        var animX = new DoubleAnimation(TileScale.ScaleX, 1.0, TimeSpan.FromMilliseconds(80))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        var animY = new DoubleAnimation(TileScale.ScaleY, 1.0, TimeSpan.FromMilliseconds(80))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        bool completedFired = false;
        void OnAnimationDone()
        {
            if (completedFired) return;
            completedFired = true;

            TileScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            TileScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            TileScale.ScaleX = 1.0;
            TileScale.ScaleY = 1.0;
            RootBorder.Effect = null;
            RootBorder.CacheMode = null;
            onCompleted?.Invoke();
        }

        animX.Completed += (s, e) => OnAnimationDone();

        TileScale.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        TileScale.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
    }

    public void ApplyTileStyle(bool animate)
    {
        if (DataContext is not TileModel tile) return;

        Color targetColor;
        if (tile.TileType == TileType.Widget)
        {
            targetColor = ColorExtractorService.GetDefaultGlassColor();
        }
        else if (string.Equals(tile.TileStyle, "Colourful", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(tile.AccentColor))
            {
                tile.AccentColor = ColorExtractorService.ExtractAccentColor(tile.IconPath);
            }
            targetColor = ColorExtractorService.GetTintedColor(tile.AccentColor);
        }
        else
        {
            targetColor = ColorExtractorService.GetDefaultGlassColor();
        }

        if (RootBorder.Background is not SolidColorBrush currentBrush || currentBrush.IsFrozen)
        {
            RootBorder.Background = new SolidColorBrush(targetColor);
            return;
        }

        if (!animate)
        {
            currentBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            currentBrush.Color = targetColor;
        }
        else
        {
            var anim = new ColorAnimation
            {
                To = targetColor,
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            currentBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
    }

    #region Lazy Shared ContextMenu Realization

    private static ContextMenu? s_sharedContextMenu;
    private static TileControl? s_activeTargetTile;

    private static MenuItem? s_resizeMenuItem;
    private static MenuItem? s_styleMenuItem;
    private static MenuItem? s_styleDefaultMenuItem;
    private static MenuItem? s_styleColourfulMenuItem;
    private static MenuItem? s_groupMenuItem;
    private static MenuItem? s_addToGroupMenuItem;
    private static MenuItem? s_moveToWorkspaceMenuItem;
    private static Separator? s_singleAppSeparator;
    private static MenuItem? s_runAdminMenuItem;
    private static MenuItem? s_openLocationMenuItem;
    private static MenuItem? s_copyUrlMenuItem;
    private static Separator? s_unpinSeparator;
    private static MenuItem? s_pinToSidebarMenuItem;
    private static Wpf.Ui.Controls.SymbolIcon? s_pinToSidebarIcon;
    private static MenuItem? s_unpinMenuItem;

    private static ContextMenu GetOrCreateSharedContextMenu()
    {
        if (s_sharedContextMenu != null) return s_sharedContextMenu;

        s_sharedContextMenu = new ContextMenu();
        s_sharedContextMenu.Closed += OnSharedContextMenuClosed;

        s_resizeMenuItem = new MenuItem { Header = "Resize" };
        var resizeIcon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = Wpf.Ui.Controls.SymbolRegular.ResizeLarge16,
            FontSize = 18
        };
        resizeIcon.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush");
        s_resizeMenuItem.Icon = resizeIcon;

        s_styleMenuItem = new MenuItem { Header = "Style" };
        var styleIcon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = Wpf.Ui.Controls.SymbolRegular.PaintBrush16,
            FontSize = 18
        };
        styleIcon.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush");
        s_styleMenuItem.Icon = styleIcon;

        s_styleDefaultMenuItem = new MenuItem
        {
            Header = "Default",
            IsCheckable = true
        };
        s_styleDefaultMenuItem.Click += OnSharedStyleDefaultClick;

        s_styleColourfulMenuItem = new MenuItem
        {
            Header = "Colourful",
            IsCheckable = true
        };
        s_styleColourfulMenuItem.Click += OnSharedStyleColourfulClick;

        s_styleMenuItem.Items.Add(s_styleDefaultMenuItem);
        s_styleMenuItem.Items.Add(s_styleColourfulMenuItem);

        s_groupMenuItem = new MenuItem { Header = "Create Group from Tiles" };
        var groupIcon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = Wpf.Ui.Controls.SymbolRegular.FolderAdd24,
            FontSize = 18
        };
        groupIcon.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush");
        s_groupMenuItem.Icon = groupIcon;
        s_groupMenuItem.Click += OnSharedGroupTilesClick;

        s_addToGroupMenuItem = new MenuItem { Header = "Add to Group" };
        var addToGroupIcon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = Wpf.Ui.Controls.SymbolRegular.FolderArrowRight24,
            FontSize = 18
        };
        addToGroupIcon.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush");
        s_addToGroupMenuItem.Icon = addToGroupIcon;

        s_moveToWorkspaceMenuItem = new MenuItem { Header = "Move to Workspace" };
        var moveToWsIcon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowRight24,
            FontSize = 18
        };
        moveToWsIcon.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush");
        s_moveToWorkspaceMenuItem.Icon = moveToWsIcon;

        s_singleAppSeparator = new Separator();

        s_runAdminMenuItem = new MenuItem { Header = "Run as Administrator" };
        var runAdminIcon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = Wpf.Ui.Controls.SymbolRegular.Shield32,
            FontSize = 18
        };
        runAdminIcon.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush");
        s_runAdminMenuItem.Icon = runAdminIcon;
        s_runAdminMenuItem.Click += OnSharedRunAsAdminClick;

        s_openLocationMenuItem = new MenuItem { Header = "Open File Location" };
        var openLocationIcon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = Wpf.Ui.Controls.SymbolRegular.FolderOpenVertical20,
            FontSize = 18
        };
        openLocationIcon.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush");
        s_openLocationMenuItem.Icon = openLocationIcon;
        s_openLocationMenuItem.Click += OnSharedOpenFileLocationClick;

        var copyUrlIcon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = Wpf.Ui.Controls.SymbolRegular.Copy24,
            FontSize = 18
        };
        copyUrlIcon.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush");
        s_copyUrlMenuItem = new MenuItem
        {
            Header = "Copy Web Link",
            Visibility = Visibility.Collapsed,
            Icon = copyUrlIcon
        };
        s_copyUrlMenuItem.Click += OnSharedCopyUrlClick;

        s_unpinSeparator = new Separator();

        s_pinToSidebarIcon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = Wpf.Ui.Controls.SymbolRegular.Pin24,
            FontSize = 18
        };
        s_pinToSidebarIcon.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush");

        s_pinToSidebarMenuItem = new MenuItem
        {
            Header = "Pin to Sidebar",
            Icon = s_pinToSidebarIcon
        };
        s_pinToSidebarMenuItem.Click += OnSharedPinToSidebarClick;

        var unpinIcon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = Wpf.Ui.Controls.SymbolRegular.PinOff24,
            FontSize = 18
        };
        unpinIcon.SetResourceReference(Control.ForegroundProperty, "StatusErrorBrush");

        s_unpinMenuItem = new MenuItem
        {
            Header = "Unpin from MetroHub",
            Icon = unpinIcon
        };
        s_unpinMenuItem.SetResourceReference(Control.ForegroundProperty, "StatusErrorBrush");
        s_unpinMenuItem.Click += OnSharedUnpinClick;

        s_sharedContextMenu.Items.Add(s_resizeMenuItem);
        s_sharedContextMenu.Items.Add(s_styleMenuItem);
        s_sharedContextMenu.Items.Add(s_groupMenuItem);
        s_sharedContextMenu.Items.Add(s_addToGroupMenuItem);
        s_sharedContextMenu.Items.Add(s_moveToWorkspaceMenuItem);
        s_sharedContextMenu.Items.Add(s_singleAppSeparator);
        s_sharedContextMenu.Items.Add(s_runAdminMenuItem);
        s_sharedContextMenu.Items.Add(s_openLocationMenuItem);
        s_sharedContextMenu.Items.Add(s_copyUrlMenuItem);
        s_sharedContextMenu.Items.Add(s_unpinSeparator);
        s_sharedContextMenu.Items.Add(s_pinToSidebarMenuItem);
        s_sharedContextMenu.Items.Add(s_unpinMenuItem);

        return s_sharedContextMenu;
    }

    private static void AttachSharedContextMenu(TileControl tile)
    {
        var menu = GetOrCreateSharedContextMenu();

        if (s_activeTargetTile != null && !ReferenceEquals(s_activeTargetTile, tile))
        {
            s_activeTargetTile.RootBorder.ContextMenu = null;
        }

        s_activeTargetTile = tile;
        tile.RootBorder.ContextMenu = menu;
    }

    private static void DetachSharedContextMenu()
    {
        if (s_sharedContextMenu != null && s_sharedContextMenu.IsOpen)
        {
            s_sharedContextMenu.IsOpen = false;
        }
        if (s_activeTargetTile != null)
        {
            s_activeTargetTile.RootBorder.ContextMenu = null;
            s_activeTargetTile = null;
        }
    }

    private static void OnSharedContextMenuClosed(object sender, RoutedEventArgs e)
    {
        if (s_sharedContextMenu != null)
        {
            var dynamicItems = s_sharedContextMenu.Items.OfType<FrameworkElement>()
                .Where(m => (string?)m.Tag == "WidgetCustomMenu")
                .ToList();
            foreach (var item in dynamicItems)
            {
                s_sharedContextMenu.Items.Remove(item);
            }
        }

        if (s_activeTargetTile != null)
        {
            s_activeTargetTile.RootBorder.ContextMenu = null;
            s_activeTargetTile = null;
        }
    }

    #endregion

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Apps || (e.Key == Key.F10 && (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift))
        {
            AttachSharedContextMenu(this);
        }
    }

    private void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not TileModel tile) return;

        // If tile is not currently selected, clear other selections and select this one
        if (!tile.IsSelected)
        {
            WeakReferenceMessenger.Default.Send(new TileClearSelectionMessage());
            tile.IsSelected = true;
        }

        AttachSharedContextMenu(this);
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (DataContext is not TileModel tile) return;

        AttachSharedContextMenu(this);

        if (!tile.IsSelected)
        {
            WeakReferenceMessenger.Default.Send(new TileClearSelectionMessage());
            tile.IsSelected = true;
        }

        var selectedTiles = CanvasMessenger.GetSelectedTiles();
        int selectedCount = selectedTiles.Count;
        if (tile.IsSelected && selectedCount > 1)
        {
            if (s_resizeMenuItem != null) s_resizeMenuItem.Header = "Resize";
            if (s_styleMenuItem != null) s_styleMenuItem.Header = "Style";
            if (s_groupMenuItem != null) s_groupMenuItem.Header = "Create Group from Tiles";
            if (s_addToGroupMenuItem != null) s_addToGroupMenuItem.Header = "Add to Group";
            if (s_moveToWorkspaceMenuItem != null)
            {
                s_moveToWorkspaceMenuItem.Header = $"Move {selectedCount} Tiles to Workspace";
            }
            if (s_unpinMenuItem != null) s_unpinMenuItem.Header = "Unpin from MetroHub";

            if (s_runAdminMenuItem != null) s_runAdminMenuItem.Visibility = Visibility.Collapsed;
            if (s_openLocationMenuItem != null) s_openLocationMenuItem.Visibility = Visibility.Collapsed;
            if (s_singleAppSeparator != null) s_singleAppSeparator.Visibility = Visibility.Collapsed;
        }
        else
        {
            if (s_resizeMenuItem != null) s_resizeMenuItem.Header = "Resize";
            if (s_styleMenuItem != null) s_styleMenuItem.Header = "Style";
            if (s_groupMenuItem != null) s_groupMenuItem.Header = "Create Group from Tiles";
            if (s_addToGroupMenuItem != null) s_addToGroupMenuItem.Header = "Add to Group";
            if (s_moveToWorkspaceMenuItem != null)
            {
                s_moveToWorkspaceMenuItem.Header = "Move to Workspace";
            }
            if (s_unpinMenuItem != null) s_unpinMenuItem.Header = "Unpin from MetroHub";

            if (s_runAdminMenuItem != null) s_runAdminMenuItem.Visibility = Visibility.Visible;
            if (s_openLocationMenuItem != null) s_openLocationMenuItem.Visibility = Visibility.Visible;
            if (s_singleAppSeparator != null) s_singleAppSeparator.Visibility = Visibility.Visible;
        }

        // Widget-specific context menu adaptation: remove all prior dynamic items
        if (s_sharedContextMenu != null)
        {
            var priorDynamicItems = s_sharedContextMenu.Items.OfType<FrameworkElement>()
                .Where(m => (string?)m.Tag == "WidgetCustomMenu")
                .ToList();
            foreach (var item in priorDynamicItems)
            {
                s_sharedContextMenu.Items.Remove(item);
            }
        }

        if (tile.TileType == TileType.Widget)
        {
            if (s_runAdminMenuItem != null) s_runAdminMenuItem.Visibility = Visibility.Collapsed;
            if (s_openLocationMenuItem != null) s_openLocationMenuItem.Visibility = Visibility.Collapsed;
            if (s_singleAppSeparator != null) s_singleAppSeparator.Visibility = Visibility.Collapsed;
            if (s_styleMenuItem != null) s_styleMenuItem.Visibility = Visibility.Collapsed;
            if (s_unpinSeparator != null) s_unpinSeparator.Visibility = Visibility.Collapsed;
            if (s_groupMenuItem != null) s_groupMenuItem.Visibility = Visibility.Collapsed;

            if (tile.TileContent is IWidgetContextMenuProvider menuProvider && s_sharedContextMenu != null)
            {
                var customItems = menuProvider.GetContextMenuItems()?.ToList();
                if (customItems != null && customItems.Count > 0)
                {
                    int insertIdx = 1;
                    foreach (var item in customItems)
                    {
                        item.Tag = "WidgetCustomMenu";
                        s_sharedContextMenu.Items.Insert(insertIdx++, item);
                    }
                    s_sharedContextMenu.Items.Insert(insertIdx, new Separator { Tag = "WidgetCustomMenu" });
                }
            }
        }
        else
        {
            bool isWebUrl = tile.TileType == TileType.WebUrl;
            if (s_singleAppSeparator != null) s_singleAppSeparator.Visibility = isWebUrl ? Visibility.Collapsed : Visibility.Visible;
            if (s_runAdminMenuItem != null) s_runAdminMenuItem.Visibility = isWebUrl ? Visibility.Collapsed : Visibility.Visible;
            if (s_openLocationMenuItem != null) s_openLocationMenuItem.Visibility = isWebUrl ? Visibility.Collapsed : Visibility.Visible;
            if (s_copyUrlMenuItem != null)
            {
                s_copyUrlMenuItem.Visibility = isWebUrl ? Visibility.Visible : Visibility.Collapsed;
            }
            if (s_styleMenuItem != null) s_styleMenuItem.Visibility = Visibility.Visible;
            if (s_unpinSeparator != null) s_unpinSeparator.Visibility = Visibility.Visible;
            if (s_groupMenuItem != null)
            {
                s_groupMenuItem.Visibility = selectedTiles.Any(t => t.TileType == TileType.Widget)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }

            bool isColourful = string.Equals(tile.TileStyle, "Colourful", StringComparison.OrdinalIgnoreCase);
            if (s_styleDefaultMenuItem != null)
            {
                s_styleDefaultMenuItem.IsCheckable = true;
                s_styleDefaultMenuItem.IsChecked = !isColourful;
            }
            if (s_styleColourfulMenuItem != null)
            {
                s_styleColourfulMenuItem.IsCheckable = true;
                s_styleColourfulMenuItem.IsChecked = isColourful;
            }
        }

        PopulateResizeSubmenu(tile);
        PopulateAddToGroupSubmenu();
        PopulateMoveToWorkspaceSubmenu(tile, selectedTiles);
        var rail = CanvasMessenger.GetSidebarRail();
        ConfigureSidebarPinMenuItem(tile, rail, selectedTiles);
    }

    private static void ConfigureSidebarPinMenuItem(TileModel tile, ISidebarShortcutStore? store, IReadOnlyList<TileModel> selectedTiles)
    {
        if (s_pinToSidebarMenuItem == null) return;

        var (isVisible, isPinned, eligibleCount) = SidebarPinningService.GetPinState(store, tile, selectedTiles);

        if (!isVisible)
        {
            s_pinToSidebarMenuItem.Visibility = Visibility.Collapsed;
            return;
        }

        s_pinToSidebarMenuItem.Visibility = Visibility.Visible;

        if (isPinned)
        {
            s_pinToSidebarMenuItem.Header = eligibleCount > 1 ? "Unpin Selected Tiles from Sidebar" : "Unpin from Sidebar";
            if (s_pinToSidebarIcon != null) s_pinToSidebarIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.PinOff24;
        }
        else
        {
            s_pinToSidebarMenuItem.Header = eligibleCount > 1 ? $"Pin {eligibleCount} Tiles to Sidebar" : "Pin to Sidebar";
            if (s_pinToSidebarIcon != null) s_pinToSidebarIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Pin24;
        }
    }

    private static void PopulateResizeSubmenu(TileModel tile)
    {
        if (s_resizeMenuItem == null) return;
        s_resizeMenuItem.Items.Clear();

        // Phase 0.3: If tile is a widget and declares allowed sizes via IWidgetViewModel, dynamically populate from them:
        if (tile.TileType == TileType.Widget && tile.TileContent is IWidgetViewModel widgetVm && widgetVm.AllowedSizes?.Count > 0)
        {
            if (widgetVm.AllowedSizes.Count <= 1)
            {
                s_resizeMenuItem.Visibility = Visibility.Collapsed;
                return;
            }

            // Universal rule: Order resize submenu strictly from smallest to largest footprint
            var sortedSizes = widgetVm.AllowedSizes
                .OrderBy(s => s.SpanX * s.SpanY)
                .ThenBy(s => s.SpanX)
                .ThenBy(s => s.SpanY)
                .ToList();

            foreach (var size in sortedSizes)
            {
                var item = new MenuItem
                {
                    Header = size.DisplayName,
                    IsCheckable = true,
                    IsChecked = (tile.SpanX == size.SpanX && tile.SpanY == size.SpanY)
                };
                int spanX = size.SpanX;
                int spanY = size.SpanY;
                item.Click += (s, e) => SharedResizeTileTo(spanX, spanY);
                s_resizeMenuItem.Items.Add(item);
            }
            s_resizeMenuItem.Visibility = Visibility.Visible;
            return;
        }

        // App tiles keep their existing three-option behavior, strictly ordered from smallest to largest:
        // Small (1x1 = 1) -> Medium (2x2 = 4) -> Wide (4x2 = 8)
        var smallItem = new MenuItem
        {
            Header = "Small (1x1)",
            IsCheckable = true,
            IsChecked = (tile.SpanX == 1 && tile.SpanY == 1)
        };
        smallItem.Click += (s, e) => SharedResizeTileTo(1, 1);

        var mediumItem = new MenuItem
        {
            Header = "Medium (2x2)",
            IsCheckable = true,
            IsChecked = (tile.SpanX == 2 && tile.SpanY == 2)
        };
        mediumItem.Click += (s, e) => SharedResizeTileTo(2, 2);

        var wideItem = new MenuItem
        {
            Header = "Wide (4x2)",
            IsCheckable = true,
            IsChecked = (tile.SpanX == 4 && tile.SpanY == 2)
        };
        wideItem.Click += (s, e) => SharedResizeTileTo(4, 2);

        s_resizeMenuItem.Items.Add(smallItem);
        s_resizeMenuItem.Items.Add(mediumItem);
        s_resizeMenuItem.Items.Add(wideItem);
        s_resizeMenuItem.Visibility = Visibility.Visible;
    }

    private static void PopulateAddToGroupSubmenu()
    {
        if (s_addToGroupMenuItem == null) return;

        s_addToGroupMenuItem.Items.Clear();

        var groups = CanvasMessenger.GetGroups();
        if (groups.Count == 0)
        {
            var emptyItem = new MenuItem
            {
                Header = "(No groups available)",
                IsEnabled = false
            };
            s_addToGroupMenuItem.Items.Add(emptyItem);
            s_addToGroupMenuItem.IsEnabled = false;
            return;
        }

        s_addToGroupMenuItem.IsEnabled = true;

        foreach (var group in groups)
        {
            var item = new MenuItem();
            string baseTitle = string.IsNullOrWhiteSpace(group.Title) ? "Untitled Section" : group.Title;

            if (group.IsLocked)
            {
                var redBrush = RedMutedBrush;

                var headerBlock = new TextBlock
                {
                    Text = $"{baseTitle} (Locked)",
                    Foreground = redBrush,
                    VerticalAlignment = VerticalAlignment.Center
                };
                item.Header = headerBlock;
                item.Foreground = redBrush;
                item.Icon = new Wpf.Ui.Controls.SymbolIcon
                {
                    Symbol = Wpf.Ui.Controls.SymbolRegular.LockClosed24,
                    FontSize = 20,
                    Foreground = redBrush
                };
                item.IsEnabled = false;
                ToolTipService.SetShowOnDisabled(item, true);
                item.ToolTip = "This group is locked and cannot receive new tiles.";
            }
            else
            {
                item.Header = baseTitle;
                item.Cursor = Cursors.Hand;

                if (!string.IsNullOrWhiteSpace(group.HeaderColor))
                {
                    try
                    {
                        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(group.HeaderColor);
                        var ellipse = new System.Windows.Shapes.Ellipse
                        {
                            Width = 14,
                            Height = 14,
                            Fill = new System.Windows.Media.SolidColorBrush(color)
                        };
                        item.Icon = ellipse;
                    }
                    catch
                    {
                        item.Icon = new Wpf.Ui.Controls.SymbolIcon
                        {
                            Symbol = Wpf.Ui.Controls.SymbolRegular.Folder24,
                            FontSize = 20
                        };
                    }
                }
                else
                {
                    item.Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.Folder24,
                        FontSize = 20
                    };
                }

                var targetGroup = group;
                item.Click += (s, e) =>
                {
                    var targets = CanvasMessenger.GetSelectedTiles();
                    if (targets.Count == 0 && s_activeTargetTile?.DataContext is TileModel currentTile)
                    {
                        targets = new List<TileModel> { currentTile };
                    }
                    WeakReferenceMessenger.Default.Send(new TileAddToGroupMessage(targets, targetGroup));
                };
            }

            s_addToGroupMenuItem.Items.Add(item);
        }
    }

    private static void PopulateMoveToWorkspaceSubmenu(TileModel tile, IReadOnlyList<TileModel> selectedTiles)
    {
        if (s_moveToWorkspaceMenuItem == null) return;

        s_moveToWorkspaceMenuItem.Items.Clear();

        var wm = WorkspaceManager.Instance;
        var activeWs = wm.ActiveWorkspace;
        var destinationWorkspaces = wm.Workspaces
            .Where(w => activeWs == null || !string.Equals(w.Id, activeWs.Id, StringComparison.OrdinalIgnoreCase))
            .OrderBy(w => w.Order)
            .ToList();

        if (destinationWorkspaces.Count == 0)
        {
            var emptyItem = new MenuItem
            {
                Header = "(No other workspaces)",
                IsEnabled = false
            };
            s_moveToWorkspaceMenuItem.Items.Add(emptyItem);
            s_moveToWorkspaceMenuItem.IsEnabled = false;
            return;
        }

        s_moveToWorkspaceMenuItem.IsEnabled = true;

        foreach (var ws in destinationWorkspaces)
        {
            var wsIcon = new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = Wpf.Ui.Controls.SymbolRegular.Desktop24,
                FontSize = 18
            };
            wsIcon.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush");

            var item = new MenuItem
            {
                Header = ws.Name,
                Icon = wsIcon
            };

            string targetWsId = ws.Id;
            item.Click += (s, e) =>
            {
                var activeTile = s_activeTargetTile?.DataContext as TileModel ?? tile;
                var tilesToMove = selectedTiles.Count > 0 && selectedTiles.Contains(activeTile)
                    ? selectedTiles.ToList()
                    : new List<TileModel> { activeTile };

                foreach (var t in tilesToMove)
                {
                    WorkspaceManager.Instance.MoveTileToWorkspace(t, targetWsId);
                }
            };

            s_moveToWorkspaceMenuItem.Items.Add(item);
        }
    }

    private static void OnSharedGroupTilesClick(object sender, RoutedEventArgs e)
    {
        if (s_activeTargetTile?.DataContext is TileModel tile)
        {
            WeakReferenceMessenger.Default.Send(new TileCreateGroupMessage(tile));
        }
    }

    private static void OnSharedStyleDefaultClick(object sender, RoutedEventArgs e)
    {
        if (s_activeTargetTile?.DataContext is TileModel tile)
        {
            WeakReferenceMessenger.Default.Send(new TileBatchStyleMessage("Default", tile));
        }
    }

    private static void OnSharedStyleColourfulClick(object sender, RoutedEventArgs e)
    {
        if (s_activeTargetTile?.DataContext is TileModel tile)
        {
            WeakReferenceMessenger.Default.Send(new TileBatchStyleMessage("Colourful", tile));
        }
    }

    private void OnToggleLockClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TileModel tile)
        {
            tile.IsLocked = !tile.IsLocked;
            RaiseEvent(new TileModifiedEventArgs(TileModifiedEvent, tile, isResize: false));
        }
    }

    private void OnResizeSmallClick(object sender, RoutedEventArgs e) => ResizeTileTo(1, 1);
    private void OnResizeMediumClick(object sender, RoutedEventArgs e) => ResizeTileTo(2, 2);
    private void OnResizeWideClick(object sender, RoutedEventArgs e) => ResizeTileTo(4, 2);

    private void ResizeTileTo(int newSpanX, int newSpanY)
    {
        if (DataContext is TileModel tile)
        {
            WeakReferenceMessenger.Default.Send(new TileBatchResizeMessage(newSpanX, newSpanY, tile));
        }
    }

    private static void SharedResizeTileTo(int newSpanX, int newSpanY)
    {
        if (s_activeTargetTile?.DataContext is TileModel tile)
        {
            WeakReferenceMessenger.Default.Send(new TileBatchResizeMessage(newSpanX, newSpanY, tile));
        }
    }

    public void AnimateResize(int oldSpanX, int oldSpanY, int newSpanX, int newSpanY)
    {
        double oldW = (oldSpanX * 64) - 8;
        double oldH = (oldSpanY * 64) - 8;
        double newW = (newSpanX * 64) - 8;
        double newH = (newSpanY * 64) - 8;

        var animW = new DoubleAnimation(oldW, newW, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
        };
        var animH = new DoubleAnimation(oldH, newH, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
        };

        animW.Completed += (s, e) =>
        {
            RootBorder.BeginAnimation(FrameworkElement.WidthProperty, null);
            RootBorder.Width = newW;
        };
        animH.Completed += (s, e) =>
        {
            RootBorder.BeginAnimation(FrameworkElement.HeightProperty, null);
            RootBorder.Height = newH;
        };

        RootBorder.BeginAnimation(FrameworkElement.WidthProperty, animW);
        RootBorder.BeginAnimation(FrameworkElement.HeightProperty, animH);
    }

    private static void OnSharedRunAsAdminClick(object sender, RoutedEventArgs e)
    {
        if (s_activeTargetTile?.DataContext is TileModel tile && !string.IsNullOrWhiteSpace(tile.TargetPath))
        {
            ProcessLauncherService.LaunchTargetAsync(tile.TargetPath, tile.Arguments, runAsAdmin: true, displayName: tile.Title);
            s_activeTargetTile.RaiseEvent(new RoutedEventArgs(TileActivatedEvent, tile));
        }
    }

    private static void OnSharedOpenFileLocationClick(object sender, RoutedEventArgs e)
    {
        if (s_activeTargetTile?.DataContext is TileModel tile && !string.IsNullOrWhiteSpace(tile.TargetPath))
        {
            try
            {
                string target = IconExtractorService.ResolveShortcutTarget(tile.TargetPath);
                if (File.Exists(target))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
                }
            }
            catch { }
        }
    }

    private static void OnSharedCopyUrlClick(object sender, RoutedEventArgs e)
    {
        if (s_activeTargetTile?.DataContext is TileModel tile && !string.IsNullOrWhiteSpace(tile.TargetPath))
        {
            try
            {
                Clipboard.SetText(tile.TargetPath);
            }
            catch { }
        }
    }

    private static void OnSharedPinToSidebarClick(object sender, RoutedEventArgs e)
    {
        if (s_activeTargetTile?.DataContext is TileModel tile)
        {
            WeakReferenceMessenger.Default.Send(new TileToggleSidebarPinMessage(tile));
        }
    }

    private static void OnSharedUnpinClick(object sender, RoutedEventArgs e)
    {
        if (s_activeTargetTile?.DataContext is TileModel tile)
        {
            WeakReferenceMessenger.Default.Send(new TileBatchUnpinMessage(tile));
        }
    }
}

public class TileModifiedEventArgs : RoutedEventArgs
{
    public TileModel Tile { get; }
    public int OldSpanX { get; }
    public int OldSpanY { get; }
    public bool IsResize { get; }

    public TileModifiedEventArgs(RoutedEvent routedEvent, TileModel tile, int oldSpanX = 0, int oldSpanY = 0, bool isResize = false)
        : base(routedEvent, tile)
    {
        Tile = tile;
        OldSpanX = oldSpanX;
        OldSpanY = oldSpanY;
        IsResize = isResize;
    }
}

