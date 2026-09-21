using System;

namespace MetroHub.Widgets.Catalog.Caffeine;

/// <summary>
/// Persisted state for the Caffeine & Light widget.
/// Round-trips cleanly through TileModel.SettingsJson.
/// </summary>
public sealed class CaffeineWidgetSettings
{
    public string LastPanel { get; set; } = "Caffeine";
    public double NightLightStrength { get; set; } = 50.0;
    public bool KeepScreenOn { get; set; } = false;
    public int SelectedMinutes { get; set; } = 30;
    public bool CinematicFade { get; set; } = true;
}
