using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MetroHub.Widgets.Catalog.Dino;

/// <summary>
/// Interaction logic for DinoWidgetView.xaml.
/// Vector-rendered Canvas game view updating at monitor refresh rate via ViewModel.FrameTick.
/// Operates with zero per-frame visual element allocations and instant response.
/// </summary>
public partial class DinoWidgetView : UserControl
{
    private DinoWidgetViewModel? _viewModel;
    private readonly Path[] _obstaclePaths = new Path[4];

    private readonly SolidColorBrush _backgroundBrush = new(Color.FromRgb(0xF7, 0xF7, 0xF7));
    private readonly SolidColorBrush _spriteBrush = new(Color.FromRgb(0x53, 0x53, 0x53));
    private readonly SolidColorBrush _groundBrush = new(Color.FromRgb(0x73, 0x73, 0x73));
    private readonly SolidColorBrush _cloudBrush = new(Color.FromRgb(0xC4, 0xC4, 0xC4));

    public DinoWidgetView()
    {
        InitializeComponent();

        _obstaclePaths[0] = Obstacle0;
        _obstaclePaths[1] = Obstacle1;
        _obstaclePaths[2] = Obstacle2;
        _obstaclePaths[3] = Obstacle3;

        RootGrid.Background = _backgroundBrush;
        DinoShape.Fill = _spriteBrush;
        GroundBaseLine.Stroke = _spriteBrush;
        CurrentScoreText.Foreground = _spriteBrush;
        GameOverText.Foreground = _spriteBrush;
        RestartIconPath.Fill = _spriteBrush;

        for (int i = 0; i < _obstaclePaths.Length; i++)
        {
            _obstaclePaths[i].Fill = _spriteBrush;
        }

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.FrameTick -= OnFrameTick;
        }

        _viewModel = DataContext as DinoWidgetViewModel;

        if (_viewModel != null)
        {
            _viewModel.FrameTick += OnFrameTick;
            RenderFrame();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RenderFrame();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.FrameTick -= OnFrameTick;
        }
    }

    private void OnFrameTick()
    {
        RenderFrame();
    }

    private void RenderFrame()
    {
        if (_viewModel == null) return;
        var engine = _viewModel.Engine;

        // 1. Day / Night / High Contrast Palettes
        UpdatePalette(engine.NightBlend);

        // 2. Dino Sprite & Position
        UpdateDino(engine);

        // 3. Obstacles
        UpdateObstacles(engine);

        // 4. Ground & Clouds Scrolling
        UpdateScenery(engine);

        // 5. HUD (Scores)
        UpdateHud(engine);

        // 6. Overlays (Idle / GameOver / Paused)
        UpdateOverlays(engine);
    }

    private void UpdatePalette(double rawNightBlend)
    {
        if (_viewModel == null) return;

        if (_viewModel.IsHighContrast)
        {
            RootGrid.Background = SystemColors.WindowBrush;
            _spriteBrush.Color = SystemColors.WindowTextColor;
            _groundBrush.Color = SystemColors.WindowTextColor;
            CurrentScoreText.Foreground = SystemColors.WindowTextBrush;
            HighScoreText.Foreground = SystemColors.GrayTextBrush;
            GameOverText.Foreground = SystemColors.WindowTextBrush;
            RestartIconPath.Fill = SystemColors.WindowTextBrush;

            MoonShape.Opacity = 0.0;
            Star1.Opacity = 0.0;
            Star2.Opacity = 0.0;
            Star3.Opacity = 0.0;
            return;
        }

        // Reduced Motion check: snap instantly between day and night without 1.2s crossfade
        double blend = _viewModel.EffectiveReducedMotion
            ? (rawNightBlend >= 0.5 ? 1.0 : 0.0)
            : rawNightBlend;

        // Day: #F7F7F7 -> Night: #202124
        byte bgR = (byte)Lerp(0xF7, 0x20, blend);
        byte bgG = (byte)Lerp(0xF7, 0x21, blend);
        byte bgB = (byte)Lerp(0xF7, 0x24, blend);
        _backgroundBrush.Color = Color.FromRgb(bgR, bgG, bgB);

        // Day Sprite: #535353 -> Night Sprite: #E8EAED
        byte spR = (byte)Lerp(0x53, 0xE8, blend);
        byte spG = (byte)Lerp(0x53, 0xEA, blend);
        byte spB = (byte)Lerp(0x53, 0xED, blend);
        _spriteBrush.Color = Color.FromRgb(spR, spG, spB);

        // Day Secondary/Ground: #737373 -> Night Secondary: #9AA0A6
        byte gndR = (byte)Lerp(0x73, 0x9A, blend);
        byte gndG = (byte)Lerp(0x73, 0xA0, blend);
        byte gndB = (byte)Lerp(0x73, 0xA6, blend);
        _groundBrush.Color = Color.FromRgb(gndR, gndG, gndB);

        HighScoreText.Foreground = _groundBrush;
        CurrentScoreText.Foreground = _spriteBrush;

        // Celestial Elements Opacity
        MoonShape.Opacity = blend;
        Star1.Opacity = blend;
        Star2.Opacity = blend;
        Star3.Opacity = blend;
    }

    private void UpdateDino(DinoGameEngine engine)
    {
        double spriteHeight;

        if (engine.IsDucking)
        {
            spriteHeight = DinoTuning.DinoDuckingHeight;
            DinoShape.Data = engine.RunLegState
                ? DinoGeometries.DinoDuckingLeft
                : DinoGeometries.DinoDuckingRight;
        }
        else
        {
            spriteHeight = DinoTuning.DinoStandingHeight;

            if (engine.State is DinoGameState.Dying or DinoGameState.GameOver)
            {
                DinoShape.Data = DinoGeometries.DinoDead;
            }
            else if (!engine.IsGrounded)
            {
                DinoShape.Data = DinoGeometries.DinoStandingIdle;
            }
            else if (engine.State == DinoGameState.Running)
            {
                DinoShape.Data = engine.RunLegState
                    ? DinoGeometries.DinoRunLeft
                    : DinoGeometries.DinoRunRight;
            }
            else
            {
                DinoShape.Data = DinoGeometries.DinoStandingIdle;
            }
        }

        double top = engine.DinoY - spriteHeight;
        Canvas.SetTop(DinoShape, top);
        Canvas.SetLeft(DinoShape, DinoTuning.DinoX);
    }

    private void UpdateObstacles(DinoGameEngine engine)
    {
        var obstacles = engine.ActiveObstacles;

        for (int i = 0; i < _obstaclePaths.Length; i++)
        {
            if (i < obstacles.Count)
            {
                var obs = obstacles[i];
                var path = _obstaclePaths[i];

                path.Data = obs.Type switch
                {
                    ObstacleType.SmallCactus => DinoGeometries.SmallCactus,
                    ObstacleType.LargeCactus => DinoGeometries.LargeCactus,
                    ObstacleType.DoubleSmallCactus => DinoGeometries.DoubleSmallCactus,
                    ObstacleType.TripleSmallCactus => DinoGeometries.TripleSmallCactus,
                    ObstacleType.BirdLow or ObstacleType.BirdMid or ObstacleType.BirdHigh =>
                        engine.BirdFlapState ? DinoGeometries.BirdWingUp : DinoGeometries.BirdWingDown,
                    _ => DinoGeometries.SmallCactus
                };

                Canvas.SetLeft(path, obs.X);
                Canvas.SetTop(path, obs.Y);
                path.Visibility = Visibility.Visible;
            }
            else
            {
                _obstaclePaths[i].Visibility = Visibility.Collapsed;
            }
        }
    }

    private void UpdateScenery(DinoGameEngine engine)
    {
        double distance = engine.Score / DinoTuning.ScoreRate;

        // Ground Pebbles & Bumps Scrolling
        double groundOffset = distance % 504.0;
        Canvas.SetLeft(GroundTrack1, -groundOffset);
        Canvas.SetLeft(GroundTrack2, 504.0 - groundOffset);

        // Clouds Drifting (wrap-around)
        double cloud1X = (550.0 - (distance * 0.15) % 650.0);
        double cloud2X = (550.0 - (distance * 0.12 + 300.0) % 650.0);
        Canvas.SetLeft(Cloud1, cloud1X);
        Canvas.SetLeft(Cloud2, cloud2X);
    }

    private void UpdateHud(DinoGameEngine engine)
    {
        CurrentScoreText.Text = engine.DisplayScore.ToString("D5");

        if (engine.HighScore > 0)
        {
            HighScoreText.Text = $"HI {engine.HighScore:D5}";
            HighScoreText.Visibility = Visibility.Visible;
        }
        else
        {
            HighScoreText.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateOverlays(DinoGameEngine engine)
    {
        IdleOverlay.Visibility = (engine.State == DinoGameState.Idle)
            ? Visibility.Visible
            : Visibility.Collapsed;

        GameOverOverlay.Visibility = (engine.State == DinoGameState.GameOver)
            ? Visibility.Visible
            : Visibility.Collapsed;

        PausedOverlay.Visibility = (engine.State == DinoGameState.Paused)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static double Lerp(double a, double b, double t)
    {
        return a + (b - a) * Math.Clamp(t, 0.0, 1.0);
    }

    // --- Input & Focus Management ---

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        if (_viewModel != null)
        {
            if (_viewModel.Engine.State == DinoGameState.Paused)
            {
                _viewModel.TogglePause();
            }
            else if (_viewModel.Engine.State == DinoGameState.GameOver)
            {
                _viewModel.RestartGame();
            }
            else
            {
                _viewModel.HandleJumpInput();
            }
            e.Handled = true;
        }
    }

    private void OnGameOverClicked(object sender, MouseButtonEventArgs e)
    {
        Focus();
        if (_viewModel != null)
        {
            _viewModel.RestartGame();
        }
        e.Handled = true;
    }

    private void OnRestartButtonClicked(object sender, MouseButtonEventArgs e)
    {
        Focus();
        if (_viewModel != null)
        {
            _viewModel.RestartGame();
        }
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (_viewModel == null) return;

        if (e.Key is Key.Space or Key.Up)
        {
            if (_viewModel.Engine.State == DinoGameState.Paused)
            {
                _viewModel.TogglePause();
            }
            else
            {
                _viewModel.HandleJumpInput();
            }
            e.Handled = true;
        }
        else if (e.Key is Key.Down)
        {
            _viewModel.SetDownInput(true);
            e.Handled = true;
        }
        else if (e.Key is Key.Escape)
        {
            _viewModel.TogglePause();
            e.Handled = true;
        }
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);

        if (_viewModel == null) return;

        if (e.Key is Key.Down)
        {
            _viewModel.SetDownInput(false);
            e.Handled = true;
        }
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _viewModel?.SetDownInput(false);
    }
}
