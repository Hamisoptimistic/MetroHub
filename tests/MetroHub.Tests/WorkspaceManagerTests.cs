using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Presentation.Controllers;
using MetroHub.Widgets.Catalog.Dino;
using Xunit;

namespace MetroHub.Tests;

[Collection("StorageTests")]
public sealed class WorkspaceManagerTests : IDisposable
{
    private readonly string _sandboxDir;

    public WorkspaceManagerTests()
    {
        StorageService.ResetPending();
        _sandboxDir = Path.Combine(Path.GetTempPath(), "MetroHub_WMTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandboxDir);
        AppPaths.CustomAppDataDir = _sandboxDir;
        WorkspaceManager.Instance.ResetForTesting();
    }

    public void Dispose()
    {
        try
        {
            StorageService.Flush();
            StorageService.ResetPending();
            WorkspaceManager.Instance.ResetForTesting();
        }
        catch { }
        finally
        {
            AppPaths.CustomAppDataDir = null;
            try
            {
                if (Directory.Exists(_sandboxDir))
                {
                    Directory.Delete(_sandboxDir, recursive: true);
                }
            }
            catch { }
        }
    }

    [Fact]
    public void Initialize_PopulatesActiveWorkspace_AndInstantiatesWidgetViewModelsEagerly()
    {
        int factoryInvocationCount = 0;
        TileModel.WidgetViewModelFactory = _ =>
        {
            factoryInvocationCount++;
            return new object();
        };

        try
        {
            // Seed a workspace with a widget tile
            var manifest = new WorkspacesManifest
            {
                ActiveWorkspaceId = "ws_test_init",
                Workspaces = new System.Collections.Generic.List<WorkspaceModel>
                {
                    new()
                    {
                        Id = "ws_test_init",
                        Name = "Init Workspace",
                        Order = 0,
                        IconSymbol = "Desktop24"
                    }
                }
            };
            StorageService.SaveWorkspacesSync(manifest);

            var tiles = new ObservableCollection<TileModel>
            {
                new()
                {
                    Id = "eager_widget_tile_1",
                    TileType = TileType.Widget
                }
            };
            StorageService.SaveWorkspaceLayoutSync("ws_test_init", tiles);

            // Act
            WorkspaceManager.Instance.Initialize();

            // Assert
            Assert.Single(WorkspaceManager.Instance.Workspaces);
            var active = WorkspaceManager.Instance.ActiveWorkspace;
            Assert.NotNull(active);
            Assert.Equal("ws_test_init", active.Id);
            Assert.True(active.IsActive);
            Assert.Equal(1, factoryInvocationCount);
        }
        finally
        {
            TileModel.WidgetViewModelFactory = null;
        }
    }

    [Fact]
    public void GetAllTileIdsAcrossAllWorkspaces_AggregatesTilesAcrossAllWorkspaces()
    {
        WorkspaceManager.Instance.Initialize();

        // Workspace 1 (active)
        WorkspaceManager.Instance.ActiveWorkspace.Tiles.Add(new TileModel { Id = "tile_ws_1_a" });
        WorkspaceManager.Instance.ActiveWorkspace.Tiles.Add(new TileModel { Id = "tile_ws_1_b" });

        // Workspace 2
        var ws2 = WorkspaceManager.Instance.CreateWorkspace("Second Workspace");
        Assert.NotNull(ws2);
        ws2.Tiles.Add(new TileModel { Id = "tile_ws_2_a" });

        // Act
        var allIds = WorkspaceManager.Instance.GetAllTileIdsAcrossAllWorkspaces();

        // Assert
        Assert.Contains("tile_ws_1_a", allIds);
        Assert.Contains("tile_ws_1_b", allIds);
        Assert.Contains("tile_ws_2_a", allIds);
        Assert.True(allIds.Count >= 3);
    }

    [Fact]
    public void CreateWorkspace_EnforcesSoftCap_OfTen()
    {
        WorkspaceManager.Instance.Initialize();

        // Workspaces starts with 1
        Assert.Equal(1, WorkspaceManager.Instance.Workspaces.Count);

        // Add up to 10
        for (int i = 2; i <= 10; i++)
        {
            var ws = WorkspaceManager.Instance.CreateWorkspace($"WS {i}");
            Assert.NotNull(ws);
        }

        Assert.Equal(10, WorkspaceManager.Instance.Workspaces.Count);
        Assert.False(WorkspaceManager.Instance.CanCreateWorkspace);

        // 11th must fail (soft cap)
        var blocked = WorkspaceManager.Instance.CreateWorkspace("Eleventh");
        Assert.Null(blocked);
        Assert.Equal(10, WorkspaceManager.Instance.Workspaces.Count);
    }

    [Fact]
    public void DeleteWorkspace_GuardsSingleWorkspace_AndSwitchesActiveBeforeDelete()
    {
        WorkspaceManager.Instance.Initialize();

        // Cannot delete only workspace
        string firstId = WorkspaceManager.Instance.ActiveWorkspace.Id;
        bool deletedOnly = WorkspaceManager.Instance.DeleteWorkspace(firstId);
        Assert.False(deletedOnly);
        Assert.Single(WorkspaceManager.Instance.Workspaces);

        // Add second workspace
        var ws2 = WorkspaceManager.Instance.CreateWorkspace("WS 2");
        Assert.NotNull(ws2);
        Assert.Equal(2, WorkspaceManager.Instance.Workspaces.Count);
        Assert.Equal(ws2.Id, WorkspaceManager.Instance.ActiveWorkspace.Id);

        // Delete active workspace: should switch back to first before deleting
        bool deleted = WorkspaceManager.Instance.DeleteWorkspace(ws2.Id);
        Assert.True(deleted);
        Assert.Single(WorkspaceManager.Instance.Workspaces);
        Assert.Equal(firstId, WorkspaceManager.Instance.ActiveWorkspace.Id);
    }

    [Fact]
    public void RenameWorkspace_UpdatesNameAndPersists()
    {
        WorkspaceManager.Instance.Initialize();
        string wsId = WorkspaceManager.Instance.ActiveWorkspace.Id;

        bool renamed = WorkspaceManager.Instance.RenameWorkspace(wsId, "Renamed Work");
        Assert.True(renamed);
        Assert.Equal("Renamed Work", WorkspaceManager.Instance.ActiveWorkspace.Name);

        // Verify persisted manifest
        var manifest = StorageService.LoadWorkspaces();
        Assert.Equal("Renamed Work", manifest.Workspaces.First(w => w.Id == wsId).Name);
    }

    [Fact]
    public void MoveTileToWorkspace_CleansesGroupAndTransfersTile()
    {
        WorkspaceManager.Instance.Initialize();
        var ws1 = WorkspaceManager.Instance.ActiveWorkspace;
        var ws2 = WorkspaceManager.Instance.CreateWorkspace("Target WS");
        Assert.NotNull(ws2);

        // Switch back to ws1
        WorkspaceManager.Instance.SwitchWorkspace(ws1.Id);
        Assert.Equal(ws1.Id, WorkspaceManager.Instance.ActiveWorkspace.Id);

        var tile = new TileModel
        {
            Id = "transfer_tile_1",
            Title = "Transfer Tile",
            Group = "SourceGroup",
            Col = 4,
            Row = 2
        };
        ws1.Tiles.Add(tile);

        // Act
        WorkspaceManager.Instance.MoveTileToWorkspace(tile, ws2.Id);

        // Assert
        Assert.DoesNotContain(tile, ws1.Tiles);
        Assert.Contains(tile, ws2.Tiles);
        Assert.Null(tile.Group);
        Assert.Equal(0, tile.Col);
        Assert.Equal(1, tile.Row);
        Assert.True(ws1.IsDirty);
        Assert.True(ws2.IsDirty);
    }

    [Fact]
    public void StartupPruning_DoesNotPruneInactiveWorkspaceWidgets()
    {
        WorkspaceManager.Instance.Initialize();
        var ws1 = WorkspaceManager.Instance.ActiveWorkspace;
        ws1.Tiles.Add(new TileModel
        {
            Id = "tile_active_1",
            TileType = TileType.Widget
        });

        var ws2 = WorkspaceManager.Instance.CreateWorkspace("Inactive WS");
        Assert.NotNull(ws2);
        ws2.Tiles.Add(new TileModel
        {
            Id = "tile_inactive_2",
            TileType = TileType.Widget
        });

        // Switch back to ws1 so ws2 is inactive
        WorkspaceManager.Instance.SwitchWorkspace(ws1.Id);
        Assert.Equal(ws1.Id, WorkspaceManager.Instance.ActiveWorkspace.Id);
        Assert.False(ws2.IsActive);

        // Seed widget state files in the store
        WidgetStateStore.Default.Write("clock", "tile_active_1", "{\"mode\": 1}");
        WidgetStateStore.Default.Write("clock", "tile_inactive_2", "{\"mode\": 2}");
        WidgetStateStore.Default.Write("clock", "tile_dead_3", "{\"mode\": 3}"); // orphaned tile

        // Act: Run the startup pruning logic from MainWindow.xaml.cs:504
        var allIds = WorkspaceManager.Instance.GetAllTileIdsAcrossAllWorkspaces();
        WidgetStateStore.Default.PruneAllExcept(allIds);

        // Assert:
        // Active workspace widget state is intact
        Assert.NotNull(WidgetStateStore.Default.Read("clock", "tile_active_1"));
        // INACTIVE workspace widget state MUST be intact (safeguard verification!)
        Assert.NotNull(WidgetStateStore.Default.Read("clock", "tile_inactive_2"));
        // Truly orphaned tile is pruned
        Assert.Null(WidgetStateStore.Default.Read("clock", "tile_dead_3"));
    }

    [Fact]
    public void LayoutHistoryService_SwitchWorkspace_IsolatesUndoRedoStacksAcrossWorkspaces()
    {
        var history = new LayoutHistoryService();
        history.PushState("snapshot_ws1_v1");
        history.PushState("snapshot_ws1_v2");
        Assert.True(history.CanUndo);

        // Switch to a new workspace "ws_dev"
        history.SwitchWorkspace("ws_dev");
        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);

        // Perform mutations on ws_dev
        history.PushState("snapshot_ws2_v1");
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);

        // Switch back to "default"
        history.SwitchWorkspace("default");
        Assert.True(history.CanUndo);
        var undone = history.Undo("snapshot_ws1_v2_dirty");
        Assert.Equal("snapshot_ws1_v2", undone);

        // Switch back to "ws_dev" and verify its stack is preserved
        history.SwitchWorkspace("ws_dev");
        Assert.True(history.CanUndo);
        var undoneDev = history.Undo("snapshot_ws2_v1_dirty");
        Assert.Equal("snapshot_ws2_v1", undoneDev);
    }

    [Fact]
    public void DinoWidgetViewModel_OnDormant_PausesRunningGameEngine()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = new TileModel
            {
                Id = "test_dino_dormant",
                TileType = TileType.Widget,
                TargetPath = "dino",
                SpanX = 8,
                SpanY = 3
            };

            using var vm = new DinoWidgetViewModel(tile);
            vm.HandleJumpInput();
            Assert.Equal(DinoGameState.Running, vm.Engine.State);

            // Act: trigger dormancy (workspace switch)
            vm.OnDormant();

            // Assert: Game engine paused to maintain 0.0% CPU overhead
            Assert.Equal(DinoGameState.Paused, vm.Engine.State);
        });
    }

    [Fact]
    public void MoveTileToWorkspace_WithExistingTilesAtTarget_StacksCleanlyBelowWithoutCollision()
    {
        WorkspaceManager.Instance.Initialize();
        var ws1 = WorkspaceManager.Instance.ActiveWorkspace;
        var ws2 = WorkspaceManager.Instance.CreateWorkspace("Target WS");
        Assert.NotNull(ws2);

        // Pre-populate Target WS with an existing tile at Col 0, Row 1, SpanY 2 (occupies rows 1..2)
        ws2.Tiles.Add(new TileModel
        {
            Id = "target_existing_tile",
            Col = 0,
            Row = 1,
            SpanX = 2,
            SpanY = 2
        });

        // Switch back to ws1
        WorkspaceManager.Instance.SwitchWorkspace(ws1.Id);

        var tileA = new TileModel { Id = "move_tile_a", SpanX = 2, SpanY = 2 };
        var tileB = new TileModel { Id = "move_tile_b", SpanX = 2, SpanY = 2 };
        ws1.Tiles.Add(tileA);
        ws1.Tiles.Add(tileB);

        // Act: Move both tiles sequentially to ws2
        WorkspaceManager.Instance.MoveTileToWorkspace(tileA, ws2.Id);
        WorkspaceManager.Instance.MoveTileToWorkspace(tileB, ws2.Id);

        // Assert: tileA starts at Row 3 (1 + 2), tileB starts at Row 5 (3 + 2)
        Assert.Equal(3, tileA.Row);
        Assert.Equal(5, tileB.Row);
        Assert.Equal(3, ws2.Tiles.Count);
        Assert.DoesNotContain(tileA, ws1.Tiles);
        Assert.DoesNotContain(tileB, ws1.Tiles);
    }

    [Fact]
    public void DeleteWorkspace_MovesWorkspaceFolderToTrashArchive()
    {
        WorkspaceManager.Instance.Initialize();
        var ws2 = WorkspaceManager.Instance.CreateWorkspace("Trash WS");
        Assert.NotNull(ws2);
        string ws2Dir = AppPaths.GetWorkspaceDir(ws2.Id);
        Assert.True(Directory.Exists(ws2Dir));

        // Act: Delete workspace
        bool deleted = WorkspaceManager.Instance.DeleteWorkspace(ws2.Id);
        Assert.True(deleted);

        // Source dir should no longer exist
        Assert.False(Directory.Exists(ws2Dir));

        // Trash archive should contain the moved directory
        Assert.True(Directory.Exists(AppPaths.WorkspacesTrashDir));
        var archivedDirs = Directory.GetDirectories(AppPaths.WorkspacesTrashDir, $"{ws2.Id}_*");
        Assert.Single(archivedDirs);
    }

    [Fact]
    public void RequestDeleteWorkspaceCommand_BehaviorsWithSingleAndMultipleWorkspaces()
    {
        WorkspaceManager.Instance.Initialize();

        // Case 1: Single workspace -> RequestDelete is NOT fired, workspace is NOT deleted
        bool deleteEventFired = false;
        WorkspaceManager.Instance.RequestDelete += _ => deleteEventFired = true;

        WorkspaceManager.Instance.RequestDeleteWorkspaceCommand.Execute(WorkspaceManager.Instance.ActiveWorkspace.Id);
        Assert.False(deleteEventFired);
        Assert.Single(WorkspaceManager.Instance.Workspaces);

        // Case 2: Add second workspace -> RequestDelete IS fired
        var ws2 = WorkspaceManager.Instance.CreateWorkspace("Second WS");
        Assert.NotNull(ws2);

        WorkspaceModel? receivedForDeletion = null;
        WorkspaceManager.Instance.RequestDelete += ws => receivedForDeletion = ws;

        WorkspaceManager.Instance.RequestDeleteWorkspaceCommand.Execute(ws2.Id);
        Assert.NotNull(receivedForDeletion);
        Assert.Equal(ws2.Id, receivedForDeletion.Id);
        // Notice: with event subscriber, it did not silently delete without confirmation!
        Assert.Equal(2, WorkspaceManager.Instance.Workspaces.Count);
    }
}
