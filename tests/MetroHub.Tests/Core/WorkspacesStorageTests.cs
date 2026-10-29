using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using Xunit;

namespace MetroHub.Tests;

[Collection("StorageTests")]
public sealed class WorkspacesStorageTests : IDisposable
{
    private readonly string _sandboxDir;

    public WorkspacesStorageTests()
    {
        StorageService.ResetPending();
        _sandboxDir = Path.Combine(Path.GetTempPath(), "MetroHub_WorkspacesTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandboxDir);
        AppPaths.CustomAppDataDir = _sandboxDir;
    }

    public void Dispose()
    {
        try
        {
            StorageService.Flush();
            StorageService.ResetPending();
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
    public void LoadWorkspaces_MigratesLegacyLayoutAndGroups_NonDestructively()
    {
        // 1. Arrange: Create legacy layout.json and groups.json in legacy location
        string legacyLayoutPath = AppPaths.LayoutPath;
        string legacyGroupsPath = AppPaths.GroupsPath;

        var legacyTiles = new ObservableCollection<TileModel>
        {
            new()
            {
                Id = "legacy_tile_1",
                Title = "Legacy Tile 1",
                TileType = TileType.App,
                TargetPath = "C:\\Windows\\notepad.exe",
                Col = 0,
                Row = 1,
                X = 0,
                Y = 48
            }
        };

        var legacyGroups = new ObservableCollection<TileGroupModel>
        {
            new()
            {
                Id = "legacy_group_1",
                Title = "Legacy Group 1",
                Col = 0,
                Row = 0,
                X = 0,
                Y = 48
            }
        };

        StorageService.SaveLayoutSync(legacyTiles);
        StorageService.SaveGroupsSync(legacyGroups);

        Assert.True(File.Exists(legacyLayoutPath), "Legacy layout.json must exist before migration.");
        Assert.True(File.Exists(legacyGroupsPath), "Legacy groups.json must exist before migration.");

        // 2. Act: Call LoadWorkspaces
        var manifest = StorageService.LoadWorkspaces();

        // 3. Assert:
        // A. Manifest is valid
        Assert.NotNull(manifest);
        Assert.Single(manifest.Workspaces);
        var defaultWs = manifest.Workspaces[0];
        Assert.Equal("default", defaultWs.Id);
        Assert.True(defaultWs.IsActive);

        // B. Tiles and Groups were migrated to default workspace
        Assert.Single(defaultWs.Tiles);
        Assert.Equal("legacy_tile_1", defaultWs.Tiles[0].Id);
        Assert.Single(defaultWs.Groups);
        Assert.Equal("legacy_group_1", defaultWs.Groups[0].Id);

        // C. Migrated files exist in workspaces\default
        string migratedLayout = AppPaths.GetWorkspaceLayoutPath("default");
        string migratedGroups = AppPaths.GetWorkspaceGroupsPath("default");
        Assert.True(File.Exists(migratedLayout), "Migrated layout.json must exist in workspaces\\default.");
        Assert.True(File.Exists(migratedGroups), "Migrated groups.json must exist in workspaces\\default.");

        // D. NON-DESTRUCTIVE: Legacy root files must STILL exist intact
        Assert.True(File.Exists(legacyLayoutPath), "Legacy layout.json must NOT be deleted or moved (downgrade safety).");
        Assert.True(File.Exists(legacyGroupsPath), "Legacy groups.json must NOT be deleted or moved (downgrade safety).");
    }

    [Fact]
    public void WorkspaceModel_DisplayGlyph_ReflectsOrder()
    {
        var ws = new WorkspaceModel { Order = 0 };
        Assert.Equal("1", ws.DisplayGlyph);

        ws.Order = 2;
        Assert.Equal("3", ws.DisplayGlyph);
    }

    [Fact]
    public void SaveWorkspaceLayoutSync_And_LoadWorkspaceLayout_RoundTrips()
    {
        string wsId = "ws_test_roundtrip";
        var tiles = new ObservableCollection<TileModel>
        {
            new()
            {
                Id = "tile_roundtrip_1",
                Title = "Roundtrip Tile",
                Col = 2,
                Row = 1
            }
        };

        StorageService.SaveWorkspaceLayoutSync(wsId, tiles);
        var loaded = StorageService.LoadWorkspaceLayout(wsId);

        Assert.NotNull(loaded);
        Assert.Single(loaded);
        Assert.Equal("tile_roundtrip_1", loaded[0].Id);
    }

    [Fact]
    public void DeleteWorkspaceStorage_MovesToTrash_NonDestructively()
    {
        string wsId = "ws_to_delete";
        var tiles = new ObservableCollection<TileModel>
        {
            new() { Id = "tile_in_del_ws", Title = "Will be moved to trash" }
        };

        StorageService.SaveWorkspaceLayoutSync(wsId, tiles);
        string wsDir = AppPaths.GetWorkspaceDir(wsId);
        Assert.True(Directory.Exists(wsDir));

        StorageService.DeleteWorkspaceStorage(wsId);

        // Source dir no longer exists at original path
        Assert.False(Directory.Exists(wsDir));

        // Trash dir contains the preserved folder
        string trashDir = AppPaths.WorkspacesTrashDir;
        Assert.True(Directory.Exists(trashDir));
        var trashedDirs = Directory.GetDirectories(trashDir, $"{wsId}_*");
        Assert.Single(trashedDirs);
        Assert.True(File.Exists(Path.Combine(trashedDirs[0], "layout.json")));
    }

    [Fact]
    public void TileModel_EnsureViewModelCreated_InvokesFactoryOnce()
    {
        int factoryInvocationCount = 0;
        TileModel.WidgetViewModelFactory = tile =>
        {
            factoryInvocationCount++;
            return new object();
        };

        try
        {
            var widgetTile = new TileModel
            {
                Id = "widget_tile_test",
                TileType = TileType.Widget
            };

            Assert.Equal(0, factoryInvocationCount);

            widgetTile.EnsureViewModelCreated();
            Assert.Equal(1, factoryInvocationCount);

            // Second call should not re-invoke
            widgetTile.EnsureViewModelCreated();
            Assert.Equal(1, factoryInvocationCount);
        }
        finally
        {
            TileModel.WidgetViewModelFactory = null;
        }
    }
}
