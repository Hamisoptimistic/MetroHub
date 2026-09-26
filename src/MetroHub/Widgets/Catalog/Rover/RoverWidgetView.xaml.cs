using System;
using System.Windows;
using System.Windows.Controls;

namespace MetroHub.Widgets.Catalog.Rover;

/// <summary>
/// Interaction logic for RoverWidgetView.xaml.
/// Implements high-performance zero-GC frame slicing by updating ImageBrush.Viewbox
/// on every tick of Rover's 100 ms DispatcherTimer.
/// </summary>
public partial class RoverWidgetView : UserControl
{
    private RoverWidgetViewModel? _currentViewModel;

    public RoverWidgetView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is RoverWidgetViewModel vm)
        {
            AttachViewModel(vm);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachViewModel();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachViewModel();
        if (e.NewValue is RoverWidgetViewModel vm)
        {
            AttachViewModel(vm);
        }
    }

    private void AttachViewModel(RoverWidgetViewModel vm)
    {
        if (ReferenceEquals(_currentViewModel, vm)) return;

        DetachViewModel();
        _currentViewModel = vm;
        _currentViewModel.Engine.FrameChanged += OnEngineFrameChanged;
        _currentViewModel.RefreshLayoutSize();

        // Render initial static pose
        var (x, y) = GetInitialFrameCoords(vm.Engine);
        UpdateAllSpriteBrushes(x, y);
    }

    private void DetachViewModel()
    {
        if (_currentViewModel != null)
        {
            _currentViewModel.Engine.FrameChanged -= OnEngineFrameChanged;
            _currentViewModel = null;
        }
    }

    private void OnEngineFrameChanged(int x, int y)
    {
        UpdateAllSpriteBrushes(x, y);
    }

    private void UpdateAllSpriteBrushes(int x, int y)
    {
        // Stack-allocated Rect: Exactly 0 bytes allocated on the managed heap!
        var rect = RoverSpriteAtlas.GetFrameRect(x, y);

        if (SpriteBrushMedium != null) SpriteBrushMedium.Viewbox = rect;
        if (SpriteBrushWide != null) SpriteBrushWide.Viewbox = rect;
        if (SpriteBrushLarge != null) SpriteBrushLarge.Viewbox = rect;
        if (SpriteBrushBanner != null) SpriteBrushBanner.Viewbox = rect;
    }

    private static (int X, int Y) GetInitialFrameCoords(RoverAnimationEngine engine)
    {
        var anim = RoverManifest.GetAnimation(engine.CurrentAnimationName);
        if (anim is { Frames.Length: > 0 })
        {
            int idx = Math.Clamp(engine.CurrentFrameIndex, 0, anim.Frames.Length - 1);
            return (anim.Frames[idx].X, anim.Frames[idx].Y);
        }
        return (0, 0);
    }
}
