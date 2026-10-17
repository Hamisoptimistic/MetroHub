using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MetroHub.Presentation.Views.Settings.ViewModels;

/// <summary>
/// ViewModel for State 2: Two-Pane Detail Layout Shell.
/// Coordinates the Left 220px Category Rail and the Right Content Viewport.
/// </summary>
public partial class SettingsDetailLayoutViewModel : ObservableObject
{
    private readonly SettingsShellViewModel _shell;

    [ObservableProperty]
    private SettingsCategoryItem? _selectedCategory;

    [ObservableProperty]
    private object? _currentPageViewModel;

    public ObservableCollection<SettingsCategoryItem> Categories { get; }

    private readonly System.Collections.Generic.Dictionary<string, object> _pageCache = new();

    public SettingsDetailLayoutViewModel(
        SettingsShellViewModel shell,
        ObservableCollection<SettingsCategoryItem> categories,
        SettingsCategoryItem? initialCategory = null)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        Categories = categories ?? throw new ArgumentNullException(nameof(categories));
        SelectedCategory = initialCategory ?? (categories.Count > 0 ? categories[0] : null);
        if (SelectedCategory != null && CurrentPageViewModel == null)
        {
            LoadCategoryPage(SelectedCategory);
        }
    }

    partial void OnSelectedCategoryChanged(SettingsCategoryItem? value)
    {
        if (value == null) return;
        LoadCategoryPage(value);
    }

    private void LoadCategoryPage(SettingsCategoryItem category)
    {
        if (!_pageCache.TryGetValue(category.Id, out var vm))
        {
            vm = category.Id switch
            {
                "General" => new GeneralSettingsViewModel(),
                _ => null
            };

            if (vm != null)
            {
                _pageCache[category.Id] = vm;
            }
        }

        CurrentPageViewModel = vm;
    }

    [RelayCommand]
    private void BackToHome()
    {
        _shell.NavigateHome();
    }
}
