using System.Text.Json.Serialization;

namespace MetroHub.Widgets.Catalog.Rover;

/// <summary>
/// Persisted user preferences for the Rover companion widget.
/// Serialized into TileModel.SettingsJson.
/// </summary>
public sealed class RoverWidgetSettings
{
    [JsonPropertyName("isMuted")]
    public bool IsMuted { get; set; } = false;

    [JsonPropertyName("volume")]
    public double Volume { get; set; } = 0.65;

    [JsonPropertyName("sleepTimeoutMinutes")]
    public int SleepTimeoutMinutes { get; set; } = 2;

    [JsonPropertyName("showSpeechBubbles")]
    public bool ShowSpeechBubbles { get; set; } = true;

    [JsonPropertyName("backgroundStyle")]
    public string BackgroundStyle { get; set; } = "FluentGlass";
}
