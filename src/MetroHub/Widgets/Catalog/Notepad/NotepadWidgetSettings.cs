using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MetroHub.Widgets.Catalog.Notepad;

public enum NotepadMode
{
    Notes = 0,
    Todo = 1
}

public sealed partial class NotepadTodoItem : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    public bool IsCompleted { get; set; }

    [ObservableProperty]
    [JsonIgnore]
    public partial bool IsEditing { get; set; }

    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// Persisted settings model for the Notepad & Todo widget.
/// Serialized through WidgetSerializer into TileModel.SettingsJson.
/// </summary>
public sealed class NotepadWidgetSettings
{
    public string DocumentXaml { get; set; } = string.Empty;
    public string PlainText { get; set; } = string.Empty;
    public List<NotepadTodoItem> ActiveTodos { get; set; } = new();
    public List<NotepadTodoItem> CompletedTodos { get; set; } = new();
    public bool IsWordWrapEnabled { get; set; } = true;
    private NotepadMode _mode = NotepadMode.Notes;
    public NotepadMode Mode
    {
        get => Enum.IsDefined(_mode) ? _mode : NotepadMode.Notes;
        set => _mode = Enum.IsDefined(value) ? value : NotepadMode.Notes;
    }
}

