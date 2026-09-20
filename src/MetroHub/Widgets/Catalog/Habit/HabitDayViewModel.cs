using System;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MetroHub.Widgets.Catalog.Habit;

/// <summary>
/// Lightweight ViewModel representing a single day cell in the monthly habit matrix.
/// Pure data-driven state with zero UI brush allocations.
/// </summary>
public partial class HabitDayViewModel : ObservableObject
{
    public DateTime Date { get; }
    public int DateKey { get; }
    public int DayNumber => Date.Day;
    public string DayText => Date.Day.ToString();
    public bool IsCurrentMonth { get; }
    public bool IsToday { get; }
    public bool IsFuture { get; }
    public bool IsClickable => !IsFuture && IsCurrentMonth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(IsTodayUnmarked))]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private HabitDayState _state = HabitDayState.Unmarked;


    public bool IsDone => State == HabitDayState.Done;
    public bool IsFailed => State == HabitDayState.Failed;
    public bool IsTodayUnmarked => IsToday && State == HabitDayState.Unmarked;

    public HabitDayViewModel(DateTime date, bool isCurrentMonth, bool isToday, bool isFuture, HabitDayState state)
    {
        Date = date;
        DateKey = date.Year * 10000 + date.Month * 100 + date.Day;
        IsCurrentMonth = isCurrentMonth;
        IsToday = isToday;
        IsFuture = isFuture;
        _state = state;
    }

    public string ToolTipText
    {
        get
        {
            string dateStr = Date.ToString("dddd, MMM d");
            if (!IsCurrentMonth) return dateStr;
            if (IsFuture) return $"{dateStr} (Upcoming)";
            return State switch
            {
                HabitDayState.Done => $"{dateStr} · Completed ✓",
                HabitDayState.Failed => $"{dateStr} · Failed ✕",
                _ => IsToday ? $"{dateStr} · Today (Click to complete)" : $"{dateStr} · Missed"
            };
        }
    }
}
