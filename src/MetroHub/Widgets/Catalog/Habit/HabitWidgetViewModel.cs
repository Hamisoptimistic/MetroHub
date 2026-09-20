using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetroHub.Core.Models;

namespace MetroHub.Widgets.Catalog.Habit;

/// <summary>
/// ViewModel for an individual Habit Tracker widget instance (8x6 Huge).
/// Manages month navigation, 42-day grid generation, seamless polygon backplate metrics,
/// streak calculation with grace period, and debounced persistence.
/// </summary>
public partial class HabitWidgetViewModel : WidgetViewModelBase
{
    public override IReadOnlyList<WidgetSize> AllowedSizes => new[]
    {
        WidgetSize.Huge // 8x6 layout
    };

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private HabitWidgetSettings _settings = new();
    private DateTime _currentDisplayMonth;
    private DateTime _lastCheckedDate = DateTime.Today;
    private System.Threading.Timer? _midnightTimer;

    [ObservableProperty]
    private string _habitName = string.Empty;

    [ObservableProperty]
    private string _iconSymbol = "TargetArrow24";

    [ObservableProperty]
    private string _accentColorHex = "#0B9E76";

    [ObservableProperty]
    private bool _isSetupMode;

    [ObservableProperty]
    private string _setupInputName = string.Empty;

    [ObservableProperty]
    private string _setupSelectedIcon = "TargetArrow24";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStreak))]
    private int _activeStreak;

    [ObservableProperty]
    private string _activeStreakText = "No streak";

    public bool HasStreak => ActiveStreak > 0;

    [ObservableProperty]
    private string _displayMonthYear = string.Empty;

    [ObservableProperty]
    private int _firstDayOffset;

    [ObservableProperty]
    private int _daysInMonth;

    public ObservableCollection<HabitDayViewModel> Days { get; } = new();

    public IReadOnlyList<string> DayOfWeekHeaders { get; }

    public HabitWidgetViewModel(TileModel model) : base(model)
    {
        // Enforce 8x6 Huge dimensions and auto-upgrade any existing 8x4 tiles
        if (model.SpanX != 8 || model.SpanY != 6)
        {
            model.SpanX = 8;
            model.SpanY = 6;
        }

        // Generate culture-aware day of week headers
        var firstDayOfWeek = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var headers = new List<string>(7);
        for (int i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)(((int)firstDayOfWeek + i) % 7);
            string abbrev = CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day);
            headers.Add(abbrev.Length > 2 ? abbrev[..2] : abbrev);
        }
        DayOfWeekHeaders = headers;

        DateTime now = DateTime.Today;
        _currentDisplayMonth = new DateTime(now.Year, now.Month, 1);
        _lastCheckedDate = now;

        LoadSettings(model.SettingsJson);

        if (string.IsNullOrWhiteSpace(_settings.HabitName))
        {
            IsSetupMode = true;
            SetupInputName = string.Empty;
            SetupSelectedIcon = "TargetArrow24";
        }
        else
        {
            HabitName = _settings.HabitName;
            model.Title = HabitName;
            IconSymbol = _settings.IconSymbol;
            AccentColorHex = _settings.AccentColorHex;
            IsSetupMode = false;
        }

        RebuildMonthGrid();
        RecalculateStreak();
        StartMidnightTimer();
    }

    protected override void LoadSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            _settings = new HabitWidgetSettings();
            return;
        }

        try
        {
            _settings = JsonSerializer.Deserialize<HabitWidgetSettings>(settingsJson, _jsonOpts) ?? new HabitWidgetSettings();
            _settings.DayStates ??= new();
        }
        catch
        {
            _settings = new HabitWidgetSettings();
        }
    }

    public override void SaveSettings()
    {
        _settings.HabitName = HabitName;
        _settings.IconSymbol = IconSymbol;
        _settings.AccentColorHex = AccentColorHex;

        try
        {
            Model.SettingsJson = JsonSerializer.Serialize(_settings);
            Model.Title = HabitName;

            var mw = MainWindow.Current;
            if (mw != null)
            {
                if (mw.Dispatcher.CheckAccess())
                {
                    mw.SaveGroupsAndLayout();
                }
                else
                {
                    mw.Dispatcher.Invoke(() => mw.SaveGroupsAndLayout());
                }
            }
        }
        catch
        {
            // Suppress serialization error
        }
    }

    public override void Pause()
    {
        base.Pause();
        StopMidnightTimer();
        SaveSettings();
    }

    public override void Resume()
    {
        base.Resume();
        CheckDateRollover();
        StartMidnightTimer();
    }

    private void StartMidnightTimer()
    {
        TimeSpan timeToMidnight = (DateTime.Today.AddDays(1) - DateTime.Now).Add(TimeSpan.FromSeconds(1));
        if (timeToMidnight <= TimeSpan.Zero || timeToMidnight > TimeSpan.FromDays(1))
        {
            timeToMidnight = TimeSpan.FromSeconds(1);
        }

        if (_midnightTimer == null)
        {
            _midnightTimer = new System.Threading.Timer(_ =>
            {
                CheckDateRollover();
            }, null, timeToMidnight, Timeout.InfiniteTimeSpan);
        }
        else
        {
            _midnightTimer.Change(timeToMidnight, Timeout.InfiniteTimeSpan);
        }
    }

    private void StopMidnightTimer()
    {
        _midnightTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    public void CheckDateRollover()
    {
        DateTime today = DateTime.Today;
        if (today == _lastCheckedDate) return;

        _lastCheckedDate = today;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(ApplyDateRollover);
        }
        else
        {
            ApplyDateRollover();
        }
    }

    private void ApplyDateRollover()
    {
        DateTime today = DateTime.Today;

        // If currently displaying the month that just ended, advance to the new month
        bool wasViewingPriorMonth = _currentDisplayMonth.Year == _lastCheckedDate.AddDays(-1).Year &&
                                   _currentDisplayMonth.Month == _lastCheckedDate.AddDays(-1).Month;

        if (wasViewingPriorMonth && (_currentDisplayMonth.Year != today.Year || _currentDisplayMonth.Month != today.Month))
        {
            _currentDisplayMonth = new DateTime(today.Year, today.Month, 1);
            RebuildMonthGrid();
        }
        else if (Days.Count == 42)
        {
            // Update in-place without destroying and recreating 42 UI elements
            foreach (var day in Days)
            {
                day.IsToday = day.Date.Date == today;
                day.IsFuture = day.Date.Date > today;
            }
        }
        else
        {
            RebuildMonthGrid();
        }

        RecalculateStreak();
        StartMidnightTimer();
    }

    public void RebuildMonthGrid()
    {
        string rawMonth = _currentDisplayMonth.ToString("MMMM", CultureInfo.CurrentCulture);
        string titleMonth = char.ToUpper(rawMonth[0]) + (rawMonth.Length > 1 ? rawMonth.Substring(1).ToLower() : "");
        DisplayMonthYear = $"{titleMonth} {_currentDisplayMonth.Year}";

        Days.Clear();

        DateTime today = DateTime.Today;
        var firstDayOfWeek = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

        DateTime firstDayOfMonth = _currentDisplayMonth;
        FirstDayOffset = ((int)firstDayOfMonth.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
        DaysInMonth = DateTime.DaysInMonth(_currentDisplayMonth.Year, _currentDisplayMonth.Month);

        DateTime startDate = firstDayOfMonth.AddDays(-FirstDayOffset);

        // Always generate exactly 42 cells (6 rows × 7 columns) for seamless stability
        for (int i = 0; i < 42; i++)
        {
            DateTime cellDate = startDate.AddDays(i);
            int key = cellDate.Year * 10000 + cellDate.Month * 100 + cellDate.Day;

            bool isCurrentMonth = cellDate.Month == _currentDisplayMonth.Month && cellDate.Year == _currentDisplayMonth.Year;
            bool isToday = cellDate.Date == today;
            bool isFuture = cellDate.Date > today;

            HabitDayState state = HabitDayState.Unmarked;
            if (_settings.DayStates.TryGetValue(key, out byte stateByte))
            {
                state = (HabitDayState)stateByte;
            }

            Days.Add(new HabitDayViewModel(cellDate, isCurrentMonth, isToday, isFuture, state));
        }
    }

    public void RecalculateStreak()
    {
        DateTime today = DateTime.Today;
        int todayKey = today.Year * 10000 + today.Month * 100 + today.Day;

        _settings.DayStates.TryGetValue(todayKey, out byte todayStateByte);
        var todayState = (HabitDayState)todayStateByte;

        // If today is explicitly marked Failed, streak is broken immediately
        if (todayState == HabitDayState.Failed)
        {
            ActiveStreak = 0;
            ActiveStreakText = "No streak";
            return;
        }

        int count = 0;
        DateTime checkDate;

        if (todayState == HabitDayState.Done)
        {
            count = 1;
            checkDate = today.AddDays(-1);
        }
        else
        {
            // Today is Unmarked -> grace period: check starts from yesterday backwards
            checkDate = today.AddDays(-1);
        }

        while (true)
        {
            int key = checkDate.Year * 10000 + checkDate.Month * 100 + checkDate.Day;
            if (_settings.DayStates.TryGetValue(key, out byte stateByte) && (HabitDayState)stateByte == HabitDayState.Done)
            {
                count++;
                checkDate = checkDate.AddDays(-1);
            }
            else
            {
                // Any Failed day or past Unmarked day breaks the consecutive chain
                break;
            }
        }

        ActiveStreak = count;
        ActiveStreakText = count switch
        {
            0 => "No streak",
            1 => "1 Day",
            _ => $"{count} Days"
        };
    }

    [RelayCommand]
    public void PrevMonth()
    {
        _currentDisplayMonth = _currentDisplayMonth.AddMonths(-1);
        RebuildMonthGrid();
    }

    [RelayCommand]
    public void NextMonth()
    {
        _currentDisplayMonth = _currentDisplayMonth.AddMonths(1);
        RebuildMonthGrid();
    }

    [RelayCommand]
    public void JumpToCurrentMonth()
    {
        DateTime now = DateTime.Today;
        _currentDisplayMonth = new DateTime(now.Year, now.Month, 1);
        RebuildMonthGrid();
    }

    [RelayCommand]
    public void ToggleDay(HabitDayViewModel? day)
    {
        if (day == null || !day.IsClickable) return;

        // 3-state left click cycle:
        // 1st click: Done (Green) -> 2nd click: Failed (Red) -> 3rd click: Unmarked (Undo)
        HabitDayState newState = day.State switch
        {
            HabitDayState.Unmarked => HabitDayState.Done,
            HabitDayState.Done => HabitDayState.Failed,
            HabitDayState.Failed => HabitDayState.Unmarked,
            _ => HabitDayState.Done
        };

        SetDayState(day, newState);
    }

    public void SetDayState(HabitDayViewModel day, HabitDayState state)
    {
        day.State = state;

        if (state == HabitDayState.Unmarked)
        {
            _settings.DayStates.Remove(day.DateKey);
        }
        else
        {
            _settings.DayStates[day.DateKey] = (byte)state;
        }

        RecalculateStreak();
        SaveSettings();
    }

    [RelayCommand]
    public void MarkDayFailed(HabitDayViewModel? day)
    {
        if (day == null || !day.IsClickable) return;
        SetDayState(day, HabitDayState.Failed);
    }

    [RelayCommand]
    public void ClearDay(HabitDayViewModel? day)
    {
        if (day == null || !day.IsClickable) return;
        SetDayState(day, HabitDayState.Unmarked);
    }

    [RelayCommand]
    public void ToggleToday()
    {
        DateTime today = DateTime.Today;
        int todayKey = today.Year * 10000 + today.Month * 100 + today.Day;

        HabitDayViewModel? todayVm = null;
        foreach (var d in Days)
        {
            if (d.DateKey == todayKey)
            {
                todayVm = d;
                break;
            }
        }

        if (todayVm != null)
        {
            ToggleDay(todayVm);
        }
        else
        {
            _settings.DayStates.TryGetValue(todayKey, out byte currentByte);
            var current = (HabitDayState)currentByte;
            var nextState = current switch
            {
                HabitDayState.Unmarked => HabitDayState.Done,
                HabitDayState.Done => HabitDayState.Failed,
                HabitDayState.Failed => HabitDayState.Unmarked,
                _ => HabitDayState.Done
            };

            if (nextState == HabitDayState.Unmarked)
                _settings.DayStates.Remove(todayKey);
            else
                _settings.DayStates[todayKey] = (byte)nextState;

            RecalculateStreak();
            SaveSettings();
        }
    }

    [RelayCommand]
    public void StartHabit()
    {
        if (string.IsNullOrWhiteSpace(SetupInputName)) return;

        HabitName = SetupInputName.Trim();
        Model.Title = HabitName;
        IconSymbol = string.IsNullOrWhiteSpace(SetupSelectedIcon) ? "TargetArrow24" : SetupSelectedIcon;
        IsSetupMode = false;

        SaveSettings();
    }

    [RelayCommand]
    public void EditHabit()
    {
        SetupInputName = HabitName;
        SetupSelectedIcon = IconSymbol;
        IsSetupMode = true;
    }

    [RelayCommand]
    public void CancelEditHabit()
    {
        if (!string.IsNullOrWhiteSpace(HabitName))
        {
            IsSetupMode = false;
        }
    }

    [RelayCommand]
    public void ResetAllData()
    {
        _settings.DayStates.Clear();
        RecalculateStreak();
        RebuildMonthGrid();
        SaveSettings();
    }

    [RelayCommand]
    public void SelectIcon(string? icon)
    {
        if (!string.IsNullOrWhiteSpace(icon))
        {
            SetupSelectedIcon = icon;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopMidnightTimer();
            _midnightTimer?.Dispose();
            _midnightTimer = null;
        }

        base.Dispose(disposing);
    }
}
