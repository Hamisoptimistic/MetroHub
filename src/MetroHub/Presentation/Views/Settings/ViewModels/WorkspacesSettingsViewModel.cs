using CommunityToolkit.Mvvm.ComponentModel;
using MetroHub.Core.Models;
using MetroHub.Core.Services;

namespace MetroHub.Presentation.Views.Settings.ViewModels;

/// <summary>
/// ViewModel for Pillar 3: Workspaces &amp; Canvas.
/// </summary>
public sealed partial class WorkspacesSettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private bool _isInitializing;

    [ObservableProperty]
    private bool _showRunningAppIndicators = true;

    public WorkspacesSettingsViewModel(SettingsService? settingsService = null)
    {
        _settingsService = settingsService ?? SettingsService.Instance;
        var current = _settingsService.Current;

        _isInitializing = true;
        try
        {
            _showRunningAppIndicators = current.ShowRunningAppIndicators;
        }
        finally
        {
            _isInitializing = false;
        }
    }

    partial void OnShowRunningAppIndicatorsChanged(bool value)
    {
        if (_isInitializing) return;
        _settingsService.Current.ShowRunningAppIndicators = value;
        _settingsService.NotifyChanged(nameof(AppSettings.ShowRunningAppIndicators));
    }
}
