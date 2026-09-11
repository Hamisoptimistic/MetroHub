using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MetroHub.Widgets.Catalog.Calendar;

/// <summary>
/// Observable ViewModel representing a single day cell within the 42-day calendar matrix.
/// </summary>
public partial class CalendarDayViewModel : ObservableObject
{
    public DateTime Date { get; }

    public int DayNumber => Date.Day;
    public string DayText => Date.Day.ToString();

    public bool IsCurrentMonth { get; }
    public bool IsToday { get; }

    [ObservableProperty]
    private bool _isSelected;

    public bool IsWeekend => Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    public CalendarDayViewModel(DateTime date, bool isCurrentMonth, bool isToday, bool isSelected = false)
    {
        Date = date;
        IsCurrentMonth = isCurrentMonth;
        IsToday = isToday;
        _isSelected = isSelected;
    }
}
