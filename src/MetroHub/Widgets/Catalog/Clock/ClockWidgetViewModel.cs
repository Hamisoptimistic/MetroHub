using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
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
public sealed partial class ClockWidgetViewModel : WidgetViewModelBase
{
    private static readonly Uri FontBaseUri = new("pack://application:,,,/MetroHub;component/Assets/Fonts/");

    private static readonly FontFamily SegoeUIFontFamily = new("Segoe UI Variable Display, Segoe UI Variable, Segoe UI, sans-serif");
    private static readonly FontFamily MonotonFontFamily = new(FontBaseUri, "./#Monoton, Segoe UI");
    private static readonly FontFamily Digital7FontFamily = new(FontBaseUri, "./#Digital-7, Segoe UI");
    private static readonly FontFamily FffForwardFontFamily = new(FontBaseUri, "./#FFF Forward, Segoe UI");
    private static readonly FontFamily KarnivoreDigitFontFamily = new(FontBaseUri, "./#Karnivore Digit, Segoe UI");
    private static readonly FontFamily NowFontFamily = new(FontBaseUri, "./#Now, Segoe UI");

    private System.Threading.Timer? _timer;

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
    private FontFamily _clockFontFamily = SegoeUIFontFamily;

    [ObservableProperty]
    private string _clockFontWeight = "SemiBold";

    public bool IsWideOnly => Model.SpanX <= 4 && Model.SpanY <= 2;
    public bool IsBanner => Model.SpanX > 4 && Model.SpanY <= 2;
    public bool IsHero => Model.SpanY >= 4;

    /// <summary>
    /// Canonical reference font size for Time elements inside the responsive Viewbox containers.
    /// The Viewbox automatically scales these elements uniformly to mathematically fit the available widget card bounds.
    /// </summary>
    public double ClockTimeFontSize => 100.0;

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
            OnPropertyChanged(nameof(ClockTimeFontSize));
        }
    }

    private int _lastMinute = -1;
    private int _lastHour = -1;

    private void StartTimer()
    {
        if (_timer == null)
        {
            _timer = new System.Threading.Timer(_ =>
            {
                var now = DateTime.Now;
                if (now.Minute == _lastMinute && now.Hour == _lastHour)
                {
                    return;
                }
                Application.Current?.Dispatcher.InvokeAsync(() => UpdateTime());
            }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }
        else
        {
            _timer.Change(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }
    }

    private void StopTimer()
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    public override void Pause()
    {
        StopTimer();
    }

    public override void Resume()
    {
        UpdateTime(force: true);
        StartTimer();
    }

    public void UpdateTime(bool force = false)
    {
        var now = DateTime.Now;
        if (!force && now.Minute == _lastMinute && now.Hour == _lastHour)
        {
            return;
        }

        _lastMinute = now.Minute;
        _lastHour = now.Hour;

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
        UpdateTime(force: true);
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
            ClockFontFace.Monoton => MonotonFontFamily,
            ClockFontFace.Digital7 => Digital7FontFamily,
            ClockFontFace.FffForward => FffForwardFontFamily,
            ClockFontFace.KarnivoreDigit => KarnivoreDigitFontFamily,
            ClockFontFace.Now => NowFontFamily,
            _ => SegoeUIFontFamily
        };

        ClockFontWeight = FontFace switch
        {
            ClockFontFace.Now => "Bold",
            ClockFontFace.Monoton or ClockFontFace.Digital7 or ClockFontFace.FffForward or ClockFontFace.KarnivoreDigit => "Normal",
            _ => "SemiBold"
        };

        OnPropertyChanged(nameof(ClockTimeFontSize));
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
            _timer?.Dispose();
            _timer = null;
            Model.PropertyChanged -= OnModelPropertyChanged;
        }

        base.Dispose(disposing);
    }
}
