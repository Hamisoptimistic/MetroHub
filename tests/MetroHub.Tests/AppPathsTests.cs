using System;
using System.IO;
using MetroHub.Core.Services;
using Xunit;

namespace MetroHub.Tests;

[Collection("StorageTests")]
public sealed class AppPathsTests : IDisposable
{
    private readonly string _testRoot;

    public AppPathsTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "MetroHub_AppPathsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void ResolveFilePath_ReturnsCategorizedPath_WhenNeitherFileExists_ForFirstTimeUser()
    {
        // Arrange
        string categorized = Path.Combine(_testRoot, "config", "layout.json");
        string legacy = Path.Combine(_testRoot, "layout.json");

        // Act
        string resolved = AppPaths.ResolveFilePath(categorized, legacy);

        // Assert: New install / first-time user gets categorized path
        Assert.Equal(categorized, resolved);
    }

    [Fact]
    public void ResolveFilePath_ReturnsLegacyPath_WhenOnlyLegacyExists_PreservingExistingUsers()
    {
        // Arrange
        string categorized = Path.Combine(_testRoot, "config", "layout.json");
        string legacy = Path.Combine(_testRoot, "layout.json");
        File.WriteAllText(legacy, "{\"test\": true}");

        // Act
        string resolved = AppPaths.ResolveFilePath(categorized, legacy);

        // Assert: Existing user folder is left untouched
        Assert.Equal(legacy, resolved);
    }

    [Fact]
    public void ResolveFilePath_ReturnsCategorizedPath_WhenCategorizedFileExists()
    {
        // Arrange
        string configDir = Path.Combine(_testRoot, "config");
        Directory.CreateDirectory(configDir);
        string categorized = Path.Combine(configDir, "layout.json");
        string legacy = Path.Combine(_testRoot, "layout.json");
        File.WriteAllText(categorized, "{\"categorized\": true}");
        File.WriteAllText(legacy, "{\"legacy\": true}");

        // Act
        string resolved = AppPaths.ResolveFilePath(categorized, legacy);

        // Assert: Categorized path takes precedence when present
        Assert.Equal(categorized, resolved);
    }

    [Fact]
    public void ResolveDirectoryPath_ReturnsLegacy_WhenLegacyHasEntries()
    {
        // Arrange
        string legacyIcons = Path.Combine(_testRoot, "icons");
        Directory.CreateDirectory(legacyIcons);
        File.WriteAllText(Path.Combine(legacyIcons, "test.png"), "dummy");

        string categorizedIcons = Path.Combine(_testRoot, "cache", "icons");

        // Act
        string resolved = AppPaths.ResolveDirectoryPath(categorizedIcons, legacyIcons);

        // Assert
        Assert.Equal(legacyIcons, resolved);
    }

    [Fact]
    public void ResolveDirectoryPath_ReturnsCategorized_WhenCleanInstall()
    {
        // Arrange
        string legacyIcons = Path.Combine(_testRoot, "icons");
        string categorizedIcons = Path.Combine(_testRoot, "cache", "icons");

        // Act
        string resolved = AppPaths.ResolveDirectoryPath(categorizedIcons, legacyIcons);

        // Assert
        Assert.Equal(categorizedIcons, resolved);
    }

    [Fact]
    public void EnsureDirectory_CreatesParentDirectorySafely()
    {
        // Arrange
        string nestedFilePath = Path.Combine(_testRoot, "deep", "nested", "folder", "test.json");

        // Act
        AppPaths.EnsureDirectory(nestedFilePath);

        // Assert
        Assert.True(Directory.Exists(Path.GetDirectoryName(nestedFilePath)));
    }

    [Fact]
    public void AppPaths_CategoryDirectories_AreLocatedUnderAppDataDir()
    {
        Assert.StartsWith(AppPaths.AppDataDir, AppPaths.ConfigDir, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(AppPaths.AppDataDir, AppPaths.CacheDir, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(AppPaths.AppDataDir, AppPaths.LogsDir, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(AppPaths.AppDataDir, AppPaths.BackupsDir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HiddenDiagnosticsLogPath_ResolvesUnderLogsDir_ForCleanState()
    {
        Assert.StartsWith(AppPaths.LogsDir, AppPaths.HiddenDiagnosticsLogPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("hidden_diagnostics.log", AppPaths.HiddenDiagnosticsLogPath, StringComparison.OrdinalIgnoreCase);
    }
}
