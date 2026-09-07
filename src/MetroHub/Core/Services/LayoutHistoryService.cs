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

    public static string CaptureSnapshot(IEnumerable<TileModel> tiles)
    {
        return JsonSerializer.Serialize(tiles, JsonOptions);
    }

    public static List<TileModel>? ParseSnapshot(string snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot)) return null;
        try
        {
            return JsonSerializer.Deserialize<List<TileModel>>(snapshot, JsonOptions);
        }
        catch
        {
            return null;
        }
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
