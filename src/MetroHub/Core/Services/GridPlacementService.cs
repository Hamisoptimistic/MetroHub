using MetroHub.Core.Models;

namespace MetroHub.Core.Services;

public static class GridPlacementService
{
    public const double GridStep = 64.0;
    public const double Gap = 8.0;
    public const double OriginX = 16.0;
    public const double OriginY = 12.0;
    public const double BaseSideMargin = 24.0;
    public const int GroupColWidth = 8;
    public const double ColumnGap = 32.0;

    public static int MaxCols { get; set; } = 28;

    public static void UpdateMetrics(double viewportWidth)
    {
        double available = Math.Max(320, viewportWidth - OriginX - BaseSideMargin);
        double stride = (GroupColWidth * GridStep) + ColumnGap;
        int numTracks = Math.Max(1, (int)Math.Floor((available + ColumnGap) / stride));
        MaxCols = Math.Max(GroupColWidth, numTracks * GroupColWidth);
    }

    public static int GetMaxCols(double viewportWidth)
    {
        double available = Math.Max(320, viewportWidth - OriginX - BaseSideMargin);
        double stride = (GroupColWidth * GridStep) + ColumnGap;
        int numTracks = Math.Max(1, (int)Math.Floor((available + ColumnGap) / stride));
        return Math.Max(GroupColWidth, numTracks * GroupColWidth);
    }

    public static int ColFromPixel(double x)
    {
        double relX = Math.Max(0, x - OriginX);
        double stride = (GroupColWidth * GridStep) + ColumnGap;
        int colIdx = (int)Math.Max(0, Math.Floor((relX + (ColumnGap / 2.0)) / stride));
        double intraX = relX - (colIdx * stride);
        int localCol = Math.Clamp((int)Math.Round(intraX / GridStep), 0, GroupColWidth - 1);
        return (colIdx * GroupColWidth) + localCol;
    }

    public static int RowFromPixel(double y)
    {
        return Math.Max(0, (int)Math.Round((y - OriginY) / GridStep));
    }

    public static double PixelXFromCol(int col)
    {
        if (col <= 0) return OriginX;
        int colIdx = col / GroupColWidth;
        int localCol = col % GroupColWidth;
        return OriginX + (colIdx * ((GroupColWidth * GridStep) + ColumnGap)) + (localCol * GridStep);
    }

    public static double PixelYFromRow(int row)
    {
        return OriginY + (row * GridStep);
    }

    public static int GetCol(TileModel tile) => (tile.X > 0 || tile.Col == 0) ? ColFromPixel(tile.X) : tile.Col;
    public static int GetRow(TileModel tile) => (tile.Y > 0 || tile.Row == 0) ? RowFromPixel(tile.Y) : tile.Row;

    public static bool DoTilesOverlap(int c1, int r1, int sx1, int sy1, int c2, int r2, int sx2, int sy2)
    {
        return c1 < c2 + sx2 &&
               c1 + sx1 > c2 &&
               r1 < r2 + sy2 &&
               r1 + sy1 > r2;
    }

    public static bool IsRegionFree(
        int col,
        int row,
        int spanX,
        int spanY,
        IEnumerable<TileModel> tiles,
        TileModel? ignoreTile = null,
        int maxCols = int.MaxValue,
        IEnumerable<TileGroupModel>? groups = null)
    {
        if (col < 0 || row < 1) return false;
        if (col + spanX > maxCols) return false;

        // When groups are present and this check is for a loose canvas tile,
        // enforce that it does not overlap any group and does not occupy the 1x1 perimeter gap around any group.
        if (groups != null && groups.Any() && (ignoreTile == null || string.IsNullOrEmpty(ignoreTile.Group)))
        {
            foreach (var g in groups)
            {
                var members = tiles?.Where(t => t.Group == g.Id && !ReferenceEquals(t, ignoreTile)).ToList();
                int gMinC = g.Col;
                int gMaxC = g.Col + GroupColWidth;
                int gMinR = g.Row;
                int gMaxR = (members != null && members.Count > 0) ? members.Max(t => t.Row + t.SpanY) : g.Row + 1;

                // 1. Must not overlap group bounding box
                if (DoTilesOverlap(col, row, spanX, spanY, gMinC, gMinR, gMaxC - gMinC, gMaxR - gMinR))
                {
                    return false;
                }

                // 2. Must not occupy 1x1 perimeter gap buffer around this group
                // Bottom gap (row == gMaxR, within group horizontal span)
                if (row == gMaxR && col < gMaxC && (col + spanX) > gMinC) return false;

                // Top gap (row + spanY == gMinR, within group horizontal span)
                if (gMinR > 0 && (row + spanY) == gMinR && col < gMaxC && (col + spanX) > gMinC) return false;
            }
        }

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

    public static (int Col, int Row) FindNearestAvailableSlot(
        int startCol,
        int startRow,
        int spanX,
        int spanY,
        IEnumerable<TileModel> tiles,
        TileModel? ignoreTile = null,
        int maxCols = int.MaxValue,
        IEnumerable<TileGroupModel>? groups = null)
    {
        startCol = Math.Max(0, Math.Min(startCol, Math.Max(0, maxCols - spanX)));
        startRow = Math.Max(1, startRow);

        if (IsRegionFree(startCol, startRow, spanX, spanY, tiles, ignoreTile, maxCols, groups))
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

                    if (c < 0 || r < 1) continue;
                    if (c + spanX > maxCols) continue;

                    if (IsRegionFree(c, r, spanX, spanY, tiles, ignoreTile, maxCols, groups))
                    {
                        return (c, r);
                    }
                }
            }
        }

        // Fallback: search downward row by row
        for (int r = Math.Max(1, startRow + 1); r < startRow + 50; r++)
        {
            for (int c = 0; c <= maxCols - spanX; c++)
            {
                if (IsRegionFree(c, r, spanX, spanY, tiles, ignoreTile, maxCols, groups))
                {
                    return (c, r);
                }
            }
        }

        int maxOccupiedRow = 0;
        if (tiles != null && tiles.Any())
        {
            maxOccupiedRow = Math.Max(maxOccupiedRow, tiles.Max(t => t.Row + t.SpanY));
        }
        if (groups != null && groups.Any())
        {
            maxOccupiedRow = Math.Max(maxOccupiedRow, groups.Max(g => GetGroupBoundingBox(g, tiles ?? Enumerable.Empty<TileModel>()).MaxRow));
        }

        return (0, Math.Max(startRow + 2, maxOccupiedRow));
    }

    public static bool CanDisplace(TileModel tile) => !tile.IsLocked;

    public static List<TileModel> ResolveResizeExpansion(
        TileModel resizingTile,
        int oldSpanX,
        int oldSpanY,
        int newSpanX,
        int newSpanY,
        int maxCols,
        IList<TileModel> allTiles,
        IList<TileGroupModel>? groups = null)
    {
        var modifiedTiles = new List<TileModel>();
        int col = GetCol(resizingTile);
        int row = GetRow(resizingTile);

        int effectiveMinCol = 0;
        int effectiveMaxCol = maxCols;
        IList<TileModel> scopeTiles = allTiles;

        if (!string.IsNullOrEmpty(resizingTile.Group))
        {
            var group = groups?.FirstOrDefault(g => g.Id == resizingTile.Group);
            int groupCol = group != null ? group.Col : GetColumnStartCol(GetColumnIndexFromCol(col));
            effectiveMinCol = groupCol;
            effectiveMaxCol = groupCol + GroupColWidth;
            scopeTiles = allTiles.Where(t => t.Group == resizingTile.Group).ToList();
        }
        else
        {
            scopeTiles = allTiles.Where(t => string.IsNullOrEmpty(t.Group)).ToList();
        }

        // If expanding horizontally pushes beyond the allowed column boundary, clamp/shift left
        if (col + newSpanX > effectiveMaxCol)
        {
            int overflow = (col + newSpanX) - effectiveMaxCol;
            col = Math.Max(effectiveMinCol, col - overflow);
        }
        else if (col < effectiveMinCol)
        {
            col = effectiveMinCol;
        }

        resizingTile.Col = col;
        resizingTile.Row = row;
        resizingTile.X = PixelXFromCol(col);
        resizingTile.Y = PixelYFromRow(row);
        modifiedTiles.Add(resizingTile);

        // Check for overlapping tiles within scope
        var overlapping = scopeTiles
            .Where(t => !ReferenceEquals(t, resizingTile))
            .Where(t => DoTilesOverlap(col, row, newSpanX, newSpanY, GetCol(t), GetRow(t), t.SpanX, t.SpanY))
            .ToList();

        if (overlapping.Count == 0)
        {
            return modifiedTiles;
        }

        // Tier 1: Try Rightward Push (Same Row, strictly within effectiveMaxCol)
        if (TryPushRight(resizingTile, col, row, newSpanX, newSpanY, effectiveMaxCol, scopeTiles, out var rightMoves))
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

        // Tier 2: Try Shift Left (Self Growth Leftward, strictly >= effectiveMinCol)
        int deltaSpanX = newSpanX - oldSpanX;
        if (deltaSpanX > 0 && TryShiftLeft(resizingTile, col, row, newSpanX, newSpanY, deltaSpanX, effectiveMinCol, scopeTiles, out int leftCol))
        {
            resizingTile.Col = leftCol;
            resizingTile.X = PixelXFromCol(leftCol);
            return modifiedTiles;
        }

        // Tier 3: Column-Preserving Accordion Push Down
        var positions = new Dictionary<TileModel, (int Col, int Row)>();
        CascadePushDown(resizingTile, col, row, newSpanX, newSpanY, scopeTiles, positions);

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
        IList<TileModel> allTiles,
        IList<TileGroupModel>? groups = null)
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

            var displaced = ResolveResizeExpansion(t, oldX, oldY, newSpanX, newSpanY, maxCols, allTiles, groups);
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

        // If any loose canvas tiles expanded downward or displaced other tiles, ensure they never crash into group headers below
        if (groups != null && groups.Count > 0)
        {
            var looseTiles = allTiles.Where(t => string.IsNullOrEmpty(t.Group)).ToList();
            var pushedGroupTiles = PushGroupsDownFromLooseTiles(looseTiles, groups, allTiles);
            foreach (var pt in pushedGroupTiles)
            {
                if (!modifiedTiles.Contains(pt))
                {
                    modifiedTiles.Add(pt);
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
        int minCol,
        IList<TileModel> allTiles,
        out int newCol)
    {
        newCol = currentCol;
        int candidateCol = currentCol - deltaCols;
        if (candidateCol < minCol) return false;

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

        // When placing a loose canvas tile, only consider other loose canvas tiles for collisions and displacement
        bool isLoosePlacement = string.IsNullOrEmpty(draggedTile.Group);
        var candidateTiles = isLoosePlacement
            ? allTiles.Where(t => string.IsNullOrEmpty(t.Group)).ToList()
            : allTiles;

        var overlapping = GetOverlappingTiles(targetCol, targetRow, draggedTile.SpanX, draggedTile.SpanY, candidateTiles, draggedTile);

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
        if (TryPushRight(draggedTile, targetCol, targetRow, draggedTile.SpanX, draggedTile.SpanY, maxCols, candidateTiles, out var rightMoves))
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
        CascadePushDown(draggedTile, targetCol, targetRow, draggedTile.SpanX, draggedTile.SpanY, candidateTiles, positions);

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
            int relC = (origPositions != null && origPositions.TryGetValue(t, out var pos))
                ? pos.Col - anchorOrigCol
                : GetCol(t) - GetCol(anchorTile);

            int relR = (origPositions != null && origPositions.TryGetValue(t, out pos))
                ? pos.Row - anchorOrigRow
                : GetRow(t) - GetRow(anchorTile);

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
            int relC = (origPositions != null && origPositions.TryGetValue(tile, out var pos))
                ? pos.Col - anchorOrigCol
                : GetCol(tile) - GetCol(anchorTile);

            int relR = (origPositions != null && origPositions.TryGetValue(tile, out pos))
                ? pos.Row - anchorOrigRow
                : GetRow(tile) - GetRow(anchorTile);

            int targetC = Math.Max(0, Math.Min(anchorTargetCol + relC, maxCols - tile.SpanX));
            int targetR = Math.Max(isGroupCluster ? 1 : 0, anchorTargetRow + relR);

            tile.Col = targetC;
            tile.Row = targetR;
            tile.X = PixelXFromCol(targetC);
            tile.Y = PixelYFromRow(targetR);
            modifiedTiles.Add(tile);
        }

        // Identify collisions with non-cluster tiles (loose tiles cannot displace group tiles)
        var nonClusterTiles = isGroupCluster
            ? allTiles.Where(t => !clusterSet.Contains(t)).ToList()
            : allTiles.Where(t => !clusterSet.Contains(t) && t.Group == null).ToList();
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
            if (!CanDisplace(obstacle)) continue;
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
                if (ReferenceEquals(other, current) || !CanDisplace(other)) continue;

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
    /// Checks if a tile at (col, row) with span (spanX, spanY) is positioned in or straddling a 1x1 gap buffer.
    /// This includes group perimeter top and bottom gap buffers.
    /// </summary>
    public static bool IsIn1x1Gap(
        int col,
        int row,
        int spanX,
        int spanY,
        IEnumerable<TileGroupModel> groups,
        IEnumerable<TileModel> allTiles,
        out int gapCol,
        out int gapRow,
        string? ignoreGroupId = null)
    {
        gapCol = -1;
        gapRow = -1;

        // Group perimeter gaps (bottom and top)
        if (groups != null)
        {
            foreach (var g in groups)
            {
                if (ignoreGroupId != null && g.Id == ignoreGroupId) continue;

                var members = allTiles?.Where(t => t.Group == g.Id).ToList();
                if ((members == null || members.Count == 0) && string.IsNullOrWhiteSpace(g.Title)) continue;

                int gMinC = g.Col;
                int gMaxC = g.Col + GroupColWidth;
                int gMinR = g.Row;
                int gMaxR = (members != null && members.Count > 0) ? members.Max(t => t.Row + t.SpanY) : g.Row + 1;

                // Bottom 1x1 gap buffer (row gMaxR, immediately below member tiles)
                if (row == gMaxR && col < gMaxC && col + spanX > gMinC)
                {
                    gapCol = Math.Clamp(col, gMinC, gMaxC - 1);
                    gapRow = gMaxR;
                    return true;
                }

                // Top 1x1 gap buffer (row gMinR - 1, immediately above group header)
                if (gMinR > 0 && row == gMinR - 1 && col < gMaxC && col + spanX > gMinC)
                {
                    gapCol = Math.Clamp(col, gMinC, gMaxC - 1);
                    gapRow = gMinR - 1;
                    return true;
                }
            }
        }

        return false;
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
    /// Finds the nearest free grid slot within the group's bounds, starting from the target position.
    /// Falls back to scanning row-by-row if no slot nearby. Never goes outside the group column width.
    /// </summary>
    public static (int Col, int Row) FindFreeSlotInGroup(
        TileGroupModel group,
        int targetRelCol,
        int targetRelRow,
        int spanX,
        int spanY,
        IList<TileModel> groupTiles,
        TileModel? ignoreTile = null)
    {
        int groupCol = group.Col;
        int maxRelCol = GroupColWidth - spanX;
        int minRow = group.Row + 1; // tiles start below header

        targetRelCol = Math.Clamp(targetRelCol, 0, maxRelCol);
        targetRelRow = Math.Max(0, targetRelRow);

        // Build occupation grid relative to (groupCol, minRow)
        bool IsFree(int relC, int relR)
        {
            if (relC < 0 || relC > maxRelCol || relR < 0) return false;
            int absCol = groupCol + relC;
            int absRow = minRow + relR;
            foreach (var t in groupTiles)
            {
                if (ReferenceEquals(t, ignoreTile)) continue;
                int tc = t.Col >= 0 ? t.Col : GetCol(t);
                int tr = t.Row >= 0 ? t.Row : GetRow(t);
                if (DoTilesOverlap(absCol, absRow, spanX, spanY, tc, tr, t.SpanX, t.SpanY))
                    return false;
            }
            return true;
        }

        // Check exact target first
        if (IsFree(targetRelCol, targetRelRow))
            return (groupCol + targetRelCol, minRow + targetRelRow);

        // Expanding concentric scan within group bounds
        for (int radius = 1; radius <= 40; radius++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Abs(dx) != radius && Math.Abs(dy) != radius) continue;
                    int rc = targetRelCol + dx;
                    int rr = targetRelRow + dy;
                    if (rr < 0) continue;
                    if (rc < 0 || rc > maxRelCol) continue;
                    if (IsFree(rc, rr))
                        return (groupCol + rc, minRow + rr);
                }
            }
        }

        // Fallback: scan row-by-row from top
        int maxScanRow = groupTiles.Count > 0 ? Math.Max(100, groupTiles.Max(t => t.Row) + 20) : 100;
        for (int r = 0; r < maxScanRow; r++)
        {
            for (int c = 0; c <= maxRelCol; c++)
            {
                if (IsFree(c, r))
                    return (groupCol + c, minRow + r);
            }
        }

        int fallbackRow = groupTiles.Count > 0 ? groupTiles.Max(t => t.Row + t.SpanY) : minRow;
        return (groupCol, Math.Max(minRow, fallbackRow));
    }

    /// <summary>
    /// Places (or moves) a tile within the group at the given absolute (targetCol, targetRow).
    /// Uses the same 3-tier strategy as PlaceAndResolveCollisions but scoped to the group's own tiles:
    ///   1. Free slot → place directly
    ///   2. Single same-size tile → swap positions
    ///   3. Otherwise → try rightward push within group, then cascade push down within group
    /// Never calls PackGroupTiles, so existing tile positions are preserved.
    /// </summary>
    public static List<TileModel> PlaceTileInGroup(
        TileModel draggedTile,
        int targetCol,
        int targetRow,
        int originalCol,
        int originalRow,
        TileGroupModel group,
        IList<TileModel> allTiles,
        IList<TileGroupModel>? groups = null,
        bool isSameGroup = true)
    {
        var modified = new List<TileModel>();

        int groupCol = group.Col >= 0 ? group.Col : GetColumnStartCol(group.ColumnIndex);
        int groupMaxCol = groupCol + GroupColWidth;
        int minRow = group.Row + 1;

        // Clamp target within group bounds
        targetCol = Math.Clamp(targetCol, groupCol, Math.Max(groupCol, groupMaxCol - draggedTile.SpanX));
        targetRow = Math.Max(minRow, targetRow);

        var groupTiles = allTiles.Where(t => t.Group == group.Id).ToList();

        // Detect overlapping group tiles (excluding the dragged tile itself)
        var overlapping = groupTiles
            .Where(t => !ReferenceEquals(t, draggedTile))
            .Where(t => DoTilesOverlap(targetCol, targetRow, draggedTile.SpanX, draggedTile.SpanY,
                                       GetCol(t), GetRow(t), t.SpanX, t.SpanY))
            .ToList();

        // Case 1: Free slot
        if (overlapping.Count == 0)
        {
            draggedTile.Col = targetCol;
            draggedTile.Row = targetRow;
            draggedTile.X = PixelXFromCol(targetCol);
            draggedTile.Y = PixelYFromRow(targetRow);
            draggedTile.Group = group.Id;
            draggedTile.SectionHeader = group.Title;
            modified.Add(draggedTile);

            if (groups != null)
            {
                var pushed = PushLowerGroupsDown(group, groups, allTiles);
                foreach (var pt in pushed)
                {
                    if (!modified.Contains(pt)) modified.Add(pt);
                }
            }

            return modified;
        }

        // If target slot collides with any locked tile, fallback to finding a free slot
        if (overlapping.Any(t => !CanDisplace(t)))
        {
            var (freeC, freeR) = FindFreeSlotInGroup(
                group, targetCol - groupCol, targetRow - minRow, draggedTile.SpanX, draggedTile.SpanY, groupTiles, draggedTile);
            draggedTile.Col = freeC;
            draggedTile.Row = freeR;
            draggedTile.X = PixelXFromCol(freeC);
            draggedTile.Y = PixelYFromRow(freeR);
            draggedTile.Group = group.Id;
            draggedTile.SectionHeader = group.Title;
            modified.Add(draggedTile);

            if (groups != null)
            {
                var pushed = PushLowerGroupsDown(group, groups, allTiles);
                foreach (var pt in pushed)
                {
                    if (!modified.Contains(pt)) modified.Add(pt);
                }
            }

            return modified;
        }

        // Case 2: Clean 1-to-1 swap (same size, within the same group)
        if (isSameGroup &&
            overlapping.Count == 1 &&
            overlapping[0].SpanX == draggedTile.SpanX &&
            overlapping[0].SpanY == draggedTile.SpanY &&
            (targetCol != originalCol || targetRow != originalRow))
        {
            var swapTarget = overlapping[0];
            int swapTargetCol = GetCol(swapTarget);
            int swapTargetRow = GetRow(swapTarget);

            draggedTile.Col = swapTargetCol;
            draggedTile.Row = swapTargetRow;
            draggedTile.X = PixelXFromCol(draggedTile.Col);
            draggedTile.Y = PixelYFromRow(draggedTile.Row);
            draggedTile.Group = group.Id;
            draggedTile.SectionHeader = group.Title;
            modified.Add(draggedTile);

            swapTarget.Col = originalCol;
            swapTarget.Row = originalRow;
            swapTarget.X = PixelXFromCol(originalCol);
            swapTarget.Y = PixelYFromRow(originalRow);
            swapTarget.Group = group.Id;
            swapTarget.SectionHeader = group.Title;
            modified.Add(swapTarget);
            return modified;
        }

        // Case 3: Place dragged tile, then cascade push down within group
        draggedTile.Col = targetCol;
        draggedTile.Row = targetRow;
        draggedTile.X = PixelXFromCol(targetCol);
        draggedTile.Y = PixelYFromRow(targetRow);
        draggedTile.Group = group.Id;
        draggedTile.SectionHeader = group.Title;
        modified.Add(draggedTile);

        // Cascade: displaced tiles get pushed down within the group's 8-column band
        var positions = new Dictionary<TileModel, (int Col, int Row)>();
        var queue = new Queue<TileModel>(overlapping);
        var inQueue = new HashSet<TileModel>(overlapping);

        foreach (var t in overlapping)
        {
            if (!CanDisplace(t)) continue;
            int neededRow = targetRow + draggedTile.SpanY;
            positions[t] = (GetCol(t), Math.Max(neededRow, GetRow(t)));
        }

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            inQueue.Remove(cur);
            if (!positions.TryGetValue(cur, out var curPos)) continue;
            int curC = curPos.Col;
            int curR = curPos.Row;

            foreach (var other in groupTiles)
            {
                if (ReferenceEquals(other, draggedTile) || ReferenceEquals(other, cur)) continue;
                int oC = positions.TryGetValue(other, out var op) ? op.Col : GetCol(other);
                int oR = positions.TryGetValue(other, out op) ? op.Row : GetRow(other);

                if (DoTilesOverlap(curC, curR, cur.SpanX, cur.SpanY, oC, oR, other.SpanX, other.SpanY))
                {
                    if (!CanDisplace(other)) continue;
                    int neededOtherRow = curR + cur.SpanY;
                    if (neededOtherRow > oR)
                    {
                        positions[other] = (oC, neededOtherRow);
                        if (!inQueue.Contains(other))
                        {
                            inQueue.Add(other);
                            queue.Enqueue(other);
                        }
                    }
                }
            }
        }

        foreach (var kvp in positions)
        {
            var t = kvp.Key;
            t.Col = kvp.Value.Col;
            t.Row = kvp.Value.Row;
            t.X = PixelXFromCol(kvp.Value.Col);
            t.Y = PixelYFromRow(kvp.Value.Row);
            if (!modified.Contains(t)) modified.Add(t);
        }

        if (groups != null)
        {
            var pushed = PushLowerGroupsDown(group, groups, allTiles);
            foreach (var pt in pushed)
            {
                if (!modified.Contains(pt)) modified.Add(pt);
            }
        }

        return modified;
    }

    /// <summary>
    /// Places multiple tiles (or a single tile) into a group, finding free non-overlapping slots
    /// for each tile while accommodating their exact SpanX and SpanY.
    /// Preserves existing tile positions inside the group without repacking.
    /// Pushes lower groups down if the target group expands.
    /// Returns the list of all tiles whose positions were updated.
    /// </summary>
    public static List<TileModel> PlaceTilesInGroup(
        IList<TileModel> incomingTiles,
        TileGroupModel targetGroup,
        IList<TileModel> allTiles,
        TileModel? anchorTile = null,
        int? dropAnchorCol = null,
        int? dropAnchorRow = null,
        IList<TileGroupModel>? groups = null)
    {
        var modified = new List<TileModel>();
        if (incomingTiles == null || incomingTiles.Count == 0) return modified;

        // Group tiles that are NOT part of the incoming batch (their positions remain undisturbed)
        var incomingSet = new HashSet<TileModel>(incomingTiles);
        var groupTiles = allTiles.Where(t => t.Group == targetGroup.Id && !incomingSet.Contains(t)).ToList();

        // Sort incoming tiles top-to-bottom, left-to-right to preserve their relative spatial layout
        var orderedIncoming = incomingTiles
            .OrderBy(t => (anchorTile != null && dropAnchorRow.HasValue) 
                ? (dropAnchorRow.Value + (RowFromPixel(t.Y) - RowFromPixel(anchorTile.Y))) 
                : RowFromPixel(t.Y))
            .ThenBy(t => (anchorTile != null && dropAnchorCol.HasValue) 
                ? (dropAnchorCol.Value + (ColFromPixel(t.X) - ColFromPixel(anchorTile.X))) 
                : ColFromPixel(t.X))
            .ToList();

        foreach (var tile in orderedIncoming)
        {
            int desiredCol;
            int desiredRow;

            if (anchorTile != null && dropAnchorCol.HasValue && dropAnchorRow.HasValue)
            {
                int relCol = ColFromPixel(tile.X) - ColFromPixel(anchorTile.X);
                int relRow = RowFromPixel(tile.Y) - RowFromPixel(anchorTile.Y);
                desiredCol = dropAnchorCol.Value + relCol;
                desiredRow = dropAnchorRow.Value + relRow;
            }
            else if (dropAnchorCol.HasValue && dropAnchorRow.HasValue)
            {
                desiredCol = dropAnchorCol.Value;
                desiredRow = dropAnchorRow.Value;
            }
            else if (!double.IsNaN(tile.X) && tile.X > 0 && !double.IsNaN(tile.Y) && tile.Y > 0)
            {
                desiredCol = ColFromPixel(tile.X);
                desiredRow = RowFromPixel(tile.Y);
            }
            else
            {
                desiredCol = targetGroup.Col;
                desiredRow = targetGroup.Row + 1;
            }

            int targetRelCol = desiredCol - targetGroup.Col;
            int targetRelRow = desiredRow - (targetGroup.Row + 1);

            var (freeCol, freeRow) = FindFreeSlotInGroup(
                targetGroup, targetRelCol, targetRelRow, tile.SpanX, tile.SpanY, groupTiles);

            tile.Group = targetGroup.Id;
            tile.SectionHeader = targetGroup.Title;
            tile.Col = freeCol;
            tile.Row = freeRow;
            tile.X = PixelXFromCol(freeCol);
            tile.Y = PixelYFromRow(freeRow);

            // Add tile to groupTiles so subsequent tiles in this batch will not overlap it
            groupTiles.Add(tile);
            modified.Add(tile);
        }

        // Push lower groups down if targetGroup expanded downwards
        if (groups != null)
        {
            var pushedTiles = PushLowerGroupsDown(targetGroup, groups, allTiles);
            foreach (var pt in pushedTiles)
            {
                if (!modified.Contains(pt)) modified.Add(pt);
            }
        }

        return modified;
    }

    /// <summary>
    /// Checks all loose canvas tiles against all groups.
    /// If any loose tile overlaps a group header or penetrates into a group's vertical footprint,
    /// pushes the group (and all of its member tiles) downward, and cascades downward
    /// to push any subsequent groups and loose canvas tiles down.
    /// Returns all modified tiles.
    /// </summary>
    public static List<TileModel> PushGroupsDownFromLooseTiles(
        IEnumerable<TileModel> looseTiles,
        IList<TileGroupModel>? groups,
        IList<TileModel> allTiles)
    {
        var modified = new List<TileModel>();
        if (groups == null || groups.Count == 0 || looseTiles == null || allTiles == null) return modified;

        var orderedGroups = groups.OrderBy(g => g.Row).ToList();

        foreach (var g in orderedGroups)
        {
            int gColStart = g.Col >= 0 ? g.Col : GetColumnStartCol(g.ColumnIndex);
            int gColEnd = gColStart + GroupColWidth;

            var collidingLooseTiles = looseTiles
                .Where(t => t.Col < gColEnd && (t.Col + t.SpanX) > gColStart && t.Row <= g.Row)
                .Where(t => (t.Row + t.SpanY) > g.Row)
                .ToList();

            if (collidingLooseTiles.Count > 0)
            {
                int maxRequiredRow = collidingLooseTiles.Max(t => t.Row + t.SpanY);
                if (maxRequiredRow > g.Row)
                {
                    int delta = maxRequiredRow - g.Row;
                    g.Row = maxRequiredRow;
                    g.Y = PixelYFromRow(g.Row) + 8;

                    var gMembers = allTiles.Where(t => t.Group == g.Id).ToList();
                    foreach (var m in gMembers)
                    {
                        m.Row += delta;
                        m.Y = PixelYFromRow(m.Row);
                        if (!modified.Contains(m)) modified.Add(m);
                    }

                    var cascadeTiles = PushLowerGroupsDown(g, groups, allTiles);
                    foreach (var ct in cascadeTiles)
                    {
                        if (!modified.Contains(ct)) modified.Add(ct);
                    }
                }
            }
        }

        return modified;
    }

    /// <summary>
    /// Pushes any groups or ungrouped canvas tiles located below targetGroup down if targetGroup expanded downwards.
    /// Eliminates empty gaps and preserves the 1x1 grid row separation below the bottom of targetGroup's tiles.
    /// </summary>
    public static List<TileModel> PushLowerGroupsDown(
        TileGroupModel targetGroup,
        IList<TileGroupModel>? groups,
        IList<TileModel> allTiles)
    {
        var modified = new List<TileModel>();
        if (targetGroup == null || allTiles == null) return modified;

        var targetBox = GetGroupBoundingBox(targetGroup, allTiles);
        int groupBottom = targetBox.MaxRow; // Already incorporates +1 row for 1x1 grid separation
        int groupStartCol = targetGroup.Col;
        int groupWidth = GroupColWidth;
        int groupHeight = Math.Max(1, groupBottom - targetGroup.Row);

        var groupList = groups ?? new List<TileGroupModel>();

        var proposedGroupPositions = new Dictionary<TileGroupModel, (int Col, int Row)>();
        var proposedTilePositions = new Dictionary<TileModel, (int Col, int Row)>();

        var groupQueue = new Queue<TileGroupModel>();
        var tileQueue = new Queue<TileModel>();
        var inGroupQueue = new HashSet<TileGroupModel>();
        var inTileQueue = new HashSet<TileModel>();

        // 1. Initial collisions with the expanded targetGroup bounding box (including the 1x1 gap)
        foreach (var otherG in groupList)
        {
            if (ReferenceEquals(otherG, targetGroup)) continue;
            if (otherG.ColumnIndex != targetGroup.ColumnIndex || otherG.Row < targetGroup.Row) continue;

            var (minC, maxC, minR, maxR) = GetGroupBoundingBox(otherG, allTiles);
            if (DoTilesOverlap(groupStartCol, targetGroup.Row, groupWidth, groupHeight, minC, minR, maxC - minC, maxR - minR))
            {
                proposedGroupPositions[otherG] = (otherG.Col, groupBottom);
                inGroupQueue.Add(otherG);
                groupQueue.Enqueue(otherG);
            }
        }

        foreach (var ut in allTiles.Where(t => t.Group == null))
        {
            if (ut.Col < groupStartCol + groupWidth && (ut.Col + ut.SpanX) > groupStartCol && ut.Row >= targetGroup.Row)
            {
                if (DoTilesOverlap(groupStartCol, targetGroup.Row, groupWidth, groupHeight, ut.Col, ut.Row, ut.SpanX, ut.SpanY))
                {
                    proposedTilePositions[ut] = (ut.Col, groupBottom + 1);
                    inTileQueue.Add(ut);
                    tileQueue.Enqueue(ut);
                }
            }
        }

        // 2. Cascade down for any displaced groups and tiles
        while (groupQueue.Count > 0 || tileQueue.Count > 0)
        {
            if (groupQueue.Count > 0)
            {
                var curG = groupQueue.Dequeue();
                inGroupQueue.Remove(curG);
                var pos = proposedGroupPositions[curG];
                var (_, _, _, curGMaxR) = GetGroupBoundingBox(curG, allTiles);
                int curGHeight = Math.Max(1, curGMaxR - curG.Row);

                foreach (var otherG in groupList)
                {
                    if (ReferenceEquals(otherG, targetGroup) || ReferenceEquals(otherG, curG)) continue;
                    if (otherG.ColumnIndex != targetGroup.ColumnIndex || otherG.Row < curG.Row) continue;

                    var oPos = proposedGroupPositions.TryGetValue(otherG, out var op) ? op : (otherG.Col, otherG.Row);
                    var (_, _, _, otherGMaxR) = GetGroupBoundingBox(otherG, allTiles);
                    int oHeight = Math.Max(1, otherGMaxR - otherG.Row);

                    if (DoTilesOverlap(pos.Col, pos.Row, GroupColWidth, curGHeight, oPos.Col, oPos.Row, GroupColWidth, oHeight))
                    {
                        int neededRow = pos.Row + curGHeight;
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
                    if (ut.Col < groupStartCol + groupWidth && (ut.Col + ut.SpanX) > groupStartCol)
                    {
                        var oPos = proposedTilePositions.TryGetValue(ut, out var op) ? op : (ut.Col, ut.Row);
                        if (DoTilesOverlap(pos.Col, pos.Row, GroupColWidth, curGHeight, oPos.Col, oPos.Row, ut.SpanX, ut.SpanY))
                        {
                            int neededRow = pos.Row + curGHeight + 1;
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
            }
            else if (tileQueue.Count > 0)
            {
                var curT = tileQueue.Dequeue();
                inTileQueue.Remove(curT);
                var pos = proposedTilePositions[curT];

                foreach (var otherT in allTiles.Where(t => t.Group == null))
                {
                    if (ReferenceEquals(otherT, curT)) continue;
                    if (otherT.Col < groupStartCol + groupWidth && (otherT.Col + otherT.SpanX) > groupStartCol)
                    {
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
                }

                foreach (var otherG in groupList)
                {
                    if (ReferenceEquals(otherG, targetGroup)) continue;
                    if (otherG.ColumnIndex != targetGroup.ColumnIndex || otherG.Row < curT.Row) continue;

                    var oPos = proposedGroupPositions.TryGetValue(otherG, out var op) ? op : (otherG.Col, otherG.Row);
                    var (_, _, _, otherGMaxR) = GetGroupBoundingBox(otherG, allTiles);
                    int oHeight = Math.Max(1, otherGMaxR - otherG.Row);

                    if (DoTilesOverlap(pos.Col, pos.Row, curT.SpanX, curT.SpanY, oPos.Col, oPos.Row, GroupColWidth, oHeight))
                    {
                        int neededRow = pos.Row + curT.SpanY;
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

        // 3. Apply proposed positions
        foreach (var kvp in proposedGroupPositions)
        {
            var g = kvp.Key;
            int newGRow = kvp.Value.Row;
            int deltaGRow = newGRow - g.Row;
            if (deltaGRow == 0) continue;

            g.Row = newGRow;
            g.Y = PixelYFromRow(g.Row) + 8;

            var gTiles = allTiles.Where(t => t.Group == g.Id).ToList();
            foreach (var t in gTiles)
            {
                t.Row += deltaGRow;
                t.Y = PixelYFromRow(t.Row);
                if (!modified.Contains(t)) modified.Add(t);
            }
        }

        foreach (var kvp in proposedTilePositions)
        {
            var t = kvp.Key;
            int newRow = kvp.Value.Row;
            if (newRow == t.Row) continue;

            t.Row = newRow;
            t.Y = PixelYFromRow(t.Row);
            if (!modified.Contains(t)) modified.Add(t);
        }

        return modified;
    }

    /// <summary>
    /// Pulls lower groups and ungrouped canvas tiles upwards when targetGroup shrinks,
    /// closing empty voids while preserving the 1x1 grid row separation below targetGroup
    /// and maintaining the relative spatial arrangement of all items below.
    /// </summary>
    public static List<TileModel> PullLowerGroupsUp(
        TileGroupModel targetGroup,
        IList<TileGroupModel>? groups,
        IList<TileModel> allTiles,
        int? maxPullUpRows = null)
    {
        var modified = new List<TileModel>();
        if (targetGroup == null || allTiles == null) return modified;

        var groupList = groups ?? new List<TileGroupModel>();
        int targetColStart = targetGroup.Col >= 0 ? targetGroup.Col : GetColumnStartCol(targetGroup.ColumnIndex);
        int targetColEnd = targetColStart + GroupColWidth;

        // 1. Identify all groups and loose canvas tiles below targetGroup in the same column track
        var lowerGroups = groupList
            .Where(g => !ReferenceEquals(g, targetGroup) && g.ColumnIndex == targetGroup.ColumnIndex && g.Row > targetGroup.Row)
            .ToList();

        var lowerLooseTiles = allTiles
            .Where(t => t.Group == null && t.Col < targetColEnd && (t.Col + t.SpanX) > targetColStart && t.Row > targetGroup.Row)
            .ToList();

        if (lowerGroups.Count == 0 && lowerLooseTiles.Count == 0) return modified;

        // 2. Determine the top-most row among all lower items
        int highestLowerRow = int.MaxValue;
        foreach (var g in lowerGroups)
        {
            if (g.Row < highestLowerRow) highestLowerRow = g.Row;
        }
        foreach (var t in lowerLooseTiles)
        {
            if (t.Row < highestLowerRow) highestLowerRow = t.Row;
        }

        // 3. Calculate the earliest allowed row where lower items can sit (including 1x1 gap buffer)
        bool isGroupDeleted = !groupList.Contains(targetGroup);
        int minAllowedRow;
        if (isGroupDeleted)
        {
            minAllowedRow = targetGroup.Row;
        }
        else
        {
            var targetBox = GetGroupBoundingBox(targetGroup, allTiles);
            bool highestIsLoose = lowerLooseTiles.Any(t => t.Row == highestLowerRow);
            minAllowedRow = highestIsLoose ? targetBox.MaxRow + 1 : targetBox.MaxRow;
        }

        if (highestLowerRow == int.MaxValue || highestLowerRow <= minAllowedRow) return modified;

        // 4. Calculate upward shift amount
        int availableVoid = highestLowerRow - minAllowedRow;
        int pullUpDelta = maxPullUpRows.HasValue
            ? Math.Min(maxPullUpRows.Value, availableVoid)
            : availableVoid;

        if (pullUpDelta <= 0) return modified;

        // 5. Shift all lower groups and member tiles up by pullUpDelta
        foreach (var g in lowerGroups)
        {
            g.Row -= pullUpDelta;
            g.Y = PixelYFromRow(g.Row) + 8;

            var gMembers = allTiles.Where(t => t.Group == g.Id).ToList();
            foreach (var t in gMembers)
            {
                t.Row -= pullUpDelta;
                t.Y = PixelYFromRow(t.Row);
                if (!modified.Contains(t)) modified.Add(t);
            }
        }

        // 6. Shift all lower loose canvas tiles up by pullUpDelta
        foreach (var t in lowerLooseTiles)
        {
            t.Row -= pullUpDelta;
            t.Y = PixelYFromRow(t.Row);
            if (!modified.Contains(t)) modified.Add(t);
        }

        return modified;
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
        int compactVerticalSpacing = 0)
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
        // Groups can be created empty directly on canvas and populated later.
        // Empty groups are managed/deleted explicitly by the user via the header context menu.
        return false;
    }

    /// <summary>
    /// Calculates the total vertical footprint of a group (1 header row + rows occupied by member tiles).
    /// Preserves existing tile positions without repacking.
    /// </summary>
    public static int CalculateGroupHeightRows(TileGroupModel group, IEnumerable<TileModel> allTiles)
    {
        var members = allTiles.Where(t => t.Group == group.Id).ToList();
        if (members.Count == 0) return 1;
        int maxBottom = members.Max(t => t.Row + t.SpanY);
        return Math.Max(1, maxBottom - group.Row);
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

        int candidateRow = maxExistingRow;

        double firstItemTopY = PixelYFromRow(minExistingRow);
        if (mouseY < firstItemTopY + 20)
        {
            candidateRow = 0;
        }
        else
        {
            double lastItemBottomY = PixelYFromRow(maxExistingRow);
            if (mouseY >= lastItemBottomY)
            {
                candidateRow = Math.Max(0, maxExistingRow);
            }
            else
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    double topY = PixelYFromRow(item.MinRow);
                    double bottomY = PixelYFromRow(item.MaxRow);
                    double midY = (topY + bottomY) / 2.0;

                    if (mouseY < midY)
                    {
                        candidateRow = Math.Max(0, item.MinRow);
                        break;
                    }

                    if (i + 1 < items.Count)
                    {
                        var nextItem = items[i + 1];
                        double nextTopY = PixelYFromRow(nextItem.MinRow);
                        if (mouseY >= bottomY && mouseY < nextTopY)
                        {
                            candidateRow = Math.Max(0, item.MaxRow);
                            break;
                        }
                    }
                }
            }
        }

        // Guard: If candidateRow would displace any locked group, snap candidateRow below the locked group
        int draggedHeight = excludeGroup != null ? CalculateGroupHeightRows(excludeGroup, allTiles) : 2;
        var groupsList = groups.ToList();
        var allTilesList = allTiles.ToList();
        int guardLoop = 0;
        while (guardLoop++ < 30 && WouldDisplaceLockedGroup(columnIndex, candidateRow, draggedHeight, groupsList, allTilesList, excludeGroup, out var conflict))
        {
            if (conflict != null)
            {
                var box = GetGroupBoundingBox(conflict, allTilesList);
                candidateRow = Math.Max(candidateRow, box.MaxRow);
            }
            else
            {
                candidateRow++;
            }
        }

        return Math.Max(0, candidateRow);
    }

    /// <summary>
    /// Checks if inserting a group with height <paramref name="groupHeight"/> at (<paramref name="targetColIndex"/>, <paramref name="targetRow"/>)
    /// would displace any locked group (or locked ungrouped tile) either directly or through cascading collisions.
    /// </summary>
    public static bool WouldDisplaceLockedGroup(
        int targetColIndex,
        int targetRow,
        int groupHeight,
        IEnumerable<TileGroupModel> groups,
        IEnumerable<TileModel> allTiles,
        TileGroupModel? draggedGroup,
        out TileGroupModel? conflictingLockedGroup)
    {
        conflictingLockedGroup = null;
        targetColIndex = Math.Max(0, targetColIndex);
        targetRow = Math.Max(0, targetRow);
        int targetColStart = GetColumnStartCol(targetColIndex);
        int groupSpanX = GroupColWidth;

        var allTilesList = allTiles.ToList();
        var groupsList = groups.ToList();

        var proposedGroupPositions = new Dictionary<TileGroupModel, (int Col, int Row)>();
        var proposedTilePositions = new Dictionary<TileModel, (int Col, int Row)>();

        var groupQueue = new Queue<TileGroupModel>();
        var tileQueue = new Queue<TileModel>();
        var inGroupQueue = new HashSet<TileGroupModel>();
        var inTileQueue = new HashSet<TileModel>();

        // 1. Check direct collision with newly placed group
        foreach (var otherG in groupsList)
        {
            if (ReferenceEquals(otherG, draggedGroup)) continue;

            var (minC, maxC, minR, maxR) = GetGroupBoundingBox(otherG, allTilesList);
            if (DoTilesOverlap(targetColStart, targetRow, groupSpanX, groupHeight, minC, minR, maxC - minC, maxR - minR))
            {
                if (otherG.IsLocked)
                {
                    conflictingLockedGroup = otherG;
                    return true;
                }
                int pushedRow = targetRow + groupHeight;
                proposedGroupPositions[otherG] = (otherG.Col, pushedRow);
                inGroupQueue.Add(otherG);
                groupQueue.Enqueue(otherG);
            }
        }

        foreach (var ut in allTilesList.Where(t => t.Group == null))
        {
            if (DoTilesOverlap(targetColStart, targetRow, groupSpanX, groupHeight, ut.Col, ut.Row, ut.SpanX, ut.SpanY))
            {
                if (ut.IsLocked)
                {
                    return true;
                }
                int pushedRow = targetRow + groupHeight;
                proposedTilePositions[ut] = (ut.Col, pushedRow);
                inTileQueue.Add(ut);
                tileQueue.Enqueue(ut);
            }
        }

        // 2. Cascade Push Down Simulation
        while (groupQueue.Count > 0 || tileQueue.Count > 0)
        {
            if (groupQueue.Count > 0)
            {
                var curG = groupQueue.Dequeue();
                inGroupQueue.Remove(curG);
                var pos = proposedGroupPositions[curG];
                int curGHeight = CalculateGroupHeightRows(curG, allTilesList);

                foreach (var otherG in groupsList)
                {
                    if (ReferenceEquals(otherG, draggedGroup) || ReferenceEquals(otherG, curG)) continue;

                    var oPos = proposedGroupPositions.TryGetValue(otherG, out var op) ? op : (otherG.Col, otherG.Row);
                    int oHeight = CalculateGroupHeightRows(otherG, allTilesList);

                    if (DoTilesOverlap(pos.Col, pos.Row, GroupColWidth, curGHeight, oPos.Col, oPos.Row, GroupColWidth, oHeight))
                    {
                        if (otherG.IsLocked)
                        {
                            conflictingLockedGroup = otherG;
                            return true;
                        }
                        int neededRow = pos.Row + curGHeight;
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

                foreach (var ut in allTilesList.Where(t => t.Group == null))
                {
                    var oPos = proposedTilePositions.TryGetValue(ut, out var op) ? op : (ut.Col, ut.Row);

                    if (DoTilesOverlap(pos.Col, pos.Row, GroupColWidth, curGHeight, oPos.Col, oPos.Row, ut.SpanX, ut.SpanY))
                    {
                        if (ut.IsLocked)
                        {
                            return true;
                        }
                        int neededRow = pos.Row + curGHeight;
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

                foreach (var otherT in allTilesList.Where(t => t.Group == null))
                {
                    if (ReferenceEquals(otherT, curT)) continue;
                    var oPos = proposedTilePositions.TryGetValue(otherT, out var op) ? op : (otherT.Col, otherT.Row);

                    if (DoTilesOverlap(pos.Col, pos.Row, curT.SpanX, curT.SpanY, oPos.Col, oPos.Row, otherT.SpanX, otherT.SpanY))
                    {
                        if (otherT.IsLocked)
                        {
                            return true;
                        }
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

                foreach (var otherG in groupsList)
                {
                    if (ReferenceEquals(otherG, draggedGroup)) continue;
                    var oPos = proposedGroupPositions.TryGetValue(otherG, out var op) ? op : (otherG.Col, otherG.Row);
                    int oHeight = CalculateGroupHeightRows(otherG, allTilesList);

                    if (DoTilesOverlap(pos.Col, pos.Row, curT.SpanX, curT.SpanY, oPos.Col, oPos.Row, GroupColWidth, oHeight))
                    {
                        if (otherG.IsLocked)
                        {
                            conflictingLockedGroup = otherG;
                            return true;
                        }
                        int neededRow = pos.Row + curT.SpanY;
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

        return false;
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
        int compactVerticalSpacing = 0)
    {
        var modifiedTiles = new List<TileModel>();

        targetColIndex = Math.Max(0, targetColIndex);
        targetRow = Math.Max(0, targetRow);

        int groupHeight = CalculateGroupHeightRows(draggedGroup, allTiles);

        // Guard: Prevent placing or shoving that would displace any locked group
        int guardLoop = 0;
        while (guardLoop++ < 30 && WouldDisplaceLockedGroup(targetColIndex, targetRow, groupHeight, groups, allTiles, draggedGroup, out var conflict))
        {
            if (conflict != null)
            {
                var box = GetGroupBoundingBox(conflict, allTiles);
                targetRow = Math.Max(targetRow, box.MaxRow);
            }
            else
            {
                targetRow++;
            }
        }

        int targetColStart = GetColumnStartCol(targetColIndex);

        int oldGroupCol = draggedGroup.Col >= 0 ? draggedGroup.Col : GetColumnStartCol(draggedGroup.ColumnIndex);
        int oldGroupRow = Math.Max(0, draggedGroup.Row);
        int colDelta = targetColStart - oldGroupCol;
        int rowDelta = targetRow - oldGroupRow;

        // 1. Position the group header
        draggedGroup.ColumnIndex = targetColIndex;
        draggedGroup.Col = targetColStart;
        draggedGroup.Row = targetRow;
        draggedGroup.X = PixelXFromCol(targetColStart);
        draggedGroup.Y = PixelYFromRow(targetRow) + 8;

        // 2. Move member tiles rigidly preserving relative positions
        var memberTiles = allTiles.Where(t => t.Group == draggedGroup.Id).ToList();
        int bottomRow = targetRow + 1;

        foreach (var t in memberTiles)
        {
            int newCol = t.Col + colDelta;
            int newRow = t.Row + rowDelta;

            // Ensure tile stays within the group's 8-unit width and below header
            int minCol = targetColStart;
            int maxCol = targetColStart + GroupColWidth - Math.Clamp(t.SpanX, 1, GroupColWidth);
            t.Col = Math.Clamp(newCol, minCol, maxCol);
            t.Row = Math.Max(targetRow + 1, newRow);
            t.X = PixelXFromCol(t.Col);
            t.Y = PixelYFromRow(t.Row);

            if (!modifiedTiles.Contains(t)) modifiedTiles.Add(t);
            bottomRow = Math.Max(bottomRow, t.Row + t.SpanY);
        }

        groupHeight = bottomRow - targetRow;
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
            if (ReferenceEquals(otherG, draggedGroup) || otherG.IsLocked) continue;

            var (minC, maxC, minR, maxR) = GetGroupBoundingBox(otherG, allTiles);
            if (DoTilesOverlap(targetColStart, targetRow, groupSpanX, groupHeight, minC, minR, maxC - minC, maxR - minR))
            {
                int pushedRow = targetRow + groupHeight + compactVerticalSpacing;
                proposedGroupPositions[otherG] = (otherG.Col, pushedRow);
                inGroupQueue.Add(otherG);
                groupQueue.Enqueue(otherG);
            }
        }

        foreach (var ut in allTiles.Where(t => t.Group == null && !t.IsLocked))
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
                    if (ReferenceEquals(otherG, draggedGroup) || ReferenceEquals(otherG, curG) || otherG.IsLocked) continue;

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

                foreach (var ut in allTiles.Where(t => t.Group == null && !t.IsLocked))
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

                foreach (var otherT in allTiles.Where(t => t.Group == null && !t.IsLocked))
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
                    if (ReferenceEquals(otherG, draggedGroup) || otherG.IsLocked) continue;
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
            if (g.IsLocked) continue; // Never move locked groups

            int oldGRow = g.Row;
            int newGRow = Math.Max(0, kvp.Value.Row);
            int deltaGRow = newGRow - oldGRow;

            g.Row = newGRow;
            g.Y = PixelYFromRow(g.Row) + 8;

            var gTiles = allTiles.Where(t => t.Group == g.Id).ToList();
            foreach (var t in gTiles)
            {
                t.Row += deltaGRow;
                t.Y = PixelYFromRow(t.Row);
                if (!modifiedTiles.Contains(t)) modifiedTiles.Add(t);
            }
        }

        foreach (var kvp in proposedTilePositions)
        {
            var t = kvp.Key;
            if (t.IsLocked) continue; // Never move locked tiles

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
