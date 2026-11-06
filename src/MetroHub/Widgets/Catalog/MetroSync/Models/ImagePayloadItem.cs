using System.Text.Json.Serialization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MetroHub.Widgets.Catalog.MetroSync.Models;

public sealed partial class ImagePayloadItem : MetroSyncMessageItem
{
    public string? ThumbnailFilePath { get; set; }
    public string? FileId { get; set; }
    public string? Caption { get; set; }

    [ObservableProperty]
    [property: JsonIgnore]
    private ImageSource? _thumbnailImage;
}
