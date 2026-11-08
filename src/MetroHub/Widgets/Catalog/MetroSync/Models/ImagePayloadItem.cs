using System.Text.Json.Serialization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MetroHub.Widgets.Catalog.MetroSync.Models;

public sealed partial class ImagePayloadItem : MetroSyncMessageItem
{
    public string? ThumbnailFilePath { get; set; }
    public string? ThumbnailFileId { get; set; }
    public string? FileId { get; set; }
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

    [JsonIgnore]
    public bool HasCaption => !string.IsNullOrWhiteSpace(Caption);

    [ObservableProperty]
    [property: JsonIgnore]
    private ImageSource? _thumbnailImage;

    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isDownloading;

    [ObservableProperty]
    [property: JsonIgnore]
    private double _downloadProgress;

    [ObservableProperty]
    [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [JsonIgnore]
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    [JsonIgnore]
    public bool IsDownloaded => !string.IsNullOrEmpty(LocalPath) && System.IO.File.Exists(LocalPath);
}
