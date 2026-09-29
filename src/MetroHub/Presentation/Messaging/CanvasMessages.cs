using System;
using System.Collections.Generic;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using MetroHub.Core.Models;
using MetroHub.Presentation.Controls;
using MetroHub.Widgets.Catalog.Weather;

namespace MetroHub.Presentation.Messaging;

// ─────────────────────────────────────────────────────────────
// Query Messages (Synchronous Request-Response via WeakReferenceMessenger)
// ─────────────────────────────────────────────────────────────

public class QuerySelectedTilesMessage : RequestMessage<IReadOnlyList<TileModel>> { }

public class QueryGroupsMessage : RequestMessage<IReadOnlyList<TileGroupModel>> { }

public class QuerySidebarRailMessage : RequestMessage<SidebarRailControl?> { }

// ─────────────────────────────────────────────────────────────
// Tile Command Messages
// ─────────────────────────────────────────────────────────────

public record TileClearSelectionMessage();

public record TileBatchResizeMessage(int SpanX, int SpanY, TileModel SourceTile);

public record TileBatchStyleMessage(string Style, TileModel SourceTile);

public record TileCreateGroupMessage(TileModel SourceTile);

public record TileAddToGroupMessage(IReadOnlyList<TileModel> Targets, TileGroupModel TargetGroup);

public record TileBatchUnpinMessage(TileModel SourceTile);

public record TileToggleSidebarPinMessage(TileModel Tile);

public record TileShowWeatherLocationDialogMessage(WeatherWidgetViewModel WeatherVm);

// ─────────────────────────────────────────────────────────────
// Group Command Messages
// ─────────────────────────────────────────────────────────────

public record GroupFlashLockedMessage(TileGroupModel Group);

public class GroupRenameMessage : RequestMessage<bool>
{
    public TileGroupModel Group { get; }
    public string NewTitle { get; }

    public GroupRenameMessage(TileGroupModel group, string newTitle)
    {
        Group = group;
        NewTitle = newTitle;
    }
}

public record GroupStartDragMessage(TileGroupModel Group, MouseEventArgs MouseArgs);

public class GroupToggleLockMessage : RequestMessage<bool>
{
    public TileGroupModel Group { get; }

    public GroupToggleLockMessage(TileGroupModel group)
    {
        Group = group;
    }
}

public record GroupSetColorMessage(TileGroupModel Group, string? HexColor);

public record GroupSetTintColorMessage(TileGroupModel Group, string? HexTint);

public record GroupUngroupMessage(TileGroupModel Group);

public record GroupDeleteMessage(TileGroupModel Group);

// ─────────────────────────────────────────────────────────────
// Static Canvas Messenger Convenience Facade
// ─────────────────────────────────────────────────────────────

public static class CanvasMessenger
{
    public static IReadOnlyList<TileModel> GetSelectedTiles()
    {
        var msg = WeakReferenceMessenger.Default.Send(new QuerySelectedTilesMessage());
        return msg.HasReceivedResponse ? msg.Response : Array.Empty<TileModel>();
    }

    public static IReadOnlyList<TileGroupModel> GetGroups()
    {
        var msg = WeakReferenceMessenger.Default.Send(new QueryGroupsMessage());
        return msg.HasReceivedResponse ? msg.Response : Array.Empty<TileGroupModel>();
    }

    public static SidebarRailControl? GetSidebarRail()
    {
        var msg = WeakReferenceMessenger.Default.Send(new QuerySidebarRailMessage());
        return msg.HasReceivedResponse ? msg.Response : null;
    }
}
