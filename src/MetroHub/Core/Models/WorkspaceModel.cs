using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace MetroHub.Core.Models;

/// <summary>
/// Represents a discrete workspace (canvas) containing its own independent tiles and groups.
/// Only the active workspace is realized in the WPF visual tree; inactive workspaces reside in managed memory.
/// </summary>
public sealed class WorkspaceModel : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "New Workspace";
    private string _iconSymbol = "Desktop24";
    private int _order = 0;
    private DateTime _createdUtc = DateTime.UtcNow;
    private bool _isActive;
    private bool _isDirty;

    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetField(ref _name, value))
            {
                OnPropertyChanged(nameof(TooltipText));
            }
        }
    }

    public string IconSymbol
    {
        get => _iconSymbol;
        set => SetField(ref _iconSymbol, value);
    }

    public int Order
    {
        get => _order;
        set
        {
            if (SetField(ref _order, value))
            {
                OnPropertyChanged(nameof(DisplayGlyph));
                OnPropertyChanged(nameof(TooltipText));
            }
        }
    }

    public DateTime CreatedUtc
    {
        get => _createdUtc;
        set => SetField(ref _createdUtc, value);
    }

    // ── Runtime state (Excluded from JSON manifest) ───────────

    [JsonIgnore]
    public bool IsActive
    {
        get => _isActive;
        set => SetField(ref _isActive, value);
    }

    [JsonIgnore]
    public bool IsDirty
    {
        get => _isDirty;
        set => SetField(ref _isDirty, value);
    }

    [JsonIgnore]
    public ObservableCollection<TileModel> Tiles { get; } = new();

    [JsonIgnore]
    public ObservableCollection<TileGroupModel> Groups { get; } = new();

    [JsonIgnore]
    public string DisplayGlyph => (Order + 1).ToString();

    [JsonIgnore]
    public string TooltipText => $"{Name} (Ctrl+Shift+{Math.Min(Order + 1, 9)})";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

/// <summary>
/// Root manifest serialized to disk (workspaces.json), storing workspace metadata and the active workspace pointer.
/// </summary>
public sealed class WorkspacesManifest
{
    public string ActiveWorkspaceId { get; set; } = "default";
    public List<WorkspaceModel> Workspaces { get; set; } = new();
}
