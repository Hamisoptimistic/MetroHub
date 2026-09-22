using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace MetroHub.Widgets.Catalog.CaffeineSleep;

/// <summary>
/// Interaction logic for CaffeineSleepWidgetView.xaml
/// </summary>
public partial class CaffeineSleepWidgetView : UserControl
{
    public CaffeineSleepWidgetView()
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
