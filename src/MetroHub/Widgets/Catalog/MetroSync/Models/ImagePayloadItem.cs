namespace MetroHub.Widgets.Catalog.MetroSync.Models;

public sealed partial class ImagePayloadItem : MetroSyncMessageItem
{
    public string? ThumbnailFilePath { get; set; }
    public string? FileId { get; set; }
    public string? Caption { get; set; }
}
