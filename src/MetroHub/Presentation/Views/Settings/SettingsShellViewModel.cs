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

    public SettingsShellViewModel()
    {
        _currentView = new SettingsHomeViewModel(this);
    }

    public void NavigateTo(object view)
    {
        CurrentView = view;
    }

    public void NavigateHome()
    {
        CurrentView = new SettingsHomeViewModel(this);
    }
}
