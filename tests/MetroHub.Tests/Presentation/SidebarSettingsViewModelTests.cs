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
        Assert.Equal(SettingsService.Instance.Current.OpenDrawerOnSearchHover, vm.OpenDrawerOnSearchHover);
        Assert.Contains("Grid", vm.RecentLayouts);
        Assert.Contains("List", vm.RecentLayouts);
        Assert.Contains("Relaxed", vm.DrawerDensities);
        Assert.Contains("Compact", vm.DrawerDensities);
        Assert.Equal(SettingsService.Instance.Current.DrawerDensity == DrawerDensity.Compact ? "Compact" : "Relaxed", vm.SelectedDrawerDensity);
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
        bool initialHover = vm.OpenDrawerOnSearchHover;
        string initialLayout = vm.SelectedRecentLayout;
        string initialDensity = vm.SelectedDrawerDensity;

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

            // Toggle Open Drawer On Search Hover
            vm.OpenDrawerOnSearchHover = !initialHover;
            Assert.Equal(!initialHover, SettingsService.Instance.Current.OpenDrawerOnSearchHover);

            // Change Recent Apps Layout
            vm.SelectedRecentLayout = "List";
            Assert.Equal(RecentItemsViewMode.List, SettingsService.Instance.Current.RecentViewMode);

            vm.SelectedRecentLayout = "Grid";
            Assert.Equal(RecentItemsViewMode.Grid, SettingsService.Instance.Current.RecentViewMode);

            // Change Drawer Density
            vm.SelectedDrawerDensity = "Compact";
            Assert.Equal(DrawerDensity.Compact, SettingsService.Instance.Current.DrawerDensity);

            vm.SelectedDrawerDensity = "Relaxed";
            Assert.Equal(DrawerDensity.Relaxed, SettingsService.Instance.Current.DrawerDensity);
        }
        finally
        {
            vm.AutoHideSidebar = initialAutoHide;
            vm.ShowWorkspacesButton = initialWorkspaces;
            vm.ShowAddShortcutButton = initialAdd;
            vm.ShowPowerButton = initialPower;
            vm.ShowRecentApps = initialRecent;
            vm.OpenDrawerOnSearchHover = initialHover;
            vm.SelectedRecentLayout = initialLayout;
            vm.SelectedDrawerDensity = initialDensity;
        }
    }
}

