using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Presentation.Controllers;
using MenuItem = System.Windows.Controls.MenuItem;

namespace MetroHub;

public partial class MainWindow
{
    private CanvasGroupManager? _canvasGroupManager;
    public CanvasGroupManager CanvasGroupManager => _canvasGroupManager ??= new CanvasGroupManager(
        () => Groups,
        () => Tiles,
        () => GroupsListBox,
        () => GroupTintBackplates,
        () => ContentScrollViewer,
        () => MainCanvasGrid,
        () => Width,
        () => SelectedTiles,
        AnimateModifiedTiles,
        UpdateExposedAddSlots,
        FlashLockedGroupPerimeter,
        ClearTileSelection,
        _historyService,
        scale => CurrentScale = scale);

    private TileGroupModel? _activeContextMenuGroup
    {
        get => CanvasGroupManager.ActiveContextMenuGroup;
        set => CanvasGroupManager.ActiveContextMenuGroup = value;
    }

    private void UpdateScaleFactor(double viewportWidth) => CanvasGroupManager.UpdateScaleFactor(viewportWidth);

    private void UpdateLayoutMetrics() => CanvasGroupManager.UpdateLayoutMetrics();

    private void UpdateCanvasHeight() => CanvasGroupManager.UpdateCanvasHeight();

    private void DiscoverGroupsFromTiles() => CanvasGroupManager.DiscoverGroupsFromTiles();

    private void EnsureGroupIndices() => CanvasGroupManager.EnsureGroupIndices();

    private void MigrateGroupColumnOffsets() => CanvasGroupManager.MigrateGroupColumnOffsets();

    public void CompactGroupGaps() => CanvasGroupManager.CompactGroupGaps();

    public void CleanEmptyGroupsAndReflow() => CanvasGroupManager.CleanEmptyGroupsAndReflow();

    public void EnsureGroupsHaveHeaderSpace() => CanvasGroupManager.EnsureGroupsHaveHeaderSpace();

    public void UpdateGroupHeaderPositions(bool animate = false) => CanvasGroupManager.UpdateGroupHeaderPositions(animate);

    public void SaveGroupsAndLayout() => CanvasGroupManager.SaveGroupsAndLayout();

    public void CreateGroupFromSelectedTiles(TileModel anchorTile) => CanvasGroupManager.CreateGroupFromSelectedTiles(anchorTile);

    public void AddTilesToExistingGroup(IList<TileModel> incomingTiles, TileGroupModel targetGroup) => CanvasGroupManager.AddTilesToExistingGroup(incomingTiles, targetGroup);

    public void SetGroupColor(TileGroupModel group, string hex) => CanvasGroupManager.SetGroupColor(group, hex);

    public void SetGroupTintColor(TileGroupModel group, string? hex) => CanvasGroupManager.SetGroupTintColor(group, hex);

    public void ToggleGroupLock(TileGroupModel group) => CanvasGroupManager.ToggleGroupLock(group);

    public void RenameGroup(TileGroupModel group, string newTitle) => CanvasGroupManager.RenameGroup(group, newTitle);

    public LayoutHistoryService HistoryService => _historyService;

    public void UngroupTiles(TileGroupModel group) => CanvasGroupManager.UngroupTiles(group);

    public void DeleteGroupAndTiles(TileGroupModel group) => CanvasGroupManager.DeleteGroupAndTiles(group);

    private void OnCreateGroupCanvasClick(object sender, RoutedEventArgs e)
    {
        CreateGroupAtPosition(_canvasRightClickPoint);
    }

    public void CreateGroupAtPosition(Point canvasPoint) => CanvasGroupManager.CreateGroupAtPosition(canvasPoint);

    private TileGroupModel? GetGroupAtCanvasPoint(Point pt) => CanvasGroupManager.GetGroupAtCanvasPoint(pt);

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
