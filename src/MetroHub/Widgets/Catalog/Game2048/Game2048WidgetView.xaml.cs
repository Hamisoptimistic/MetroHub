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
    private const double BoardPadding = 14.0;
    private const double TileStep = 96.0; // 84px tile + 12px gap
    private const double TileSize = 84.0;

    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(95);
    private static readonly TimeSpan PopDuration = TimeSpan.FromMilliseconds(95);
    private static readonly TimeSpan SpawnDelay = TimeSpan.FromMilliseconds(55);
    private static readonly TimeSpan SpawnDuration = TimeSpan.FromMilliseconds(110);

    private static readonly CubicEase SlideEasing = new() { EasingMode = EasingMode.EaseOut };
    private static readonly BackEase SpawnEasing = new() { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 };
    private static readonly CubicEase PopEasing = new() { EasingMode = EasingMode.EaseOut };

    private Game2048WidgetViewModel? _viewModel;
    private readonly Dictionary<long, Border> _tileViews = new(16);
    private readonly Dictionary<long, int> _displayedValues = new(16);
    private Point _gestureStartPoint;
    private bool _isTrackingGesture;

    static Game2048WidgetView()
    {
        SlideEasing.Freeze();
        SpawnEasing.Freeze();
        PopEasing.Freeze();
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

                        // Soft Dissolve: fade out smoothly during the final 40ms of the slide
                        var fadeAnim = new DoubleAnimationUsingKeyFrames();
                        fadeAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                        fadeAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(55))));
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
                        // Option 1: Subtle Micro-Pulse (peak 1.05 max) with Soft Dissolve synchronization
                        long capturedPieceId = piece.Id;
                        Border capturedDestBorder = tileBorder;
                        int targetValue = piece.Value;

                        var microPulseAnim = new DoubleAnimationUsingKeyFrames
                        {
                            BeginTime = TimeSpan.FromMilliseconds(70)
                        };
                        microPulseAnim.KeyFrames.Add(new SplineDoubleKeyFrame(1.05, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(35)), new KeySpline(0.2, 0.8, 0.4, 1.0)));
                        microPulseAnim.KeyFrames.Add(new SplineDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(PopDuration), new KeySpline(0.2, 0.8, 0.4, 1.0)));

                        var delayTrigger = new DoubleAnimation(1.0, 1.0, TimeSpan.FromMilliseconds(70));
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
                        scale.BeginAnimation(ScaleTransform.ScaleXProperty, microPulseAnim);
                        scale.BeginAnimation(ScaleTransform.ScaleYProperty, microPulseAnim);
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
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var scaleTransform = new ScaleTransform(1.0, 1.0);
        var translateTransform = new TranslateTransform(initialX, initialY);
        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(scaleTransform);     // Index 0
        transformGroup.Children.Add(translateTransform); // Index 1

        var border = new Border
        {
            Width = TileSize,
            Height = TileSize,
            CornerRadius = new CornerRadius(4),
            Child = textBlock,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = transformGroup
        };

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

    private static readonly SolidColorBrush Tile2Bg = FreezeBrush("#363640");
    private static readonly SolidColorBrush Tile4Bg = FreezeBrush("#444452");
    private static readonly SolidColorBrush Tile8Bg = FreezeBrush("#D97706");
    private static readonly SolidColorBrush Tile16Bg = FreezeBrush("#EA580C");
    private static readonly SolidColorBrush Tile32Bg = FreezeBrush("#DC2626");
    private static readonly SolidColorBrush Tile64Bg = FreezeBrush("#B91C1C");
    private static readonly SolidColorBrush Tile128Bg = FreezeBrush("#CA8A04");
    private static readonly SolidColorBrush Tile256Bg = FreezeBrush("#EAB308");
    private static readonly SolidColorBrush Tile512Bg = FreezeBrush("#0D9488");
    private static readonly SolidColorBrush Tile1024Bg = FreezeBrush("#059669");
    private static readonly SolidColorBrush TileSuperBg = FreezeBrush("#7C3AED");

    private static readonly SolidColorBrush FgLight = FreezeBrush("#FFFFFFFF");
    private static readonly SolidColorBrush FgOffWhite = FreezeBrush("#EAEAEA");
    private static readonly SolidColorBrush FgDark = FreezeBrush("#111116");

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
                         ?? TileSuperBg;
            var accentFg = element.TryFindResource("SystemAccentColorForegroundBrush") as Brush ?? FgLight;
            return (accent, accentFg, fontSize);
        }

        Brush bg = value switch
        {
            2 => Tile2Bg,
            4 => Tile4Bg,
            8 => Tile8Bg,
            16 => Tile16Bg,
            32 => Tile32Bg,
            64 => Tile64Bg,
            128 => Tile128Bg,
            256 => Tile256Bg,
            512 => Tile512Bg,
            1024 => Tile1024Bg,
            _ => TileSuperBg
        };

        Brush fg = value switch
        {
            2 => FgOffWhite,
            256 => FgDark,
            _ => FgLight
        };

        return (bg, fg, fontSize);
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
