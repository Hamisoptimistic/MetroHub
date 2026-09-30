namespace MetroHub.Widgets;

/// <summary>
/// Optional contract for widget ViewModels that handle primary interactions (single click / launch) on their host tile.
/// Eliminates hardcoded widget type-checks in TileControl.LaunchTile().
/// </summary>
public interface IWidgetActionHandler
{
    void OnPrimaryAction();
}
