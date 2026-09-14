using System.Windows.Controls;
using System.Windows.Input;

namespace MetroHub.Widgets.Catalog.Network;

public partial class NetworkWidgetView : UserControl
{
    public NetworkWidgetView()
    {
        InitializeComponent();
    }

    private void EthernetToggleSwitch_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (DataContext is NetworkWidgetViewModel vm && vm.CanToggleEthernet)
        {
            vm.ToggleEthernetCommand.Execute(null);
        }
    }

    private void EthernetToggleSwitch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter)
        {
            e.Handled = true;
            if (DataContext is NetworkWidgetViewModel vm && vm.CanToggleEthernet)
            {
                vm.ToggleEthernetCommand.Execute(null);
            }
        }
    }
}
