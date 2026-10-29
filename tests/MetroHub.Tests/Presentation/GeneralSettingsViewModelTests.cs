using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Presentation.Views.Settings.ViewModels;
using Xunit;

namespace MetroHub.Tests;

public class GeneralSettingsViewModelTests
{
    [Fact]
    public void GeneralSettingsViewModel_InitializesPropertiesFromSettings()
    {
        var vm = new GeneralSettingsViewModel();

        Assert.Equal(SettingsService.Instance.Current.StartMinimized, vm.StartMinimized);
        Assert.Equal("Follow Mouse", vm.SelectedTargetDisplay);
        Assert.True(vm.ShowTrayIcon);
        Assert.Contains("Follow Mouse", vm.TargetDisplays);
        Assert.Contains("Primary Display", vm.TargetDisplays);
    }

    [Fact]
    public void GeneralSettingsViewModel_MutatingProperties_UpdatesUnderlyingSettings()
    {
        var vm = new GeneralSettingsViewModel();
        bool initialMinimized = vm.StartMinimized;

        try
        {
            vm.StartMinimized = !initialMinimized;
            Assert.Equal(!initialMinimized, SettingsService.Instance.Current.StartMinimized);

            vm.SelectedTargetDisplay = "Primary Display";
            Assert.Equal("Primary Display", vm.SelectedTargetDisplay);

            vm.ShowTrayIcon = false;
            Assert.False(vm.ShowTrayIcon);
        }
        finally
        {
            vm.StartMinimized = initialMinimized;
        }
    }
}
