namespace MetroHub.Widgets.Catalog.Pomodoro;

/// <summary>
/// Persisted configuration payload for the Pomodoro Widget.
/// Round-trips cleanly through TileModel.SettingsJson.
/// </summary>
public class PomodoroWidgetSettings
{
    public int FocusMinutes { get; set; } = 25;
    public int ShortBreakMinutes { get; set; } = 5;
    public int LongBreakMinutes { get; set; } = 15;
    public int LongBreakInterval { get; set; } = 4;
    public bool SoundEnabled { get; set; } = true;

    // Analytics / Gamification
    public int CompletedSessions { get; set; } = 0;
    public int TotalFocusMinutes { get; set; } = 0;
    public int CurrentStreak { get; set; } = 1;
    public string? LastActiveDate { get; set; }
}
