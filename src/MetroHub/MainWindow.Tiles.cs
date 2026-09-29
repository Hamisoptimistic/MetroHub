using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Core.Services.Catalog;
using MetroHub.Presentation.Controls;
using MenuItem = System.Windows.Controls.MenuItem;
using ContextMenu = System.Windows.Controls.ContextMenu;

namespace MetroHub;

public partial class MainWindow
{
    private Point _canvasRightClickPoint;
    private bool _isAppsLoaded = false;
    private bool _isLoadingApps = false;
    private DateTime _lastAppsRefreshTime = DateTime.UtcNow;

    private readonly LayoutHistoryService _historyService = new();
    public void ClearTileSelection()
    {
        foreach (var t in Tiles)
        {
            t.IsSelected = false;
        }
    }

    public List<TileModel> SelectedTiles => Tiles.Where(t => t.IsSelected).ToList();

    public void ExecuteUndo()
    {
        if (_isDragging || _isRubberBanding || !_historyService.CanUndo) return;

        ClearTileSelection();
        string currentSnapshot = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
        string? targetSnapshot = _historyService.Undo(currentSnapshot);
        if (!string.IsNullOrWhiteSpace(targetSnapshot))
        {
            RestoreLayoutFromSnapshot(targetSnapshot);
        }
    }

    public void ExecuteRedo()
    {
        if (_isDragging || _isRubberBanding || !_historyService.CanRedo) return;

        ClearTileSelection();
        string currentSnapshot = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
        string? targetSnapshot = _historyService.Redo(currentSnapshot);
        if (!string.IsNullOrWhiteSpace(targetSnapshot))
        {
            RestoreLayoutFromSnapshot(targetSnapshot);
        }
    }

    private void RestoreLayoutFromSnapshot(string snapshot)
    {
        var snapshotModel = LayoutHistoryService.ParseSnapshot(snapshot);
        if (snapshotModel == null) return;

        var targetTiles = snapshotModel.Tiles;
        var targetGroups = snapshotModel.Groups;

        var targetDict = targetTiles.ToDictionary(t => t.Id);
        var currentTiles = Tiles.ToList();
        var currentDict = currentTiles.ToDictionary(t => t.Id);

        var toRemove = currentTiles.Where(t => !targetDict.ContainsKey(t.Id)).ToList();
        foreach (var t in toRemove)
        {
            t.Teardown();
            Tiles.Remove(t);
        }

        var toAdd = targetTiles.Where(t => !currentDict.ContainsKey(t.Id)).ToList();
        foreach (var t in toAdd)
        {
            Tiles.Add(t);
        }

        var targetGroupDict = targetGroups.ToDictionary(g => g.Id);
        var currentGroups = Groups.ToList();
        var currentGroupDict = currentGroups.ToDictionary(g => g.Id);

        bool shouldRestoreGroups = targetGroups.Count > 0 || !targetTiles.Any(t => !string.IsNullOrEmpty(t.Group));
        if (shouldRestoreGroups)
        {
            var groupsToRemove = currentGroups.Where(g => !targetGroupDict.ContainsKey(g.Id)).ToList();
            foreach (var g in groupsToRemove)
            {
                Groups.Remove(g);
            }

            var groupsToAdd = targetGroups.Where(g => !currentGroupDict.ContainsKey(g.Id)).ToList();
            foreach (var g in groupsToAdd)
            {
                Groups.Add(g);
            }

            foreach (var tg in targetGroups)
            {
                var existingG = Groups.FirstOrDefault(g => g.Id == tg.Id);
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
            var existing = Tiles.FirstOrDefault(t => t.Id == target.Id);
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
                var container = TilesListBox?.ItemContainerGenerator.ContainerFromItem(existing) as ContentPresenter;
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
            AnimateModifiedTiles(modifiedList);
        }

        UpdateGroupHeaderPositions();
        UpdateLayoutMetrics();
        UpdateCanvasHeight();
        UpdateExposedAddSlots();
        SaveGroupsAndLayout();
    }

    private void OnTileActivated(object sender, RoutedEventArgs e)
    {
        if (Settings.CloseOnLaunch)
        {
            HideScreen(restorePreviousFocus: false);
        }
    }

    public void BatchResizeSelectedTiles(int newSpanX, int newSpanY, TileModel anchorTile)
    {
        List<TileModel> targets = (anchorTile.IsSelected && SelectedTiles.Count > 1)
            ? SelectedTiles.ToList()
            : new List<TileModel> { anchorTile };

        if (targets.All(t => t.SpanX == newSpanX && t.SpanY == newSpanY))
        {
            return;
        }

        string preModify = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);

        var oldSpans = targets.ToDictionary(t => t, t => (t.SpanX, t.SpanY));

        foreach (var t in targets)
        {
            var container = TilesListBox?.ItemContainerGenerator.ContainerFromItem(t) as ContentPresenter;
            if (container != null)
            {
                Panel.SetZIndex(container, 50);
                Dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(260);
                    Panel.SetZIndex(container, 0);
                });
            }
        }

        UpdateLayoutMetrics();
        int maxCols = GridPlacementService.MaxCols;

        var affectedGroups = targets
            .Where(t => !string.IsNullOrEmpty(t.Group))
            .Select(t => Groups.FirstOrDefault(g => g.Id == t.Group))
            .Where(g => g != null)
            .Distinct()
            .ToList();

        var oldGroupBottoms = affectedGroups.ToDictionary(
            g => g!,
            g => GridPlacementService.GetGroupBoundingBox(g!, Tiles).MaxRow);

        var modified = GridPlacementService.ResolveBatchResizeExpansion(
            targets,
            newSpanX,
            newSpanY,
            maxCols,
            Tiles,
            Groups);

        foreach (var t in targets)
        {
            var (oldX, oldY) = oldSpans[t];
            var control = Presentation.Controls.TileControl.ActiveTiles.FirstOrDefault(tc => ReferenceEquals(tc.DataContext, t));
            control?.AnimateResize(oldX, oldY, newSpanX, newSpanY);
        }

        foreach (var ag in affectedGroups)
        {
            int oldBottom = oldGroupBottoms[ag!];
            int newBottom = GridPlacementService.GetGroupBoundingBox(ag!, Tiles).MaxRow;
            if (newBottom > oldBottom)
            {
                var pushed = GridPlacementService.PushLowerGroupsDown(ag!, Groups, Tiles);
                foreach (var pt in pushed)
                {
                    if (!modified.Contains(pt)) modified.Add(pt);
                }
            }
            else if (newBottom < oldBottom)
            {
                int shrink = oldBottom - newBottom;
                var pulled = GridPlacementService.PullLowerGroupsUp(ag!, Groups, Tiles, shrink);
                foreach (var pt in pulled)
                {
                    if (!modified.Contains(pt)) modified.Add(pt);
                }
            }
        }

        AnimateModifiedTiles(modified);

        DropSlotIndicator.Visibility = Visibility.Collapsed;
        UpdateCanvasHeight();
        UpdateExposedAddSlots();
        UpdateGroupHeaderPositions();
        SaveGroupsAndLayout();

        string postModify = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
        if (preModify != postModify)
        {
            _historyService.PushState(preModify);
        }
    }

    public void BatchStyleSelectedTiles(string newStyle, TileModel anchorTile)
    {
        List<TileModel> targets = (anchorTile.IsSelected && SelectedTiles.Count > 1)
            ? SelectedTiles.ToList()
            : new List<TileModel> { anchorTile };

        string preModify = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);

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

        SaveGroupsAndLayout();

        string postModify = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
        if (preModify != postModify)
        {
            _historyService.PushState(preModify);
        }
    }

    public void DeleteSelectedTiles()
    {
        var targets = SelectedTiles.ToList();
        if (targets.Count == 0) return;
        BatchUnpinTiles(targets);
    }

    public void BatchUnpinSelectedTiles(TileModel anchorTile)
    {
        List<TileModel> targets = (anchorTile.IsSelected && SelectedTiles.Count > 1)
            ? SelectedTiles.ToList()
            : new List<TileModel> { anchorTile };

        BatchUnpinTiles(targets);
    }

    public void BatchUnpinTiles(IList<TileModel> targets)
    {
        if (targets == null || targets.Count == 0) return;

        var eligible = targets
            .Where(t => !t.IsLocked &&
                        !(t.Group != null && Groups.FirstOrDefault(g => g.Id == t.Group)?.IsLocked == true))
            .ToList();

        if (eligible.Count == 0)
        {
            var lockedGroup = targets
                .Where(t => t.Group != null)
                .Select(t => Groups.FirstOrDefault(g => g.Id == t.Group))
                .FirstOrDefault(g => g != null && g.IsLocked);
            if (lockedGroup != null)
            {
                FlashLockedGroupPerimeter(lockedGroup);
            }

            return;
        }

        string preUnpin = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
        _historyService.PushState(preUnpin);

        var affectedGroups = eligible
            .Where(t => !string.IsNullOrEmpty(t.Group))
            .Select(t => Groups.FirstOrDefault(g => g.Id == t.Group))
            .Where(g => g != null)
            .Distinct()
            .ToList();

        var oldBottoms = affectedGroups.ToDictionary(
            g => g!,
            g => GridPlacementService.GetGroupBoundingBox(g!, Tiles).MaxRow);

        foreach (var t in eligible)
        {
            t.Teardown();
            Tiles.Remove(t);
        }

        var modified = new List<TileModel>();
        foreach (var g in affectedGroups)
        {
            int oldBottom = oldBottoms[g!];
            int newBottom = GridPlacementService.GetGroupBoundingBox(g!, Tiles).MaxRow;
            int shrink = oldBottom - newBottom;
            if (shrink > 0)
            {
                var pulled = GridPlacementService.PullLowerGroupsUp(g!, Groups, Tiles, shrink);
                foreach (var pt in pulled)
                {
                    if (!modified.Contains(pt)) modified.Add(pt);
                }
            }
        }

        AnimateModifiedTiles(modified);
        CleanEmptyGroupsAndReflow();
        UpdateGroupHeaderPositions(animate: true);
        SaveGroupsAndLayout();
        UpdateCanvasHeight();
        UpdateExposedAddSlots();
    }

    private void OnTileUnpinned(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TileModel tile)
        {
            BatchUnpinSelectedTiles(tile);
        }
    }

    private void OnTileModified(object sender, RoutedEventArgs e)
    {
        TileModel? tile = (e is TileModifiedEventArgs args ? args.Tile : e.OriginalSource as TileModel);
        if (tile != null)
        {
            bool isResize = e is TileModifiedEventArgs tmArgs && tmArgs.IsResize;
            if (isResize)
            {
                BatchResizeSelectedTiles(tile.SpanX, tile.SpanY, tile);
            }
            else
            {
                string preModify = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
                SaveGroupsAndLayout();
                string postModify = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
                if (preModify != postModify)
                {
                    _historyService.PushState(preModify);
                }
            }
        }
    }

    private void AnimateModifiedTiles(IList<TileModel> modified)
    {
        foreach (var t in modified)
        {
            var container = TilesListBox.ItemContainerGenerator.ContainerFromItem(t) as ContentPresenter;
            if (container != null)
            {
                double currentLeft = Canvas.GetLeft(container);
                double currentTop = Canvas.GetTop(container);
                if (double.IsNaN(currentLeft)) currentLeft = t.X;
                if (double.IsNaN(currentTop)) currentTop = t.Y;

                if (Math.Abs(currentLeft - t.X) > 0.5 || Math.Abs(currentTop - t.Y) > 0.5)
                {
                    var animX = new DoubleAnimation(currentLeft, t.X, TimeSpan.FromMilliseconds(220))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    var animY = new DoubleAnimation(currentTop, t.Y, TimeSpan.FromMilliseconds(220))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    animX.Completed += (s, ev) =>
                    {
                        Canvas.SetLeft(container, t.X);
                        container.BeginAnimation(Canvas.LeftProperty, null);
                    };
                    animY.Completed += (s, ev) =>
                    {
                        Canvas.SetTop(container, t.Y);
                        container.BeginAnimation(Canvas.TopProperty, null);
                    };
                    container.BeginAnimation(Canvas.LeftProperty, animX);
                    container.BeginAnimation(Canvas.TopProperty, animY);
                }
                else
                {
                    Canvas.SetLeft(container, t.X);
                    Canvas.SetTop(container, t.Y);
                }
            }
        }

        UpdateGroupHeaderPositions();
    }

    private void OnAddTileClick(object sender, RoutedEventArgs e)
    {
        PromptAddTile();
    }

    public void UpdateExposedAddSlots()
    {
    }

    private void OnCanvasContextMenuClosed(object sender, RoutedEventArgs e)
    {
    }

    public void PromptAddTile()
    {
        IsDialogOpen = true;
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select Application or Shortcut to Pin",
                Filter = "Executables & Shortcuts (*.exe;*.lnk)|*.exe;*.lnk|All Files (*.*)|*.*",
                Multiselect = true
            };

            if (dialog.ShowDialog(this) == true && dialog.FileNames.Length > 0)
            {
                string preAdd = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
                _historyService.PushState(preAdd);

                foreach (string file in dialog.FileNames)
                {
                    AddFileAsTile(file, _canvasRightClickPoint.X, _canvasRightClickPoint.Y, recordHistory: false);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Failed to open add tile file dialog: {ex.Message}");
        }
        finally
        {
            IsDialogOpen = false;
            Activate();
        }
    }

    public void AddFileAsTile(string filePath, double x = 0, double y = 0, bool recordHistory = true)
    {
        if (File.Exists(filePath) || Directory.Exists(filePath))
        {
            if (recordHistory)
            {
                string preAdd = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
                _historyService.PushState(preAdd);
            }

            string title = Path.GetFileNameWithoutExtension(filePath);
            string? iconPath = IconExtractorService.ExtractAndCacheIcon(filePath);

            double viewportWidth = ContentScrollViewer?.ActualWidth > 0
                ? ContentScrollViewer.ActualWidth
                : (Width > 0 ? Width : 1920);
            int maxCols = GridPlacementService.GetMaxCols(viewportWidth);

            int col = x > 0 ? GridPlacementService.ColFromPixel(x) : 0;
            int row = y > 0 ? GridPlacementService.RowFromPixel(y) : 0;

            TileGroupModel? targetGroup = null;
            if (x > 0 || y > 0)
            {
                foreach (var g in Groups)
                {
                    var (minC, maxC, minR, maxR) = GridPlacementService.GetGroupBoundingBox(g, Tiles);
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
                var existingGroupTiles = Tiles.Where(t => t.Group == targetGroup.Id).ToList();
                var (slotCol, slotRow) = GridPlacementService.FindFreeSlotInGroup(
                    targetGroup, clickRelCol, clickRelRow, tile.SpanX, tile.SpanY, existingGroupTiles);
                tile.Col = slotCol;
                tile.Row = slotRow;
                tile.X = GridPlacementService.PixelXFromCol(slotCol);
                tile.Y = GridPlacementService.PixelYFromRow(slotRow);

                Tiles.Add(tile);
                var mod = GridPlacementService.PlaceTileInGroup(
                    tile, slotCol, slotRow, slotCol, slotRow, targetGroup, Tiles);
                var pushed = GridPlacementService.PushLowerGroupsDown(targetGroup, Groups, Tiles);
                foreach (var pt in pushed)
                {
                    if (!mod.Contains(pt)) mod.Add(pt);
                }

                AnimateModifiedTiles(mod);
                UpdateGroupHeaderPositions();
                StorageService.SaveLayout(Tiles);
                SaveGroupsAndLayout();
                UpdateCanvasHeight();
                UpdateExposedAddSlots();
                return;
            }

            var (freeCol, freeRow) = GridPlacementService.FindNearestAvailableSlot(
                col, Math.Max(1, row), 2, 2, Tiles, null, maxCols, Groups);

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

            Tiles.Add(tileUngrouped);

            if (Groups != null && Groups.Count > 0)
            {
                var looseTiles = Tiles.Where(t => string.IsNullOrEmpty(t.Group)).ToList();
                var pushedGroupTiles = GridPlacementService.PushGroupsDownFromLooseTiles(looseTiles, Groups, Tiles);
                AnimateModifiedTiles(pushedGroupTiles);
                UpdateGroupHeaderPositions(animate: true);
                CompactGroupGaps();
                SaveGroupsAndLayout();
            }

            StorageService.SaveLayout(Tiles);
            UpdateCanvasHeight();
            UpdateExposedAddSlots();
        }
    }

    public void ShowSetWeatherLocationDialog(Widgets.Catalog.Weather.WeatherWidgetViewModel weatherVm)
    {
        if (weatherVm == null) return;

        using (EnterDialogScope())
        {
            AcrylicModalWindow.ShowWeatherLocation(this, weatherVm);
        }
    }

    public void ShowAddWebLinkDialog(string? initialUrl = null)
    {
        using (EnterDialogScope())
        {
            var result = AcrylicModalWindow.ShowAddWebLink(this, initialUrl);
            if (result != null)
            {
                OnWebLinkCreated(this, result);
            }
        }
    }

    private void OnSidebarAddWebLinkRequested(object? sender, string? initialUrl)
    {
        ShowAddWebLinkDialog(initialUrl);
    }

    private void OnCanvasAddWebLinkClick(object sender, RoutedEventArgs e)
    {
        ShowAddWebLinkDialog();
    }

    private void OnWebLinkCreated(object? sender, WebLinkCreatedEventArgs e)
    {
        if (e.AddToCanvas)
        {
            AddWebLinkTile(e.Title, e.Url, e.IconPath, _canvasRightClickPoint.X, _canvasRightClickPoint.Y, recordHistory: true);
        }

        if (e.AddToSidebar)
        {
            SidebarRail?.AddWebLinkShortcut(e.Title, e.Url, e.IconPath);
        }
    }

    public void AddWebLinkTile(string title, string url, string? iconPath, double x = 0, double y = 0, bool recordHistory = true)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        string normalized = WebFaviconService.NormalizeUrl(url);
        string displayTitle = !string.IsNullOrWhiteSpace(title)
            ? title
            : WebFaviconService.InferTitleFromUrl(normalized);

        if (recordHistory)
        {
            string preAdd = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
            _historyService.PushState(preAdd);
        }

        double viewportWidth = ContentScrollViewer?.ActualWidth > 0
            ? ContentScrollViewer.ActualWidth
            : (Width > 0 ? Width : 1920);
        int maxCols = GridPlacementService.GetMaxCols(viewportWidth);

        int col = x > 0 ? GridPlacementService.ColFromPixel(x) : 0;
        int row = y > 0 ? GridPlacementService.RowFromPixel(y) : 0;

        TileGroupModel? targetGroup = null;
        if (x > 0 || y > 0)
        {
            foreach (var g in Groups)
            {
                var (minC, maxC, minR, maxR) = GridPlacementService.GetGroupBoundingBox(g, Tiles);
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
            var existingGroupTiles = Tiles.Where(t => t.Group == targetGroup.Id).ToList();
            var (slotCol, slotRow) = GridPlacementService.FindFreeSlotInGroup(
                targetGroup, clickRelCol, clickRelRow, tile.SpanX, tile.SpanY, existingGroupTiles);
            tile.Col = slotCol;
            tile.Row = slotRow;
            tile.X = GridPlacementService.PixelXFromCol(slotCol);
            tile.Y = GridPlacementService.PixelYFromRow(slotRow);

            Tiles.Add(tile);
            var mod = GridPlacementService.PlaceTileInGroup(
                tile, slotCol, slotRow, slotCol, slotRow, targetGroup, Tiles);
            var pushed = GridPlacementService.PushLowerGroupsDown(targetGroup, Groups, Tiles);
            foreach (var pt in pushed)
            {
                if (!mod.Contains(pt)) mod.Add(pt);
            }

            AnimateModifiedTiles(mod);
            UpdateGroupHeaderPositions();
            StorageService.SaveLayout(Tiles);
            SaveGroupsAndLayout();
            UpdateCanvasHeight();
            UpdateExposedAddSlots();
        }
        else
        {
            var (freeCol, freeRow) = GridPlacementService.FindNearestAvailableSlot(
                col, Math.Max(1, row), 2, 2, Tiles, null, maxCols, Groups);

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

            Tiles.Add(tile);

            if (Groups != null && Groups.Count > 0)
            {
                var looseTiles = Tiles.Where(t => string.IsNullOrEmpty(t.Group)).ToList();
                var pushedGroupTiles = GridPlacementService.PushGroupsDownFromLooseTiles(looseTiles, Groups, Tiles);
                AnimateModifiedTiles(pushedGroupTiles);
                UpdateGroupHeaderPositions(animate: true);
                CompactGroupGaps();
                SaveGroupsAndLayout();
            }

            StorageService.SaveLayout(Tiles);
            UpdateCanvasHeight();
            UpdateExposedAddSlots();
        }

        // Asynchronously fetch high-resolution favicon if not yet available
        if (string.IsNullOrWhiteSpace(iconPath))
        {
            _ = Task.Run(async () =>
            {
                string? fetched = await WebFaviconService.GetFaviconPathAsync(normalized);
                if (!string.IsNullOrWhiteSpace(fetched))
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        tile.IconPath = fetched;
                        StorageService.SaveLayout(Tiles);
                    });
                }
            });
        }
    }



    private void OnCanvasPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
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

        DependencyObject? dep = e.OriginalSource as DependencyObject;
        var tileControl = FindParent<Presentation.Controls.TileControl>(dep);

        if (tileControl != null && tileControl.DataContext is TileModel tile)
        {
            bool isCtrlDown = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

            if (isCtrlDown)
            {
                tile.IsSelected = !tile.IsSelected;
            }
            else
            {
                if (!tile.IsSelected)
                {
                    ClearTileSelection();
                    tile.IsSelected = true;
                }
            }
        }
        else
        {
            if (TilesListBox != null)
            {
                _canvasRightClickPoint = e.GetPosition(TilesListBox);
            }

            bool isCtrlDown = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            if (!isCtrlDown)
            {
                ClearTileSelection();
            }
        }
    }


    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is not Visual && parent is not System.Windows.Media.Media3D.Visual3D) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild) return typedChild;
            var desc = FindVisualChild<T>(child);
            if (desc != null) return desc;
        }
        return null;
    }

    private void OnCanvasContextMenuOpened(object sender, RoutedEventArgs e)
    {
        _activeContextMenuGroup = GetGroupAtCanvasPoint(_canvasRightClickPoint);

        bool isInsideGroup = _activeContextMenuGroup != null;
        UpdateBackdropMenuChecks();
        UpdateTileCornerMenuChecks();

        if (GroupMenuSeparator != null)
            GroupMenuSeparator.Visibility = isInsideGroup ? Visibility.Visible : Visibility.Collapsed;

        if (RenameGroupCanvasMenuItem != null)
        {
            RenameGroupCanvasMenuItem.Visibility = isInsideGroup ? Visibility.Visible : Visibility.Collapsed;
            RenameGroupCanvasMenuItem.IsEnabled = isInsideGroup && !_activeContextMenuGroup!.IsLocked;
        }

        if (GroupHeaderColorCanvasMenuItem != null)
            GroupHeaderColorCanvasMenuItem.Visibility = isInsideGroup ? Visibility.Visible : Visibility.Collapsed;

        if (GroupTintColorCanvasMenuItem != null)
            GroupTintColorCanvasMenuItem.Visibility = isInsideGroup ? Visibility.Visible : Visibility.Collapsed;

        if (LockGroupCanvasMenuItem != null)
        {
            LockGroupCanvasMenuItem.Visibility = isInsideGroup ? Visibility.Visible : Visibility.Collapsed;
            if (isInsideGroup)
            {
                LockGroupCanvasMenuItem.Header = _activeContextMenuGroup!.IsLocked ? "Unlock Group" : "Lock Group";
                if (LockGroupCanvasIcon != null)
                {
                    LockGroupCanvasIcon.Symbol = _activeContextMenuGroup.IsLocked
                        ? Wpf.Ui.Controls.SymbolRegular.LockOpen24
                        : Wpf.Ui.Controls.SymbolRegular.LockClosed24;
                }
            }
        }

        if (UngroupCanvasMenuItem != null)
        {
            UngroupCanvasMenuItem.Visibility = isInsideGroup ? Visibility.Visible : Visibility.Collapsed;
            UngroupCanvasMenuItem.IsEnabled = isInsideGroup && !_activeContextMenuGroup!.IsLocked;
        }

        if (DeleteGroupCanvasMenuItem != null)
        {
            DeleteGroupCanvasMenuItem.Visibility = isInsideGroup ? Visibility.Visible : Visibility.Collapsed;
            DeleteGroupCanvasMenuItem.IsEnabled = isInsideGroup && !_activeContextMenuGroup!.IsLocked;
        }

        if (CreateGroupCanvasMenuItem != null)
        {
            CreateGroupCanvasMenuItem.Visibility = isInsideGroup ? Visibility.Collapsed : Visibility.Visible;
        }

        if (!_isAppsLoaded && !_isLoadingApps)
        {
            _ = LoadAppsSubmenuAsync();
        }
    }

    private async void OnAppsSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!_isAppsLoaded)
        {
            await LoadAppsSubmenuAsync();
        }
    }

    private void StartBackgroundAppWarmup()
    {
        Task.Run(async () =>
        {
            try
            {
                var apps = InstalledAppsService.GetInstalledApps(forceRefresh: false);
                if (apps == null || apps.Count == 0) return;

                var groups = Presentation.Controls.AllAppsDrawerControl.CreateAlphabeticalGroups(apps);
                var ordered = apps.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();

                await Dispatcher.InvokeAsync(() =>
                {
                    AllAppsDrawer?.SetPreloadedApps(apps, groups);
                    if (AppsMenuItem != null)
                    {
                        AppsMenuItem.ItemsSource = null;
                        AppsMenuItem.Items.Clear();
                        AppsMenuItem.ItemsSource = ordered;
                    }
                    _isAppsLoaded = true;
                }, DispatcherPriority.ApplicationIdle);

                // Defer non-critical icon prewarming by 500ms so startup completes with 0% CPU/disk contention
                await Task.Delay(500);
                CatalogItemModel.PrewarmMemoryCache(apps);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppWarmup] Background warmup error: {ex.Message}");
            }
        });
    }

    private async Task LoadAppsSubmenuAsync()
    {
        if (_isAppsLoaded || _isLoadingApps) return;
        _isLoadingApps = true;

        try
        {
            var provider = CatalogService.GetProvider("installed_apps");
            if (provider != null && AppsMenuItem != null)
            {
                var items = await provider.GetItemsAsync();
                var ordered = items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();

                AppsMenuItem.ItemsSource = null;
                AppsMenuItem.Items.Clear();
                AppsMenuItem.ItemsSource = ordered;

                _isAppsLoaded = true;

                CatalogItemModel.PrewarmMemoryCache(ordered);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppsMenu] Error loading apps: {ex.Message}");
        }
        finally
        {
            _isLoadingApps = false;
        }
    }

    private void OnAppsCatalogChanged(List<CatalogItemModel> freshApps)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_isAppsLoaded && AppsMenuItem != null)
            {
                var ordered = freshApps.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
                AppsMenuItem.ItemsSource = null;
                AppsMenuItem.Items.Clear();
                AppsMenuItem.ItemsSource = ordered;
            }
            AllAppsDrawer?.LoadApps(freshApps);
        }, DispatcherPriority.Background);
    }

    private void TriggerBackgroundAppsCatalogRefresh()
    {
        if ((DateTime.UtcNow - _lastAppsRefreshTime).TotalSeconds < 10)
        {
            return;
        }

        _lastAppsRefreshTime = DateTime.UtcNow;

        Task.Run(async () =>
        {
            try
            {
                var provider = CatalogService.GetProvider("installed_apps");
                if (provider == null) return;

                var freshItems = await provider.GetItemsAsync(forceRefresh: true);

                if (_isAppsLoaded)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (AppsMenuItem != null)
                        {
                            var ordered = freshItems.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
                            AppsMenuItem.ItemsSource = null;
                            AppsMenuItem.Items.Clear();
                            AppsMenuItem.ItemsSource = ordered;
                        }
                    }, DispatcherPriority.Background);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppsCatalog] Background refresh error: {ex.Message}");
            }
        });
    }

    private void OnAppMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is CatalogItemModel item)
        {
            PinCatalogItem(item, _canvasRightClickPoint);
        }
    }

    public void PinCatalogItem(CatalogItemModel item, Point? targetCanvasPosition = null)
    {
        if (item == null) return;

        string prePin = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
        _historyService.PushState(prePin);

        UpdateLayoutMetrics();
        int maxCols = GridPlacementService.MaxCols;

        Point clickPoint = targetCanvasPosition ?? new Point(GridPlacementService.OriginX, GridPlacementService.OriginY);

        int col = GridPlacementService.ColFromPixel(clickPoint.X);
        int row = GridPlacementService.RowFromPixel(clickPoint.Y);

        TileGroupModel? targetGroup = null;
        if (targetCanvasPosition != null)
        {
            foreach (var g in Groups)
            {
                var (minC, maxC, minR, maxR) = GridPlacementService.GetGroupBoundingBox(g, Tiles);
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
            var existingPinGroupTiles = Tiles.Where(t => t.Group == targetGroup.Id).ToList();
            var (pinSlotCol, pinSlotRow) = GridPlacementService.FindFreeSlotInGroup(
                targetGroup, pinRelCol, pinRelRow, pinSpanX, pinSpanY, existingPinGroupTiles);
            groupTile.Col = pinSlotCol;
            groupTile.Row = pinSlotRow;
            groupTile.X = GridPlacementService.PixelXFromCol(pinSlotCol);
            groupTile.Y = GridPlacementService.PixelYFromRow(pinSlotRow);

            Tiles.Add(groupTile);

            if (item.TileType == TileType.WebUrl)
            {
                string targetUrl = item.TargetPath;
                var createdTile = groupTile;
                _ = Task.Run(async () =>
                {
                    string? fetched = await WebFaviconService.GetFaviconPathAsync(targetUrl).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(fetched))
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            createdTile.IconPath = fetched;
                            StorageService.SaveLayout(Tiles);
                        });
                    }
                });
            }
            var mod = GridPlacementService.PlaceTileInGroup(
                groupTile, pinSlotCol, pinSlotRow, pinSlotCol, pinSlotRow, targetGroup, Tiles);
            var pushed = GridPlacementService.PushLowerGroupsDown(targetGroup, Groups, Tiles);
            foreach (var pt in pushed)
            {
                if (!mod.Contains(pt)) mod.Add(pt);
            }

            AnimateModifiedTiles(mod);
            UpdateGroupHeaderPositions();
            StorageService.SaveLayout(Tiles);
            SaveGroupsAndLayout();
            UpdateCanvasHeight();
            UpdateExposedAddSlots();
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
            Tiles,
            null,
            maxCols,
            Groups);

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

        Tiles.Add(tile);

        if (item.TileType == TileType.WebUrl)
        {
            string targetUrl = item.TargetPath;
            var createdTile = tile;
            _ = Task.Run(async () =>
            {
                string? fetched = await WebFaviconService.GetFaviconPathAsync(targetUrl).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(fetched))
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        createdTile.IconPath = fetched;
                        StorageService.SaveLayout(Tiles);
                    });
                }
            });
        }

        if (Groups != null && Groups.Count > 0)
        {
            var looseTiles = Tiles.Where(t => string.IsNullOrEmpty(t.Group)).ToList();
            var pushedGroupTiles = GridPlacementService.PushGroupsDownFromLooseTiles(looseTiles, Groups, Tiles);
            AnimateModifiedTiles(pushedGroupTiles);
            UpdateGroupHeaderPositions(animate: true);
            CompactGroupGaps();
            SaveGroupsAndLayout();
        }

        StorageService.SaveLayout(Tiles);
        UpdateCanvasHeight();
        UpdateExposedAddSlots();
    }

    private void OnWidgetsSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem widgetsMenu) return;
        widgetsMenu.Items.Clear();

        var widgets = MetroHub.Widgets.Registry.WidgetRegistry.GetAll();
        if (widgets.Count == 0)
        {
            widgetsMenu.Items.Add(new MenuItem { Header = "No widgets available", IsEnabled = false });
            return;
        }

        var categories = new[]
        {
            "Quick Actions",
            "Sound",
            "Display",
            "Productivity",
            "Lifestyle"
        };

        bool isFirstGroup = true;

        foreach (var categoryName in categories)
        {
            var categoryWidgets = widgets
                .Where(w => string.Equals(w.Category, categoryName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (categoryWidgets.Count == 0) continue;

            if (!isFirstGroup)
            {
                widgetsMenu.Items.Add(new Separator());
            }
            isFirstGroup = false;

            // Section Header (non-selectable, subtle typography)
            var headerItem = new MenuItem
            {
                Header = categoryName.ToUpperInvariant(),
                IsEnabled = false,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)),
                Padding = new Thickness(10, 4, 14, 2),
                MinHeight = 22,
                Focusable = false
            };
            widgetsMenu.Items.Add(headerItem);

            foreach (var def in categoryWidgets)
            {
                var mi = new MenuItem
                {
                    Header = def.DisplayName,
                    Icon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = def.Icon,
                        FontSize = 18,
                        Foreground = new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF))
                    },
                    Tag = def,
                    Cursor = Cursors.Hand,
                    MinHeight = 32,
                    Padding = new Thickness(10, 4, 14, 4)
                };
                mi.Click += OnWidgetMenuItemClick;
                widgetsMenu.Items.Add(mi);
            }
        }
    }

    private void OnWidgetMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is MetroHub.Widgets.Registry.WidgetDefinition def)
        {
            PinWidget(def, _canvasRightClickPoint);
        }
    }

    public void PinWidget(MetroHub.Widgets.Registry.WidgetDefinition def, Point? targetCanvasPosition = null)
    {
        if (def == null) return;

        string prePin = LayoutHistoryService.CaptureSnapshot(Tiles, Groups);
        _historyService.PushState(prePin);

        UpdateLayoutMetrics();
        int maxCols = GridPlacementService.MaxCols;

        Point clickPoint = targetCanvasPosition ?? new Point(GridPlacementService.OriginX, GridPlacementService.OriginY);

        int col = GridPlacementService.ColFromPixel(clickPoint.X);
        int row = GridPlacementService.RowFromPixel(clickPoint.Y);

        TileGroupModel? targetGroup = null;
        if (targetCanvasPosition != null)
        {
            foreach (var g in Groups)
            {
                var (minC, maxC, minR, maxR) = GridPlacementService.GetGroupBoundingBox(g, Tiles);
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
            var existingPinGroupTiles = Tiles.Where(t => t.Group == targetGroup.Id).ToList();
            var (pinSlotCol, pinSlotRow) = GridPlacementService.FindFreeSlotInGroup(
                targetGroup, pinRelCol, pinRelRow, spanX, spanY, existingPinGroupTiles);
            groupTile.Col = pinSlotCol;
            groupTile.Row = pinSlotRow;
            groupTile.X = GridPlacementService.PixelXFromCol(pinSlotCol);
            groupTile.Y = GridPlacementService.PixelYFromRow(pinSlotRow);

            Tiles.Add(groupTile);
            var mod = GridPlacementService.PlaceTileInGroup(
                groupTile, pinSlotCol, pinSlotRow, pinSlotCol, pinSlotRow, targetGroup, Tiles, Groups, false);
            var pushed = GridPlacementService.PushLowerGroupsDown(targetGroup, Groups, Tiles);
            foreach (var pt in pushed)
            {
                if (!mod.Contains(pt)) mod.Add(pt);
            }

            AnimateModifiedTiles(mod);
            UpdateGroupHeaderPositions();
            StorageService.SaveLayout(Tiles);
            SaveGroupsAndLayout();
            UpdateCanvasHeight();
            UpdateExposedAddSlots();
            return;
        }

        int clampedCol = Math.Max(0, Math.Min(col, maxCols - spanX));
        int clampedRow = Math.Max(1, row);

        var (freeCol, freeRow) = GridPlacementService.FindNearestAvailableSlot(
            clampedCol,
            clampedRow,
            spanX,
            spanY,
            Tiles,
            null,
            maxCols,
            Groups);

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

        Tiles.Add(tile);

        var modLoose = GridPlacementService.PlaceAndResolveCollisions(
            tile, freeCol, freeRow, freeCol, freeRow, maxCols, Tiles, groups: Groups);

        if (Groups != null && Groups.Count > 0)
        {
            var looseTiles = Tiles.Where(t => string.IsNullOrEmpty(t.Group)).ToList();
            var pushedGroupTiles = GridPlacementService.PushGroupsDownFromLooseTiles(looseTiles, Groups, Tiles);
            foreach (var pt in pushedGroupTiles)
            {
                if (!modLoose.Contains(pt)) modLoose.Add(pt);
            }
            UpdateGroupHeaderPositions(animate: true);
            CompactGroupGaps();
        }

        AnimateModifiedTiles(modLoose);
        SaveGroupsAndLayout();
        StorageService.SaveLayout(Tiles);
        UpdateCanvasHeight();
        UpdateExposedAddSlots();
    }


    private void OnExportLayoutClick(object sender, RoutedEventArgs e)
    {
        IsDialogOpen = true;
        try
        {
            var sfd = new SaveFileDialog
            {
                Title = "Export MetroHub Layout",
                Filter = "JSON Files (*.json)|*.json",
                FileName = "MetroHub_Layout.json"
            };

            if (sfd.ShowDialog(this) == true)
            {
                if (StorageService.ExportLayout(Tiles, sfd.FileName))
                {
                    System.Windows.MessageBox.Show(this, "Layout exported successfully!", "MetroHub",
                        System.Windows.MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Failed to export layout: {ex.Message}");
        }
        finally
        {
            IsDialogOpen = false;
            Activate();
        }
    }

    private void OnTileCornerSharpClick(object sender, RoutedEventArgs e) => ApplyTileCornerRadius(0);
    private void OnTileCornerSubtleClick(object sender, RoutedEventArgs e) => ApplyTileCornerRadius(2);
    private void OnTileCornerMediumClick(object sender, RoutedEventArgs e) => ApplyTileCornerRadius(4);
    private void OnTileCornerRoundedClick(object sender, RoutedEventArgs e) => ApplyTileCornerRadius(8);

    public void ApplyTileCornerRadius(int radius)
    {
        Settings.TileCornerRadius = radius;
        StorageService.SaveSettings(Settings);
        Application.Current.Resources["TileCornerRadius"] = new CornerRadius(radius);
        UpdateTileCornerMenuChecks();
    }

    private void UpdateTileCornerMenuChecks()
    {
        int r = Settings.TileCornerRadius;
        if (TileCornerSharpItem != null) TileCornerSharpItem.IsChecked = r == 0;
        if (TileCornerSubtleItem != null) TileCornerSubtleItem.IsChecked = r == 2;
        if (TileCornerMediumItem != null) TileCornerMediumItem.IsChecked = r == 4;
        if (TileCornerRoundedItem != null) TileCornerRoundedItem.IsChecked = r == 8;
    }
}
