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
/// High-performance vector canvas game view with smooth cubic sliding and merge/spawn animations.
/// Adheres strictly to MetroHub UI and lifecycle standards.
/// </summary>
public partial class Game2048WidgetView : UserControl
{
    private const double BoardPadding = 14.0;
    private const double TileStep = 96.0; // 84px tile + 12px gap
    private const double TileSize = 84.0;

    private static readonly CubicEase SlideEasing = new() { EasingMode = EasingMode.EaseOut };
    private static readonly BackEase SpawnEasing = new() { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 };
    private static readonly CubicEase PopEasing = new() { EasingMode = EasingMode.EaseOut };

    private Game2048WidgetViewModel? _viewModel;
    private readonly Dictionary<long, Border> _tileViews = new(16);
    private Point _gestureStartPoint;
    private bool _isTrackingGesture;

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

        var tiles = _viewModel.Engine.Tiles;
        var activeIds = new HashSet<long>(tiles.Count);

        foreach (var piece in tiles)
        {
            activeIds.Add(piece.Id);

            double targetLeft = BoardPadding + piece.Col * TileStep;
            double targetTop = BoardPadding + piece.Row * TileStep;

            if (!_tileViews.TryGetValue(piece.Id, out var tileBorder))
            {
                // New Tile
                tileBorder = CreateTileElement(piece);
                _tileViews[piece.Id] = tileBorder;
                TilesCanvas.Children.Add(tileBorder);

                Canvas.SetLeft(tileBorder, targetLeft);
                Canvas.SetTop(tileBorder, targetTop);

                if (animate)
                {
                    AnimateSpawn(tileBorder);
                }
            }
            else
            {
                // Existing Tile
                UpdateTileVisuals(tileBorder, piece.Value);

                double currentLeft = Canvas.GetLeft(tileBorder);
                double currentTop = Canvas.GetTop(tileBorder);

                if (animate && (Math.Abs(currentLeft - targetLeft) > 0.5 || Math.Abs(currentTop - targetTop) > 0.5))
                {
                    AnimateSlide(tileBorder, currentLeft, targetLeft, currentTop, targetTop);
                }
                else
                {
                    Canvas.SetLeft(tileBorder, targetLeft);
                    Canvas.SetTop(tileBorder, targetTop);
                }

                if (piece.MergedInto != null)
                {
                    // Merged away: remove from canvas after slide
                    if (animate)
                    {
                        var borderToRemove = tileBorder;
                        long idToRemove = piece.Id;
                        var timer = new System.Windows.Threading.DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(110)
                        };
                        timer.Tick += (s, _) =>
                        {
                            timer.Stop();
                            TilesCanvas.Children.Remove(borderToRemove);
                            _tileViews.Remove(idToRemove);
                        };
                        timer.Start();
                    }
                    else
                    {
                        TilesCanvas.Children.Remove(tileBorder);
                        _tileViews.Remove(piece.Id);
                    }
                }
                else if (piece.IsMerged && animate)
                {
                    AnimateMergePop(tileBorder);
                }
            }
        }

        // Clean up visual tiles that are no longer in the engine list
        var toRemove = new List<long>();
        foreach (var kvp in _tileViews)
        {
            if (!activeIds.Contains(kvp.Key))
            {
                toRemove.Add(kvp.Key);
            }
        }

        foreach (var id in toRemove)
        {
            if (_tileViews.TryGetValue(id, out var b))
            {
                TilesCanvas.Children.Remove(b);
                _tileViews.Remove(id);
            }
        }
    }

    private static Border CreateTileElement(TilePiece piece)
    {
        var textBlock = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true
        };

        var scaleTransform = new ScaleTransform(1.0, 1.0, TileSize / 2, TileSize / 2);

        var border = new Border
        {
            Width = TileSize,
            Height = TileSize,
            CornerRadius = new CornerRadius(4),
            Child = textBlock,
            RenderTransform = scaleTransform,
            SnapsToDevicePixels = true
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

    private static void AnimateSlide(Border border, double fromX, double toX, double fromY, double toY)
    {
        var animX = new DoubleAnimation(fromX, toX, TimeSpan.FromMilliseconds(100))
        {
            EasingFunction = SlideEasing
        };
        var animY = new DoubleAnimation(fromY, toY, TimeSpan.FromMilliseconds(100))
        {
            EasingFunction = SlideEasing
        };

        border.BeginAnimation(Canvas.LeftProperty, animX, HandoffBehavior.SnapshotAndReplace);
        border.BeginAnimation(Canvas.TopProperty, animY, HandoffBehavior.SnapshotAndReplace);
    }

    private static void AnimateSpawn(Border border)
    {
        if (border.RenderTransform is ScaleTransform st)
        {
            var anim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = SpawnEasing
            };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
    }

    private static void AnimateMergePop(Border border)
    {
        if (border.RenderTransform is ScaleTransform st)
        {
            var keyFrames = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(140)
            };
            keyFrames.KeyFrames.Add(new SplineDoubleKeyFrame(1.18, KeyTime.FromPercent(0.4), new KeySpline(0.2, 0.8, 0.4, 1.0)));
            keyFrames.KeyFrames.Add(new SplineDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), new KeySpline(0.2, 0.8, 0.4, 1.0)));

            st.BeginAnimation(ScaleTransform.ScaleXProperty, keyFrames);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, keyFrames);
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
            // Accent color for hitting 2048
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
