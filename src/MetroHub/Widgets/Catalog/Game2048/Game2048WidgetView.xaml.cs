using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MetroHub.Widgets.Catalog.Game2048;

/// <summary>
/// Interaction logic for Game2048WidgetView.xaml.
/// GPU-accelerated RenderTransform canvas game view with smooth cubic sliding and merge/spawn animations.
/// Adheres strictly to MetroHub UI, performance, and lifecycle standards.
/// </summary>
public partial class Game2048WidgetView : UserControl
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

    private Game2048WidgetViewModel? _viewModel;
    private readonly Dictionary<long, Border> _tileViews = new(16);
    private readonly Dictionary<long, int> _displayedValues = new(16);
    private Point _gestureStartPoint;
    private bool _isTrackingGesture;

    static Game2048WidgetView()
    {
        SlideEasing.Freeze();
        SpawnEasing.Freeze();
        PopSpline.Freeze();
    }

    public Game2048WidgetView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.BoardChanged -= OnBoardChanged;
        }

        _viewModel = DataContext as Game2048WidgetViewModel;

        if (_viewModel != null)
        {
            _viewModel.BoardChanged += OnBoardChanged;
            RenderBoard(animate: false);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.BoardChanged -= OnBoardChanged;
            _viewModel.BoardChanged += OnBoardChanged;
            RenderBoard(animate: false);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.BoardChanged -= OnBoardChanged;
        }
    }

    private void OnBoardChanged()
    {
        RenderBoard(animate: SystemParameters.ClientAreaAnimation);
    }

    private void RenderBoard(bool animate)
    {
        if (_viewModel == null) return;

        var engineTiles = _viewModel.Engine.Tiles;
        var currentEngineIds = new HashSet<long>(engineTiles.Count);
        foreach (var t in engineTiles)
        {
            currentEngineIds.Add(t.Id);
        }

        // 1. Pre-clean orphaned visual elements (consumed merged tiles from previous moves or rapid key presses)
        var orphanedIds = new List<long>();
        foreach (var id in _tileViews.Keys)
        {
            if (!currentEngineIds.Contains(id))
            {
                orphanedIds.Add(id);
            }
        }

        foreach (var id in orphanedIds)
        {
            if (_tileViews.Remove(id, out var orphanedBorder))
            {
                TilesCanvas.Children.Remove(orphanedBorder);
            }
            _displayedValues.Remove(id);
        }

        // 2. Rapid input safety: ensure visual state is up-to-date for existing non-merging tiles
        foreach (var piece in engineTiles)
        {
            if (!piece.IsMerged && _tileViews.TryGetValue(piece.Id, out var existingBorder))
            {
                if (!_displayedValues.TryGetValue(piece.Id, out int dispVal) || dispVal != piece.Value)
                {
                    UpdateTileVisuals(existingBorder, piece.Value);
                    _displayedValues[piece.Id] = piece.Value;
                }
            }
        }

        // 3. Process each tile in engineTiles
        foreach (var piece in engineTiles)
        {
            double targetX = BoardPadding + piece.Col * TileStep;
            double targetY = BoardPadding + piece.Row * TileStep;

            if (!_tileViews.TryGetValue(piece.Id, out var tileBorder))
            {
                // Case A: New Tile
                tileBorder = CreateTileElement(piece, targetX, targetY);
                _tileViews[piece.Id] = tileBorder;
                _displayedValues[piece.Id] = piece.Value;
                TilesCanvas.Children.Add(tileBorder);
                Panel.SetZIndex(tileBorder, 1);

                var (scale, _) = GetTransforms(tileBorder);
                if (animate && piece.IsNew)
                {
                    scale.ScaleX = 0.0;
                    scale.ScaleY = 0.0;

                    var spawnAnim = new DoubleAnimationUsingKeyFrames();
                    spawnAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                    spawnAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(SpawnDelay)));
                    spawnAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(SpawnDelay + SpawnDuration), SpawnEasing));

                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, spawnAnim);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, spawnAnim);
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
                    // Case B: Consumed Merging Tile (slides under destination with soft dissolve, then removed)
                    Panel.SetZIndex(tileBorder, 2);

                    if (animate)
                    {
                        double startX = translate.X;
                        double startY = translate.Y;

                        var animX = new DoubleAnimation(startX, targetX, SlideDuration) { EasingFunction = SlideEasing };
                        var animY = new DoubleAnimation(startY, targetY, SlideDuration) { EasingFunction = SlideEasing };

                        // Soft Dissolve: fade out smoothly during the final 30ms of the slide (70ms to 100ms)
                        var fadeAnim = new DoubleAnimationUsingKeyFrames();
                        fadeAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                        fadeAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70))));
                        fadeAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(SlideDuration), SlideEasing));

                        long capturedId = piece.Id;
                        Border capturedBorder = tileBorder;

                        animX.Completed += (s, e) =>
                        {
                            if (_tileViews.TryGetValue(capturedId, out var current) && ReferenceEquals(current, capturedBorder))
                            {
                                _tileViews.Remove(capturedId);
                                _displayedValues.Remove(capturedId);
                                TilesCanvas.Children.Remove(capturedBorder);
                            }
                        };

                        tileBorder.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
                        translate.BeginAnimation(TranslateTransform.XProperty, animX, HandoffBehavior.SnapshotAndReplace);
                        translate.BeginAnimation(TranslateTransform.YProperty, animY, HandoffBehavior.SnapshotAndReplace);
                    }
                    else
                    {
                        _tileViews.Remove(piece.Id);
                        _displayedValues.Remove(piece.Id);
                        TilesCanvas.Children.Remove(tileBorder);
                    }
                }
                else
                {
                    // Case C: Surviving / Destination Tile
                    Panel.SetZIndex(tileBorder, piece.IsMerged ? 3 : 2);
                    tileBorder.BeginAnimation(UIElement.OpacityProperty, null);
                    tileBorder.Opacity = 1.0;

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
                        // Tuned Tactile Pop: 1.0 -> 1.08 -> 1.0 over 130ms (fits inside 12px grid gap without clipping)
                        long capturedPieceId = piece.Id;
                        Border capturedDestBorder = tileBorder;
                        int targetValue = piece.Value;

                        var popAnim = new DoubleAnimationUsingKeyFrames
                        {
                            BeginTime = PopDelay
                        };
                        popAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                        popAnim.KeyFrames.Add(new SplineDoubleKeyFrame(PopPeakScale, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(50)), PopSpline));
                        popAnim.KeyFrames.Add(new SplineDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(PopDuration), PopSpline));

                        var delayTrigger = new DoubleAnimation(1.0, 1.0, PopDelay);
                        delayTrigger.Completed += (s, e) =>
                        {
                            if (_tileViews.TryGetValue(capturedPieceId, out var cur) && ReferenceEquals(cur, capturedDestBorder))
                            {
                                UpdateTileVisuals(capturedDestBorder, targetValue);
                                _displayedValues[capturedPieceId] = targetValue;
                                capturedDestBorder.BeginAnimation(UIElement.OpacityProperty, null);
                                capturedDestBorder.Opacity = 1.0;
                            }
                        };

                        capturedDestBorder.BeginAnimation(UIElement.OpacityProperty, delayTrigger);
                        scale.BeginAnimation(ScaleTransform.ScaleXProperty, popAnim);
                        scale.BeginAnimation(ScaleTransform.ScaleYProperty, popAnim);
                    }
                    else
                    {
                        UpdateTileVisuals(tileBorder, piece.Value);
                        _displayedValues[piece.Id] = piece.Value;
                        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                        scale.ScaleX = 1.0;
                        scale.ScaleY = 1.0;
                    }
                }
            }
        }
    }

    private static (ScaleTransform scale, TranslateTransform translate) GetTransforms(Border border)
    {
        var group = (TransformGroup)border.RenderTransform;
        return ((ScaleTransform)group.Children[0], (TranslateTransform)group.Children[1]);
    }

    private static Border CreateTileElement(TilePiece piece, double initialX, double initialY)
    {
        var textBlock = new TextBlock
        {
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        textBlock.SetResourceReference(TextBlock.FontFamilyProperty, "AppFontFamily");

        var scaleTransform = new ScaleTransform(1.0, 1.0);
        var translateTransform = new TranslateTransform(initialX, initialY);
        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(scaleTransform);     // Index 0
        transformGroup.Children.Add(translateTransform); // Index 1

        var border = new Border
        {
            Width = TileSize,
            Height = TileSize,
            BorderThickness = new Thickness(0),
            Child = textBlock,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = transformGroup,
            SnapsToDevicePixels = false,
            UseLayoutRounding = false
        };
        border.SetResourceReference(Border.CornerRadiusProperty, "TileCornerRadius");

        UpdateTileVisuals(border, piece.Value);
        return border;
    }

    private static void UpdateTileVisuals(Border border, int value)
    {
        if (border.Child is TextBlock tb)
        {
            tb.Text = value.ToString(CultureInfo.InvariantCulture);
            var (bgBrush, fgBrush, fontSize) = GetTileStyle(value, border);
            border.Background = bgBrush;
            tb.Foreground = fgBrush;
            tb.FontSize = fontSize;
        }
    }

    #region Palette & Tile Styling

    private static readonly SolidColorBrush FgLight = FreezeBrush("#FFFFFFFF");
    private static readonly Dictionary<int, SolidColorBrush> s_glassBrushCache = new();
    private static readonly object s_brushLock = new();

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
        Focus();

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _gestureStartPoint = e.GetPosition(this);
            _isTrackingGesture = true;
        }
    }

    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseUp(e);

        if (_isTrackingGesture && _viewModel != null)
        {
            _isTrackingGesture = false;
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

    #endregion
}
