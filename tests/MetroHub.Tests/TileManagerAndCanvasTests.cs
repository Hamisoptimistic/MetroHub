using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Presentation.Controllers;
using Xunit;

namespace MetroHub.Tests;

public class TileManagerAndCanvasTests
{
    private static TileManager CreateTestTileManager(
        ObservableCollection<TileModel> tiles,
        ObservableCollection<TileGroupModel> groups,
        LayoutHistoryService? historyService = null,
        Action? saveAction = null,
        Action<TileGroupModel>? flashLockedAction = null)
    {
        return new TileManager(
            tilesProvider: () => tiles,
            groupsProvider: () => groups,
            tilesListBoxProvider: () => null,
            contentScrollViewerProvider: () => null,
            windowWidthProvider: () => 1920.0,
            animateModifiedTilesAction: _ => { },
            updateGroupHeaderPositionsAction: () => { },
            updateLayoutMetricsAction: () => { },
            updateCanvasHeightAction: () => { },
            updateExposedAddSlotsAction: () => { },
            saveGroupsAndLayoutAction: saveAction ?? (() => { }),
            cleanEmptyGroupsAndReflowAction: () => { },
            compactGroupGapsAction: () => { },
            flashLockedGroupAction: flashLockedAction ?? (_ => { }),
            hideDropSlotIndicatorAction: () => { },
            historyService: historyService ?? new LayoutHistoryService(),
            dispatcher: Dispatcher.CurrentDispatcher);
    }

    [Fact]
    public void TileManager_ClearSelection_UnselectsAllTiles()
    {
        MarkdownTestHost.RunSta(() =>
        {
            var tiles = new ObservableCollection<TileModel>
            {
                new() { Id = "t1", IsSelected = true },
                new() { Id = "t2", IsSelected = true },
                new() { Id = "t3", IsSelected = false }
            };
            var groups = new ObservableCollection<TileGroupModel>();

            var manager = CreateTestTileManager(tiles, groups);
            manager.ClearSelection();

            Assert.All(tiles, t => Assert.False(t.IsSelected));
        });
    }

    [Fact]
    public void TileManager_GetSelectedTiles_ReturnsOnlySelected()
    {
        MarkdownTestHost.RunSta(() =>
        {
            var tiles = new ObservableCollection<TileModel>
            {
                new() { Id = "t1", IsSelected = true },
                new() { Id = "t2", IsSelected = false },
                new() { Id = "t3", IsSelected = true }
            };
            var groups = new ObservableCollection<TileGroupModel>();

            var manager = CreateTestTileManager(tiles, groups);
            var selected = manager.GetSelectedTiles();

            Assert.Equal(2, selected.Count);
            Assert.Contains(selected, t => t.Id == "t1");
            Assert.Contains(selected, t => t.Id == "t3");
        });
    }

    [Fact]
    public void TileManager_BatchStyleSelectedTiles_AppliesStyleToAllSelected()
    {
        MarkdownTestHost.RunSta(() =>
        {
            var tiles = new ObservableCollection<TileModel>
            {
                new() { Id = "t1", TileStyle = "Glass", IsSelected = true },
                new() { Id = "t2", TileStyle = "Glass", IsSelected = true },
                new() { Id = "t3", TileStyle = "Glass", IsSelected = false }
            };
            var groups = new ObservableCollection<TileGroupModel>();
            var history = new LayoutHistoryService();
            bool saveInvoked = false;

            var manager = CreateTestTileManager(tiles, groups, history, () => saveInvoked = true);
            manager.BatchStyleSelectedTiles("Translucent", tiles[0]);

            Assert.True(saveInvoked);
            Assert.Equal("Translucent", tiles[0].TileStyle);
            Assert.Equal("Translucent", tiles[1].TileStyle);
            Assert.Equal("Glass", tiles[2].TileStyle); // Unselected remains unchanged
            Assert.True(history.CanUndo);
        });
    }

    [Fact]
    public void TileManager_BatchUnpinTiles_RemovesUnlockedTiles()
    {
        MarkdownTestHost.RunSta(() =>
        {
            var tiles = new ObservableCollection<TileModel>
            {
                new() { Id = "t1", Title = "App 1", IsLocked = false },
                new() { Id = "t2", Title = "App 2", IsLocked = false },
                new() { Id = "t3", Title = "App 3", IsLocked = false }
            };
            var groups = new ObservableCollection<TileGroupModel>();
            var history = new LayoutHistoryService();

            var manager = CreateTestTileManager(tiles, groups, history);
            manager.BatchUnpinTiles(new List<TileModel> { tiles[0], tiles[1] });

            Assert.Single(tiles);
            Assert.Equal("t3", tiles[0].Id);
            Assert.True(history.CanUndo);
        });
    }

    [Fact]
    public void TileManager_BatchUnpinTiles_PreservesLockedTilesAndLockedGroups()
    {
        MarkdownTestHost.RunSta(() =>
        {
            var lockedGroup = new TileGroupModel { Id = "g_locked", Title = "Locked Group", IsLocked = true };
            var openGroup = new TileGroupModel { Id = "g_open", Title = "Open Group", IsLocked = false };

            var tiles = new ObservableCollection<TileModel>
            {
                new() { Id = "t_locked", Title = "Locked Tile", IsLocked = true, Group = openGroup.Id },
                new() { Id = "t_in_locked_group", Title = "Group Tile", IsLocked = false, Group = lockedGroup.Id },
                new() { Id = "t_free", Title = "Free Tile", IsLocked = false, Group = openGroup.Id }
            };
            var groups = new ObservableCollection<TileGroupModel> { lockedGroup, openGroup };

            var manager = CreateTestTileManager(tiles, groups);

            // Attempt to unpin all tiles
            manager.BatchUnpinTiles(tiles.ToList());

            // Only t_free should be removed; t_locked and t_in_locked_group must remain safe!
            Assert.Equal(2, tiles.Count);
            Assert.Contains(tiles, t => t.Id == "t_locked");
            Assert.Contains(tiles, t => t.Id == "t_in_locked_group");
            Assert.DoesNotContain(tiles, t => t.Id == "t_free");
        });
    }

    [Fact]
    public void TileManager_BatchUnpinTiles_WhenAllLocked_TriggersFlashLockedGroup()
    {
        MarkdownTestHost.RunSta(() =>
        {
            var lockedGroup = new TileGroupModel { Id = "g_locked", Title = "Locked Group", IsLocked = true };
            var tiles = new ObservableCollection<TileModel>
            {
                new() { Id = "t_in_locked_group", Title = "Group Tile", IsLocked = false, Group = lockedGroup.Id }
            };
            var groups = new ObservableCollection<TileGroupModel> { lockedGroup };

            bool flashedGroup = false;
            var manager = CreateTestTileManager(tiles, groups, flashLockedAction: g =>
            {
                if (g.Id == lockedGroup.Id) flashedGroup = true;
            });

            manager.BatchUnpinTiles(tiles.ToList());

            Assert.True(flashedGroup);
            Assert.Single(tiles);
        });
    }

    [Fact]
    public void TileManager_RestoreLayoutFromSnapshot_RestoresPreviousCoordinates()
    {
        MarkdownTestHost.RunSta(() =>
        {
            var tiles = new ObservableCollection<TileModel>
            {
                new() { Id = "t1", Col = 0, Row = 0, X = 0, Y = 0, SpanX = 2, SpanY = 2 }
            };
            var groups = new ObservableCollection<TileGroupModel>();

            string originalSnapshot = LayoutHistoryService.CaptureSnapshot(tiles, groups);

            // Modify tile position
            tiles[0].Col = 4;
            tiles[0].Row = 6;
            tiles[0].X = 200;
            tiles[0].Y = 300;

            var manager = CreateTestTileManager(tiles, groups);
            manager.RestoreLayoutFromSnapshot(originalSnapshot);

            Assert.Equal(0, tiles[0].Col);
            Assert.Equal(0, tiles[0].Row);
            Assert.Equal(0, tiles[0].X);
            Assert.Equal(0, tiles[0].Y);
        });
    }

    [Theory]
    [InlineData(1280, 1.0)]
    [InlineData(1920, 1.0)]
    [InlineData(2500, 1.0)]
    [InlineData(3840, 1.75)] // 3840 / 1920 = 2.0 -> clamped to 1.75
    [InlineData(2880, 1.5)]  // 2880 / 1920 = 1.5
    public void CanvasGroupManager_UpdateScaleFactor_CalculatesExpectedScale(double viewportWidth, double expectedScale)
    {
        double recordedScale = 0.0;
        var manager = new CanvasGroupManager(
            groupsProvider: () => new ObservableCollection<TileGroupModel>(),
            tilesProvider: () => new ObservableCollection<TileModel>(),
            groupsListBoxProvider: () => null,
            groupTintBackplatesProvider: () => null,
            contentScrollViewerProvider: () => null,
            mainCanvasGridProvider: () => null,
            windowWidthProvider: () => viewportWidth,
            selectedTilesProvider: () => new List<TileModel>(),
            animateModifiedTilesAction: _ => { },
            updateExposedAddSlotsAction: () => { },
            flashLockedGroupAction: _ => { },
            clearTileSelectionAction: () => { },
            historyService: new LayoutHistoryService(),
            setCurrentScaleAction: scale => recordedScale = scale);

        manager.UpdateScaleFactor(viewportWidth);

        Assert.Equal(expectedScale, recordedScale, 2);
    }

    [Fact]
    public void IsInteractiveElement_TileSurfaceAndLabels_ReturnsFalse()
    {
        MarkdownTestHost.RunSta(() =>
        {
            var tileControl = new Presentation.Controls.TileControl();
            var grid = new System.Windows.Controls.Grid();
            var textBlock = new System.Windows.Controls.TextBlock { Text = "Notepad" };
            grid.Children.Add(textBlock);
            tileControl.Content = grid;

            bool isInteractive = MainWindow.IsInteractiveElement(textBlock);
            Assert.False(isInteractive);
        });
    }

    [Fact]
    public void IsInteractiveElement_ButtonInsideTile_ReturnsTrue()
    {
        MarkdownTestHost.RunSta(() =>
        {
            var tileControl = new Presentation.Controls.TileControl();
            var grid = new System.Windows.Controls.Grid();
            var button = new System.Windows.Controls.Button { Content = "Play" };
            grid.Children.Add(button);
            tileControl.Content = grid;

            bool isInteractive = MainWindow.IsInteractiveElement(button);
            Assert.True(isInteractive);
        });
    }
}

