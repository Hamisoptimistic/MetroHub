using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MetroHub.Widgets.Catalog.Habit;

/// <summary>
/// Interaction logic for HabitWidgetView.xaml.
/// Handles keyboard shortcuts on day cells and setup input without violating MVVM.
/// </summary>
public partial class HabitWidgetView : UserControl
{
    public HabitWidgetView()
    {
        InitializeComponent();
    }

    private void HabitNameInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (DataContext is HabitWidgetViewModel vm && vm.StartHabitCommand.CanExecute(null))
            {
                vm.StartHabitCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            if (DataContext is HabitWidgetViewModel vm && vm.CancelEditHabitCommand.CanExecute(null))
            {
                vm.CancelEditHabitCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private void DayButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HabitDayViewModel day } && DataContext is HabitWidgetViewModel vm)
        {
            if (e.Key is Key.Space or Key.Enter)
            {
                vm.ToggleDayCommand.Execute(day);
                e.Handled = true;
            }
            else if (e.Key == Key.F)
            {
                vm.MarkDayFailedCommand.Execute(day);
                e.Handled = true;
            }
            else if (e.Key is Key.Delete or Key.Back)
            {
                vm.ClearDayCommand.Execute(day);
                e.Handled = true;
            }
        }
    }
}
