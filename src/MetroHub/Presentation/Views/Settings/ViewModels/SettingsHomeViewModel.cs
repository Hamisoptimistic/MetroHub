using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Controls;

namespace MetroHub.Presentation.Views.Settings.ViewModels;

public sealed class SettingsCategoryItem
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public SymbolRegular Symbol { get; init; } = SymbolRegular.Settings24;
}

/// <summary>
/// ViewModel for State 1: Settings Home Category Grid.
/// Exposes the 6 high-level categories and navigation command.
/// </summary>
public partial class SettingsHomeViewModel : ObservableObject
{
    private readonly SettingsShellViewModel? _shell;

    public ObservableCollection<SettingsCategoryItem> Categories { get; } = new();

    public SettingsHomeViewModel(SettingsShellViewModel? shell = null)
    {
        _shell = shell;

        Categories.Add(new SettingsCategoryItem
        {
            Id = "General",
            Title = "General",
            Description = "Startup, window behavior, tray",
            Symbol = SymbolRegular.Power24
        });

        Categories.Add(new SettingsCategoryItem
        {
            Id = "Appearance",
            Title = "Appearance",
            Description = "Themes, materials, wallpapers",
            Symbol = SymbolRegular.PaintBucket24
        });

        Categories.Add(new SettingsCategoryItem
        {
            Id = "Workspaces",
            Title = "Workspaces & Canvas",
            Description = "Layout orientation, groups, grid",
            Symbol = SymbolRegular.Grid24
        });

        Categories.Add(new SettingsCategoryItem
        {
            Id = "Sidebar",
            Title = "Sidebar & Apps",
            Description = "Navigation rail, quick links, drawer",
            Symbol = SymbolRegular.AppsListDetail24
        });

        Categories.Add(new SettingsCategoryItem
        {
            Id = "Search",
            Title = "Search & Indexing",
            Description = "Everything engine, FZF, sources",
            Symbol = SymbolRegular.Search24
        });

        Categories.Add(new SettingsCategoryItem
        {
            Id = "Widgets",
            Title = "Widgets",
            Description = "Clock, weather, calendar, refresh",
            Symbol = SymbolRegular.AppGeneric24
        });

        Categories.Add(new SettingsCategoryItem
        {
            Id = "Integrations",
            Title = "Integrations & Companion",
            Description = "Browser extension, media, port",
            Symbol = SymbolRegular.AppsAddIn24
        });

        Categories.Add(new SettingsCategoryItem
        {
            Id = "Shortcuts",
            Title = "Shortcuts & Hotkeys",
            Description = "Global hotkey, workspace jumps",
            Symbol = SymbolRegular.Keyboard24
        });

        Categories.Add(new SettingsCategoryItem
        {
            Id = "About",
            Title = "About & Maintenance",
            Description = "Updates, version, logs, backup",
            Symbol = SymbolRegular.Info24
        });
    }

    [RelayCommand]
    private void SelectCategory(SettingsCategoryItem? category)
    {
        if (category == null || _shell == null) return;
        _shell.NavigateToCategory(category);
    }
}
