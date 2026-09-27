using System.Windows;
using System.Windows.Controls;

namespace MetroHub.Widgets.Catalog.Radio;

/// <summary>
/// Interaction logic for the Focus Radio & Ambient Sounds widget view (Huge 8x6, 504x376px).
/// Implements seamless border-to-border 4-column station grid, top WidgetTiles category strip,
/// and bottom Fluent audio player bar.
/// </summary>
public partial class RadioWidgetView : UserControl
{
    public RadioWidgetView()
    {
        InitializeComponent();
    }

    private void OnTileContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Suppress right-click context menu entirely on the '+' custom station placeholder tile
        if (sender is FrameworkElement { DataContext: RadioStationItemViewModel { IsAddPlaceholder: true } })
        {
            e.Handled = true;
        }
    }
}
