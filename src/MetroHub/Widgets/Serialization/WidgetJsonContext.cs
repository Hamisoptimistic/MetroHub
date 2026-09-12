using System.Text.Json.Serialization;
using MetroHub.Widgets.Catalog.Clock;
using MetroHub.Widgets.Catalog.Media;
using MetroHub.Widgets.Catalog.Photos;
using MetroHub.Widgets.Catalog.Pomodoro;
using MetroHub.Widgets.Catalog.Stub;

namespace MetroHub.Widgets.Serialization;

/// <summary>
/// Source-generated System.Text.Json serialization context for widget settings payloads (Phase 0.6).
/// Replaces reflection-based serialization to ensure compile-time verification, trimmed-assembly safety,
/// and maximum execution performance.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StubWidgetSettings))]
[JsonSerializable(typeof(ClockWidgetSettings))]
[JsonSerializable(typeof(MediaWidgetSettings))]
[JsonSerializable(typeof(PomodoroWidgetSettings))]
[JsonSerializable(typeof(PhotosWidgetSettings))]
[JsonSerializable(typeof(MetroHub.Widgets.Catalog.Volume.VolumeWidgetSettings))]
public partial class WidgetJsonContext : JsonSerializerContext
{
}
