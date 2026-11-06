using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MetroHub.Widgets.Catalog.MetroSync.Models;

public sealed partial class FilePayloadItem : MetroSyncMessageItem
{
    public string? FileId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public long? FileSize { get; set; }
    public string? MimeType { get; set; }
    public string? Caption { get; set; }

    private string? _localPath;
    public string? LocalPath
    {
        get => _localPath;
        set
        {
            if (SetProperty(ref _localPath, value))
            {
                OnPropertyChanged(nameof(IsDownloaded));
            }
        }
    }

    [ObservableProperty]
    [property: JsonIgnore]
    private double _downloadProgress;

    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isDownloading;

    [ObservableProperty]
    [property: JsonIgnore]
    private string? _errorMessage;

    [JsonIgnore]
    public bool IsDownloaded => !string.IsNullOrEmpty(LocalPath) && System.IO.File.Exists(LocalPath);
}
