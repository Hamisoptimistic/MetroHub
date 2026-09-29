using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MetroHub.Core.Models.Geocoding;

/// <summary>
/// Represents a geographical location search result from geocoding providers.
/// </summary>
public sealed record GeoResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("latitude")] double? Latitude,
    [property: JsonPropertyName("longitude")] double? Longitude,
    [property: JsonPropertyName("admin1")] string? Admin1,
    [property: JsonPropertyName("admin2")] string? Admin2,
    [property: JsonPropertyName("country")] string? Country,
    [property: JsonPropertyName("timezone")] string? Timezone)
{
    [JsonIgnore]
    public string DisplaySubtitle { get; set; } = string.Empty;
}

internal sealed record OpenMeteoGeocodingResponse(
    [property: JsonPropertyName("results")] List<GeoResult>? Results);

internal sealed record PhotonResponse(
    [property: JsonPropertyName("features")] List<PhotonFeature>? Features);

internal sealed record PhotonFeature(
    [property: JsonPropertyName("geometry")] PhotonGeometry? Geometry,
    [property: JsonPropertyName("properties")] PhotonProperties? Properties);

internal sealed record PhotonGeometry(
    [property: JsonPropertyName("coordinates")] double[]? Coordinates);

internal sealed record PhotonProperties(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("country")] string? Country);
