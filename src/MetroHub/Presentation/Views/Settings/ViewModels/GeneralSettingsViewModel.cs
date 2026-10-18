using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private bool _showTrayIcon = true;

    // --- Windows Hibernation Integration ---
    [ObservableProperty]
    private bool _isHibernateEnabled;

    [ObservableProperty]
    private string _hibernateToggleLabel = "Off";

    [ObservableProperty]
    private string _hibernateStatusDescription = string.Empty;

    // UI-only property with simple names (no brackets, no 'Only')
    [ObservableProperty]
    private string _selectedTargetDisplay = "Follow Mouse";

    public ObservableCollection<string> TargetDisplays { get; } = new()
    {
        "Follow Mouse",
        "Primary Display"
    };

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
            UpdateHibernateState();
        }
        finally
        {
            _isInitializing = false;
        }

        PowerService.HibernateStateChanged += UpdateHibernateState;
    }

    private void UpdateHibernateState()
    {
        bool supported = PowerService.IsHibernateSupported();
        _isInitializing = true;
        try
        {
            IsHibernateEnabled = supported;
        }
        finally
        {
            _isInitializing = false;
        }

        HibernateToggleLabel = supported ? "On" : "Off";
        HibernateStatusDescription = supported
            ? "Hibernation is active and ready to use"
            : "Currently disabled in Windows (Requires admin approval to enable)";
    }

    async partial void OnIsHibernateEnabledChanged(bool value)
    {
        if (_isInitializing) return;

        HibernateToggleLabel = value ? "On" : "Off";
        HibernateStatusDescription = value
            ? "Hibernation is active and ready to use"
            : "Currently disabled in Windows (Requires admin approval to enable)";

        if (value)
        {
            bool success = await PowerService.RequestEnableHibernateAsync();
            if (!success)
            {
                _isInitializing = true;
                IsHibernateEnabled = false;
                _isInitializing = false;
            }
        }
        else
        {
            bool success = await PowerService.RequestDisableHibernateAsync();
            if (!success)
            {
                _isInitializing = true;
                IsHibernateEnabled = true;
                _isInitializing = false;
            }
        }

        UpdateHibernateState();
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
}
