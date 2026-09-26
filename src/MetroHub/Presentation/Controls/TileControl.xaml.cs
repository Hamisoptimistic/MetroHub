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



    private static readonly Brush MenuIconForegroundBrush = CreateFrozenBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF));
    private static readonly Brush RedMutedBrush = CreateFrozenBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));

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
        if (DataContext is TileModel { TileType: TileType.Widget, TargetPath: "notepad" })
        {
            return;
        }

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
        if (DataContext is TileModel { TileType: TileType.Widget } tm && (tm.TargetPath == "notepad" || tm.TargetPath == "network" || tm.TargetPath == "photos" || tm.TargetPath == "power" || tm.TargetPath == "caffeine" || tm.TargetPath == "caffeine_sleep"))
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
                if (tile.TileContent is Widgets.Catalog.Clock.ClockWidgetViewModel clockVm)
                {
                    clockVm.Toggle24HourFormat();
                    return;
                }
                if (tile.TileContent is Widgets.Catalog.Stub.StubWidgetViewModel stubVm)
                {
                    stubVm.CycleColor();
                    return;
                }
                if (tile.TileContent is Widgets.Catalog.Photos.PhotosWidgetViewModel photosVm)
                {
                    if (photosVm.IsEmpty)
                    {
                        photosVm.ChooseFolder();
                    }
                    else
                    {
                        photosVm.OpenCurrentPhoto();
                    }
                    return;
                }
                if (tile.TileContent is Widgets.Catalog.Weather.WeatherWidgetViewModel weatherVm)
                {
                    weatherVm.ToggleUnits();
                    return;
                }
                if (tile.TileContent is Widgets.Catalog.Quotes.QuotesWidgetViewModel quotesVm)
                {
                    quotesVm.NextQuote();
                    return;
                }
                if (tile.TileContent is Widgets.Catalog.Habit.HabitWidgetViewModel)
                {
                    return;
                }
                if (tile.TileContent is Widgets.Catalog.Dino.DinoWidgetViewModel)
                {
                    return;
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

            NativeMethods.LaunchTarget(tile.TargetPath, tile.Arguments, tile.RunAsAdmin);
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

            if (tile.TileContent is Widgets.Catalog.Clock.ClockWidgetViewModel clockVm)
            {
                // 1. Time Format Submenu
                var timeFormatItem = new MenuItem
                {
                    Header = "Time Format",
                    Tag = "WidgetCustomMenu",
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.Clock24,
                        FontSize = 20,
                        Foreground = MenuIconForegroundBrush
                    }
                };

                var item12Hr = new MenuItem
                {
                    Header = "12 Hours",
                    IsCheckable = true,
                    IsChecked = !clockVm.Is24HourFormat
                };
                item12Hr.Click += (s, ev) => clockVm.SetTimeFormat(false);

                var item24Hr = new MenuItem
                {
                    Header = "24 Hours",
                    IsCheckable = true,
                    IsChecked = clockVm.Is24HourFormat
                };
                item24Hr.Click += (s, ev) => clockVm.SetTimeFormat(true);

                timeFormatItem.Items.Add(item12Hr);
                timeFormatItem.Items.Add(item24Hr);

                // 2. Font Submenu
                var fontItem = new MenuItem
                {
                    Header = "Font",
                    Tag = "WidgetCustomMenu",
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.TextFont24,
                        FontSize = 20,
                        Foreground = MenuIconForegroundBrush
                    }
                };

                var itemSegoe = new MenuItem
                {
                    Header = "Segoe UI Variable",
                    IsCheckable = true,
                    IsChecked = clockVm.FontFace == Widgets.Catalog.Clock.ClockFontFace.SegoeUI
                };
                itemSegoe.Click += (s, ev) => clockVm.SetFontFace(Widgets.Catalog.Clock.ClockFontFace.SegoeUI);

                var itemMonoton = new MenuItem
                {
                    Header = "Monoton",
                    IsCheckable = true,
                    IsChecked = clockVm.FontFace == Widgets.Catalog.Clock.ClockFontFace.Monoton
                };
                itemMonoton.Click += (s, ev) => clockVm.SetFontFace(Widgets.Catalog.Clock.ClockFontFace.Monoton);

                var itemDigital7 = new MenuItem
                {
                    Header = "Digital-7",
                    IsCheckable = true,
                    IsChecked = clockVm.FontFace == Widgets.Catalog.Clock.ClockFontFace.Digital7
                };
                itemDigital7.Click += (s, ev) => clockVm.SetFontFace(Widgets.Catalog.Clock.ClockFontFace.Digital7);

                var itemFffForward = new MenuItem
                {
                    Header = "FFF Forward",
                    IsCheckable = true,
                    IsChecked = clockVm.FontFace == Widgets.Catalog.Clock.ClockFontFace.FffForward
                };
                itemFffForward.Click += (s, ev) => clockVm.SetFontFace(Widgets.Catalog.Clock.ClockFontFace.FffForward);

                var itemKarnivore = new MenuItem
                {
                    Header = "Karnivore Digit",
                    IsCheckable = true,
                    IsChecked = clockVm.FontFace == Widgets.Catalog.Clock.ClockFontFace.KarnivoreDigit
                };
                itemKarnivore.Click += (s, ev) => clockVm.SetFontFace(Widgets.Catalog.Clock.ClockFontFace.KarnivoreDigit);

                fontItem.Items.Add(itemSegoe);
                fontItem.Items.Add(itemMonoton);
                fontItem.Items.Add(itemDigital7);
                fontItem.Items.Add(itemFffForward);
                fontItem.Items.Add(itemKarnivore);

                // 3. Divider after widget features
                var clockDivider = new Separator
                {
                    Tag = "WidgetCustomMenu"
                };

                // Universal Widget Hierarchy:
                // Index 0: Resize
                // Index 1: Time Format
                // Index 2: Font
                // Index 3: Separator
                // Then: AddToGroupMenuItem, UnpinMenuItem (GroupMenuItem & UnpinSeparator collapsed)
                TileContextMenu.Items.Insert(1, timeFormatItem);
                TileContextMenu.Items.Insert(2, fontItem);
                TileContextMenu.Items.Insert(3, clockDivider);
            }
            else if (tile.TileContent is Widgets.Catalog.Calendar.CalendarWidgetViewModel calVm && calVm.IsFullSize)
            {
                // Only the 8x6 month grid has a month to jump back to; the 4x4 date card
                // is always showing today, so this action is omitted there.
                var todayItem = new MenuItem
                {
                    Header = "Go to Today",
                    Tag = "WidgetCustomMenu",
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.CalendarToday24,
                        FontSize = 20,
                        Foreground = MenuIconForegroundBrush
                    }
                };
                todayItem.Click += (s, ev) => calVm.ResetToToday();

                var calDivider = new Separator { Tag = "WidgetCustomMenu" };

                // Universal Widget Hierarchy:
                // Index 0: Resize
                // Index 1: Go to Today
                // Index 2: Separator
                // Then: AddToGroupMenuItem, UnpinMenuItem (GroupMenuItem & UnpinSeparator collapsed)
                TileContextMenu.Items.Insert(1, todayItem);
                TileContextMenu.Items.Insert(2, calDivider);
            }

            else if (tile.TileContent is Widgets.Catalog.Pomodoro.PomodoroWidgetViewModel pomodoroVm)
            {
                var presetItem = new MenuItem
                {
                    Header = "Focus Duration",
                    Tag = "WidgetCustomMenu",
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.Timer24,
                        FontSize = 20,
                        Foreground = MenuIconForegroundBrush
                    }
                };

                var item25 = new MenuItem
                {
                    Header = "25m Focus / 5m Break (Classic)",
                    IsCheckable = true,
                    IsChecked = pomodoroVm.FocusMinutes == 25
                };
                item25.Click += (s, ev) => pomodoroVm.SetPreset(25, 5, 15);

                var item50 = new MenuItem
                {
                    Header = "50m Focus / 10m Break (Deep Work)",
                    IsCheckable = true,
                    IsChecked = pomodoroVm.FocusMinutes == 50
                };
                item50.Click += (s, ev) => pomodoroVm.SetPreset(50, 10, 30);

                var item15 = new MenuItem
                {
                    Header = "15m Focus / 3m Break (Sprint)",
                    IsCheckable = true,
                    IsChecked = pomodoroVm.FocusMinutes == 15
                };
                item15.Click += (s, ev) => pomodoroVm.SetPreset(15, 3, 10);

                presetItem.Items.Add(item25);
                presetItem.Items.Add(item50);
                presetItem.Items.Add(item15);

                var pomodoroDivider = new Separator { Tag = "WidgetCustomMenu" };

                // Universal Widget Hierarchy:
                // Index 0: Resize
                // Index 1: Focus Duration
                // Index 2: Separator
                // Then: AddToGroupMenuItem, UnpinMenuItem (GroupMenuItem & UnpinSeparator collapsed)
                TileContextMenu.Items.Insert(1, presetItem);
                TileContextMenu.Items.Insert(2, pomodoroDivider);
            }
            else if (tile.TileContent is Widgets.Catalog.Photos.PhotosWidgetViewModel photosVm)
            {
                // 1. Choose Photos Folder
                var chooseFolderItem = new MenuItem
                {
                    Header = "Choose Photo Stream Folder...",
                    Tag = "WidgetCustomMenu",
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.FolderOpen24,
                        FontSize = 20,
                        Foreground = MenuIconForegroundBrush
                    }
                };
                chooseFolderItem.Click += (s, ev) => photosVm.ChooseFolder();

                // 2. Stream Interval Submenu
                var intervalItem = new MenuItem
                {
                    Header = "Photo Stream Interval",
                    Tag = "WidgetCustomMenu",
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.Timer24,
                        FontSize = 20,
                        Foreground = MenuIconForegroundBrush
                    }
                };

                var item10s = new MenuItem
                {
                    Header = "10 Seconds",
                    IsCheckable = true,
                    IsChecked = photosVm.IntervalSeconds == 10
                };
                item10s.Click += (s, ev) => photosVm.SetInterval(10);

                var item30s = new MenuItem
                {
                    Header = "30 Seconds (Default)",
                    IsCheckable = true,
                    IsChecked = photosVm.IntervalSeconds == 30
                };
                item30s.Click += (s, ev) => photosVm.SetInterval(30);

                var item1m = new MenuItem
                {
                    Header = "1 Minute",
                    IsCheckable = true,
                    IsChecked = photosVm.IntervalSeconds == 60
                };
                item1m.Click += (s, ev) => photosVm.SetInterval(60);

                var item5m = new MenuItem
                {
                    Header = "5 Minutes",
                    IsCheckable = true,
                    IsChecked = photosVm.IntervalSeconds == 300
                };
                item5m.Click += (s, ev) => photosVm.SetInterval(300);

                intervalItem.Items.Add(item10s);
                intervalItem.Items.Add(item30s);
                intervalItem.Items.Add(item1m);
                intervalItem.Items.Add(item5m);

                // 3. Shuffle Stream Toggle
                var shuffleItem = new MenuItem
                {
                    Header = "Shuffle Photo Stream",
                    Tag = "WidgetCustomMenu",
                    IsCheckable = true,
                    IsChecked = photosVm.Shuffle,
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowShuffle24,
                        FontSize = 20,
                        Foreground = MenuIconForegroundBrush
                    }
                };
                shuffleItem.Click += (s, ev) => photosVm.ToggleShuffle();

                // 4. Include Subfolders Toggle
                var subfoldersItem = new MenuItem
                {
                    Header = "Include Subfolders",
                    Tag = "WidgetCustomMenu",
                    IsCheckable = true,
                    IsChecked = photosVm.IncludeSubfolders,
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.Folder24,
                        FontSize = 20,
                        Foreground = MenuIconForegroundBrush
                    }
                };
                subfoldersItem.Click += (s, ev) => photosVm.ToggleIncludeSubfolders();

                // 5. Fit Mode Submenu (Smart Fit with Backdrop vs Fill)
                var fitModeItem = new MenuItem
                {
                    Header = "Photo Stream Fit",
                    Tag = "WidgetCustomMenu",
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.SlideSize24,
                        FontSize = 20,
                        Foreground = MenuIconForegroundBrush
                    }
                };

                var itemSmartFit = new MenuItem
                {
                    Header = "Smart Fit (With Backdrop)",
                    IsCheckable = true,
                    IsChecked = photosVm.FitMode == "FitWithBlur"
                };
                itemSmartFit.Click += (s, ev) => photosVm.SetFitMode("FitWithBlur");

                var itemFill = new MenuItem
                {
                    Header = "Fill Tile (Crop)",
                    IsCheckable = true,
                    IsChecked = photosVm.FitMode == "Fill"
                };
                itemFill.Click += (s, ev) => photosVm.SetFitMode("Fill");

                fitModeItem.Items.Add(itemSmartFit);
                fitModeItem.Items.Add(itemFill);

                // 6. Open in Windows Photos
                var openPhotoItem = new MenuItem
                {
                    Header = "Open in Windows Photos",
                    Tag = "WidgetCustomMenu",
                    IsEnabled = !photosVm.IsEmpty,
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.Image24,
                        FontSize = 20,
                        Foreground = MenuIconForegroundBrush
                    }
                };
                openPhotoItem.Click += (s, ev) => photosVm.OpenCurrentPhoto();

                var photosDivider = new Separator { Tag = "WidgetCustomMenu" };

                // Universal Widget Hierarchy:
                // Index 0: Resize
                // Index 1: Open in Windows Photos
                // Index 2: Choose Folder
                // Index 3: Interval
                // Index 4: Shuffle
                // Index 5: Subfolders
                // Index 6: Fit Mode
                // Index 7: Separator
                TileContextMenu.Items.Insert(1, openPhotoItem);
                TileContextMenu.Items.Insert(2, chooseFolderItem);
                TileContextMenu.Items.Insert(3, intervalItem);
                TileContextMenu.Items.Insert(4, shuffleItem);
                TileContextMenu.Items.Insert(5, subfoldersItem);
                TileContextMenu.Items.Insert(6, fitModeItem);
                TileContextMenu.Items.Insert(7, photosDivider);
            }

            if (tile.TileContent is Widgets.Catalog.Notepad.NotepadWidgetViewModel notepadVm)
            {
                var viewModeItem = new MenuItem
                {
                    Header = "View Mode",
                    Tag = "WidgetCustomMenu",
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.Notepad24,
                        FontSize = 20,
                        Foreground = MenuIconForegroundBrush
                    }
                };

                var notesMode = new MenuItem
                {
                    Header = "Notes",
                    IsCheckable = true,
                    IsChecked = notepadVm.IsNotesView
                };
                notesMode.Click += (s, ev) => notepadVm.SwitchToNotes();

                var todoMode = new MenuItem
                {
                    Header = "To-Do Tasks",
                    IsCheckable = true,
                    IsChecked = notepadVm.IsTodoView
                };
                todoMode.Click += (s, ev) => notepadVm.SwitchToTodo();

                viewModeItem.Items.Add(notesMode);
                viewModeItem.Items.Add(todoMode);

                var notepadDivider = new Separator { Tag = "WidgetCustomMenu" };
                TileContextMenu.Items.Insert(1, viewModeItem);

                if (notepadVm.HasCompletedTasks)
                {
                    var clearCompletedItem = new MenuItem
                    {
                        Header = "Clear Completed Tasks",
                        Tag = "WidgetCustomMenu",
                        Icon = new Wpf.Ui.Controls.SymbolIcon
                        {
                            Symbol = Wpf.Ui.Controls.SymbolRegular.DismissCircle20,
                            FontSize = 20,
                            Foreground = MenuIconForegroundBrush
                        }
                    };
                    clearCompletedItem.Click += (s, ev) => notepadVm.ClearCompleted();
                    TileContextMenu.Items.Insert(2, clearCompletedItem);
                    TileContextMenu.Items.Insert(3, notepadDivider);
                }
                else
                {
                    TileContextMenu.Items.Insert(2, notepadDivider);
                }
            }
            else if (tile.TileContent is Widgets.Catalog.Weather.WeatherWidgetViewModel weatherVm)
            {
                var changeLocationItem = new MenuItem
                {
                    Header = "Change Location...",
                    Tag = "WidgetCustomMenu",
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.Location24,
                        FontSize = 20,
                        Foreground = MenuIconForegroundBrush
                    }
                };
                changeLocationItem.Click += (s, ev) =>
                {
                    MetroHub.MainWindow.Current?.ShowSetWeatherLocationDialog(weatherVm);
                };

                var autoLocationItem = new MenuItem
                {
                    Header = "Use Automatic Location (GPS / IP)",
                    Tag = "WidgetCustomMenu",
                    IsCheckable = true,
                    IsChecked = weatherVm.IsAutoLocation,
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.MyLocation24,
                        FontSize = 20,
                        Foreground = MenuIconForegroundBrush
                    }
                };
                autoLocationItem.Click += (s, ev) => _ = weatherVm.UseAutoLocationAsync();

                var toggleUnitsItem = new MenuItem
                {
                    Header = weatherVm.IsFahrenheit ? "Switch to Celsius (°C)" : "Switch to Fahrenheit (°F)",
                    Tag = "WidgetCustomMenu",
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.Temperature24,
                        FontSize = 20,
                        Foreground = MenuIconForegroundBrush
                    }
                };
                toggleUnitsItem.Click += (s, ev) => weatherVm.ToggleUnits();

                var refreshItem = new MenuItem
                {
                    Header = "Refresh Weather",
                    Tag = "WidgetCustomMenu",
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowSync24,
                        FontSize = 20,
                        Foreground = MenuIconForegroundBrush
                    }
                };
                refreshItem.Click += (s, ev) => _ = weatherVm.RefreshAsync();

                var styleItem = new MenuItem
                {
                    Header = "Style",
                    Tag = "WidgetCustomMenu",
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.Color24,
                        FontSize = 20,
                        Foreground = MenuIconForegroundBrush
                    }
                };

                var ambientGlowItem = new MenuItem
                {
                    Header = "Ambient Glow",
                    IsCheckable = true,
                    IsChecked = weatherVm.BackgroundStyle == Widgets.Catalog.Weather.WeatherBackgroundStyle.AmbientGlow
                };
                ambientGlowItem.Click += (s, ev) => weatherVm.SetStyle(Widgets.Catalog.Weather.WeatherBackgroundStyle.AmbientGlow);

                var horizonAuraItem = new MenuItem
                {
                    Header = "Horizon Aura",
                    IsCheckable = true,
                    IsChecked = weatherVm.BackgroundStyle == Widgets.Catalog.Weather.WeatherBackgroundStyle.HorizonAura
                };
                horizonAuraItem.Click += (s, ev) => weatherVm.SetStyle(Widgets.Catalog.Weather.WeatherBackgroundStyle.HorizonAura);

                var noneStyleItem = new MenuItem
                {
                    Header = "None",
                    IsCheckable = true,
                    IsChecked = weatherVm.BackgroundStyle == Widgets.Catalog.Weather.WeatherBackgroundStyle.None
                };
                noneStyleItem.Click += (s, ev) => weatherVm.SetStyle(Widgets.Catalog.Weather.WeatherBackgroundStyle.None);

                styleItem.Items.Add(ambientGlowItem);
                styleItem.Items.Add(horizonAuraItem);
                styleItem.Items.Add(noneStyleItem);

                var weatherDivider = new Separator { Tag = "WidgetCustomMenu" };
                TileContextMenu.Items.Insert(1, changeLocationItem);
                TileContextMenu.Items.Insert(2, autoLocationItem);
                TileContextMenu.Items.Insert(3, toggleUnitsItem);
                TileContextMenu.Items.Insert(4, styleItem);
                TileContextMenu.Items.Insert(5, refreshItem);
                TileContextMenu.Items.Insert(6, weatherDivider);
            }
            else if (tile.TileContent is Widgets.Catalog.Media.MediaWidgetViewModel mediaVm)
            {
                var zuneActionItem = new MenuItem
                {
                    Header = mediaVm.IsZuneMode ? "Switch to Standard Player (8x4)" : "Switch to Zune Player (4x6)",
                    Tag = "WidgetCustomMenu",
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.MusicNote224,
                        FontSize = 20,
                        Foreground = MenuIconForegroundBrush
                    }
                };
                zuneActionItem.Click += (s, ev) =>
                {
                    if (mediaVm.IsZuneMode)
                    {
                        ResizeTileTo(8, 4);
                    }
                    else
                    {
                        ResizeTileTo(4, 6);
                    }
                };

                var mediaDivider = new Separator { Tag = "WidgetCustomMenu" };
                TileContextMenu.Items.Insert(1, zuneActionItem);
                TileContextMenu.Items.Insert(2, mediaDivider);
            }
            else if (tile.TileContent is Widgets.Catalog.Quotes.QuotesWidgetViewModel quotesVm)
            {
                var nextQuoteItem = new MenuItem
                {
                    Header = "Next Quote",
                    Tag = "WidgetCustomMenu",
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowShuffle24,
                        FontSize = 20,
                        Foreground = MenuIconForegroundBrush
                    }
                };
                nextQuoteItem.Click += (s, ev) => quotesVm.NextQuote();

                var copyQuoteItem = new MenuItem
                {
                    Header = "Copy Quote",
                    Tag = "WidgetCustomMenu",
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.Copy24,
                        FontSize = 20,
                        Foreground = MenuIconForegroundBrush
                    }
                };
                copyQuoteItem.Click += (s, ev) => quotesVm.CopyQuote();

                var fontMenuItem = new MenuItem
                {
                    Header = "Font",
                    Tag = "WidgetCustomMenu",
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.TextFont24,
                        FontSize = 20,
                        Foreground = MenuIconForegroundBrush
                    }
                };

                var itemMerriweather = new MenuItem
                {
                    Header = "Merriweather",
                    IsCheckable = true,
                    IsChecked = quotesVm.SelectedFont == Widgets.Catalog.Quotes.QuoteFontFamilyChoice.Merriweather
                };
                itemMerriweather.Click += (s, ev) => quotesVm.SetFont(Widgets.Catalog.Quotes.QuoteFontFamilyChoice.Merriweather);

                var itemQuintessential = new MenuItem
                {
                    Header = "Quintessential",
                    IsCheckable = true,
                    IsChecked = quotesVm.SelectedFont == Widgets.Catalog.Quotes.QuoteFontFamilyChoice.Quintessential
                };
                itemQuintessential.Click += (s, ev) => quotesVm.SetFont(Widgets.Catalog.Quotes.QuoteFontFamilyChoice.Quintessential);

                var itemGeorgia = new MenuItem
                {
                    Header = "Georgia",
                    IsCheckable = true,
                    IsChecked = quotesVm.SelectedFont == Widgets.Catalog.Quotes.QuoteFontFamilyChoice.Georgia
                };
                itemGeorgia.Click += (s, ev) => quotesVm.SetFont(Widgets.Catalog.Quotes.QuoteFontFamilyChoice.Georgia);

                var itemPalatino = new MenuItem
                {
                    Header = "Palatino Linotype",
                    IsCheckable = true,
                    IsChecked = quotesVm.SelectedFont == Widgets.Catalog.Quotes.QuoteFontFamilyChoice.Palatino
                };
                itemPalatino.Click += (s, ev) => quotesVm.SetFont(Widgets.Catalog.Quotes.QuoteFontFamilyChoice.Palatino);

                var itemSegoe = new MenuItem
                {
                    Header = "Segoe UI",
                    IsCheckable = true,
                    IsChecked = quotesVm.SelectedFont == Widgets.Catalog.Quotes.QuoteFontFamilyChoice.SegoeUI
                };
                itemSegoe.Click += (s, ev) => quotesVm.SetFont(Widgets.Catalog.Quotes.QuoteFontFamilyChoice.SegoeUI);

                fontMenuItem.Items.Add(itemMerriweather);
                fontMenuItem.Items.Add(itemQuintessential);
                fontMenuItem.Items.Add(itemGeorgia);
                fontMenuItem.Items.Add(itemPalatino);
                fontMenuItem.Items.Add(itemSegoe);

                fontMenuItem.Items.Add(new Separator());

                var styleSubItem = new MenuItem
                {
                    Header = "Style",
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.TextItalic24,
                        FontSize = 18,
                        Foreground = MenuIconForegroundBrush
                    }
                };

                var itemRegular = new MenuItem
                {
                    Header = "Regular",
                    IsCheckable = true,
                    IsChecked = quotesVm.SelectedStyle == Widgets.Catalog.Quotes.QuoteFontStyleChoice.Regular
                };
                itemRegular.Click += (s, ev) => quotesVm.SetStyle(Widgets.Catalog.Quotes.QuoteFontStyleChoice.Regular);

                var itemItalic = new MenuItem
                {
                    Header = "Italic",
                    IsCheckable = true,
                    IsChecked = quotesVm.SelectedStyle == Widgets.Catalog.Quotes.QuoteFontStyleChoice.Italic
                };
                itemItalic.Click += (s, ev) => quotesVm.SetStyle(Widgets.Catalog.Quotes.QuoteFontStyleChoice.Italic);

                styleSubItem.Items.Add(itemRegular);
                styleSubItem.Items.Add(itemItalic);

                fontMenuItem.Items.Add(styleSubItem);

                var quotesDivider = new Separator { Tag = "WidgetCustomMenu" };
                TileContextMenu.Items.Insert(1, nextQuoteItem);
                TileContextMenu.Items.Insert(2, copyQuoteItem);
                TileContextMenu.Items.Insert(3, fontMenuItem);
                TileContextMenu.Items.Insert(4, quotesDivider);
            }
            else if (tile.TileContent is Widgets.Catalog.Habit.HabitWidgetViewModel habitVm)
            {
                var editHabitItem = new MenuItem
                {
                    Header = "Edit Habit...",
                    Tag = "WidgetCustomMenu",
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.Edit24,
                        FontSize = 20,
                        Foreground = MenuIconForegroundBrush
                    }
                };
                editHabitItem.Click += (s, ev) => habitVm.EditHabit();

                var markTodayItem = new MenuItem
                {
                    Header = "Toggle Today (Done / Unmarked)",
                    Tag = "WidgetCustomMenu",
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24,
                        FontSize = 20,
                        Foreground = MenuIconForegroundBrush
                    }
                };
                markTodayItem.Click += (s, ev) => habitVm.ToggleToday();

                var jumpCurrentMonthItem = new MenuItem
                {
                    Header = "Jump to Current Month",
                    Tag = "WidgetCustomMenu",
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.CalendarToday24,
                        FontSize = 20,
                        Foreground = MenuIconForegroundBrush
                    }
                };
                jumpCurrentMonthItem.Click += (s, ev) => habitVm.JumpToCurrentMonth();

                var resetItem = new MenuItem
                {
                    Header = "Reset All Habit Data...",
                    Tag = "WidgetCustomMenu",
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.Delete24,
                        FontSize = 20,
                        Foreground = MenuIconForegroundBrush
                    }
                };
                resetItem.Click += (s, ev) =>
                {
                    var result = System.Windows.MessageBox.Show(
                        $"Are you sure you want to reset all tracking data for '{habitVm.HabitName}'? This cannot be undone.",
                        "Reset Habit Data",
                        System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxImage.Warning);
                    if (result == System.Windows.MessageBoxResult.Yes)
                    {
                        habitVm.ResetAllData();
                    }
                };

                var habitDivider = new Separator { Tag = "WidgetCustomMenu" };
                TileContextMenu.Items.Insert(1, editHabitItem);
                TileContextMenu.Items.Insert(2, markTodayItem);
                TileContextMenu.Items.Insert(3, jumpCurrentMonthItem);
                TileContextMenu.Items.Insert(4, resetItem);
                TileContextMenu.Items.Insert(5, habitDivider);
            }
            else if (tile.TileContent is Widgets.Catalog.Dino.DinoWidgetViewModel dinoVm)
            {
                // 1. Mute Sound Toggle
                var muteItem = new MenuItem
                {
                    Header = "Mute Sound",
                    Tag = "WidgetCustomMenu",
                    IsCheckable = true,
                    IsChecked = dinoVm.IsMuted,
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = dinoVm.IsMuted ? Wpf.Ui.Controls.SymbolRegular.SpeakerOff24 : Wpf.Ui.Controls.SymbolRegular.Speaker224,
                        FontSize = 20,
                        Foreground = MenuIconForegroundBrush
                    }
                };
                muteItem.Click += (s, ev) => dinoVm.ToggleMute();

                // 2. Reduced Motion Submenu
                var motionItem = new MenuItem
                {
                    Header = "Reduced Motion",
                    Tag = "WidgetCustomMenu",
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.Accessibility24,
                        FontSize = 20,
                        Foreground = MenuIconForegroundBrush
                    }
                };

                var autoMotionItem = new MenuItem
                {
                    Header = "Follow Windows Setting",
                    IsCheckable = true,
                    IsChecked = dinoVm.ReducedMotion == null
                };
                autoMotionItem.Click += (s, ev) => dinoVm.SetReducedMotion(null);

                var onMotionItem = new MenuItem
                {
                    Header = "Enabled (Low Motion)",
                    IsCheckable = true,
                    IsChecked = dinoVm.ReducedMotion == true
                };
                onMotionItem.Click += (s, ev) => dinoVm.SetReducedMotion(true);

                var offMotionItem = new MenuItem
                {
                    Header = "Disabled (Full Animation)",
                    IsCheckable = true,
                    IsChecked = dinoVm.ReducedMotion == false
                };
                offMotionItem.Click += (s, ev) => dinoVm.SetReducedMotion(false);

                motionItem.Items.Add(autoMotionItem);
                motionItem.Items.Add(onMotionItem);
                motionItem.Items.Add(offMotionItem);

                // 3. Reset High Score
                var resetScoreItem = new MenuItem
                {
                    Header = "Reset High Score",
                    Tag = "WidgetCustomMenu",
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowReset24,
                        FontSize = 20,
                        Foreground = MenuIconForegroundBrush
                    }
                };
                resetScoreItem.Click += (s, ev) => dinoVm.ResetHighScore();

                var dinoDivider = new Separator { Tag = "WidgetCustomMenu" };
                TileContextMenu.Items.Insert(1, muteItem);
                TileContextMenu.Items.Insert(2, motionItem);
                TileContextMenu.Items.Insert(3, resetScoreItem);
                TileContextMenu.Items.Insert(4, dinoDivider);
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
            GroupMenuItem.Visibility = mainWindow.SelectedTiles.Any(t => t.TileType == TileType.Widget)
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
        PopulateAddToGroupSubmenu(mainWindow);
        SidebarPinningService.ConfigureTileContextMenu(PinToSidebarMenuItem, PinToSidebarIcon, tile, mainWindow);
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
            SidebarPinningService.HandleContextMenuClick(tile, MetroHub.MainWindow.Current);
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

