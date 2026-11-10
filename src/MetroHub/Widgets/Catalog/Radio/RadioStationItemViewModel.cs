using System;
using System.Buffers;
using CommunityToolkit.Mvvm.ComponentModel;
using MetroHub.Widgets.Catalog.Radio.Services;

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

    private static readonly SearchValues<string> s_hlsKeywords = SearchValues.Create([".m3u8", "/hls", "chunklist", "playlist"], StringComparison.OrdinalIgnoreCase);
    private static readonly SearchValues<string> s_aacKeywords = SearchValues.Create([".aac", "aacp", "mp4a", "/aac"], StringComparison.OrdinalIgnoreCase);
    private static readonly SearchValues<string> s_oggKeywords = SearchValues.Create([".ogg", ".opus"], StringComparison.OrdinalIgnoreCase);

    private static string InferStreamFormat(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "MP3";
        ReadOnlySpan<char> span = url.AsSpan();
        if (span.ContainsAny(s_hlsKeywords)) return "HLS";
        if (span.ContainsAny(s_aacKeywords)) return "AAC";
        if (span.Contains(".flac", StringComparison.OrdinalIgnoreCase)) return "FLAC";
        if (span.ContainsAny(s_oggKeywords)) return "OGG";
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
