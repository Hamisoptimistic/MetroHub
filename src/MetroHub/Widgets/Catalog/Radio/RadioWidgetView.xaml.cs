using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace MetroHub.Widgets.Catalog.Radio;

/// <summary>
/// Interaction logic for the Focus Radio & Ambient Sounds widget view (Huge 8x6, 504x376px).
/// Implements seamless border-to-border 4-column station grid, top WidgetTiles category strip,
/// snappy micro-fade transitions on category change, and bottom Fluent audio player bar.
/// </summary>
public partial class RadioWidgetView : UserControl
{
    private static readonly QuadraticEase FadeEasing = new() { EasingMode = EasingMode.EaseOut };

    private string? _previousCategory;
    private RadioWidgetViewModel? _currentViewModel;

    public RadioWidgetView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HookViewModel(DataContext as RadioWidgetViewModel);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnhookViewModel();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UnhookViewModel();
        HookViewModel(e.NewValue as RadioWidgetViewModel);
    }

    private void HookViewModel(RadioWidgetViewModel? vm)
    {
        if (vm == null || ReferenceEquals(_currentViewModel, vm)) return;

        _currentViewModel = vm;
        _previousCategory = vm.SelectedCategoryId;
        _currentViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void UnhookViewModel()
    {
        if (_currentViewModel != null)
        {
            _currentViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _currentViewModel = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RadioWidgetViewModel.SelectedCategoryId))
        {
            if (_currentViewModel != null)
            {
                string? newCategory = _currentViewModel.SelectedCategoryId;
                if (!string.Equals(_previousCategory, newCategory, StringComparison.OrdinalIgnoreCase))
                {
                    AnimateCategoryFade();
                    _previousCategory = newCategory;
                }
            }
        }
    }

    private void AnimateCategoryFade()
    {
        if (StationItemsControl == null) return;

        // Reset scroll position to top on category change
        StationScrollViewer?.ScrollToVerticalOffset(0);

        // Ultra-snappy micro-fade (100ms): starts from 0.35 opacity so there is no black flash or flicker
        var fadeAnimation = new DoubleAnimation
        {
            From = 0.35,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(100),
            EasingFunction = FadeEasing
        };
        Timeline.SetDesiredFrameRate(fadeAnimation, 120);

        StationItemsControl.BeginAnimation(UIElement.OpacityProperty, fadeAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    private void OnTileContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Suppress right-click context menu entirely on the '+' custom station placeholder tile
        if (sender is FrameworkElement { DataContext: RadioStationItemViewModel { IsAddPlaceholder: true } })
        {
            e.Handled = true;
        }
    }
}
