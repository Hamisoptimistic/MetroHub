using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MetroHub.Core.Radio;

/// <summary>
/// Represents a single radio or ambient audio streaming station.
/// </summary>
public sealed record RadioStation
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("streamUrl")]
    public string StreamUrl { get; init; } = string.Empty;

    [JsonPropertyName("bitrateKbps")]
    public int BitrateKbps { get; init; } = 128;

    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;

    [JsonPropertyName("icon")]
    public string Icon { get; init; } = "HeadphonesSoundWave24";

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("homepageUrl")]
    public string HomepageUrl { get; init; } = string.Empty;

    [JsonPropertyName("isCustom")]
    public bool IsCustom { get; init; } = false;

    [JsonPropertyName("apiStationUuid")]
    public string? ApiStationUuid { get; init; }

    [JsonPropertyName("codec")]
    public string? Codec { get; init; }
}

/// <summary>
/// Represents a categorized group of radio stations.
/// </summary>
public sealed record RadioCategory
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("iconSymbol")]
    public string IconSymbol { get; init; } = "HeadphonesSoundWave24";

    [JsonPropertyName("stations")]
    public List<RadioStation> Stations { get; init; } = new();
}

/// <summary>
/// Root catalog container loaded from embedded JSON resource.
/// </summary>
public sealed record RadioCatalog
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("categories")]
    public List<RadioCategory> Categories { get; init; } = new();
}

/// <summary>
/// Display projection of an online radio station search result.
/// </summary>
public sealed class RadioSearchResultItem
{
    public required string Name { get; init; }
    public required string Subtitle { get; init; }
    public required string BitrateDisplay { get; init; }
    public required string ResolvedStreamUrl { get; init; }
    public required string? StationUuid { get; init; }
    public required int BitrateKbps { get; init; }
    public string? Codec { get; init; }
}
