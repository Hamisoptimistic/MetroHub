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
public partial class ClockWidgetViewModel : WidgetViewModelBase, IRecipient<HubVisibilityChangedMessage>
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

    public void Receive(HubVisibilityChangedMessage message)
    {
        if (message.IsVisible)
        {
            Resume();
        }
        else
        {
            Pause();
        }
    }

    public void UpdateTime()
    {
        var now = DateTime.Now;

        HoursString = Is24HourFormat ? now.ToString("HH") : now.ToString("%h");
        MinutesString = now.ToString("mm");
        TimeString = $"{HoursString}:{MinutesString}";
        AmPmString = Is24HourFormat ? string.Empty : now.ToString("tt", CultureInfo.InvariantCulture).ToUpperInvariant();
        FullDateString = now.ToString("dddd, d MMMM", CultureInfo.InvariantCulture);
        DayOfWeekString = now.ToString("dddd", CultureInfo.InvariantCulture);
    }

    protected override void LoadSettings(string? settingsJson)
    {
        var settings = WidgetSerializer.Deserialize<ClockWidgetSettings>(settingsJson);
        if (settings != null)
        {
            Is24HourFormat = settings.Is24HourFormat;
        }
    }

    public override void SaveSettings()
    {
        var settings = new ClockWidgetSettings
        {
            Is24HourFormat = Is24HourFormat
        };
        Model.TargetPath = "clock";
        Model.SettingsJson = WidgetSerializer.Serialize(settings);
        MainWindow.Current?.SaveGroupsAndLayout();
    }

    [RelayCommand]
    public void Toggle24HourFormat()
    {
        Is24HourFormat = !Is24HourFormat;
        UpdateTime();
        SaveSettings();
    }
}
