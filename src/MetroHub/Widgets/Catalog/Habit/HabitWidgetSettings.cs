using System.Collections.Generic;

namespace MetroHub.Widgets.Catalog.Habit;

/// <summary>
/// Persistent state for an individual Habit Tracker widget instance.
/// Stored in TileModel.SettingsJson.
/// </summary>
public sealed class HabitWidgetSettings
{
    public string HabitName { get; set; } = string.Empty;
    public string IconSymbol { get; set; } = "TargetArrow24";
    public string AccentColorHex { get; set; } = "#0B9E76";

    /// <summary>
    /// Key = YYYYMMDD as int (e.g. 20260919). Value = HabitDayState (0 = Unmarked, 1 = Done, 2 = Failed).
    /// Absence of a key means Unmarked.
    /// </summary>
    public Dictionary<int, byte> DayStates { get; set; } = new();
}
