using MetroHub.Core.Models;

namespace MetroHub.Core.Services;

public static class GridPlacementService
{
    public const double GridStep = 64.0;
    public const double Gap = 8.0;
    public const double OriginX = 48.0;
    public const double OriginY = 40.0;
    public const double BaseSideMargin = 48.0;

    public static int MaxCols { get; set; } = 28;

    public static void UpdateMetrics(double viewportWidth)
    {
        double available = Math.Max(320, viewportWidth - OriginX - BaseSideMargin);
        MaxCols = Math.Max(4, (int)Math.Floor((available + Gap) / GridStep));
    }

    public static int GetMaxCols(double viewportWidth)
    {
        double available = Math.Max(320, viewportWidth - OriginX - BaseSideMargin);
        return Math.Max(4, (int)Math.Floor((available + Gap) / GridStep));
    }

    public static int ColFromPixel(double x)
    {
        return Math.Max(0, (int)Math.Round((x - OriginX) / GridStep));
    }

    public static int RowFromPixel(double y)
    {
        return Math.Max(0, (int)Math.Round((y - OriginY) / GridStep));
    }

    public static double PixelXFromCol(int col)
    {
        return OriginX + (col * GridStep);
    }

    public static double PixelYFromRow(int row)
    {
        return OriginY + (row * GridStep);
    }

    public static int GetCol(TileModel tile) => ColFromPixel(tile.X);
    public static int GetRow(TileModel tile) => RowFromPixel(tile.Y);

    public static bool DoTilesOverlap(int c1, int r1, int sx1, int sy1, int c2, int r2, int sx2, int sy2)
    {
        return c1 < c2 + sx2 &&
               c1 + sx1 > c2 &&
               r1 < r2 + sy2 &&
               r1 + sy1 > r2;
    }

    public static bool IsRegionFree(int col, int row, int spanX, int spanY, IEnumerable<TileModel> tiles, TileModel? ignoreTile = null, int maxCols = int.MaxValue)
    {
        if (col < 0 || row < 0) return false;
        if (col + spanX > maxCols) return false;

        foreach (var other in tiles)
        {
            if (ReferenceEquals(other, ignoreTile)) continue;

            int otherCol = GetCol(other);
            int otherRow = GetRow(other);

            if (DoTilesOverlap(col, row, spanX, spanY, otherCol, otherRow, other.SpanX, other.SpanY))
            {
                return false;
            }
        }

        return true;
    }

    public static List<TileModel> GetOverlappingTiles(int col, int row, int spanX, int spanY, IEnumerable<TileModel> tiles, TileModel? ignoreTile = null)
    {
        var result = new List<TileModel>();
        if (col < 0 || row < 0) return result;

        foreach (var other in tiles)
        {
            if (ReferenceEquals(other, ignoreTile)) continue;

            int otherCol = GetCol(other);
            int otherRow = GetRow(other);

            if (DoTilesOverlap(col, row, spanX, spanY, otherCol, otherRow, other.SpanX, other.SpanY))
            {
                result.Add(other);
            }
        }

        return result;
    }

    public static (int Col, int Row) FindNearestAvailableSlot(int startCol, int startRow, int spanX, int spanY, IEnumerable<TileModel> tiles, TileModel? ignoreTile = null, int maxCols = int.MaxValue)
    {
        startCol = Math.Max(0, Math.Min(startCol, Math.Max(0, maxCols - spanX)));
        startRow = Math.Max(0, startRow);

        if (IsRegionFree(startCol, startRow, spanX, spanY, tiles, ignoreTile, maxCols))
        {
            return (startCol, startRow);
        }

        // Search in expanding concentric rings
        for (int radius = 1; radius <= 40; radius++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Abs(dx) != radius && Math.Abs(dy) != radius) continue;

                    int c = startCol + dx;
                    int r = startRow + dy;

                    if (c < 0 || r < 0) continue;
                    if (c + spanX > maxCols) continue;

                    if (IsRegionFree(c, r, spanX, spanY, tiles, ignoreTile, maxCols))
                    {
                        return (c, r);
                    }
                }
            }
        }

        // Fallback: search downward row by row
        for (int r = startRow + 1; r < startRow + 50; r++)
        {
            for (int c = 0; c <= maxCols - spanX; c++)
            {
                if (IsRegionFree(c, r, spanX, spanY, tiles, ignoreTile, maxCols))
                {
                    return (c, r);
                }
            }
        }

        return (0, startRow + 2);
    }

    public static bool CanDisplace(TileModel tile) => !tile.IsLocked;

    public static List<TileModel> ResolveResizeExpansion(
        TileModel resizingTile,
        int oldSpanX,
        int oldSpanY,
        int newSpanX,
        int newSpanY,
        int maxCols,
        IList<TileModel> allTiles)
    {
        var modifiedTiles = new List<TileModel>();
        int col = GetCol(resizingTile);
        int row = GetRow(resizingTile);

        // If expanding horizontally pushes beyond maxCols, clamp target column
        if (col + newSpanX > maxCols)
        {
            int overflow = (col + newSpanX) - maxCols;
            col = Math.Max(0, col - overflow);
        }

        resizingTile.Col = col;
        resizingTile.Row = row;
        resizingTile.X = PixelXFromCol(col);
        resizingTile.Y = PixelYFromRow(row);
        modifiedTiles.Add(resizingTile);

        // Check for overlapping tiles
        var overlapping = allTiles
            .Where(t => !ReferenceEquals(t, resizingTile))
            .Where(t => DoTilesOverlap(col, row, newSpanX, newSpanY, GetCol(t), GetRow(t), t.SpanX, t.SpanY))
            .ToList();

        if (overlapping.Count == 0)
        {
            return modifiedTiles;
        }

        // Tier 1: Try Rightward Push (Same Row)
        if (TryPushRight(resizingTile, col, row, newSpanX, newSpanY, maxCols, allTiles, out var rightMoves))
        {
            foreach (var kvp in rightMoves)
            {
                var t = kvp.Key;
                t.Col = kvp.Value;
                t.X = PixelXFromCol(kvp.Value);
                modifiedTiles.Add(t);
            }
            return modifiedTiles;
        }

        // Tier 2: Try Shift Left (Self Growth Leftward)
        int deltaSpanX = newSpanX - oldSpanX;
        if (deltaSpanX > 0 && TryShiftLeft(resizingTile, col, row, newSpanX, newSpanY, deltaSpanX, maxCols, allTiles, out int leftCol))
        {
            resizingTile.Col = leftCol;
            resizingTile.X = PixelXFromCol(leftCol);
            return modifiedTiles;
        }

        // Tier 3: Column-Preserving Accordion Push Down
        var positions = new Dictionary<TileModel, (int Col, int Row)>();
        CascadePushDown(resizingTile, col, row, newSpanX, newSpanY, allTiles, positions);

        foreach (var kvp in positions)
        {
            var t = kvp.Key;
            t.Col = kvp.Value.Col;
            t.Row = kvp.Value.Row;
            t.X = PixelXFromCol(kvp.Value.Col);
            t.Y = PixelYFromRow(kvp.Value.Row);
            modifiedTiles.Add(t);
        }

        return modifiedTiles;
    }

    public static List<TileModel> ResolveBatchResizeExpansion(
        IList<TileModel> resizingTiles,
        int newSpanX,
        int newSpanY,
        int maxCols,
        IList<TileModel> allTiles)
    {
        var modifiedTiles = new List<TileModel>();
        if (resizingTiles == null || resizingTiles.Count == 0) return modifiedTiles;

        // Separate into shrinking tiles (both dimensions <= current) and expanding tiles
        var shrinking = resizingTiles.Where(t => newSpanX <= t.SpanX && newSpanY <= t.SpanY).ToList();
        var expanding = resizingTiles.Where(t => newSpanX > t.SpanX || newSpanY > t.SpanY).ToList();

        // 1. Process shrinking tiles first to liberate grid space
        foreach (var t in shrinking)
        {
            t.SpanX = newSpanX;
            t.SpanY = newSpanY;
            if (!modifiedTiles.Contains(t))
            {
                modifiedTiles.Add(t);
            }
        }

        // 2. Process expanding tiles right-to-left, bottom-to-top to avoid self-blocking
        var orderedExpanding = expanding
            .OrderByDescending(t => t.Col)
            .ThenByDescending(t => t.Row)
            .ToList();

        foreach (var t in orderedExpanding)
        {
            int oldX = t.SpanX;
            int oldY = t.SpanY;

            var displaced = ResolveResizeExpansion(t, oldX, oldY, newSpanX, newSpanY, maxCols, allTiles);
            t.SpanX = newSpanX;
            t.SpanY = newSpanY;

            foreach (var d in displaced)
            {
                if (!modifiedTiles.Contains(d))
                {
                    modifiedTiles.Add(d);
                }
            }
        }

        return modifiedTiles;
    }

    private static bool TryPushRight(
        TileModel mainTile,
        int mainCol,
        int mainRow,
        int mainSpanX,
        int mainSpanY,
        int maxCols,
        IList<TileModel> allTiles,
        out Dictionary<TileModel, int> shiftedCols)
    {
        shiftedCols = new Dictionary<TileModel, int>();

        var overlapping = allTiles
            .Where(t => !ReferenceEquals(t, mainTile))
            .Where(t => DoTilesOverlap(mainCol, mainRow, mainSpanX, mainSpanY, GetCol(t), GetRow(t), t.SpanX, t.SpanY))
            .ToList();

        if (overlapping.Count == 0) return true;

        // If any directly overlapping tile is locked, right push is blocked
        if (overlapping.Any(t => !CanDisplace(t))) return false;

        var queue = new Queue<TileModel>(overlapping);
        var visited = new HashSet<TileModel>(overlapping);

        foreach (var tile in overlapping)
        {
            int requiredCol = mainCol + mainSpanX;
            shiftedCols[tile] = requiredCol;
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            int newCol = shiftedCols[current];
            int r = GetRow(current);

            if (newCol + current.SpanX > maxCols)
            {
                return false; // Exceeds right edge of grid
            }

            foreach (var other in allTiles)
            {
                if (ReferenceEquals(other, mainTile) || ReferenceEquals(other, current)) continue;

                int otherCol = shiftedCols.TryGetValue(other, out int oc) ? oc : GetCol(other);
                int otherRow = GetRow(other);

                if (DoTilesOverlap(newCol, r, current.SpanX, current.SpanY, otherCol, otherRow, other.SpanX, other.SpanY))
                {
                    if (!CanDisplace(other))
                    {
                        return false; // Collides with an immovable/locked tile
                    }

                    int neededOtherCol = newCol + current.SpanX;
                    if (neededOtherCol > otherCol)
                    {
                        shiftedCols[other] = neededOtherCol;
                        if (!visited.Contains(other))
                        {
                            visited.Add(other);
                            queue.Enqueue(other);
                        }
                        else
                        {
                            queue.Enqueue(other);
                        }
                    }
                }
            }
        }

        foreach (var kvp in shiftedCols)
        {
            if (!CanDisplace(kvp.Key) || kvp.Value + kvp.Key.SpanX > maxCols)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryShiftLeft(
        TileModel mainTile,
        int currentCol,
        int currentRow,
        int targetSpanX,
        int targetSpanY,
        int deltaCols,
        int maxCols,
        IList<TileModel> allTiles,
        out int newCol)
    {
        newCol = currentCol;
        int candidateCol = currentCol - deltaCols;
        if (candidateCol < 0) return false;

        var overlapping = allTiles
            .Where(t => !ReferenceEquals(t, mainTile))
            .Where(t => DoTilesOverlap(candidateCol, currentRow, targetSpanX, targetSpanY, GetCol(t), GetRow(t), t.SpanX, t.SpanY))
            .ToList();

        if (overlapping.Count == 0)
        {
            newCol = candidateCol;
            return true;
        }

        return false;
    }

    private static void CascadePushDown(
        TileModel triggerTile,
        int triggerCol,
        int triggerRow,
        int triggerSpanX,
        int triggerSpanY,
        IList<TileModel> allTiles,
        Dictionary<TileModel, (int Col, int Row)> proposedPositions)
    {
        var directCollisions = allTiles
            .Where(t => !ReferenceEquals(t, triggerTile))
            .Where(t =>
            {
                int c = proposedPositions.TryGetValue(t, out var pos) ? pos.Col : GetCol(t);
                int r = proposedPositions.TryGetValue(t, out pos) ? pos.Row : GetRow(t);
                return DoTilesOverlap(triggerCol, triggerRow, triggerSpanX, triggerSpanY, c, r, t.SpanX, t.SpanY);
            })
            .ToList();

        var queue = new Queue<TileModel>(directCollisions);
        var inQueue = new HashSet<TileModel>(directCollisions);

        foreach (var tile in directCollisions)
        {
            int c = proposedPositions.TryGetValue(tile, out var pos) ? pos.Col : GetCol(tile);
            int minRow = triggerRow + triggerSpanY;

            if (!CanDisplace(tile)) continue;

            proposedPositions[tile] = (c, minRow);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            inQueue.Remove(current);

            var (curCol, curRow) = proposedPositions[current];

            foreach (var other in allTiles)
            {
                if (ReferenceEquals(other, triggerTile) || ReferenceEquals(other, current)) continue;

                int otherCol = proposedPositions.TryGetValue(other, out var oPos) ? oPos.Col : GetCol(other);
                int otherRow = proposedPositions.TryGetValue(other, out oPos) ? oPos.Row : GetRow(other);

                if (DoTilesOverlap(curCol, curRow, current.SpanX, current.SpanY, otherCol, otherRow, other.SpanX, other.SpanY))
                {
                    if (!CanDisplace(other))
                    {
                        // Other is locked: current jumps beneath other in its same column
                        int pushedCurRow = otherRow + other.SpanY;
                        proposedPositions[current] = (curCol, pushedCurRow);
                        if (!inQueue.Contains(current))
                        {
                            inQueue.Add(current);
                            queue.Enqueue(current);
                        }
                        break;
                    }
                    else
                    {
                        // Other is movable: push down in its column
                        int neededOtherRow = curRow + current.SpanY;
                        if (neededOtherRow > otherRow)
                        {
                            proposedPositions[other] = (otherCol, neededOtherRow);
                            if (!inQueue.Contains(other))
                            {
                                inQueue.Add(other);
                                queue.Enqueue(other);
                            }
                        }
                    }
                }
            }
        }
    }

    public static List<TileModel> PlaceAndResolveCollisions(
        TileModel draggedTile, 
        int targetCol, 
        int targetRow, 
        int originalCol, 
        int originalRow, 
        int maxCols, 
        IList<TileModel> allTiles)
    {
        var modifiedTiles = new List<TileModel>();
        int maxAllowedCol = Math.Max(0, maxCols - draggedTile.SpanX);
        targetCol = Math.Max(0, Math.Min(targetCol, maxAllowedCol));
        targetRow = Math.Max(0, targetRow);

        var overlapping = GetOverlappingTiles(targetCol, targetRow, draggedTile.SpanX, draggedTile.SpanY, allTiles, draggedTile);

        // If dropped back where it started and no collision with neighbors, simply re-snap
        if (targetCol == originalCol && targetRow == originalRow && overlapping.Count == 0)
        {
            draggedTile.Col = targetCol;
            draggedTile.Row = targetRow;
            draggedTile.X = PixelXFromCol(targetCol);
            draggedTile.Y = PixelYFromRow(targetRow);
            modifiedTiles.Add(draggedTile);
            return modifiedTiles;
        }

        if (overlapping.Count == 0)
        {
            // Case 1: Slot is completely free
            draggedTile.Col = targetCol;
            draggedTile.Row = targetRow;
            draggedTile.X = PixelXFromCol(targetCol);
            draggedTile.Y = PixelYFromRow(targetRow);
            modifiedTiles.Add(draggedTile);
            return modifiedTiles;
        }

        if (overlapping.Count == 1 && 
            overlapping[0].SpanX == draggedTile.SpanX && 
            overlapping[0].SpanY == draggedTile.SpanY && 
            CanDisplace(overlapping[0]) &&
            (targetCol != originalCol || targetRow != originalRow))
        {
            // Case 2: Clean 1-to-1 swap with tile of identical size!
            var targetTile = overlapping[0];
            int swapCol = GetCol(targetTile);
            int swapRow = GetRow(targetTile);

            draggedTile.Col = swapCol;
            draggedTile.Row = swapRow;
            draggedTile.X = PixelXFromCol(swapCol);
            draggedTile.Y = PixelYFromRow(swapRow);
            modifiedTiles.Add(draggedTile);

            targetTile.Col = originalCol;
            targetTile.Row = originalRow;
            targetTile.X = PixelXFromCol(originalCol);
            targetTile.Y = PixelYFromRow(originalRow);
            modifiedTiles.Add(targetTile);

            return modifiedTiles;
        }

        // Case 3: Multiple tiles, different size, or locked tile
        draggedTile.Col = targetCol;
        draggedTile.Row = targetRow;
        draggedTile.X = PixelXFromCol(targetCol);
        draggedTile.Y = PixelYFromRow(targetRow);
        modifiedTiles.Add(draggedTile);

        // Try gentle rightward push first
        if (TryPushRight(draggedTile, targetCol, targetRow, draggedTile.SpanX, draggedTile.SpanY, maxCols, allTiles, out var rightMoves))
        {
            foreach (var kvp in rightMoves)
            {
                var t = kvp.Key;
                t.Col = kvp.Value;
                t.X = PixelXFromCol(kvp.Value);
                modifiedTiles.Add(t);
            }
            return modifiedTiles;
        }

        // Fallback: column accordion cascade push down
        var positions = new Dictionary<TileModel, (int Col, int Row)>();
        CascadePushDown(draggedTile, targetCol, targetRow, draggedTile.SpanX, draggedTile.SpanY, allTiles, positions);

        foreach (var kvp in positions)
        {
            var t = kvp.Key;
            t.Col = kvp.Value.Col;
            t.Row = kvp.Value.Row;
            t.X = PixelXFromCol(kvp.Value.Col);
            t.Y = PixelYFromRow(kvp.Value.Row);
            modifiedTiles.Add(t);
        }

        return modifiedTiles;
    }

    public static List<TileModel> PlaceClusterAndResolveCollisions(
        IList<TileModel> clusterTiles,
        TileModel anchorTile,
        int anchorTargetCol,
        int anchorTargetRow,
        int anchorOrigCol,
        int anchorOrigRow,
        int maxCols,
        IList<TileModel> allTiles,
        Dictionary<TileModel, (int Col, int Row)>? origPositions = null)
    {
        var modifiedTiles = new List<TileModel>();
        if (clusterTiles == null || clusterTiles.Count == 0) return modifiedTiles;

        if (clusterTiles.Count == 1)
        {
            return PlaceAndResolveCollisions(clusterTiles[0], anchorTargetCol, anchorTargetRow, anchorOrigCol, anchorOrigRow, maxCols, allTiles);
        }

        // Calculate cluster relative boundaries
        int minRelCol = int.MaxValue;
        int maxRelCol = int.MinValue;
        int minRelRow = int.MaxValue;

        foreach (var t in clusterTiles)
        {
            int origC = (origPositions != null && origPositions.TryGetValue(t, out var pos)) ? pos.Col : GetCol(t);
            int origR = (origPositions != null && origPositions.TryGetValue(t, out pos)) ? pos.Row : GetRow(t);

            int relC = origC - anchorOrigCol;
            int relR = origR - anchorOrigRow;

            minRelCol = Math.Min(minRelCol, relC);
            maxRelCol = Math.Max(maxRelCol, relC + t.SpanX);
            minRelRow = Math.Min(minRelRow, relR);
        }

        // Clamp anchor target so the ENTIRE cluster stays strictly within grid bounds
        int minAllowedAnchorCol = -minRelCol;
        int maxAllowedAnchorCol = Math.Max(minAllowedAnchorCol, maxCols - maxRelCol);
        anchorTargetCol = Math.Clamp(anchorTargetCol, minAllowedAnchorCol, maxAllowedAnchorCol);
        anchorTargetRow = Math.Max(-minRelRow, anchorTargetRow);

        var clusterSet = new HashSet<TileModel>(clusterTiles);

        // Position all cluster tiles in rigid formation
        foreach (var tile in clusterTiles)
        {
            int origC = (origPositions != null && origPositions.TryGetValue(tile, out var pos)) ? pos.Col : GetCol(tile);
            int origR = (origPositions != null && origPositions.TryGetValue(tile, out pos)) ? pos.Row : GetRow(tile);

            int targetC = anchorTargetCol + (origC - anchorOrigCol);
            int targetR = anchorTargetRow + (origR - anchorOrigRow);

            tile.Col = targetC;
            tile.Row = targetR;
            tile.X = PixelXFromCol(targetC);
            tile.Y = PixelYFromRow(targetR);
            modifiedTiles.Add(tile);
        }

        // Identify collisions with non-cluster tiles
        var nonClusterTiles = allTiles.Where(t => !clusterSet.Contains(t)).ToList();
        var proposedPositions = new Dictionary<TileModel, (int Col, int Row)>();
        var queue = new Queue<TileModel>();
        var inQueue = new HashSet<TileModel>();

        foreach (var obstacle in nonClusterTiles)
        {
            int obsCol = GetCol(obstacle);
            int obsRow = GetRow(obstacle);

            foreach (var cTile in clusterTiles)
            {
                if (DoTilesOverlap(cTile.Col, cTile.Row, cTile.SpanX, cTile.SpanY, obsCol, obsRow, obstacle.SpanX, obstacle.SpanY))
                {
                    int pushedRow = cTile.Row + cTile.SpanY;
                    if (proposedPositions.TryGetValue(obstacle, out var curPos))
                    {
                        pushedRow = Math.Max(curPos.Row, pushedRow);
                    }
                    proposedPositions[obstacle] = (obsCol, pushedRow);
                    if (!inQueue.Contains(obstacle))
                    {
                        inQueue.Add(obstacle);
                        queue.Enqueue(obstacle);
                    }
                }
            }
        }

        // Cascade downward in columns for any secondary collisions
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            inQueue.Remove(current);

            var (curCol, curRow) = proposedPositions[current];

            // Re-verify against cluster tiles at new row
            foreach (var cTile in clusterTiles)
            {
                if (DoTilesOverlap(cTile.Col, cTile.Row, cTile.SpanX, cTile.SpanY, curCol, curRow, current.SpanX, current.SpanY))
                {
                    int neededRow = cTile.Row + cTile.SpanY;
                    if (neededRow > curRow)
                    {
                        curRow = neededRow;
                        proposedPositions[current] = (curCol, curRow);
                    }
                }
            }

            // Verify against other non-cluster tiles
            foreach (var other in nonClusterTiles)
            {
                if (ReferenceEquals(other, current)) continue;

                int otherCol = proposedPositions.TryGetValue(other, out var oPos) ? oPos.Col : GetCol(other);
                int otherRow = proposedPositions.TryGetValue(other, out oPos) ? oPos.Row : GetRow(other);

                if (DoTilesOverlap(curCol, curRow, current.SpanX, current.SpanY, otherCol, otherRow, other.SpanX, other.SpanY))
                {
                    int neededOtherRow = curRow + current.SpanY;
                    if (neededOtherRow > otherRow)
                    {
                        proposedPositions[other] = (otherCol, neededOtherRow);
                        if (!inQueue.Contains(other))
                        {
                            inQueue.Add(other);
                            queue.Enqueue(other);
                        }
                    }
                }
            }
        }

        // Apply final positions to displaced tiles
        foreach (var kvp in proposedPositions)
        {
            var t = kvp.Key;
            t.Col = kvp.Value.Col;
            t.Row = kvp.Value.Row;
            t.X = PixelXFromCol(kvp.Value.Col);
            t.Y = PixelYFromRow(kvp.Value.Row);
            modifiedTiles.Add(t);
        }

        return modifiedTiles;
    }

    public static void SanitizeAndSnapAll(IList<TileModel> tiles, int maxCols)
    {
        var placedTiles = new List<TileModel>();
        var orderedTiles = tiles.OrderByDescending(t => t.IsLocked).ToList();

        foreach (var tile in orderedTiles)
        {
            int col = Math.Min(Math.Max(0, ColFromPixel(tile.X)), Math.Max(0, maxCols - tile.SpanX));
            int row = Math.Max(0, RowFromPixel(tile.Y));

            var (freeCol, freeRow) = FindNearestAvailableSlot(col, row, tile.SpanX, tile.SpanY, placedTiles, null, maxCols);

            tile.Col = freeCol;
            tile.Row = freeRow;
            tile.X = PixelXFromCol(freeCol);
            tile.Y = PixelYFromRow(freeRow);

            placedTiles.Add(tile);
        }
    }
}
