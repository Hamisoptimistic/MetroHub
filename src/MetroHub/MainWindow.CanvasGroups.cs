using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Presentation.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace MetroHub;

public partial class MainWindow
{
    private const double BaseReferenceWidth = 1920.0;

    private void UpdateScaleFactor(double viewportWidth)
    {
        // 1.0 native pixel scale for 1080p, 768p, and 720p to keep text crisp.
        // Scales up only on 4K monitors (viewport > 2500)
        double scale = viewportWidth > 2500 ? Math.Clamp(viewportWidth / BaseReferenceWidth, 1.0, 1.75) : 1.0;
        CurrentScale = scale;
    }

    private void UpdateLayoutMetrics()
    {
        double viewportWidth = ContentScrollViewer?.ActualWidth > 0
            ? ContentScrollViewer.ActualWidth
            : (Width > 0 ? Width : BaseReferenceWidth);

        UpdateScaleFactor(viewportWidth);

        GridPlacementService.UpdateMetrics(viewportWidth, Groups);
    }

    private void UpdateCanvasHeight()
    {
        if (MainCanvasGrid == null) return;
        double maxTileBottom = (Tiles != null && Tiles.Any()) ? Tiles.Max(t => t.Y + t.HeightPixels) + 120 : 600;
        double maxGroupBottom = (Groups != null && Groups.Any()) ? Groups.Max(g => g.Y + 120) : 600;
        double maxBottom = Math.Max(maxTileBottom, maxGroupBottom);
        double viewportHeight = ContentScrollViewer?.ActualHeight > 0 ? ContentScrollViewer.ActualHeight : 600;
        MainCanvasGrid.MinHeight = Math.Max(viewportHeight, maxBottom);
    }


    private void DiscoverGroupsFromTiles()
    {
        if (Groups.Count > 0) return;

        var grouped = Tiles
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

            Groups.Add(groupModel);
        }

        if (Groups.Count > 0)
        {
            SaveGroupsAndLayout();
        }
    }

    private void EnsureGroupIndices()
    {
        if (Groups.Count == 0) return;

        var groupedByCol = Groups.GroupBy(g => g.ColumnIndex).ToList();
        foreach (var colGroup in groupedByCol)
        {
            var ordered = colGroup.OrderBy(g => g.OrderIndex).ThenBy(g => g.Row).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                ordered[i].OrderIndex = i;
            }
        }
    }

    private void MigrateGroupColumnOffsets()
    {
        if (Groups.Count == 0) return;

        bool changed = false;
        foreach (var group in Groups)
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
            var members = Tiles.Where(t => t.Group == group.Id).ToList();
            foreach (var t in members)
            {
                double expX = GridPlacementService.PixelXFromCol(t.Col);
                if (Math.Abs(t.X - expX) > 0.5)
                {
                    t.X = expX;
                    changed = true;
                }
            }

            var groupMembers = Tiles.Where(t => t.Group == group.Id).ToList();
            if (groupMembers.Count > 0)
            {
                int gMinR = group.Row;
                int gMaxRow = groupMembers.Max(t => t.Row + t.SpanY);
                int gMinCol = Math.Min(group.Col, groupMembers.Min(t => t.Col));
                int gMaxCol = groupMembers.Max(t => t.Col + t.SpanX);

                var trappedLoose = Tiles.Where(t =>
                    t.Group == null && t.Col < gMaxCol && (t.Col + t.SpanX) > gMinCol && t.Row < gMaxRow &&
                    (t.Row + t.SpanY) > gMinR).ToList();
                foreach (var lt in trappedLoose)
                {
                    var (freeCol, freeRow) = GridPlacementService.FindNearestAvailableSlot(gMaxCol, lt.Row,
                        lt.SpanX, lt.SpanY, Tiles, lt, GridPlacementService.MaxCols, Groups);
                    lt.Col = freeCol;
                    lt.Row = freeRow;
                    lt.X = GridPlacementService.PixelXFromCol(freeCol);
                    lt.Y = GridPlacementService.PixelYFromRow(freeRow);
                    changed = true;
                }
            }
        }

        // Compact loose tiles that were artificially pushed by old +1 or +2 sideways gap buffers
        foreach (var lt in Tiles.Where(t => string.IsNullOrEmpty(t.Group)).ToList())
        {
            foreach (var g in Groups)
            {
                var gMembers = Tiles.Where(t => t.Group == g.Id).ToList();
                if (gMembers.Count == 0 && string.IsNullOrWhiteSpace(g.Title)) continue;
                int gMaxC = (gMembers.Count > 0) ? gMembers.Max(t => t.Col + t.SpanX) : g.Col + 2;
                for (int offset = 2; offset >= 1; offset--)
                {
                    if (lt.Col == gMaxC + offset)
                    {
                        if (GridPlacementService.IsRegionFree(gMaxC, lt.Row, lt.SpanX, lt.SpanY, Tiles, lt, GridPlacementService.MaxCols, Groups))
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
        var looseTilesToHeal = Tiles.Where(t => string.IsNullOrEmpty(t.Group)).OrderBy(t => t.Row).ThenBy(t => t.Col).ToList();
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
                    placedLooseTiles.Concat(Tiles.Where(t => !string.IsNullOrEmpty(t.Group))),
                    lt, GridPlacementService.MaxCols, Groups);

                lt.Col = freeC;
                lt.Row = freeR;
                lt.X = GridPlacementService.PixelXFromCol(freeC);
                lt.Y = GridPlacementService.PixelYFromRow(freeR);
                changed = true;
            }
            placedLooseTiles.Add(lt);
        }

        foreach (var lt in Tiles.Where(t => string.IsNullOrEmpty(t.Group)))
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
        if (Groups.Count == 0) return;
        bool changed = false;
        var ordered = Groups.OrderBy(g => g.Row).ToList();
        foreach (var g in ordered)
        {
            var pulled = GridPlacementService.PullLowerGroupsUp(g, Groups, Tiles);
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
        bool anyCleaned = GridPlacementService.CleanEmptyGroups(Groups, Tiles);
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
        foreach (var group in Groups)
        {
            group.X = GridPlacementService.PixelXFromCol(group.Col);
            group.Y = GridPlacementService.PixelYFromRow(group.Row) + 8;

            var container = GroupsListBox?.ItemContainerGenerator.ContainerFromItem(group) as ContentPresenter;
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

            var members = Tiles.Where(t => t.Group == group.Id).ToList();
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

            var plateContainer = GroupTintBackplates?.ItemContainerGenerator.ContainerFromItem(group) as ContentPresenter;
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
        StorageService.SaveLayout(Tiles);
        StorageService.SaveGroups(Groups);
        GridPlacementService.SetActiveGroups(Groups);
        UpdateCanvasHeight();
    }

    public void CreateGroupFromSelectedTiles(TileModel anchorTile)
    {
        if (anchorTile.TileType == TileType.Widget) return;

        List<TileModel> targets = (anchorTile.IsSelected && SelectedTiles.Count > 1)
            ? SelectedTiles.Where(t => t.TileType != TileType.Widget).ToList()
            : new List<TileModel> { anchorTile };

        if (targets.Count == 0) return;

        string pre = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);

        string newGroupId = Guid.NewGuid().ToString("N");
        string defaultTitle = "New Section";

        int targetCol = Math.Max(0, targets.Min(t => GridPlacementService.ColFromPixel(t.X)));
        int targetColIndex = GridPlacementService.GetColumnIndexFromCol(targetCol);

        int minRow = targets.Min(t => GridPlacementService.RowFromPixel(t.Y));
        int targetRow = Math.Max(0, minRow - 1);

        int nextOrder = Groups.Count;

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
            .Select(t => Groups.FirstOrDefault(g => g.Id == t.Group))
            .Where(g => g != null)
            .Distinct()
            .ToList();

        var originOldBottoms = originGroups.ToDictionary(
            g => g!,
            g => GridPlacementService.GetGroupBoundingBox(g!, Tiles).MaxRow);

        var arranged = GridPlacementService.ArrangeTilesInNewGroup(group, targets, targetCol, targetRow, defaultTitle);

        Groups.Add(group);

        var modified = GridPlacementService.InsertGroupAndResolveCollisions(group, targetCol, targetRow, Groups, Tiles);
        foreach (var at in arranged)
        {
            if (!modified.Contains(at)) modified.Add(at);
        }

        ClearTileSelection();

        foreach (var og in originGroups)
        {
            int oldBottom = originOldBottoms[og!];
            int newBottom = GridPlacementService.GetGroupBoundingBox(og!, Tiles).MaxRow;
            int shrink = oldBottom - newBottom;
            if (shrink > 0)
            {
                var pulled = GridPlacementService.PullLowerGroupsUp(og!, Groups, Tiles, shrink);
                foreach (var pt in pulled)
                {
                    if (!modified.Contains(pt)) modified.Add(pt);
                }
            }
        }

        AnimateModifiedTiles(modified);
        UpdateGroupHeaderPositions();
        SaveGroupsAndLayout();
        UpdateExposedAddSlots();

        _historyService.PushState(pre);
    }

    public void AddTilesToExistingGroup(IList<TileModel> incomingTiles, TileGroupModel targetGroup)
    {
        if (incomingTiles == null || incomingTiles.Count == 0 || targetGroup == null) return;

        if (targetGroup.IsLocked)
        {
            FlashLockedGroupPerimeter(targetGroup);
            return;
        }

        var tilesToAdd = incomingTiles.Where(t => t.Group != targetGroup.Id).ToList();
        if (tilesToAdd.Count == 0) return;

        string pre = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);

        var originGroups = tilesToAdd
            .Where(t => !string.IsNullOrEmpty(t.Group))
            .Select(t => Groups.FirstOrDefault(g => g.Id == t.Group))
            .Where(g => g != null)
            .Distinct()
            .ToList();

        var originOldBottoms = originGroups.ToDictionary(
            g => g!,
            g => GridPlacementService.GetGroupBoundingBox(g!, Tiles).MaxRow);

        var modified = GridPlacementService.PlaceTilesInGroup(
            tilesToAdd,
            targetGroup,
            Tiles,
            anchorTile: null,
            dropAnchorCol: targetGroup.Col,
            dropAnchorRow: targetGroup.Row + 1,
            groups: Groups);

        ClearTileSelection();

        foreach (var og in originGroups)
        {
            int oldBottom = originOldBottoms[og!];
            int newBottom = GridPlacementService.GetGroupBoundingBox(og!, Tiles).MaxRow;
            int shrink = oldBottom - newBottom;
            if (shrink > 0)
            {
                var pulled = GridPlacementService.PullLowerGroupsUp(og!, Groups, Tiles, shrink);
                foreach (var pt in pulled)
                {
                    if (!modified.Contains(pt)) modified.Add(pt);
                }
            }
        }

        AnimateModifiedTiles(modified);
        UpdateGroupHeaderPositions();
        SaveGroupsAndLayout();
        UpdateExposedAddSlots();
        UpdateCanvasHeight();

        _historyService.PushState(pre);
    }

    public void SetGroupColor(TileGroupModel group, string hex)
    {
        string pre = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
        group.HeaderColor = hex;
        SaveGroupsAndLayout();
        _historyService.PushState(pre);
    }

    public void SetGroupTintColor(TileGroupModel group, string? hex)
    {
        string pre = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
        group.TintColor = hex;
        UpdateGroupHeaderPositions();
        SaveGroupsAndLayout();
        _historyService.PushState(pre);
    }

    public void ToggleGroupLock(TileGroupModel group)
    {
        string pre = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
        group.IsLocked = !group.IsLocked;
        SaveGroupsAndLayout();
        _historyService.PushState(pre);
    }

    public void RenameGroup(TileGroupModel group, string newTitle)
    {
        if (group.Title == newTitle) return;
        string pre = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
        group.Title = newTitle;
        SaveGroupsAndLayout();
        _historyService.PushState(pre);
    }

    public LayoutHistoryService HistoryService => _historyService;

    public void UngroupTiles(TileGroupModel group)
    {
        if (group.IsLocked)
        {
            FlashLockedGroupPerimeter(group);
            return;
        }

        string pre = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);

        var memberTiles = Tiles.Where(t => t.Group == group.Id).ToList();
        foreach (var t in memberTiles)
        {
            t.Group = null;
            t.SectionHeader = null;
        }

        Groups.Remove(group);

        UpdateGroupHeaderPositions();
        SaveGroupsAndLayout();
        UpdateExposedAddSlots();
        _historyService.PushState(pre);
    }

    public void DeleteGroupAndTiles(TileGroupModel group)
    {
        if (group.IsLocked)
        {
            FlashLockedGroupPerimeter(group);
            return;
        }

        string pre = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
        var memberTiles = Tiles.Where(t => t.Group == group.Id).ToList();
        foreach (var t in memberTiles)
        {
            t.Teardown();
            Tiles.Remove(t);
        }

        Groups.Remove(group);

        var modifiedTiles = GridPlacementService.PullLowerGroupsUp(group, Groups, Tiles);
        AnimateModifiedTiles(modifiedTiles);
        UpdateGroupHeaderPositions(animate: true);
        CompactGroupGaps();
        SaveGroupsAndLayout();
        UpdateCanvasHeight();
        UpdateExposedAddSlots();
        _historyService.PushState(pre);
    }

    private void OnCreateGroupCanvasClick(object sender, RoutedEventArgs e)
    {
        CreateGroupAtPosition(_canvasRightClickPoint);
    }

    public void CreateGroupAtPosition(Point canvasPoint)
    {
        string pre = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);

        int targetCol = Math.Max(0, GridPlacementService.ColFromPixel(canvasPoint.X));
        int targetColIndex = GridPlacementService.GetColumnIndexFromCol(targetCol);
        int targetRow = GridPlacementService.RowFromPixel(canvasPoint.Y);

        int nextOrder = Groups.Count;

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

        Groups.Add(group);

        var modified = GridPlacementService.InsertGroupAndResolveCollisions(group, targetCol, targetRow, Groups, Tiles);
        AnimateModifiedTiles(modified);
        UpdateGroupHeaderPositions();
        SaveGroupsAndLayout();
        UpdateExposedAddSlots();

        _historyService.PushState(pre);
    }

    private TileGroupModel? _activeContextMenuGroup;
    private TileGroupModel? GetGroupAtCanvasPoint(Point pt)
    {
        int col = GridPlacementService.ColFromPixel(pt.X);
        int row = GridPlacementService.RowFromPixel(pt.Y);

        foreach (var g in Groups)
        {
            var (minC, maxC, minR, maxR) = GridPlacementService.GetGroupBoundingBox(g, Tiles);

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
            var gMembers = Tiles.Where(t => t.Group == g.Id).ToList();
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

    private void OnCanvasRenameGroupClick(object sender, RoutedEventArgs e)
    {
        if (_activeContextMenuGroup == null) return;
        if (_activeContextMenuGroup.IsLocked)
        {
            FlashLockedGroupPerimeter(_activeContextMenuGroup);
            return;
        }

        var group = _activeContextMenuGroup;
        var container = GroupsListBox?.ItemContainerGenerator.ContainerFromItem(group) as ContentPresenter;
        if (container != null)
        {
            var headerCtrl = FindVisualChild<Presentation.Controls.GroupHeaderControl>(container);
            if (headerCtrl != null)
            {
                headerCtrl.BeginEdit();
                return;
            }
        }
        group.IsEditing = true;
    }

    private void OnCanvasGroupColorSelectClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Tag is string hex && _activeContextMenuGroup != null)
        {
            SetGroupColor(_activeContextMenuGroup, hex);
        }
    }

    private void OnCanvasGroupTintColorSelectClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && _activeContextMenuGroup != null)
        {
            string? hex = item.Tag as string;
            if (string.Equals(hex, "None", StringComparison.OrdinalIgnoreCase))
            {
                hex = null;
            }
            SetGroupTintColor(_activeContextMenuGroup, hex);
        }
    }

    private void OnCanvasLockGroupClick(object sender, RoutedEventArgs e)
    {
        if (_activeContextMenuGroup != null)
        {
            ToggleGroupLock(_activeContextMenuGroup);
        }
    }

    private void OnCanvasUngroupClick(object sender, RoutedEventArgs e)
    {
        if (_activeContextMenuGroup != null)
        {
            UngroupTiles(_activeContextMenuGroup);
        }
    }

    private void OnCanvasDeleteGroupClick(object sender, RoutedEventArgs e)
    {
        if (_activeContextMenuGroup != null)
        {
            DeleteGroupAndTiles(_activeContextMenuGroup);
        }
    }

}
