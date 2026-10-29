using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MetroHub.Core.Models;
using MetroHub.Core.Search;
using MetroHub.Core.Services;
using MetroHub.Presentation.Controls;
using MetroHub.Presentation.Controls.Behaviors;
using Xunit;

namespace MetroHub.Tests;

public sealed class UniversalSearchUiTests
{
    [Fact]
    public void SearchItemRowViewModel_AppCandidate_MapsCorrectly()
    {
        var app = new CatalogItemModel
        {
            Name = "Calculator",
            TargetPath = @"C:\Windows\System32\calc.exe"
        };

        var candidate = new Candidate(
            Id: app.TargetPath,
            DisplayName: app.Name,
            FullPathOrKey: app.TargetPath,
            Category: SearchCategory.Apps,
            SourceId: "installed_apps",
            Tag: app);

        var vm = new SearchItemRowViewModel(candidate, "Apps");

        Assert.True(vm.IsApp);
        Assert.Equal(app, vm.AppModel);
        Assert.Equal("Calculator", vm.DisplayName);
        Assert.Equal(string.Empty, vm.Subtitle); // Apps hide subtitle
        Assert.Equal(Visibility.Collapsed, vm.SubtitleVisibility);
        Assert.Equal("Apps", vm.CategoryHeader);
        Assert.Equal(Visibility.Visible, vm.CategoryHeaderVisibility);
        Assert.Equal(Wpf.Ui.Controls.SymbolRegular.Apps24, vm.Symbol);
    }

    [Fact]
    public void SearchItemRowViewModel_FileCandidate_MapsCorrectly()
    {
        var candidate = new Candidate(
            Id: @"D:\Docs\Report.pdf",
            DisplayName: "Report.pdf",
            FullPathOrKey: @"D:\Docs\Report.pdf",
            Category: SearchCategory.Documents,
            SourceId: "everything");

        var vm = new SearchItemRowViewModel(candidate, "");

        Assert.False(vm.IsApp);
        Assert.Null(vm.AppModel);
        Assert.Equal("Report.pdf", vm.DisplayName);
        Assert.Equal(@"D:\Docs", vm.Subtitle);
        Assert.Equal(@"D:\Docs\Report.pdf", vm.FullPath);
        Assert.Equal(Visibility.Visible, vm.SubtitleVisibility);
        Assert.Equal(string.Empty, vm.CategoryHeader);
        Assert.Equal(Visibility.Collapsed, vm.CategoryHeaderVisibility);
        Assert.Equal(Wpf.Ui.Controls.SymbolRegular.Document24, vm.Symbol);
        Assert.NotNull(vm.Icon);
        Assert.Equal(Visibility.Visible, vm.CustomIconVisibility);
        Assert.Equal(Visibility.Collapsed, vm.SymbolVisibility);
    }

    [Fact]
    public void SearchItemRowViewModel_FolderCandidate_MapsCorrectly()
    {
        var candidate = new Candidate(
            Id: @"D:\Projects\MetroHub",
            DisplayName: "MetroHub",
            FullPathOrKey: @"D:\Projects\MetroHub",
            Category: SearchCategory.Folders,
            SourceId: "everything",
            IsFolder: true);

        var vm = new SearchItemRowViewModel(candidate, "Folders");

        Assert.False(vm.IsApp);
        Assert.Null(vm.AppModel);
        Assert.Equal("MetroHub", vm.DisplayName);
        Assert.Equal(@"D:\Projects\MetroHub", vm.Subtitle);
        Assert.Equal(@"D:\Projects\MetroHub", vm.FullPath);
        Assert.Equal(Visibility.Visible, vm.SubtitleVisibility);
        Assert.Equal("Folders", vm.CategoryHeader);
        Assert.Equal(Visibility.Visible, vm.CategoryHeaderVisibility);
        Assert.Equal(Wpf.Ui.Controls.SymbolRegular.Folder24, vm.Symbol);
        Assert.NotNull(vm.Icon);
        Assert.Equal(Visibility.Visible, vm.CustomIconVisibility);
        Assert.Equal(Visibility.Collapsed, vm.SymbolVisibility);
    }

    [Fact]
    public void IconExtractorService_ExtensionFlyweightCache_ReusesExactSameInstance()
    {
        var icon1 = IconExtractorService.GetFileOrExtensionIcon(@"C:\test1.docx", isFolder: false);
        var icon2 = IconExtractorService.GetFileOrExtensionIcon(@"D:\path\test2.docx", isFolder: false);

        Assert.NotNull(icon1);
        Assert.NotNull(icon2);
        Assert.Same(icon1, icon2);
    }

    [Fact]
    public void IconExtractorService_FolderIcon_ResolvesAndCaches()
    {
        var folder1 = IconExtractorService.GetFileOrExtensionIcon(@"C:\Folder1", isFolder: true);
        var folder2 = IconExtractorService.GetFileOrExtensionIcon(@"D:\Folder2", isFolder: true);

        Assert.NotNull(folder1);
        Assert.NotNull(folder2);
        Assert.Same(folder1, folder2);
    }

    [Fact]
    public void IconExtractorService_InvalidateAssociationCache_ClearsCacheWithoutExceptions()
    {
        var iconBefore = IconExtractorService.GetFileOrExtensionIcon(@"C:\document.pdf", isFolder: false);
        Assert.NotNull(iconBefore);

        IconExtractorService.InvalidateAssociationCache();

        var iconAfter = IconExtractorService.GetFileOrExtensionIcon(@"C:\document.pdf", isFolder: false);
        Assert.NotNull(iconAfter);
    }

    [Fact]
    public void SearchItemRowViewModel_MediaAndCodeCandidates_MapCorrectSymbols()
    {
        var mediaCandidate = new Candidate(
            Id: @"D:\Music\song.mp3",
            DisplayName: "song.mp3",
            FullPathOrKey: @"D:\Music\song.mp3",
            Category: SearchCategory.Media,
            SourceId: "everything");

        var mediaVm = new SearchItemRowViewModel(mediaCandidate);
        Assert.Equal(Wpf.Ui.Controls.SymbolRegular.MusicNote224, mediaVm.Symbol);

        var codeCandidate = new Candidate(
            Id: @"D:\Code\main.rs",
            DisplayName: "main.rs",
            FullPathOrKey: @"D:\Code\main.rs",
            Category: SearchCategory.Code,
            SourceId: "everything");

        var codeVm = new SearchItemRowViewModel(codeCandidate);
        Assert.Equal(Wpf.Ui.Controls.SymbolRegular.Code24, codeVm.Symbol);

        var otherCandidate = new Candidate(
            Id: @"D:\Data\archive.zip",
            DisplayName: "archive.zip",
            FullPathOrKey: @"D:\Data\archive.zip",
            Category: SearchCategory.Other,
            SourceId: "everything");

        var otherVm = new SearchItemRowViewModel(otherCandidate);
        Assert.Equal(Wpf.Ui.Controls.SymbolRegular.Document24, otherVm.Symbol);
    }

    [Fact]
    public void SearchItemRowViewModel_ActionCandidates_MapToFluentSymbolsAndSuppressFileIcon()
    {
        var clockCandidate = new Candidate(
            Id: "time:tokyo",
            DisplayName: "Tokyo, Japan: 11:42 PM",
            FullPathOrKey: "Friday, October 16, 2026",
            Category: SearchCategory.Actions,
            SourceId: "world_clock")
        {
            Icon = Wpf.Ui.Controls.SymbolRegular.Clock24
        };

        var clockVm = new SearchItemRowViewModel(clockCandidate, "Actions");

        Assert.Equal(Wpf.Ui.Controls.SymbolRegular.Clock24, clockVm.Symbol);
        Assert.Null(clockVm.Icon);
        Assert.Equal(Visibility.Collapsed, clockVm.CustomIconVisibility);
        Assert.Equal(Visibility.Visible, clockVm.SymbolVisibility);

        var calcCandidate = new Candidate(
            Id: "calc:27.6",
            DisplayName: "= 27.6",
            FullPathOrKey: "24 * 1.15 = 27.6",
            Category: SearchCategory.Actions,
            SourceId: "calculator")
        {
            Icon = Wpf.Ui.Controls.SymbolRegular.Calculator24
        };

        var calcVm = new SearchItemRowViewModel(calcCandidate, "Actions");
        Assert.Equal(Wpf.Ui.Controls.SymbolRegular.Calculator24, calcVm.Symbol);
        Assert.Null(calcVm.Icon);

        var termCandidate = new Candidate(
            Id: "terminal:ping",
            DisplayName: "> ping 1.1.1.1",
            FullPathOrKey: "ping 1.1.1.1",
            Category: SearchCategory.Actions,
            SourceId: "terminal")
        {
            Icon = Wpf.Ui.Controls.SymbolRegular.WindowConsole20
        };

        var termVm = new SearchItemRowViewModel(termCandidate, "Actions");
        Assert.Equal(Wpf.Ui.Controls.SymbolRegular.WindowConsole20, termVm.Symbol);
        Assert.Null(termVm.Icon);
    }

    [Fact]
    public void BuildSearchGroups_PlacesActionsAtTheVeryTopAboveAppsAndFiles()
    {
        var actionCand = new Candidate("1", "Tokyo: 11:42 PM", "Tokyo", SearchCategory.Actions, "world_clock");
        var appCand = new Candidate("2", "Terminal", @"C:\wt.exe", SearchCategory.Apps, "installed_apps");
        var docCand = new Candidate("3", "notes.txt", @"C:\notes.txt", SearchCategory.Documents, "everything");
        var otherCand = new Candidate("4", "trace.etl", @"C:\trace.etl", SearchCategory.Other, "everything");

        var scored = new List<ScoredResult>
        {
            new(docCand, 100, MatchKind.Exact),
            new(otherCand, 90, MatchKind.Prefix),
            new(appCand, 80, MatchKind.Prefix),
            new(actionCand, 70, MatchKind.Contains)
        };

        var groups = SearchOrchestrator.BuildSearchGroups(scored, 10);

        Assert.Equal(4, groups.Count);
        Assert.Equal(SearchCategory.Actions, groups[0].Category);
        Assert.Equal("Actions", groups[0].Title);
        Assert.Equal(SearchCategory.Apps, groups[1].Category);
        Assert.Equal("Apps", groups[1].Title);
    }

    [Fact]
    public void AllAppsDrawerControl_InstantiationInSta_Succeeds()
    {
        WpfTestHost.RunSta(() =>
        {
            var drawer = new AllAppsDrawerControl();
            Assert.NotNull(drawer);
            Assert.False(drawer.IsOpen);
        });
    }

    [Fact]
    public void AllAppsDrawerControl_NoResultsPanel_InitializedCollapsed()
    {
        WpfTestHost.RunSta(() =>
        {
            var drawer = new AllAppsDrawerControl();
            Assert.NotNull(drawer.NoResultsPanelControl);
            Assert.Equal(System.Windows.Visibility.Collapsed, drawer.NoResultsPanelControl.Visibility);
        });
    }

    [Fact]
    public void AllAppsDrawerControl_SearchResultsListBox_VirtualizationConfigured()
    {
        WpfTestHost.RunSta(() =>
        {
            var drawer = new AllAppsDrawerControl();
            Assert.NotNull(drawer.SearchResultsListBoxControl);
            Assert.Equal(System.Windows.Visibility.Collapsed, drawer.SearchResultsListBoxControl.Visibility);
            Assert.True(System.Windows.Controls.VirtualizingPanel.GetIsVirtualizing(drawer.SearchResultsListBoxControl));
            Assert.Equal(System.Windows.Controls.VirtualizationMode.Recycling, System.Windows.Controls.VirtualizingPanel.GetVirtualizationMode(drawer.SearchResultsListBoxControl));
            Assert.Equal(System.Windows.Controls.ScrollUnit.Pixel, System.Windows.Controls.VirtualizingPanel.GetScrollUnit(drawer.SearchResultsListBoxControl));
            Assert.True(System.Windows.Controls.ScrollViewer.GetCanContentScroll(drawer.SearchResultsListBoxControl));
            Assert.Equal(System.Windows.Controls.ScrollBarVisibility.Auto, System.Windows.Controls.ScrollViewer.GetVerticalScrollBarVisibility(drawer.SearchResultsListBoxControl));
            Assert.Equal(System.Windows.Controls.ScrollBarVisibility.Disabled, System.Windows.Controls.ScrollViewer.GetHorizontalScrollBarVisibility(drawer.SearchResultsListBoxControl));
            Assert.True(AutoHideScrollBehavior.GetIsEnabled(drawer.SearchResultsListBoxControl));
        });
    }

    [Fact]
    public void SearchItemRowViewModel_LazyIcon_NotLoadedUntilRequested()
    {
        var app = new CatalogItemModel
        {
            Name = "Notepad",
            TargetPath = @"C:\Windows\notepad.exe"
        };
        var candidate = new Candidate("notepad", "Notepad", @"C:\Windows\notepad.exe", SearchCategory.Apps, "test", Tag: app);

        using var vm = new SearchItemRowViewModel(candidate, "Apps");

        // Should NOT be loaded or requested eagerly upon construction
        Assert.False(vm.IsIconRequested);
        Assert.False(vm.IsAppModelHandlerAttached);

        // Accessing Icon triggers lazy loading on demand
        _ = vm.Icon;

        Assert.True(vm.IsIconRequested);
        Assert.True(vm.IsAppModelHandlerAttached);
    }

    [Fact]
    public void SearchItemRowViewModel_Detach_CleansUpHandlerAndMarksDiscarded()
    {
        var app = new CatalogItemModel
        {
            Name = "Calculator",
            TargetPath = @"C:\Windows\calc.exe"
        };
        var candidate = new Candidate("calc", "Calculator", @"C:\Windows\calc.exe", SearchCategory.Apps, "test", Tag: app);

        using var vm = new SearchItemRowViewModel(candidate, "Apps");
        _ = vm.Icon; // trigger attach
        Assert.True(vm.IsAppModelHandlerAttached);

        vm.Detach();

        Assert.True(vm.IsDiscarded);
        Assert.False(vm.IsAppModelHandlerAttached);
        Assert.True(vm.IsStale());
    }

    [Fact]
    public void SearchItemRowViewModel_IsStale_DetectsSessionMismatchAndCancellation()
    {
        long currentSession = 10;
        using var cts = new System.Threading.CancellationTokenSource();

        var candidate = new Candidate("test", "Test", @"C:\test.exe", SearchCategory.Documents, "test");
        using var vm = new SearchItemRowViewModel(
            candidate,
            "Documents",
            sessionId: 10,
            sessionIdProvider: () => currentSession,
            cancellationToken: cts.Token);

        Assert.False(vm.IsStale());

        // Session ID changed (e.g. user typed next character)
        currentSession = 11;
        Assert.True(vm.IsStale());

        // Reset session, then test CancellationToken
        currentSession = 10;
        Assert.False(vm.IsStale());
        cts.Cancel();
        Assert.True(vm.IsStale());
    }

    [Fact]
    public void IconExtractorService_PerFileCache_BoundsSize()
    {
        IconExtractorService.InvalidateAssociationCache();
        Assert.Equal(0, IconExtractorService.PerFileCacheCount);

        var dummyBrush = new System.Windows.Media.Imaging.BitmapImage();
        dummyBrush.Freeze();

        // Add 300 unique paths, exceeding MaxPerFileCachedIcons (256)
        for (int i = 0; i < 300; i++)
        {
            IconExtractorService.AddToPerFileCacheForTesting($@"C:\fake\app_{i}.exe", dummyBrush);
        }

        // Cache must enforce the bound and not grow indefinitely
        Assert.True(IconExtractorService.PerFileCacheCount <= 256);
        Assert.True(IconExtractorService.PerFileCacheCount > 200);

        IconExtractorService.InvalidateAssociationCache();
    }

    [Fact]
    public void IconExtractorService_InFlightTasks_DeduplicatesSamePath()
    {
        IconExtractorService.InvalidateAssociationCache();

        string target = @"C:\Windows\System32\cmd.exe";
        int callbacksReceived = 0;

        // Fire multiple concurrent requests for the exact same file path
        var icon1 = IconExtractorService.GetFileOrExtensionIcon(target, isFolder: false, onAsyncIconLoaded: _ => Interlocked.Increment(ref callbacksReceived));
        var icon2 = IconExtractorService.GetFileOrExtensionIcon(target, isFolder: false, onAsyncIconLoaded: _ => Interlocked.Increment(ref callbacksReceived));

        Assert.NotNull(icon1);
        Assert.NotNull(icon2);
        Assert.Same(icon1, icon2);

        IconExtractorService.InvalidateAssociationCache();
    }

    [Fact]
    public void AllAppsDrawerControl_SnapshotTierUpdate_ReusesExistingRowInstances()
    {
        WpfTestHost.RunSta(() =>
        {
            var drawer = new AllAppsDrawerControl();
            drawer.SearchBox.Text = "test";

            long sessionId = drawer.SearchOrchestratorInstance.CurrentSessionId;

            var cand1 = new Candidate("app:1", "App One", @"C:\App1.exe", SearchCategory.Apps, "test");
            var cand2 = new Candidate("file:2", "Doc Two", @"C:\Doc2.txt", SearchCategory.Documents, "test");

            var group1 = new SearchGroup(SearchCategory.Apps, "Apps", new[] { new ScoredResult(cand1, 100, MatchKind.Exact) }, 1, 100);
            var snapshotTier1 = new SearchSnapshot(
                SessionId: sessionId,
                Groups: new[] { group1 },
                IsFinal: false,
                ElapsedMs: 5,
                SourceStates: new Dictionary<string, SourceState>());

            drawer.OnSearchSnapshotUpdated(snapshotTier1);
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);

            var rowsTier1 = (drawer.SearchResultsListBoxControl?.ItemsSource as IEnumerable<SearchItemRowViewModel>)?.ToList();
            Assert.NotNull(rowsTier1);
            Assert.Single(rowsTier1);
            var firstRow = rowsTier1[0];

            // Now Tier 2 arrives with an additional file candidate in the same session
            var group2 = new SearchGroup(SearchCategory.Documents, "Documents", new[] { new ScoredResult(cand2, 80, MatchKind.Contains) }, 1, 80);
            var snapshotTier2 = new SearchSnapshot(
                SessionId: sessionId,
                Groups: new[] { group1, group2 },
                IsFinal: true,
                ElapsedMs: 30,
                SourceStates: new Dictionary<string, SourceState>());

            drawer.OnSearchSnapshotUpdated(snapshotTier2);
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);

            var rowsTier2 = (drawer.SearchResultsListBoxControl?.ItemsSource as IEnumerable<SearchItemRowViewModel>)?.ToList();
            Assert.NotNull(rowsTier2);
            Assert.Equal(2, rowsTier2.Count);

            // Row 1 should be the exact same instance (reused!)
            Assert.Same(firstRow, rowsTier2[0]);
        });
    }

    [Fact]
    public void IconExtractorService_CancelledToken_DoesNotScheduleOrTriggerCallback()
    {
        IconExtractorService.InvalidateAssociationCache();

        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();

        bool callbackInvoked = false;
        var icon = IconExtractorService.GetFileOrExtensionIcon(
            @"C:\Windows\explorer.exe",
            isFolder: false,
            onAsyncIconLoaded: _ => callbackInvoked = true,
            cancellationToken: cts.Token);

        Assert.NotNull(icon);
        Assert.False(callbackInvoked);
        Assert.Equal(0, IconExtractorService.InFlightTaskCount);

        IconExtractorService.InvalidateAssociationCache();
    }

    [Fact]
    public void IconExtractorService_EmptyOrWhitespacePath_ReturnsNullWithoutThrowing()
    {
        Assert.Null(IconExtractorService.GetFileOrExtensionIcon("", isFolder: false));
        Assert.Null(IconExtractorService.GetFileOrExtensionIcon("   ", isFolder: false));
        Assert.Null(IconExtractorService.GetFileOrExtensionIcon(null!, isFolder: false));
    }

    [Fact]
    public void SearchItemRowViewModel_Detach_IsIdempotent()
    {
        var app = new CatalogItemModel
        {
            Name = "Terminal",
            TargetPath = @"C:\Windows\System32\cmd.exe"
        };
        var candidate = new Candidate("term", "Terminal", @"C:\Windows\System32\cmd.exe", SearchCategory.Apps, "test", Tag: app);

        using var vm = new SearchItemRowViewModel(candidate, "Apps");
        _ = vm.Icon;

        // Calling Detach and Dispose multiple times should not throw
        vm.Detach();
        vm.Detach();
        vm.Dispose();

        Assert.True(vm.IsDiscarded);
        Assert.False(vm.IsAppModelHandlerAttached);
    }

    [Fact]
    public void AllAppsDrawerControl_SearchResultsListBox_VirtualizationAndScrollViewer_Configured()
    {
        WpfTestHost.RunSta(() =>
        {
            var drawer = new AllAppsDrawerControl();
            var listBox = drawer.SearchResultsListBoxControl;
            Assert.NotNull(listBox);

            Assert.True(VirtualizingPanel.GetIsVirtualizing(listBox));
            Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(listBox));
            Assert.Equal(ScrollUnit.Pixel, VirtualizingPanel.GetScrollUnit(listBox));
            Assert.True(ScrollViewer.GetCanContentScroll(listBox));
            Assert.Equal(ScrollBarVisibility.Auto, ScrollViewer.GetVerticalScrollBarVisibility(listBox));
            Assert.Equal(ScrollBarVisibility.Disabled, ScrollViewer.GetHorizontalScrollBarVisibility(listBox));
            Assert.True(AutoHideScrollBehavior.GetIsEnabled(listBox));
            Assert.True(Grid.GetIsSharedSizeScope(listBox));

            var svStyle = listBox.Resources[typeof(ScrollViewer)] as Style;
            Assert.NotNull(svStyle);
            Assert.Equal(typeof(ScrollViewer), svStyle.TargetType);

            var floatingStyle = drawer.FindResource("FloatingScrollViewerStyle") as Style;
            Assert.NotNull(floatingStyle);
            Assert.Same(floatingStyle, svStyle.BasedOn);
        });
    }

    [Fact]
    public void AllAppsDrawerControl_NoResultsPanel_OverlayAndCentering_Configured()
    {
        WpfTestHost.RunSta(() =>
        {
            var drawer = new AllAppsDrawerControl();
            var panel = drawer.NoResultsPanelControl;
            Assert.NotNull(panel);

            Assert.Equal(HorizontalAlignment.Center, panel.HorizontalAlignment);
            Assert.Equal(VerticalAlignment.Center, panel.VerticalAlignment);
            Assert.Equal(Visibility.Collapsed, panel.Visibility);

            var hostGrid = panel.Parent as Grid;
            Assert.NotNull(hostGrid);
            Assert.Equal("AppsContentHostGrid", hostGrid.Name);
            Assert.Contains(drawer.SearchResultsListBoxControl, hostGrid.Children.Cast<UIElement>());
            Assert.Contains(panel, hostGrid.Children.Cast<UIElement>());
        });
    }

    [Fact]
    public void AllAppsDrawerControl_NoResultsPanel_VisibilityFlow_Correct()
    {
        WpfTestHost.RunSta(() =>
        {
            var drawer = new AllAppsDrawerControl();
            drawer.SearchBoxControl!.Text = "nonexistentquery123";

            long sessionId = drawer.SearchOrchestratorInstance.CurrentSessionId;
            var emptySnapshot = new SearchSnapshot(
                SessionId: sessionId,
                Groups: Array.Empty<SearchGroup>(),
                IsFinal: true,
                ElapsedMs: 15,
                SourceStates: new Dictionary<string, SourceState>());

            drawer.OnSearchSnapshotUpdated(emptySnapshot);
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);

            Assert.Equal(Visibility.Visible, drawer.NoResultsPanelControl!.Visibility);

            // Clearing query restores grouped view and hides no results overlay
            drawer.SearchBoxControl.Text = string.Empty;
            Assert.Equal(Visibility.Collapsed, drawer.NoResultsPanelControl.Visibility);
        });
    }

    [Fact]
    public void AllAppsDrawerControl_KeyboardNavigation_PageSizeAndBounds_Correct()
    {
        WpfTestHost.RunSta(() =>
        {
            var drawer = new AllAppsDrawerControl();
            int pageSize = drawer.GetSearchPageSize();
            Assert.InRange(pageSize, 3, 20);

            var rows = Enumerable.Range(0, 25)
                .Select(i => new SearchItemRowViewModel(new Candidate($"app:{i}", $"App {i}", $@"C:\app_{i}.exe", SearchCategory.Apps, "test")))
                .ToList();

            drawer.SearchResultsListBoxControl!.ItemsSource = rows;
            drawer.SearchResultsListBoxControl.SelectedIndex = 0;

            // Focus top item
            drawer.FocusSearchItem(0);
            Assert.Equal(0, drawer.SearchResultsListBoxControl.SelectedIndex);

            // Page down from index 0
            int pageDownIndex = Math.Min(rows.Count - 1, drawer.SearchResultsListBoxControl.SelectedIndex + pageSize);
            drawer.FocusSearchItem(pageDownIndex);
            Assert.Equal(pageDownIndex, drawer.SearchResultsListBoxControl.SelectedIndex);

            // End key: jump to last index
            drawer.FocusSearchItem(rows.Count - 1);
            Assert.Equal(rows.Count - 1, drawer.SearchResultsListBoxControl.SelectedIndex);

            // Wrap down from last index to top
            drawer.FocusSearchItem(0);
            Assert.Equal(0, drawer.SearchResultsListBoxControl.SelectedIndex);

            // Clean up rows
            foreach (var r in rows) r.Detach();
        });
    }

    [Fact]
    public void AutoHideScrollBehavior_AttachOnVisibilityChange_Succeeds()
    {
        WpfTestHost.RunSta(() =>
        {
            var border = new Border
            {
                Visibility = Visibility.Collapsed
            };
            var sv = new ScrollViewer();
            border.Child = sv;

            AutoHideScrollBehavior.SetIsEnabled(border, true);
            Assert.True(AutoHideScrollBehavior.GetIsEnabled(border));

            // Toggling visibility exercises the IsVisibleChanged hook safely
            border.Visibility = Visibility.Visible;
            border.Visibility = Visibility.Collapsed;
        });
    }
}

