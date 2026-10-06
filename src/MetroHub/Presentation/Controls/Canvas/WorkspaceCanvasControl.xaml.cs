using System.Windows.Controls;
using MetroHub.Core.Models;

namespace MetroHub.Presentation.Controls.Canvas;

/// <summary>
/// Dedicated container for a single workspace canvas. Hosts the tint backplates,
/// group headers, and tile collection items controls. Toggled via Visibility.Visible/Collapsed.
/// </summary>
public partial class WorkspaceCanvasControl : UserControl
{
    public WorkspaceModel? Workspace => DataContext as WorkspaceModel;

    public WorkspaceCanvasControl()
    {
        InitializeComponent();
    }
}
