namespace MetroHub.Widgets.Catalog.Habit;

/// <summary>
/// Status of a single habit day.
/// Compact byte fits in Dictionary&lt;int, byte&gt;.
/// </summary>
public enum HabitDayState : byte
{
    Unmarked = 0,
    Done = 1,
    Failed = 2
}
