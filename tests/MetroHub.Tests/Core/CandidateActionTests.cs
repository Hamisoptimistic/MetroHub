using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Models;
using MetroHub.Core.Search;
using MetroHub.Presentation.Controls;
using Xunit;

namespace MetroHub.Tests;

public sealed class CandidateActionTests
{
    [Fact]
    public void Candidate_WithoutActions_PreservesBackwardCompatibility()
    {
        var candidate = new Candidate(
            Id: "test_id",
            DisplayName: "Test App",
            FullPathOrKey: @"C:\test.exe",
            Category: SearchCategory.Apps,
            SourceId: "test_source");

        Assert.Equal("test_id", candidate.Id);
        Assert.Null(candidate.PrimaryAction);
        Assert.Null(candidate.SecondaryActions);
    }

    [Fact]
    public void Candidate_WithPrimaryAndSecondaryActions_InitializesCorrectly()
    {
        var primaryAction = new CandidateAction(
            Id: "open",
            Title: "Open",
            KeyGestureHint: "Enter",
            ExecuteAsync: ctx => Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer)));

        var secondaryAction = new CandidateAction(
            Id: "admin",
            Title: "Run as Administrator",
            KeyGestureHint: "Ctrl+Shift+Enter",
            ExecuteAsync: ctx => Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer)));

        var candidate = new Candidate(
            Id: "test_id",
            DisplayName: "Test App",
            FullPathOrKey: @"C:\test.exe",
            Category: SearchCategory.Apps,
            SourceId: "test_source")
        {
            PrimaryAction = primaryAction,
            SecondaryActions = new List<CandidateAction> { secondaryAction }
        };

        Assert.NotNull(candidate.PrimaryAction);
        Assert.Equal("open", candidate.PrimaryAction.Id);
        Assert.NotNull(candidate.SecondaryActions);
        Assert.Single(candidate.SecondaryActions);
        Assert.Equal("admin", candidate.SecondaryActions[0].Id);
    }

    [Fact]
    public void ExecuteCandidateActionAsync_DismissDrawer_ClosesDrawer()
    {
        WpfTestHost.RunSta(() =>
        {
            var drawer = new AllAppsDrawerControl();
            bool executed = false;

            var action = new CandidateAction(
                Id: "test",
                Title: "Test",
                KeyGestureHint: null,
                ExecuteAsync: ctx =>
                {
                    executed = true;
                    return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                });

            var task = drawer.ExecuteCandidateActionAsync("item1", action.ExecuteAsync);
            task.GetAwaiter().GetResult();

            Assert.True(executed);
            Assert.False(drawer.IsOpen);
        });
    }

    [Fact]
    public void ExecuteCandidateActionAsync_TwoStepConfirmation_FlowsConfirmedState()
    {
        WpfTestHost.RunSta(() =>
        {
            var drawer = new AllAppsDrawerControl();
            bool firstConfirmed = false;
            bool secondConfirmed = false;

            Func<ActionContext, Task<ActionResult>> action = ctx =>
            {
                if (!ctx.Confirmed)
                {
                    firstConfirmed = ctx.Confirmed;
                    return Task.FromResult(new ActionResult(ActionOutcome.RequireConfirmation, "Press Enter again"));
                }

                secondConfirmed = ctx.Confirmed;
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            };

            // First execution (not confirmed)
            drawer.ExecuteCandidateActionAsync("danger_item", action).GetAwaiter().GetResult();
            Assert.False(firstConfirmed);
            Assert.NotNull(drawer.PendingConfirm);
            Assert.Equal("danger_item", drawer.PendingConfirm.Value.Id);

            // Second execution within timeout (confirmed)
            drawer.ExecuteCandidateActionAsync("danger_item", action).GetAwaiter().GetResult();
            Assert.True(secondConfirmed);
            Assert.Null(drawer.PendingConfirm);
        });
    }

    [Fact]
    public void ExecuteCandidateActionAsync_UacCancelled_GracefullySwallowed()
    {
        WpfTestHost.RunSta(() =>
        {
            var drawer = new AllAppsDrawerControl();

            Func<ActionContext, Task<ActionResult>> action = _ =>
            {
                throw new Win32Exception(1223); // ERROR_CANCELLED (User said No to UAC)
            };

            // Should not throw
            var task = drawer.ExecuteCandidateActionAsync("uac_item", action);
            task.GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void ExecuteCandidateActionAsync_UnhandledException_ShowsErrorMessageWithoutCrashing()
    {
        WpfTestHost.RunSta(() =>
        {
            var drawer = new AllAppsDrawerControl();

            Func<ActionContext, Task<ActionResult>> action = _ =>
            {
                throw new InvalidOperationException("Fatal crash test");
            };

            // Should catch, log, and not crash
            var task = drawer.ExecuteCandidateActionAsync("bad_item", action);
            task.GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void SearchItemRowViewModel_OverrideSubtitle_UpdatesAndNotifies()
    {
        var candidate = new Candidate(
            Id: @"D:\file.txt",
            DisplayName: "file.txt",
            FullPathOrKey: @"D:\file.txt",
            Category: SearchCategory.Documents,
            SourceId: "everything");

        var vm = new SearchItemRowViewModel(candidate);
        Assert.Equal(@"D:\", vm.Subtitle);

        bool subtitleChanged = false;
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(SearchItemRowViewModel.Subtitle)) subtitleChanged = true;
        };

        vm.OverrideSubtitle = "Press Enter again to confirm";
        Assert.True(subtitleChanged);
        Assert.Equal("Press Enter again to confirm", vm.Subtitle);

        vm.OverrideSubtitle = null;
        Assert.Equal(@"D:\", vm.Subtitle);
    }

    [Fact]
    public void AppActionProvider_ShortcutLnkApp_IncludesRunAsAdminAction()
    {
        var app = new CatalogItemModel
        {
            Name = "Visual Studio Code",
            TargetPath = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Visual Studio Code.lnk",
            TileType = TileType.App
        };

        var candidate = new Candidate(
            Id: app.TargetPath,
            DisplayName: app.Name,
            FullPathOrKey: app.TargetPath,
            Category: SearchCategory.Apps,
            SourceId: "app_catalog",
            Tag: app)
        {
            ActionProvider = AppActionProvider.GetActions
        };

        var actions = candidate.GetAllActions();
        var adminAction = actions.FirstOrDefault(a => a.Id == "app:run_admin");

        Assert.NotNull(adminAction);
        Assert.Equal("Run as administrator", adminAction.Title);
    }

    [Fact]
    public void AppActionProvider_RunAsAdminAction_InvokesSearchActionRegistry()
    {
        var app = new CatalogItemModel
        {
            Name = "Notepad",
            TargetPath = @"C:\Windows\notepad.exe",
            TileType = TileType.App
        };

        var candidate = new Candidate(
            Id: app.TargetPath,
            DisplayName: app.Name,
            FullPathOrKey: app.TargetPath,
            Category: SearchCategory.Apps,
            SourceId: "app_catalog",
            Tag: app)
        {
            ActionProvider = AppActionProvider.GetActions
        };

        CatalogItemModel? launchedAdminApp = null;
        SearchActionRegistry.LaunchAppAsAdmin = a => launchedAdminApp = a;

        try
        {
            var actions = candidate.GetAllActions();
            var adminAction = actions.First(a => a.Id == "app:run_admin");

            adminAction.ExecuteAsync(new ActionContext(CancellationToken.None)).GetAwaiter().GetResult();

            Assert.Same(app, launchedAdminApp);
        }
        finally
        {
            SearchActionRegistry.LaunchAppAsAdmin = null;
        }
    }

    [Fact]
    public void FileActionProvider_ShortcutLnkFile_IncludesRunAsAdminAction()
    {
        var candidate = new Candidate(
            Id: @"C:\Users\User\Desktop\CustomApp.lnk",
            DisplayName: "CustomApp.lnk",
            FullPathOrKey: @"C:\Users\User\Desktop\CustomApp.lnk",
            Category: SearchCategory.Documents,
            SourceId: "everything")
        {
            ActionProvider = FileActionProvider.GetActions
        };

        var actions = candidate.GetAllActions();
        var adminAction = actions.FirstOrDefault(a => a.Id == "file:run_admin");

        Assert.NotNull(adminAction);
        Assert.Equal("Run as administrator", adminAction.Title);
    }
}

