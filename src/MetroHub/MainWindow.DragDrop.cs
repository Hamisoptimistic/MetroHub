using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using ContextMenu = System.Windows.Controls.ContextMenu;

namespace MetroHub;

public partial class MainWindow
{
    private DispatcherTimer? _autoScrollTimer;
    private double _autoScrollVelocityY;
    private string? _preDragLayoutSnapshot;

    private TileModel? _draggedTile;
    private Presentation.Controls.TileControl? _draggedControl;
    private ContentPresenter? _draggedContainer;
    private Point _dragStartPoint;
    private double _dragOffsetX;
    private double _dragOffsetY;
    private int _dragOriginalCol;
    private int _dragOriginalRow;
    private bool _isPotentialDrag;
    private bool _isDragging;
    private bool _isLaunchingTile;
    private DateTime _lastTileLaunchTime = DateTime.MinValue;
    private const int GroupColWidth = 8;
    private TileGroupModel? _hoveredTargetGroup;
    private bool _isGroupDrag;
    private TileGroupModel? _draggedGroupModel;
    private double _draggedGroupOffsetX;
    private double _draggedGroupOffsetY;
    private double _draggedPlateOffsetX;
    private double _draggedPlateOffsetY;
    private int _groupDragTargetCol;
    private int _groupDragTargetColIndex;
    private int _groupDragTargetRow;

    private bool _isRubberBanding;
    private Point _rubberBandStartPoint;
    private bool _rubberBandHasMoved;
    private HashSet<TileModel> _preRubberBandSelected = new();
    private List<TileModel> _draggedCluster = new();
    private Dictionary<TileModel, (double X, double Y, int Col, int Row)> _dragClusterOriginals = new();
    private (double MinRelX, double MaxRelX, double MinRelY, double MaxRelY) _clusterRelBounds;
    private (int MinRelCol, int MaxRelCol, int MinRelRow, int MaxRelRow) _clusterRelGridBounds;
    private bool _dragBeganWithSelection;

    private void SetupAutoScrollTimer()
    {
        _autoScrollTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _autoScrollTimer.Tick += OnAutoScrollTick;
    }

    private void StopAutoScroll()
    {
        _autoScrollVelocityY = 0;
        if (_autoScrollTimer != null && _autoScrollTimer.IsEnabled)
        {
            _autoScrollTimer.Stop();
        }
    }

    private void UpdateAutoScrollVelocity()
    {
        if (!_isDragging || ContentScrollViewer == null)
        {
            StopAutoScroll();
            return;
        }

        Point svMouse = Mouse.GetPosition(ContentScrollViewer);
        double svHeight = ContentScrollViewer.ActualHeight;
        if (svHeight <= 0)
        {
            StopAutoScroll();
            return;
        }

        const double edgeThreshold = 80.0;
        const double maxVelocity = 24.0;

        if (svMouse.Y > svHeight - edgeThreshold && svMouse.Y <= svHeight + 120)
        {
            double excess = svMouse.Y - (svHeight - edgeThreshold);
            double fraction = Math.Clamp(excess / edgeThreshold, 0.15, 2.0);
            _autoScrollVelocityY = fraction * maxVelocity;
            if (_autoScrollTimer != null && !_autoScrollTimer.IsEnabled)
            {
                _autoScrollTimer.Start();
            }
        }
        else if (svMouse.Y < edgeThreshold && svMouse.Y >= -120)
        {
            double excess = edgeThreshold - svMouse.Y;
            double fraction = Math.Clamp(excess / edgeThreshold, 0.15, 2.0);
            _autoScrollVelocityY = -fraction * maxVelocity;
            if (_autoScrollTimer != null && !_autoScrollTimer.IsEnabled)
            {
                _autoScrollTimer.Start();
            }
        }
        else
        {
            StopAutoScroll();
        }
    }

    private void OnAutoScrollTick(object? sender, EventArgs e)
    {
        if (!_isDragging || ContentScrollViewer == null || Math.Abs(_autoScrollVelocityY) < 0.1)
        {
            StopAutoScroll();
            return;
        }

        UpdateAutoScrollVelocity();
        if (Math.Abs(_autoScrollVelocityY) < 0.1) return;

        if (_autoScrollVelocityY > 0 && MainCanvasGrid != null)
        {
            double currentBottom = ContentScrollViewer.VerticalOffset + ContentScrollViewer.ActualHeight;
            if (currentBottom + 300 > MainCanvasGrid.MinHeight)
            {
                MainCanvasGrid.MinHeight = currentBottom + 600;
                ContentScrollViewer.UpdateLayout();
            }
        }

        double newOffset = Math.Clamp(
            ContentScrollViewer.VerticalOffset + _autoScrollVelocityY,
            0,
            Math.Max(0, ContentScrollViewer.ScrollableHeight));

        ContentScrollViewer.ScrollToVerticalOffset(newOffset);
        ContentScrollViewer.UpdateLayout();

        Point currentCanvasMouse = TilesListBox != null 
            ? Mouse.GetPosition(TilesListBox) 
            : (MainCanvasGrid != null ? Mouse.GetPosition(MainCanvasGrid) : Mouse.GetPosition(this));
        ProcessDragMovement(currentCanvasMouse);
    }

    private void OnCanvasPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            CancelActiveDrag();
        }

        if (e.LeftButton != MouseButtonState.Pressed) return;

        DependencyObject? dep = e.OriginalSource as DependencyObject;

        if (FindParent<System.Windows.Controls.Primitives.Thumb>(dep) != null)
        {
            _isPotentialDrag = false;
            _isDragging = false;
            _draggedTile = null;
            return;
        }

        if (FindParent<Presentation.Controls.GroupHeaderControl>(dep) != null)
        {
            _isPotentialDrag = false;
            _isDragging = false;
            _draggedTile = null;
            return;
        }

        if (AllAppsDrawer != null && AllAppsDrawer.IsOpen)
        {
            if (!AllAppsDrawer.IsMouseOver && (SidebarRail == null || !SidebarRail.IsMouseOver))
            {
                AllAppsDrawer.Close();
                SidebarRail?.SetAppsDrawerActive(false);
            }
        }

        if (SidebarRail != null && SidebarRail.IsMouseOver) return;
        if (AllAppsDrawer != null && AllAppsDrawer.IsMouseOver) return;

        Point canvasMouse = TilesListBox != null ? e.GetPosition(TilesListBox) : e.GetPosition(this);
        bool isCtrlDown = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        if (IsInteractiveElement(dep))
        {
            _isPotentialDrag = false;
            _isDragging = false;
            _draggedTile = null;
            return;
        }

        var tileControl = FindParent<Presentation.Controls.TileControl>(dep);

        if (tileControl != null && tileControl.DataContext is TileModel tile)
        {
            _preDragLayoutSnapshot = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
            _isRubberBanding = false;
            _draggedTile = tile;

            _draggedControl = tileControl;
            _draggedContainer = FindParent<ContentPresenter>(tileControl)
                                ?? TilesListBox?.ItemContainerGenerator.ContainerFromItem(tile) as ContentPresenter;
            _dragStartPoint = e.GetPosition(this);

            _dragOriginalCol = GridPlacementService.ColFromPixel(tile.X);
            _dragOriginalRow = GridPlacementService.RowFromPixel(tile.Y);

            _dragOffsetX = canvasMouse.X - tile.X;
            _dragOffsetY = canvasMouse.Y - tile.Y;

            if (isCtrlDown)
            {
                tile.IsSelected = !tile.IsSelected;
                _dragBeganWithSelection = tile.IsSelected;
            }
            else
            {
                if (!tile.IsSelected)
                {
                    ClearTileSelection();
                    tile.IsSelected = true;
                    _dragBeganWithSelection = false;
                }
                else
                {
                    _dragBeganWithSelection = true;
                }
            }

            if (tile.IsSelected)
            {
                _draggedCluster = Tiles.Where(t => t.IsSelected).ToList();
            }
            else
            {
                _draggedCluster = new List<TileModel> { tile };
            }

            _dragClusterOriginals.Clear();
            double minRelX = 0, maxRelX = tile.WidthPixels, minRelY = 0, maxRelY = tile.HeightPixels;
            int minRelCol = 0, maxRelCol = tile.SpanX, minRelRow = 0, maxRelRow = tile.SpanY;

            foreach (var cTile in _draggedCluster)
            {
                int cCol = GridPlacementService.ColFromPixel(cTile.X);
                int cRow = GridPlacementService.RowFromPixel(cTile.Y);
                _dragClusterOriginals[cTile] = (cTile.X, cTile.Y, cCol, cRow);

                double relX = cTile.X - tile.X;
                double relY = cTile.Y - tile.Y;
                int relCol = cCol - _dragOriginalCol;
                int relRow = cRow - _dragOriginalRow;

                minRelX = Math.Min(minRelX, relX);
                maxRelX = Math.Max(maxRelX, relX + cTile.WidthPixels);
                minRelY = Math.Min(minRelY, relY);
                maxRelY = Math.Max(maxRelY, relY + cTile.HeightPixels);

                minRelCol = Math.Min(minRelCol, relCol);
                maxRelCol = Math.Max(maxRelCol, relCol + cTile.SpanX);
                minRelRow = Math.Min(minRelRow, relRow);
                maxRelRow = Math.Max(maxRelRow, relRow + cTile.SpanY);
            }

            _clusterRelBounds = (minRelX, maxRelX, minRelY, maxRelY);
            _clusterRelGridBounds = (minRelCol, maxRelCol, minRelRow, maxRelRow);

            _isPotentialDrag = true;
            _isDragging = false;
            if (!(tile.TileType == TileType.Widget && !string.Equals(tile.TargetPath, "stub", StringComparison.OrdinalIgnoreCase)))
            {
                _draggedControl.AnimatePressDown();
            }
        }
        else
        {
            _isPotentialDrag = false;
            _isDragging = false;
            _draggedTile = null;
            _draggedControl = null;
            _draggedCluster.Clear();

            if (IsInteractiveElement(dep))
            {
                return;
            }

            _isRubberBanding = true;
            _rubberBandHasMoved = false;
            _rubberBandStartPoint = canvasMouse;
            _preRubberBandSelected = new HashSet<TileModel>(Tiles.Where(t => t.IsSelected));

            if (!isCtrlDown)
            {
                ClearTileSelection();
                _preRubberBandSelected.Clear();
            }

            if (RubberBandBox != null)
            {
                Canvas.SetLeft(RubberBandBox, canvasMouse.X);
                Canvas.SetTop(RubberBandBox, canvasMouse.Y);
                RubberBandBox.Width = 0;
                RubberBandBox.Height = 0;
                RubberBandBox.Visibility = Visibility.Collapsed;
            }

            RootGrid.CaptureMouse();
        }
    }

    private void OnCanvasPreviewMouseMove(object sender, MouseEventArgs e)
    {
        Point canvasMouse = TilesListBox != null ? e.GetPosition(TilesListBox) : e.GetPosition(this);

        if (_isRubberBanding)
        {
            Vector diff = canvasMouse - _rubberBandStartPoint;
            if (Math.Abs(diff.X) > 3 || Math.Abs(diff.Y) > 3)
            {
                _rubberBandHasMoved = true;
            }

            if (_rubberBandHasMoved && RubberBandBox != null)
            {
                double left = Math.Min(_rubberBandStartPoint.X, canvasMouse.X);
                double top = Math.Min(_rubberBandStartPoint.Y, canvasMouse.Y);
                double width = Math.Abs(canvasMouse.X - _rubberBandStartPoint.X);
                double height = Math.Abs(canvasMouse.Y - _rubberBandStartPoint.Y);

                Canvas.SetLeft(RubberBandBox, left);
                Canvas.SetTop(RubberBandBox, top);
                RubberBandBox.Width = width;
                RubberBandBox.Height = height;
                RubberBandBox.Visibility = Visibility.Visible;

                var marqueeRect = new Rect(left, top, width, height);
                bool isCtrlDown = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

                foreach (var t in Tiles)
                {
                    var tileRect = new Rect(t.X, t.Y, t.WidthPixels, t.HeightPixels);
                    bool intersects = marqueeRect.IntersectsWith(tileRect);

                    if (isCtrlDown)
                    {
                        t.IsSelected = intersects
                            ? !_preRubberBandSelected.Contains(t)
                            : _preRubberBandSelected.Contains(t);
                    }
                    else
                    {
                        t.IsSelected = intersects;
                    }
                }
            }

            return;
        }

        if (_isPotentialDrag && !_isDragging)
        {
            if (e.LeftButton != MouseButtonState.Pressed && Mouse.LeftButton != MouseButtonState.Pressed)
            {
                CancelActiveDrag();
                return;
            }

            if (_draggedTile != null)
            {
                bool lockedTile = _draggedCluster.Any(t =>
                    t.IsLocked || (!string.IsNullOrEmpty(t.Group) &&
                                   Groups.FirstOrDefault(g => g.Id == t.Group)?.IsLocked == true));

                Point current = e.GetPosition(this);
                Vector diff = current - _dragStartPoint;
                if (lockedTile && (Math.Abs(diff.X) > 5 || Math.Abs(diff.Y) > 5))
                {
                    _isPotentialDrag = false;
                    var g = Groups.FirstOrDefault(gr => gr.Id == _draggedTile?.Group);
                    if (g != null && g.IsLocked)
                    {
                        FlashLockedGroupPerimeter(g);
                    }

                    _draggedControl?.AnimateRelease();
                    _draggedTile = null;
                    _draggedControl = null;
                    _draggedContainer = null;
                    return;
                }

                if (!lockedTile && (Math.Abs(diff.X) > 5 || Math.Abs(diff.Y) > 5))
                {
                    _isDragging = true;
                    ClearAllAmbientReveals();
                    RootGrid.CaptureMouse();

                    foreach (var cTile in _draggedCluster)
                    {
                        cTile.IsBeingDragged = true;
                        var control = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, cTile));
                        control?.AnimateElevationLift();

                        var container = TilesListBox?.ItemContainerGenerator.ContainerFromItem(cTile) as ContentPresenter;
                        if (container != null)
                        {
                            Panel.SetZIndex(container, 9999);
                        }
                    }

                    DropSlotIndicator.Width = Math.Max(56, _clusterRelBounds.MaxRelX - _clusterRelBounds.MinRelX);
                    DropSlotIndicator.Height = Math.Max(56, _clusterRelBounds.MaxRelY - _clusterRelBounds.MinRelY);
                    DropSlotIndicator.Visibility = Visibility.Visible;
                }
            }
        }

        if (!_isDragging && !_isRubberBanding && TilesListBox != null)
        {
            UpdateAmbientReveal(canvasMouse);
        }

        if (_isDragging)
        {
            if (e.LeftButton != MouseButtonState.Pressed && Mouse.LeftButton != MouseButtonState.Pressed)
            {
                CancelActiveDrag();
                return;
            }

            if (_draggedTile != null && e.GetPosition(this).X <= 54)
            {
                ShowSidebarRail();
            }

            if (_draggedTile != null || (_isGroupDrag && _draggedGroupModel != null))
            {
                UpdateAutoScrollVelocity();
                ProcessDragMovement(canvasMouse);
            }
            else
            {
                StopAutoScroll();
            }
        }
        else
        {
            StopAutoScroll();
        }
    }

    private void ProcessDragMovement(Point canvasMouse)
    {
        if (!_isDragging || (_draggedTile == null && (!_isGroupDrag || _draggedGroupModel == null)))
        {
            return;
        }

        UpdateLayoutMetrics();
        int maxCols = GridPlacementService.MaxCols;

        double rawAnchorX = canvasMouse.X - _dragOffsetX;
        double rawAnchorY = canvasMouse.Y - _dragOffsetY;

        double minAllowedAnchorX = GridPlacementService.OriginX - _clusterRelBounds.MinRelX;
        double maxAllowedAnchorX = GridPlacementService.PixelXFromCol(maxCols) - _clusterRelBounds.MaxRelX;
        double minAllowedAnchorY = GridPlacementService.OriginY - _clusterRelBounds.MinRelY;

        if (_isGroupDrag && _draggedGroupModel != null)
        {
            double minGroupAnchorY = GridPlacementService.OriginY + 8 - _draggedGroupOffsetY;
            minAllowedAnchorY = Math.Max(minAllowedAnchorY, minGroupAnchorY);
        }

        if (maxAllowedAnchorX < minAllowedAnchorX) maxAllowedAnchorX = minAllowedAnchorX;

        double maxAllowedAnchorY = Math.Max(3000, (MainCanvasGrid?.MinHeight ?? 3000) + 1000);
        double clampedAnchorX = Math.Max(minAllowedAnchorX, Math.Min(rawAnchorX, maxAllowedAnchorX));
        double clampedAnchorY = Math.Max(minAllowedAnchorY, Math.Min(rawAnchorY, maxAllowedAnchorY));

        if (MainCanvasGrid != null && clampedAnchorY + 400 > MainCanvasGrid.MinHeight)
        {
            MainCanvasGrid.MinHeight = clampedAnchorY + 600;
        }

        if (_draggedTile != null)
        {
            foreach (var cTile in _draggedCluster)
            {
                double relX = _dragClusterOriginals.TryGetValue(cTile, out var orig)
                    ? orig.X - _dragClusterOriginals[_draggedTile].X
                    : 0;
                double relY = orig.Y - _dragClusterOriginals[_draggedTile].Y;

                cTile.X = clampedAnchorX + relX;
                cTile.Y = clampedAnchorY + relY;

                var container = TilesListBox?.ItemContainerGenerator.ContainerFromItem(cTile) as ContentPresenter;
                if (container != null)
                {
                    Canvas.SetLeft(container, cTile.X);
                    Canvas.SetTop(container, cTile.Y);
                }
            }
        }

        int minAllowedCol = Math.Max(0, -_clusterRelGridBounds.MinRelCol);
        int maxAllowedCol = Math.Max(minAllowedCol, maxCols - _clusterRelGridBounds.MaxRelCol);
        int rawCol = Math.Max(0, Math.Clamp(GridPlacementService.ColFromPixel(clampedAnchorX), minAllowedCol, maxAllowedCol));
        int clusterSpanX = Math.Max(1, _clusterRelGridBounds.MaxRelCol - _clusterRelGridBounds.MinRelCol);
        int anchorCol = GridPlacementService.SnapColToValidTrackSlot(rawCol, clusterSpanX, maxCols, clampedAnchorX);

        int minAllowedRow = _isGroupDrag
            ? (_draggedTile != null
                ? Math.Max(1, Math.Max(-_clusterRelGridBounds.MinRelRow, 1 - _clusterRelGridBounds.MinRelRow))
                : 0)
            : Math.Max(1, 1 - _clusterRelGridBounds.MinRelRow);
        int anchorRow = Math.Max(minAllowedRow, GridPlacementService.RowFromPixel(clampedAnchorY));

        int rawAnchorCol = anchorCol;
        int rawAnchorRow = anchorRow;

        if (!_isGroupDrag && Groups.Count > 0)
        {
            UpdateGroupDropHighlight(canvasMouse, clampedAnchorX, clampedAnchorY);
        }
        else
        {
            HideGroupDropHighlight();
        }

        if (_isGroupDrag && _draggedGroupModel != null)
        {
            _draggedGroupModel.X = clampedAnchorX + _draggedGroupOffsetX;
            _draggedGroupModel.Y = Math.Max(GridPlacementService.OriginY + 8, clampedAnchorY + _draggedGroupOffsetY);

            var gContainer = GroupsListBox?.ItemContainerGenerator.ContainerFromItem(_draggedGroupModel) as ContentPresenter;
            if (gContainer != null)
            {
                Canvas.SetLeft(gContainer, _draggedGroupModel.X);
                Canvas.SetTop(gContainer, _draggedGroupModel.Y);
            }

            _draggedGroupModel.PlateX = clampedAnchorX + _draggedPlateOffsetX;
            _draggedGroupModel.PlateY = clampedAnchorY + _draggedPlateOffsetY;

            var plateContainer = GroupTintBackplates?.ItemContainerGenerator.ContainerFromItem(_draggedGroupModel) as ContentPresenter;
            if (plateContainer != null)
            {
                Canvas.SetLeft(plateContainer, _draggedGroupModel.PlateX);
                Canvas.SetTop(plateContainer, _draggedGroupModel.PlateY);
            }

            DropSlotIndicator.Visibility = Visibility.Collapsed;

            var draggedMembers = Tiles.Where(t => t.Group == _draggedGroupModel.Id).ToList();
            var gBox = GridPlacementService.GetGroupBoundingBox(_draggedGroupModel, Tiles);
            int groupSpanX = draggedMembers.Count > 0 ? Math.Max(2, gBox.MaxCol - gBox.MinCol) : 4;
            int minMemberColOffset = draggedMembers.Count > 0 ? (draggedMembers.Min(t => t.Col) - _draggedGroupModel.Col) : 0;
            int maxMemberColOffset = draggedMembers.Count > 0 ? (draggedMembers.Max(t => t.Col + t.SpanX) - _draggedGroupModel.Col) : groupSpanX;
            int minLeftDelta = Math.Min(0, minMemberColOffset);
            int maxRightDelta = Math.Max(groupSpanX, maxMemberColOffset);
            int totalGroupCols = Math.Max(2, maxRightDelta - minLeftDelta);
            double colWidth = (totalGroupCols * GridPlacementService.GridStep) - GridPlacementService.Gap;

            int rawGroupCol = GridPlacementService.ColFromPixel(clampedAnchorX + _draggedGroupOffsetX);
            int minAllowedTargetCol = Math.Max(0, -minLeftDelta);
            int maxAllowedTargetCol = Math.Max(minAllowedTargetCol, maxCols - maxRightDelta);
            int targetCol = Math.Clamp(rawGroupCol, minAllowedTargetCol, maxAllowedTargetCol);

            int rawGroupRow = GridPlacementService.RowFromPixel(clampedAnchorY + _draggedGroupOffsetY);
            int targetRow = Math.Max(0, rawGroupRow);

            double colLeft = GridPlacementService.PixelXFromCol(targetCol + minLeftDelta);
            double insertionY = GridPlacementService.PixelYFromRow(targetRow) - 2;

            _groupDragTargetCol = targetCol;
            _groupDragTargetColIndex = targetCol;
            _groupDragTargetRow = targetRow;

            if (GroupInsertionLine != null)
            {
                GroupInsertionLine.Width = colWidth;
                Canvas.SetLeft(GroupInsertionLine, colLeft);
                Canvas.SetTop(GroupInsertionLine, Math.Max(GridPlacementService.OriginY + 6, insertionY));
                GroupInsertionLine.Visibility = Visibility.Visible;
            }
        }
        else if (_hoveredTargetGroup != null && _draggedTile != null)
        {
            if (GroupInsertionLine != null) GroupInsertionLine.Visibility = Visibility.Collapsed;
            HideGapDropHighlight();

            if (_hoveredTargetGroup.IsLocked)
            {
                DropSlotIndicator.Visibility = Visibility.Collapsed;
            }
            else
            {
                var allMembers = Tiles.Where(t => t.Group == _hoveredTargetGroup.Id).ToList();
                int gMaxR = allMembers.Count > 0 ? allMembers.Max(t => t.Row + t.SpanY) : _hoveredTargetGroup.Row + 1;
                double memberMaxY = allMembers.Count > 0 ? allMembers.Max(t => t.Y + t.HeightPixels) : _hoveredTargetGroup.Y + 60;
                if (_hoveredTargetGroup.PlateHeight > 0)
                {
                    memberMaxY = Math.Max(memberMaxY, _hoveredTargetGroup.PlateY + _hoveredTargetGroup.PlateHeight);
                }

                int queryRow = rawAnchorRow;
                int mouseRow = GridPlacementService.RowFromPixel(canvasMouse.Y);
                if (canvasMouse.Y >= memberMaxY - 12 || mouseRow >= gMaxR)
                {
                    queryRow = Math.Max(queryRow, gMaxR);
                }

                var (wrapCol, wrapRow) = FindGroupWrapPosition(_hoveredTargetGroup, _draggedTile, rawAnchorCol, queryRow);
                double wrapX = GridPlacementService.PixelXFromCol(wrapCol);
                double wrapY = GridPlacementService.PixelYFromRow(wrapRow);

                DropSlotIndicator.Width = _draggedTile.WidthPixels;
                DropSlotIndicator.Height = _draggedTile.HeightPixels;
                Canvas.SetLeft(DropSlotIndicator, wrapX);
                Canvas.SetTop(DropSlotIndicator, wrapY);
                DropSlotIndicator.Visibility = Visibility.Visible;
            }
        }
        else
        {
            if (GroupInsertionLine != null) GroupInsertionLine.Visibility = Visibility.Collapsed;

            string? originGroupId = _draggedTile?.Group;
            int clusterMinCol = anchorCol + _clusterRelGridBounds.MinRelCol;
            int clusterMaxCol = anchorCol + _clusterRelGridBounds.MaxRelCol;
            int clusterStartRow = anchorRow + _clusterRelGridBounds.MinRelRow;
            int clusterEndRow = anchorRow + _clusterRelGridBounds.MaxRelRow;

            foreach (var g in Groups)
            {
                if (originGroupId != null && g.Id == originGroupId) continue;

                var members = Tiles.Where(t => t.Group == g.Id).ToList();
                if (members.Count == 0 && string.IsNullOrWhiteSpace(g.Title)) continue;

                int gMinC = (members.Count > 0) ? Math.Min(g.Col, members.Min(t => t.Col)) : g.Col;
                int gMaxC = (members.Count > 0) ? members.Max(t => t.Col + t.SpanX) : g.Col + 2;
                int gMinR = g.Row;
                int gMaxR = members.Count > 0 ? members.Max(t => t.Row + t.SpanY) : g.Row + 1;

                if (clusterMinCol < gMaxC && clusterMaxCol > gMinC)
                {
                    if (clusterStartRow < gMaxR && clusterEndRow > gMinR)
                    {
                        anchorRow = gMaxR - _clusterRelGridBounds.MinRelRow;
                    }
                }
            }

            double snappedAnchorX = GridPlacementService.PixelXFromCol(anchorCol);
            double snappedAnchorY = GridPlacementService.PixelYFromRow(anchorRow);

            DropSlotIndicator.Width = Math.Max(56, _clusterRelBounds.MaxRelX - _clusterRelBounds.MinRelX);
            DropSlotIndicator.Height = Math.Max(56, _clusterRelBounds.MaxRelY - _clusterRelBounds.MinRelY);
            Canvas.SetLeft(DropSlotIndicator, snappedAnchorX + _clusterRelBounds.MinRelX);
            Canvas.SetTop(DropSlotIndicator, snappedAnchorY + _clusterRelBounds.MinRelY);
            DropSlotIndicator.Visibility = Visibility.Visible;

            HideGapDropHighlight();
        }
    }

    private void OnCanvasPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isRubberBanding)
        {
            _isRubberBanding = false;
            RootGrid.ReleaseMouseCapture();

            if (RubberBandBox != null)
            {
                RubberBandBox.Visibility = Visibility.Collapsed;
            }

            if (!_rubberBandHasMoved)
            {
                bool isCtrlDown = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                if (!isCtrlDown)
                {
                    ClearTileSelection();
                }
            }

            e.Handled = true;
            return;
        }

        if (_isDragging)
        {
            if (_draggedTile != null && (e.GetPosition(this).X <= 54 || (SidebarRail != null && SidebarRail.IsMouseOver)))
            {
                var tilesToPin = _draggedCluster.Count > 0 ? _draggedCluster.ToList() : new List<TileModel> { _draggedTile };
                if (SidebarPinningService.TryHandleTileDropOnSidebar(SidebarRail, tilesToPin))
                {
                    CancelActiveDrag();
                    e.Handled = true;
                    return;
                }
            }

            StopAutoScroll();
            _isDragging = false;
            _isPotentialDrag = false;
            DropSlotIndicator.Visibility = Visibility.Collapsed;
            if (GroupInsertionLine != null) GroupInsertionLine.Visibility = Visibility.Collapsed;
            RootGrid.ReleaseMouseCapture();

            var targetGroup = _hoveredTargetGroup;
            HideGroupDropHighlight();
            HideGapDropHighlight();

            if (_isGroupDrag && _draggedGroupModel != null)
            {
                _isGroupDrag = false;
                var movedGroup = _draggedGroupModel;
                _draggedGroupModel = null;

                int requestedCol = _groupDragTargetCol;
                int requestedRow = _groupDragTargetRow;
                int groupH = GridPlacementService.CalculateGroupHeightRows(movedGroup, Tiles);
                GridPlacementService.WouldDisplaceLockedGroup(requestedCol, requestedRow, groupH, Groups, Tiles,
                    movedGroup, out var conflictingLocked);

                var modified = GridPlacementService.InsertGroupAndResolveCollisions(
                    movedGroup,
                    requestedCol,
                    requestedRow,
                    Groups,
                    Tiles);

                if (conflictingLocked != null)
                {
                    FlashLockedGroupPerimeter(conflictingLocked);
                }

                AnimateModifiedTiles(modified);
                UpdateGroupHeaderPositions(animate: true);
                UpdateCanvasHeight();
                SaveGroupsAndLayout();

                var gc = GroupsListBox?.ItemContainerGenerator.ContainerFromItem(movedGroup) as ContentPresenter;
                if (gc != null) Panel.SetZIndex(gc, 0);

                foreach (var cTile in _draggedCluster)
                {
                    cTile.IsBeingDragged = false;
                    var container = TilesListBox?.ItemContainerGenerator.ContainerFromItem(cTile) as ContentPresenter;
                    if (container != null) Panel.SetZIndex(container, 0);
                    var control = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, cTile));
                    control?.AnimateRelease();
                }

                if (_draggedContainer != null)
                {
                    Panel.SetZIndex(_draggedContainer, 0);
                    _draggedContainer = null;
                }

                _draggedTile = null;
                _draggedControl = null;
                _draggedCluster.Clear();
                ClearTileSelection();
                UpdateCanvasHeight();

                string postDropSnapshot = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
                if (!string.IsNullOrEmpty(_preDragLayoutSnapshot) && _preDragLayoutSnapshot != postDropSnapshot)
                {
                    _historyService.PushState(_preDragLayoutSnapshot);
                }

                _preDragLayoutSnapshot = null;

                e.Handled = true;
                return;
            }

            _isGroupDrag = false;
            _draggedGroupModel = null;

            if (_draggedTile != null)
            {
                UpdateLayoutMetrics();
                int maxCols = GridPlacementService.MaxCols;

                var originGroups = _dragClusterOriginals.Keys
                    .Where(t => !string.IsNullOrEmpty(t.Group))
                    .Select(t => Groups.FirstOrDefault(g => g.Id == t.Group))
                    .Where(g => g != null)
                    .Distinct()
                    .ToList();

                var originOldBottoms = originGroups.ToDictionary(
                    g => g!,
                    g => GridPlacementService.GetGroupBoundingBox(g!, Tiles).MaxRow);

                if (targetGroup != null)
                {
                    if (targetGroup.IsLocked)
                    {
                        FlashLockedGroupPerimeter(targetGroup);

                        foreach (var cTile in _draggedCluster)
                        {
                            cTile.IsBeingDragged = false;

                            if (_dragClusterOriginals.TryGetValue(cTile, out var orig))
                            {
                                cTile.Col = orig.Col;
                                cTile.Row = orig.Row;
                                cTile.X = orig.X;
                                cTile.Y = orig.Y;
                            }
                            else
                            {
                                cTile.Col = _dragOriginalCol;
                                cTile.Row = _dragOriginalRow;
                                cTile.X = GridPlacementService.PixelXFromCol(_dragOriginalCol);
                                cTile.Y = GridPlacementService.PixelYFromRow(_dragOriginalRow);
                            }

                            var container = TilesListBox?.ItemContainerGenerator.ContainerFromItem(cTile) as ContentPresenter;
                            if (container != null)
                            {
                                Canvas.SetLeft(container, cTile.X);
                                Canvas.SetTop(container, cTile.Y);
                                Panel.SetZIndex(container, 0);
                            }

                            var control = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, cTile));
                            control?.AnimateRelease();
                        }

                        if (_draggedContainer != null)
                        {
                            Panel.SetZIndex(_draggedContainer, 0);
                            _draggedContainer = null;
                        }

                        _draggedTile = null;
                        _draggedControl = null;
                        ClearTileSelection();
                        _preDragLayoutSnapshot = null;
                        e.Handled = true;
                        return;
                    }
                    else
                    {
                        int dropCol = GridPlacementService.ColFromPixel(_draggedTile.X);
                        int dropRow = GridPlacementService.RowFromPixel(_draggedTile.Y);

                        var members = Tiles.Where(t => t.Group == targetGroup.Id).ToList();
                        int gMaxR = members.Count > 0 ? members.Max(t => t.Row + t.SpanY) : targetGroup.Row + 1;
                        double memberMaxY = members.Count > 0 ? members.Max(t => t.Y + t.HeightPixels) : targetGroup.Y + 60;
                        if (targetGroup.PlateHeight > 0)
                        {
                            memberMaxY = Math.Max(memberMaxY, targetGroup.PlateY + targetGroup.PlateHeight);
                        }

                        Point dropMouse = e.GetPosition(MainCanvasGrid);
                        int mouseRow = GridPlacementService.RowFromPixel(dropMouse.Y);
                        if (dropMouse.Y >= memberMaxY - 12 || mouseRow >= gMaxR)
                        {
                            dropRow = Math.Max(dropRow, gMaxR);
                        }

                        var (finalCol, finalRow) = FindGroupWrapPosition(targetGroup, _draggedTile, dropCol, dropRow);
                        dropCol = finalCol;
                        dropRow = finalRow;

                        bool isSameGroup = _draggedTile.Group == targetGroup.Id;

                        foreach (var cTile in _draggedCluster)
                        {
                            cTile.Group = targetGroup.Id;
                            cTile.SectionHeader = targetGroup.Title;
                        }

                        List<TileModel> mod;
                        if (_draggedCluster.Count == 1)
                        {
                            mod = GridPlacementService.PlaceTileInGroup(
                                _draggedTile,
                                dropCol,
                                dropRow,
                                _dragOriginalCol,
                                _dragOriginalRow,
                                targetGroup,
                                Tiles,
                                Groups,
                                isSameGroup);
                        }
                        else
                        {
                            mod = GridPlacementService.PlaceTilesInGroup(
                                _draggedCluster,
                                targetGroup,
                                Tiles,
                                _draggedTile,
                                dropCol,
                                dropRow,
                                Groups);
                        }

                        AnimateModifiedTiles(mod);
                        UpdateGroupHeaderPositions(animate: true);
                        UpdateCanvasHeight();
                    }
                }
                else
                {
                    foreach (var cTile in _draggedCluster)
                    {
                        cTile.Group = null;
                        cTile.SectionHeader = null;
                    }

                    int dropCol = GridPlacementService.ColFromPixel(_draggedTile.X);
                    int dropRow = GridPlacementService.RowFromPixel(_draggedTile.Y);

                    int clusterSpanX = _draggedCluster.Count > 1
                        ? Math.Max(1, _clusterRelGridBounds.MaxRelCol - _clusterRelGridBounds.MinRelCol)
                        : _draggedTile.SpanX;
                    int targetCol = GridPlacementService.SnapColToValidTrackSlot(dropCol, clusterSpanX, maxCols, _draggedTile.X);
                    int targetRow = Math.Max(1, dropRow);

                    var origDict = _dragClusterOriginals.ToDictionary(kvp => kvp.Key, kvp => (kvp.Value.Col, kvp.Value.Row));

                    foreach (var g in Groups)
                    {
                        var gMembers = Tiles.Where(t => t.Group == g.Id && !_draggedCluster.Contains(t)).ToList();
                        if (gMembers.Count == 0 && string.IsNullOrWhiteSpace(g.Title)) continue;

                        int gMinC = (gMembers.Count > 0) ? Math.Min(g.Col, gMembers.Min(t => t.Col)) : g.Col;
                        int gMaxC = (gMembers.Count > 0) ? gMembers.Max(t => t.Col + t.SpanX) : g.Col + 2;
                        int gMinR = g.Row;
                        int gMaxR = membersCount(gMembers, g);

                        bool isInsideGroup = (targetCol < gMaxC && (targetCol + _draggedTile.SpanX) > gMinC &&
                                              targetRow < gMaxR && (targetRow + _draggedTile.SpanY) > gMinR);
                        if (isInsideGroup)
                        {
                            targetRow = gMaxR;
                        }
                    }



                    List<TileModel> modifiedTiles;
                    if (_draggedCluster.Count > 1)
                    {
                        modifiedTiles = GridPlacementService.PlaceClusterAndResolveCollisions(
                            _draggedCluster, _draggedTile, targetCol, targetRow, _dragOriginalCol, _dragOriginalRow,
                            maxCols, Tiles, origDict, isGroupCluster: false, groups: Groups);
                    }
                    else
                    {
                        modifiedTiles = GridPlacementService.PlaceAndResolveCollisions(
                            _draggedTile, targetCol, targetRow, _dragOriginalCol, _dragOriginalRow, maxCols, Tiles, groups: Groups);
                    }

                    if (Groups != null && Groups.Count > 0)
                    {
                        var looseTiles = Tiles.Where(t => string.IsNullOrEmpty(t.Group)).ToList();
                        var pushedGroupTiles = GridPlacementService.PushGroupsDownFromLooseTiles(looseTiles, Groups, Tiles);
                        foreach (var pt in pushedGroupTiles)
                        {
                            if (!modifiedTiles.Contains(pt)) modifiedTiles.Add(pt);
                        }
                    }

                    AnimateModifiedTiles(modifiedTiles);
                    UpdateGroupHeaderPositions(animate: true);
                }

                foreach (var cTile in _draggedCluster)
                {
                    cTile.IsBeingDragged = false;
                }

                bool anyPulled = false;
                foreach (var og in originGroups)
                {
                    int oldBottom = originOldBottoms[og!];
                    int newBottom = GridPlacementService.GetGroupBoundingBox(og!, Tiles).MaxRow;
                    int shrink = oldBottom - newBottom;
                    if (shrink > 0)
                    {
                        var pulled = GridPlacementService.PullLowerGroupsUp(og!, Groups, Tiles, shrink, _draggedCluster);
                        AnimateModifiedTiles(pulled);
                        anyPulled = true;
                    }
                }

                if (anyPulled)
                {
                    UpdateGroupHeaderPositions(animate: true);
                    SaveGroupsAndLayout();
                }

                UpdateCanvasHeight();
            }

            foreach (var cTile in _draggedCluster)
            {
                var container = TilesListBox?.ItemContainerGenerator.ContainerFromItem(cTile) as ContentPresenter;
                if (container != null) Panel.SetZIndex(container, 0);
                var control = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, cTile));
                control?.AnimateRelease();
            }

            if (_draggedContainer != null)
            {
                Panel.SetZIndex(_draggedContainer, 0);
                _draggedContainer = null;
            }

            _draggedTile = null;
            _draggedControl = null;
            _draggedCluster.Clear();

            UpdateExposedAddSlots();
            if (Groups != null) foreach (var g in Groups) g.IsBeingDragged = false;
            UpdateGroupHeaderPositions();
            ClearTileSelection();
            SaveGroupsAndLayout();

            string postDropSnapshot2 = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
            if (!string.IsNullOrEmpty(_preDragLayoutSnapshot) && _preDragLayoutSnapshot != postDropSnapshot2)
            {
                _historyService.PushState(_preDragLayoutSnapshot);
            }

            _preDragLayoutSnapshot = null;

            e.Handled = true;
            return;
        }

        if (_isPotentialDrag)
        {
            _isPotentialDrag = false;
            var controlToLaunch = _draggedControl;
            _draggedTile = null;
            _draggedControl = null;
            _draggedContainer = null;
            _draggedCluster.Clear();

            bool isCtrlDown = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

            if (!isCtrlDown && controlToLaunch != null)
            {
                bool isWidget = controlToLaunch.DataContext is TileModel t && t.TileType == TileType.Widget;
                var now = DateTime.UtcNow;
                if (!isWidget && (_isLaunchingTile || (now - _lastTileLaunchTime).TotalMilliseconds < 800))
                {
                    controlToLaunch.AnimateRelease();
                    return;
                }

                if (!isWidget)
                {
                    _isLaunchingTile = true;
                    _lastTileLaunchTime = now;
                }

                ClearTileSelection();
                controlToLaunch.AnimateRelease(() =>
                {
                    try
                    {
                        controlToLaunch.LaunchTile();
                        if (!isWidget && Settings.CloseOnLaunch)
                        {
                            HideScreen(restorePreviousFocus: false);
                        }
                    }
                    finally
                    {
                        if (!isWidget)
                        {
                            Dispatcher.InvokeAsync(async () =>
                            {
                                await Task.Delay(500);
                                _isLaunchingTile = false;
                            });
                        }
                    }
                });
            }
            else
            {
                controlToLaunch?.AnimateRelease();
            }

            return;
        }
    }

    private static int membersCount(List<TileModel> members, TileGroupModel g) =>
        members.Count > 0 ? members.Max(t => t.Row + t.SpanY) : g.Row + 1;

    public void CancelActiveDrag()
    {
        if (!_isDragging && !_isPotentialDrag && !_isRubberBanding) return;

        if (_isRubberBanding)
        {
            _isRubberBanding = false;
            if (RubberBandBox != null) RubberBandBox.Visibility = Visibility.Collapsed;
        }

        if (DropSlotIndicator != null) DropSlotIndicator.Visibility = Visibility.Collapsed;
        if (GroupInsertionLine != null) GroupInsertionLine.Visibility = Visibility.Collapsed;
        HideGroupDropHighlight();
        HideGapDropHighlight();
        _hoveredTargetGroup = null;
        _groupDragTargetColIndex = -1;

        if (_preDragLayoutSnapshot != null)
        {
            var snap = LayoutHistoryService.ParseSnapshot(_preDragLayoutSnapshot);
            if (snap != null)
            {
                var dict = snap.Tiles.ToDictionary(t => t.Id);
                foreach (var t in Tiles)
                {
                    if (dict.TryGetValue(t.Id, out var orig))
                    {
                        t.Col = orig.Col;
                        t.Row = orig.Row;
                        t.X = orig.X;
                        t.Y = orig.Y;
                        t.Group = orig.Group;
                        t.SectionHeader = orig.SectionHeader;
                        var c = TilesListBox?.ItemContainerGenerator.ContainerFromItem(t) as ContentPresenter;
                        if (c != null)
                        {
                            Canvas.SetLeft(c, t.X);
                            Canvas.SetTop(c, t.Y);
                            Panel.SetZIndex(c, 0);
                        }
                    }
                }

                if (snap.Groups != null && snap.Groups.Count > 0)
                {
                    var gDict = snap.Groups.ToDictionary(g => g.Id);
                    foreach (var g in Groups)
                    {
                        if (gDict.TryGetValue(g.Id, out var origG))
                        {
                            g.Col = origG.Col;
                            g.Row = origG.Row;
                            g.X = origG.X;
                            g.Y = origG.Y;
                            var gc = GroupsListBox?.ItemContainerGenerator.ContainerFromItem(g) as ContentPresenter;
                            if (gc != null)
                            {
                                Canvas.SetLeft(gc, g.X);
                                Canvas.SetTop(gc, g.Y);
                            }
                        }
                    }
                }
            }

            _preDragLayoutSnapshot = null;
        }
        else if (_dragClusterOriginals.Count > 0)
        {
            foreach (var kvp in _dragClusterOriginals)
            {
                var t = kvp.Key;
                var orig = kvp.Value;
                t.X = orig.X;
                t.Y = orig.Y;
                t.Col = orig.Col;
                t.Row = orig.Row;
                var c = TilesListBox?.ItemContainerGenerator.ContainerFromItem(t) as ContentPresenter;
                if (c != null)
                {
                    Canvas.SetLeft(c, t.X);
                    Canvas.SetTop(c, t.Y);
                    Panel.SetZIndex(c, 0);
                }
            }
        }

        foreach (var cTile in _draggedCluster)
        {
            cTile.IsBeingDragged = false;
            var control = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, cTile));
            control?.AnimateRelease();
            var c = TilesListBox?.ItemContainerGenerator.ContainerFromItem(cTile) as ContentPresenter;
            if (c != null) Panel.SetZIndex(c, 0);
        }

        if (_draggedTile != null)
        {
            _draggedTile.IsBeingDragged = false;
            _draggedControl?.AnimateRelease();
            if (_draggedContainer != null)
            {
                Panel.SetZIndex(_draggedContainer, 0);
                _draggedContainer = null;
            }

            _draggedTile = null;
            _draggedControl = null;
        }

        foreach (var t in Tiles)
        {
            if (t.IsBeingDragged)
            {
                t.IsBeingDragged = false;
                var control = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, t));
                control?.AnimateRelease();
                var c = TilesListBox?.ItemContainerGenerator.ContainerFromItem(t) as ContentPresenter;
                if (c != null) Panel.SetZIndex(c, 0);
            }
        }

        foreach (var g in Groups)
        {
            g.IsBeingDragged = false;
            var gc = GroupsListBox?.ItemContainerGenerator.ContainerFromItem(g) as ContentPresenter;
            if (gc != null) Panel.SetZIndex(gc, 0);
        }

        StopAutoScroll();
        _draggedCluster.Clear();
        _dragClusterOriginals.Clear();
        _isDragging = false;
        _isPotentialDrag = false;
        _isGroupDrag = false;
        _draggedGroupModel = null;

        UpdateCanvasHeight();
        UpdateGroupHeaderPositions();
        ClearAllAmbientReveals();

        try
        {
            if (RootGrid.IsMouseCaptured)
            {
                RootGrid.ReleaseMouseCapture();
            }
        }
        catch
        {
        }
    }

    private void OnCanvasMouseLeave(object sender, MouseEventArgs e)
    {
        if (_isRubberBanding)
        {
            _isRubberBanding = false;
            if (RubberBandBox != null) RubberBandBox.Visibility = Visibility.Collapsed;
            RootGrid.ReleaseMouseCapture();
        }

        if (_isPotentialDrag && !_isDragging)
        {
            foreach (var cTile in _draggedCluster)
            {
                var control = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, cTile));
                control?.AnimateRelease();
            }

            _draggedControl?.AnimateRelease();
            _draggedControl = null;
            _isPotentialDrag = false;
        }

        if (!_isDragging)
        {
            _isGroupDrag = false;
            _draggedGroupModel = null;
        }
        HideGroupDropHighlight();
        ClearAllAmbientReveals();
    }

    private Point _lastAmbientRevealPoint = new(-9999, -9999);

    private void UpdateAmbientReveal(Point mouseOnCanvas)
    {
        if (Presentation.Controls.TileControl.ActiveTiles.Count == 0) return;

        // Skip calculations if mouse moved less than 3 pixels to prevent flooding UI thread at high polling rates
        double dxMove = mouseOnCanvas.X - _lastAmbientRevealPoint.X;
        double dyMove = mouseOnCanvas.Y - _lastAmbientRevealPoint.Y;
        if ((dxMove * dxMove + dyMove * dyMove) < 9.0) return;
        _lastAmbientRevealPoint = mouseOnCanvas;

        foreach (var control in Presentation.Controls.TileControl.ActiveTiles)
        {
            if (control.DataContext is TileModel tile)
            {
                double right = tile.X + tile.WidthPixels;
                double bottom = tile.Y + tile.HeightPixels;

                double dx = Math.Max(0, Math.Max(tile.X - mouseOnCanvas.X, mouseOnCanvas.X - right));
                double dy = Math.Max(0, Math.Max(tile.Y - mouseOnCanvas.Y, mouseOnCanvas.Y - bottom));
                double distance = Math.Sqrt(dx * dx + dy * dy);

                if (distance <= 160)
                {
                    control.UpdateAmbientReveal(new Point(mouseOnCanvas.X - tile.X, mouseOnCanvas.Y - tile.Y), distance);
                }
                else
                {
                    control.ClearAmbientReveal();
                }
            }
        }
    }

    private void ClearAllAmbientReveals()
    {
        _lastAmbientRevealPoint = new(-9999, -9999);
        foreach (var control in Presentation.Controls.TileControl.ActiveTiles)
        {
            control.ClearAmbientReveal();
        }
    }

    private static bool IsInteractiveElement(DependencyObject? dep)
    {
        // Opt-in escape hatch: a widget whose interactive children would otherwise make the
        // whole tile undraggable can mark itself Tag="AllowTileDrag" (see PowerWidgetView).
        // The hub then still starts a potential drag on press, and the click/drag conflict
        // resolves itself: ButtonBase only raises Click while it still holds mouse capture,
        // and promoting the press to a drag steals that capture — so a clean click still
        // activates the control while a press-and-move drags the tile instead.
        if (HasAncestorTag(dep, "AllowTileDrag"))
        {
            return false;
        }

        DependencyObject? current = dep;
        while (current != null)
        {
            if (current is System.Windows.Controls.Primitives.TextBoxBase ||
                current is System.Windows.Controls.PasswordBox ||
                current is System.Windows.Controls.Primitives.ButtonBase ||
                current is System.Windows.Controls.Primitives.Thumb ||
                current is System.Windows.Controls.Primitives.RangeBase ||
                current is System.Windows.Controls.Slider ||
                current is System.Windows.Controls.ProgressBar ||
                current is System.Windows.Controls.ListBoxItem ||
                current is System.Windows.Controls.Primitives.Selector ||
                current is ContextMenu ||
                current is Presentation.Controls.FluentVolumeSlider ||
                current is Widgets.WidgetSegmentedControl ||
                current is Widgets.WidgetSegmentedItem ||
                current is Widgets.WidgetTiles ||
                current is Widgets.WidgetTile)
            {
                return true;
            }

            if (current is FrameworkElement fe)
            {
                if (fe.Tag as string == "InteractiveControl")
                {
                    return true;
                }
            }

            current = GetVisualOrLogicalParent(current);
        }

        return false;
    }

    /// <summary>
    /// True when the element or any ancestor carries the supplied Tag string.
    /// </summary>
    private static bool HasAncestorTag(DependencyObject? dep, string tag)
    {
        DependencyObject? current = dep;
        while (current != null)
        {
            if (current is FrameworkElement fe && fe.Tag as string == tag)
            {
                return true;
            }

            current = GetVisualOrLogicalParent(current);
        }

        return false;
    }

    private void UpdateGroupDropHighlight(Point mousePos, double anchorX, double anchorY)
    {
        TileGroupModel? targetGroup = null;
        Rect bestGroupRect = Rect.Empty;

        string? originGroupId = _draggedTile?.Group;
        bool isClusterFromOriginGroup = originGroupId != null &&
                                        (_draggedCluster.Count == 0 ||
                                         _draggedCluster.All(t => t.Group == originGroupId));

        var sortedGroups = isClusterFromOriginGroup
            ? Groups.OrderByDescending(g => g.Id == originGroupId).ToList()
            : Groups.ToList();

        int mouseCol = GridPlacementService.ColFromPixel(mousePos.X);
        int mouseRow = GridPlacementService.RowFromPixel(mousePos.Y);

        foreach (var group in sortedGroups)
        {
            var allMembers = Tiles.Where(t => t.Group == group.Id).ToList();
            if (allMembers.Count == 0 && string.IsNullOrWhiteSpace(group.Title)) continue;

            // Bounded to actual member tiles so adjacent canvas is free for loose tiles
            int gMinC = allMembers.Count > 0 ? Math.Min(group.Col, allMembers.Min(t => t.Col)) : group.Col;
            int gMaxC = allMembers.Count > 0 ? allMembers.Max(t => t.Col + t.SpanX) : group.Col + 2;
            int groupSpan = Math.Max(1, gMaxC - gMinC);
            double blockWidth = (groupSpan * GridPlacementService.GridStep) - GridPlacementService.Gap;
            double minX = GridPlacementService.PixelXFromCol(gMinC);

            int gMinR = group.Row;
            int gMaxR = allMembers.Count > 0 ? allMembers.Max(t => t.Row + t.SpanY) : group.Row + 1;

            // 1. Strict column and gap guard:
            // Mouse MUST be within the group's boundary [gMinC, gMaxC - 1].
            // Any mouse position outside this column range is open canvas.
            if (mouseCol < gMinC || mouseCol >= gMaxC) continue;

            // 2. Vertical row and gap guard:
            // For locked groups, strictly bound to member tiles [gMinR, gMaxR - 1] (or group.Row + 2 if empty).
            // For unlocked groups, allow hover detection down into the append zone (gMaxR + tileSpanY) so the tint
            // can smoothly expand vertically to accommodate new rows, while preserving exact horizontal and top sensitivity.
            int tileSpanY = _draggedTile != null
                ? _draggedTile.SpanY
                : Math.Max(1, _clusterRelGridBounds.MaxRelRow - _clusterRelGridBounds.MinRelRow + 1);
            double tileHeight = _draggedTile != null
                ? _draggedTile.HeightPixels
                : Math.Max(60, _clusterRelBounds.MaxRelY - _clusterRelBounds.MinRelY);

            int effectiveMaxRow = group.IsLocked
                ? (allMembers.Count > 0 ? gMaxR : group.Row + 2)
                : (allMembers.Count > 0 ? (gMaxR + tileSpanY + 1) : (group.Row + 1 + tileSpanY + 1));
            if (mouseRow < gMinR || mouseRow >= effectiveMaxRow) continue;

            // 3. Pixel-exact boundary check:
            double minY = group.PlateHeight > 0 ? Math.Min(group.Y, group.PlateY) : group.Y;

            double memberMaxY = allMembers.Count > 0 ? allMembers.Max(t => t.Y + t.HeightPixels) : group.Y + 60;
            if (group.PlateHeight > 0)
            {
                memberMaxY = Math.Max(memberMaxY, group.PlateY + group.PlateHeight);
            }

            double detectMaxY = group.IsLocked
                ? memberMaxY
                : (allMembers.Count > 0 ? (memberMaxY + tileHeight) : (group.Y + 60 + tileHeight));

            // Bounded to group interior and append area - zero outward pad into adjacent column tracks
            Rect groupDetectRect = new Rect(minX, minY, blockWidth, Math.Max(60, detectMaxY - minY));

            if (groupDetectRect.Contains(mousePos))
            {
                targetGroup = group;

                if (allMembers.Count > 0)
                {
                    int minCol = allMembers.Min(t => t.Col);
                    int maxCol = allMembers.Max(t => t.Col + t.SpanX);
                    int minRow = allMembers.Min(t => t.Row);
                    int maxRow = allMembers.Max(t => t.Row + t.SpanY);

                    if (_draggedTile != null && !group.IsLocked)
                    {
                        int rawCol = GridPlacementService.ColFromPixel(anchorX);
                        int rawRow = GridPlacementService.RowFromPixel(anchorY);
                        int queryRow = rawRow;
                        if (mousePos.Y >= memberMaxY - 12 || mouseRow >= gMaxR)
                        {
                            queryRow = Math.Max(queryRow, gMaxR);
                        }
                        var (wrapCol, wrapRow) = FindGroupWrapPosition(group, _draggedTile, rawCol, queryRow);
                        minCol = Math.Min(minCol, wrapCol);
                        maxCol = Math.Max(maxCol, wrapCol + _draggedTile.SpanX);
                        minRow = Math.Min(minRow, wrapRow);
                        maxRow = Math.Max(maxRow, wrapRow + _draggedTile.SpanY);
                    }

                    int colSpan = Math.Max(1, maxCol - minCol);
                    int rowSpan = Math.Max(1, maxRow - minRow);

                    double rectX = GridPlacementService.PixelXFromCol(minCol) - 8;
                    double rectY = GridPlacementService.PixelYFromRow(minRow) - 8;
                    double rectW = (colSpan * GridPlacementService.GridStep) - GridPlacementService.Gap + 16;
                    double rectH = (rowSpan * GridPlacementService.GridStep) - GridPlacementService.Gap + 16;

                    bestGroupRect = new Rect(rectX, rectY, rectW, rectH);
                }
                else
                {
                    if (group.IsLocked)
                    {
                        int col = group.Col >= 0
                            ? group.Col
                            : GridPlacementService.GetColumnStartCol(group.ColumnIndex);
                        int row = GridPlacementService.RowFromPixel(group.Y) + 1;
                        double rectX = GridPlacementService.PixelXFromCol(col) - 8;
                        double rectY = GridPlacementService.PixelYFromRow(row) - 8;
                        double rectW = (GridPlacementService.GroupColWidth * GridPlacementService.GridStep) -
                            GridPlacementService.Gap + 16;
                        double rectH = 56;
                        bestGroupRect = new Rect(rectX, rectY, rectW, rectH);
                    }
                    else
                    {
                        int rawCol = GridPlacementService.ColFromPixel(anchorX);
                        int rawRow = GridPlacementService.RowFromPixel(anchorY);
                        int wrapCol = group.Col >= 0
                            ? group.Col
                            : GridPlacementService.GetColumnStartCol(group.ColumnIndex);
                        int wrapRow = GridPlacementService.RowFromPixel(group.Y) + 1;
                        if (_draggedTile != null)
                        {
                            var (wc, wr) = FindGroupWrapPosition(group, _draggedTile, rawCol, rawRow);
                            wrapCol = wc;
                            wrapRow = wr;
                        }

                        int spanX = _draggedTile?.SpanX ?? 2;
                        int spanY = _draggedTile?.SpanY ?? 2;
                        double rectX = GridPlacementService.PixelXFromCol(wrapCol) - 8;
                        double rectY = GridPlacementService.PixelYFromRow(wrapRow) - 8;
                        double rectW = (spanX * GridPlacementService.GridStep) - GridPlacementService.Gap + 16;
                        double rectH = (spanY * GridPlacementService.GridStep) - GridPlacementService.Gap + 16;
                        bestGroupRect = new Rect(rectX, rectY, rectW, rectH);
                    }
                }

                break;
            }
        }

        if (targetGroup != null)
        {
            _hoveredTargetGroup = targetGroup;
            bool isLocked = targetGroup.IsLocked;

            Color groupColor = isLocked
                ? Color.FromRgb(0xFF, 0x43, 0x43)
                : (Color)ColorConverter.ConvertFromString("#60CDFF");

            if (!isLocked)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(targetGroup.HeaderColor))
                    {
                        groupColor = (Color)ColorConverter.ConvertFromString(targetGroup.HeaderColor);
                    }
                }
                catch
                {
                }
            }

            var solidBrush = new SolidColorBrush(groupColor);
            solidBrush.Freeze();
            var tintBrush = new SolidColorBrush(Color.FromArgb(isLocked ? (byte)36 : (byte)22, groupColor.R,
                groupColor.G, groupColor.B));
            tintBrush.Freeze();

            if (GroupDropPerimeterBorder != null)
            {
                GroupDropPerimeterBorder.BeginAnimation(UIElement.OpacityProperty, null);
                if (GroupDropPerimeterTranslate != null)
                {
                    GroupDropPerimeterTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                    GroupDropPerimeterTranslate.X = 0;
                }

                GroupDropPerimeterBorder.CornerRadius = new CornerRadius(6);
                Canvas.SetLeft(GroupDropPerimeterBorder, bestGroupRect.Left);
                Canvas.SetTop(GroupDropPerimeterBorder, bestGroupRect.Top);
                GroupDropPerimeterBorder.Width = bestGroupRect.Width;
                GroupDropPerimeterBorder.Height = bestGroupRect.Height;
                GroupDropPerimeterBorder.BorderBrush = solidBrush;
                GroupDropPerimeterBorder.Background = tintBrush;
                if (GroupDropGlowEffect != null)
                {
                    GroupDropGlowEffect.Color = groupColor;
                    GroupDropGlowEffect.Opacity = isLocked ? 0.85 : 0.65;
                }

                GroupDropPerimeterBorder.Opacity = 1.0;
                GroupDropPerimeterBorder.Visibility = Visibility.Visible;
            }

            if (GroupDropFloatingBadge != null)
            {
                if (GroupDropBadgeIcon != null)
                {
                    GroupDropBadgeIcon.Foreground = solidBrush;
                    GroupDropBadgeIcon.Symbol = isLocked
                        ? Wpf.Ui.Controls.SymbolRegular.LockClosed24
                        : (_draggedCluster.Count > 0 && _draggedCluster.All(t => t.Group == targetGroup.Id)
                            ? Wpf.Ui.Controls.SymbolRegular.ReOrder24
                            : Wpf.Ui.Controls.SymbolRegular.Add24);
                }

                if (GroupDropBadgeText != null)
                {
                    GroupDropBadgeText.Text = isLocked
                        ? "🔒 Locked (Drop Denied)"
                        : (_draggedCluster.Count > 0 && _draggedCluster.All(t => t.Group == targetGroup.Id)
                            ? $"Reorder in {targetGroup.Title}"
                            : $"Add to {targetGroup.Title}");
                }

                Canvas.SetLeft(GroupDropFloatingBadge, mousePos.X + 16);
                Canvas.SetTop(GroupDropFloatingBadge, Math.Max(10, mousePos.Y - 38));
                GroupDropFloatingBadge.Visibility = Visibility.Visible;
            }
        }
        else
        {
            HideGroupDropHighlight();
        }
    }

    private void HideGroupDropHighlight()
    {
        _hoveredTargetGroup = null;
        if (GroupDropPerimeterBorder != null)
        {
            GroupDropPerimeterBorder.BeginAnimation(UIElement.OpacityProperty, null);
            if (GroupDropPerimeterTranslate != null)
            {
                GroupDropPerimeterTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                GroupDropPerimeterTranslate.X = 0;
            }

            GroupDropPerimeterBorder.Visibility = Visibility.Collapsed;
        }

        if (GroupDropFloatingBadge != null) GroupDropFloatingBadge.Visibility = Visibility.Collapsed;
    }

    private (int Col, int Row) FindGroupWrapPosition(TileGroupModel group, TileModel tile, int rawCol, int rawRow)
    {
        int groupMinCol = group.Col;
        var members = Tiles.Where(t => t.Group == group.Id && !ReferenceEquals(t, tile)).ToList();
        int maxMemberCol = members.Count > 0 ? members.Max(t => t.Col + t.SpanX) : (group.Col + GroupColWidth);
        int naturalWidth = Math.Max(GroupColWidth, maxMemberCol - group.Col);
        int groupMaxCol = Math.Min(GridPlacementService.MaxCols, group.Col + naturalWidth);

        int minRow = group.Row + 1;
        int baseRow = Math.Max(minRow, rawRow);

        int col;
        int row;

        if (rawCol + tile.SpanX > groupMaxCol)
        {
            col = groupMinCol;
            row = baseRow + (tile.SpanY > 1 ? tile.SpanY : 1);
        }
        else
        {
            col = Math.Clamp(rawCol, groupMinCol, Math.Max(groupMinCol, groupMaxCol - tile.SpanX));
            row = baseRow;
        }

        return (col, row);
    }

    public void FlashLockedGroupPerimeter(TileGroupModel group)
    {
        if (GroupDropPerimeterBorder == null) return;

        var members = Tiles.Where(t => t.Group == group.Id).ToList();
        if (members.Count > 0)
        {
            int minMemberCol = members.Min(t => t.Col);
            int maxMemberCol = members.Max(t => t.Col + t.SpanX);
            int colSpan = Math.Max(1, maxMemberCol - minMemberCol);

            int minMemberRow = members.Min(t => t.Row);
            int maxMemberBottom = members.Max(t => t.Row + t.SpanY);
            int rowSpan = Math.Max(1, maxMemberBottom - minMemberRow);

            Canvas.SetLeft(GroupDropPerimeterBorder, GridPlacementService.PixelXFromCol(minMemberCol) - 8);
            Canvas.SetTop(GroupDropPerimeterBorder, GridPlacementService.PixelYFromRow(minMemberRow) - 8);
            GroupDropPerimeterBorder.Width = (colSpan * GridPlacementService.GridStep) - GridPlacementService.Gap + 16;
            GroupDropPerimeterBorder.Height = (rowSpan * GridPlacementService.GridStep) - GridPlacementService.Gap + 16;
        }
        else
        {
            int col = group.Col >= 0 ? group.Col : GridPlacementService.GetColumnStartCol(group.ColumnIndex);
            Canvas.SetLeft(GroupDropPerimeterBorder, GridPlacementService.PixelXFromCol(col) - 8);
            Canvas.SetTop(GroupDropPerimeterBorder, group.Y);
            GroupDropPerimeterBorder.Width = (GridPlacementService.GroupColWidth * GridPlacementService.GridStep) -
                GridPlacementService.Gap + 16;
            GroupDropPerimeterBorder.Height = 36;
        }

        GroupDropPerimeterBorder.CornerRadius = new CornerRadius(6);

        var redColor = Color.FromRgb(0xFF, 0x43, 0x43);
        var redBorderBrush = new SolidColorBrush(redColor);
        redBorderBrush.Freeze();
        var redBackgroundBrush = new SolidColorBrush(Color.FromArgb(45, redColor.R, redColor.G, redColor.B));
        redBackgroundBrush.Freeze();
        GroupDropPerimeterBorder.BorderBrush = redBorderBrush;
        GroupDropPerimeterBorder.Background = redBackgroundBrush;
        if (GroupDropGlowEffect != null)
        {
            GroupDropGlowEffect.Color = redColor;
            GroupDropGlowEffect.Opacity = 0.95;
        }

        GroupDropPerimeterBorder.Opacity = 1.0;
        GroupDropPerimeterBorder.Visibility = Visibility.Visible;

        var translate = GroupDropPerimeterTranslate ?? new TranslateTransform();
        GroupDropPerimeterBorder.RenderTransform = translate;

        var shakeAnimation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(300)
        };
        shakeAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))));
        shakeAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(-6, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(50))));
        shakeAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(6, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100))));
        shakeAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(-4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
        shakeAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200))));
        shakeAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(-2, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250))));
        shakeAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300))));

        var fadeAnimation = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            BeginTime = TimeSpan.FromMilliseconds(300),
            Duration = TimeSpan.FromMilliseconds(350),
            FillBehavior = FillBehavior.Stop
        };

        fadeAnimation.Completed += (s, e) =>
        {
            GroupDropPerimeterBorder.Visibility = Visibility.Collapsed;
            GroupDropPerimeterBorder.Opacity = 1.0;
            translate.X = 0;
        };

        translate.BeginAnimation(TranslateTransform.XProperty, shakeAnimation);
        GroupDropPerimeterBorder.BeginAnimation(UIElement.OpacityProperty, fadeAnimation);
    }

    private void HideGapDropHighlight()
    {
        if (GapDropWarningBorder != null && GapDropWarningBorder.Visibility == Visibility.Visible)
        {
            GapDropWarningBorder.BeginAnimation(UIElement.OpacityProperty, null);
            GapDropWarningBorder.Visibility = Visibility.Collapsed;
            GapDropWarningBorder.Opacity = 1.0;
        }

        if (_hoveredTargetGroup == null && GroupDropFloatingBadge != null &&
            GroupDropFloatingBadge.Visibility == Visibility.Visible)
        {
            GroupDropFloatingBadge.Visibility = Visibility.Collapsed;
        }
    }

    public void StartGroupDrag(TileGroupModel group, MouseEventArgs e)
    {
        if (group.IsLocked)
        {
            FlashLockedGroupPerimeter(group);
            return;
        }

        ClearTileSelection();
        _preDragLayoutSnapshot = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);

        var members = Tiles.Where(t => t.Group == group.Id).ToList();
        Point canvasMouse = TilesListBox != null 
            ? e.GetPosition(TilesListBox) 
            : (MainCanvasGrid != null ? e.GetPosition(MainCanvasGrid) : e.GetPosition(this));

        var gc = GroupsListBox?.ItemContainerGenerator.ContainerFromItem(group) as ContentPresenter;
        if (gc != null) Panel.SetZIndex(gc, 9999);

        if (members.Count == 0)
        {
            _draggedTile = null;
            _draggedControl = null;
            _draggedContainer = null;
            _dragStartPoint = e.GetPosition(this);

            int gCol = group.Col >= 0 ? group.Col : GridPlacementService.GetColumnStartCol(group.ColumnIndex);
            int gRow = Math.Max(0, group.Row);
            double gX = GridPlacementService.PixelXFromCol(gCol);
            double gY = GridPlacementService.PixelYFromRow(gRow) + 8;

            _dragOriginalCol = gCol;
            _dragOriginalRow = gRow;
            _dragOffsetX = canvasMouse.X - gX;
            _dragOffsetY = canvasMouse.Y - gY;
            _dragBeganWithSelection = false;

            _draggedCluster = members;
            _dragClusterOriginals.Clear();

            _clusterRelBounds = (0, GridPlacementService.GroupColWidth * GridPlacementService.GridStep - GridPlacementService.Gap, 0, GridPlacementService.GridStep);
            _clusterRelGridBounds = (0, GridPlacementService.GroupColWidth, 0, 1);

            _isPotentialDrag = false;
            _isDragging = true;
            _isGroupDrag = true;
            _draggedGroupModel = group;
            _draggedGroupOffsetX = 0;
            _draggedGroupOffsetY = 0;
            _draggedPlateOffsetX = 0;
            _draggedPlateOffsetY = 0;
            group.IsBeingDragged = true;

            UpdateAutoScrollVelocity();
            RootGrid.CaptureMouse();
            DropSlotIndicator.Visibility = Visibility.Collapsed;
            if (GroupInsertionLine != null)
            {
                GroupInsertionLine.Width = (4 * GridPlacementService.GridStep) - GridPlacementService.Gap;
                Canvas.SetLeft(GroupInsertionLine, GridPlacementService.PixelXFromCol(gCol));
                Canvas.SetTop(GroupInsertionLine, group.Y);
                GroupInsertionLine.Visibility = Visibility.Visible;
            }

            return;
        }

        foreach (var m in members)
        {
            m.IsSelected = true;
        }

        var anchor = members.OrderBy(t => t.Row).ThenBy(t => t.Col).First();

        _draggedTile = anchor;
        _draggedControl = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, anchor));
        _draggedContainer = TilesListBox?.ItemContainerGenerator.ContainerFromItem(anchor) as ContentPresenter;
        _dragStartPoint = e.GetPosition(this);

        _dragOriginalCol = GridPlacementService.ColFromPixel(anchor.X);
        _dragOriginalRow = GridPlacementService.RowFromPixel(anchor.Y);
        _dragOffsetX = canvasMouse.X - anchor.X;
        _dragOffsetY = canvasMouse.Y - anchor.Y;
        _dragBeganWithSelection = true;

        _draggedCluster = members;
        _dragClusterOriginals.Clear();

        double minRelX = 0, maxRelX = anchor.WidthPixels, minRelY = 0, maxRelY = anchor.HeightPixels;
        int minRelCol = 0, maxRelCol = anchor.SpanX, minRelRow = 0, maxRelRow = anchor.SpanY;

        foreach (var cTile in _draggedCluster)
        {
            cTile.IsBeingDragged = true;
            var control = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, cTile));
            control?.AnimateElevationLift();

            var container = TilesListBox?.ItemContainerGenerator.ContainerFromItem(cTile) as ContentPresenter;
            if (container != null)
            {
                Panel.SetZIndex(container, 9999);
            }

            int cCol = GridPlacementService.ColFromPixel(cTile.X);
            int cRow = GridPlacementService.RowFromPixel(cTile.Y);
            _dragClusterOriginals[cTile] = (cTile.X, cTile.Y, cCol, cRow);

            double relX = cTile.X - anchor.X;
            double relY = cTile.Y - anchor.Y;
            int relCol = cCol - _dragOriginalCol;
            int relRow = cRow - _dragOriginalRow;

            minRelX = Math.Min(minRelX, relX);
            maxRelX = Math.Max(maxRelX, relX + cTile.WidthPixels);
            minRelY = Math.Min(minRelY, relY);
            maxRelY = Math.Max(maxRelY, relY + cTile.HeightPixels);

            minRelCol = Math.Min(minRelCol, relCol);
            maxRelCol = Math.Max(maxRelCol, relCol + cTile.SpanX);
            minRelRow = Math.Min(minRelRow, relRow);
            maxRelRow = Math.Max(maxRelRow, relRow + cTile.SpanY);
        }

        _clusterRelBounds = (minRelX, maxRelX, minRelY, maxRelY);
        _clusterRelGridBounds = (minRelCol, maxRelCol, minRelRow, maxRelRow);

        _isPotentialDrag = false;
        _isDragging = true;
        _isGroupDrag = true;
        _draggedGroupModel = group;
        _draggedGroupOffsetX = group.X - anchor.X;
        _draggedGroupOffsetY = group.Y - anchor.Y;
        _draggedPlateOffsetX = group.PlateX - anchor.X;
        _draggedPlateOffsetY = group.PlateY - anchor.Y;
        group.IsBeingDragged = true;

        UpdateAutoScrollVelocity();
        RootGrid.CaptureMouse();
        DropSlotIndicator.Visibility = Visibility.Collapsed;
        if (GroupInsertionLine != null)
        {
            var gBox = GridPlacementService.GetGroupBoundingBox(group, Tiles);
            int gSpan = members.Count > 0 ? Math.Max(2, gBox.MaxCol - gBox.MinCol) : 4;
            GroupInsertionLine.Width = (gSpan * GridPlacementService.GridStep) - GridPlacementService.Gap;
            Canvas.SetLeft(GroupInsertionLine, GridPlacementService.PixelXFromCol(gBox.MinCol));
            Canvas.SetTop(GroupInsertionLine, group.Y);
            GroupInsertionLine.Visibility = Visibility.Visible;
        }
    }


    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetDataPresent(typeof(CatalogItemModel)) ||
            e.Data.GetDataPresent(DataFormats.UnicodeText) ||
            e.Data.GetDataPresent(DataFormats.Text))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnWindowDragLeave(object sender, DragEventArgs e)
    {
        UpdateExposedAddSlots();
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(CatalogItemModel)))
        {
            var item = e.Data.GetData(typeof(CatalogItemModel)) as CatalogItemModel;
            if (item != null)
            {
                Point pos = e.GetPosition(TilesListBox);
                PinCatalogItem(item, pos);
                e.Handled = true;
            }
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string[]? files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0)
            {
                string preDrop = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
                _historyService.PushState(preDrop);

                Point pos = e.GetPosition(TilesListBox);
                double currentX = Math.Max(0, pos.X);
                double currentY = Math.Max(0, pos.Y);
                foreach (string file in files)
                {
                    if (string.Equals(Path.GetExtension(file), ".url", StringComparison.OrdinalIgnoreCase))
                    {
                        string parsedUrl = ParseUrlFile(file);
                        string title = Path.GetFileNameWithoutExtension(file);
                        AddWebLinkTile(title, parsedUrl, null, currentX, currentY, recordHistory: false);
                    }
                    else
                    {
                        AddFileAsTile(file, currentX, currentY, recordHistory: false);
                    }
                    currentX += 70;
                    currentY += 70;
                }

                e.Handled = true;
            }
        }
        else if (e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.Text))
        {
            string? text = (e.Data.GetData(DataFormats.UnicodeText) ?? e.Data.GetData(DataFormats.Text)) as string;
            if (!string.IsNullOrWhiteSpace(text))
            {
                string trimmed = text.Trim();
                if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                {
                    Point pos = e.GetPosition(TilesListBox);
                    AddWebLinkTile(string.Empty, trimmed, null, pos.X, pos.Y, recordHistory: true);
                    e.Handled = true;
                }
            }
        }

        UpdateExposedAddSlots();
    }

    private static string ParseUrlFile(string urlFilePath)
    {
        try
        {
            foreach (var line in File.ReadAllLines(urlFilePath))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed.Substring(4).Trim();
                }
            }
        }
        catch { }
        return urlFilePath;
    }


}
