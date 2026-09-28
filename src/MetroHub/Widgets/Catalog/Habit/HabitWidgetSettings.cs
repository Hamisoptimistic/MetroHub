using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MetroHub.Widgets.Catalog.Habit;

/// <summary>
/// Layout-side payload for one Habit Tracker instance. Written into <c>TileModel.SettingsJson</c> —
/// i.e. into <c>layout.json</c> and layout snapshots — so it stays tiny: a schema marker plus a
/// pointer back at the widget's own state file. The habit name, icon, accent and day grid live in
/// <see cref="HabitWidgetState"/> under <c>config\widgets\habit\</c>.
/// <para>
/// The properties marked "legacy" are read-only compatibility surface: every payload written
/// before the split carried its grid inline here, so the loader still accepts them (case-insensitively,
/// because pre-split payloads were PascalCase) and migrates once. A save writes only
/// <see cref="SchemaVersion"/> and <see cref="StateRef"/>.
/// </para>
/// </summary>
public sealed class HabitWidgetSettings
{
    /// <summary>Schema 2 = "state lives in the widget state file". Absent/1 = legacy inline payload.</summary>
    public const int CurrentSchemaVersion = 2;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    /// <summary>Tile id owning the state file (informational: the file is already keyed by tile id).</summary>
    [JsonPropertyName("stateRef")]
    public string? StateRef { get; set; }

    #region Legacy payload (read-only compatibility)

    /// <summary>Legacy inline habit name.</summary>
    [JsonPropertyName("habitName")]
    public string? HabitName { get; set; }

    /// <summary>Legacy inline icon symbol.</summary>
    [JsonPropertyName("iconSymbol")]
    public string? IconSymbol { get; set; }

    /// <summary>Legacy inline accent color.</summary>
    [JsonPropertyName("accentColorHex")]
    public string? AccentColorHex { get; set; }

    /// <summary>Legacy inline day grid (YYYYMMDD → HabitDayState byte).</summary>
    [JsonPropertyName("dayStates")]
    public Dictionary<int, byte>? DayStates { get; set; }

    #endregion

    /// <summary>True when this payload predates the state-file split (nothing else should be trusted).</summary>
    [JsonIgnore]
    public bool IsLegacyPayload => SchemaVersion < CurrentSchemaVersion;

    /// <summary>
    /// Builds a state object from a pre-split payload, or null when there is nothing to migrate.
    /// </summary>
    public HabitWidgetState? TryBuildLegacyState()
    {
        if (!IsLegacyPayload)
        {
            return null;
        }

        var state = new HabitWidgetState
        {
            HabitName = HabitName ?? string.Empty,
            IconSymbol = IconSymbol ?? "TargetArrow24",
            AccentColorHex = AccentColorHex ?? "#0B9E76",
            DayStates = DayStates ?? new Dictionary<int, byte>()
        };
        state.Normalize();
        return state;
    }
}
