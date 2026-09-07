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
        Dictionary<TileModel, (int Col, int Row)>? origPositions = null,
        bool isGroupCluster = false)
    {
        var modifiedTiles = new List<TileModel>();
        if (clusterTiles == null || clusterTiles.Count == 0) return modifiedTiles;

        if (clusterTiles.Count == 1 && !isGroupCluster)
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

        int minAllowedAnchorRow = isGroupCluster ? Math.Max(1, Math.Max(-minRelRow, 1 - minRelRow)) : Math.Max(0, -minRelRow);
        anchorTargetRow = Math.Max(minAllowedAnchorRow, anchorTargetRow);

        var clusterSet = new HashSet<TileModel>(clusterTiles);

        // Position all cluster tiles in rigid formation
        foreach (var tile in clusterTiles)
        {
            int origC = (origPositions != null && origPositions.TryGetValue(tile, out var pos)) ? pos.Col : GetCol(tile);
            int origR = (origPositions != null && origPositions.TryGetValue(tile, out pos)) ? pos.Row : GetRow(tile);

            int targetC = Math.Max(0, Math.Min(anchorTargetCol + (origC - anchorOrigCol), maxCols - tile.SpanX));
            int targetR = Math.Max(isGroupCluster ? 1 : 0, anchorTargetRow + (origR - anchorOrigRow));

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

        int clusterMinCol = clusterTiles.Min(t => t.Col);
        int clusterMaxCol = clusterTiles.Max(t => t.Col + t.SpanX);
        int clusterMinRow = clusterTiles.Min(t => t.Row);
        int clusterMaxRow = clusterTiles.Max(t => t.Row + t.SpanY);
        int groupHeaderRow = clusterMinRow - 1;
        int groupHeaderSpanX = clusterMaxCol - clusterMinCol;

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

            if (isGroupCluster)
            {
                if (DoTilesOverlap(clusterMinCol, groupHeaderRow, groupHeaderSpanX, 1, obsCol, obsRow, obstacle.SpanX, obstacle.SpanY))
                {
                    int pushedRow = clusterMaxRow;
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

            if (isGroupCluster && DoTilesOverlap(clusterMinCol, groupHeaderRow, groupHeaderSpanX, 1, curCol, curRow, current.SpanX, current.SpanY))
            {
                int neededRow = clusterMaxRow;
                if (neededRow > curRow)
                {
                    curRow = neededRow;
                    proposedPositions[current] = (curCol, curRow);
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

    #region Atomic Group Container & Column Architecture

    public const int GroupColWidth = 8;

    public static int GetColumnStartCol(int columnIndex)
    {
        return columnIndex * GroupColWidth;
    }

    public static int GetColumnIndexFromCol(int col)
    {
        return Math.Max(0, col / GroupColWidth);
    }

    public static (int MinCol, int MaxCol, int MinRow, int MaxRow) GetGroupBoundingBox(TileGroupModel group, IEnumerable<TileModel> allTiles)
    {
        int minCol = group.Col;
        int maxCol = group.Col + GroupColWidth;
        int minRow = group.Row;
        int maxRow = group.Row + 1;

        var members = allTiles.Where(t => t.Group == group.Id).ToList();
        if (members.Count > 0)
        {
            maxRow = Math.Max(maxRow, members.Max(t => t.Row + t.SpanY));
        }

        return (minCol, maxCol, minRow, maxRow);
    }

    /// <summary>
    /// Packs the member tiles of a group gaplessly from left-to-right, top-to-bottom within the 8-unit width.
    /// Returns the bottom-most row used by the member tiles.
    /// </summary>
    public static int PackGroupTiles(
        TileGroupModel group,
        IList<TileModel> memberTiles,
        int startRow,
        IList<TileModel>? modifiedList = null)
    {
        startRow = Math.Max(1, startRow);
        if (memberTiles == null || memberTiles.Count == 0)
        {
            return startRow;
        }

        int groupStartCol = group.Col;
        int maxRowReached = startRow;

        // Stable sort so tiles are evaluated in row-major order
        var orderedTiles = memberTiles
            .OrderBy(t => t.Row)
            .ThenBy(t => t.Col)
            .ToList();

        // Track occupied grid cells relative to (groupStartCol, startRow)
        var occupied = new HashSet<(int RelCol, int RelRow)>();

        foreach (var tile in orderedTiles)
        {
            int spanX = Math.Clamp(tile.SpanX, 1, GroupColWidth);
            int spanY = Math.Max(1, tile.SpanY);

            int targetRelCol = -1;
            int targetRelRow = -1;

            for (int r = 0; r < 200; r++)
            {
                for (int c = 0; c <= GroupColWidth - spanX; c++)
                {
                    bool fits = true;
                    for (int dy = 0; dy < spanY; dy++)
                    {
                        for (int dx = 0; dx < spanX; dx++)
                        {
                            if (occupied.Contains((c + dx, r + dy)))
                            {
                                fits = false;
                                break;
                            }
                        }
                        if (!fits) break;
                    }

                    if (fits)
                    {
                        targetRelCol = c;
                        targetRelRow = r;
                        break;
                    }
                }
                if (targetRelCol != -1) break;
            }

            if (targetRelCol == -1)
            {
                targetRelCol = 0;
                targetRelRow = 0;
            }

            for (int dy = 0; dy < spanY; dy++)
            {
                for (int dx = 0; dx < spanX; dx++)
                {
                    occupied.Add((targetRelCol + dx, targetRelRow + dy));
                }
            }

            int finalCol = Math.Max(0, groupStartCol + targetRelCol);
            int finalRow = Math.Max(1, startRow + targetRelRow);
            double finalX = PixelXFromCol(finalCol);
            double finalY = PixelYFromRow(finalRow);

            if (tile.Col != finalCol || tile.Row != finalRow || Math.Abs(tile.X - finalX) > 0.5 || Math.Abs(tile.Y - finalY) > 0.5)
            {
                tile.Col = finalCol;
                tile.Row = finalRow;
                tile.X = finalX;
                tile.Y = finalY;
                if (modifiedList != null && !modifiedList.Contains(tile))
                {
                    modifiedList.Add(tile);
                }
            }

            maxRowReached = Math.Max(maxRowReached, finalRow + spanY);
        }

        return maxRowReached;
    }

    /// <summary>
    /// Stacks all groups in a column deterministically by OrderIndex.
    /// If collapsed, lower groups slide up directly beneath the header.
    /// Returns any tiles whose coordinates were modified.
    /// </summary>
    public static List<TileModel> ReflowColumnGroups(
        int columnIndex,
        IList<TileGroupModel> groups,
        IList<TileModel> allTiles,
        int compactVerticalSpacing = 1)
    {
        var modifiedTiles = new List<TileModel>();
        int colStart = GetColumnStartCol(columnIndex);

        var colGroups = groups
            .Where(g => g.ColumnIndex == columnIndex)
            .OrderBy(g => g.OrderIndex)
            .ToList();

        for (int i = 0; i < colGroups.Count; i++)
        {
            colGroups[i].OrderIndex = i;
        }

        int currentRow = 0;

        foreach (var group in colGroups)
        {
            group.Col = colStart;
            group.Row = currentRow;
            group.X = PixelXFromCol(colStart);
            group.Y = PixelYFromRow(currentRow) + 8;

            var memberTiles = allTiles.Where(t => t.Group == group.Id).ToList();

            int bottomRow = PackGroupTiles(group, memberTiles, startRow: currentRow + 1, modifiedList: modifiedTiles);

            currentRow = bottomRow + compactVerticalSpacing;
        }

        return modifiedTiles;
    }

    public static List<TileModel> ReflowAllGroups(
        IList<TileGroupModel> groups,
        IList<TileModel> allTiles)
    {
        var modified = new List<TileModel>();
        if (groups == null || groups.Count == 0) return modified;

        var columnIndices = groups.Select(g => g.ColumnIndex).Distinct().ToList();
        if (columnIndices.Count == 0) columnIndices.Add(0);

        foreach (var colIdx in columnIndices)
        {
            var list = ReflowColumnGroups(colIdx, groups, allTiles);
            foreach (var t in list)
            {
                if (!modified.Contains(t)) modified.Add(t);
            }
        }

        return modified;
    }

    public static List<TileModel> ResolveGroupReorder(
        TileGroupModel draggedGroup,
        int targetColumnIndex,
        int targetOrderIndex,
        IList<TileGroupModel> groups,
        IList<TileModel> allTiles)
    {
        int oldColIndex = draggedGroup.ColumnIndex;

        // Remove from old column list
        var oldColGroups = groups.Where(g => g.ColumnIndex == oldColIndex && !ReferenceEquals(g, draggedGroup)).OrderBy(g => g.OrderIndex).ToList();
        for (int i = 0; i < oldColGroups.Count; i++)
        {
            oldColGroups[i].OrderIndex = i;
        }

        draggedGroup.ColumnIndex = targetColumnIndex;

        // Insert into target column list
        var targetColGroups = groups.Where(g => g.ColumnIndex == targetColumnIndex && !ReferenceEquals(g, draggedGroup)).OrderBy(g => g.OrderIndex).ToList();
        int insertIdx = Math.Clamp(targetOrderIndex, 0, targetColGroups.Count);
        targetColGroups.Insert(insertIdx, draggedGroup);

        for (int i = 0; i < targetColGroups.Count; i++)
        {
            targetColGroups[i].OrderIndex = i;
        }

        var modified = new List<TileModel>();

        if (oldColIndex != targetColumnIndex)
        {
            var m1 = ReflowColumnGroups(oldColIndex, groups, allTiles);
            modified.AddRange(m1);
        }

        var m2 = ReflowColumnGroups(targetColumnIndex, groups, allTiles);
        foreach (var t in m2)
        {
            if (!modified.Contains(t)) modified.Add(t);
        }

        return modified;
    }

    public static bool CleanEmptyGroups(IList<TileGroupModel> groups, IList<TileModel> allTiles)
    {
        var emptyGroups = groups.Where(g => !allTiles.Any(t => t.Group == g.Id)).ToList();
        if (emptyGroups.Count == 0) return false;

        foreach (var eg in emptyGroups)
        {
            groups.Remove(eg);
        }

        return true;
    }

    /// <summary>
    /// Calculates the total vertical footprint of a group (1 header row + rows occupied by packed members).
    /// </summary>
    public static int CalculateGroupHeightRows(TileGroupModel group, IEnumerable<TileModel> allTiles)
    {
        var members = allTiles.Where(t => t.Group == group.Id)
            .OrderBy(t => t.Row)
            .ThenBy(t => t.Col)
            .ToList();
        if (members.Count == 0) return 1;

        var occupied = new HashSet<(int RelCol, int RelRow)>();
        int maxRelRowReached = 0;

        foreach (var tile in members)
        {
            int spanX = Math.Clamp(tile.SpanX, 1, GroupColWidth);
            int spanY = Math.Max(1, tile.SpanY);

            int targetRelCol = -1;
            int targetRelRow = -1;

            for (int r = 0; r < 200; r++)
            {
                for (int c = 0; c <= GroupColWidth - spanX; c++)
                {
                    bool fits = true;
                    for (int dy = 0; dy < spanY; dy++)
                    {
                        for (int dx = 0; dx < spanX; dx++)
                        {
                            if (occupied.Contains((c + dx, r + dy)))
                            {
                                fits = false;
                                break;
                            }
                        }
                        if (!fits) break;
                    }

                    if (fits)
                    {
                        targetRelCol = c;
                        targetRelRow = r;
                        break;
                    }
                }
                if (targetRelCol != -1) break;
            }

            if (targetRelCol == -1)
            {
                targetRelCol = 0;
                targetRelRow = 0;
            }

            for (int dy = 0; dy < spanY; dy++)
            {
                for (int dx = 0; dx < spanX; dx++)
                {
                    occupied.Add((targetRelCol + dx, targetRelRow + dy));
                }
            }

            maxRelRowReached = Math.Max(maxRelRowReached, targetRelRow + spanY);
        }

        return 1 + maxRelRowReached;
    }

    /// <summary>
    /// Finds the exact target row for inserting a group in a column based on cursor Y.
    /// Dynamically snaps above, between, or below both ungrouped tiles and other groups.
    /// </summary>
    public static int FindInsertionRow(
        int columnIndex,
        double mouseY,
        IEnumerable<TileGroupModel> groups,
        IEnumerable<TileModel> allTiles,
        TileGroupModel? excludeGroup)
    {
        int colStart = GetColumnStartCol(columnIndex);
        int colEnd = colStart + GroupColWidth;

        var otherGroups = groups
            .Where(g => g.ColumnIndex == columnIndex && !ReferenceEquals(g, excludeGroup))
            .ToList();

        var ungroupedTiles = allTiles
            .Where(t => t.Group == null && t.Col < colEnd && (t.Col + t.SpanX) > colStart)
            .ToList();

        if (otherGroups.Count == 0 && ungroupedTiles.Count == 0)
        {
            return 0;
        }

        var items = new List<(int MinRow, int MaxRow)>();

        foreach (var g in otherGroups)
        {
            var (_, _, minR, maxR) = GetGroupBoundingBox(g, allTiles);
            items.Add((Math.Max(0, minR), Math.Max(1, maxR)));
        }

        foreach (var t in ungroupedTiles)
        {
            items.Add((Math.Max(0, t.Row), Math.Max(1, t.Row + t.SpanY)));
        }

        items = items.OrderBy(x => x.MinRow).ThenBy(x => x.MaxRow).ToList();

        int minExistingRow = Math.Max(0, items.Min(x => x.MinRow));
        int maxExistingRow = Math.Max(1, items.Max(x => x.MaxRow));

        double firstItemTopY = PixelYFromRow(minExistingRow);
        if (mouseY < firstItemTopY + 20)
        {
            return 0;
        }

        double lastItemBottomY = PixelYFromRow(maxExistingRow);
        if (mouseY >= lastItemBottomY)
        {
            return Math.Max(0, maxExistingRow);
        }

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            double topY = PixelYFromRow(item.MinRow);
            double bottomY = PixelYFromRow(item.MaxRow);
            double midY = (topY + bottomY) / 2.0;

            if (mouseY < midY)
            {
                return Math.Max(0, item.MinRow);
            }

            if (i + 1 < items.Count)
            {
                var nextItem = items[i + 1];
                double nextTopY = PixelYFromRow(nextItem.MinRow);
                if (mouseY >= bottomY && mouseY < nextTopY)
                {
                    return Math.Max(0, item.MaxRow);
                }
            }
        }

        return Math.Max(0, maxExistingRow);
    }

    /// <summary>
    /// Inserts a group at targetRow in targetColIndex, using a robust 2D bounding-box
    /// algorithm to dynamically push down any intersecting groups and ungrouped tiles.
    /// </summary>
    public static List<TileModel> InsertGroupAndResolveCollisions(
        TileGroupModel draggedGroup,
        int targetColIndex,
        int targetRow,
        IList<TileGroupModel> groups,
        IList<TileModel> allTiles,
        int compactVerticalSpacing = 1)
    {
        var modifiedTiles = new List<TileModel>();

        targetColIndex = Math.Max(0, targetColIndex);
        targetRow = Math.Max(0, targetRow);
        int targetColStart = GetColumnStartCol(targetColIndex);

        // 1. Position the group header
        draggedGroup.ColumnIndex = targetColIndex;
        draggedGroup.Col = targetColStart;
        draggedGroup.Row = targetRow;
        draggedGroup.X = PixelXFromCol(targetColStart);
        draggedGroup.Y = PixelYFromRow(targetRow) + 8;

        // 2. Pack its tiles gaplessly to determine its total footprint (tiles start at targetRow + 1 >= 1)
        var memberTiles = allTiles.Where(t => t.Group == draggedGroup.Id).ToList();
        int bottomRow = PackGroupTiles(draggedGroup, memberTiles, startRow: Math.Max(1, targetRow + 1), modifiedList: modifiedTiles);

        int groupHeight = bottomRow - targetRow;
        int groupSpanX = GroupColWidth;

        // 3. 2D Cascade Push Down for any intersecting objects
        var proposedGroupPositions = new Dictionary<TileGroupModel, (int Col, int Row)>();
        var proposedTilePositions = new Dictionary<TileModel, (int Col, int Row)>();

        var groupQueue = new Queue<TileGroupModel>();
        var tileQueue = new Queue<TileModel>();
        var inGroupQueue = new HashSet<TileGroupModel>();
        var inTileQueue = new HashSet<TileModel>();

        // Check initial collisions with the newly placed group
        foreach (var otherG in groups)
        {
            if (ReferenceEquals(otherG, draggedGroup)) continue;

            var (minC, maxC, minR, maxR) = GetGroupBoundingBox(otherG, allTiles);
            if (DoTilesOverlap(targetColStart, targetRow, groupSpanX, groupHeight, minC, minR, maxC - minC, maxR - minR))
            {
                int pushedRow = targetRow + groupHeight + compactVerticalSpacing;
                proposedGroupPositions[otherG] = (otherG.Col, pushedRow);
                inGroupQueue.Add(otherG);
                groupQueue.Enqueue(otherG);
            }
        }

        foreach (var ut in allTiles.Where(t => t.Group == null))
        {
            if (DoTilesOverlap(targetColStart, targetRow, groupSpanX, groupHeight, ut.Col, ut.Row, ut.SpanX, ut.SpanY))
            {
                int pushedRow = targetRow + groupHeight + compactVerticalSpacing;
                proposedTilePositions[ut] = (ut.Col, pushedRow);
                inTileQueue.Add(ut);
                tileQueue.Enqueue(ut);
            }
        }

        // Cascade
        while (groupQueue.Count > 0 || tileQueue.Count > 0)
        {
            if (groupQueue.Count > 0)
            {
                var curG = groupQueue.Dequeue();
                inGroupQueue.Remove(curG);
                var pos = proposedGroupPositions[curG];

                int curGHeight = CalculateGroupHeightRows(curG, allTiles);

                foreach (var otherG in groups)
                {
                    if (ReferenceEquals(otherG, draggedGroup) || ReferenceEquals(otherG, curG)) continue;

                    var oPos = proposedGroupPositions.TryGetValue(otherG, out var op) ? op : (otherG.Col, otherG.Row);
                    int oHeight = CalculateGroupHeightRows(otherG, allTiles);

                    if (DoTilesOverlap(pos.Col, pos.Row, GroupColWidth, curGHeight, oPos.Col, oPos.Row, GroupColWidth, oHeight))
                    {
                        int neededRow = pos.Row + curGHeight + compactVerticalSpacing;
                        if (neededRow > oPos.Row)
                        {
                            proposedGroupPositions[otherG] = (oPos.Col, neededRow);
                            if (!inGroupQueue.Contains(otherG))
                            {
                                inGroupQueue.Add(otherG);
                                groupQueue.Enqueue(otherG);
                            }
                        }
                    }
                }

                foreach (var ut in allTiles.Where(t => t.Group == null))
                {
                    var oPos = proposedTilePositions.TryGetValue(ut, out var op) ? op : (ut.Col, ut.Row);

                    if (DoTilesOverlap(pos.Col, pos.Row, GroupColWidth, curGHeight, oPos.Col, oPos.Row, ut.SpanX, ut.SpanY))
                    {
                        int neededRow = pos.Row + curGHeight + compactVerticalSpacing;
                        if (neededRow > oPos.Row)
                        {
                            proposedTilePositions[ut] = (oPos.Col, neededRow);
                            if (!inTileQueue.Contains(ut))
                            {
                                inTileQueue.Add(ut);
                                tileQueue.Enqueue(ut);
                            }
                        }
                    }
                }
            }

            if (tileQueue.Count > 0)
            {
                var curT = tileQueue.Dequeue();
                inTileQueue.Remove(curT);
                var pos = proposedTilePositions[curT];

                foreach (var otherT in allTiles.Where(t => t.Group == null))
                {
                    if (ReferenceEquals(otherT, curT)) continue;
                    var oPos = proposedTilePositions.TryGetValue(otherT, out var op) ? op : (otherT.Col, otherT.Row);

                    if (DoTilesOverlap(pos.Col, pos.Row, curT.SpanX, curT.SpanY, oPos.Col, oPos.Row, otherT.SpanX, otherT.SpanY))
                    {
                        int neededRow = pos.Row + curT.SpanY;
                        if (neededRow > oPos.Row)
                        {
                            proposedTilePositions[otherT] = (oPos.Col, neededRow);
                            if (!inTileQueue.Contains(otherT))
                            {
                                inTileQueue.Add(otherT);
                                tileQueue.Enqueue(otherT);
                            }
                        }
                    }
                }

                foreach (var otherG in groups)
                {
                    if (ReferenceEquals(otherG, draggedGroup)) continue;
                    var oPos = proposedGroupPositions.TryGetValue(otherG, out var op) ? op : (otherG.Col, otherG.Row);
                    int oHeight = CalculateGroupHeightRows(otherG, allTiles);

                    if (DoTilesOverlap(pos.Col, pos.Row, curT.SpanX, curT.SpanY, oPos.Col, oPos.Row, GroupColWidth, oHeight))
                    {
                        int neededRow = pos.Row + curT.SpanY + compactVerticalSpacing;
                        if (neededRow > oPos.Row)
                        {
                            proposedGroupPositions[otherG] = (oPos.Col, neededRow);
                            if (!inGroupQueue.Contains(otherG))
                            {
                                inGroupQueue.Add(otherG);
                                groupQueue.Enqueue(otherG);
                            }
                        }
                    }
                }
            }
        }

        // Apply proposed positions
        foreach (var kvp in proposedGroupPositions)
        {
            var g = kvp.Key;
            g.Row = Math.Max(0, kvp.Value.Row);
            g.Y = PixelYFromRow(g.Row) + 8;

            var gTiles = allTiles.Where(t => t.Group == g.Id).ToList();
            PackGroupTiles(g, gTiles, startRow: Math.Max(1, g.Row + 1), modifiedList: modifiedTiles);
        }

        foreach (var kvp in proposedTilePositions)
        {
            var t = kvp.Key;
            t.Row = Math.Max(0, kvp.Value.Row);
            t.Y = PixelYFromRow(t.Row);
            if (!modifiedTiles.Contains(t)) modifiedTiles.Add(t);
        }

        // Normalize OrderIndex for groups
        var colIndices = groups.Select(g => g.ColumnIndex).Distinct().ToList();
        foreach (var cIdx in colIndices)
        {
            var colGroups = groups.Where(g => g.ColumnIndex == cIdx).OrderBy(g => g.Row).ToList();
            for (int i = 0; i < colGroups.Count; i++)
            {
                colGroups[i].OrderIndex = i;
            }
        }

        return modifiedTiles;
    }

    #endregion
}
