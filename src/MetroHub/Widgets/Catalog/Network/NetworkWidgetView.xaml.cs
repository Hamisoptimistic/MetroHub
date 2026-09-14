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


    private void WifiPasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (sender is PasswordBox pb && DataContext is NetworkWidgetViewModel vm)
            {
                vm.ConnectWithPasswordCommand.Execute(pb);
            }
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (sender is PasswordBox pb && DataContext is NetworkWidgetViewModel vm)
            {
                vm.CancelWifiPasswordCommand.Execute(pb);
            }
        }
    }
}
