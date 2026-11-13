using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MetroHub.Widgets.Catalog.Game2048;

/// <summary>
/// Interaction logic for Game2048WidgetView.xaml.
/// GPU-accelerated RenderTransform canvas game view with smooth cubic sliding and merge/spawn animations.
/// Adheres strictly to MetroHub UI, performance, and lifecycle standards.
/// </summary>
public sealed partial class Game2048WidgetView : UserControl
{
    private const double BoardPadding = 8.0;
    private const double TileStep = 98.0; // 90px tile + 8px gap
    private const double TileSize = 90.0;

    private const double PopPeakScale = 1.08;
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PopDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PopDuration = TimeSpan.FromMilliseconds(130);
    private static readonly TimeSpan SpawnDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SpawnDuration = TimeSpan.FromMilliseconds(200);

    private static readonly CubicEase SlideEasing = new() { EasingMode = EasingMode.EaseInOut };
    private static readonly CubicEase SpawnEasing = new() { EasingMode = EasingMode.EaseOut };
    private static readonly KeySpline PopSpline = new(0.25, 0.1, 0.25, 1.0);

    private static readonly DoubleAnimationUsingKeyFrames s_spawnAnimation;
    private static readonly DoubleAnimationUsingKeyFrames s_popAnimation;
    private static readonly DoubleAnimationUsingKeyFrames s_dissolveAnimation;

    private readonly struct PendingVisualUpdate
    {
        public readonly Border Border;
        public readonly long PieceId;
        public readonly int Value;

        public PendingVisualUpdate(Border border, long pieceId, int value)
        {
            Border = border;
            PieceId = pieceId;
            Value = value;
        }
    }

    private sealed class TileVisualHolder
    {
        public readonly TextBlock TextBlock;
        public readonly ScaleTransform Scale;
        public readonly TranslateTransform Translate;

        public TileVisualHolder(TextBlock textBlock, ScaleTransform scale, TranslateTransform translate)
        {
            TextBlock = textBlock;
            Scale = scale;
            Translate = translate;
        }
    }

    private Game2048WidgetViewModel? _viewModel;
    private readonly Dictionary<long, Border> _tileViews = new(16);
    private readonly Dictionary<long, int> _displayedValues = new(16);
    private readonly Stack<Border> _tilePool = new(24);
    private readonly List<Border> _dissolvingTiles = new(8);
    private readonly List<PendingVisualUpdate> _pendingUpdates = new(8);
    private readonly DispatcherTimer _postSlideTimer;

    private FontFamily? _cachedFontFamily;
    private CornerRadius _cachedCornerRadius = new(8);

    private Point _gestureStartPoint;
    private bool _isTrackingGesture;

    static Game2048WidgetView()
    {
        SlideEasing.Freeze();
        SpawnEasing.Freeze();
        PopSpline.Freeze();

        // Spawn Animation: 0.0 -> 0.0 at 100ms -> 1.0 at 300ms
        var spawnAnim = new DoubleAnimationUsingKeyFrames();
        spawnAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        spawnAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(SpawnDelay)));
        spawnAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(SpawnDelay + SpawnDuration), SpawnEasing));
        spawnAnim.Freeze();
        s_spawnAnimation = spawnAnim;

        // Pop Animation: 1.0 -> 1.08 at 150ms -> 1.0 at 230ms (BeginTime = 100ms)
        var popAnim = new DoubleAnimationUsingKeyFrames
        {
            BeginTime = PopDelay
        };
        popAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        popAnim.KeyFrames.Add(new SplineDoubleKeyFrame(PopPeakScale, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(50)), PopSpline));
        popAnim.KeyFrames.Add(new SplineDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(PopDuration), PopSpline));
        popAnim.Freeze();
        s_popAnimation = popAnim;

        // Dissolve Animation: 1.0 -> 1.0 at 70ms -> 0.0 at 100ms
        var dissolveAnim = new DoubleAnimationUsingKeyFrames();
        dissolveAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        dissolveAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70))));
        dissolveAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(SlideDuration), SlideEasing));
        dissolveAnim.Freeze();
        s_dissolveAnimation = dissolveAnim;
    }

    public Game2048WidgetView()
    {
        InitializeComponent();

        _postSlideTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = SlideDuration
        };
        _postSlideTimer.Tick += OnPostSlideTimerTick;

        ResolveThemeResources();
        PrewarmTilePool(24);

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void PrewarmTilePool(int count)
    {
        while (TilesCanvas.Children.Count < count)
        {
            var border = CreateNewTileBorder();
            border.Visibility = Visibility.Collapsed;
            TilesCanvas.Children.Add(border);
            _tilePool.Push(border);
        }
    }

    private Border CreateNewTileBorder()
    {
        var textBlock = new TextBlock
        {
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            FontFamily = _cachedFontFamily
        };

        var textContainer = new Grid
        {
            Width = TileSize,
            Height = TileSize,
            ClipToBounds = true
        };
        textContainer.Children.Add(textBlock);

        var scaleTransform = new ScaleTransform(1.0, 1.0);
        var translateTransform = new TranslateTransform(0, 0);
        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(scaleTransform);     // Index 0
        transformGroup.Children.Add(translateTransform); // Index 1

        var border = new Border
        {
            Width = TileSize,
            Height = TileSize,
            BorderThickness = new Thickness(0),
            CornerRadius = _cachedCornerRadius,
            Child = textContainer,
            Tag = new TileVisualHolder(textBlock, scaleTransform, translateTransform),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = transformGroup,
            SnapsToDevicePixels = false,
            UseLayoutRounding = false
        };

        return border;
    }

    private Border AcquireTileElement(double initialX, double initialY)
    {
        Border border;
        if (_tilePool.Count > 0)
        {
            border = _tilePool.Pop();
        }
        else
        {
            border = CreateNewTileBorder();
            TilesCanvas.Children.Add(border);
        }

        var (scale, translate) = GetTransforms(border);
        scale.ScaleX = 1.0;
        scale.ScaleY = 1.0;
        translate.X = initialX;
        translate.Y = initialY;

        border.Opacity = 1.0;
        border.Visibility = Visibility.Visible;
        return border;
    }

    private void RecycleTileElement(Border border)
    {
        var (scale, translate) = GetTransforms(border);

        border.BeginAnimation(UIElement.OpacityProperty, null);
        border.Opacity = 1.0;

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = 1.0;
        scale.ScaleY = 1.0;

        translate.BeginAnimation(TranslateTransform.XProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);

        Panel.SetZIndex(border, 0);
        border.Visibility = Visibility.Collapsed;
        _tilePool.Push(border);
    }

    private void ResolveThemeResources()
    {
        var ff = TryFindResource("AppFontFamily") as FontFamily
                 ?? Application.Current?.TryFindResource("AppFontFamily") as FontFamily;
        if (ff != null)
        {
            _cachedFontFamily = ff;
        }

        if (TryFindResource("TileCornerRadius") is CornerRadius cr)
        {
            _cachedCornerRadius = cr;
        }
        else if (Application.Current?.TryFindResource("TileCornerRadius") is CornerRadius appCr)
        {
            _cachedCornerRadius = appCr;
        }
        else
        {
            _cachedCornerRadius = new CornerRadius(8);
        }
    }

    private void ApplyCachedThemeResourcesToAllTiles()
    {
        for (int i = 0; i < TilesCanvas.Children.Count; i++)
        {
            if (TilesCanvas.Children[i] is Border b)
            {
                b.CornerRadius = _cachedCornerRadius;
                if (b.Tag is TileVisualHolder holder && _cachedFontFamily != null)
                {
                    holder.TextBlock.FontFamily = _cachedFontFamily;
                }
            }
        }
    }

    private void OnPostSlideTimerTick(object? sender, EventArgs e)
    {
        ProcessPostSlideUpdates();
    }

    private void ProcessPostSlideUpdates()
    {
        _postSlideTimer.Stop();

        for (int i = 0; i < _dissolvingTiles.Count; i++)
        {
            RecycleTileElement(_dissolvingTiles[i]);
        }
        _dissolvingTiles.Clear();

        for (int i = 0; i < _pendingUpdates.Count; i++)
        {
            var update = _pendingUpdates[i];
            if (_tileViews.TryGetValue(update.PieceId, out var current) && ReferenceEquals(current, update.Border))
            {
                UpdateTileVisuals(update.Border, update.Value);
                _displayedValues[update.PieceId] = update.Value;
            }
        }
        _pendingUpdates.Clear();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.BoardChanged -= OnBoardChanged;
            _viewModel.OnUnloaded();
        }

        _viewModel = DataContext as Game2048WidgetViewModel;

        if (_viewModel != null)
        {
            _viewModel.BoardChanged += OnBoardChanged;
            RenderBoard(animate: false);
        }
        else
        {
            _postSlideTimer.Stop();
            ProcessPostSlideUpdates();
            RecycleAllActiveTiles();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ResolveThemeResources();
        ApplyCachedThemeResourcesToAllTiles();

        if (_viewModel != null)
        {
            _viewModel.BoardChanged -= OnBoardChanged;
            _viewModel.BoardChanged += OnBoardChanged;
            RenderBoard(animate: false);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _postSlideTimer.Stop();
        ProcessPostSlideUpdates();

        if (_viewModel != null)
        {
            _viewModel.BoardChanged -= OnBoardChanged;
            _viewModel.OnUnloaded();
        }
    }

    private void RecycleAllActiveTiles()
    {
        foreach (var border in _tileViews.Values)
        {
            RecycleTileElement(border);
        }
        _tileViews.Clear();
        _displayedValues.Clear();
    }

    private void OnBoardChanged()
    {
        RenderBoard(animate: SystemParameters.ClientAreaAnimation);
    }

    private void RenderBoard(bool animate)
    {
        if (_viewModel == null) return;

        if (_postSlideTimer.IsEnabled || _dissolvingTiles.Count > 0 || _pendingUpdates.Count > 0)
        {
            ProcessPostSlideUpdates();
        }

        var engineTiles = _viewModel.Engine.Tiles;

        // 1. Pre-clean orphaned visual elements (e.g. from state reload or undo)
        Span<long> orphanedIds = stackalloc long[_tileViews.Count];
        int orphanCount = 0;
        foreach (var id in _tileViews.Keys)
        {
            if (!ContainsEngineId(engineTiles, id))
            {
                orphanedIds[orphanCount++] = id;
            }
        }

        for (int i = 0; i < orphanCount; i++)
        {
            long id = orphanedIds[i];
            if (_tileViews.Remove(id, out var orphanedBorder))
            {
                RecycleTileElement(orphanedBorder);
            }
            _displayedValues.Remove(id);
        }

        // 2. Process each tile in engineTiles
        for (int i = 0; i < engineTiles.Count; i++)
        {
            var piece = engineTiles[i];
            double targetX = BoardPadding + piece.Col * TileStep;
            double targetY = BoardPadding + piece.Row * TileStep;

            if (!_tileViews.TryGetValue(piece.Id, out var tileBorder))
            {
                // Case A: New Tile
                tileBorder = AcquireTileElement(targetX, targetY);
                _tileViews[piece.Id] = tileBorder;
                _displayedValues[piece.Id] = piece.Value;
                UpdateTileVisuals(tileBorder, piece.Value);
                Panel.SetZIndex(tileBorder, 1);

                var (scale, _) = GetTransforms(tileBorder);
                if (animate && piece.IsNew)
                {
                    scale.ScaleX = 0.0;
                    scale.ScaleY = 0.0;
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, s_spawnAnimation);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, s_spawnAnimation);
                }
                else
                {
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    scale.ScaleX = 1.0;
                    scale.ScaleY = 1.0;
                }
            }
            else
            {
                var (scale, translate) = GetTransforms(tileBorder);

                if (piece.MergedInto != null)
                {
                    // Case B: Consumed Merging Tile (slides under destination with soft dissolve, then recycled)
                    Panel.SetZIndex(tileBorder, 2);
                    _tileViews.Remove(piece.Id);
                    _displayedValues.Remove(piece.Id);

                    if (animate)
                    {
                        double startX = translate.X;
                        double startY = translate.Y;

                        var animX = new DoubleAnimation(startX, targetX, SlideDuration) { EasingFunction = SlideEasing };
                        var animY = new DoubleAnimation(startY, targetY, SlideDuration) { EasingFunction = SlideEasing };

                        tileBorder.BeginAnimation(UIElement.OpacityProperty, s_dissolveAnimation);
                        translate.BeginAnimation(TranslateTransform.XProperty, animX, HandoffBehavior.SnapshotAndReplace);
                        translate.BeginAnimation(TranslateTransform.YProperty, animY, HandoffBehavior.SnapshotAndReplace);

                        _dissolvingTiles.Add(tileBorder);
                    }
                    else
                    {
                        RecycleTileElement(tileBorder);
                    }
                }
                else
                {
                    // Case C: Surviving / Destination Tile
                    Panel.SetZIndex(tileBorder, piece.IsMerged ? 3 : 2);

                    if (animate)
                    {
                        double currentX = translate.X;
                        double currentY = translate.Y;

                        if (Math.Abs(currentX - targetX) > 0.5 || Math.Abs(currentY - targetY) > 0.5)
                        {
                            var animX = new DoubleAnimation(currentX, targetX, SlideDuration) { EasingFunction = SlideEasing };
                            var animY = new DoubleAnimation(currentY, targetY, SlideDuration) { EasingFunction = SlideEasing };
                            translate.BeginAnimation(TranslateTransform.XProperty, animX, HandoffBehavior.SnapshotAndReplace);
                            translate.BeginAnimation(TranslateTransform.YProperty, animY, HandoffBehavior.SnapshotAndReplace);
                        }
                        else
                        {
                            translate.BeginAnimation(TranslateTransform.XProperty, null);
                            translate.BeginAnimation(TranslateTransform.YProperty, null);
                            translate.X = targetX;
                            translate.Y = targetY;
                        }
                    }
                    else
                    {
                        translate.BeginAnimation(TranslateTransform.XProperty, null);
                        translate.BeginAnimation(TranslateTransform.YProperty, null);
                        translate.X = targetX;
                        translate.Y = targetY;
                    }

                    if (piece.IsMerged && animate)
                    {
                        _pendingUpdates.Add(new PendingVisualUpdate(tileBorder, piece.Id, piece.Value));

                        scale.BeginAnimation(ScaleTransform.ScaleXProperty, s_popAnimation);
                        scale.BeginAnimation(ScaleTransform.ScaleYProperty, s_popAnimation);
                    }
                    else
                    {
                        if (!_displayedValues.TryGetValue(piece.Id, out int dispVal) || dispVal != piece.Value)
                        {
                            UpdateTileVisuals(tileBorder, piece.Value);
                            _displayedValues[piece.Id] = piece.Value;
                        }

                        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                        scale.ScaleX = 1.0;
                        scale.ScaleY = 1.0;
                    }
                }
            }
        }

        if (_pendingUpdates.Count > 0 || _dissolvingTiles.Count > 0)
        {
            _postSlideTimer.Stop();
            _postSlideTimer.Start();
        }
    }

    private static bool ContainsEngineId(IReadOnlyList<TilePiece> engineTiles, long id)
    {
        for (int i = 0; i < engineTiles.Count; i++)
        {
            if (engineTiles[i].Id == id) return true;
        }
        return false;
    }

    private static (ScaleTransform scale, TranslateTransform translate) GetTransforms(Border border)
    {
        if (border.Tag is TileVisualHolder holder)
        {
            return (holder.Scale, holder.Translate);
        }

        var group = (TransformGroup)border.RenderTransform;
        return ((ScaleTransform)group.Children[0], (TranslateTransform)group.Children[1]);
    }

    private static void UpdateTileVisuals(Border border, int value)
    {
        if (border.Tag is TileVisualHolder holder)
        {
            var tb = holder.TextBlock;
            tb.Text = GetTileString(value);
            var (bgBrush, fgBrush, fontSize) = GetTileStyle(value, border);
            border.Background = bgBrush;
            tb.Foreground = fgBrush;
            tb.FontSize = fontSize;
        }
    }

    private static string GetTileString(int value) => value switch
    {
        2 => "2",
        4 => "4",
        8 => "8",
        16 => "16",
        32 => "32",
        64 => "64",
        128 => "128",
        256 => "256",
        512 => "512",
        1024 => "1024",
        2048 => "2048",
        4096 => "4096",
        8192 => "8192",
        16384 => "16384",
        32768 => "32768",
        65536 => "65536",
        131072 => "131072",
        _ => value.ToString(CultureInfo.InvariantCulture)
    };

    #region Palette & Tile Styling

    private static readonly SolidColorBrush FgLight = FreezeBrush("#FFFFFFFF");
    private static readonly Dictionary<int, SolidColorBrush> s_glassBrushCache = new();
    private static readonly Lock s_brushLock = new();

    private static SolidColorBrush FreezeBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static (Brush Background, Brush Foreground, double FontSize) GetTileStyle(int value, FrameworkElement element)
    {
        double fontSize = value switch
        {
            < 100 => 32,
            < 1000 => 26,
            < 10000 => 20,
            _ => 16
        };

        if (value == 2048)
        {
            var accent = element.TryFindResource("SystemAccentColorPrimaryBrush") as Brush
                         ?? element.TryFindResource("SystemAccentColorSolidBrush") as Brush
                         ?? FreezeBrush("#B04CC2FF");
            var accentFg = element.TryFindResource("SystemAccentColorForegroundBrush") as Brush ?? FgLight;
            return (accent, accentFg, fontSize);
        }

        var bg = GetGlassBrush(value, element);
        return (bg, FgLight, fontSize);
    }

    private static SolidColorBrush GetGlassBrush(int value, FrameworkElement element)
    {
        lock (s_brushLock)
        {
            if (s_glassBrushCache.TryGetValue(value, out var cached))
            {
                return cached;
            }

            var brush = CreateGlassBrush(value, element);
            s_glassBrushCache[value] = brush;
            return brush;
        }
    }

    private static SolidColorBrush CreateGlassBrush(int value, FrameworkElement element)
    {
        return value switch
        {
            // 2: Cool neutral frosted glass plate matching SurfaceGlassBrush (#18FFFFFF)
            2 => FreezeTint(255, 255, 255, 0x18),

            // 4: Warm champagne/sand translucent glass plate with distinct golden warmth
            4 => FreezeTint(245, 210, 160, 0x44),

            // 8: GroupColorAmber (#FFD700) from Tokens.xaml
            8 => MakeTintedBrush(element, "GroupColorAmber", "#FFD700", fillAlpha: 0x58),

            // 16: GroupColorOrange (#FF8C00) from Tokens.xaml
            16 => MakeTintedBrush(element, "GroupColorOrange", "#FF8C00", fillAlpha: 0x64),

            // 32: GroupColorCrimson (#FF4343) from Tokens.xaml
            32 => MakeTintedBrush(element, "GroupColorCrimson", "#FF4343", fillAlpha: 0x70),

            // 64: GroupColorHotPink (#E3008C) from Tokens.xaml
            64 => MakeTintedBrush(element, "GroupColorHotPink", "#E3008C", fillAlpha: 0x78),

            // 128: GroupColorPurple (#881798) from Tokens.xaml
            128 => MakeTintedBrush(element, "GroupColorPurple", "#881798", fillAlpha: 0x80),

            // 256: GroupColorAzure (#0078D7) from Tokens.xaml
            256 => MakeTintedBrush(element, "GroupColorAzure", "#0078D7", fillAlpha: 0x88),

            // 512: GroupColorCyan (#00B7C3) from Tokens.xaml
            512 => MakeTintedBrush(element, "GroupColorCyan", "#00B7C3", fillAlpha: 0x90),

            // 1024: GroupColorEmerald (#107C10) from Tokens.xaml
            1024 => MakeTintedBrush(element, "GroupColorEmerald", "#107C10", fillAlpha: 0x98),

            // Super (>2048): GroupColorSkyBlue (#60CDFF) from Tokens.xaml
            _ => MakeTintedBrush(element, "GroupColorSkyBlue", "#60CDFF", fillAlpha: 0xA0)
        };
    }

    private static SolidColorBrush MakeTintedBrush(FrameworkElement element, string resourceKey, string fallbackHex, byte fillAlpha)
    {
        Color baseColor;
        if (element.TryFindResource(resourceKey) is Color c)
        {
            baseColor = c;
        }
        else if (Application.Current?.TryFindResource(resourceKey) is Color appC)
        {
            baseColor = appC;
        }
        else
        {
            baseColor = (Color)ColorConverter.ConvertFromString(fallbackHex);
        }

        return FreezeTint(baseColor.R, baseColor.G, baseColor.B, fillAlpha);
    }

    private static SolidColorBrush FreezeTint(byte r, byte g, byte b, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, r, g, b));
        brush.Freeze();
        return brush;
    }

    #endregion

    #region Input Handling

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (_viewModel == null) return;

        bool handled = true;
        switch (e.Key)
        {
            case Key.Left:
            case Key.A:
                _viewModel.Move(MoveDirection.Left);
                break;
            case Key.Right:
            case Key.D:
                _viewModel.Move(MoveDirection.Right);
                break;
            case Key.Up:
            case Key.W:
                _viewModel.Move(MoveDirection.Up);
                break;
            case Key.Down:
            case Key.S:
                _viewModel.Move(MoveDirection.Down);
                break;
            case Key.R:
                _viewModel.RestartGame();
                break;
            case Key.U:
            case Key.Z:
                _viewModel.UndoCommand.Execute(null);
                break;
            default:
                handled = false;
                break;
        }

        if (handled)
        {
            e.Handled = true;
        }
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);

        if (IsOverButton(e.OriginalSource))
        {
            return;
        }

        Focus();

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _gestureStartPoint = e.GetPosition(this);
            _isTrackingGesture = true;
            CaptureMouse();
        }
    }

    private static bool IsOverButton(object? source)
    {
        if (source is not DependencyObject dep) return false;
        DependencyObject? current = dep;
        while (current != null)
        {
            if (current is ButtonBase)
            {
                return true;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseUp(e);

        if (_isTrackingGesture && e.ChangedButton == MouseButton.Left)
        {
            _isTrackingGesture = false;
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }

            if (_viewModel != null)
            {
                var endPoint = e.GetPosition(this);
                double deltaX = endPoint.X - _gestureStartPoint.X;
                double deltaY = endPoint.Y - _gestureStartPoint.Y;

                const double minSwipeDistance = 30.0;
                if (Math.Abs(deltaX) > minSwipeDistance || Math.Abs(deltaY) > minSwipeDistance)
                {
                    if (Math.Abs(deltaX) > Math.Abs(deltaY))
                    {
                        _viewModel.Move(deltaX > 0 ? MoveDirection.Right : MoveDirection.Left);
                    }
                    else
                    {
                        _viewModel.Move(deltaY > 0 ? MoveDirection.Down : MoveDirection.Up);
                    }
                    e.Handled = true;
                }
            }
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _isTrackingGesture = false;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
    }

    #endregion
}
