using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Presentation.Controllers;
using Xunit;

namespace MetroHub.Tests;

[Collection("StorageTests")]
public sealed class WorkspaceAdversarialVerificationTests : IDisposable
{
    private readonly string _sandboxDir;

    public WorkspaceAdversarialVerificationTests()
    {
        StorageService.ResetPending();
        _sandboxDir = Path.Combine(Path.GetTempPath(), "MetroHub_AdvTest_" + Guid.NewGuid().ToString("N"));
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

    // ────────────────────────────────────────────────────────
    // Challenge R2: Defensive Recovery Stress Tests
    // ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")] // 0 bytes
    [InlineData("   \r\n\t ")] // Whitespace only
    [InlineData("{ \"Workspaces\": [")] // Truncated JSON
    [InlineData("NOT_EVEN_JSON_???!!!@@@")] // Complete garbage
    [InlineData("{\"ActiveWorkspaceId\": \"\", \"Workspaces\": []}")] // Empty list
    [InlineData("{ \"ActiveWorkspaceId\": \"missing_ws\", \"Workspaces\": null }")] // Null workspaces
    [InlineData("{\"Unexpected\": 1234}")] // Missing Workspaces property
    public void LoadWorkspaces_CorruptManifestVariations_PreservesAndRecoversAllDiskWorkspaces(string malformedContent)
    {
        // Arrange: 3 valid workspaces on disk
        string dir1 = AppPaths.GetWorkspaceDir("default");
        string dir2 = AppPaths.GetWorkspaceDir("ws_project_omega");
        string dir3 = AppPaths.GetWorkspaceDir("ws_analytics");

        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);
        Directory.CreateDirectory(dir3);

        StorageService.SaveWorkspaceLayoutSync("default", new ObservableCollection<TileModel>
        {
            new() { Id = "tile_def_1", Title = "Main Tile" }
        });
        StorageService.SaveWorkspaceLayoutSync("ws_project_omega", new ObservableCollection<TileModel>
        {
            new() { Id = "tile_omega_1", Title = "Omega Tile" }
        });
        StorageService.SaveWorkspaceGroupsSync("ws_analytics", new ObservableCollection<TileGroupModel>
        {
            new() { Id = "grp_analytics_1", Title = "Analytics Group" }
        });

        // Seed corrupt workspaces.json and delete .bak
        AppPaths.EnsureDirectory(AppPaths.WorkspacesManifestPath);
        File.WriteAllText(AppPaths.WorkspacesManifestPath, malformedContent);

        if (File.Exists(AppPaths.WorkspacesManifestBakPath))
        {
            File.Delete(AppPaths.WorkspacesManifestBakPath);
        }

        // Act
        var manifest = StorageService.LoadWorkspaces();

        // Assert: All 3 workspaces are preserved and recovered
        Assert.NotNull(manifest);
        Assert.Equal(3, manifest.Workspaces.Count);
        Assert.Contains(manifest.Workspaces, w => w.Id == "default");
        Assert.Contains(manifest.Workspaces, w => w.Id == "ws_project_omega");
        Assert.Contains(manifest.Workspaces, w => w.Id == "ws_analytics");

        // Verify tile and group reconstruction
        var wsDef = manifest.Workspaces.First(w => w.Id == "default");
        Assert.Single(wsDef.Tiles);
        Assert.Equal("tile_def_1", wsDef.Tiles[0].Id);

        var wsOmega = manifest.Workspaces.First(w => w.Id == "ws_project_omega");
        Assert.Single(wsOmega.Tiles);
        Assert.Equal("tile_omega_1", wsOmega.Tiles[0].Id);

        var wsAnalytics = manifest.Workspaces.First(w => w.Id == "ws_analytics");
        Assert.Single(wsAnalytics.Groups);
        Assert.Equal("grp_analytics_1", wsAnalytics.Groups[0].Id);

        // Verify ActiveWorkspaceId defaults to "default"
        Assert.Equal("default", manifest.ActiveWorkspaceId);

        // Verify manifest was written to disk
        Assert.True(File.Exists(AppPaths.WorkspacesManifestPath));
        string recoveredDiskJson = File.ReadAllText(AppPaths.WorkspacesManifestPath);
        Assert.Contains("default", recoveredDiskJson);
        Assert.Contains("ws_project_omega", recoveredDiskJson);
        Assert.Contains("ws_analytics", recoveredDiskJson);
    }

    [Fact]
    public void LoadWorkspaces_BinaryGarbageManifest_PreservesCorruptFileAndRecovers()
    {
        // Arrange
        string dir1 = AppPaths.GetWorkspaceDir("ws_binary_test");
        Directory.CreateDirectory(dir1);
        StorageService.SaveWorkspaceLayoutSync("ws_binary_test", new ObservableCollection<TileModel>
        {
            new() { Id = "tile_bin_1", Title = "Binary Recovery Tile" }
        });

        // Write non-text binary bytes to workspaces.json
        AppPaths.EnsureDirectory(AppPaths.WorkspacesManifestPath);
        byte[] binaryGarbage = new byte[] { 0x00, 0xFF, 0xFE, 0x12, 0x34, 0x00, 0x00, 0xDE, 0xAD, 0xBE, 0xEF };
        File.WriteAllBytes(AppPaths.WorkspacesManifestPath, binaryGarbage);

        if (File.Exists(AppPaths.WorkspacesManifestBakPath))
        {
            File.Delete(AppPaths.WorkspacesManifestBakPath);
        }

        // Act
        var manifest = StorageService.LoadWorkspaces();

        // Assert
        Assert.NotNull(manifest);
        Assert.Contains(manifest.Workspaces, w => w.Id == "ws_binary_test");

        // Diagnostic preservation verified
        string dirPath = Path.GetDirectoryName(AppPaths.WorkspacesManifestPath) ?? _sandboxDir;
        var corruptFiles = Directory.GetFiles(dirPath, "workspaces_corrupt_*.json");
        Assert.NotEmpty(corruptFiles);
    }

    [Fact]
    public void LoadWorkspaces_WorkspaceWithOnlyBackupFiles_RecoversSuccessfully()
    {
        // Arrange: Workspace has ONLY layout.json.bak (layout.json missing)
        string dir = AppPaths.GetWorkspaceDir("ws_backup_only");
        Directory.CreateDirectory(dir);

        string layoutBakPath = Path.Combine(dir, "layout.json.bak");
        File.WriteAllText(layoutBakPath, "[{\"Id\": \"tile_bak_1\", \"Title\": \"From Backup\"}]");

        if (File.Exists(AppPaths.WorkspacesManifestPath)) File.Delete(AppPaths.WorkspacesManifestPath);
        if (File.Exists(AppPaths.WorkspacesManifestBakPath)) File.Delete(AppPaths.WorkspacesManifestBakPath);

        // Act
        var manifest = StorageService.LoadWorkspaces();

        // Assert
        Assert.NotNull(manifest);
        Assert.Contains(manifest.Workspaces, w => w.Id == "ws_backup_only");
    }

    // ────────────────────────────────────────────────────────
    // Challenge R3: Non-destructive Cleanup Stress Tests
    // ────────────────────────────────────────────────────────

    private static readonly string[] SingleValidIdKept = ["ws_valid_kept"];
    private static readonly string[] SingleDefaultId = ["default"];
    private static readonly string[] SingleUppercaseId = ["WS_CASE_SENSITIVE"];

    [Fact]
    public void CleanUnmanifestedWorkspaces_OmittedValidWorkspaces_AreNeverMovedToTrashNorDeleted()
    {
        // Arrange: Create 3 valid workspaces on disk
        string dirA = AppPaths.GetWorkspaceDir("ws_omitted_1");
        string dirB = AppPaths.GetWorkspaceDir("ws_omitted_2");
        string dirC = AppPaths.GetWorkspaceDir("ws_valid_kept");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        Directory.CreateDirectory(dirC);

        // All 3 have valid layouts
        StorageService.SaveWorkspaceLayoutSync("ws_omitted_1", new ObservableCollection<TileModel> { new() { Id = "t1" } });
        StorageService.SaveWorkspaceLayoutSync("ws_omitted_2", new ObservableCollection<TileModel> { new() { Id = "t2" } });
        StorageService.SaveWorkspaceLayoutSync("ws_valid_kept", new ObservableCollection<TileModel> { new() { Id = "t3" } });

        // Add 2 zombies: one completely empty, one with a 0-byte layout.json
        string zombieEmpty = AppPaths.GetWorkspaceDir("ws_zombie_empty");
        string zombieZeroByte = AppPaths.GetWorkspaceDir("ws_zombie_zerobyte");
        Directory.CreateDirectory(zombieEmpty);
        Directory.CreateDirectory(zombieZeroByte);
        File.WriteAllText(Path.Combine(zombieZeroByte, "layout.json"), "");

        // Act: Run cleanup where validIds ONLY contains "ws_valid_kept"
        WorkspaceManager.CleanUnmanifestedWorkspaces(SingleValidIdKept, isRecoveryScenario: false);

        // Assert: Valid omitted directories MUST REMAIN INTACT in AppPaths.WorkspacesDir
        Assert.True(Directory.Exists(dirA), "ws_omitted_1 must NOT be deleted.");
        Assert.True(Directory.Exists(dirB), "ws_omitted_2 must NOT be deleted.");
        Assert.True(Directory.Exists(dirC), "ws_valid_kept must remain intact.");

        // Assert: Neither omitted directory was moved to workspaces_trash
        if (Directory.Exists(AppPaths.WorkspacesTrashDir))
        {
            var trashedA = Directory.GetDirectories(AppPaths.WorkspacesTrashDir, "ws_omitted_1*");
            var trashedB = Directory.GetDirectories(AppPaths.WorkspacesTrashDir, "ws_omitted_2*");
            Assert.Empty(trashedA);
            Assert.Empty(trashedB);
        }

        // Assert: Zombie directories MUST BE DELETED
        Assert.False(Directory.Exists(zombieEmpty), "Empty zombie directory must be deleted.");
        Assert.False(Directory.Exists(zombieZeroByte), "0-byte zombie directory must be deleted.");
    }

    [Fact]
    public void CleanUnmanifestedWorkspaces_DirectoryWithOnlyValidBackupOrGroups_ShouldNotBeDestroyed()
    {
        // Arrange: Workspace has corrupted/missing layout.json, but valid layout.json.bak and groups.json
        string dirWithBackup = AppPaths.GetWorkspaceDir("ws_has_backup_only");
        Directory.CreateDirectory(dirWithBackup);
        File.WriteAllText(Path.Combine(dirWithBackup, "layout.json.bak"), "[{\"Id\":\"tile_safe_1\"}]");
        File.WriteAllText(Path.Combine(dirWithBackup, "groups.json"), "[{\"Id\":\"grp_safe_1\"}]");

        // Act: Clean unmanifested workspaces with only "default"
        WorkspaceManager.CleanUnmanifestedWorkspaces(SingleDefaultId, isRecoveryScenario: true);

        // Assert: Directory must NOT be destroyed because it has valid recoverable data
        Assert.True(Directory.Exists(dirWithBackup), "Directory with valid backup or groups must not be purged as a zombie!");
    }

    [Fact]
    public void CleanUnmanifestedWorkspaces_NullOrEmptyValidIds_SafelyAbortsWithoutDeletingAnything()
    {
        // Arrange
        string dirA = AppPaths.GetWorkspaceDir("ws_test_safe_a");
        string zombieEmpty = AppPaths.GetWorkspaceDir("ws_zombie_empty");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(zombieEmpty);

        StorageService.SaveWorkspaceLayoutSync("ws_test_safe_a", new ObservableCollection<TileModel> { new() { Id = "t1" } });

        // Act 1: Pass null
        WorkspaceManager.CleanUnmanifestedWorkspaces(null);

        // Assert: Nothing was touched
        Assert.True(Directory.Exists(dirA));
        Assert.True(Directory.Exists(zombieEmpty));

        // Act 2: Pass empty array
        WorkspaceManager.CleanUnmanifestedWorkspaces(Array.Empty<string>());

        // Assert: Still nothing was touched
        Assert.True(Directory.Exists(dirA));
        Assert.True(Directory.Exists(zombieEmpty));
    }

    [Fact]
    public void CleanUnmanifestedWorkspaces_CaseInsensitiveValidIds_PreservesWorkspaces()
    {
        // Arrange
        string dir = AppPaths.GetWorkspaceDir("ws_case_sensitive");
        Directory.CreateDirectory(dir);
        StorageService.SaveWorkspaceLayoutSync("ws_case_sensitive", new ObservableCollection<TileModel> { new() { Id = "t1" } });

        // Act: Pass uppercase ID
        WorkspaceManager.CleanUnmanifestedWorkspaces(SingleUppercaseId);

        // Assert
        Assert.True(Directory.Exists(dir));
    }

    // ────────────────────────────────────────────────────────
    // Challenge R4: Concurrency & Persistence Atomicity
    // ────────────────────────────────────────────────────────

    [Fact]
    public void WorkspaceModel_SequentialRapidMutations_SynchronouslyReflectedOnDiskWithoutCorruption()
    {
        // Arrange
        WorkspaceManager.Instance.Initialize();
        var active = WorkspaceManager.Instance.ActiveWorkspace;
        Assert.NotNull(active);

        // Act: 10 rapid mutations to name and icon
        for (int i = 1; i <= 10; i++)
        {
            active.Name = $"Step Name {i}";
            active.IconSymbol = i % 2 == 0 ? "Flame24" : "Globe24";

            // Assert: At each step, file on disk MUST match current value synchronously
            string onDisk = File.ReadAllText(AppPaths.WorkspacesManifestPath);
            Assert.Contains($"\"Name\": \"Step Name {i}\"", onDisk);
            string expectedIcon = i % 2 == 0 ? "Flame24" : "Globe24";
            Assert.Contains($"\"IconSymbol\": \"{expectedIcon}\"", onDisk);
        }

        // Verify final reload from StorageService matches disk
        var reloaded = StorageService.LoadWorkspaces();
        var reloadedWs = reloaded.Workspaces.First(w => w.Id == active.Id);
        Assert.Equal("Step Name 10", reloadedWs.Name);
        Assert.Equal("Flame24", reloadedWs.IconSymbol);
    }
}
