using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MetroHub.Core.Models;
using MetroHub.Widgets.Messaging;

namespace MetroHub.Widgets.Catalog.Calendar;

/// <summary>
/// ViewModel for the native Windows 10 style Calendar Widget.
/// Operates at fixed 8x6 dimensions (504x376px) featuring vertical month navigation
/// and a 42-day contiguous month grid with zero UI thread overhead when hidden.
/// </summary>
public partial class CalendarWidgetViewModel : WidgetViewModelBase
{
    private DispatcherTimer? _midnightTimer;

    [ObservableProperty]
    private DateTime _displayMonth;

    [ObservableProperty]
    private string _monthYearTitle = string.Empty;

    [ObservableProperty]
    private bool _isViewingCurrentMonth = true;

    [ObservableProperty]
    private IReadOnlyList<CalendarDayViewModel> _days = Array.Empty<CalendarDayViewModel>();

    [ObservableProperty]
    private int _firstDayOffset;

    [ObservableProperty]
    private int _daysInMonth = 30;

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new List<WidgetSize>
    {
        WidgetSize.Huge // 8x6 (504x376px)
    };

    public CalendarWidgetViewModel(TileModel model) : base(model)
    {
        // Enforce 8x6 size for this widget
        if (model.SpanX != 8 || model.SpanY != 6)
        {
            model.SpanX = 8;
            model.SpanY = 6;
        }

        IsActive = true;
        _displayMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        RebuildCalendar();
        StartMidnightTimer();
    }

    private void StartMidnightTimer()
    {
        if (_midnightTimer == null)
        {
            _midnightTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMinutes(15)
            };
            _midnightTimer.Tick += (s, e) =>
            {
                // Refresh if the date has changed
                if (IsViewingCurrentMonth)
                {
                    RebuildCalendar();
                }
            };
        }
        _midnightTimer.Start();
    }

    public override void Pause()
    {
        _midnightTimer?.Stop();
    }

    public override void Resume()
    {
        RebuildCalendar();
        _midnightTimer?.Start();
    }

    [RelayCommand]
    public void PreviousMonth()
    {
        DisplayMonth = DisplayMonth.AddMonths(-1);
        RebuildCalendar();
    }

    [RelayCommand]
    public void NextMonth()
    {
        DisplayMonth = DisplayMonth.AddMonths(1);
        RebuildCalendar();
    }

    [RelayCommand]
    public void ResetToToday()
    {
        DisplayMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        RebuildCalendar();
    }

    [RelayCommand]
    public void SelectDay(CalendarDayViewModel? day)
    {
        if (day == null) return;

        foreach (var d in Days)
        {
            d.IsSelected = (d == day);
        }
    }

    private void RebuildCalendar()
    {
        var today = DateTime.Today;
        IsViewingCurrentMonth = (DisplayMonth.Year == today.Year && DisplayMonth.Month == today.Month);

        // Format Title, e.g. "March 2024"
        MonthYearTitle = DisplayMonth.ToString("MMMM yyyy", CultureInfo.CurrentCulture);

        // Calculate starting day for standard Sunday-first 7-column calendar
        var firstDayOfMonth = new DateTime(DisplayMonth.Year, DisplayMonth.Month, 1);
        int dayOfWeekOffset = (int)firstDayOfMonth.DayOfWeek; // Sunday = 0
        FirstDayOffset = dayOfWeekOffset;
        DaysInMonth = DateTime.DaysInMonth(DisplayMonth.Year, DisplayMonth.Month);
        var gridStartDate = firstDayOfMonth.AddDays(-dayOfWeekOffset);

        var daysList = new List<CalendarDayViewModel>(42);
        for (int i = 0; i < 42; i++)
        {
            var date = gridStartDate.AddDays(i);
            bool isCurrentMonth = (date.Month == DisplayMonth.Month && date.Year == DisplayMonth.Year);
            bool isToday = (date.Date == today);

            daysList.Add(new CalendarDayViewModel(date, isCurrentMonth, isToday));
        }

        Days = daysList;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _midnightTimer?.Stop();
            _midnightTimer = null;
        }

        base.Dispose(disposing);
    }
}
