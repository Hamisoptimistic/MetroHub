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
        WpfTestHost.RunSta(() =>
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
        WpfTestHost.RunSta(() =>
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
        WpfTestHost.RunSta(() =>
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
        WpfTestHost.RunSta(() =>
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
        WpfTestHost.RunSta(() =>
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
        WpfTestHost.RunSta(() =>
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
        WpfTestHost.RunSta(() =>
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
        WpfTestHost.RunSta(() =>
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
        WpfTestHost.RunSta(() =>
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

    [Fact]
    public void TileControl_ContextMenu_IsLazyAndSharedAcrossInstances()
    {
        WpfTestHost.RunSta(() =>
        {
            var tileModel1 = new Core.Models.TileModel { Title = "App1", TargetPath = "C:\\app1.exe" };
            var tileModel2 = new Core.Models.TileModel { Title = "App2", TargetPath = "C:\\app2.exe" };

            var tile1 = new Presentation.Controls.TileControl { DataContext = tileModel1 };
            var tile2 = new Presentation.Controls.TileControl { DataContext = tileModel2 };

            // 1. Verify zero eager ContextMenu allocation on creation (Lazy realization)
            Assert.Null(tile1.RootBorder.ContextMenu);
            Assert.Null(tile2.RootBorder.ContextMenu);

            // 2. Simulate right click opening on tile 1
            tile1.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Right)
            {
                RoutedEvent = System.Windows.UIElement.PreviewMouseRightButtonDownEvent
            });
            var menu1 = tile1.RootBorder.ContextMenu;
            Assert.NotNull(menu1);

            // 3. Simulate right click opening on tile 2 - must reuse same menu instance and detach tile 1
            tile2.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Right)
            {
                RoutedEvent = System.Windows.UIElement.PreviewMouseRightButtonDownEvent
            });
            var menu2 = tile2.RootBorder.ContextMenu;
            Assert.NotNull(menu2);
            Assert.Same(menu1, menu2);
            Assert.Null(tile1.RootBorder.ContextMenu);
        });
    }

    [Fact]
    public void PullLowerGroupsUp_DoesNotPullLooseTilesBelowGroup()
    {
        var group = new TileGroupModel
        {
            Id = "grp1",
            Col = 0,
            Row = 0,
            Title = "Top Group"
        };
        var groupMember = new TileModel
        {
            Id = "member1",
            Group = "grp1",
            Col = 0,
            Row = 1,
            SpanX = 2,
            SpanY = 2
        };
        var looseTile = new TileModel
        {
            Id = "bleachbit",
            Group = null,
            Col = 0,
            Row = 15,
            SpanX = 2,
            SpanY = 2,
            Y = GridPlacementService.PixelYFromRow(15)
        };

        var groups = new List<TileGroupModel> { group };
        var allTiles = new List<TileModel> { groupMember, looseTile };

        // Even with maxPullUpRows = null (unbounded pull as in CompactGroupGaps),
        // loose tiles must remain at their explicit user-placed coordinates.
        var modified = GridPlacementService.PullLowerGroupsUp(group, groups, allTiles);

        Assert.Equal(15, looseTile.Row);
        Assert.DoesNotContain(looseTile, modified);
    }

    [Fact]
    public void PullLowerGroupsUp_PullsLowerGroupsWhenUpperGroupShrinks()
    {
        var upperGroup = new TileGroupModel
        {
            Id = "grpUpper",
            Col = 0,
            Row = 0,
            Title = "Upper Group"
        };
        var upperMember = new TileModel
        {
            Id = "upperMember",
            Group = "grpUpper",
            Col = 0,
            Row = 1,
            SpanX = 2,
            SpanY = 2
        };
        var lowerGroup = new TileGroupModel
        {
            Id = "grpLower",
            Col = 0,
            Row = 8,
            Title = "Lower Group"
        };
        var lowerMember = new TileModel
        {
            Id = "lowerMember",
            Group = "grpLower",
            Col = 0,
            Row = 9,
            SpanX = 2,
            SpanY = 2
        };

        var groups = new List<TileGroupModel> { upperGroup, lowerGroup };
        var allTiles = new List<TileModel> { upperMember, lowerMember };

        var modified = GridPlacementService.PullLowerGroupsUp(upperGroup, groups, allTiles, maxPullUpRows: 3);

        Assert.Equal(5, lowerGroup.Row);
        Assert.Equal(6, lowerMember.Row);
        Assert.Contains(lowerMember, modified);
    }

    [Fact]
    public void TileControl_AtRest_IsBorderless()
    {
        WpfTestHost.RunSta(() =>
        {
            var model = new TileModel { Id = "test1", Title = "Test Tile", IsSelected = false, IsBeingDragged = false };
            var tile = new Presentation.Controls.TileControl { DataContext = model };
            tile.ApplyTemplate();

            // ContentClipHost border thickness must be 0
            Assert.Equal(new System.Windows.Thickness(0), tile.ContentClipHost.BorderThickness);
            Assert.Equal(new System.Windows.Thickness(0), Presentation.Controls.ClipHelper.GetBorderThickness(tile.ContentClipHost));
            Assert.True(Presentation.Controls.ClipHelper.GetIsCornerClipped(tile.ContentClipHost));

            // RestBorder default brush must be Transparent
            Assert.Equal(System.Windows.Media.Brushes.Transparent, tile.RestBorder.BorderBrush);

            // RootBorder border thickness must be 0
            Assert.Equal(new System.Windows.Thickness(0), tile.RootBorder.BorderThickness);
        });
    }

    [Fact]
    public void TileControl_WhenSelectedOrDragged_ActivatesAccentBorder()
    {
        WpfTestHost.RunSta(() =>
        {
            var model = new TileModel { Id = "test1", Title = "Test Tile", IsSelected = true, IsBeingDragged = false };
            var tile = new Presentation.Controls.TileControl { DataContext = model };
            var window = new System.Windows.Window { Content = tile, Width = 300, Height = 300 };
            window.Show();

            // Process dispatcher to ensure bindings and triggers update
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

            // RestBorder brush becomes the accent brush when selected
            var accentBrush = System.Windows.Application.Current.FindResource("SystemAccentColorPrimaryBrush");
            Assert.Equal(accentBrush, tile.RestBorder.BorderBrush);

            // RestBorder brush becomes the accent brush when dragged
            model.IsSelected = false;
            model.IsBeingDragged = true;
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            Assert.Equal(accentBrush, tile.RestBorder.BorderBrush);

            window.Close();
        });
    }

    [Fact]
    public void AllAppsDrawer_RecentGridTile_AtRest_IsBorderless_AndSelected_ShowsAccentBorder()
    {
        WpfTestHost.RunSta(() =>
        {
            var drawer = new Presentation.Controls.AllAppsDrawerControl();
            drawer.ApplyTemplate();
            var template = drawer.Resources["RecentGridTileTemplate"] as System.Windows.DataTemplate;
            Assert.NotNull(template);

            var content = template.LoadContent() as System.Windows.Controls.Border;
            Assert.NotNull(content);
            Assert.Equal(new System.Windows.Thickness(0), content.BorderThickness);

            var grid = content.Child as System.Windows.Controls.Grid;
            Assert.NotNull(grid);
            var restBorder = grid.Children.OfType<System.Windows.Controls.Border>().FirstOrDefault(b => b.Name == "RestBorder");
            Assert.NotNull(restBorder);
            Assert.Equal(System.Windows.Media.Brushes.Transparent, restBorder.BorderBrush);
            Assert.Equal(new System.Windows.Thickness(1), restBorder.BorderThickness);

            // Verify that selection trigger on ListBoxItem activates SystemAccentColorPrimaryBrush
            var candidate = new Core.Search.Candidate("app:test", "Test", @"C:\test.exe", Core.Search.SearchCategory.Apps, "test");
            var itemStyle = drawer.Resources["FluentSearchListBoxItemStyle"] as System.Windows.Style;
            var listBox = new System.Windows.Controls.ListBox
            {
                ItemContainerStyle = itemStyle,
                ItemTemplate = template,
                ItemsSource = new[] { new Presentation.Controls.SearchItemRowViewModel(candidate) }
            };

            var window = new System.Windows.Window { Content = listBox, Width = 200, Height = 200 };
            window.Show();
            listBox.SelectedIndex = 0;
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

            var lbi = listBox.ItemContainerGenerator.ContainerFromIndex(0) as System.Windows.Controls.ListBoxItem;
            Assert.NotNull(lbi);
            Assert.True(lbi.IsSelected);

            var card = FindVisualChild<System.Windows.Controls.Border>(lbi);
            Assert.NotNull(card);
            var cardGrid = card.Child as System.Windows.Controls.Grid;
            Assert.NotNull(cardGrid);
            var liveRestBorder = cardGrid.Children.OfType<System.Windows.Controls.Border>().FirstOrDefault(b => b.Name == "RestBorder");
            Assert.NotNull(liveRestBorder);

            var accentBrush = System.Windows.Application.Current.FindResource("SystemAccentColorPrimaryBrush");
            Assert.Equal(accentBrush, liveRestBorder.BorderBrush);

            window.Close();
        });
    }

    private static T? FindVisualChild<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var descendant = FindVisualChild<T>(child);
            if (descendant != null) return descendant;
        }
        return null;
    }

    [Fact]
    public void ClipHelper_WhenBorderThicknessZero_ClipsToFullBounds()
    {
        WpfTestHost.RunSta(() =>
        {
            var border = new System.Windows.Controls.Border
            {
                Width = 100,
                Height = 100
            };
            var window = new System.Windows.Window { Content = border, Width = 200, Height = 200 };
            Presentation.Controls.ClipHelper.SetCornerRadius(border, new System.Windows.CornerRadius(4));
            Presentation.Controls.ClipHelper.SetBorderThickness(border, new System.Windows.Thickness(0));
            Presentation.Controls.ClipHelper.SetIsCornerClipped(border, true);

            window.Show();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

            Assert.NotNull(border.Clip);
            var rectGeom = Assert.IsType<System.Windows.Media.RectangleGeometry>(border.Clip);
            Assert.Equal(new System.Windows.Rect(0, 0, 100, 100), rectGeom.Rect);
            Assert.Equal(4.0, rectGeom.RadiusX);
            Assert.Equal(4.0, rectGeom.RadiusY);

            window.Close();
        });
    }
}


