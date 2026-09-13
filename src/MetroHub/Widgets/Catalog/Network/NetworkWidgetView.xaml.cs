using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MetroHub.Widgets.Catalog.Network;

public partial class NetworkWidgetView : UserControl
{
    public NetworkWidgetView()
    {
        InitializeComponent();
        this.DataContextChanged += OnDataContextChanged;
        this.Loaded += OnLoaded;
        if (TopTilesGrid != null)
        {
            TopTilesGrid.SizeChanged += OnTopTilesGridSizeChanged;
        }
    }

    private void OnTopTilesGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateSlidingIndicator(false);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateSlidingIndicator(false);
    }

    private System.ComponentModel.INotifyPropertyChanged? _viewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is System.ComponentModel.INotifyPropertyChanged vm)
        {
            _viewModel = vm;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            UpdateSlidingIndicator(false);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "CurrentPanel")
        {
            UpdateSlidingIndicator(true);
        }
    }

    private void UpdateSlidingIndicator(bool animate)
    {
        if (TopTilesGrid == null || SlidingActiveIndicator == null || SlidingIndicatorTransform == null || SlidingIndicatorShadow == null) return;
        
        if (DataContext is not NetworkWidgetViewModel vm) return;

        int columnIndex = -1;
        string colorHex = "#00E676"; // Green by default

        if (vm.IsEthernetPanel) { columnIndex = 0; }
        else if (vm.IsWifiPanel) { columnIndex = 1; }
        else if (vm.IsKillNetPanel) { columnIndex = 2; colorHex = "#FF3B30"; } // Red for kill net
        else if (vm.IsSpeedPanel) { columnIndex = 3; }
        else if (vm.IsHotspotPanel) { columnIndex = 4; }

        if (columnIndex >= 0)
        {
            double columnWidth = TopTilesGrid.ActualWidth / 5.0;
            if (columnWidth <= 0 || double.IsNaN(columnWidth)) 
            {
                // Fallback if layout hasn't run yet
                SlidingActiveIndicator.Opacity = 0;
                return;
            }

            double targetX = columnIndex * columnWidth;
            var targetColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex);
            var targetBrush = new System.Windows.Media.SolidColorBrush(targetColor);

            if (animate)
            {
                var xAnim = new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(300)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                var opAnim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(200));
                
                // Color animation requires brushing
                SlidingActiveIndicator.Background = targetBrush;
                SlidingIndicatorShadow.Color = targetColor;

                SlidingIndicatorTransform.BeginAnimation(TranslateTransform.XProperty, xAnim);
                SlidingActiveIndicator.BeginAnimation(UIElement.OpacityProperty, opAnim);
                SlidingActiveIndicator.Width = columnWidth;
            }
            else
            {
                SlidingIndicatorTransform.BeginAnimation(TranslateTransform.XProperty, null);
                SlidingActiveIndicator.BeginAnimation(UIElement.OpacityProperty, null);
                SlidingIndicatorTransform.X = targetX;
                SlidingActiveIndicator.Opacity = 1.0;
                SlidingActiveIndicator.Width = columnWidth;
                SlidingActiveIndicator.Background = targetBrush;
                SlidingIndicatorShadow.Color = targetColor;
            }
        }
        else
        {
            SlidingActiveIndicator.Opacity = 0;
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(this);
        if (BottomRevealBrush != null)
        {
            BottomRevealBrush.Center = pos;
            BottomRevealBrush.GradientOrigin = pos;
        }
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        var anim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(100));
        BottomRevealBorder?.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        var anim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(200));
        BottomRevealBorder?.BeginAnimation(UIElement.OpacityProperty, anim);
    }
}
