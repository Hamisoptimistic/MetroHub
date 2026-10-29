using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Presentation.Controllers;
using Xunit;

namespace MetroHub.Tests;

[Collection("StorageTests")]
public sealed class WorkspaceRecoveryAndPersistenceTests : IDisposable
{
    private readonly string _sandboxDir;

    public WorkspaceRecoveryAndPersistenceTests()
    {
        StorageService.ResetPending();
        _sandboxDir = Path.Combine(Path.GetTempPath(), "MetroHub_RecoveryTest_" + Guid.NewGuid().ToString("N"));
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
    public void LoadWorkspaces_WhenManifestMissing_RecoversExistingDirectoriesFromDisk()
    {
        // Arrange: Create multiple workspace folders on disk with valid layout files
        string dirA = AppPaths.GetWorkspaceDir("ws_alpha");
        string dirB = AppPaths.GetWorkspaceDir("ws_beta");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        var tilesA = new ObservableCollection<TileModel> { new() { Id = "tile_a1", Title = "Alpha Tile" } };
        var tilesB = new ObservableCollection<TileModel> { new() { Id = "tile_b1", Title = "Beta Tile" } };
        StorageService.SaveWorkspaceLayoutSync("ws_alpha", tilesA);
        StorageService.SaveWorkspaceLayoutSync("ws_beta", tilesB);

        // Ensure manifest file does not exist
        if (File.Exists(AppPaths.WorkspacesManifestPath))
        {
            File.Delete(AppPaths.WorkspacesManifestPath);
        }
        if (File.Exists(AppPaths.WorkspacesManifestBakPath))
        {
            File.Delete(AppPaths.WorkspacesManifestBakPath);
        }

        // Act
        var manifest = StorageService.LoadWorkspaces();

        // Assert: Manifest is restored and contains both existing workspaces
        Assert.NotNull(manifest);
        Assert.Contains(manifest.Workspaces, w => w.Id == "ws_alpha");
        Assert.Contains(manifest.Workspaces, w => w.Id == "ws_beta");

        var wsA = manifest.Workspaces.First(w => w.Id == "ws_alpha");
        Assert.Single(wsA.Tiles);
        Assert.Equal("tile_a1", wsA.Tiles[0].Id);

        var wsB = manifest.Workspaces.First(w => w.Id == "ws_beta");
        Assert.Single(wsB.Tiles);
        Assert.Equal("tile_b1", wsB.Tiles[0].Id);

        // Assert: Manifest file is now written to disk
        Assert.True(File.Exists(AppPaths.WorkspacesManifestPath));
    }

    [Fact]
    public void LoadWorkspaces_WhenManifestCorrupted_RecoversExistingDirectoriesWithoutCrashing()
    {
        // Arrange: Create existing workspace with tiles
        string dir = AppPaths.GetWorkspaceDir("ws_recover_corrupt");
        Directory.CreateDirectory(dir);
        var tiles = new ObservableCollection<TileModel> { new() { Id = "tile_rec_1", Title = "Recovered Tile" } };
        StorageService.SaveWorkspaceLayoutSync("ws_recover_corrupt", tiles);

        // Write malformed JSON to workspaces.json
        AppPaths.EnsureDirectory(AppPaths.WorkspacesManifestPath);
        File.WriteAllText(AppPaths.WorkspacesManifestPath, "{ \"Workspaces\": [ INVALID_JSON_TRUNCATED ");

        if (File.Exists(AppPaths.WorkspacesManifestBakPath))
        {
            File.Delete(AppPaths.WorkspacesManifestBakPath);
        }

        // Act
        var manifest = StorageService.LoadWorkspaces();

        // Assert
        Assert.NotNull(manifest);
        Assert.Contains(manifest.Workspaces, w => w.Id == "ws_recover_corrupt");
        var ws = manifest.Workspaces.First(w => w.Id == "ws_recover_corrupt");
        Assert.Single(ws.Tiles);
        Assert.Equal("tile_rec_1", ws.Tiles[0].Id);

        // Corrupt file was preserved
        string dirPath = Path.GetDirectoryName(AppPaths.WorkspacesManifestPath) ?? _sandboxDir;
        var corruptFiles = Directory.GetFiles(dirPath, "workspaces_corrupt_*.json");
        Assert.NotEmpty(corruptFiles);
    }

    [Fact]
    public void CleanUnmanifestedWorkspaces_NeverMovesValidWorkspaceDirectoriesToTrash()
    {
        // Arrange: Seed an unmanifested directory with valid layout.json
        string unmanifestedDir = AppPaths.GetWorkspaceDir("ws_valid_unmanifested");
        Directory.CreateDirectory(unmanifestedDir);
        var tiles = new ObservableCollection<TileModel> { new() { Id = "tile_keep", Title = "Must Keep" } };
        StorageService.SaveWorkspaceLayoutSync("ws_valid_unmanifested", tiles);

        // Initialize WorkspaceManager with a manifest that only knows "default"
        var manifest = new WorkspacesManifest
        {
            ActiveWorkspaceId = "default",
            Workspaces = new List<WorkspaceModel>
            {
                new() { Id = "default", Name = "Main", Order = 0, IconSymbol = "Desktop24" }
            }
        };
        StorageService.SaveWorkspacesSync(manifest);

        // Act: Initialize triggers cleanup
        WorkspaceManager.Instance.Initialize();

        // Assert: The unmanifested folder with valid layout is NOT moved to trash
        Assert.True(Directory.Exists(unmanifestedDir), "Valid directory must NOT be deleted or moved to trash.");
        if (Directory.Exists(AppPaths.WorkspacesTrashDir))
        {
            var trashed = Directory.GetDirectories(AppPaths.WorkspacesTrashDir, "ws_valid_unmanifested*");
            Assert.Empty(trashed);
        }
    }

    [Fact]
    public void CleanUnmanifestedWorkspaces_RemovesEmptyZombieDirectories()
    {
        // Arrange: Create empty zombie directory
        string zombieDir = AppPaths.GetWorkspaceDir("ws_zombie_empty");
        Directory.CreateDirectory(zombieDir);

        var manifest = new WorkspacesManifest
        {
            ActiveWorkspaceId = "default",
            Workspaces = new List<WorkspaceModel>
            {
                new() { Id = "default", Name = "Main", Order = 0, IconSymbol = "Desktop24" }
            }
        };
        StorageService.SaveWorkspacesSync(manifest);

        // Act
        WorkspaceManager.Instance.Initialize();

        // Assert: Zombie directory is deleted
        Assert.False(Directory.Exists(zombieDir), "Empty zombie directory must be cleaned up.");
    }

    [Fact]
    public void UpdateWorkspace_SynchronouslyAndAtomicallyPersistsNameAndIconToDisk()
    {
        // Arrange
        WorkspaceManager.Instance.Initialize();
        var active = WorkspaceManager.Instance.ActiveWorkspace;
        Assert.NotNull(active);
        string wsId = active.Id;

        // Act: Update workspace name and icon
        bool updated = WorkspaceManager.Instance.UpdateWorkspace(wsId, "Operations Center", "Globe24");
        Assert.True(updated);

        // Assert: Read directly from disk synchronously (no flush call, no delay)
        Assert.True(File.Exists(AppPaths.WorkspacesManifestPath));
        string onDiskJson = File.ReadAllText(AppPaths.WorkspacesManifestPath);

        Assert.Contains("\"Name\": \"Operations Center\"", onDiskJson);
        Assert.Contains("\"IconSymbol\": \"Globe24\"", onDiskJson);

        // Verify UpdateWorkspaceIcon also commits synchronously
        bool iconUpdated = WorkspaceManager.Instance.UpdateWorkspaceIcon(wsId, "TargetArrow24");
        Assert.True(iconUpdated);

        string updatedDiskJson = File.ReadAllText(AppPaths.WorkspacesManifestPath);
        Assert.Contains("\"IconSymbol\": \"TargetArrow24\"", updatedDiskJson);

        // Verify deserialization matches
        var reloadedManifest = StorageService.LoadWorkspaces();
        var reloadedWs = reloadedManifest.Workspaces.First(w => w.Id == wsId);
        Assert.Equal("Operations Center", reloadedWs.Name);
        Assert.Equal("TargetArrow24", reloadedWs.IconSymbol);
    }

    [Fact]
    public void WorkspaceModel_PropertyChange_SynchronouslyPersistsToDiskManifest()
    {
        // Arrange
        WorkspaceManager.Instance.Initialize();
        var active = WorkspaceManager.Instance.ActiveWorkspace;
        Assert.NotNull(active);

        // Act: Directly change properties on the model
        active.Name = "Direct Renamed Canvas";
        active.IconSymbol = "Flame24";

        // Read directly from disk synchronously
        Assert.True(File.Exists(AppPaths.WorkspacesManifestPath));
        string onDiskJson = File.ReadAllText(AppPaths.WorkspacesManifestPath);
        Assert.Contains("\"Name\": \"Direct Renamed Canvas\"", onDiskJson);
        Assert.Contains("\"IconSymbol\": \"Flame24\"", onDiskJson);
    }

    [Fact]
    public void SwitchWorkspace_SynchronouslyPersistsActiveWorkspaceIdToDiskWithoutDelay()
    {
        // Arrange
        WorkspaceManager.Instance.Initialize();
        var ws1 = WorkspaceManager.Instance.ActiveWorkspace;
        Assert.NotNull(ws1);

        var ws2 = WorkspaceManager.Instance.CreateWorkspace("Secondary Canvas", "Star24");
        Assert.NotNull(ws2);

        // Switch back to ws1
        WorkspaceManager.Instance.SwitchWorkspace(ws1.Id);
        Assert.Equal(ws1.Id, WorkspaceManager.Instance.ActiveWorkspace.Id);

        string diskJsonWs1 = File.ReadAllText(AppPaths.WorkspacesManifestPath);
        Assert.Contains($"\"ActiveWorkspaceId\": \"{ws1.Id}\"", diskJsonWs1);

        // Act: Switch to ws2
        WorkspaceManager.Instance.SwitchWorkspace(ws2.Id);

        // Assert: Disk reflects active ws2 synchronously with zero delay
        Assert.True(File.Exists(AppPaths.WorkspacesManifestPath));
        string diskJsonWs2 = File.ReadAllText(AppPaths.WorkspacesManifestPath);
        Assert.Contains($"\"ActiveWorkspaceId\": \"{ws2.Id}\"", diskJsonWs2);

        // Reload fresh from disk
        var reloaded = StorageService.LoadWorkspaces();
        Assert.Equal(ws2.Id, reloaded.ActiveWorkspaceId);
    }

    [Fact]
    public void WorkspaceModel_OrderPropertyChange_SynchronouslyPersistsToDiskManifest()
    {
        // Arrange
        WorkspaceManager.Instance.Initialize();
        var active = WorkspaceManager.Instance.ActiveWorkspace;
        Assert.NotNull(active);

        // Act: Directly change Order property
        active.Order = 42;

        // Assert: Read directly from disk synchronously
        Assert.True(File.Exists(AppPaths.WorkspacesManifestPath));
        string onDiskJson = File.ReadAllText(AppPaths.WorkspacesManifestPath);
        Assert.Contains("\"Order\": 42", onDiskJson);

        var reloaded = StorageService.LoadWorkspaces();
        var reloadedWs = reloaded.Workspaces.First(w => w.Id == active.Id);
        Assert.Equal(42, reloadedWs.Order);
    }

    [Fact]
    public void UpdateWorkspace_EdgeCases_InvalidInputsDoNotCorruptDiskManifest()
    {
        // Arrange
        WorkspaceManager.Instance.Initialize();
        var active = WorkspaceManager.Instance.ActiveWorkspace;
        Assert.NotNull(active);

        string initialJson = File.ReadAllText(AppPaths.WorkspacesManifestPath);

        // Act: Invalid arguments
        bool invalidName = WorkspaceManager.Instance.UpdateWorkspace(active.Id, "   ");
        bool nonExistentWs = WorkspaceManager.Instance.UpdateWorkspace("non_existent_id", "Valid Name");
        bool invalidIcon = WorkspaceManager.Instance.UpdateWorkspaceIcon(active.Id, "   ");

        // Assert
        Assert.False(invalidName);
        Assert.False(nonExistentWs);
        Assert.False(invalidIcon);

        string currentJson = File.ReadAllText(AppPaths.WorkspacesManifestPath);
        Assert.Equal(initialJson, currentJson);
    }

    [Fact]
    public void StressTest_RapidConsecutivePropertyChanges_MaintainsDiskIntegrityAndConsistency()
    {
        // Arrange
        WorkspaceManager.Instance.Initialize();
        var active = WorkspaceManager.Instance.ActiveWorkspace;
        Assert.NotNull(active);

        // Act & Assert: Rapidly mutate 50 times and assert immediate disk state on each cycle
        for (int i = 0; i < 50; i++)
        {
            string expectedName = $"Stress Canvas {i}";
            string expectedIcon = (i % 2 == 0) ? "Flame24" : "Globe24";

            active.Name = expectedName;
            active.IconSymbol = expectedIcon;

            string diskJson = File.ReadAllText(AppPaths.WorkspacesManifestPath);
            Assert.Contains($"\"Name\": \"{expectedName}\"", diskJson);
            Assert.Contains($"\"IconSymbol\": \"{expectedIcon}\"", diskJson);
        }

        // Final verification: Reload fresh from disk
        var finalManifest = StorageService.LoadWorkspaces();
        var finalActive = finalManifest.Workspaces.First(w => w.Id == active.Id);
        Assert.Equal("Stress Canvas 49", finalActive.Name);
        Assert.Equal("Globe24", finalActive.IconSymbol);
    }
}

