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
    public string BitrateBadge => BitrateKbps > 0 ? $"{BitrateKbps}k" : string.Empty;
    public string Description => Station?.Description ?? "Add your own custom streaming link";
    public string Icon => Station?.Icon ?? "Add24";

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
