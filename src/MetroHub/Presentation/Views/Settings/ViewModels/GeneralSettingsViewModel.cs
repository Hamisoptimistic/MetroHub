using CommunityToolkit.Mvvm.ComponentModel;
using MetroHub.Core.Models;
using MetroHub.Core.Services;

namespace MetroHub.Presentation.Views.Settings.ViewModels;

/// <summary>
/// ViewModel for Pillar 1: General (System & Window Behavior).
/// </summary>
public partial class GeneralSettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private bool _isInitializing;

    [ObservableProperty]
    private bool _launchAtStartup;

    [ObservableProperty]
    private bool _startMinimized;

    [ObservableProperty]
    private bool _closeOnLaunch;

    [ObservableProperty]
    private bool _dismissOnDeactivate;

    public GeneralSettingsViewModel(SettingsService? settingsService = null)
    {
        _settingsService = settingsService ?? SettingsService.Instance;
        var current = _settingsService.Current;

        _isInitializing = true;
        try
        {
            bool isRegistryEnabled = StartupService.IsLaunchAtStartupEnabled();
            if (current.LaunchAtStartup != isRegistryEnabled)
            {
                current.LaunchAtStartup = isRegistryEnabled;
                _settingsService.NotifyChanged(nameof(AppSettings.LaunchAtStartup));
            }

            _launchAtStartup = current.LaunchAtStartup;
            _startMinimized = current.StartMinimized;
            _closeOnLaunch = current.CloseOnLaunch;
            _dismissOnDeactivate = current.DismissOnDeactivate;
        }
        finally
        {
            _isInitializing = false;
        }
    }

    partial void OnLaunchAtStartupChanged(bool value)
    {
        if (_isInitializing) return;
        _settingsService.Current.LaunchAtStartup = value;
        StartupService.SetLaunchAtStartup(value);
        _settingsService.NotifyChanged(nameof(AppSettings.LaunchAtStartup));
    }

    partial void OnStartMinimizedChanged(bool value)
    {
        if (_isInitializing) return;
        _settingsService.Current.StartMinimized = value;
        _settingsService.NotifyChanged(nameof(AppSettings.StartMinimized));
    }

    partial void OnCloseOnLaunchChanged(bool value)
    {
        if (_isInitializing) return;
        _settingsService.Current.CloseOnLaunch = value;
        _settingsService.NotifyChanged(nameof(AppSettings.CloseOnLaunch));
    }

    partial void OnDismissOnDeactivateChanged(bool value)
    {
        if (_isInitializing) return;
        _settingsService.Current.DismissOnDeactivate = value;
        _settingsService.NotifyChanged(nameof(AppSettings.DismissOnDeactivate));
    }
}

