using CommunityToolkit.Mvvm.ComponentModel;

namespace MetroHub.Widgets.Catalog.MetroSync.Models;

public sealed partial class FilePayloadItem : MetroSyncMessageItem
{
    public string? FileId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public long? FileSize { get; set; }
    public string? MimeType { get; set; }
    public string? Caption { get; set; }

    [ObservableProperty]
    private string? _localPath;
}
