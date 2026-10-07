using System.Windows.Controls;

namespace MetroHub.Presentation.Views.Settings.Pages;

/// <summary>
/// Pure XAML view for Settings State 1 (Home Category Grid).
/// Zero business logic; all bindings and commands are driven by SettingsHomeViewModel.
/// </summary>
public partial class SettingsHomeView : UserControl
{
    public SettingsHomeView()
    {
        InitializeComponent();
    }
}
