using System.Text.Json;
using MetroHub.Core.Models;

namespace MetroHub.Core.Services;

public class LayoutHistoryService
{
    private const int MaxHistory = 40;
    private readonly Stack<string> _undoStack = new();
    private readonly Stack<string> _redoStack = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public static string CaptureSnapshot(IEnumerable<TileModel> tiles, IEnumerable<TileGroupModel>? groups = null)
    {
        var tileList = tiles.ToList();
        List<TileGroupModel> groupList;

        if (groups != null)
        {
            groupList = groups.ToList();
        }
        else
        {
            // Auto-discover groups from tiles if groups was omitted
            groupList = tileList
                .Where(t => !string.IsNullOrEmpty(t.Group))
                .GroupBy(t => t.Group!)
                .Select(g => new TileGroupModel
                {
                    Id = g.Key,
                    Title = g.First().SectionHeader ?? "Group",
                    Col = g.Min(t => t.Col),
                    Row = Math.Max(0, g.Min(t => t.Row) - 1)
                })
                .ToList();
        }

        var model = new LayoutSnapshotModel
        {
            Tiles = tileList,
            Groups = groupList
        };
        return JsonSerializer.Serialize(model, JsonOptions);
    }

    public static LayoutSnapshotModel? ParseSnapshot(string snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot)) return null;
        try
        {
            if (snapshot.TrimStart().StartsWith("{"))
            {
                var model = JsonSerializer.Deserialize<LayoutSnapshotModel>(snapshot, JsonOptions);
                if (model != null) return model;
            }

            var legacy = JsonSerializer.Deserialize<List<TileModel>>(snapshot, JsonOptions);
            if (legacy != null)
            {
                return new LayoutSnapshotModel { Tiles = legacy };
            }
        }
        catch
        {
            try
            {
                var legacy = JsonSerializer.Deserialize<List<TileModel>>(snapshot, JsonOptions);
                if (legacy != null) return new LayoutSnapshotModel { Tiles = legacy };
            }
            catch { }
        }
        return null;
    }

    public void PushState(string snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot)) return;

        // Avoid pushing identical consecutive state
        if (_undoStack.Count > 0 && _undoStack.Peek() == snapshot) return;

        _undoStack.Push(snapshot);
        _redoStack.Clear();

        if (_undoStack.Count > MaxHistory)
        {
            // Truncate the oldest item to preserve the 40-item cap
            var items = _undoStack.Reverse().Skip(1).ToList();
            _undoStack.Clear();
            foreach (var item in items)
            {
                _undoStack.Push(item);
            }
        }
    }

    public string? Undo(string currentSnapshot)
    {
        if (!CanUndo) return null;

        _redoStack.Push(currentSnapshot);
        return _undoStack.Pop();
    }

    public string? Redo(string currentSnapshot)
    {
        if (!CanRedo) return null;

        _undoStack.Push(currentSnapshot);
        return _redoStack.Pop();
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }
}

public class LayoutSnapshotModel
{
    public List<TileModel> Tiles { get; set; } = new();
    public List<TileGroupModel> Groups { get; set; } = new();
}
