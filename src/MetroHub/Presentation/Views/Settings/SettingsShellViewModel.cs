using CommunityToolkit.Mvvm.ComponentModel;
using MetroHub.Presentation.Views.Settings.ViewModels;

namespace MetroHub.Presentation.Views.Settings;

/// <summary>
/// Shell coordinator ViewModel for MetroHub Settings.
/// Manages CurrentView navigation between State 1 (Home) and State 2 (Detail).
/// </summary>
public partial class SettingsShellViewModel : ObservableObject
{
    [ObservableProperty]
    private object _currentView;

    private readonly SettingsHomeViewModel _homeViewModel;
    private SettingsDetailLayoutViewModel? _detailViewModel;

    public SettingsShellViewModel()
    {
        _homeViewModel = new SettingsHomeViewModel(this);
        _currentView = _homeViewModel;
    }

    public bool IsInDetailView => CurrentView is not SettingsHomeViewModel;

    public void NavigateTo(object view)
    {
        CurrentView = view;
    }

    public void NavigateHome()
    {
        CurrentView = _homeViewModel;
    }

    public void NavigateToCategory(SettingsCategoryItem category)
    {
        _detailViewModel ??= new SettingsDetailLayoutViewModel(this, _homeViewModel.Categories, category);
        _detailViewModel.SelectedCategory = category;
        CurrentView = _detailViewModel;
    }
}
