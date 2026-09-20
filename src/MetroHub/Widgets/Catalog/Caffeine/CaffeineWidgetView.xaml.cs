using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace MetroHub.Widgets.Catalog.Caffeine;

/// <summary>
/// Interaction logic for CaffeineWidgetView.xaml
/// </summary>
public partial class CaffeineWidgetView : UserControl
{
    public CaffeineWidgetView()
    {
        InitializeComponent();
    }

    private void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
    }
}
