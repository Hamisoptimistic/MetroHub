using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace MetroHub.Presentation.Themes;

/// <summary>
/// Microsoft Fluent 2 Motion System (Phase 3G).
/// Provides standardized animation durations and easing curves for both C# procedural animations
/// and XAML Storyboards. Supports an instant "Reduce Motion / Animations: Off" toggle for accessibility and performance.
/// </summary>
public static class MotionTokens
{
    private static bool _animationsEnabled = true;

    /// <summary>
    /// Gets or sets whether UI animations are globally enabled.
    /// When disabled, all durations collapse to 0ms for instant, zero-latency transitions.
    /// </summary>
    public static bool AnimationsEnabled
    {
        get => _animationsEnabled;
        set
        {
            _animationsEnabled = value;
            if (Application.Current != null)
            {
                Application.Current.Resources["FluentDurationFast"] = value ? new Duration(TimeSpan.FromMilliseconds(100)) : new Duration(TimeSpan.Zero);
                Application.Current.Resources["FluentDurationNormal"] = value ? new Duration(TimeSpan.FromMilliseconds(200)) : new Duration(TimeSpan.Zero);
                Application.Current.Resources["FluentDurationGentle"] = value ? new Duration(TimeSpan.FromMilliseconds(350)) : new Duration(TimeSpan.Zero);
            }
        }
    }

    /// <summary>
    /// Fast micro-interaction duration (100ms): button presses, reveal lighting, scale bounces.
    /// </summary>
    public static TimeSpan DurationFast => _animationsEnabled ? TimeSpan.FromMilliseconds(100) : TimeSpan.Zero;

    /// <summary>
    /// Standard transition duration (200ms): tile sizing, context menus, tooltips, list item highlights.
    /// </summary>
    public static TimeSpan DurationNormal => _animationsEnabled ? TimeSpan.FromMilliseconds(200) : TimeSpan.Zero;

    /// <summary>
    /// Gentle transition duration (350ms): window entrances/exits, layout morphing, large drawers.
    /// </summary>
    public static TimeSpan DurationGentle => _animationsEnabled ? TimeSpan.FromMilliseconds(350) : TimeSpan.Zero;

    /// <summary>
    /// Standard Fluent deceleration easing (Fast-out, slow-in) for entrances and arrivals.
    /// </summary>
    public static IEasingFunction Decelerate { get; } = new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>
    /// Standard Fluent acceleration easing (Slow-out, fast-in) for dismissals and exits.
    /// </summary>
    public static IEasingFunction Accelerate { get; } = new CubicEase { EasingMode = EasingMode.EaseIn };

    /// <summary>
    /// General-purpose smooth quadratic easing for continuous or bidirectional movements.
    /// </summary>
    public static IEasingFunction Standard { get; } = new QuadraticEase { EasingMode = EasingMode.EaseOut };

    /// <summary>
    /// Circular deceleration curve for menu popups and floating flyouts.
    /// </summary>
    public static IEasingFunction Circle { get; } = new CircleEase { EasingMode = EasingMode.EaseOut };
}
