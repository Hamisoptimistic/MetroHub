using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MetroHub.Widgets.Catalog.Notepad;

/// <summary>
/// Layout-side payload for the Notes &amp; Tasks widget. Written into <c>TileModel.SettingsJson</c> —
/// i.e. into <c>layout.json</c> and layout snapshots — so it must stay tiny: a schema marker plus
/// a pointer back at the widget's own state file. The note body and to-do list live in
/// <see cref="NotepadWidgetState"/> under <c>config\widgets\notepad\</c>.
/// <para>
/// The properties marked "legacy" are read-only compatibility surface: every payload written
/// before the split carried its note inline here, so the loader still accepts them and migrates
/// once. They are never populated again — a save writes only <see cref="SchemaVersion"/> and
/// <see cref="StateRef"/>, which is what strips the note text out of <c>layout.json</c>.
/// </para>
/// </summary>
public class NotepadWidgetSettings
{
    /// <summary>Schema 2 = "state lives in the widget state file". Absent/1 = legacy inline payload.</summary>
    public const int CurrentSchemaVersion = 2;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    /// <summary>Tile id owning the state file (informational: the file is already keyed by tile id).</summary>
    [JsonPropertyName("stateRef")]
    public string? StateRef { get; set; }

    #region Legacy payload (read-only compatibility)

    /// <summary>Legacy inline note body.</summary>
    [JsonPropertyName("noteText")]
    public string? NoteText { get; set; }

    /// <summary>Legacy inline view mode: "Notes" or "Todo".</summary>
    [JsonPropertyName("activeViewMode")]
    public string? ActiveViewMode { get; set; }

    /// <summary>Legacy inline to-do list.</summary>
    [JsonPropertyName("tasks")]
    public List<TodoTaskItem>? Tasks { get; set; }

    #endregion

    /// <summary>True when this payload predates the state-file split (nothing else should be trusted).</summary>
    [JsonIgnore]
    public bool IsLegacyPayload => SchemaVersion < CurrentSchemaVersion;

    /// <summary>
    /// Builds a state object from a pre-split payload, or null when there is nothing to migrate.
    /// </summary>
    public NotepadWidgetState? TryBuildLegacyState()
    {
        if (!IsLegacyPayload)
        {
            return null;
        }

        var state = new NotepadWidgetState
        {
            NoteText = NoteText ?? string.Empty,
            ActiveViewMode = ActiveViewMode ?? "Notes",
            Tasks = Tasks ?? new List<TodoTaskItem>()
        };
        state.Normalize();
        return state;
    }
}
