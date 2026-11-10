using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace MetroHub.Widgets.Catalog.Game2048;

public enum MoveDirection
{
    Left,
    Right,
    Up,
    Down
}

[InlineArray(GridLength)]
public struct GridBuffer
{
    public const int GridLength = 16;
    private int _element0;
}

public readonly struct GameStateSnapshot
{
    public GridBuffer Grid { get; }
    public int Score { get; }
    public bool HasWon { get; }
    public bool KeepPlaying { get; }

    public GameStateSnapshot(GridBuffer grid, int score, bool hasWon, bool keepPlaying)
    {
        Grid = grid;
        Score = score;
        HasWon = hasWon;
        KeepPlaying = keepPlaying;
    }

    public static GameStateSnapshot Capture(TilePiece?[,] grid, int score, bool hasWon, bool keepPlaying)
    {
        GridBuffer buffer = default;
        for (int r = 0; r < Game2048Engine.GridSize; r++)
        {
            for (int c = 0; c < Game2048Engine.GridSize; c++)
            {
                buffer[r * Game2048Engine.GridSize + c] = grid[r, c]?.Value ?? 0;
            }
        }
        return new GameStateSnapshot(buffer, score, hasWon, keepPlaying);
    }
}

public sealed class TilePiece
{
    private static long _idCounter;

    public long Id { get; }
    public int Value { get; set; }
    public int Row { get; set; }
    public int Col { get; set; }
    public int PreviousRow { get; set; }
    public int PreviousCol { get; set; }
    public bool IsNew { get; set; }
    public bool IsMerged { get; set; }
    public TilePiece? MergedInto { get; set; }

    public TilePiece(int row, int col, int value, bool isNew = false)
    {
        Id = System.Threading.Interlocked.Increment(ref _idCounter);
        Row = row;
        Col = col;
        PreviousRow = row;
        PreviousCol = col;
        Value = value;
        IsNew = isNew;
    }
}

public sealed class Game2048Engine
{
    public const int GridSize = 4;
    public const int MaxUndoHistory = 64;

    private readonly TilePiece?[,] _grid = new TilePiece?[GridSize, GridSize];
    private readonly List<TilePiece> _allTiles = new(32);
    private readonly List<GameStateSnapshot> _undoHistory = new(MaxUndoHistory);

    public int Score { get; private set; }
    public int BestScore { get; set; }
    public bool IsGameOver { get; private set; }
    public bool HasWon { get; private set; }
    public bool KeepPlaying { get; set; }

    public bool CanUndo => _undoHistory.Count > 0;
    public int UndoHistoryCount => _undoHistory.Count;

    public IReadOnlyList<TilePiece> Tiles => _allTiles;

    public Game2048Engine()
    {
        Reset();
    }

    public void Reset()
    {
        _undoHistory.Clear();
        Array.Clear(_grid, 0, _grid.Length);
        _allTiles.Clear();
        Score = 0;
        IsGameOver = false;
        HasWon = false;
        KeepPlaying = false;

        SpawnTile();
        SpawnTile();
    }

    public void LoadState(List<int>? savedGrid, int currentScore, int bestScore)
    {
        BestScore = Math.Max(BestScore, bestScore);

        if (savedGrid == null || savedGrid.Count != GridSize * GridSize)
        {
            Reset();
            return;
        }

        _undoHistory.Clear();
        Array.Clear(_grid, 0, _grid.Length);
        _allTiles.Clear();
        Score = currentScore;
        IsGameOver = false;
        HasWon = false;
        KeepPlaying = false;

        int nonZeroCount = 0;
        for (int r = 0; r < GridSize; r++)
        {
            for (int c = 0; c < GridSize; c++)
            {
                int val = savedGrid[r * GridSize + c];
                if (val > 0)
                {
                    var piece = new TilePiece(r, c, val);
                    _grid[r, c] = piece;
                    _allTiles.Add(piece);
                    nonZeroCount++;
                    if (val >= 2048) HasWon = true;
                }
            }
        }

        if (nonZeroCount == 0)
        {
            Reset();
        }
        else
        {
            CheckGameOver();
        }
    }

    public List<int> ExportGrid()
    {
        var list = new List<int>(GridSize * GridSize);
        for (int r = 0; r < GridSize; r++)
        {
            for (int c = 0; c < GridSize; c++)
            {
                list.Add(_grid[r, c]?.Value ?? 0);
            }
        }
        return list;
    }

    public bool Move(MoveDirection direction)
    {
        if (IsGameOver) return false;

        var snapshot = GameStateSnapshot.Capture(_grid, Score, HasWon, KeepPlaying);

        // Clean up previous merged/disposed tiles
        for (int i = _allTiles.Count - 1; i >= 0; i--)
        {
            var t = _allTiles[i];
            if (t.MergedInto != null)
            {
                _allTiles.RemoveAt(i);
            }
            else
            {
                t.PreviousRow = t.Row;
                t.PreviousCol = t.Col;
                t.IsNew = false;
                t.IsMerged = false;
            }
        }

        bool hasMoved = false;

        switch (direction)
        {
            case MoveDirection.Left:
                for (int r = 0; r < GridSize; r++)
                    if (SlideLine(r, 0, 0, 1)) hasMoved = true;
                break;

            case MoveDirection.Right:
                for (int r = 0; r < GridSize; r++)
                    if (SlideLine(r, GridSize - 1, 0, -1)) hasMoved = true;
                break;

            case MoveDirection.Up:
                for (int c = 0; c < GridSize; c++)
                    if (SlideLine(0, c, 1, 0)) hasMoved = true;
                break;

            case MoveDirection.Down:
                for (int c = 0; c < GridSize; c++)
                    if (SlideLine(GridSize - 1, c, -1, 0)) hasMoved = true;
                break;
        }

        if (hasMoved)
        {
            if (_undoHistory.Count >= MaxUndoHistory)
            {
                _undoHistory.RemoveAt(0);
            }
            _undoHistory.Add(snapshot);

            SpawnTile();
            CheckGameOver();

            if (Score > BestScore)
            {
                BestScore = Score;
            }

            return true;
        }

        return false;
    }

    public bool Undo()
    {
        if (_undoHistory.Count == 0) return false;

        var snapshot = _undoHistory[^1];
        _undoHistory.RemoveAt(_undoHistory.Count - 1);

        Array.Clear(_grid, 0, _grid.Length);
        _allTiles.Clear();
        Score = snapshot.Score;
        HasWon = snapshot.HasWon;
        KeepPlaying = snapshot.KeepPlaying;
        IsGameOver = false;

        for (int r = 0; r < GridSize; r++)
        {
            for (int c = 0; c < GridSize; c++)
            {
                int val = snapshot.Grid[r * GridSize + c];
                if (val > 0)
                {
                    var piece = new TilePiece(r, c, val, isNew: false);
                    _grid[r, c] = piece;
                    _allTiles.Add(piece);
                }
            }
        }

        CheckGameOver();
        return true;
    }

    private bool SlideLine(int startR, int startC, int dr, int dc)
    {
        bool moved = false;
        int targetR = startR;
        int targetC = startC;
        TilePiece? lastMerged = null;

        for (int step = 0; step < GridSize; step++)
        {
            int curR = startR + step * dr;
            int curC = startC + step * dc;

            TilePiece? current = _grid[curR, curC];
            if (current == null) continue;

            // Check if can merge with preceding target
            int prevTargetR = targetR - dr;
            int prevTargetC = targetC - dc;

            if (step > 0 && prevTargetR >= 0 && prevTargetR < GridSize &&
                prevTargetC >= 0 && prevTargetC < GridSize &&
                _grid[prevTargetR, prevTargetC] is { } dest &&
                dest.Value == current.Value &&
                dest != lastMerged)
            {
                // Merge current into destination
                _grid[curR, curC] = null;
                current.Row = prevTargetR;
                current.Col = prevTargetC;
                current.MergedInto = dest;

                dest.Value *= 2;
                dest.IsMerged = true;
                Score += dest.Value;
                lastMerged = dest;

                if (dest.Value >= 2048 && !HasWon)
                {
                    HasWon = true;
                }

                moved = true;
            }
            else
            {
                if (curR != targetR || curC != targetC)
                {
                    _grid[targetR, targetC] = current;
                    _grid[curR, curC] = null;
                    current.Row = targetR;
                    current.Col = targetC;
                    moved = true;
                }

                targetR += dr;
                targetC += dc;
            }
        }

        return moved;
    }

    private void SpawnTile()
    {
        var emptyCells = new List<(int r, int c)>(GridSize * GridSize);
        for (int r = 0; r < GridSize; r++)
        {
            for (int c = 0; c < GridSize; c++)
            {
                if (_grid[r, c] == null)
                {
                    emptyCells.Add((r, c));
                }
            }
        }

        if (emptyCells.Count == 0) return;

        var (row, col) = emptyCells[Random.Shared.Next(emptyCells.Count)];
        int val = Random.Shared.NextDouble() < 0.9 ? 2 : 4;
        var newPiece = new TilePiece(row, col, val, isNew: true);

        _grid[row, col] = newPiece;
        _allTiles.Add(newPiece);
    }

    private void CheckGameOver()
    {
        // 1. Any empty cells?
        for (int r = 0; r < GridSize; r++)
        {
            for (int c = 0; c < GridSize; c++)
            {
                if (_grid[r, c] == null)
                {
                    IsGameOver = false;
                    return;
                }
            }
        }

        // 2. Any adjacent horizontal matching neighbors?
        for (int r = 0; r < GridSize; r++)
        {
            for (int c = 0; c < GridSize - 1; c++)
            {
                if (_grid[r, c]!.Value == _grid[r, c + 1]!.Value)
                {
                    IsGameOver = false;
                    return;
                }
            }
        }

        // 3. Any adjacent vertical matching neighbors?
        for (int c = 0; c < GridSize; c++)
        {
            for (int r = 0; r < GridSize - 1; r++)
            {
                if (_grid[r, c]!.Value == _grid[r + 1, c]!.Value)
                {
                    IsGameOver = false;
                    return;
                }
            }
        }

        IsGameOver = true;
    }
}
