using System.Windows.Controls;

namespace MetroHub.Widgets.Catalog.Jukebox;

/// <summary>
/// Code-behind for the YouTube Jukebox widget view. Logic-free by design;
/// all behavior lives in <see cref="JukeboxWidgetViewModel"/> via bindings.
/// </summary>
public partial class JukeboxWidgetView : UserControl
{
    public JukeboxWidgetView()
    {
        InitializeComponent();
    }
}
