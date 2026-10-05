using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using MetroHub.Core.Models;
using MetroHub.Core.Services;

namespace MetroHub.Presentation.Controllers;

/// <summary>
/// Manages canvas group layout, positioning, serialization, ordering migrations,
/// animations, and group CRUD operations.
/// </summary>
public sealed class CanvasGroupManager
{
    private const double BaseReferenceWidth = 1920.0;

    private readonly Func<ObservableCollection<TileGroupModel>> _groupsProvider;
    private readonly Func<ObservableCollection<TileModel>> _tilesProvider;
    private readonly Func<ItemsControl?> _groupsListBoxProvider;
    private readonly Func<ItemsControl?> _groupTintBackplatesProvider;
    private readonly Func<ScrollViewer?> _contentScrollViewerProvider;
    private readonly Func<FrameworkElement?> _mainCanvasGridProvider;
    private readonly Func<double> _windowWidthProvider;
    private readonly Func<IList<TileModel>> _selectedTilesProvider;
    private readonly Action<IList<TileModel>> _animateModifiedTilesAction;
    private readonly Action _updateExposedAddSlotsAction;
    private readonly Action<TileGroupModel> _flashLockedGroupAction;
    private readonly Action _clearTileSelectionAction;
    private readonly LayoutHistoryService _historyService;
    private readonly Action<double>? _setCurrentScaleAction;

    public TileGroupModel? ActiveContextMenuGroup { get; set; }

    public CanvasGroupManager(
        Func<ObservableCollection<TileGroupModel>> groupsProvider,
        Func<ObservableCollection<TileModel>> tilesProvider,
        Func<ItemsControl?> groupsListBoxProvider,
        Func<ItemsControl?> groupTintBackplatesProvider,
        Func<ScrollViewer?> contentScrollViewerProvider,
        Func<FrameworkElement?> mainCanvasGridProvider,
        Func<double> windowWidthProvider,
        Func<IList<TileModel>> selectedTilesProvider,
        Action<IList<TileModel>> animateModifiedTilesAction,
        Action updateExposedAddSlotsAction,
        Action<TileGroupModel> flashLockedGroupAction,
        Action clearTileSelectionAction,
        LayoutHistoryService historyService,
        Action<double>? setCurrentScaleAction = null)
    {
        _groupsProvider = groupsProvider;
        _tilesProvider = tilesProvider;
        _groupsListBoxProvider = groupsListBoxProvider;
        _groupTintBackplatesProvider = groupTintBackplatesProvider;
        _contentScrollViewerProvider = contentScrollViewerProvider;
        _mainCanvasGridProvider = mainCanvasGridProvider;
        _windowWidthProvider = windowWidthProvider;
        _selectedTilesProvider = selectedTilesProvider;
        _animateModifiedTilesAction = animateModifiedTilesAction;
        _updateExposedAddSlotsAction = updateExposedAddSlotsAction;
        _flashLockedGroupAction = flashLockedGroupAction;
        _clearTileSelectionAction = clearTileSelectionAction;
        _historyService = historyService;
        _setCurrentScaleAction = setCurrentScaleAction;
    }

    public void UpdateScaleFactor(double viewportWidth)
    {
        // 1.0 native pixel scale for 1080p, 768p, and 720p to keep text crisp.
        // Scales up only on 4K monitors (viewport > 2500)
        double scale = viewportWidth > 2500 ? Math.Clamp(viewportWidth / BaseReferenceWidth, 1.0, 1.75) : 1.0;
        _setCurrentScaleAction?.Invoke(scale);
    }

    public void UpdateLayoutMetrics()
    {
        var sv = _contentScrollViewerProvider();
        double winWidth = _windowWidthProvider();
        double viewportWidth = sv?.ActualWidth > 0
            ? sv.ActualWidth
            : (winWidth > 0 ? winWidth : BaseReferenceWidth);

        UpdateScaleFactor(viewportWidth);
        GridPlacementService.UpdateMetrics(viewportWidth, _groupsProvider());
    }

    public void UpdateCanvasHeight()
    {
        var grid = _mainCanvasGridProvider();
        if (grid == null) return;
        var tiles = _tilesProvider();
        var groups = _groupsProvider();
        double maxTileBottom = (tiles != null && tiles.Any()) ? tiles.Max(t => t.Y + t.HeightPixels) + 120 : 600;
        double maxGroupBottom = (groups != null && groups.Any()) ? groups.Max(g => g.Y + 120) : 600;
        double maxBottom = Math.Max(maxTileBottom, maxGroupBottom);
        var sv = _contentScrollViewerProvider();
        double viewportHeight = sv?.ActualHeight > 0 ? sv.ActualHeight : 600;
        grid.MinHeight = Math.Max(viewportHeight, maxBottom);
    }

    public void DiscoverGroupsFromTiles()
    {
        var groups = _groupsProvider();
        if (groups.Count > 0) return;

        var tiles = _tilesProvider();
        var grouped = tiles
            .Where(t => !string.IsNullOrWhiteSpace(t.SectionHeader) || !string.IsNullOrWhiteSpace(t.Group))
            .GroupBy(t => !string.IsNullOrWhiteSpace(t.Group) ? t.Group : t.SectionHeader!)
            .ToList();

        foreach (var grp in grouped)
        {
            string title = grp.First().SectionHeader ?? "Group";
            string id = grp.First().Group ?? Guid.NewGuid().ToString("N");

            var groupModel = new TileGroupModel
            {
                Id = id,
                Title = title
            };

            foreach (var t in grp)
            {
                t.Group = id;
                t.SectionHeader = title;
            }

            groups.Add(groupModel);
        }

        if (groups.Count > 0)
        {
            SaveGroupsAndLayout();
        }
    }

    public void EnsureGroupIndices()
    {
        var groups = _groupsProvider();
        if (groups.Count == 0) return;

        var groupedByCol = groups.GroupBy(g => g.ColumnIndex).ToList();
        foreach (var colGroup in groupedByCol)
        {
            var ordered = colGroup.OrderBy(g => g.OrderIndex).ThenBy(g => g.Row).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                ordered[i].OrderIndex = i;
            }
        }
    }

    public void MigrateGroupColumnOffsets()
    {
        var groups = _groupsProvider();
        var tiles = _tilesProvider();
        if (groups.Count == 0) return;

        bool changed = false;
        foreach (var group in groups)
        {
            if (group.Col < 0)
            {
                group.Col = GridPlacementService.GetColumnStartCol(group.ColumnIndex);
            }
            group.ColumnIndex = GridPlacementService.GetColumnIndexFromCol(group.Col);
            double expectedX = GridPlacementService.PixelXFromCol(group.Col);
            if (Math.Abs(group.X - expectedX) > 0.5)
            {
                group.X = expectedX;
                changed = true;
            }
            var members = tiles.Where(t => t.Group == group.Id).ToList();
            foreach (var t in members)
            {
                double expX = GridPlacementService.PixelXFromCol(t.Col);
                if (Math.Abs(t.X - expX) > 0.5)
                {
                    t.X = expX;
                    changed = true;
                }
            }

            var groupMembers = tiles.Where(t => t.Group == group.Id).ToList();
            if (groupMembers.Count > 0)
            {
                int gMinR = group.Row;
                int gMaxRow = groupMembers.Max(t => t.Row + t.SpanY);
                int gMinCol = Math.Min(group.Col, groupMembers.Min(t => t.Col));
                int gMaxCol = groupMembers.Max(t => t.Col + t.SpanX);

                var trappedLoose = tiles.Where(t =>
                    t.Group == null && t.Col < gMaxCol && (t.Col + t.SpanX) > gMinCol && t.Row < gMaxRow &&
                    (t.Row + t.SpanY) > gMinR).ToList();
                foreach (var lt in trappedLoose)
                {
                    var (freeCol, freeRow) = GridPlacementService.FindNearestAvailableSlot(gMaxCol, lt.Row,
                        lt.SpanX, lt.SpanY, tiles, lt, GridPlacementService.MaxCols, groups);
                    lt.Col = freeCol;
                    lt.Row = freeRow;
                    lt.X = GridPlacementService.PixelXFromCol(freeCol);
                    lt.Y = GridPlacementService.PixelYFromRow(freeRow);
                    changed = true;
                }
            }
        }

        // Compact loose tiles that were artificially pushed by old +1 or +2 sideways gap buffers
        foreach (var lt in tiles.Where(t => string.IsNullOrEmpty(t.Group)).ToList())
        {
            foreach (var g in groups)
            {
                var gMembers = tiles.Where(t => t.Group == g.Id).ToList();
                if (gMembers.Count == 0 && string.IsNullOrWhiteSpace(g.Title)) continue;
                int gMaxC = (gMembers.Count > 0) ? gMembers.Max(t => t.Col + t.SpanX) : g.Col + 2;
                for (int offset = 2; offset >= 1; offset--)
                {
                    if (lt.Col == gMaxC + offset)
                    {
                        if (GridPlacementService.IsRegionFree(gMaxC, lt.Row, lt.SpanX, lt.SpanY, tiles, lt, GridPlacementService.MaxCols, groups))
                        {
                            lt.Col = gMaxC;
                            lt.X = GridPlacementService.PixelXFromCol(gMaxC);
                            changed = true;
                            break;
                        }
                    }
                }
            }
        }

        // Auto-heal any loose tiles that currently straddle 8-column track boundaries or overlap
        var looseTilesToHeal = tiles.Where(t => string.IsNullOrEmpty(t.Group)).OrderBy(t => t.Row).ThenBy(t => t.Col).ToList();
        var placedLooseTiles = new List<TileModel>();
        foreach (var lt in looseTilesToHeal)
        {
            int validCol = GridPlacementService.SnapColToValidTrackSlot(lt.Col, lt.SpanX, GridPlacementService.MaxCols, lt.X);
            if (validCol != lt.Col)
            {
                lt.Col = validCol;
                lt.X = GridPlacementService.PixelXFromCol(validCol);
                changed = true;
            }

            bool hasOverlap = placedLooseTiles.Any(other =>
                GridPlacementService.DoTilesOverlap(lt.Col, lt.Row, lt.SpanX, lt.SpanY, other.Col, other.Row, other.SpanX, other.SpanY));

            if (hasOverlap)
            {
                var (freeC, freeR) = GridPlacementService.FindNearestAvailableSlot(
                    lt.Col, lt.Row, lt.SpanX, lt.SpanY,
                    placedLooseTiles.Concat(tiles.Where(t => !string.IsNullOrEmpty(t.Group))),
                    lt, GridPlacementService.MaxCols, groups);

                lt.Col = freeC;
                lt.Row = freeR;
                lt.X = GridPlacementService.PixelXFromCol(freeC);
                lt.Y = GridPlacementService.PixelYFromRow(freeR);
                changed = true;
            }
            placedLooseTiles.Add(lt);
        }

        foreach (var lt in tiles.Where(t => string.IsNullOrEmpty(t.Group)))
        {
            double expX = GridPlacementService.PixelXFromCol(lt.Col);
            if (Math.Abs(lt.X - expX) > 0.5)
            {
                lt.X = expX;
                changed = true;
            }
        }

        if (changed)
        {
            SaveGroupsAndLayout();
        }
    }

    public void CompactGroupGaps()
    {
        var groups = _groupsProvider();
        var tiles = _tilesProvider();
        if (groups.Count == 0) return;
        bool changed = false;
        var ordered = groups.OrderBy(g => g.Row).ToList();
        foreach (var g in ordered)
        {
            var pulled = GridPlacementService.PullLowerGroupsUp(g, groups, tiles);
            if (pulled.Count > 0) changed = true;
        }

        if (changed)
        {
            UpdateGroupHeaderPositions();
            SaveGroupsAndLayout();
        }
    }

    public void CleanEmptyGroupsAndReflow()
    {
        var groups = _groupsProvider();
        var tiles = _tilesProvider();
        bool anyCleaned = GridPlacementService.CleanEmptyGroups(groups, tiles);
        if (anyCleaned)
        {
            UpdateGroupHeaderPositions();
            SaveGroupsAndLayout();
        }
    }

    public void EnsureGroupsHaveHeaderSpace()
    {
        UpdateGroupHeaderPositions();
    }

    public void UpdateGroupHeaderPositions(bool animate = false)
    {
        var groups = _groupsProvider();
        var tiles = _tilesProvider();
        var groupsListBox = _groupsListBoxProvider();
        var groupTintBackplates = _groupTintBackplatesProvider();

        foreach (var group in groups)
        {
            group.X = GridPlacementService.PixelXFromCol(group.Col);
            group.Y = GridPlacementService.PixelYFromRow(group.Row) + 8;

            var container = groupsListBox?.ItemContainerGenerator.ContainerFromItem(group) as ContentPresenter;
            if (container != null)
            {
                if (animate)
                {
                    double curL = Canvas.GetLeft(container);
                    double curT = Canvas.GetTop(container);
                    if (double.IsNaN(curL)) curL = group.X;
                    if (double.IsNaN(curT)) curT = group.Y;

                    if (Math.Abs(curL - group.X) > 0.5 || Math.Abs(curT - group.Y) > 0.5)
                    {
                        var animX = new DoubleAnimation(curL, group.X, TimeSpan.FromMilliseconds(220))
                        {
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                        };
                        var animY = new DoubleAnimation(curT, group.Y, TimeSpan.FromMilliseconds(220))
                        {
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                        };
                        animX.Completed += (s, ev) =>
                        {
                            Canvas.SetLeft(container, group.X);
                            container.BeginAnimation(Canvas.LeftProperty, null);
                        };
                        animY.Completed += (s, ev) =>
                        {
                            Canvas.SetTop(container, group.Y);
                            container.BeginAnimation(Canvas.TopProperty, null);
                        };
                        container.BeginAnimation(Canvas.LeftProperty, animX);
                        container.BeginAnimation(Canvas.TopProperty, animY);
                    }
                    else
                    {
                        Canvas.SetLeft(container, group.X);
                        Canvas.SetTop(container, group.Y);
                    }
                }
                else
                {
                    container.BeginAnimation(Canvas.LeftProperty, null);
                    container.BeginAnimation(Canvas.TopProperty, null);
                    Canvas.SetLeft(container, group.X);
                    Canvas.SetTop(container, group.Y);
                }
            }

            var members = tiles.Where(t => t.Group == group.Id).ToList();
            if (members.Count > 0)
            {
                int minMemberCol = members.Min(t => t.Col);
                int maxMemberCol = members.Max(t => t.Col + t.SpanX);
                int colSpan = Math.Max(1, maxMemberCol - minMemberCol);

                int minMemberRow = members.Min(t => t.Row);
                int maxMemberBottom = members.Max(t => t.Row + t.SpanY);
                int rowSpan = Math.Max(1, maxMemberBottom - minMemberRow);

                const double PlatePadding = GridPlacementService.Gap * 0.5;

                group.PlateX = GridPlacementService.PixelXFromCol(minMemberCol) - PlatePadding;
                group.PlateY = GridPlacementService.PixelYFromRow(minMemberRow) - PlatePadding;
                group.PlateWidth = (colSpan * GridPlacementService.GridStep) - GridPlacementService.Gap + (PlatePadding * 2);
                group.PlateHeight = (rowSpan * GridPlacementService.GridStep) - GridPlacementService.Gap + (PlatePadding * 2);
            }
            else
            {
                group.PlateWidth = 0;
                group.PlateHeight = 0;
            }

            var plateContainer = groupTintBackplates?.ItemContainerGenerator.ContainerFromItem(group) as ContentPresenter;
            if (plateContainer != null)
            {
                if (animate)
                {
                    double curL = Canvas.GetLeft(plateContainer);
                    double curT = Canvas.GetTop(plateContainer);
                    if (double.IsNaN(curL)) curL = group.PlateX;
                    if (double.IsNaN(curT)) curT = group.PlateY;

                    if (Math.Abs(curL - group.PlateX) > 0.5 || Math.Abs(curT - group.PlateY) > 0.5)
                    {
                        var animX = new DoubleAnimation(curL, group.PlateX, TimeSpan.FromMilliseconds(220))
                        {
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                        };
                        var animY = new DoubleAnimation(curT, group.PlateY, TimeSpan.FromMilliseconds(220))
                        {
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                        };
                        animX.Completed += (s, ev) =>
                        {
                            Canvas.SetLeft(plateContainer, group.PlateX);
                            plateContainer.BeginAnimation(Canvas.LeftProperty, null);
                        };
                        animY.Completed += (s, ev) =>
                        {
                            Canvas.SetTop(plateContainer, group.PlateY);
                            plateContainer.BeginAnimation(Canvas.TopProperty, null);
                        };
                        plateContainer.BeginAnimation(Canvas.LeftProperty, animX);
                        plateContainer.BeginAnimation(Canvas.TopProperty, animY);
                    }
                    else
                    {
                        Canvas.SetLeft(plateContainer, group.PlateX);
                        Canvas.SetTop(plateContainer, group.PlateY);
                    }
                }
                else
                {
                    plateContainer.BeginAnimation(Canvas.LeftProperty, null);
                    plateContainer.BeginAnimation(Canvas.TopProperty, null);
                    Canvas.SetLeft(plateContainer, group.PlateX);
                    Canvas.SetTop(plateContainer, group.PlateY);
                }
            }
        }
    }

    public void SaveGroupsAndLayout()
    {
        var tiles = _tilesProvider();
        var groups = _groupsProvider();
        StorageService.SaveLayout(tiles);
        StorageService.SaveGroups(groups);

        var activeWs = WorkspaceManager.Instance.ActiveWorkspace;
        if (activeWs != null)
        {
            StorageService.SaveWorkspaceLayout(activeWs.Id, tiles);
            StorageService.SaveWorkspaceGroups(activeWs.Id, groups);
        }

        GridPlacementService.SetActiveGroups(groups);
        UpdateCanvasHeight();
    }

    public void CreateGroupFromSelectedTiles(TileModel anchorTile)
    {
        if (anchorTile.TileType == TileType.Widget) return;

        var selected = _selectedTilesProvider();
        List<TileModel> targets = (anchorTile.IsSelected && selected.Count > 1)
            ? selected.Where(t => t.TileType != TileType.Widget).ToList()
            : new List<TileModel> { anchorTile };

        if (targets.Count == 0) return;

        var tiles = _tilesProvider();
        var groups = _groupsProvider();

        string pre = LayoutHistoryService.CaptureSnapshot(tiles, groups);

        string newGroupId = Guid.NewGuid().ToString("N");
        string defaultTitle = "New Section";

        int targetCol = Math.Max(0, targets.Min(t => GridPlacementService.ColFromPixel(t.X)));
        int targetColIndex = GridPlacementService.GetColumnIndexFromCol(targetCol);

        int minRow = targets.Min(t => GridPlacementService.RowFromPixel(t.Y));
        int targetRow = Math.Max(0, minRow - 1);

        int nextOrder = groups.Count;

        var group = new TileGroupModel
        {
            Id = newGroupId,
            Title = defaultTitle,
            ColumnIndex = targetColIndex,
            OrderIndex = nextOrder,
            Col = targetCol,
            Row = targetRow,
            IsEditing = true
        };

        var originGroups = targets
            .Where(t => !string.IsNullOrEmpty(t.Group))
            .Select(t => groups.FirstOrDefault(g => g.Id == t.Group))
            .Where(g => g != null)
            .Distinct()
            .ToList();

        var originOldBottoms = originGroups.ToDictionary(
            g => g!,
            g => GridPlacementService.GetGroupBoundingBox(g!, tiles).MaxRow);

        var arranged = GridPlacementService.ArrangeTilesInNewGroup(group, targets, targetCol, targetRow, defaultTitle);

        groups.Add(group);

        var modified = GridPlacementService.InsertGroupAndResolveCollisions(group, targetCol, targetRow, groups, tiles);
        foreach (var at in arranged)
        {
            if (!modified.Contains(at)) modified.Add(at);
        }

        _clearTileSelectionAction();

        foreach (var og in originGroups)
        {
            int oldBottom = originOldBottoms[og!];
            int newBottom = GridPlacementService.GetGroupBoundingBox(og!, tiles).MaxRow;
            int shrink = oldBottom - newBottom;
            if (shrink > 0)
            {
                var pulled = GridPlacementService.PullLowerGroupsUp(og!, groups, tiles, shrink);
                foreach (var pt in pulled)
                {
                    if (!modified.Contains(pt)) modified.Add(pt);
                }
            }
        }

        _animateModifiedTilesAction(modified);
        UpdateGroupHeaderPositions();
        SaveGroupsAndLayout();
        _updateExposedAddSlotsAction();

        _historyService.PushState(pre);
    }

    public void AddTilesToExistingGroup(IList<TileModel> incomingTiles, TileGroupModel targetGroup)
    {
        if (incomingTiles == null || incomingTiles.Count == 0 || targetGroup == null) return;

        if (targetGroup.IsLocked)
        {
            _flashLockedGroupAction(targetGroup);
            return;
        }

        var tilesToAdd = incomingTiles.Where(t => t.Group != targetGroup.Id).ToList();
        if (tilesToAdd.Count == 0) return;

        var tiles = _tilesProvider();
        var groups = _groupsProvider();

        string pre = LayoutHistoryService.CaptureSnapshot(tiles, groups);

        var originGroups = tilesToAdd
            .Where(t => !string.IsNullOrEmpty(t.Group))
            .Select(t => groups.FirstOrDefault(g => g.Id == t.Group))
            .Where(g => g != null)
            .Distinct()
            .ToList();

        var originOldBottoms = originGroups.ToDictionary(
            g => g!,
            g => GridPlacementService.GetGroupBoundingBox(g!, tiles).MaxRow);

        var modified = GridPlacementService.PlaceTilesInGroup(
            tilesToAdd,
            targetGroup,
            tiles,
            anchorTile: null,
            dropAnchorCol: targetGroup.Col,
            dropAnchorRow: targetGroup.Row + 1,
            groups: groups);

        _clearTileSelectionAction();

        foreach (var og in originGroups)
        {
            int oldBottom = originOldBottoms[og!];
            int newBottom = GridPlacementService.GetGroupBoundingBox(og!, tiles).MaxRow;
            int shrink = oldBottom - newBottom;
            if (shrink > 0)
            {
                var pulled = GridPlacementService.PullLowerGroupsUp(og!, groups, tiles, shrink);
                foreach (var pt in pulled)
                {
                    if (!modified.Contains(pt)) modified.Add(pt);
                }
            }
        }

        _animateModifiedTilesAction(modified);
        UpdateGroupHeaderPositions();
        SaveGroupsAndLayout();
        _updateExposedAddSlotsAction();
        UpdateCanvasHeight();

        _historyService.PushState(pre);
    }

    public void SetGroupColor(TileGroupModel group, string hex)
    {
        string pre = LayoutHistoryService.CaptureSnapshot(_tilesProvider(), _groupsProvider());
        group.HeaderColor = hex;
        SaveGroupsAndLayout();
        _historyService.PushState(pre);
    }

    public void SetGroupTintColor(TileGroupModel group, string? hex)
    {
        string pre = LayoutHistoryService.CaptureSnapshot(_tilesProvider(), _groupsProvider());
        group.TintColor = hex;
        UpdateGroupHeaderPositions();
        SaveGroupsAndLayout();
        _historyService.PushState(pre);
    }

    public void ToggleGroupLock(TileGroupModel group)
    {
        string pre = LayoutHistoryService.CaptureSnapshot(_tilesProvider(), _groupsProvider());
        group.IsLocked = !group.IsLocked;
        SaveGroupsAndLayout();
        _historyService.PushState(pre);
    }

    public void RenameGroup(TileGroupModel group, string newTitle)
    {
        if (group.Title == newTitle) return;
        string pre = LayoutHistoryService.CaptureSnapshot(_tilesProvider(), _groupsProvider());
        group.Title = newTitle;
        SaveGroupsAndLayout();
        _historyService.PushState(pre);
    }

    public void UngroupTiles(TileGroupModel group)
    {
        if (group.IsLocked)
        {
            _flashLockedGroupAction(group);
            return;
        }

        var tiles = _tilesProvider();
        var groups = _groupsProvider();
        string pre = LayoutHistoryService.CaptureSnapshot(tiles, groups);

        var memberTiles = tiles.Where(t => t.Group == group.Id).ToList();
        foreach (var t in memberTiles)
        {
            t.Group = null;
            t.SectionHeader = null;
        }

        groups.Remove(group);

        UpdateGroupHeaderPositions();
        SaveGroupsAndLayout();
        _updateExposedAddSlotsAction();
        _historyService.PushState(pre);
    }

    public void DeleteGroupAndTiles(TileGroupModel group)
    {
        if (group.IsLocked)
        {
            _flashLockedGroupAction(group);
            return;
        }

        var tiles = _tilesProvider();
        var groups = _groupsProvider();
        string pre = LayoutHistoryService.CaptureSnapshot(tiles, groups);
        var memberTiles = tiles.Where(t => t.Group == group.Id).ToList();
        foreach (var t in memberTiles)
        {
            t.Teardown();
            tiles.Remove(t);
        }

        groups.Remove(group);

        var modifiedTiles = GridPlacementService.PullLowerGroupsUp(group, groups, tiles);
        _animateModifiedTilesAction(modifiedTiles);
        UpdateGroupHeaderPositions(animate: true);
        CompactGroupGaps();
        SaveGroupsAndLayout();
        UpdateCanvasHeight();
        _updateExposedAddSlotsAction();
        _historyService.PushState(pre);
    }

    public void CreateGroupAtPosition(Point canvasPoint)
    {
        var tiles = _tilesProvider();
        var groups = _groupsProvider();
        string pre = LayoutHistoryService.CaptureSnapshot(tiles, groups);

        int targetCol = Math.Max(0, GridPlacementService.ColFromPixel(canvasPoint.X));
        int targetColIndex = GridPlacementService.GetColumnIndexFromCol(targetCol);
        int targetRow = GridPlacementService.RowFromPixel(canvasPoint.Y);

        int nextOrder = groups.Count;

        var group = new TileGroupModel
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = "New Section",
            ColumnIndex = targetColIndex,
            OrderIndex = nextOrder,
            Col = targetCol,
            Row = targetRow,
            IsEditing = true
        };

        groups.Add(group);

        var modified = GridPlacementService.InsertGroupAndResolveCollisions(group, targetCol, targetRow, groups, tiles);
        _animateModifiedTilesAction(modified);
        UpdateGroupHeaderPositions();
        SaveGroupsAndLayout();
        _updateExposedAddSlotsAction();

        _historyService.PushState(pre);
    }

    public TileGroupModel? GetGroupAtCanvasPoint(Point pt)
    {
        var groups = _groupsProvider();
        var tiles = _tilesProvider();

        int col = GridPlacementService.ColFromPixel(pt.X);
        int row = GridPlacementService.RowFromPixel(pt.Y);

        foreach (var g in groups)
        {
            var (minC, maxC, minR, maxR) = GridPlacementService.GetGroupBoundingBox(g, tiles);

            // 1. Check logical grid cell bounds
            if (col >= minC && col < maxC && row >= minR && row <= maxR)
            {
                return g;
            }

            // 2. Check acrylic backplate bounds
            if (g.PlateWidth > 0 && g.PlateHeight > 0)
            {
                Rect plateRect = new Rect(g.PlateX, g.PlateY, g.PlateWidth, g.PlateHeight);
                if (plateRect.Contains(pt))
                {
                    return g;
                }
            }

            // 3. Check group header area [g.X, g.Y, headerWidth, 36]
            var gMembers = tiles.Where(t => t.Group == g.Id).ToList();
            int gSpan = (gMembers.Count > 0) ? (gMembers.Max(t => t.Col + t.SpanX) - g.Col) : 2;
            double headerWidth = (Math.Max(2, gSpan) * GridPlacementService.GridStep) - GridPlacementService.Gap;
            Rect headerRect = new Rect(g.X, g.Y, headerWidth, 36);
            if (headerRect.Contains(pt))
            {
                return g;
            }
        }

        return null;
    }
}
