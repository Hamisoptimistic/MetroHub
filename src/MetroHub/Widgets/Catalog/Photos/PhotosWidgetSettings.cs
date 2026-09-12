namespace MetroHub.Widgets.Catalog.Photos;

public class PhotosWidgetSettings
{
    public string? FolderPath { get; set; }
    public int IntervalSeconds { get; set; } = 30;
    public bool Shuffle { get; set; } = true;
    public bool IncludeSubfolders { get; set; } = false;
    public string FitMode { get; set; } = "FitWithBlur"; // "FitWithBlur" | "CropFill"
}
