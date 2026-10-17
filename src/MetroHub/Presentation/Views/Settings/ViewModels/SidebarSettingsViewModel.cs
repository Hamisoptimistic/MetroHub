using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MetroHub.Core.Models;
using MetroHub.Core.Services;

namespace MetroHub.Presentation.Views.Settings.ViewModels;

/// <summary>
/// ViewModel for Pillar 4: Sidebar & Apps (Navigation Rails & Drawer).
/// </summary>
public partial class SidebarSettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private bool _isInitializing;

    [ObservableProperty]
    private bool _autoHideSidebar;

    [ObservableProperty]
    private bool _showWorkspacesButton = true;

    [ObservableProperty]
    private bool _showAddShortcutButton = true;

    [ObservableProperty]
    private bool _showPowerButton = true;

    [ObservableProperty]
    private bool _showRecentApps = true;

    [ObservableProperty]
    private string _selectedRecentLayout = "Grid";

    // C# 14 collection expression
    public ObservableCollection<string> RecentLayouts { get; } = ["Grid", "List"];

    public SidebarSettingsViewModel(SettingsService? settingsService = null)
    {
        _settingsService = settingsService ?? SettingsService.Instance;
        var current = _settingsService.Current;

        _isInitializing = true;
        try
        {
            _autoHideSidebar = current.SidebarAutoHide;
            _showWorkspacesButton = current.ShowWorkspacesButton;
            _showAddShortcutButton = current.ShowAddShortcutButton;
            _showPowerButton = current.ShowPowerButton;
            _showRecentApps = current.ShowRecentApps;
            _selectedRecentLayout = current.RecentViewMode == RecentItemsViewMode.List ? "List" : "Grid";
        }
        finally
        {
            _isInitializing = false;
        }
    }

    partial void OnAutoHideSidebarChanged(bool value)
    {
        if (_isInitializing) return;
        _settingsService.Current.SidebarAutoHide = value;
        _settingsService.Current.SidebarPinned = !value;
        _settingsService.NotifyChanged(nameof(AppSettings.SidebarAutoHide));
        _settingsService.NotifyChanged(nameof(AppSettings.SidebarPinned));
    }

    partial void OnShowWorkspacesButtonChanged(bool value)
    {
        if (_isInitializing) return;
        _settingsService.Current.ShowWorkspacesButton = value;
        _settingsService.NotifyChanged(nameof(AppSettings.ShowWorkspacesButton));
    }

    partial void OnShowAddShortcutButtonChanged(bool value)
    {
        if (_isInitializing) return;
        _settingsService.Current.ShowAddShortcutButton = value;
        _settingsService.NotifyChanged(nameof(AppSettings.ShowAddShortcutButton));
    }

    partial void OnShowPowerButtonChanged(bool value)
    {
        if (_isInitializing) return;
        _settingsService.Current.ShowPowerButton = value;
        _settingsService.NotifyChanged(nameof(AppSettings.ShowPowerButton));
    }

    partial void OnShowRecentAppsChanged(bool value)
    {
        if (_isInitializing) return;
        _settingsService.Current.ShowRecentApps = value;
        _settingsService.NotifyChanged(nameof(AppSettings.ShowRecentApps));
    }

    partial void OnSelectedRecentLayoutChanged(string value)
    {
        if (_isInitializing) return;
        var mode = value == "List" ? RecentItemsViewMode.List : RecentItemsViewMode.Grid;
        _settingsService.Current.RecentViewMode = mode;
        _settingsService.NotifyChanged(nameof(AppSettings.RecentViewMode));
    }
}

