using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace MetroHub.Widgets.Catalog.Network;

public partial class NetworkWidgetView : UserControl
{
    public NetworkWidgetView()
    {
        InitializeComponent();
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
