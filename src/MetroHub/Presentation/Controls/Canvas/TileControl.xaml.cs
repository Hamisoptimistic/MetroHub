using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using CommunityToolkit.Mvvm.Messaging;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
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

    public TileControl()
    {
        InitializeComponent();
        Loaded += (s, e) =>
        {
            ActiveTiles.RemoveAll(tc => !tc.IsLoaded && !ReferenceEquals(tc, this));
            if (!ActiveTiles.Contains(this)) ActiveTiles.Add(this);
            ApplyTileStyle(animate: false);
        };
        Unloaded += (s, e) =>
        {
            ActiveTiles.Remove(this);
            ActiveTiles.RemoveAll(tc => !tc.IsLoaded);
        };
        DataContextChanged += (s, e) => ApplyTileStyle(animate: false);

        MouseEnter += OnMouseEnter;
        MouseLeave += OnMouseLeave;
        MouseMove += OnMouseMove;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        Point pos = e.GetPosition(RootBorder);
        UpdateRevealPositions(pos);
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
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
        if (!IsMouseOver && RevealEdgeBorder.Opacity > 0)
        {
            RevealEdgeBorder.BeginAnimation(UIElement.OpacityProperty, null);
            RevealEdgeBorder.Opacity = 0.0;
        }
    }

    private static DateTime _lastControlLaunchTime = DateTime.MinValue;
    private static string? _lastControlLaunchPath;
    private static readonly object _controlLaunchLock = new();

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

    private void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not TileModel tile) return;

        // If tile is not currently selected, clear other selections and select this one
        if (!tile.IsSelected)
        {
            WeakReferenceMessenger.Default.Send(new TileClearSelectionMessage());
            tile.IsSelected = true;
        }
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (DataContext is not TileModel tile) return;

        if (!tile.IsSelected)
        {
            WeakReferenceMessenger.Default.Send(new TileClearSelectionMessage());
            tile.IsSelected = true;
        }

        var selectedTiles = CanvasMessenger.GetSelectedTiles();
        int selectedCount = selectedTiles.Count;
        if (tile.IsSelected && selectedCount > 1)
        {
            ResizeMenuItem.Header = "Resize";
            StyleMenuItem.Header = "Style";
            GroupMenuItem.Header = "Create Group from Tiles";
            AddToGroupMenuItem.Header = "Add to Group";
            UnpinMenuItem.Header = "Unpin from MetroHub";

            RunAdminMenuItem.Visibility = Visibility.Collapsed;
            OpenLocationMenuItem.Visibility = Visibility.Collapsed;
            SingleAppSeparator.Visibility = Visibility.Collapsed;
        }
        else
        {
            ResizeMenuItem.Header = "Resize";
            StyleMenuItem.Header = "Style";
            GroupMenuItem.Header = "Create Group from Tiles";
            AddToGroupMenuItem.Header = "Add to Group";
            UnpinMenuItem.Header = "Unpin from MetroHub";

            RunAdminMenuItem.Visibility = Visibility.Visible;
            OpenLocationMenuItem.Visibility = Visibility.Visible;
            SingleAppSeparator.Visibility = Visibility.Visible;
        }

        // Widget-specific context menu adaptation: remove all prior dynamic items
        var priorDynamicItems = TileContextMenu.Items.OfType<FrameworkElement>()
            .Where(m => (string?)m.Tag == "WidgetCustomMenu")
            .ToList();
        foreach (var item in priorDynamicItems)
        {
            TileContextMenu.Items.Remove(item);
        }

        if (tile.TileType == TileType.Widget)
        {
            RunAdminMenuItem.Visibility = Visibility.Collapsed;
            OpenLocationMenuItem.Visibility = Visibility.Collapsed;
            SingleAppSeparator.Visibility = Visibility.Collapsed;
            StyleMenuItem.Visibility = Visibility.Collapsed;
            UnpinSeparator.Visibility = Visibility.Collapsed;
            GroupMenuItem.Visibility = Visibility.Collapsed;

            if (tile.TileContent is IWidgetContextMenuProvider menuProvider)
            {
                var customItems = menuProvider.GetContextMenuItems()?.ToList();
                if (customItems != null && customItems.Count > 0)
                {
                    int insertIdx = 1;
                    foreach (var item in customItems)
                    {
                        item.Tag = "WidgetCustomMenu";
                        TileContextMenu.Items.Insert(insertIdx++, item);
                    }
                    TileContextMenu.Items.Insert(insertIdx, new Separator { Tag = "WidgetCustomMenu" });
                }
            }
        }
        else
        {
            bool isWebUrl = tile.TileType == TileType.WebUrl;
            SingleAppSeparator.Visibility = isWebUrl ? Visibility.Collapsed : Visibility.Visible;
            RunAdminMenuItem.Visibility = isWebUrl ? Visibility.Collapsed : Visibility.Visible;
            OpenLocationMenuItem.Visibility = isWebUrl ? Visibility.Collapsed : Visibility.Visible;
            if (CopyUrlMenuItem != null)
            {
                CopyUrlMenuItem.Visibility = isWebUrl ? Visibility.Visible : Visibility.Collapsed;
            }
            StyleMenuItem.Visibility = Visibility.Visible;
            UnpinSeparator.Visibility = Visibility.Visible;
            GroupMenuItem.Visibility = selectedTiles.Any(t => t.TileType == TileType.Widget)
                ? Visibility.Collapsed
                : Visibility.Visible;

            bool isColourful = string.Equals(tile.TileStyle, "Colourful", StringComparison.OrdinalIgnoreCase);
            if (StyleDefaultMenuItem != null)
            {
                StyleDefaultMenuItem.IsCheckable = true;
                StyleDefaultMenuItem.IsChecked = !isColourful;
            }
            if (StyleColourfulMenuItem != null)
            {
                StyleColourfulMenuItem.IsCheckable = true;
                StyleColourfulMenuItem.IsChecked = isColourful;
            }
        }

        PopulateResizeSubmenu(tile);
        PopulateAddToGroupSubmenu();
        var rail = CanvasMessenger.GetSidebarRail();
        ConfigureSidebarPinMenuItem(tile, rail, selectedTiles);
    }

    private void ConfigureSidebarPinMenuItem(TileModel tile, ISidebarShortcutStore? store, IReadOnlyList<TileModel> selectedTiles)
    {
        if (PinToSidebarMenuItem == null) return;

        var (isVisible, isPinned, eligibleCount) = SidebarPinningService.GetPinState(store, tile, selectedTiles);

        if (!isVisible)
        {
            PinToSidebarMenuItem.Visibility = Visibility.Collapsed;
            return;
        }

        PinToSidebarMenuItem.Visibility = Visibility.Visible;

        if (isPinned)
        {
            PinToSidebarMenuItem.Header = eligibleCount > 1 ? "Unpin Selected Tiles from Sidebar" : "Unpin from Sidebar";
            if (PinToSidebarIcon != null) PinToSidebarIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.PinOff24;
        }
        else
        {
            PinToSidebarMenuItem.Header = eligibleCount > 1 ? $"Pin {eligibleCount} Tiles to Sidebar" : "Pin to Sidebar";
            if (PinToSidebarIcon != null) PinToSidebarIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Pin24;
        }
    }

    private void PopulateResizeSubmenu(TileModel tile)
    {
        if (ResizeMenuItem == null) return;
        ResizeMenuItem.Items.Clear();

        // Phase 0.3: If tile is a widget and declares allowed sizes via IWidgetViewModel, dynamically populate from them:
        if (tile.TileType == TileType.Widget && tile.TileContent is IWidgetViewModel widgetVm && widgetVm.AllowedSizes?.Count > 0)
        {
            if (widgetVm.AllowedSizes.Count <= 1)
            {
                ResizeMenuItem.Visibility = Visibility.Collapsed;
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
                item.Click += (s, e) => ResizeTileTo(spanX, spanY);
                ResizeMenuItem.Items.Add(item);
            }
            ResizeMenuItem.Visibility = Visibility.Visible;
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
        smallItem.Click += OnResizeSmallClick;

        var mediumItem = new MenuItem
        {
            Header = "Medium (2x2)",
            IsCheckable = true,
            IsChecked = (tile.SpanX == 2 && tile.SpanY == 2)
        };
        mediumItem.Click += OnResizeMediumClick;

        var wideItem = new MenuItem
        {
            Header = "Wide (4x2)",
            IsCheckable = true,
            IsChecked = (tile.SpanX == 4 && tile.SpanY == 2)
        };
        wideItem.Click += OnResizeWideClick;

        ResizeMenuItem.Items.Add(smallItem);
        ResizeMenuItem.Items.Add(mediumItem);
        ResizeMenuItem.Items.Add(wideItem);
        ResizeMenuItem.Visibility = Visibility.Visible;
    }

    private void PopulateAddToGroupSubmenu()
    {
        if (AddToGroupMenuItem == null) return;

        AddToGroupMenuItem.Items.Clear();

        var groups = CanvasMessenger.GetGroups();
        if (groups.Count == 0)
        {
            var emptyItem = new MenuItem
            {
                Header = "(No groups available)",
                IsEnabled = false
            };
            AddToGroupMenuItem.Items.Add(emptyItem);
            AddToGroupMenuItem.IsEnabled = false;
            return;
        }

        AddToGroupMenuItem.IsEnabled = true;

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
                    if (targets.Count == 0 && DataContext is TileModel currentTile)
                    {
                        targets = new List<TileModel> { currentTile };
                    }
                    WeakReferenceMessenger.Default.Send(new TileAddToGroupMessage(targets, targetGroup));
                };
            }

            AddToGroupMenuItem.Items.Add(item);
        }
    }

    private void OnGroupTilesClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TileModel tile)
        {
            WeakReferenceMessenger.Default.Send(new TileCreateGroupMessage(tile));
        }
    }

    private void OnStyleDefaultClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TileModel tile)
        {
            WeakReferenceMessenger.Default.Send(new TileBatchStyleMessage("Default", tile));
        }
    }

    private void OnStyleColourfulClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TileModel tile)
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

    private void OnRunAsAdminClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TileModel tile && !string.IsNullOrWhiteSpace(tile.TargetPath))
        {
            ProcessLauncherService.LaunchTargetAsync(tile.TargetPath, tile.Arguments, runAsAdmin: true, displayName: tile.Title);
            RaiseEvent(new RoutedEventArgs(TileActivatedEvent, tile));
        }
    }

    private void OnOpenFileLocationClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TileModel tile && !string.IsNullOrWhiteSpace(tile.TargetPath))
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

    private void OnCopyUrlClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TileModel tile && !string.IsNullOrWhiteSpace(tile.TargetPath))
        {
            try
            {
                Clipboard.SetText(tile.TargetPath);
            }
            catch { }
        }
    }

    private void OnPinToSidebarClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TileModel tile)
        {
            WeakReferenceMessenger.Default.Send(new TileToggleSidebarPinMessage(tile));
        }
    }

    private void OnUnpinClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TileModel tile)
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

