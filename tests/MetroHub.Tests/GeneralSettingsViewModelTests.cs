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
        Assert.Equal(SettingsService.Instance.Current.CloseOnLaunch, vm.CloseOnLaunch);
        Assert.Equal(SettingsService.Instance.Current.DismissOnDeactivate, vm.DismissOnDeactivate);
    }

    [Fact]
    public void GeneralSettingsViewModel_MutatingProperties_UpdatesUnderlyingSettings()
    {
        var vm = new GeneralSettingsViewModel();
        bool initialClose = vm.CloseOnLaunch;
        bool initialMinimized = vm.StartMinimized;
        bool initialDismiss = vm.DismissOnDeactivate;

        try
        {
            vm.CloseOnLaunch = !initialClose;
            Assert.Equal(!initialClose, SettingsService.Instance.Current.CloseOnLaunch);

            vm.StartMinimized = !initialMinimized;
            Assert.Equal(!initialMinimized, SettingsService.Instance.Current.StartMinimized);

            vm.DismissOnDeactivate = !initialDismiss;
            Assert.Equal(!initialDismiss, SettingsService.Instance.Current.DismissOnDeactivate);
        }
        finally
        {
            // Restore original state
            vm.CloseOnLaunch = initialClose;
            vm.StartMinimized = initialMinimized;
            vm.DismissOnDeactivate = initialDismiss;
        }
    }
}

