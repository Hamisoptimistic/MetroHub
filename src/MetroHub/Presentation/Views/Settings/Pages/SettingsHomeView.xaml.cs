using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MetroHub.Presentation.Views.Settings.Pages;

/// <summary>
/// Interaction logic for Settings State 1 (Home Category Grid).
/// Implements floating category cards with canvas-style hover reveal fill.
/// </summary>
public partial class SettingsHomeView : UserControl
{
    private static readonly QuadraticEase RevealEasing = new() { EasingMode = EasingMode.EaseOut };

    public SettingsHomeView()
    {
        InitializeComponent();
    }

    private void OnCardMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is Button button && button.Template != null)
        {
            if (button.Template.FindName("PART_RevealFillBorder", button) is Border revealBorder)
            {
                UpdateRevealFillPosition(revealBorder, e.GetPosition(revealBorder));
            }
        }
    }

    private void OnCardMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Button button && button.Template != null)
        {
            if (button.Template.FindName("PART_RevealFillBorder", button) is Border revealBorder)
            {
                UpdateRevealFillPosition(revealBorder, e.GetPosition(revealBorder));

                var fadeIn = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(120))
                {
                    EasingFunction = RevealEasing
                };
                Timeline.SetDesiredFrameRate(fadeIn, 120);
                revealBorder.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            }
        }
    }

    private void OnCardMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Button button && button.Template != null)
        {
            if (button.Template.FindName("PART_RevealFillBorder", button) is Border revealBorder)
            {
                var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = RevealEasing
                };
                Timeline.SetDesiredFrameRate(fadeOut, 120);
                revealBorder.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }
        }
    }

    private static void UpdateRevealFillPosition(Border revealBorder, Point pos)
    {
        if (revealBorder.Background is RadialGradientBrush brush)
        {
            if (brush.IsFrozen)
            {
                brush = brush.Clone();
                revealBorder.Background = brush;
            }

            brush.Center = pos;
            brush.GradientOrigin = pos;
        }
    }
}
