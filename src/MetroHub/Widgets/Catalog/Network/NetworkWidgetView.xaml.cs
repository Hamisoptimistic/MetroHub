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
        this.Unloaded += OnUnloaded;
        if (TopTilesGrid != null)
        {
            TopTilesGrid.SizeChanged += OnTopTilesGridSizeChanged;
        }
        if (TimeframeHostGrid != null)
        {
            TimeframeHostGrid.SizeChanged += (s, e) => UpdateTimeframeSlidingIndicator(false);
        }
    }

    private void OnTopTilesGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateSlidingIndicator(false);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
        Dispatcher.InvokeAsync(() =>
        {
            UpdateSlidingIndicator(false);
            UpdateTimeframeSlidingIndicator(false);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
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
            Dispatcher.InvokeAsync(() =>
            {
                UpdateSlidingIndicator(false);
                UpdateTimeframeSlidingIndicator(false);
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "CurrentPanel" or "IsInternetDisconnected" or "IsEthernetConnected" or "IsWifiConnected" or "HasEthernetAdapter" or "HasWifiAdapter" or "IsLocalOnlyNoInternet" or "Health")
        {
            Dispatcher.InvokeAsync(() => UpdateSlidingIndicator(true));
        }
        if (e.PropertyName == nameof(NetworkWidgetViewModel.SelectedDataUsageTimeframe))
        {
            Dispatcher.InvokeAsync(() => UpdateTimeframeSlidingIndicator(true));
        }
    }

    private void UpdateSlidingIndicator(bool animate)
    {
        try
        {
            if (TopTilesGrid == null || SlidingActiveIndicator == null || SlidingIndicatorTransform == null || SlidingIndicatorShadow == null) return;
            
            if (DataContext is not NetworkWidgetViewModel vm) return;

            int columnIndex = -1;
            double widthMultiplier = 1.0;
            string colorHex = "#00E676"; // Green by default

            bool isLinked = vm.IsEthernetConnected || vm.IsWifiConnected;
            bool isDisconnected = vm.IsInternetDisconnected || !isLinked;
            bool isLocalOnly = vm.IsLocalOnlyNoInternet;

            if (vm.IsEthernetPanel || vm.IsWifiPanel) 
            { 
                if (isDisconnected)
                {
                    // NO CONNECTION / UNPLUGGED / DISCONNECTED:
                    // Check for WIFI and ETHERNET. If both are there, both indicators are RED.
                    // If WIFI adapter is not there, only Ethernet is RED.
                    // If Ethernet adapter is not there, only WIFI is RED.
                    colorHex = "#FF3B30"; // Red
                    if (vm.HasEthernetAdapter && vm.HasWifiAdapter)
                    {
                        columnIndex = 0;
                        widthMultiplier = 2.0; // Stretches across both Ethernet and WiFi
                    }
                    else if (vm.HasEthernetAdapter)
                    {
                        columnIndex = 0;
                        widthMultiplier = 1.0;
                    }
                    else if (vm.HasWifiAdapter)
                    {
                        columnIndex = 1;
                        widthMultiplier = 1.0;
                    }
                    else
                    {
                        columnIndex = 0;
                        widthMultiplier = 1.0;
                    }
                }
                else
                {
                    // Connected to router/switch
                    if (vm.IsEthernetPanel)
                    {
                        columnIndex = 0;
                        widthMultiplier = 1.0;
                        if (!vm.IsEthernetConnected)
                        {
                            colorHex = "#FF3B30"; // Red (cable unplugged)
                        }
                        else if (isLocalOnly)
                        {
                            colorHex = "#FFB703"; // Warning Amber/Yellow (connected to router, no internet)
                        }
                        else
                        {
                            colorHex = "#00E676"; // Green (verified internet access)
                        }
                    }
                    else // IsWifiPanel
                    {
                        columnIndex = 1;
                        widthMultiplier = 1.0;
                        if (!vm.IsWifiConnected)
                        {
                            colorHex = "#FF3B30"; // Red (Wi-Fi disconnected)
                        }
                        else if (isLocalOnly)
                        {
                            colorHex = "#FFB703"; // Warning Amber/Yellow (connected to router, no internet)
                        }
                        else
                        {
                            colorHex = "#00E676"; // Green (verified internet access)
                        }
                    }
                }
            }
            else if (vm.IsKillNetPanel) 
            { 
                columnIndex = 2; 
                widthMultiplier = 1.0;
                colorHex = "#FF3B30"; // Red for kill net
            }
            else if (vm.IsSpeedPanel) 
            { 
                columnIndex = 3; 
                widthMultiplier = 1.0;
                colorHex = isDisconnected ? "#FF3B30" : (isLocalOnly ? "#FFB703" : "#00E676"); 
            }
            else if (vm.IsHotspotPanel) 
            { 
                columnIndex = 4; 
                widthMultiplier = 1.0;
                colorHex = "#00E676"; 
            }

            if (columnIndex >= 0)
            {
                double columnWidth = TopTilesGrid.ActualWidth / 5.0;
                if (columnWidth <= 0 || double.IsNaN(columnWidth)) 
                {
                    // Re-attempt once layout finishes
                    Dispatcher.InvokeAsync(() => UpdateSlidingIndicator(false), System.Windows.Threading.DispatcherPriority.Loaded);
                    return;
                }

                double targetX = columnIndex * columnWidth;
                double targetWidth = columnWidth * widthMultiplier;
                var targetColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex);
                var targetBrush = new System.Windows.Media.SolidColorBrush(targetColor);

                if (animate)
                {
                    var xAnim = new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(300)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                    var wAnim = new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(300)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                    var opAnim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(200));
                    
                    // Color animation requires brushing
                    SlidingActiveIndicator.Background = targetBrush;
                    SlidingIndicatorShadow.Color = targetColor;

                    SlidingIndicatorTransform.BeginAnimation(TranslateTransform.XProperty, xAnim);
                    SlidingActiveIndicator.BeginAnimation(FrameworkElement.WidthProperty, wAnim);
                    SlidingActiveIndicator.BeginAnimation(UIElement.OpacityProperty, opAnim);
                }
                else
                {
                    SlidingIndicatorTransform.BeginAnimation(TranslateTransform.XProperty, null);
                    SlidingActiveIndicator.BeginAnimation(FrameworkElement.WidthProperty, null);
                    SlidingActiveIndicator.BeginAnimation(UIElement.OpacityProperty, null);
                    SlidingIndicatorTransform.X = targetX;
                    SlidingActiveIndicator.Opacity = 1.0;
                    SlidingActiveIndicator.Width = targetWidth;
                    SlidingActiveIndicator.Background = targetBrush;
                    SlidingIndicatorShadow.Color = targetColor;
                }
            }
            else
            {
                SlidingActiveIndicator.Opacity = 0;
            }
        }
        catch { }
    }

    private void UpdateTimeframeSlidingIndicator(bool animate)
    {
        try
        {
            if (TimeframeHostGrid == null || SlidingTimeframeIndicator == null || SlidingTimeframeTransform == null) return;
            if (DataContext is not NetworkWidgetViewModel vm) return;

            int index = 0;
            if (vm.IsSessionTimeframe) index = 0;
            else if (vm.Is24HoursTimeframe) index = 1;
            else if (vm.Is7DaysTimeframe) index = 2;
            else if (vm.Is30DaysTimeframe) index = 3;

            double totalWidth = TimeframeHostGrid.ActualWidth;
            if (totalWidth <= 0 || double.IsNaN(totalWidth))
            {
                totalWidth = 240.0;
            }

            double slotWidth = totalWidth / 4.0;
            double targetX = index * slotWidth;
            double targetWidth = slotWidth;

            if (animate)
            {
                var xAnim = new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(300))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                var wAnim = new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(300))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                var opAnim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(200));

                SlidingTimeframeTransform.BeginAnimation(TranslateTransform.XProperty, xAnim);
                SlidingTimeframeIndicator.BeginAnimation(FrameworkElement.WidthProperty, wAnim);
                SlidingTimeframeIndicator.BeginAnimation(UIElement.OpacityProperty, opAnim);
            }
            else
            {
                SlidingTimeframeTransform.BeginAnimation(TranslateTransform.XProperty, null);
                SlidingTimeframeIndicator.BeginAnimation(FrameworkElement.WidthProperty, null);
                SlidingTimeframeIndicator.BeginAnimation(UIElement.OpacityProperty, null);
                SlidingTimeframeTransform.X = targetX;
                SlidingTimeframeIndicator.Width = targetWidth;
                SlidingTimeframeIndicator.Opacity = 1.0;
            }
        }
        catch { }
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
