using System;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MetroHub.Widgets.Catalog.Notepad;

/// <summary>
/// Observable model representing a single task in the interactive To-Do checklist.
/// Supports inline completion toggling, strikethrough, and serialization.
/// </summary>
public class TodoTaskItem : ObservableObject
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    private string _text = string.Empty;

    [JsonPropertyName("text")]
    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    private bool _isCompleted;

    [JsonPropertyName("isCompleted")]
    public bool IsCompleted
    {
        get => _isCompleted;
        set
        {
            if (SetProperty(ref _isCompleted, value))
            {
                CompletedAt = value ? DateTime.UtcNow : null;
            }
        }
    }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("completedAt")]
    public DateTime? CompletedAt { get; set; }

    public TodoTaskItem()
    {
    }

    public TodoTaskItem(string text, bool isCompleted = false)
    {
        Text = text;
        IsCompleted = isCompleted;
        if (isCompleted)
        {
            CompletedAt = DateTime.UtcNow;
        }
    }
}
