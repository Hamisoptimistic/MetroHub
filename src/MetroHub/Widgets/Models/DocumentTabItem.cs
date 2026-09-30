using System;
using System.IO;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MetroHub.Widgets.Models;

/// <summary>
/// Observable model representing a document/note tab.
/// Reusable across Markdown and Notepad widgets.
/// </summary>
public class DocumentTabItem : ObservableObject
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    private string _title = "Untitled";

    [JsonPropertyName("title")]
    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value))
            {
                OnPropertyChanged(nameof(DisplayTitle));
            }
        }
    }

    private string _text = string.Empty;

    [JsonPropertyName("text")]
    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                IsDirty = true;
            }
        }
    }

    private string? _sourceFilePath;

    [JsonPropertyName("sourceFilePath")]
    public string? SourceFilePath
    {
        get => _sourceFilePath;
        set
        {
            if (SetProperty(ref _sourceFilePath, value))
            {
                OnPropertyChanged(nameof(DisplayTitle));
            }
        }
    }

    private long _sourceFileWriteUtc;

    [JsonPropertyName("sourceFileWriteUtc")]
    public long SourceFileWriteUtc
    {
        get => _sourceFileWriteUtc;
        set => SetProperty(ref _sourceFileWriteUtc, value);
    }

    private bool _isDirty;

    [JsonIgnore]
    public bool IsDirty
    {
        get => _isDirty;
        set => SetProperty(ref _isDirty, value);
    }

    private bool _isActive;

    [JsonIgnore]
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    /// <summary>
    /// User-facing display title: uses file name if linked to a file, otherwise Title.
    /// </summary>
    [JsonIgnore]
    public string DisplayTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SourceFilePath))
            {
                try
                {
                    return Path.GetFileName(SourceFilePath);
                }
                catch
                {
                    // fallback
                }
            }
            return string.IsNullOrWhiteSpace(Title) ? "Untitled" : Title;
        }
    }

    public DocumentTabItem()
    {
    }

    public DocumentTabItem(string title, string text = "", string? filePath = null)
    {
        _title = title;
        _text = text;
        _sourceFilePath = filePath;
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            try
            {
                _sourceFileWriteUtc = File.GetLastWriteTimeUtc(filePath).Ticks;
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Clones tab state for settings serialization or snapshotting.
    /// </summary>
    public DocumentTabItem Clone()
    {
        return new DocumentTabItem
        {
            Id = this.Id,
            Title = this.Title,
            Text = this.Text,
            SourceFilePath = this.SourceFilePath,
            SourceFileWriteUtc = this.SourceFileWriteUtc,
            IsDirty = this.IsDirty,
            IsActive = this.IsActive
        };
    }
}
