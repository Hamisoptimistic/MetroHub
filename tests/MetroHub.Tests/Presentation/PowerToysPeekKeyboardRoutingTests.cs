using System;
using System.IO;
using System.Linq;
using System.Windows.Input;
using System.Windows.Interop;
using MetroHub.Core.Search;
using MetroHub.Presentation.Controls;
using Xunit;

namespace MetroHub.Tests;

public sealed class PowerToysPeekKeyboardRoutingTests : IDisposable
{
    private readonly string _tempFile;

    public PowerToysPeekKeyboardRoutingTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"peek_kb_{Guid.NewGuid():N}.txt");
        File.WriteAllText(_tempFile, "sample preview file content");
        PowerToysPeekLauncher.ResetCacheForTesting();
    }

    public void Dispose()
    {
        PowerToysPeekLauncher.ResetCacheForTesting();
        try
        {
            if (File.Exists(_tempFile)) File.Delete(_tempFile);
        }
        catch
        {
            // Ignore temp file cleanup error
        }
    }

#pragma warning disable CA2000 // Test dummy presentation source does not leak native window handles
    private static KeyEventArgs CreateKeyEventArgs(Key key, bool isRepeat = false)
    {
        var e = new KeyEventArgs(
            Keyboard.PrimaryDevice,
            new HwndSource(0, 0, 0, 0, 0, "", IntPtr.Zero),
            0,
            key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };

        if (isRepeat)
        {
            typeof(KeyEventArgs).GetField("_isRepeat", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(e, true);
        }

        return e;
    }
#pragma warning restore CA2000

    [Fact]
    public void SearchResults_SpaceKey_OnValidFileCandidate_LaunchesPeekAndHandlesEvent()
    {
        WpfTestHost.RunSta(() =>
        {
            PowerToysPeekLauncher.CustomPeekPathResolver = () => @"C:\PowerToys\PowerToys.Peek.UI.exe";
            string? launchedPath = null;
            PowerToysPeekLauncher.ProcessStarter = psi =>
            {
                launchedPath = psi.ArgumentList.FirstOrDefault();
                return true;
            };

            var drawer = new AllAppsDrawerControl();
            var candidate = new Candidate("file:1", Path.GetFileName(_tempFile), _tempFile, SearchCategory.Documents, "test");
            var row = new SearchItemRowViewModel(candidate);

            drawer.SearchResultsListBox.ItemsSource = new[] { row };
            drawer.SearchResultsListBox.SelectedIndex = 0;

            var keyArgs = CreateKeyEventArgs(Key.Space);

            drawer.HandleSearchResultsPreviewKeyDown(keyArgs, ModifierKeys.None);

            Assert.True(keyArgs.Handled);
            Assert.Equal(_tempFile, launchedPath);
        });
    }

    [Fact]
    public void SearchResults_SpaceKey_WithAltModifier_PreservesSystemMenuAndDoesNotHandle()
    {
        WpfTestHost.RunSta(() =>
        {
            PowerToysPeekLauncher.CustomPeekPathResolver = () => @"C:\PowerToys\PowerToys.Peek.UI.exe";
            bool processStarted = false;
            PowerToysPeekLauncher.ProcessStarter = _ =>
            {
                processStarted = true;
                return true;
            };

            var drawer = new AllAppsDrawerControl();
            var candidate = new Candidate("file:1", Path.GetFileName(_tempFile), _tempFile, SearchCategory.Documents, "test");
            var row = new SearchItemRowViewModel(candidate);

            drawer.SearchResultsListBox.ItemsSource = new[] { row };
            drawer.SearchResultsListBox.SelectedIndex = 0;

            var keyArgs = CreateKeyEventArgs(Key.Space);

            // Alt+Space must pass through to Win32 system menu
            drawer.HandleSearchResultsPreviewKeyDown(keyArgs, ModifierKeys.Alt);

            Assert.False(keyArgs.Handled);
            Assert.False(processStarted);
        });
    }

    [Fact]
    public void SearchResults_SpaceKey_OnNonFileCandidate_DoesNotHandle()
    {
        WpfTestHost.RunSta(() =>
        {
            PowerToysPeekLauncher.CustomPeekPathResolver = () => @"C:\PowerToys\PowerToys.Peek.UI.exe";
            bool processStarted = false;
            PowerToysPeekLauncher.ProcessStarter = _ =>
            {
                processStarted = true;
                return true;
            };

            var drawer = new AllAppsDrawerControl();
            // Calculator candidate with non-file path
            var candidate = new Candidate("calc:token", "Calculator", "42", SearchCategory.Actions, "calculator");
            var row = new SearchItemRowViewModel(candidate);

            drawer.SearchResultsListBox.ItemsSource = new[] { row };
            drawer.SearchResultsListBox.SelectedIndex = 0;

            var keyArgs = CreateKeyEventArgs(Key.Space);

            drawer.HandleSearchResultsPreviewKeyDown(keyArgs, ModifierKeys.None);

            Assert.False(keyArgs.Handled);
            Assert.False(processStarted);
        });
    }

    [Fact]
    public void SearchBox_CtrlSpaceKey_WhenCandidateSelected_LaunchesPeekAndHandlesEvent()
    {
        WpfTestHost.RunSta(() =>
        {
            PowerToysPeekLauncher.CustomPeekPathResolver = () => @"C:\PowerToys\PowerToys.Peek.UI.exe";
            string? launchedPath = null;
            PowerToysPeekLauncher.ProcessStarter = psi =>
            {
                launchedPath = psi.ArgumentList.FirstOrDefault();
                return true;
            };

            var drawer = new AllAppsDrawerControl();
            drawer.SearchBox.Text = "report";

            var candidate = new Candidate("file:1", Path.GetFileName(_tempFile), _tempFile, SearchCategory.Documents, "test");
            var row = new SearchItemRowViewModel(candidate);

            drawer.SearchResultsListBox.ItemsSource = new[] { row };
            drawer.SearchResultsListBox.SelectedIndex = 0;

            var keyArgs = CreateKeyEventArgs(Key.Space);

            drawer.HandleSearchBoxPreviewKeyDown(keyArgs, ModifierKeys.Control);

            Assert.True(keyArgs.Handled);
            Assert.Equal(_tempFile, launchedPath);
        });
    }

    [Fact]
    public void SearchBox_NormalSpaceKey_WithoutControl_DoesNotHandleEventToAllowTyping()
    {
        WpfTestHost.RunSta(() =>
        {
            PowerToysPeekLauncher.CustomPeekPathResolver = () => @"C:\PowerToys\PowerToys.Peek.UI.exe";
            bool processStarted = false;
            PowerToysPeekLauncher.ProcessStarter = _ =>
            {
                processStarted = true;
                return true;
            };

            var drawer = new AllAppsDrawerControl();
            drawer.SearchBox.Text = "metro";

            var candidate = new Candidate("file:1", Path.GetFileName(_tempFile), _tempFile, SearchCategory.Documents, "test");
            var row = new SearchItemRowViewModel(candidate);
            drawer.SearchResultsListBox.ItemsSource = new[] { row };
            drawer.SearchResultsListBox.SelectedIndex = 0;

            var keyArgs = CreateKeyEventArgs(Key.Space);

            // User types space character without Ctrl
            drawer.HandleSearchBoxPreviewKeyDown(keyArgs, ModifierKeys.None);

            Assert.False(keyArgs.Handled);
            Assert.False(processStarted);
        });
    }

    [Fact]
    public void SearchBox_CtrlSpaceKey_WithAltModifier_DoesNotHandleEvent()
    {
        WpfTestHost.RunSta(() =>
        {
            PowerToysPeekLauncher.CustomPeekPathResolver = () => @"C:\PowerToys\PowerToys.Peek.UI.exe";
            bool processStarted = false;
            PowerToysPeekLauncher.ProcessStarter = _ =>
            {
                processStarted = true;
                return true;
            };

            var drawer = new AllAppsDrawerControl();
            drawer.SearchBox.Text = "report";

            var candidate = new Candidate("file:1", Path.GetFileName(_tempFile), _tempFile, SearchCategory.Documents, "test");
            var row = new SearchItemRowViewModel(candidate);
            drawer.SearchResultsListBox.ItemsSource = new[] { row };
            drawer.SearchResultsListBox.SelectedIndex = 0;

            var keyArgs = CreateKeyEventArgs(Key.Space);

            // Ctrl+Alt+Space must not intercept
            drawer.HandleSearchBoxPreviewKeyDown(keyArgs, ModifierKeys.Control | ModifierKeys.Alt);

            Assert.False(keyArgs.Handled);
            Assert.False(processStarted);
        });
    }

    [Fact]
    public void SearchResults_SpaceKey_RepeatKeystrokes_SwallowsEventWithoutSpawningProcess()
    {
        WpfTestHost.RunSta(() =>
        {
            PowerToysPeekLauncher.CustomPeekPathResolver = () => @"C:\PowerToys\PowerToys.Peek.UI.exe";
            int spawnCount = 0;
            PowerToysPeekLauncher.ProcessStarter = _ =>
            {
                spawnCount++;
                return true;
            };

            var drawer = new AllAppsDrawerControl();
            var candidate = new Candidate("file:1", Path.GetFileName(_tempFile), _tempFile, SearchCategory.Documents, "test");
            var row = new SearchItemRowViewModel(candidate);

            drawer.SearchResultsListBox.ItemsSource = new[] { row };
            drawer.SearchResultsListBox.SelectedIndex = 0;

            // First press (not repeat): should launch peek and handle
            var firstPress = CreateKeyEventArgs(Key.Space, isRepeat: false);
            drawer.HandleSearchResultsPreviewKeyDown(firstPress, ModifierKeys.None);
            Assert.True(firstPress.Handled);
            Assert.Equal(1, spawnCount);

            // Subsequent repeated keystroke (isRepeat == true): must swallow (Handled = true) but NOT launch again
            var repeatPress = CreateKeyEventArgs(Key.Space, isRepeat: true);
            drawer.HandleSearchResultsPreviewKeyDown(repeatPress, ModifierKeys.None);
            Assert.True(repeatPress.Handled);
            Assert.Equal(1, spawnCount); // No second launch!
        });
    }

    [Fact]
    public void SearchBox_CtrlSpaceKey_RepeatKeystrokes_SwallowsEventWithoutSpawningProcess()
    {
        WpfTestHost.RunSta(() =>
        {
            PowerToysPeekLauncher.CustomPeekPathResolver = () => @"C:\PowerToys\PowerToys.Peek.UI.exe";
            int spawnCount = 0;
            PowerToysPeekLauncher.ProcessStarter = _ =>
            {
                spawnCount++;
                return true;
            };

            var drawer = new AllAppsDrawerControl();
            drawer.SearchBox.Text = "query";

            var candidate = new Candidate("file:1", Path.GetFileName(_tempFile), _tempFile, SearchCategory.Documents, "test");
            var row = new SearchItemRowViewModel(candidate);

            drawer.SearchResultsListBox.ItemsSource = new[] { row };
            drawer.SearchResultsListBox.SelectedIndex = 0;

            // Repeated Ctrl+Space keystroke inside search box
            var repeatPress = CreateKeyEventArgs(Key.Space, isRepeat: true);
            drawer.HandleSearchBoxPreviewKeyDown(repeatPress, ModifierKeys.Control);

            Assert.True(repeatPress.Handled);
            Assert.Equal(0, spawnCount); // Repeat keystrokes must not spawn processes
        });
    }

    [Fact]
    public void RecentSuggestions_SpaceKey_OnValidFileCandidate_LaunchesPeekAndHandlesEvent()
    {
        WpfTestHost.RunSta(() =>
        {
            PowerToysPeekLauncher.CustomPeekPathResolver = () => @"C:\PowerToys\PowerToys.Peek.UI.exe";
            string? launchedPath = null;
            PowerToysPeekLauncher.ProcessStarter = psi =>
            {
                launchedPath = psi.ArgumentList.FirstOrDefault();
                return true;
            };

            var drawer = new AllAppsDrawerControl();
            var candidate = new Candidate("recent:1", Path.GetFileName(_tempFile), _tempFile, SearchCategory.Documents, "test");
            var row = new SearchItemRowViewModel(candidate);

            drawer.RecentSuggestionsListBox.ItemsSource = new[] { row };
            drawer.RecentSuggestionsListBox.SelectedIndex = 0;

            var keyArgs = CreateKeyEventArgs(Key.Space, isRepeat: false);
            drawer.HandleRecentSuggestionsPreviewKeyDown(keyArgs, ModifierKeys.None);

            Assert.True(keyArgs.Handled);
            Assert.Equal(_tempFile, launchedPath);
        });
    }

    [Fact]
    public void RecentSuggestions_SpaceKey_RepeatKeystrokes_SwallowsEventWithoutSpawningProcess()
    {
        WpfTestHost.RunSta(() =>
        {
            PowerToysPeekLauncher.CustomPeekPathResolver = () => @"C:\PowerToys\PowerToys.Peek.UI.exe";
            int spawnCount = 0;
            PowerToysPeekLauncher.ProcessStarter = _ =>
            {
                spawnCount++;
                return true;
            };

            var drawer = new AllAppsDrawerControl();
            var candidate = new Candidate("recent:1", Path.GetFileName(_tempFile), _tempFile, SearchCategory.Documents, "test");
            var row = new SearchItemRowViewModel(candidate);

            drawer.RecentSuggestionsListBox.ItemsSource = new[] { row };
            drawer.RecentSuggestionsListBox.SelectedIndex = 0;

            var repeatPress = CreateKeyEventArgs(Key.Space, isRepeat: true);
            drawer.HandleRecentSuggestionsPreviewKeyDown(repeatPress, ModifierKeys.None);

            Assert.True(repeatPress.Handled);
            Assert.Equal(0, spawnCount);
        });
    }

    [Fact]
    public void RecentSuggestions_SpaceKey_WithAltModifier_PreservesSystemMenuAndDoesNotHandle()
    {
        WpfTestHost.RunSta(() =>
        {
            PowerToysPeekLauncher.CustomPeekPathResolver = () => @"C:\PowerToys\PowerToys.Peek.UI.exe";
            bool processStarted = false;
            PowerToysPeekLauncher.ProcessStarter = _ =>
            {
                processStarted = true;
                return true;
            };

            var drawer = new AllAppsDrawerControl();
            var candidate = new Candidate("recent:1", Path.GetFileName(_tempFile), _tempFile, SearchCategory.Documents, "test");
            var row = new SearchItemRowViewModel(candidate);

            drawer.RecentSuggestionsListBox.ItemsSource = new[] { row };
            drawer.RecentSuggestionsListBox.SelectedIndex = 0;

            var keyArgs = CreateKeyEventArgs(Key.Space, isRepeat: false);
            drawer.HandleRecentSuggestionsPreviewKeyDown(keyArgs, ModifierKeys.Alt);

            Assert.False(keyArgs.Handled);
            Assert.False(processStarted);
        });
    }

    [Fact]
    public void SearchBox_CtrlSpaceKey_WhenSearchBoxEmpty_PrefersRecentSuggestionsOverStaleSearchResults()
    {
        WpfTestHost.RunSta(() =>
        {
            PowerToysPeekLauncher.CustomPeekPathResolver = () => @"C:\PowerToys\PowerToys.Peek.UI.exe";
            string? launchedPath = null;
            PowerToysPeekLauncher.ProcessStarter = psi =>
            {
                launchedPath = psi.ArgumentList.FirstOrDefault();
                return true;
            };

            var drawer = new AllAppsDrawerControl();
            drawer.SearchBox.Text = string.Empty; // Zero state!

            string staleFile = Path.Combine(Path.GetTempPath(), $"stale_{Guid.NewGuid():N}.txt");
            File.WriteAllText(staleFile, "stale");

            try
            {
                var staleCandidate = new Candidate("file:stale", "stale.txt", staleFile, SearchCategory.Documents, "test");
                var staleRow = new SearchItemRowViewModel(staleCandidate);
                drawer.SearchResultsListBox.ItemsSource = new[] { staleRow };
                drawer.SearchResultsListBox.SelectedIndex = 0;

                var recentCandidate = new Candidate("file:recent", Path.GetFileName(_tempFile), _tempFile, SearchCategory.Documents, "test");
                var recentRow = new SearchItemRowViewModel(recentCandidate);
                drawer.RecentSuggestionsListBox.ItemsSource = new[] { recentRow };
                drawer.RecentSuggestionsListBox.SelectedIndex = 0;

                var keyArgs = CreateKeyEventArgs(Key.Space);
                drawer.HandleSearchBoxPreviewKeyDown(keyArgs, ModifierKeys.Control);

                Assert.True(keyArgs.Handled);
                Assert.Equal(_tempFile, launchedPath); // Must be recent item, NOT stale search item!
            }
            finally
            {
                if (File.Exists(staleFile)) File.Delete(staleFile);
            }
        });
    }

    [Fact]
    public void IsTransientToolOverlay_WhenPowerToysPeekWindow_ReturnsTrue()
    {
        PowerToysPeekLauncher.CustomPeekWindowChecker = (hwnd, pid) => pid == 7777;

        bool isTransient = MainWindow.IsTransientToolOverlay(new IntPtr(1234), 7777);

        Assert.True(isTransient);
    }

    [Fact]
    public void IsTransientToolOverlay_WhenZeroHwnd_ReturnsFalse()
    {
        bool isTransient = MainWindow.IsTransientToolOverlay(IntPtr.Zero, 7777);

        Assert.False(isTransient);
    }

    [Fact]
    public void ShouldHandlePeekOverlay_WhenPeekWindow_ReturnsTrue()
    {
        PowerToysPeekLauncher.CustomPeekWindowChecker = (hwnd, pid) => pid == 7777;

        bool shouldHandle = MainWindow.ShouldHandlePeekOverlay(new IntPtr(1234), 7777);

        Assert.True(shouldHandle);
    }

    [Fact]
    public void ShouldHandlePeekOverlay_WhenRecentLaunchAndZeroHwnd_ReturnsTrue()
    {
        PowerToysPeekLauncher.CustomPeekPathResolver = () => @"C:\PowerToys\PowerToys.Peek.UI.exe";
        PowerToysPeekLauncher.ProcessStarter = _ => true;

        bool launched = PowerToysPeekLauncher.TryLaunchPreview(_tempFile);
        Assert.True(launched);

        bool shouldHandle = MainWindow.ShouldHandlePeekOverlay(IntPtr.Zero, 0);

        Assert.True(shouldHandle);
    }

    [Fact]
    public void ShouldHandlePeekOverlay_WhenRecentLaunchAndNonPowerToysProcess_ReturnsFalse()
    {
        PowerToysPeekLauncher.CustomPeekPathResolver = () => @"C:\PowerToys\PowerToys.Peek.UI.exe";
        PowerToysPeekLauncher.ProcessStarter = _ => true;

        bool launched = PowerToysPeekLauncher.TryLaunchPreview(_tempFile);
        Assert.True(launched);

        // e.g. User switched to Chrome (PID 99999) - should NOT handle/retain Peek overlay
        bool shouldHandle = MainWindow.ShouldHandlePeekOverlay(new IntPtr(5678), 99999);

        Assert.False(shouldHandle);
    }

    [Fact]
    public void ShouldHandlePeekOverlay_WhenNotRecentLaunchAndNonPeek_ReturnsFalse()
    {
        PowerToysPeekLauncher.ResetCacheForTesting();

        bool shouldHandle = MainWindow.ShouldHandlePeekOverlay(IntPtr.Zero, 0);

        Assert.False(shouldHandle);
    }
}
