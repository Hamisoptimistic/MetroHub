using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.Input;
using MetroHub.Core.Models;
using MetroHub.Core.Services;

namespace MetroHub.Presentation.Controllers;

/// <summary>
/// Core domain controller and single source of truth for MetroHub multi-canvas workspaces.
/// Holds live collections in managed memory, manages active canvas state, coordinates in-memory swaps,
/// triggers eager ViewModel realization for background widgets, and debounces asynchronous disk flushes.
/// </summary>
public sealed class WorkspaceManager : INotifyPropertyChanged
{
    private static readonly Lazy<WorkspaceManager> _instance = new(() => new WorkspaceManager());
    public static WorkspaceManager Instance => _instance.Value;

    private const int MaxWorkspacesLimit = 10;
    private WorkspaceModel? _activeWorkspace;
    private bool _isInitialized;
    private readonly object _flushGate = new();
    private Task? _backgroundFlushTask;

    private ObservableCollection<WorkspaceModel> _workspaces = new();
    public ObservableCollection<WorkspaceModel> Workspaces
    {
        get => _workspaces;
        private set
        {
            if (_workspaces != value)
            {
                _workspaces = value;
                OnPropertyChanged();
            }
        }
    }

    public WorkspaceModel ActiveWorkspace
    {
        get => _activeWorkspace ??= (Workspaces.FirstOrDefault(w => w.IsActive) ?? Workspaces.FirstOrDefault() ?? CreateDefaultInMemory());
        private set
        {
            if (SetField(ref _activeWorkspace, value))
            {
                OnPropertyChanged(nameof(CanCreateWorkspace));
            }
        }
    }

    /// <summary>Soft cap: max 10 workspaces.</summary>
    public bool CanCreateWorkspace => Workspaces.Count < MaxWorkspacesLimit;

    public int MaxWorkspaces => MaxWorkspacesLimit;

    // ── Commands for XAML DataBindings ────────────────────────
    public IRelayCommand SwitchWorkspaceCommand { get; }
    public IRelayCommand CreateWorkspaceCommand { get; }
    public IRelayCommand DeleteWorkspaceCommand { get; }
    public IRelayCommand RequestDeleteWorkspaceCommand { get; }
    public IRelayCommand RequestRenameWorkspaceCommand { get; }

    // ── Events for Decoupled UI & Shell Coordination ──────────
    public event Action<WorkspaceModel?, WorkspaceModel>? WorkspaceChanging;
    public event Action<WorkspaceModel>? WorkspaceChanged;
    public event Action<WorkspaceModel>? RequestRename;
    public event Action<WorkspaceModel>? RequestDelete;
    public event Action? TileMoved;

    public WorkspaceManager()
    {
        SwitchWorkspaceCommand = new RelayCommand<object>(param =>
        {
            if (param is string id) SwitchWorkspace(id);
            else if (param is WorkspaceModel model) SwitchWorkspace(model.Id);
        });

        CreateWorkspaceCommand = new RelayCommand(() => CreateWorkspace());

        DeleteWorkspaceCommand = new RelayCommand<object>(param =>
        {
            if (param is string id) DeleteWorkspace(id);
            else if (param is WorkspaceModel model) DeleteWorkspace(model.Id);
        });

        RequestDeleteWorkspaceCommand = new RelayCommand<object>(param =>
        {
            WorkspaceModel? ws = null;
            if (param is WorkspaceModel model) ws = model;
            else if (param is string id) ws = Workspaces.FirstOrDefault(w => w.Id == id);

            if (ws != null && Workspaces.Count > 1)
            {
                if (RequestDelete != null)
                {
                    RequestDelete.Invoke(ws);
                }
                else
                {
                    DeleteWorkspace(ws.Id);
                }
            }
        });

        RequestRenameWorkspaceCommand = new RelayCommand<object>(param =>
        {
            if (param is WorkspaceModel model) RequestRename?.Invoke(model);
            else if (param is string id)
            {
                var ws = Workspaces.FirstOrDefault(w => w.Id == id);
                if (ws != null) RequestRename?.Invoke(ws);
            }
        });
    }

    /// <summary>
    /// Loads workspaces from disk or runs legacy copy migration on first startup.
    /// Eagerly creates ViewModels for stateful widgets across all workspaces.
    /// </summary>
    public void Initialize()
    {
        if (_isInitialized) return;
        _isInitialized = true;

        var manifest = StorageService.LoadWorkspaces();
        Workspaces.Clear();

        foreach (var ws in manifest.Workspaces.OrderBy(w => w.Order))
        {
            AttachWorkspaceTracking(ws);
            Workspaces.Add(ws);
        }

        if (Workspaces.Count == 0)
        {
            var def = CreateDefaultInMemory();
            AttachWorkspaceTracking(def);
            Workspaces.Add(def);
        }

        var active = Workspaces.FirstOrDefault(w => w.Id == manifest.ActiveWorkspaceId)
                     ?? Workspaces.First();

        foreach (var w in Workspaces)
        {
            w.IsActive = (w == active);
        }

        ActiveWorkspace = active;
        OnPropertyChanged(nameof(CanCreateWorkspace));
    }

    /// <summary>
    /// Returns the union of tile IDs across ALL workspaces in managed memory.
    /// Used by MainWindow startup to prevent silent pruning of inactive workspace widget state.
    /// </summary>
    public IReadOnlyCollection<string> GetAllTileIdsAcrossAllWorkspaces()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ws in Workspaces)
        {
            foreach (var tile in ws.Tiles)
            {
                if (!string.IsNullOrWhiteSpace(tile.Id))
                {
                    set.Add(tile.Id);
                }
            }
        }
        return set;
    }

    /// <summary>
    /// Switches the active workspace. Performs immediate flush of outgoing workspace if dirty,
    /// updates active markers, saves manifest pointer, and triggers transition events.
    /// </summary>
    public void SwitchWorkspace(string workspaceId)
    {
        if (ActiveWorkspace?.Id == workspaceId) return;

        var target = Workspaces.FirstOrDefault(w => w.Id == workspaceId);
        if (target == null) return;

        var outgoing = ActiveWorkspace;
        if (outgoing != null)
        {
            if (outgoing.IsDirty)
            {
                outgoing.IsDirty = false;
                StorageService.SaveWorkspaceLayoutSync(outgoing.Id, outgoing.Tiles);
                StorageService.SaveWorkspaceGroupsSync(outgoing.Id, outgoing.Groups);
            }
            outgoing.IsActive = false;
        }

        target.IsActive = true;
        ActiveWorkspace = target;

        SaveManifest();

        WorkspaceChanging?.Invoke(outgoing, target);
        WorkspaceChanged?.Invoke(target);
        OnPropertyChanged(nameof(ActiveWorkspace));
    }

    /// <summary>
    /// Creates a new workspace if under the 10-workspace soft cap, persists it, and switches to it.
    /// </summary>
    public WorkspaceModel? CreateWorkspace(string? name = null)
    {
        if (Workspaces.Count >= MaxWorkspacesLimit) return null;

        int nextOrder = Workspaces.Count;
        string wsName = string.IsNullOrWhiteSpace(name) ? $"Workspace {nextOrder + 1}" : name.Trim();

        var ws = new WorkspaceModel
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = wsName,
            Order = nextOrder,
            IconSymbol = "Desktop24",
            CreatedUtc = DateTime.UtcNow,
            IsDirty = true
        };

        AttachWorkspaceTracking(ws);
        Workspaces.Add(ws);

        StorageService.SaveWorkspaceSync(ws);
        SaveManifest(sync: true);

        OnPropertyChanged(nameof(CanCreateWorkspace));

        SwitchWorkspace(ws.Id);
        return ws;
    }

    /// <summary>
    /// Renames a workspace and persists the updated manifest.
    /// </summary>
    public bool RenameWorkspace(string workspaceId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return false;

        var ws = Workspaces.FirstOrDefault(w => w.Id == workspaceId);
        if (ws == null) return false;

        ws.Name = newName.Trim();
        SaveManifest(sync: true);
        return true;
    }

    /// <summary>
    /// Non-destructively deletes a workspace (minimum 1 workspace check; moves directory to trash).
    /// </summary>
    public bool DeleteWorkspace(string workspaceId)
    {
        if (Workspaces.Count <= 1) return false;

        var ws = Workspaces.FirstOrDefault(w => w.Id == workspaceId);
        if (ws == null) return false;

        if (ActiveWorkspace?.Id == workspaceId)
        {
            var fallback = Workspaces.FirstOrDefault(w => w.Id != workspaceId);
            if (fallback != null)
            {
                SwitchWorkspace(fallback.Id);
            }
        }

        foreach (var tile in ws.Tiles)
        {
            tile.Teardown();
        }

        Workspaces.Remove(ws);
        for (int i = 0; i < Workspaces.Count; i++)
        {
            Workspaces[i].Order = i;
        }

        StorageService.DeleteWorkspaceStorage(workspaceId);
        SaveManifest(sync: true);

        OnPropertyChanged(nameof(CanCreateWorkspace));
        return true;
    }

    /// <summary>
    /// Cleanly transfers a tile from the active workspace to a target workspace in-memory.
    /// </summary>
    public void MoveTileToWorkspace(TileModel tile, string targetWorkspaceId)
    {
        var target = Workspaces.FirstOrDefault(w => w.Id == targetWorkspaceId);
        if (target == null || ActiveWorkspace == null || target.Id == ActiveWorkspace.Id) return;

        // 1. Remove from source
        ActiveWorkspace.Tiles.Remove(tile);
        ActiveWorkspace.IsDirty = true;

        // 2. Cleanse source-specific references and position cleanly in target
        tile.Group = null;
        int targetCol = 0;
        int targetRow = 1;
        if (target.Tiles.Count > 0)
        {
            int maxRow = target.Tiles.Max(t => t.Row + t.SpanY);
            targetRow = Math.Max(1, maxRow);
        }
        tile.Col = targetCol;
        tile.Row = targetRow;
        tile.X = GridPlacementService.PixelXFromCol(targetCol);
        tile.Y = GridPlacementService.PixelYFromRow(targetRow);

        // 3. Add to target
        target.Tiles.Add(tile);
        target.IsDirty = true;

        // 4. Invalidate source undo stack
        TileMoved?.Invoke();

        ScheduleBackgroundFlush();
    }

    private void AttachWorkspaceTracking(WorkspaceModel ws)
    {
        ws.Tiles.CollectionChanged += (s, e) =>
        {
            if (e.NewItems != null)
            {
                foreach (TileModel tile in e.NewItems)
                {
                    tile.PropertyChanged += (sender, args) => OnTilePropertyChanged(ws, args);
                    if (tile.TileType == TileType.Widget)
                    {
                        tile.EnsureViewModelCreated();
                    }
                }
            }
            MarkDirty(ws);
        };

        ws.Groups.CollectionChanged += (s, e) => MarkDirty(ws);

        foreach (var tile in ws.Tiles)
        {
            tile.PropertyChanged += (sender, args) => OnTilePropertyChanged(ws, args);
            if (tile.TileType == TileType.Widget)
            {
                tile.EnsureViewModelCreated();
            }
        }
    }

    private void OnTilePropertyChanged(WorkspaceModel ws, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(TileModel.IsBeingDragged)
                               or nameof(TileModel.IsSelected)
                               or nameof(TileModel.TileContent)
                               or nameof(TileModel.WidgetViewModel))
        {
            return;
        }

        MarkDirty(ws);
    }

    public void MarkDirty(WorkspaceModel ws)
    {
        ws.IsDirty = true;
        ScheduleBackgroundFlush();
    }

    public void SaveManifest(bool sync = false)
    {
        var manifest = new WorkspacesManifest
        {
            ActiveWorkspaceId = ActiveWorkspace?.Id ?? "default",
            Workspaces = Workspaces.ToList()
        };
        if (sync)
        {
            StorageService.SaveWorkspacesSync(manifest);
        }
        else
        {
            StorageService.SaveWorkspaces(manifest);
        }
    }

    public void ScheduleBackgroundFlush()
    {
        lock (_flushGate)
        {
            if (_backgroundFlushTask == null || _backgroundFlushTask.IsCompleted)
            {
                _backgroundFlushTask = Task.Run(async () =>
                {
                    while (true)
                    {
                        await Task.Delay(50).ConfigureAwait(false);
                        FlushDirtyWorkspaces();

                        lock (_flushGate)
                        {
                            if (!Workspaces.Any(w => w.IsDirty))
                            {
                                _backgroundFlushTask = null;
                                break;
                            }
                        }
                    }
                });
            }
        }
    }

    public void FlushDirtyWorkspaces()
    {
        foreach (var ws in Workspaces.ToList())
        {
            if (ws.IsDirty)
            {
                ws.IsDirty = false;
                StorageService.SaveWorkspaceLayout(ws.Id, ws.Tiles);
                StorageService.SaveWorkspaceGroups(ws.Id, ws.Groups);
            }
        }
    }

    public void FlushSync()
    {
        foreach (var ws in Workspaces.ToList())
        {
            if (ws.IsDirty)
            {
                ws.IsDirty = false;
                StorageService.SaveWorkspaceLayoutSync(ws.Id, ws.Tiles);
                StorageService.SaveWorkspaceGroupsSync(ws.Id, ws.Groups);
            }
        }
        SaveManifest();
        StorageService.Flush();
    }

    public void ResetForTesting()
    {
        _isInitialized = false;
        Workspaces = new ObservableCollection<WorkspaceModel>();
        _activeWorkspace = null;
    }

    private static WorkspaceModel CreateDefaultInMemory()
    {
        return new WorkspaceModel
        {
            Id = "default",
            Name = "Main",
            Order = 0,
            IconSymbol = "Desktop24",
            IsActive = true
        };
    }

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
