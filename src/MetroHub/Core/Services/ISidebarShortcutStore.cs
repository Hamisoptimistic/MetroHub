using System.Collections.Generic;
using MetroHub.Core.Models;

namespace MetroHub.Core.Services;

/// <summary>
/// Abstraction over sidebar shortcut storage so Core never touches a Presentation control.
/// Implemented by SidebarRailControl in the Presentation layer.
/// </summary>
public interface ISidebarShortcutStore
{
    IReadOnlyList<SidebarShortcutItem> Shortcuts { get; }

    void AddShortcutItem(SidebarShortcutItem item, int? insertIndex = null);

    bool RemoveShortcutItem(SidebarShortcutItem item);

    void SaveShortcutsState();
}
