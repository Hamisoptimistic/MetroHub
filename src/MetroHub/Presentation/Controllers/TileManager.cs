using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Core.Services.Catalog;

namespace MetroHub.Presentation.Controllers;

/// <summary>
/// Manages tile selection, undo/redo state restoration, batch resizing and styling,
/// batch unpinning, and tile pinning (files, links, catalog items, widgets).
/// </summary>
public sealed class TileManager
{
    private readonly Func<ObservableCollection<TileModel>> _tilesProvider;
    private readonly Func<ObservableCollection<TileGroupModel>> _groupsProvider;
    private readonly Func<ItemsControl?> _tilesListBoxProvider;
    private readonly Func<ScrollViewer?> _contentScrollViewerProvider;
    private readonly Func<double> _windowWidthProvider;
    private readonly Action<IList<TileModel>> _animateModifiedTilesAction;
    private readonly Action _updateGroupHeaderPositionsAction;
    private readonly Action _updateLayoutMetricsAction;
    private readonly Action _updateCanvasHeightAction;
    private readonly Action _updateExposedAddSlotsAction;
    private readonly Action _saveGroupsAndLayoutAction;
    private readonly Action _cleanEmptyGroupsAndReflowAction;
    private readonly Action _compactGroupGapsAction;
    private readonly Action<TileGroupModel> _flashLockedGroupAction;
    private readonly Action? _hideDropSlotIndicatorAction;
    private readonly LayoutHistoryService _historyService;
    private readonly Dispatcher _dispatcher;

    public TileManager(
        Func<ObservableCollection<TileModel>> tilesProvider,
        Func<ObservableCollection<TileGroupModel>> groupsProvider,
        Func<ItemsControl?> tilesListBoxProvider,
        Func<ScrollViewer?> contentScrollViewerProvider,
        Func<double> windowWidthProvider,
        Action<IList<TileModel>> animateModifiedTilesAction,
        Action updateGroupHeaderPositionsAction,
        Action updateLayoutMetricsAction,
        Action updateCanvasHeightAction,
        Action updateExposedAddSlotsAction,
        Action saveGroupsAndLayoutAction,
        Action cleanEmptyGroupsAndReflowAction,
        Action compactGroupGapsAction,
        Action<TileGroupModel> flashLockedGroupAction,
        Action? hideDropSlotIndicatorAction,
        LayoutHistoryService historyService,
        Dispatcher dispatcher)
    {
        _tilesProvider = tilesProvider;
        _groupsProvider = groupsProvider;
        _tilesListBoxProvider = tilesListBoxProvider;
        _contentScrollViewerProvider = contentScrollViewerProvider;
        _windowWidthProvider = windowWidthProvider;
        _animateModifiedTilesAction = animateModifiedTilesAction;
        _updateGroupHeaderPositionsAction = updateGroupHeaderPositionsAction;
        _updateLayoutMetricsAction = updateLayoutMetricsAction;
        _updateCanvasHeightAction = updateCanvasHeightAction;
        _updateExposedAddSlotsAction = updateExposedAddSlotsAction;
        _saveGroupsAndLayoutAction = saveGroupsAndLayoutAction;
        _cleanEmptyGroupsAndReflowAction = cleanEmptyGroupsAndReflowAction;
        _compactGroupGapsAction = compactGroupGapsAction;
        _flashLockedGroupAction = flashLockedGroupAction;
        _hideDropSlotIndicatorAction = hideDropSlotIndicatorAction;
        _historyService = historyService;
        _dispatcher = dispatcher;
    }

    public void ClearSelection()
    {
        foreach (var t in _tilesProvider())
        {
            t.IsSelected = false;
        }
    }

    public List<TileModel> GetSelectedTiles() => _tilesProvider().Where(t => t.IsSelected).ToList();

    public void ExecuteUndo(bool isDragging, bool isRubberBanding)
    {
        if (isDragging || isRubberBanding || !_historyService.CanUndo) return;

        ClearSelection();
        var tiles = _tilesProvider();
        var groups = _groupsProvider();
        string currentSnapshot = LayoutHistoryService.CaptureSnapshot(tiles, groups);
        string? targetSnapshot = _historyService.Undo(currentSnapshot);
        if (!string.IsNullOrWhiteSpace(targetSnapshot))
        {
            RestoreLayoutFromSnapshot(targetSnapshot);
        }
    }

    public void ExecuteRedo(bool isDragging, bool isRubberBanding)
    {
        if (isDragging || isRubberBanding || !_historyService.CanRedo) return;

        ClearSelection();
        var tiles = _tilesProvider();
        var groups = _groupsProvider();
        string currentSnapshot = LayoutHistoryService.CaptureSnapshot(tiles, groups);
        string? targetSnapshot = _historyService.Redo(currentSnapshot);
        if (!string.IsNullOrWhiteSpace(targetSnapshot))
        {
            RestoreLayoutFromSnapshot(targetSnapshot);
        }
    }

    public void RestoreLayoutFromSnapshot(string snapshot)
    {
        var snapshotModel = LayoutHistoryService.ParseSnapshot(snapshot);
        if (snapshotModel == null) return;

        var targetTiles = snapshotModel.Tiles;
        var targetGroups = snapshotModel.Groups;

        var targetDict = targetTiles.ToDictionary(t => t.Id);
        var currentTiles = _tilesProvider().ToList();
        var currentDict = currentTiles.ToDictionary(t => t.Id);

        var toRemove = currentTiles.Where(t => !targetDict.ContainsKey(t.Id)).ToList();
        foreach (var t in toRemove)
        {
            t.Teardown();
            _tilesProvider().Remove(t);
        }

        var toAdd = targetTiles.Where(t => !currentDict.ContainsKey(t.Id)).ToList();
        foreach (var t in toAdd)
        {
            _tilesProvider().Add(t);
        }

        var targetGroupDict = targetGroups.ToDictionary(g => g.Id);
        var currentGroups = _groupsProvider().ToList();
        var currentGroupDict = currentGroups.ToDictionary(g => g.Id);

        bool shouldRestoreGroups = targetGroups.Count > 0 || !targetTiles.Any(t => !string.IsNullOrEmpty(t.Group));
        if (shouldRestoreGroups)
        {
            var groupsToRemove = currentGroups.Where(g => !targetGroupDict.ContainsKey(g.Id)).ToList();
            foreach (var g in groupsToRemove)
            {
                _groupsProvider().Remove(g);
            }

            var groupsToAdd = targetGroups.Where(g => !currentGroupDict.ContainsKey(g.Id)).ToList();
            foreach (var g in groupsToAdd)
            {
                _groupsProvider().Add(g);
            }

            foreach (var tg in targetGroups)
            {
                var existingG = _groupsProvider().FirstOrDefault(g => g.Id == tg.Id);
                if (existingG == null) continue;
                existingG.Title = tg.Title;
                existingG.HeaderColor = tg.HeaderColor;
                existingG.Col = tg.Col;
                existingG.Row = tg.Row;
                existingG.X = tg.X;
                existingG.Y = tg.Y;
                existingG.IsEditing = false;
                existingG.IsLocked = tg.IsLocked;
                existingG.TintColor = tg.TintColor;
                existingG.ColumnIndex = tg.ColumnIndex;
                existingG.OrderIndex = tg.OrderIndex;
            }
        }

        var modifiedList = new List<TileModel>();

        foreach (var target in targetTiles)
        {
            var existing = _tilesProvider().FirstOrDefault(t => t.Id == target.Id);
            if (existing == null) continue;

            bool posChanged = Math.Abs(existing.X - target.X) > 0.5 || Math.Abs(existing.Y - target.Y) > 0.5;
            bool spanChanged = existing.SpanX != target.SpanX || existing.SpanY != target.SpanY;
            bool styleChanged = existing.TileStyle != target.TileStyle || existing.AccentColor != target.AccentColor;

            int oldSpanX = existing.SpanX;
            int oldSpanY = existing.SpanY;

            existing.Col = target.Col;
            existing.Row = target.Row;
            existing.SpanX = target.SpanX;
            existing.SpanY = target.SpanY;
            existing.TileStyle = target.TileStyle;
            existing.AccentColor = target.AccentColor;
            existing.Group = target.Group;
            existing.SectionHeader = target.SectionHeader;
            existing.IsLocked = target.IsLocked;

            if (spanChanged)
            {
                var control = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, existing));
                control?.AnimateResize(oldSpanX, oldSpanY, target.SpanX, target.SpanY);
            }

            if (posChanged)
            {
                existing.X = target.X;
                existing.Y = target.Y;
                modifiedList.Add(existing);
            }
            else
            {
                var container = _tilesListBoxProvider()?.ItemContainerGenerator.ContainerFromItem(existing) as ContentPresenter;
                if (container != null)
                {
                    Canvas.SetLeft(container, target.X);
                    Canvas.SetTop(container, target.Y);
                }
            }

            if (styleChanged)
            {
                var control = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, existing));
                control?.ApplyTileStyle(animate: true);
            }
        }

        if (modifiedList.Count > 0)
        {
            _animateModifiedTilesAction(modifiedList);
        }

        _updateGroupHeaderPositionsAction();
        _updateLayoutMetricsAction();
        _updateCanvasHeightAction();
        _updateExposedAddSlotsAction();
        _saveGroupsAndLayoutAction();
    }

    public void BatchResizeSelectedTiles(int newSpanX, int newSpanY, TileModel anchorTile)
    {
        var tiles = _tilesProvider();
        var groups = _groupsProvider();
        var selected = GetSelectedTiles();
        List<TileModel> targets = (anchorTile.IsSelected && selected.Count > 1)
            ? selected.ToList()
            : new List<TileModel> { anchorTile };

        if (targets.All(t => t.SpanX == newSpanX && t.SpanY == newSpanY))
        {
            return;
        }

        string preModify = LayoutHistoryService.CaptureSnapshot(tiles, groups);

        var oldSpans = targets.ToDictionary(t => t, t => (t.SpanX, t.SpanY));

        var listBox = _tilesListBoxProvider();
        foreach (var t in targets)
        {
            var container = listBox?.ItemContainerGenerator.ContainerFromItem(t) as ContentPresenter;
            if (container != null)
            {
                Panel.SetZIndex(container, 50);
                _dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(260);
                    Panel.SetZIndex(container, 0);
                });
            }
        }

        _updateLayoutMetricsAction();
        int maxCols = GridPlacementService.MaxCols;

        var affectedGroups = targets
            .Where(t => !string.IsNullOrEmpty(t.Group))
            .Select(t => groups.FirstOrDefault(g => g.Id == t.Group))
            .Where(g => g != null)
            .Distinct()
            .ToList();

        var oldGroupBottoms = affectedGroups.ToDictionary(
            g => g!,
            g => GridPlacementService.GetGroupBoundingBox(g!, tiles).MaxRow);

        var modified = GridPlacementService.ResolveBatchResizeExpansion(
            targets,
            newSpanX,
            newSpanY,
            maxCols,
            tiles,
            groups);

        foreach (var t in targets)
        {
            var (oldX, oldY) = oldSpans[t];
            var control = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, t));
            control?.AnimateResize(oldX, oldY, newSpanX, newSpanY);
        }

        foreach (var ag in affectedGroups)
        {
            int oldBottom = oldGroupBottoms[ag!];
            int newBottom = GridPlacementService.GetGroupBoundingBox(ag!, tiles).MaxRow;
            if (newBottom > oldBottom)
            {
                var pushed = GridPlacementService.PushLowerGroupsDown(ag!, groups, tiles);
                foreach (var pt in pushed)
                {
                    if (!modified.Contains(pt)) modified.Add(pt);
                }
            }
            else if (newBottom < oldBottom)
            {
                int shrink = oldBottom - newBottom;
                var pulled = GridPlacementService.PullLowerGroupsUp(ag!, groups, tiles, shrink);
                foreach (var pt in pulled)
                {
                    if (!modified.Contains(pt)) modified.Add(pt);
                }
            }
        }

        _animateModifiedTilesAction(modified);
        _hideDropSlotIndicatorAction?.Invoke();
        _updateCanvasHeightAction();
        _updateExposedAddSlotsAction();
        _updateGroupHeaderPositionsAction();
        _saveGroupsAndLayoutAction();

        string postModify = LayoutHistoryService.CaptureSnapshot(tiles, groups);
        if (preModify != postModify)
        {
            _historyService.PushState(preModify);
        }
    }

    public void BatchStyleSelectedTiles(string newStyle, TileModel anchorTile)
    {
        var tiles = _tilesProvider();
        var groups = _groupsProvider();
        var selected = GetSelectedTiles();
        List<TileModel> targets = (anchorTile.IsSelected && selected.Count > 1)
            ? selected.ToList()
            : new List<TileModel> { anchorTile };

        string preModify = LayoutHistoryService.CaptureSnapshot(tiles, groups);

        foreach (var t in targets)
        {
            t.TileStyle = newStyle;
            if (string.Equals(newStyle, "Colourful", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(t.AccentColor))
                {
                    t.AccentColor = ColorExtractorService.ExtractAccentColor(t.IconPath);
                }
            }

            var control = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, t));
            control?.ApplyTileStyle(animate: true);
        }

        _saveGroupsAndLayoutAction();

        string postModify = LayoutHistoryService.CaptureSnapshot(tiles, groups);
        if (preModify != postModify)
        {
            _historyService.PushState(preModify);
        }
    }

    public void DeleteSelectedTiles()
    {
        var targets = GetSelectedTiles();
        if (targets.Count == 0) return;
        BatchUnpinTiles(targets);
    }

    public void BatchUnpinSelectedTiles(TileModel anchorTile)
    {
        var selected = GetSelectedTiles();
        List<TileModel> targets = (anchorTile.IsSelected && selected.Count > 1)
            ? selected.ToList()
            : new List<TileModel> { anchorTile };

        BatchUnpinTiles(targets);
    }

    public void BatchUnpinTiles(IList<TileModel> targets)
    {
        if (targets == null || targets.Count == 0) return;

        var tiles = _tilesProvider();
        var groups = _groupsProvider();

        var eligible = targets
            .Where(t => !t.IsLocked &&
                        !(t.Group != null && groups.FirstOrDefault(g => g.Id == t.Group)?.IsLocked == true))
            .ToList();

        if (eligible.Count == 0)
        {
            var lockedGroup = targets
                .Where(t => t.Group != null)
                .Select(t => groups.FirstOrDefault(g => g.Id == t.Group))
                .FirstOrDefault(g => g != null && g.IsLocked);
            if (lockedGroup != null)
            {
                _flashLockedGroupAction(lockedGroup);
            }

            return;
        }

        string preUnpin = LayoutHistoryService.CaptureSnapshot(tiles, groups);
        _historyService.PushState(preUnpin);

        var affectedGroups = eligible
            .Where(t => !string.IsNullOrEmpty(t.Group))
            .Select(t => groups.FirstOrDefault(g => g.Id == t.Group))
            .Where(g => g != null)
            .Distinct()
            .ToList();

        var oldBottoms = affectedGroups.ToDictionary(
            g => g!,
            g => GridPlacementService.GetGroupBoundingBox(g!, tiles).MaxRow);

        foreach (var t in eligible)
        {
            t.Teardown();
            tiles.Remove(t);
        }

        var modified = new List<TileModel>();
        foreach (var g in affectedGroups)
        {
            int oldBottom = oldBottoms[g!];
            int newBottom = GridPlacementService.GetGroupBoundingBox(g!, tiles).MaxRow;
            int shrink = oldBottom - newBottom;
            if (shrink > 0)
            {
                var pulled = GridPlacementService.PullLowerGroupsUp(g!, groups, tiles, shrink);
                foreach (var pt in pulled)
                {
                    if (!modified.Contains(pt)) modified.Add(pt);
                }
            }
        }

        _animateModifiedTilesAction(modified);
        _cleanEmptyGroupsAndReflowAction();
        _updateGroupHeaderPositionsAction();
        _saveGroupsAndLayoutAction();
        _updateCanvasHeightAction();
        _updateExposedAddSlotsAction();
    }

    public void AddFileAsTile(string filePath, double x = 0, double y = 0, bool recordHistory = true)
    {
        if (!File.Exists(filePath) && !Directory.Exists(filePath)) return;

        var tiles = _tilesProvider();
        var groups = _groupsProvider();

        if (recordHistory)
        {
            string preAdd = LayoutHistoryService.CaptureSnapshot(tiles, groups);
            _historyService.PushState(preAdd);
        }

        string title = Path.GetFileNameWithoutExtension(filePath);
        string? iconPath = IconExtractorService.ExtractAndCacheIcon(filePath);

        var sv = _contentScrollViewerProvider();
        double winWidth = _windowWidthProvider();
        double viewportWidth = sv?.ActualWidth > 0 ? sv.ActualWidth : (winWidth > 0 ? winWidth : 1920);
        int maxCols = GridPlacementService.GetMaxCols(viewportWidth);

        int col = x > 0 ? GridPlacementService.ColFromPixel(x) : 0;
        int row = y > 0 ? GridPlacementService.RowFromPixel(y) : 0;

        TileGroupModel? targetGroup = null;
        if (x > 0 || y > 0)
        {
            foreach (var g in groups)
            {
                var (minC, maxC, minR, maxR) = GridPlacementService.GetGroupBoundingBox(g, tiles);
                if (col >= minC && col < maxC && row >= minR && row <= maxR)
                {
                    targetGroup = g;
                    break;
                }
            }
        }

        if (targetGroup != null)
        {
            var tile = new TileModel
            {
                Title = title,
                TargetPath = filePath,
                IconPath = iconPath,
                TileType = TileType.App,
                SpanX = 2,
                SpanY = 2,
                Group = targetGroup.Id,
                SectionHeader = targetGroup.Title
            };

            int clickRelCol = col - targetGroup.Col;
            int clickRelRow = row - (targetGroup.Row + 1);
            var existingGroupTiles = tiles.Where(t => t.Group == targetGroup.Id).ToList();
            var (slotCol, slotRow) = GridPlacementService.FindFreeSlotInGroup(
                targetGroup, clickRelCol, clickRelRow, tile.SpanX, tile.SpanY, existingGroupTiles);
            tile.Col = slotCol;
            tile.Row = slotRow;
            tile.X = GridPlacementService.PixelXFromCol(slotCol);
            tile.Y = GridPlacementService.PixelYFromRow(slotRow);

            tiles.Add(tile);
            var mod = GridPlacementService.PlaceTileInGroup(
                tile, slotCol, slotRow, slotCol, slotRow, targetGroup, tiles);
            var pushed = GridPlacementService.PushLowerGroupsDown(targetGroup, groups, tiles);
            foreach (var pt in pushed)
            {
                if (!mod.Contains(pt)) mod.Add(pt);
            }

            _animateModifiedTilesAction(mod);
            _updateGroupHeaderPositionsAction();
            _saveGroupsAndLayoutAction();
            _updateCanvasHeightAction();
            _updateExposedAddSlotsAction();
            return;
        }

        var (freeCol, freeRow) = GridPlacementService.FindNearestAvailableSlot(
            col, Math.Max(1, row), 2, 2, tiles, null, maxCols, groups);

        var tileUngrouped = new TileModel
        {
            Title = title,
            TargetPath = filePath,
            IconPath = iconPath,
            TileType = TileType.App,
            SpanX = 2,
            SpanY = 2,
            Col = freeCol,
            Row = freeRow,
            X = GridPlacementService.PixelXFromCol(freeCol),
            Y = GridPlacementService.PixelYFromRow(freeRow)
        };

        tiles.Add(tileUngrouped);

        if (groups.Count > 0)
        {
            var looseTiles = tiles.Where(t => string.IsNullOrEmpty(t.Group)).ToList();
            var pushedGroupTiles = GridPlacementService.PushGroupsDownFromLooseTiles(looseTiles, groups, tiles);
            _animateModifiedTilesAction(pushedGroupTiles);
            _updateGroupHeaderPositionsAction();
            _compactGroupGapsAction();
            _saveGroupsAndLayoutAction();
        }

        SaveLayoutAndWorkspace(tiles);
        _updateCanvasHeightAction();
        _updateExposedAddSlotsAction();
    }

    public void AddWebLinkTile(string title, string url, string? iconPath, double x = 0, double y = 0, bool recordHistory = true)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        var tiles = _tilesProvider();
        var groups = _groupsProvider();

        string normalized = WebFaviconService.NormalizeUrl(url);
        string displayTitle = !string.IsNullOrWhiteSpace(title)
            ? title
            : WebFaviconService.InferTitleFromUrl(normalized);

        if (recordHistory)
        {
            string preAdd = LayoutHistoryService.CaptureSnapshot(tiles, groups);
            _historyService.PushState(preAdd);
        }

        var sv = _contentScrollViewerProvider();
        double winWidth = _windowWidthProvider();
        double viewportWidth = sv?.ActualWidth > 0 ? sv.ActualWidth : (winWidth > 0 ? winWidth : 1920);
        int maxCols = GridPlacementService.GetMaxCols(viewportWidth);

        int col = x > 0 ? GridPlacementService.ColFromPixel(x) : 0;
        int row = y > 0 ? GridPlacementService.RowFromPixel(y) : 0;

        TileGroupModel? targetGroup = null;
        if (x > 0 || y > 0)
        {
            foreach (var g in groups)
            {
                var (minC, maxC, minR, maxR) = GridPlacementService.GetGroupBoundingBox(g, tiles);
                if (col >= minC && col < maxC && row >= minR && row <= maxR)
                {
                    targetGroup = g;
                    break;
                }
            }
        }

        TileModel tile;

        if (targetGroup != null)
        {
            tile = new TileModel
            {
                Title = displayTitle,
                TargetPath = normalized,
                IconPath = iconPath,
                TileType = TileType.WebUrl,
                SpanX = 2,
                SpanY = 2,
                Group = targetGroup.Id,
                SectionHeader = targetGroup.Title
            };

            int clickRelCol = col - targetGroup.Col;
            int clickRelRow = row - (targetGroup.Row + 1);
            var existingGroupTiles = tiles.Where(t => t.Group == targetGroup.Id).ToList();
            var (slotCol, slotRow) = GridPlacementService.FindFreeSlotInGroup(
                targetGroup, clickRelCol, clickRelRow, tile.SpanX, tile.SpanY, existingGroupTiles);
            tile.Col = slotCol;
            tile.Row = slotRow;
            tile.X = GridPlacementService.PixelXFromCol(slotCol);
            tile.Y = GridPlacementService.PixelYFromRow(slotRow);

            tiles.Add(tile);
            var mod = GridPlacementService.PlaceTileInGroup(
                tile, slotCol, slotRow, slotCol, slotRow, targetGroup, tiles);
            var pushed = GridPlacementService.PushLowerGroupsDown(targetGroup, groups, tiles);
            foreach (var pt in pushed)
            {
                if (!mod.Contains(pt)) mod.Add(pt);
            }

            _animateModifiedTilesAction(mod);
            _updateGroupHeaderPositionsAction();
            _saveGroupsAndLayoutAction();
            _updateCanvasHeightAction();
            _updateExposedAddSlotsAction();
        }
        else
        {
            var (freeCol, freeRow) = GridPlacementService.FindNearestAvailableSlot(
                col, Math.Max(1, row), 2, 2, tiles, null, maxCols, groups);

            tile = new TileModel
            {
                Title = displayTitle,
                TargetPath = normalized,
                IconPath = iconPath,
                TileType = TileType.WebUrl,
                SpanX = 2,
                SpanY = 2,
                Col = freeCol,
                Row = freeRow,
                X = GridPlacementService.PixelXFromCol(freeCol),
                Y = GridPlacementService.PixelYFromRow(freeRow)
            };

            tiles.Add(tile);

            if (groups.Count > 0)
            {
                var looseTiles = tiles.Where(t => string.IsNullOrEmpty(t.Group)).ToList();
                var pushedGroupTiles = GridPlacementService.PushGroupsDownFromLooseTiles(looseTiles, groups, tiles);
                _animateModifiedTilesAction(pushedGroupTiles);
                _updateGroupHeaderPositionsAction();
                _compactGroupGapsAction();
                _saveGroupsAndLayoutAction();
            }

            SaveLayoutAndWorkspace(tiles);
            _updateCanvasHeightAction();
            _updateExposedAddSlotsAction();
        }

        // Asynchronously fetch high-resolution favicon if not yet available
        if (string.IsNullOrWhiteSpace(iconPath))
        {
            _ = Task.Run(async () =>
            {
                string? fetched = await WebFaviconService.GetFaviconPathAsync(normalized);
                if (!string.IsNullOrWhiteSpace(fetched))
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        tile.IconPath = fetched;
                        SaveLayoutAndWorkspace(tiles);
                    });
                }
            });
        }
    }

    public void BatchAddPastedTiles(
        IReadOnlyList<PasteItemSpec> items,
        Point? targetCanvasPosition = null,
        Func<int, int, int, int, (int Col, int Row)>? customSlotFinder = null)
    {
        if (items == null || items.Count == 0) return;

        var tiles = _tilesProvider();
        var groups = _groupsProvider();

        string prePaste = LayoutHistoryService.CaptureSnapshot(tiles, groups);
        _historyService.PushState(prePaste);

        // Clear previous selection before selecting new pasted batch
        ClearSelection();

        _updateLayoutMetricsAction();
        var sv = _contentScrollViewerProvider();
        double winWidth = _windowWidthProvider();
        double viewportWidth = sv?.ActualWidth > 0 ? sv.ActualWidth : (winWidth > 0 ? winWidth : 1920);
        int maxCols = GridPlacementService.GetMaxCols(viewportWidth);

        // Determine anchor column and row
        int anchorCol;
        int anchorRow;
        if (targetCanvasPosition.HasValue)
        {
            anchorCol = GridPlacementService.ColFromPixel(targetCanvasPosition.Value.X);
            anchorRow = GridPlacementService.RowFromPixel(targetCanvasPosition.Value.Y);
        }
        else
        {
            double vOffset = sv?.VerticalOffset ?? 0;
            anchorCol = 0;
            anchorRow = Math.Max(1, GridPlacementService.RowFromPixel(vOffset));
        }

        TileGroupModel? targetGroup = null;
        if (targetCanvasPosition.HasValue)
        {
            foreach (var g in groups)
            {
                var (minC, maxC, minR, maxR) = GridPlacementService.GetGroupBoundingBox(g, tiles);
                if (anchorCol >= minC && anchorCol < maxC && anchorRow >= minR && anchorRow <= maxR)
                {
                    targetGroup = g;
                    break;
                }
            }
        }

        var newTiles = new List<TileModel>();
        var modifiedTiles = new List<TileModel>();
        int currentCol = anchorCol;
        int currentRow = Math.Max(1, anchorRow);

        foreach (var item in items)
        {
            if (targetGroup != null)
            {
                int clickRelCol = Math.Max(0, currentCol - targetGroup.Col);
                int clickRelRow = Math.Max(0, currentRow - (targetGroup.Row + 1));
                var existingGroupTiles = tiles.Where(t => t.Group == targetGroup.Id).ToList();
                var (slotCol, slotRow) = GridPlacementService.FindFreeSlotInGroup(
                    targetGroup, clickRelCol, clickRelRow, item.SpanX, item.SpanY, existingGroupTiles);

                // Guard G10: Skip invalid slot safely
                if (slotCol < 0 || slotRow < 0) continue;

                var groupTile = new TileModel
                {
                    Title = item.Title,
                    TargetPath = item.TargetPath,
                    IconPath = item.IconPath,
                    TileType = item.TileType,
                    SpanX = item.SpanX,
                    SpanY = item.SpanY,
                    Group = targetGroup.Id,
                    SectionHeader = targetGroup.Title,
                    Col = slotCol,
                    Row = slotRow,
                    X = GridPlacementService.PixelXFromCol(slotCol),
                    Y = GridPlacementService.PixelYFromRow(slotRow),
                    IsSelected = true // Automatically select newly pasted tile
                };

                tiles.Add(groupTile);
                newTiles.Add(groupTile);
                var mod = GridPlacementService.PlaceTileInGroup(
                    groupTile, slotCol, slotRow, slotCol, slotRow, targetGroup, tiles);
                foreach (var m in mod)
                {
                    if (!modifiedTiles.Contains(m)) modifiedTiles.Add(m);
                }

                currentCol = slotCol + item.SpanX;
                var (_, gMaxC, _, _) = GridPlacementService.GetGroupBoundingBox(targetGroup, tiles);
                if (currentCol >= gMaxC)
                {
                    currentCol = targetGroup.Col;
                    currentRow = slotRow + item.SpanY;
                }
            }
            else
            {
                // Guard G10: Search for nearest available slot
                var (freeCol, freeRow) = customSlotFinder != null
                    ? customSlotFinder(currentCol, Math.Max(1, currentRow), item.SpanX, item.SpanY)
                    : GridPlacementService.FindNearestAvailableSlot(
                        currentCol, Math.Max(1, currentRow), item.SpanX, item.SpanY, tiles, null, maxCols, groups);

                if (freeCol < 0 || freeRow < 1) continue;

                var tile = new TileModel
                {
                    Title = item.Title,
                    TargetPath = item.TargetPath,
                    IconPath = item.IconPath,
                    TileType = item.TileType,
                    SpanX = item.SpanX,
                    SpanY = item.SpanY,
                    Col = freeCol,
                    Row = freeRow,
                    X = GridPlacementService.PixelXFromCol(freeCol),
                    Y = GridPlacementService.PixelYFromRow(freeRow),
                    IsSelected = true // Automatically select newly pasted tile
                };

                tiles.Add(tile);
                newTiles.Add(tile);
                if (!modifiedTiles.Contains(tile)) modifiedTiles.Add(tile);

                // Advance horizontal flow across columns, then wrap downwards
                currentCol = freeCol + item.SpanX;
                if (currentCol + item.SpanX > maxCols)
                {
                    currentCol = 0;
                    currentRow = freeRow + item.SpanY;
                }
                else
                {
                    currentRow = freeRow;
                }
            }
        }

        if (newTiles.Count == 0) return;

        // Push lower groups down if needed
        if (targetGroup != null)
        {
            var pushed = GridPlacementService.PushLowerGroupsDown(targetGroup, groups, tiles);
            foreach (var pt in pushed)
            {
                if (!modifiedTiles.Contains(pt)) modifiedTiles.Add(pt);
            }
        }
        else if (groups.Count > 0)
        {
            var looseTiles = tiles.Where(t => string.IsNullOrEmpty(t.Group)).ToList();
            var pushedGroupTiles = GridPlacementService.PushGroupsDownFromLooseTiles(looseTiles, groups, tiles);
            foreach (var pt in pushedGroupTiles)
            {
                if (!modifiedTiles.Contains(pt)) modifiedTiles.Add(pt);
            }
            _compactGroupGapsAction();
        }

        // Single batch animation pass and single atomic layout save
        _animateModifiedTilesAction(modifiedTiles);
        _updateGroupHeaderPositionsAction();
        _saveGroupsAndLayoutAction();
        SaveLayoutAndWorkspace(tiles);
        _updateCanvasHeightAction();
        _updateExposedAddSlotsAction();

        // Guard G11: Asynchronously fetch high-resolution web favicons with fallback
        var pendingWebTiles = newTiles.Where(t => t.TileType == TileType.WebUrl && string.IsNullOrWhiteSpace(t.IconPath)).ToList();
        if (pendingWebTiles.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                bool updated = false;
                foreach (var wt in pendingWebTiles)
                {
                    string? fetched = await WebFaviconService.GetFaviconPathAsync(wt.TargetPath).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(fetched))
                    {
                        fetched = ResolveFallbackWebIcon();
                    }

                    if (!string.IsNullOrWhiteSpace(fetched))
                    {
                        await _dispatcher.InvokeAsync(() =>
                        {
                            wt.IconPath = fetched;
                            updated = true;
                        });
                    }
                }

                if (updated)
                {
                    await _dispatcher.InvokeAsync(() => SaveLayoutAndWorkspace(tiles));
                }
            });
        }
    }

    private static string? ResolveFallbackWebIcon()
    {
        try
        {
            string edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
            if (File.Exists(edge)) return IconExtractorService.ExtractAndCacheIcon(edge);

            string chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
            if (File.Exists(chrome)) return IconExtractorService.ExtractAndCacheIcon(chrome);

            return IconExtractorService.ExtractAndCacheIcon("explorer.exe");
        }
        catch
        {
            return null;
        }
    }

    public void PinCatalogItem(CatalogItemModel item, Point? targetCanvasPosition = null)
    {
        if (item == null) return;

        var tiles = _tilesProvider();
        var groups = _groupsProvider();

        string prePin = LayoutHistoryService.CaptureSnapshot(tiles, groups);
        _historyService.PushState(prePin);

        _updateLayoutMetricsAction();
        int maxCols = GridPlacementService.MaxCols;

        Point clickPoint = targetCanvasPosition ?? new Point(GridPlacementService.OriginX, GridPlacementService.OriginY);

        int col = GridPlacementService.ColFromPixel(clickPoint.X);
        int row = GridPlacementService.RowFromPixel(clickPoint.Y);

        TileGroupModel? targetGroup = null;
        if (targetCanvasPosition != null)
        {
            foreach (var g in groups)
            {
                var (minC, maxC, minR, maxR) = GridPlacementService.GetGroupBoundingBox(g, tiles);
                if (col >= minC && col < maxC && row >= minR && row <= maxR)
                {
                    targetGroup = g;
                    break;
                }
            }
        }

        if (targetGroup != null)
        {
            string? iconPathG = item.TileType == TileType.WebUrl
                ? null
                : IconExtractorService.ExtractAndCacheIcon(item.TargetPath);
            int pinSpanX = Math.Min(item.SpanX > 0 ? item.SpanX : 2, 4);
            int pinSpanY = item.SpanY > 0 ? item.SpanY : 2;
            var groupTile = new TileModel
            {
                Title = item.Name,
                TargetPath = item.TargetPath,
                Arguments = item.Arguments,
                IconPath = iconPathG,
                TileType = item.TileType,
                SpanX = pinSpanX,
                SpanY = pinSpanY,
                Group = targetGroup.Id,
                SectionHeader = targetGroup.Title
            };

            int pinRelCol = col - targetGroup.Col;
            int pinRelRow = row - (targetGroup.Row + 1);
            var existingPinGroupTiles = tiles.Where(t => t.Group == targetGroup.Id).ToList();
            var (pinSlotCol, pinSlotRow) = GridPlacementService.FindFreeSlotInGroup(
                targetGroup, pinRelCol, pinRelRow, pinSpanX, pinSpanY, existingPinGroupTiles);
            groupTile.Col = pinSlotCol;
            groupTile.Row = pinSlotRow;
            groupTile.X = GridPlacementService.PixelXFromCol(pinSlotCol);
            groupTile.Y = GridPlacementService.PixelYFromRow(pinSlotRow);

            tiles.Add(groupTile);

            if (item.TileType == TileType.WebUrl)
            {
                string targetUrl = item.TargetPath;
                var createdTile = groupTile;
                _ = Task.Run(async () =>
                {
                    string? fetched = await WebFaviconService.GetFaviconPathAsync(targetUrl).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(fetched))
                    {
                        await _dispatcher.InvokeAsync(() =>
                        {
                            createdTile.IconPath = fetched;
                            SaveLayoutAndWorkspace(tiles);
                        });
                    }
                });
            }
            var mod = GridPlacementService.PlaceTileInGroup(
                groupTile, pinSlotCol, pinSlotRow, pinSlotCol, pinSlotRow, targetGroup, tiles);
            var pushed = GridPlacementService.PushLowerGroupsDown(targetGroup, groups, tiles);
            foreach (var pt in pushed)
            {
                if (!mod.Contains(pt)) mod.Add(pt);
            }

            _animateModifiedTilesAction(mod);
            _updateGroupHeaderPositionsAction();
            _saveGroupsAndLayoutAction();
            _updateCanvasHeightAction();
            _updateExposedAddSlotsAction();
            return;
        }

        int spanX = item.SpanX > 0 ? item.SpanX : 2;
        int spanY = item.SpanY > 0 ? item.SpanY : 2;

        int clampedCol = Math.Max(0, Math.Min(col, maxCols - spanX));
        int clampedRow = Math.Max(1, row);

        var (freeCol, freeRow) = GridPlacementService.FindNearestAvailableSlot(
            clampedCol,
            clampedRow,
            spanX,
            spanY,
            tiles,
            null,
            maxCols,
            groups);

        string? iconPath = item.TileType == TileType.WebUrl
            ? null
            : IconExtractorService.ExtractAndCacheIcon(item.TargetPath);

        var tile = new TileModel
        {
            Title = item.Name,
            TargetPath = item.TargetPath,
            Arguments = item.Arguments,
            IconPath = iconPath,
            TileType = item.TileType,
            SpanX = spanX,
            SpanY = spanY,
            Col = freeCol,
            Row = freeRow,
            X = GridPlacementService.PixelXFromCol(freeCol),
            Y = GridPlacementService.PixelYFromRow(freeRow)
        };

        tiles.Add(tile);

        if (item.TileType == TileType.WebUrl)
        {
            string targetUrl = item.TargetPath;
            var createdTile = tile;
            _ = Task.Run(async () =>
            {
                string? fetched = await WebFaviconService.GetFaviconPathAsync(targetUrl).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(fetched))
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        createdTile.IconPath = fetched;
                        SaveLayoutAndWorkspace(tiles);
                    });
                }
            });
        }

        if (groups.Count > 0)
        {
            var looseTiles = tiles.Where(t => string.IsNullOrEmpty(t.Group)).ToList();
            var pushedGroupTiles = GridPlacementService.PushGroupsDownFromLooseTiles(looseTiles, groups, tiles);
            _animateModifiedTilesAction(pushedGroupTiles);
            _updateGroupHeaderPositionsAction();
            _compactGroupGapsAction();
            _saveGroupsAndLayoutAction();
        }

        SaveLayoutAndWorkspace(tiles);
        _updateCanvasHeightAction();
        _updateExposedAddSlotsAction();
    }

    public void PinWidget(MetroHub.Widgets.Registry.WidgetDefinition def, Point? targetCanvasPosition = null)
    {
        if (def == null) return;

        var tiles = _tilesProvider();
        var groups = _groupsProvider();

        string prePin = LayoutHistoryService.CaptureSnapshot(tiles, groups);
        _historyService.PushState(prePin);

        _updateLayoutMetricsAction();
        int maxCols = GridPlacementService.MaxCols;

        Point clickPoint = targetCanvasPosition ?? new Point(GridPlacementService.OriginX, GridPlacementService.OriginY);

        int col = GridPlacementService.ColFromPixel(clickPoint.X);
        int row = GridPlacementService.RowFromPixel(clickPoint.Y);

        TileGroupModel? targetGroup = null;
        if (targetCanvasPosition != null)
        {
            foreach (var g in groups)
            {
                var (minC, maxC, minR, maxR) = GridPlacementService.GetGroupBoundingBox(g, tiles);
                if (col >= minC && col < maxC && row >= minR && row <= maxR)
                {
                    targetGroup = g;
                    break;
                }
            }
        }

        int spanX = def.InitialSize.SpanX;
        int spanY = def.InitialSize.SpanY;

        if (targetGroup != null)
        {
            var groupTile = new TileModel
            {
                Title = def.DisplayName,
                TargetPath = def.Id,
                TileType = TileType.Widget,
                SpanX = spanX,
                SpanY = spanY,
                Group = targetGroup.Id,
                SectionHeader = targetGroup.Title
            };

            int pinRelCol = col - targetGroup.Col;
            int pinRelRow = row - (targetGroup.Row + 1);
            var existingPinGroupTiles = tiles.Where(t => t.Group == targetGroup.Id).ToList();
            var (pinSlotCol, pinSlotRow) = GridPlacementService.FindFreeSlotInGroup(
                targetGroup, pinRelCol, pinRelRow, spanX, spanY, existingPinGroupTiles);
            groupTile.Col = pinSlotCol;
            groupTile.Row = pinSlotRow;
            groupTile.X = GridPlacementService.PixelXFromCol(pinSlotCol);
            groupTile.Y = GridPlacementService.PixelYFromRow(pinSlotRow);

            tiles.Add(groupTile);
            var mod = GridPlacementService.PlaceTileInGroup(
                groupTile, pinSlotCol, pinSlotRow, pinSlotCol, pinSlotRow, targetGroup, tiles, groups, false);
            var pushed = GridPlacementService.PushLowerGroupsDown(targetGroup, groups, tiles);
            foreach (var pt in pushed)
            {
                if (!mod.Contains(pt)) mod.Add(pt);
            }

            _animateModifiedTilesAction(mod);
            _updateGroupHeaderPositionsAction();
            _saveGroupsAndLayoutAction();
            _updateCanvasHeightAction();
            _updateExposedAddSlotsAction();
            return;
        }

        int clampedCol = Math.Max(0, Math.Min(col, maxCols - spanX));
        int clampedRow = Math.Max(1, row);

        var (freeCol, freeRow) = GridPlacementService.FindNearestAvailableSlot(
            clampedCol,
            clampedRow,
            spanX,
            spanY,
            tiles,
            null,
            maxCols,
            groups);

        var tile = new TileModel
        {
            Title = def.DisplayName,
            TargetPath = def.Id,
            TileType = TileType.Widget,
            SpanX = spanX,
            SpanY = spanY,
            Col = freeCol,
            Row = freeRow,
            X = GridPlacementService.PixelXFromCol(freeCol),
            Y = GridPlacementService.PixelYFromRow(freeRow)
        };

        tiles.Add(tile);

        var modLoose = GridPlacementService.PlaceAndResolveCollisions(
            tile, freeCol, freeRow, freeCol, freeRow, maxCols, tiles, groups: groups);

        if (groups.Count > 0)
        {
            var looseTiles = tiles.Where(t => string.IsNullOrEmpty(t.Group)).ToList();
            var pushedGroupTiles = GridPlacementService.PushGroupsDownFromLooseTiles(looseTiles, groups, tiles);
            foreach (var pt in pushedGroupTiles)
            {
                if (!modLoose.Contains(pt)) modLoose.Add(pt);
            }
            _updateGroupHeaderPositionsAction();
            _compactGroupGapsAction();
        }

        _animateModifiedTilesAction(modLoose);
        _saveGroupsAndLayoutAction();
        SaveLayoutAndWorkspace(tiles);
        _updateCanvasHeightAction();
        _updateExposedAddSlotsAction();
    }

    private static void SaveLayoutAndWorkspace(ObservableCollection<TileModel> tiles)
    {
        StorageService.SaveLayout(tiles);
        var activeWs = WorkspaceManager.Instance.ActiveWorkspace;
        if (activeWs != null)
        {
            StorageService.SaveWorkspaceLayout(activeWs.Id, tiles);
        }
    }

    /// <summary>
    /// Pins a web link received from the browser extension companion service.
    /// Handles duplicate URL checks, target workspace resolution (active or inactive),
    /// slot finding, tile creation, asynchronous thumbnail/favicon download, and persistence.
    /// </summary>
    public async Task<CompanionPinResult> PinWebLinkFromCompanionAsync(CompanionPinRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Url))
        {
            return new CompanionPinResult(false, false, null, 0, 0, "Missing URL");
        }

        string targetUrl = request.Url.Trim();
        var wm = WorkspaceManager.Instance;

        // 1. Determine target workspace
        WorkspaceModel? targetWorkspace = null;
        if (!string.IsNullOrWhiteSpace(request.WorkspaceId))
        {
            targetWorkspace = wm.Workspaces.FirstOrDefault(w => w.Id.Equals(request.WorkspaceId, StringComparison.OrdinalIgnoreCase));
            if (targetWorkspace == null)
            {
                return new CompanionPinResult(false, false, null, 0, 0, $"Workspace '{request.WorkspaceId}' not found");
            }
        }
        else
        {
            targetWorkspace = wm.ActiveWorkspace;
        }

        bool isActiveWorkspace = targetWorkspace == null || targetWorkspace.Id == wm.ActiveWorkspace?.Id;

        // 2. Select target collections
        var targetTiles = isActiveWorkspace ? _tilesProvider() : targetWorkspace!.Tiles;
        var targetGroups = isActiveWorkspace ? _groupsProvider() : targetWorkspace!.Groups;

        // 3. Deduplication check: if identical URL already exists in this workspace, return existing coordinates
        var existingTile = targetTiles.FirstOrDefault(t =>
            (t.TileType == TileType.WebUrl || t.TileType == TileType.App) &&
            string.Equals(t.TargetPath?.TrimEnd('/'), targetUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

        if (existingTile != null)
        {
            return new CompanionPinResult(
                Success: true,
                Duplicate: true,
                TileId: existingTile.Id,
                Col: existingTile.Col,
                Row: existingTile.Row,
                Message: "Tile already exists in workspace"
            );
        }

        // 4. Dimensions & slot calculation
        int spanX = request.SpanX is 2 or 4 ? request.SpanX.Value : 2;
        int spanY = request.SpanY is 2 ? request.SpanY.Value : 2;
        int maxCols = GridPlacementService.GetMaxCols(_windowWidthProvider());

        var (freeCol, freeRow) = GridPlacementService.FindNearestAvailableSlot(
            0, 1, spanX, spanY, targetTiles, null, maxCols, targetGroups);

        string displayTitle = !string.IsNullOrWhiteSpace(request.Title)
            ? request.Title
            : WebFaviconService.InferTitleFromUrl(targetUrl);

        var tile = new TileModel
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = displayTitle,
            TargetPath = targetUrl,
            TileType = TileType.WebUrl,
            SpanX = spanX,
            SpanY = spanY,
            Col = freeCol,
            Row = freeRow,
            X = GridPlacementService.PixelXFromCol(freeCol),
            Y = GridPlacementService.PixelYFromRow(freeRow)
        };

        targetTiles.Add(tile);

        if (isActiveWorkspace)
        {
            var mod = new List<TileModel> { tile };
            if (targetGroups.Count > 0)
            {
                var looseTiles = targetTiles.Where(t => string.IsNullOrEmpty(t.Group)).ToList();
                var pushed = GridPlacementService.PushGroupsDownFromLooseTiles(looseTiles, targetGroups, targetTiles);
                foreach (var pt in pushed)
                {
                    if (!mod.Contains(pt)) mod.Add(pt);
                }
                _compactGroupGapsAction();
            }

            _animateModifiedTilesAction(mod);
            _updateGroupHeaderPositionsAction();
            _saveGroupsAndLayoutAction();
            SaveLayoutAndWorkspace(targetTiles);
            _updateCanvasHeightAction();
            _updateExposedAddSlotsAction();
        }
        else
        {
            StorageService.SaveWorkspaceLayout(targetWorkspace!.Id, targetTiles);
            targetWorkspace.IsDirty = true;
        }

        // 5. Asynchronous thumbnail / favicon retrieval
        string? thumbUrl = request.ThumbnailUrl;
        _ = Task.Run(async () =>
        {
            string? iconPath = null;
            if (!string.IsNullOrWhiteSpace(thumbUrl))
            {
                iconPath = await CompanionImageDownloader.DownloadImageAsync(thumbUrl).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(iconPath))
            {
                iconPath = await WebFaviconService.GetFaviconPathAsync(targetUrl).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(iconPath))
            {
                iconPath = ResolveFallbackWebIcon();
            }

            if (!string.IsNullOrWhiteSpace(iconPath))
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    tile.IconPath = iconPath;
                    if (isActiveWorkspace)
                    {
                        SaveLayoutAndWorkspace(targetTiles);
                    }
                    else
                    {
                        StorageService.SaveWorkspaceLayout(targetWorkspace!.Id, targetTiles);
                    }
                });
            }
        });

        return new CompanionPinResult(
            Success: true,
            Duplicate: false,
            TileId: tile.Id,
            Col: freeCol,
            Row: freeRow
        );
    }
}
