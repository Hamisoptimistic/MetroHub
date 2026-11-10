using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MetroHub.Core.Models;
using MetroHub.Core.Services;

namespace MetroHub.Presentation.Views.Settings.ViewModels;

/// <summary>
/// ViewModel for Pillar 4: Sidebar & Apps (Navigation Rails & Drawer).
/// </summary>
public sealed partial class SidebarSettingsViewModel : ObservableObject
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

    [ObservableProperty]
    private string _selectedDrawerDensity = "Relaxed";

    public ObservableCollection<string> DrawerDensities { get; } = ["Relaxed", "Compact"];

    [ObservableProperty]
    private bool _openDrawerOnSearchHover;

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
            _selectedDrawerDensity = current.DrawerDensity == DrawerDensity.Compact ? "Compact" : "Relaxed";
            _openDrawerOnSearchHover = current.OpenDrawerOnSearchHover;
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

    partial void OnSelectedDrawerDensityChanged(string value)
    {
        if (_isInitializing) return;
        var density = value == "Compact" ? DrawerDensity.Compact : DrawerDensity.Relaxed;
        _settingsService.Current.DrawerDensity = density;
        _settingsService.NotifyChanged(nameof(AppSettings.DrawerDensity));
    }

    partial void OnOpenDrawerOnSearchHoverChanged(bool value)
    {
        if (_isInitializing) return;
        _settingsService.Current.OpenDrawerOnSearchHover = value;
        _settingsService.NotifyChanged(nameof(AppSettings.OpenDrawerOnSearchHover));
    }
}

