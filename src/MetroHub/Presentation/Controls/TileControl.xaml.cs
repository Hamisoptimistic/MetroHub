using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using MetroHub.Core.Models;
using MetroHub.Core.Services;

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



    public static readonly List<TileControl> ActiveTiles = new();

    public TileControl()
    {
        InitializeComponent();
        Loaded += (s, e) =>
        {
            if (!ActiveTiles.Contains(this)) ActiveTiles.Add(this);
            ApplyTileStyle(animate: false);
        };
        Unloaded += (s, e) => { ActiveTiles.Remove(this); };
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
        AnimateRevealFill(1.0, 100);
        RevealEdgeBorder.BeginAnimation(UIElement.OpacityProperty, null);
        RevealEdgeBorder.Opacity = 1.0;
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        AnimateRevealFill(0.0, 200);
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

    public void LaunchTile()
    {
        if (DataContext is TileModel tile && !string.IsNullOrWhiteSpace(tile.TargetPath))
        {
            NativeMethods.LaunchTarget(tile.TargetPath, tile.Arguments, tile.RunAsAdmin);
            RaiseEvent(new RoutedEventArgs(TileActivatedEvent, tile));
        }
    }

    public void AnimatePressDown()
    {
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
        RootBorder.Effect = shadow;
    }

    public void AnimateRelease(Action? onCompleted = null)
    {
        var anim = new DoubleAnimation(TileScale.ScaleX, 1.0, TimeSpan.FromMilliseconds(80))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        anim.Completed += (s, e) =>
        {
            TileScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            TileScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            TileScale.ScaleX = 1.0;
            TileScale.ScaleY = 1.0;
            RootBorder.Effect = null;
            onCompleted?.Invoke();
        };
        TileScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        TileScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    public void ApplyTileStyle(bool animate)
    {
        if (DataContext is not TileModel tile) return;

        Color targetColor;
        if (string.Equals(tile.TileStyle, "Colourful", StringComparison.OrdinalIgnoreCase))
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
        var mainWindow = MetroHub.MainWindow.Current;
        if (mainWindow == null) return;

        // If tile is not currently selected, clear other selections and select this one
        if (!tile.IsSelected)
        {
            mainWindow.ClearTileSelection();
            tile.IsSelected = true;
        }
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (DataContext is not TileModel tile) return;
        var mainWindow = MetroHub.MainWindow.Current;
        if (mainWindow == null) return;

        if (!tile.IsSelected)
        {
            mainWindow.ClearTileSelection();
            tile.IsSelected = true;
        }

        int selectedCount = mainWindow.SelectedTiles.Count;
        if (tile.IsSelected && selectedCount > 1)
        {
            ResizeMenuItem.Header = "Resize";
            StyleMenuItem.Header = "Style";
            GroupMenuItem.Header = "Group into New Section";
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
            GroupMenuItem.Header = "Group into New Section";
            AddToGroupMenuItem.Header = "Add to Group";
            UnpinMenuItem.Header = "Unpin from MetroHub";

            RunAdminMenuItem.Visibility = Visibility.Visible;
            OpenLocationMenuItem.Visibility = Visibility.Visible;
            SingleAppSeparator.Visibility = Visibility.Visible;
        }

        PopulateAddToGroupSubmenu(mainWindow);
    }

    private void PopulateAddToGroupSubmenu(MetroHub.MainWindow mainWindow)
    {
        if (AddToGroupMenuItem == null) return;

        AddToGroupMenuItem.Items.Clear();

        var groups = mainWindow.Groups.ToList();
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
                var redBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));

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
                    FontSize = 16,
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
                            Width = 12,
                            Height = 12,
                            Fill = new System.Windows.Media.SolidColorBrush(color)
                        };
                        item.Icon = ellipse;
                    }
                    catch
                    {
                        item.Icon = new Wpf.Ui.Controls.SymbolIcon
                        {
                            Symbol = Wpf.Ui.Controls.SymbolRegular.Folder24,
                            FontSize = 16
                        };
                    }
                }
                else
                {
                    item.Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.Folder24,
                        FontSize = 16
                    };
                }

                var targetGroup = group;
                item.Click += (s, e) =>
                {
                    var targets = mainWindow.SelectedTiles;
                    if (targets.Count == 0 && DataContext is TileModel currentTile)
                    {
                        targets = new List<TileModel> { currentTile };
                    }
                    mainWindow.AddTilesToExistingGroup(targets, targetGroup);
                };
            }

            AddToGroupMenuItem.Items.Add(item);
        }
    }

    private void OnGroupTilesClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TileModel tile)
        {
            MetroHub.MainWindow.Current?.CreateGroupFromSelectedTiles(tile);
        }
    }

    private void OnStyleDefaultClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TileModel tile)
        {
            MetroHub.MainWindow.Current?.BatchStyleSelectedTiles("Default", tile);
        }
    }

    private void OnStyleColourfulClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TileModel tile)
        {
            MetroHub.MainWindow.Current?.BatchStyleSelectedTiles("Colourful", tile);
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
            MetroHub.MainWindow.Current?.BatchResizeSelectedTiles(newSpanX, newSpanY, tile);
        }
    }

    public void AnimateResize(int oldSpanX, int oldSpanY, int newSpanX, int newSpanY)
    {
        double oldW = (oldSpanX * 64) - 8;
        double oldH = (oldSpanY * 64) - 8;
        double newW = (newSpanX * 64) - 8;
        double newH = (newSpanY * 64) - 8;

        var animW = new DoubleAnimation(oldW, newW, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var animH = new DoubleAnimation(oldH, newH, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        animW.Completed += (s, e) => RootBorder.BeginAnimation(FrameworkElement.WidthProperty, null);
        animH.Completed += (s, e) => RootBorder.BeginAnimation(FrameworkElement.HeightProperty, null);

        RootBorder.BeginAnimation(FrameworkElement.WidthProperty, animW);
        RootBorder.BeginAnimation(FrameworkElement.HeightProperty, animH);

        // Content fade in transition
        Grid? active = null;
        if (newSpanX == 1 && newSpanY == 1) active = SmallContentGrid;
        else if (newSpanX == 2 && newSpanY == 2) active = MediumContentGrid;
        else if (newSpanX == 4 && newSpanY == 2) active = WideContentGrid;

        if (active != null)
        {
            var fade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            fade.Completed += (s, e) =>
            {
                active.BeginAnimation(UIElement.OpacityProperty, null);
                active.Opacity = 1.0;
            };
            active.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }

    private void OnRunAsAdminClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TileModel tile && !string.IsNullOrWhiteSpace(tile.TargetPath))
        {
            NativeMethods.LaunchTarget(tile.TargetPath, tile.Arguments, runAsAdmin: true);
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

    private void OnUnpinClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TileModel tile)
        {
            MetroHub.MainWindow.Current?.BatchUnpinSelectedTiles(tile);
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
