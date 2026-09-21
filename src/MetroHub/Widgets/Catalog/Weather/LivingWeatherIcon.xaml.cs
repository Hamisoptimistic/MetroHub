using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MetroHub.Widgets.Catalog.Weather;

public partial class LivingWeatherIcon : UserControl
{
    public static readonly DependencyProperty IconSourceProperty =
        DependencyProperty.Register(
            nameof(IconSource),
            typeof(ImageSource),
            typeof(LivingWeatherIcon),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ConditionSlugProperty =
        DependencyProperty.Register(
            nameof(ConditionSlug),
            typeof(string),
            typeof(LivingWeatherIcon),
            new PropertyMetadata("partly-cloudy-day", OnConditionSlugChanged));

    public static readonly DependencyProperty IsLiveAnimatingProperty =
        DependencyProperty.Register(
            nameof(IsLiveAnimating),
            typeof(bool),
            typeof(LivingWeatherIcon),
            new PropertyMetadata(true, OnIsLiveAnimatingChanged));

    public ImageSource? IconSource
    {
        get => (ImageSource?)GetValue(IconSourceProperty);
        set => SetValue(IconSourceProperty, value);
    }

    public string ConditionSlug
    {
        get => (string)GetValue(ConditionSlugProperty);
        set => SetValue(ConditionSlugProperty, value);
    }

    public bool IsLiveAnimating
    {
        get => (bool)GetValue(IsLiveAnimatingProperty);
        set => SetValue(IsLiveAnimatingProperty, value);
    }

    private Storyboard? _activeStoryboard;
    private string? _currentActiveStoryboardKey;

    public LivingWeatherIcon()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => UpdateLivingCondition();
    private void OnUnloaded(object sender, RoutedEventArgs e) => StopAllAnimations();
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateLivingCondition();

    private static void OnConditionSlugChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LivingWeatherIcon control)
        {
            control.UpdateLivingCondition();
        }
    }

    private static void OnIsLiveAnimatingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LivingWeatherIcon control)
        {
            control.UpdateLivingCondition();
        }
    }

    private void UpdateLivingCondition()
    {
        if (!IsLoaded || !IsVisible || !IsLiveAnimating)
        {
            StopAllAnimations();
            return;
        }

        string slug = (ConditionSlug ?? string.Empty).ToLowerInvariant().Trim();

        // 1. Hide all condition layers
        SunRaysContainer.Visibility = Visibility.Collapsed;
        ClearNightContainer.Visibility = Visibility.Collapsed;
        RainContainer.Visibility = Visibility.Collapsed;
        StormContainer.Visibility = Visibility.Collapsed;
        StormFlash.Visibility = Visibility.Collapsed;
        SnowContainer.Visibility = Visibility.Collapsed;
        WindContainer.Visibility = Visibility.Collapsed;
        FogContainer.Visibility = Visibility.Collapsed;

        string? storyboardKey = null;

        if (slug.Contains("thunder") || slug.Contains("storm") || slug.Contains("lightning"))
        {
            StormContainer.Visibility = Visibility.Visible;
            StormFlash.Visibility = Visibility.Visible;
            RainContainer.Visibility = Visibility.Visible;
            storyboardKey = "StormStoryboard";
        }
        else if (slug.Contains("rain") || slug.Contains("drizzle") || slug.Contains("shower"))
        {
            RainContainer.Visibility = Visibility.Visible;
            storyboardKey = "RainStoryboard";
        }
        else if (slug.Contains("snow") || slug.Contains("sleet") || slug.Contains("flurries") || slug.Contains("ice") || slug.Contains("hail"))
        {
            SnowContainer.Visibility = Visibility.Visible;
            storyboardKey = "SnowStoryboard";
        }
        else if (slug.Contains("wind") || slug.Contains("breeze") || slug.Contains("gale"))
        {
            WindContainer.Visibility = Visibility.Visible;
            storyboardKey = "WindStoryboard";
        }
        else if (slug.Contains("fog") || slug.Contains("mist") || slug.Contains("haze") || slug is "overcast")
        {
            FogContainer.Visibility = Visibility.Visible;
            storyboardKey = "FogStoryboard";
        }
        else if (slug.Contains("night"))
        {
            ClearNightContainer.Visibility = Visibility.Visible;
            storyboardKey = "ClearNightStoryboard";
        }
        else if (slug.Contains("day") || slug.Contains("clear") || slug.Contains("sun"))
        {
            SunRaysContainer.Visibility = Visibility.Visible;
            storyboardKey = "SunnyRaysStoryboard";
        }
        else
        {
            storyboardKey = "CloudFloatingStoryboard";
        }

        // 2. Manage active storyboard
        if (storyboardKey != _currentActiveStoryboardKey)
        {
            StopAllAnimations();

            if (storyboardKey != null)
            {
                _activeStoryboard = TryFindResource(storyboardKey) as Storyboard;
                if (_activeStoryboard != null)
                {
                    _activeStoryboard.Begin(this, isControllable: true);
                    _currentActiveStoryboardKey = storyboardKey;
                }
            }
        }
        else if (_activeStoryboard != null && _currentActiveStoryboardKey != null)
        {
            // Resume if previously paused
            _activeStoryboard.Resume(this);
        }
    }

    private void StopAllAnimations()
    {
        if (_activeStoryboard != null)
        {
            _activeStoryboard.Stop(this);
            _activeStoryboard = null;
            _currentActiveStoryboardKey = null;
        }

        // Reset positions/opacities to clean resting baseline
        StormFlash.Opacity = 0.0;
        StormBolt.Opacity = 0.0;
        WindCurve1.Opacity = 0.0;
        WindCurve2.Opacity = 0.0;
        CloudFloatTrans.Y = 0.0;
        SunRaysTransform.Angle = 0.0;
    }
}
