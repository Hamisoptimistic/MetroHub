using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MetroHub.Widgets.Catalog.Habit;

/// <summary>
/// Bulk payload for one Habit Tracker instance, persisted by
/// <see cref="MetroHub.Core.Services.WidgetStateStore"/> in
/// <c>config\widgets\habit\{tileId}.json</c> — never inside <c>layout.json</c>.
/// <para>
/// The payload is small, but the widget saves on every day toggle and on every hub hide, so it
/// must not ride along in the shared layout: each checkmark used to re-serialize and fsync the
/// full hub layout.
/// </para>
/// </summary>
public sealed class HabitWidgetState
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("habitName")]
    public string HabitName { get; set; } = string.Empty;

    [JsonPropertyName("iconSymbol")]
    public string IconSymbol { get; set; } = "TargetArrow24";

    [JsonPropertyName("accentColorHex")]
    public string AccentColorHex { get; set; } = "#0B9E76";

    /// <summary>
    /// Key = YYYYMMDD as int (e.g. 20260919). Value = HabitDayState (0 = Unmarked, 1 = Done, 2 = Failed).
    /// Absence of a key means Unmarked.
    /// </summary>
    [JsonPropertyName("dayStates")]
    public Dictionary<int, byte> DayStates { get; set; } = new();

    /// <summary>Sanitizes a payload that came off disk resume-safe: no nulls, known defaults.</summary>
    public void Normalize()
    {
        HabitName ??= string.Empty;
        IconSymbol = string.IsNullOrWhiteSpace(IconSymbol) ? "TargetArrow24" : IconSymbol;
        AccentColorHex = string.IsNullOrWhiteSpace(AccentColorHex) ? "#0B9E76" : AccentColorHex;
        DayStates ??= new Dictionary<int, byte>();
    }

    /// <summary>Copy that is safe to hand to serialization (callers keep their live state object).</summary>
    public HabitWidgetState CloneForSave(string habitName, string iconSymbol, string accentColorHex)
        => new()
        {
            SchemaVersion = CurrentSchemaVersion,
            HabitName = habitName ?? string.Empty,
            IconSymbol = string.IsNullOrWhiteSpace(iconSymbol) ? "TargetArrow24" : iconSymbol,
            AccentColorHex = string.IsNullOrWhiteSpace(accentColorHex) ? "#0B9E76" : accentColorHex,
            DayStates = new Dictionary<int, byte>(DayStates)
        };
}
