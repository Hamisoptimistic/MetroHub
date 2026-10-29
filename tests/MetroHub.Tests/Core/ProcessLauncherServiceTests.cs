using System;
using System.IO;
using MetroHub.Core.Services;
using Xunit;

namespace MetroHub.Tests;

public sealed class ProcessLauncherServiceTests
{
    [Theory]
    [InlineData("www.example.com", "https://www.example.com")]
    [InlineData("github.com", "https://github.com")]
    [InlineData("claude.ai", "https://claude.ai")]
    [InlineData("huggingface.co/models", "huggingface.co/models")]
    [InlineData("test.io", "https://test.io")]
    [InlineData("sub.domain.org", "https://sub.domain.org")]
    [InlineData("mycustom.app", "https://mycustom.app")]
    public void NormalizeTargetPath_InfersHttpsProtocol(string input, string expected)
    {
        // Act
        string result = ProcessLauncherService.NormalizeTargetPath(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("https://google.com", "https://google.com")]
    [InlineData("http://insecure.site", "http://insecure.site")]
    [InlineData("shell:AppsFolder", "shell:AppsFolder")]
    [InlineData("C:\\Windows\\notepad.exe", "C:\\Windows\\notepad.exe")]
    public void NormalizeTargetPath_PreservesExplicitProtocolsAndLocalPaths(string input, string expected)
    {
        // Act
        string result = ProcessLauncherService.NormalizeTargetPath(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ResolveWorkingDirectory_ReturnsNullForEmptyOrWhitespace()
    {
        // Act & Assert
        Assert.Null(ProcessLauncherService.ResolveWorkingDirectory(""));
        Assert.Null(ProcessLauncherService.ResolveWorkingDirectory("   "));
        Assert.Null(ProcessLauncherService.ResolveWorkingDirectory(null!));
    }

    [Fact]
    public void ResolveWorkingDirectory_ReturnsDirectoryForExistingFile()
    {
        // Arrange: use an existing assembly file in current working directory
        string existingFile = typeof(ProcessLauncherService).Assembly.Location;
        string expectedDir = Path.GetDirectoryName(existingFile)!;

        // Act
        string? result = ProcessLauncherService.ResolveWorkingDirectory(existingFile);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(expectedDir, result);
    }

    [Fact]
    public void ResolveWorkingDirectory_ReturnsDirectoryWhenGivenExistingDirectory()
    {
        // Arrange
        string existingDir = AppDomain.CurrentDomain.BaseDirectory;

        // Act
        string? result = ProcessLauncherService.ResolveWorkingDirectory(existingDir);

        // Assert
        Assert.Equal(existingDir, result);
    }

    [Fact]
    public void LaunchTarget_ReturnsFalseForEmptyTarget()
    {
        // Act & Assert
        Assert.False(ProcessLauncherService.LaunchTarget(""));
        Assert.False(ProcessLauncherService.LaunchTarget("   "));
    }

    [Fact]
    public void NativeMethods_ForwardsToProcessLauncherService()
    {
        // Ensure NativeMethods facade preserves identical behavior
        string existingDir = AppDomain.CurrentDomain.BaseDirectory;
        Assert.Equal(ProcessLauncherService.ResolveWorkingDirectory(existingDir), NativeMethods.ResolveWorkingDirectory(existingDir));
        Assert.False(NativeMethods.LaunchTarget(""));
    }
}
