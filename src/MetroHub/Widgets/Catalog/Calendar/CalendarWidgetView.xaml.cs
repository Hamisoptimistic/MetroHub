using System.Windows.Controls;

namespace MetroHub.Widgets.Catalog.Calendar;

/// <summary>
/// Interaction logic for CalendarWidgetView.xaml.
/// Renders two layouts from the shared ViewModel: an authentic Windows 10 taskbar style
/// 42-day month grid at 8x6 (504x376px), and a compact today's date card at 4x4 (248x248px).
/// </summary>
public partial class CalendarWidgetView : UserControl
{
    public CalendarWidgetView()
    {
        InitializeComponent();
    }
}
