using System.Collections.Generic;
using System.Windows.Controls;

namespace MetroHub.Widgets;

/// <summary>
/// Optional contract for widget ViewModels that provide custom context menu items when right-clicked on their tile.
/// Eliminates hardcoded menu builder logic in TileControl.xaml.cs.
/// </summary>
public interface IWidgetContextMenuProvider
{
    IEnumerable<Control> GetContextMenuItems();
}
