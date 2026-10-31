using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MetroHub.Core.Search;
using Wpf.Ui.Controls;
using Xunit;

namespace MetroHub.Tests;

public sealed class PowerToysPeekLauncherTests : IDisposable
{
    private readonly string _tempFile;
    private readonly string _tempDir;

    public PowerToysPeekLauncherTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"peek_test_{Guid.NewGuid():N}.txt");
        File.WriteAllText(_tempFile, "preview test content");

        _tempDir = Path.Combine(Path.GetTempPath(), $"peek_dir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        PowerToysPeekLauncher.ResetCacheForTesting();
    }

    public void Dispose()
    {
        PowerToysPeekLauncher.ResetCacheForTesting();

        try
        {
            if (File.Exists(_tempFile)) File.Delete(_tempFile);
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch
        {
            // Ignore temp file cleanup errors
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/file.txt")]
    [InlineData(@"C:\NonExistent_98765\NonExistent_12345.tmp")]
    public void TryLaunchPreview_WithInvalidOrNonExistentPath_ReturnsFalse(string? path)
    {
        PowerToysPeekLauncher.CustomPeekPathResolver = () => @"C:\PowerToys\PowerToys.Peek.UI.exe";

        bool result = PowerToysPeekLauncher.TryLaunchPreview(path!);

        Assert.False(result);
    }

    [Fact]
    public void TryLaunchPreview_WhenPeekNotAvailable_ReturnsFalse()
    {
        PowerToysPeekLauncher.CustomPeekPathResolver = () => null;

        bool result = PowerToysPeekLauncher.TryLaunchPreview(_tempFile);

        Assert.False(result);
    }

    [Fact]
    public void TryLaunchPreview_WhenPeekAvailable_PassesPathSafelyToProcessStartInfo()
    {
        string mockExe = @"C:\Program Files\PowerToys\WinUI3Apps\PowerToys.Peek.UI.exe";
        PowerToysPeekLauncher.CustomPeekPathResolver = () => mockExe;

        string? invokedExe = null;
        string? invokedArg = null;
        bool useShellExecute = true;
        bool createNoWindow = false;

        PowerToysPeekLauncher.ProcessStarter = psi =>
        {
            invokedExe = psi.FileName;
            invokedArg = psi.ArgumentList.FirstOrDefault();
            useShellExecute = psi.UseShellExecute;
            createNoWindow = psi.CreateNoWindow;
            return true;
        };

        bool result = PowerToysPeekLauncher.TryLaunchPreview(_tempFile);

        Assert.True(result);
        Assert.Equal(mockExe, invokedExe);
        Assert.Equal(_tempFile, invokedArg);
        Assert.False(useShellExecute);
        Assert.True(createNoWindow);
    }

    [Fact]
    public void TryLaunchPreview_WithTrailingBackslashDirectory_PreservesDirectoryPath()
    {
        string mockExe = @"C:\Program Files\PowerToys\WinUI3Apps\PowerToys.Peek.UI.exe";
        PowerToysPeekLauncher.CustomPeekPathResolver = () => mockExe;

        string targetDirWithTrailingSlash = _tempDir.TrimEnd('\\') + "\\";
        string? invokedArg = null;

        PowerToysPeekLauncher.ProcessStarter = psi =>
        {
            invokedArg = psi.ArgumentList.FirstOrDefault();
            return true;
        };

        bool result = PowerToysPeekLauncher.TryLaunchPreview(targetDirWithTrailingSlash);

        Assert.True(result);
        Assert.Equal(targetDirWithTrailingSlash, invokedArg);
    }

    [Fact]
    public void TryLaunchPreview_WithQuotedPath_TrimsQuotesAndSucceeds()
    {
        string mockExe = @"C:\Program Files\PowerToys\WinUI3Apps\PowerToys.Peek.UI.exe";
        PowerToysPeekLauncher.CustomPeekPathResolver = () => mockExe;

        string? invokedArg = null;
        PowerToysPeekLauncher.ProcessStarter = psi =>
        {
            invokedArg = psi.ArgumentList.FirstOrDefault();
            return true;
        };

        string quotedPath = $"\"{_tempFile}\"";
        bool result = PowerToysPeekLauncher.TryLaunchPreview(quotedPath);

        Assert.True(result);
        Assert.Equal(_tempFile, invokedArg);
    }

    [Fact]
    public void TryLaunchPreview_WithWhitespacePaddedPath_TrimsAndSucceeds()
    {
        string mockExe = @"C:\Program Files\PowerToys\WinUI3Apps\PowerToys.Peek.UI.exe";
        PowerToysPeekLauncher.CustomPeekPathResolver = () => mockExe;

        string? invokedArg = null;
        PowerToysPeekLauncher.ProcessStarter = psi =>
        {
            invokedArg = psi.ArgumentList.FirstOrDefault();
            return true;
        };

        string paddedPath = $"   {_tempFile}   ";
        bool result = PowerToysPeekLauncher.TryLaunchPreview(paddedPath);

        Assert.True(result);
        Assert.Equal(_tempFile, invokedArg);
    }

    [Fact]
    public void FileActionProvider_WhenPeekAvailable_RegistersPreviewAction()
    {
        PowerToysPeekLauncher.CustomPeekPathResolver = () => @"C:\Program Files\PowerToys\WinUI3Apps\PowerToys.Peek.UI.exe";

        var candidate = new Candidate(
            Id: _tempFile,
            DisplayName: Path.GetFileName(_tempFile),
            FullPathOrKey: _tempFile,
            Category: SearchCategory.Documents,
            SourceId: "everything");

        var actions = FileActionProvider.GetActions(candidate);

        var peekAction = actions.FirstOrDefault(a => a.Id == "file:peek");
        Assert.NotNull(peekAction);
        Assert.Equal("Preview (Peek)", peekAction.Title);
        Assert.Equal("Space", peekAction.KeyGestureHint);
        Assert.Equal(SymbolRegular.Eye24, peekAction.Icon.Symbol);
    }

    [Fact]
    public void FileActionProvider_WhenPeekUnavailable_OmitsPreviewAction()
    {
        PowerToysPeekLauncher.CustomPeekPathResolver = () => null;

        var candidate = new Candidate(
            Id: _tempFile,
            DisplayName: Path.GetFileName(_tempFile),
            FullPathOrKey: _tempFile,
            Category: SearchCategory.Documents,
            SourceId: "everything");

        var actions = FileActionProvider.GetActions(candidate);

        Assert.DoesNotContain(actions, a => a.Id == "file:peek");
    }

    [Fact]
    public async Task FileActionProvider_PreviewAction_InvokesPreviewAndReturnsStayOpen()
    {
        string mockExe = @"C:\Program Files\PowerToys\WinUI3Apps\PowerToys.Peek.UI.exe";
        PowerToysPeekLauncher.CustomPeekPathResolver = () => mockExe;

        string? launchedPath = null;
        PowerToysPeekLauncher.ProcessStarter = psi =>
        {
            launchedPath = psi.ArgumentList.FirstOrDefault();
            return true;
        };

        var candidate = new Candidate(
            Id: _tempFile,
            DisplayName: Path.GetFileName(_tempFile),
            FullPathOrKey: _tempFile,
            Category: SearchCategory.Documents,
            SourceId: "everything");

        var actions = FileActionProvider.GetActions(candidate);
        var peekAction = actions.First(a => a.Id == "file:peek");

        var outcome = await peekAction.ExecuteAsync(null!);

        Assert.Equal(ActionOutcome.StayOpenAndRefresh, outcome.Outcome);
        Assert.Equal(_tempFile, launchedPath);
    }

    [Fact]
    public void GetPeekPath_WhenResolved_CachesPathAndAvoidsSubsequentLookups()
    {
        int lookupCount = 0;
        PowerToysPeekLauncher.CustomFindPeekExe = () =>
        {
            lookupCount++;
            return _tempFile;
        };

        string? path1 = PowerToysPeekLauncher.GetPeekPath();
        string? path2 = PowerToysPeekLauncher.GetPeekPath();

        Assert.Equal(_tempFile, path1);
        Assert.Equal(_tempFile, path2);
        Assert.Equal(1, lookupCount); // Second call must hit cache
    }

    [Fact]
    public void GetPeekPath_WhenCachedExeDeleted_InvalidatesCacheAndReResolves()
    {
        string ephemeralExe = Path.Combine(Path.GetTempPath(), $"ephemeral_peek_{Guid.NewGuid():N}.exe");
        File.WriteAllText(ephemeralExe, "fake exe content");

        try
        {
            int lookupCount = 0;
            PowerToysPeekLauncher.CustomFindPeekExe = () =>
            {
                lookupCount++;
                return lookupCount == 1 ? ephemeralExe : _tempFile;
            };

            string? path1 = PowerToysPeekLauncher.GetPeekPath();
            Assert.Equal(ephemeralExe, path1);
            Assert.Equal(1, lookupCount);

            // Now delete the cached executable file
            File.Delete(ephemeralExe);

            // Next call must detect the deleted file, invalidate the cache, and re-resolve
            string? path2 = PowerToysPeekLauncher.GetPeekPath();
            Assert.Equal(_tempFile, path2);
            Assert.Equal(2, lookupCount);
        }
        finally
        {
            if (File.Exists(ephemeralExe)) File.Delete(ephemeralExe);
        }
    }

    [Fact]
    public void GetPeekPath_WhenNotFound_ThrottlesSubsequentLookups()
    {
        int lookupCount = 0;
        PowerToysPeekLauncher.CustomFindPeekExe = () =>
        {
            lookupCount++;
            return null;
        };

        string? path1 = PowerToysPeekLauncher.GetPeekPath();
        string? path2 = PowerToysPeekLauncher.GetPeekPath();

        Assert.Null(path1);
        Assert.Null(path2);
        Assert.Equal(1, lookupCount); // Second call is throttled by the 1-minute retry interval
    }

    [Fact]
    public void GetProcessPathViaLimitedQuery_WithCurrentProcess_ResolvesExecutablePath()
    {
        string? path = PowerToysPeekLauncher.GetProcessPathViaLimitedQuery(Environment.ProcessId);

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void GetProcessPathViaLimitedQuery_WithInvalidPid_ReturnsNull()
    {
        string? path = PowerToysPeekLauncher.GetProcessPathViaLimitedQuery(-1);

        Assert.Null(path);
    }

    [Fact]
    public void IsPeekWindow_WithZeroParameters_ReturnsFalse()
    {
        Assert.False(PowerToysPeekLauncher.IsPeekWindow(IntPtr.Zero, 0));
    }

    [Fact]
    public void IsPeekWindow_WithCustomChecker_UsesCheckerResult()
    {
        PowerToysPeekLauncher.CustomPeekWindowChecker = (hwnd, pid) => pid == 12345;

        Assert.True(PowerToysPeekLauncher.IsPeekWindow(IntPtr.Zero, 12345));
        Assert.False(PowerToysPeekLauncher.IsPeekWindow(IntPtr.Zero, 54321));
    }

    [Fact]
    public void IsPowerToysProcess_WithZero_ReturnsFalse()
    {
        Assert.False(PowerToysPeekLauncher.IsPowerToysProcess(0));
    }

    [Fact]
    public void IsPowerToysProcess_WithCurrentProcess_ReturnsFalse()
    {
        // Testhost is not PowerToys
        Assert.False(PowerToysPeekLauncher.IsPowerToysProcess((uint)Environment.ProcessId));
    }

    [Fact]
    public void IsRecentLaunch_TracksTryLaunchPreviewAndResets()
    {
        Assert.False(PowerToysPeekLauncher.IsRecentLaunch);

        string mockExe = @"C:\Program Files\PowerToys\WinUI3Apps\PowerToys.Peek.UI.exe";
        PowerToysPeekLauncher.CustomPeekPathResolver = () => mockExe;
        PowerToysPeekLauncher.ProcessStarter = _ => true;

        bool launched = PowerToysPeekLauncher.TryLaunchPreview(_tempFile);
        Assert.True(launched);
        Assert.True(PowerToysPeekLauncher.IsRecentLaunch);

        PowerToysPeekLauncher.ResetCacheForTesting();
        Assert.False(PowerToysPeekLauncher.IsRecentLaunch);
    }

    [Fact]
    public void IsPeekWindow_WithCurrentProcess_ReturnsFalse()
    {
        Assert.False(PowerToysPeekLauncher.IsPeekWindow(IntPtr.Zero, (uint)Environment.ProcessId));
    }

    [Fact]
    public void TryLaunchPreview_WhenProcessStarterReturnsFalse_DoesNotSetIsRecentLaunch()
    {
        Assert.False(PowerToysPeekLauncher.IsRecentLaunch);

        string mockExe = @"C:\Program Files\PowerToys\WinUI3Apps\PowerToys.Peek.UI.exe";
        PowerToysPeekLauncher.CustomPeekPathResolver = () => mockExe;
        PowerToysPeekLauncher.ProcessStarter = _ => false;

        bool launched = PowerToysPeekLauncher.TryLaunchPreview(_tempFile);
        Assert.False(launched);
        Assert.False(PowerToysPeekLauncher.IsRecentLaunch);
    }

    [Fact]
    public void TryLaunchPreview_WhenProcessStarterThrows_DoesNotSetIsRecentLaunch()
    {
        Assert.False(PowerToysPeekLauncher.IsRecentLaunch);

        string mockExe = @"C:\Program Files\PowerToys\WinUI3Apps\PowerToys.Peek.UI.exe";
        PowerToysPeekLauncher.CustomPeekPathResolver = () => mockExe;
        PowerToysPeekLauncher.ProcessStarter = _ => throw new InvalidOperationException("boom");

        bool launched = PowerToysPeekLauncher.TryLaunchPreview(_tempFile);
        Assert.False(launched);
        Assert.False(PowerToysPeekLauncher.IsRecentLaunch);
    }
}
