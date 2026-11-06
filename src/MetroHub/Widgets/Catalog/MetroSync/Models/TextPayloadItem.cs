namespace MetroHub.Widgets.Catalog.MetroSync.Models;

public sealed partial class TextPayloadItem : MetroSyncMessageItem
{
    public string Text { get; set; } = string.Empty;
}
