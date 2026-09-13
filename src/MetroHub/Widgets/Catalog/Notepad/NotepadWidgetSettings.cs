using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MetroHub.Widgets.Catalog.Notepad;

/// <summary>
/// Persisted configuration and state payload for the Notepad / Todo Widget.
/// Round-trips cleanly through TileModel.SettingsJson.
/// </summary>
public class NotepadWidgetSettings
{
    /// <summary>
    /// Freeform multi-line text note content.
    /// </summary>
    [JsonPropertyName("noteText")]
    public string NoteText { get; set; } = string.Empty;

    /// <summary>
    /// Active view mode: "Notes" or "Todo".
    /// </summary>
    [JsonPropertyName("activeViewMode")]
    public string ActiveViewMode { get; set; } = "Notes";

    /// <summary>
    /// List of interactive To-Do items.
    /// </summary>
    [JsonPropertyName("tasks")]
    public List<TodoTaskItem> Tasks { get; set; } = new();
}
