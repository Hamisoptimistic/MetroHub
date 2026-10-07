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
            Description = "Startup, window behavior, system tray",
            Symbol = SymbolRegular.Power24
        });

        Categories.Add(new SettingsCategoryItem
        {
            Id = "Personalization",
            Title = "Personalization",
            Description = "Themes, Wallpapers, Animations",
            Symbol = SymbolRegular.PaintBucket24
        });

        Categories.Add(new SettingsCategoryItem
        {
            Id = "Canvas",
            Title = "Workspaces & Sidebar",
            Description = "Canvas layout, rail behavior, scroll",
            Symbol = SymbolRegular.Grid24
        });

        Categories.Add(new SettingsCategoryItem
        {
            Id = "Shortcuts",
            Title = "Keyboard Shortcuts",
            Description = "Global hotkey, navigation Shortcuts",
            Symbol = SymbolRegular.Keyboard24
        });

        Categories.Add(new SettingsCategoryItem
        {
            Id = "Widgets",
            Title = "Widgets",
            Description = "Widget Defaults",
            Symbol = SymbolRegular.AppGeneric24
        });

        Categories.Add(new SettingsCategoryItem
        {
            Id = "About",
            Title = "About",
            Description = "App updates",
            Symbol = SymbolRegular.Info24
        });
    }

    [RelayCommand]
    private void SelectCategory(SettingsCategoryItem? category)
    {
        if (category == null) return;
        // Phase 2 will navigate to detail view for this category
    }
}
