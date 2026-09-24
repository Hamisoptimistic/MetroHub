using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MetroHub.Core.Models;
using MetroHub.Widgets.Messaging;

namespace MetroHub.Widgets.Catalog.Calendar;

/// <summary>
/// ViewModel for the native Calendar widget, which renders two personalities from a single tile:
///   • Large (4x4, 248x248px) — a compact "today" date card: weekday pinned to the top edge,
///     a large day numeral owning the middle, month at the bottom edge. No month grid is built.
///   • Huge (8x6, 504x376px)  — the Windows 10 style 42-day month grid (default size).
/// Monthly navigation and the day matrix cost zero UI-thread work when hidden.
/// </summary>
public sealed partial class CalendarWidgetViewModel : WidgetViewModelBase
{
    private System.Threading.Timer? _midnightTimer;
    private DateTime _lastCheckedDate = DateTime.Today;

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

    // ---- Compact 4x4 date card ---------------------------------------------------

    [ObservableProperty]
    private string _todayWeekday = string.Empty;

    [ObservableProperty]
    private string _todayDayNumber = string.Empty;

    [ObservableProperty]
    private string _todayMonthName = string.Empty;

    [ObservableProperty]
    private string _todayYear = string.Empty;

    /// <summary>
    /// True at the compact 4x4 footprint (248x248px), where the widget is a pure date card
    /// and the 42 day cells are never allocated.
    /// </summary>
    public bool IsCompactSize => Model.SpanX == 4 && Model.SpanY == 4;

    /// <summary>
    /// True at the full 8x6 footprint (504x376px), where the 42-day month grid is rendered.
    /// </summary>
    public bool IsFullSize => !IsCompactSize;

    /// <summary>
    /// WidgetCard padding, tightened at 4x4 so the square keeps its breathing room.
    /// </summary>
    public Thickness CardPadding => IsCompactSize
        ? new Thickness(12, 10, 12, 10)
        : new Thickness(18, 16, 18, 16);

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new List<WidgetSize>
    {
        WidgetSize.Large, // 4x4 (248x248px) — date card
        WidgetSize.Huge   // 8x6 (504x376px) — month grid (default)
    };

    public CalendarWidgetViewModel(TileModel model) : base(model)
    {
        // Only the two declared footprints are valid; anything else falls back to the full month view.
        if (!IsCompactSize && (model.SpanX != 8 || model.SpanY != 6))
        {
            model.SpanX = 8;
            model.SpanY = 6;
        }

        Model.PropertyChanged += OnModelPropertyChanged;

        IsActive = true;
        _displayMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        RefreshTodayCard();
        EnsureCalendarBuilt();
        StartMidnightTimer();
    }

    /// <summary>
    /// Flips the size-dependent layout the instant the tile is resized, so the correct branch is
    /// live during (not after) the resize animation, and lazily allocates/releases the day cells.
    /// </summary>
    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(TileModel.SpanX) or nameof(TileModel.SpanY)))
        {
            return;
        }

        OnPropertyChanged(nameof(IsCompactSize));
        OnPropertyChanged(nameof(IsFullSize));
        OnPropertyChanged(nameof(CardPadding));

        if (IsFullSize)
        {
            EnsureCalendarBuilt();
        }
        else
        {
            // Release the 42 day cells while the date card is showing.
            Days = Array.Empty<CalendarDayViewModel>();
        }
    }

    private void StartMidnightTimer()
    {
        if (_midnightTimer == null)
        {
            _midnightTimer = new System.Threading.Timer(_ =>
            {
                if (DateTime.Today != _lastCheckedDate)
                {
                    _lastCheckedDate = DateTime.Today;
                    Application.Current?.Dispatcher.InvokeAsync(() =>
                    {
                        RefreshTodayCard();

                        if (IsFullSize && IsViewingCurrentMonth)
                        {
                            RebuildCalendar();
                        }
                    });
                }
            }, null, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
        }
        else
        {
            _midnightTimer.Change(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
        }
    }

    private void StopMidnightTimer()
    {
        _midnightTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    public override void Pause()
    {
        StopMidnightTimer();
    }

    public override void Resume()
    {
        if (DateTime.Today != _lastCheckedDate)
        {
            _lastCheckedDate = DateTime.Today;
            RefreshTodayCard();

            if (IsFullSize)
            {
                RebuildCalendar();
            }
        }

        StartMidnightTimer();
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

    /// <summary>
    /// Refreshes the three lines of the 4x4 date card. Culture-aware, and cheap enough to
    /// call on every midnight rollover.
    /// </summary>
    private void RefreshTodayCard()
    {
        var today = DateTime.Today;
        var culture = CultureInfo.CurrentCulture;

        TodayWeekday = today.ToString("dddd", culture);
        TodayDayNumber = today.Day.ToString(culture);
        TodayMonthName = today.ToString("MMMM", culture);
        TodayYear = today.Year.ToString(culture);
    }

    /// <summary>
    /// Builds the month grid on demand — used when a tile is resized up from the 4x4 date card.
    /// </summary>
    private void EnsureCalendarBuilt()
    {
        if (IsCompactSize || Days.Count == 42)
        {
            return;
        }

        RebuildCalendar();
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

        // The 4x4 date card draws no month grid, so its 42 day cells are never allocated.
        if (IsCompactSize)
        {
            Days = Array.Empty<CalendarDayViewModel>();
            return;
        }

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
            Model.PropertyChanged -= OnModelPropertyChanged;

            StopMidnightTimer();
            _midnightTimer?.Dispose();
            _midnightTimer = null;
        }

        base.Dispose(disposing);
    }
}
