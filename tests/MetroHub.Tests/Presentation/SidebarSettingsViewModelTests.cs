using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Presentation.Views.Settings.ViewModels;
using Xunit;

namespace MetroHub.Tests;

public class SidebarSettingsViewModelTests
{
    [Fact]
    public void SidebarSettingsViewModel_InitializesPropertiesFromSettings()
    {
        var vm = new SidebarSettingsViewModel();

        Assert.Equal(SettingsService.Instance.Current.SidebarAutoHide, vm.AutoHideSidebar);
        Assert.Equal(SettingsService.Instance.Current.ShowWorkspacesButton, vm.ShowWorkspacesButton);
        Assert.Equal(SettingsService.Instance.Current.ShowAddShortcutButton, vm.ShowAddShortcutButton);
        Assert.Equal(SettingsService.Instance.Current.ShowPowerButton, vm.ShowPowerButton);
        Assert.Equal(SettingsService.Instance.Current.ShowRecentApps, vm.ShowRecentApps);
        Assert.Contains("Grid", vm.RecentLayouts);
        Assert.Contains("List", vm.RecentLayouts);
    }

    [Fact]
    public void SidebarSettingsViewModel_MutatingProperties_UpdatesUnderlyingSettings()
    {
        var vm = new SidebarSettingsViewModel();
        bool initialAutoHide = vm.AutoHideSidebar;
        bool initialWorkspaces = vm.ShowWorkspacesButton;
        bool initialAdd = vm.ShowAddShortcutButton;
        bool initialPower = vm.ShowPowerButton;
        bool initialRecent = vm.ShowRecentApps;
        string initialLayout = vm.SelectedRecentLayout;

        try
        {
            // Toggle Auto-Hide (should flip SidebarAutoHide and inverse SidebarPinned)
            vm.AutoHideSidebar = !initialAutoHide;
            Assert.Equal(!initialAutoHide, SettingsService.Instance.Current.SidebarAutoHide);
            Assert.Equal(initialAutoHide, SettingsService.Instance.Current.SidebarPinned);

            // Toggle Workspaces Button
            vm.ShowWorkspacesButton = !initialWorkspaces;
            Assert.Equal(!initialWorkspaces, SettingsService.Instance.Current.ShowWorkspacesButton);

            // Toggle Add Button
            vm.ShowAddShortcutButton = !initialAdd;
            Assert.Equal(!initialAdd, SettingsService.Instance.Current.ShowAddShortcutButton);

            // Toggle Power Button
            vm.ShowPowerButton = !initialPower;
            Assert.Equal(!initialPower, SettingsService.Instance.Current.ShowPowerButton);

            // Toggle Recent Apps
            vm.ShowRecentApps = !initialRecent;
            Assert.Equal(!initialRecent, SettingsService.Instance.Current.ShowRecentApps);

            // Change Recent Apps Layout
            vm.SelectedRecentLayout = "List";
            Assert.Equal(RecentItemsViewMode.List, SettingsService.Instance.Current.RecentViewMode);

            vm.SelectedRecentLayout = "Grid";
            Assert.Equal(RecentItemsViewMode.Grid, SettingsService.Instance.Current.RecentViewMode);
        }
        finally
        {
            vm.AutoHideSidebar = initialAutoHide;
            vm.ShowWorkspacesButton = initialWorkspaces;
            vm.ShowAddShortcutButton = initialAdd;
            vm.ShowPowerButton = initialPower;
            vm.ShowRecentApps = initialRecent;
            vm.SelectedRecentLayout = initialLayout;
        }
    }
}

