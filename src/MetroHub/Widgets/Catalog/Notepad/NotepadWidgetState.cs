using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MetroHub.Widgets.Catalog.Notepad;

/// <summary>
/// Bulk payload for the Notes &amp; Tasks widget, persisted by
/// <see cref="MetroHub.Core.Services.WidgetStateStore"/> in
/// <c>config\widgets\notepad\{tileId}.json</c> — never inside <c>layout.json</c>.
/// <para>
/// This is the widget's autosave mirror: it holds the note body and the to-do list so a typing
/// pause writes a few hundred bytes here instead of re-serializing and fsync-ing every widget's
/// payload in the hub layout.
/// </para>
/// </summary>
public sealed class NotepadWidgetState
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("noteText")]
    public string NoteText { get; set; } = string.Empty;

    [JsonPropertyName("activeViewMode")]
    public string ActiveViewMode { get; set; } = "Notes";

    [JsonPropertyName("tasks")]
    public List<TodoTaskItem> Tasks { get; set; } = new();

    /// <summary>
    /// Sanitizes a payload that came off disk: non-null collections and a known view mode, so a
    /// hand-edited or truncated state file can never break the widget.
    /// </summary>
    public void Normalize()
    {
        NoteText ??= string.Empty;
        Tasks ??= new List<TodoTaskItem>();
        if (!string.Equals(ActiveViewMode, "Todo", StringComparison.OrdinalIgnoreCase))
        {
            ActiveViewMode = "Notes";
        }
    }

    /// <summary>
    /// Copy that is safe to hand to serialization (callers keep their live task objects).
    /// The list is copied; the task items are shared because serialization only reads them.
    /// </summary>
    public NotepadWidgetState CloneForSave(string noteText, string activeViewMode, IEnumerable<TodoTaskItem> tasks)
        => new()
        {
            SchemaVersion = CurrentSchemaVersion,
            NoteText = noteText ?? string.Empty,
            ActiveViewMode = string.Equals(activeViewMode, "Todo", StringComparison.OrdinalIgnoreCase) ? "Todo" : "Notes",
            Tasks = new List<TodoTaskItem>(tasks)
        };
}
