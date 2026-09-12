using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MetroHub.Core.Models;
using MetroHub.Widgets.Messaging;
using MetroHub.Widgets.Serialization;

namespace MetroHub.Widgets.Catalog.Clock;

/// <summary>
/// ViewModel for the native Clock Widget.
/// Pure MVVM with zero WPF UI element references.
/// Supports 12h/24h toggle, full date formatting ("Friday, 11 September"),
/// responsive adaptive layouts (2x2 up to 8x4), and zero background drain when hidden.
/// </summary>
public partial class ClockWidgetViewModel : WidgetViewModelBase
{
    private DispatcherTimer? _timer;

    [ObservableProperty]
    private string _hoursString = string.Empty;

    [ObservableProperty]
    private string _minutesString = string.Empty;

    [ObservableProperty]
    private string _timeString = string.Empty;

    [ObservableProperty]
    private string _amPmString = string.Empty;

    [ObservableProperty]
    private string _fullDateString = string.Empty;

    [ObservableProperty]
    private string _dayOfWeekString = string.Empty;

    [ObservableProperty]
    private bool _is24HourFormat = false;

    [ObservableProperty]
    private ClockFontFace _fontFace = ClockFontFace.SegoeUI;

    [ObservableProperty]
    private string _clockFontFamily = "Segoe UI Variable Display, Segoe UI Variable, Segoe UI, sans-serif";

    [ObservableProperty]
    private string _clockFontWeight = "SemiBold";

    public bool IsWideOnly => Model.SpanX <= 4 && Model.SpanY <= 2;
    public bool IsBanner => Model.SpanX >= 8 && Model.SpanY <= 2;
    public bool IsHero => Model.SpanY >= 4;

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new List<WidgetSize>
    {
        WidgetSize.Wide,      // 4x2
        WidgetSize.Large,     // 4x4
        WidgetSize.LargeWide, // 6x4
        WidgetSize.Banner,    // 8x2
        WidgetSize.Mega       // 8x4
    };

    public ClockWidgetViewModel(TileModel model) : base(model)
    {
        // If an existing model was saved as 2x2, migrate to Wide 4x2
        if (model.SpanX <= 2 && model.SpanY <= 2)
        {
            model.SpanX = 4;
            model.SpanY = 2;
        }

        Model.PropertyChanged += OnModelPropertyChanged;
        IsActive = true;

        LoadSettings(model.SettingsJson);
        UpdateTime();
        StartTimer();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TileModel.SpanX) or nameof(TileModel.SpanY))
        {
            OnPropertyChanged(nameof(IsWideOnly));
            OnPropertyChanged(nameof(IsBanner));
            OnPropertyChanged(nameof(IsHero));
        }
    }

    private void StartTimer()
    {
        if (_timer == null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += (s, e) => UpdateTime();
        }
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer?.Stop();
    }

    public override void Pause()
    {
        StopTimer();
    }

    public override void Resume()
    {
        UpdateTime();
        StartTimer();
    }

    public void UpdateTime()
    {
        var now = DateTime.Now;

        int hour = now.Hour;
        if (!Is24HourFormat)
        {
            hour = hour % 12;
            if (hour == 0) hour = 12;
            HoursString = hour.ToString();
        }
        else
        {
            HoursString = hour.ToString("D2");
        }

        MinutesString = now.ToString("mm", CultureInfo.InvariantCulture);
        TimeString = $"{HoursString}:{MinutesString}";
        AmPmString = string.Empty;
        FullDateString = now.ToString("dddd, d MMMM", CultureInfo.InvariantCulture);
        DayOfWeekString = now.ToString("dddd", CultureInfo.InvariantCulture);
    }

    protected override void LoadSettings(string? settingsJson)
    {
        var settings = WidgetSerializer.Deserialize<ClockWidgetSettings>(settingsJson);
        if (settings != null)
        {
            Is24HourFormat = settings.Is24HourFormat;
            FontFace = settings.FontFace;
        }
        UpdateFontProperties();
    }

    public override void SaveSettings()
    {
        var settings = new ClockWidgetSettings
        {
            Is24HourFormat = Is24HourFormat,
            FontFace = FontFace
        };
        Model.TargetPath = "clock";
        Model.SettingsJson = WidgetSerializer.Serialize(settings);
        MainWindow.Current?.SaveGroupsAndLayout();
    }

    public void SetTimeFormat(bool is24Hour)
    {
        Is24HourFormat = is24Hour;
        UpdateTime();
        SaveSettings();
    }

    public void SetFontFace(ClockFontFace fontFace)
    {
        FontFace = fontFace;
        UpdateFontProperties();
        SaveSettings();
    }

    private void UpdateFontProperties()
    {
        ClockFontFamily = FontFace switch
        {
            ClockFontFace.Monoton => "pack://application:,,,/MetroHub;component/Assets/Fonts/#Monoton, Segoe UI",
            ClockFontFace.PixelifySans => "pack://application:,,,/MetroHub;component/Assets/Fonts/#Pixelify Sans, Segoe UI",
            ClockFontFace.Doto => "pack://application:,,,/MetroHub;component/Assets/Fonts/#Doto, Segoe UI",
            _ => "Segoe UI Variable Display, Segoe UI Variable, Segoe UI, sans-serif"
        };

        ClockFontWeight = FontFace switch
        {
            ClockFontFace.Monoton => "Normal",
            ClockFontFace.Doto => "Bold",
            _ => "SemiBold"
        };
    }

    [RelayCommand]
    public void Toggle24HourFormat()
    {
        SetTimeFormat(!Is24HourFormat);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopTimer();
            _timer = null;
            Model.PropertyChanged -= OnModelPropertyChanged;
        }

        base.Dispose(disposing);
    }
}
