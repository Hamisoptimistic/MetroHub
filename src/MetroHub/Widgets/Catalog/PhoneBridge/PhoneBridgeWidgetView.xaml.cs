using System.Windows.Controls;

namespace MetroHub.Widgets.Catalog.PhoneBridge;

/// <summary>
/// Interaction logic for PhoneBridgeWidgetView.xaml.
/// Purely declarative: every action routes through commands on the ViewModel.
/// </summary>
public partial class PhoneBridgeWidgetView : UserControl
{
    public PhoneBridgeWidgetView()
    {
        InitializeComponent();
    }
}
