using System;
using CommunityToolkit.Mvvm.ComponentModel;
using MetroHub.Core.Radio;

namespace MetroHub.Widgets.Catalog.Radio;

/// <summary>
/// Presentation wrapper for a radio station card within the seamless 4-column grid.
/// Also supports the trailing '+' add custom station placeholder card.
/// </summary>
public sealed partial class RadioStationItemViewModel : ObservableObject
{
    public RadioStation? Station { get; }
    public bool IsAddPlaceholder { get; }

    public string Id => Station?.Id ?? "__add_placeholder__";
    public string Name => Station?.Name ?? "Add Station";
    public int BitrateKbps => Station?.BitrateKbps ?? 0;
    public string Description
    {
        get
        {
            if (IsAddPlaceholder) return "Add custom station";
            if (Station == null) return string.Empty;
            return FormatStationStreamInfo(Station);
        }
    }

    public static string FormatStationStreamInfo(RadioStation station)
    {
        int bitrate = station.BitrateKbps > 0 ? station.BitrateKbps : 128;
        string format = !string.IsNullOrWhiteSpace(station.Codec)
            ? station.Codec
            : InferStreamFormat(station.StreamUrl);

        return $"{bitrate} kbps • {format}";
    }

    private static string InferStreamFormat(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "MP3";
        string lower = url.ToLowerInvariant();
        if (lower.Contains(".m3u8") || lower.Contains("/hls") || lower.Contains("chunklist") || lower.Contains("playlist"))
            return "HLS";
        if (lower.Contains(".aac") || lower.Contains("aacp") || lower.Contains("mp4a") || lower.Contains("/aac"))
            return "AAC";
        if (lower.Contains(".flac"))
            return "FLAC";
        if (lower.Contains(".ogg") || lower.Contains(".opus"))
            return "OGG";
        return "MP3";
    }

    public string Icon => Station?.Icon ?? "Add24";
    public string Category => Station?.Category ?? string.Empty;
    public bool IsCustom => Station?.IsCustom ?? false;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isBuffering;

    public RadioStationItemViewModel(RadioStation station)
    {
        Station = station ?? throw new ArgumentNullException(nameof(station));
        IsAddPlaceholder = false;
    }

    private RadioStationItemViewModel()
    {
        Station = null;
        IsAddPlaceholder = true;
    }

    public static RadioStationItemViewModel CreateAddPlaceholder() => new();
}
