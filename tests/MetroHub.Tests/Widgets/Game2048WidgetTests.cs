using System;
using System.Collections.Generic;
using System.Linq;
using MetroHub.Core.Models;
using MetroHub.Widgets;
using MetroHub.Widgets.Catalog.Game2048;
using MetroHub.Widgets.Registry;
using Xunit;

namespace MetroHub.Tests.Widgets;

public class Game2048WidgetTests
{
    private static readonly int[] InitialRowTestValues = [2, 2, 0, 0];
    private static readonly int[] TripleRowTestValues = [2, 2, 2, 0];

    [Fact]
    public void Engine_InitializesWithTwoTiles()
    {
        var engine = new Game2048Engine();

        Assert.Equal(2, engine.Tiles.Count);
        Assert.Equal(0, engine.Score);
        Assert.False(engine.IsGameOver);
        Assert.False(engine.HasWon);

        // Every initial tile must be 2 or 4
        Assert.All(engine.Tiles, t => Assert.True(t.Value is 2 or 4));
    }

    [Fact]
    public void Engine_SlideLeft_MergesAdjacentEqualTiles()
    {
        var engine = new Game2048Engine();
        // Load custom state: row 0 has [2, 2, 0, 0], rest 0
        var grid = new List<int>(16);
        grid.AddRange(InitialRowTestValues);
        grid.AddRange(Enumerable.Repeat(0, 12));

        engine.LoadState(grid, currentScore: 0, bestScore: 0);

        bool moved = engine.Move(MoveDirection.Left);
        Assert.True(moved);

        var exported = engine.ExportGrid();
        Assert.Equal(4, exported[0]); // Merged into 4
        Assert.Equal(4, engine.Score);
    }

    [Fact]
    public void Engine_Slide_DoesNotDoubleMergeInSingleTurn()
    {
        var engine = new Game2048Engine();
        // Row 0 has [2, 2, 2, 0], rest 0
        var grid = new List<int>(16);
        grid.AddRange(TripleRowTestValues);
        grid.AddRange(Enumerable.Repeat(0, 12));

        engine.LoadState(grid, currentScore: 0, bestScore: 0);

        bool moved = engine.Move(MoveDirection.Left);
        Assert.True(moved);

        var exported = engine.ExportGrid();
        // Index 0 and 1 merged into 4 at pos 0; index 2 moved to pos 1 as 2
        Assert.Equal(4, exported[0]);
        Assert.Equal(2, exported[1]);
        Assert.Equal(4, engine.Score);
    }

    [Fact]
    public void Engine_SlideDown_MergesCorrectly()
    {
        var engine = new Game2048Engine();
        // Column 0 has 2 at row 0, 2 at row 3
        var grid = new List<int>(16)
        {
            2, 0, 0, 0,
            0, 0, 0, 0,
            0, 0, 0, 0,
            2, 0, 0, 0
        };

        engine.LoadState(grid, currentScore: 0, bestScore: 0);

        bool moved = engine.Move(MoveDirection.Down);
        Assert.True(moved);

        var exported = engine.ExportGrid();
        Assert.Equal(4, exported[12]); // Row 3, Col 0 is index 12
    }

    [Fact]
    public void Engine_DetectsGameOver_WhenNoMovesAvailable()
    {
        var engine = new Game2048Engine();
        // Full checkerboard-style grid with zero adjacent matching numbers
        var grid = new List<int>
        {
            2, 4, 2, 4,
            4, 2, 4, 2,
            2, 4, 2, 4,
            4, 2, 4, 2
        };

        engine.LoadState(grid, currentScore: 100, bestScore: 500);

        Assert.True(engine.IsGameOver);
        // Attempting to move returns false
        Assert.False(engine.Move(MoveDirection.Left));
        Assert.False(engine.Move(MoveDirection.Right));
        Assert.False(engine.Move(MoveDirection.Up));
        Assert.False(engine.Move(MoveDirection.Down));
    }

    [Fact]
    public void Engine_DetectsWinCondition_When2048Reached()
    {
        var engine = new Game2048Engine();
        var grid = new List<int>(16)
        {
            1024, 1024, 0, 0,
            0, 0, 0, 0,
            0, 0, 0, 0,
            0, 0, 0, 0
        };

        engine.LoadState(grid, currentScore: 0, bestScore: 0);
        bool moved = engine.Move(MoveDirection.Left);

        Assert.True(moved);
        Assert.True(engine.HasWon);
        Assert.Equal(2048, engine.ExportGrid()[0]);
    }

    [Fact]
    public void ViewModel_PersistsAndRestoresState()
    {
        var tile = new TileModel
        {
            Id = "test_2048",
            TargetPath = "game_2048"
        };

        using (var vm = new Game2048WidgetViewModel(tile))
        {
            vm.Engine.LoadState(new List<int>
            {
                2, 4, 0, 0,
                0, 0, 0, 0,
                0, 0, 0, 0,
                0, 0, 0, 0
            }, currentScore: 24, bestScore: 1024);

            vm.SaveSettings();
        }

        Assert.NotNull(tile.SettingsJson);

        using (var restoredVm = new Game2048WidgetViewModel(tile))
        {
            Assert.Equal(24, restoredVm.Score);
            Assert.Equal(1024, restoredVm.BestScore);
            var grid = restoredVm.Engine.ExportGrid();
            Assert.Equal(2, grid[0]);
            Assert.Equal(4, grid[1]);
        }
    }

    [Fact]
    public void WidgetRegistry_HasRegisteredGame2048()
    {
        var def = WidgetRegistry.Get("game_2048");
        Assert.NotNull(def);
        Assert.Equal("2048", def.DisplayName);
        Assert.Equal(typeof(Game2048WidgetViewModel), def.ViewModelType);
        Assert.Equal(typeof(Game2048WidgetView), def.ViewType);
        Assert.Equal(WidgetSize.Canvas, def.DefaultSize);
        Assert.Single(def.AllowedSizes);
        Assert.Equal(WidgetSize.Canvas, def.AllowedSizes[0]);
    }

    [Fact]
    public void View_InstantiatesAndRendersInSta()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = new TileModel { Id = "test_view_2048", TargetPath = "game_2048" };
            using var vm = new Game2048WidgetViewModel(tile);
            var view = new Game2048WidgetView { DataContext = vm };

            Assert.NotNull(view);
            Assert.True(view.Focusable);

            vm.RestartGame();
        });
    }
}
