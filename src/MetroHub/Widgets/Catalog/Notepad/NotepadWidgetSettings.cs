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

public sealed class NotepadTodoItem : ObservableObject
{
    private string _text = string.Empty;
    private bool _isCompleted;
    private bool _isEditing;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public bool IsCompleted
    {
        get => _isCompleted;
        set => SetProperty(ref _isCompleted, value);
    }

    public DateTime? CompletedAt { get; set; }

    [JsonIgnore]
    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }

    [JsonIgnore]
    public string OriginalText { get; set; } = string.Empty;

    public void BeginEdit()
    {
        OriginalText = Text;
        IsEditing = true;
    }

    public void CommitEdit()
    {
        var trimmed = Text?.Trim();
        Text = string.IsNullOrEmpty(trimmed) ? OriginalText : trimmed;
        IsEditing = false;
    }

    public void CancelEdit()
    {
        Text = OriginalText;
        IsEditing = false;
    }
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
