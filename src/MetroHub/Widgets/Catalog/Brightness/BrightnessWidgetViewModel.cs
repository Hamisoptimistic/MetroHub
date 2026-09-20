using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetroHub.Core.Display;
using MetroHub.Core.Models;
using MetroHub.Widgets.Serialization;

namespace MetroHub.Widgets.Catalog.Brightness;

public partial class MonitorItemViewModel : ObservableObject
{
    private readonly Action<string, int> _onBrightnessChanged;
    private bool _isUpdatingInternally;

    public string Id { get; init; } = string.Empty;
    public string DeviceName { get; init; } = string.Empty;
    public string FriendlyName { get; init; } = string.Empty;
    public int DisplayIndex { get; init; }
    public bool IsInternal { get; init; }
    public bool IsPrimary { get; init; }
    public bool IsSupported { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrightnessPercentText))]
    [NotifyPropertyChangedFor(nameof(BrightnessGlyph))]
    private double _brightness = 50.0; // 0 - 100

    [ObservableProperty]
    private bool _isCurrentCursorMonitor;

    public string BrightnessPercentText => IsSupported ? $"{Math.Round(Brightness)}%" : "--";

    public string BrightnessGlyph
    {
        get
        {
            if (!IsSupported) return "\uE7BA"; // Warning / Unsupported
            return "\uE706"; // Brightness sun glyph
        }
    }

    public string DeviceTypeGlyph => IsInternal ? "\uE7F8" : "\uE7F4"; // Laptop vs Desktop Monitor

    public MonitorItemViewModel(Action<string, int> onBrightnessChanged)
    {
        _onBrightnessChanged = onBrightnessChanged;
    }

    partial void OnBrightnessChanged(double value)
    {
        if (_isUpdatingInternally) return;
        _onBrightnessChanged(Id, (int)Math.Round(Math.Clamp(value, 0.0, 100.0)));
    }

    public void UpdateBrightnessDirectly(double newBrightness)
    {
        _isUpdatingInternally = true;
        try
        {
            Brightness = Math.Clamp(newBrightness, 0.0, 100.0);
        }
        finally
        {
            _isUpdatingInternally = false;
        }
    }
}

public sealed partial class BrightnessWidgetViewModel : WidgetViewModelBase
{
    private readonly MonitorBrightnessService _brightnessService;
    private System.Threading.Timer? _cursorTimer;
    private bool _isHubVisible = true;
    private bool _isUpdatingMasterInternally;
    private string? _pinnedCursorMonitorId;

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new[]
    {
        WidgetSize.Mega,       // 8x4
        WidgetSize.SlimBanner  // 8x1
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MasterBrightnessPercentText))]
    [NotifyPropertyChangedFor(nameof(MasterGlyph))]
    private double _masterBrightness = 80.0;

    public string MasterBrightnessPercentText => $"{Math.Round(MasterBrightness)}%";

    public string MasterGlyph => IsNightMode ? "\uE708" : "\uE706"; // Moon (Night) vs Sun (Day)

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LinkGlyph))]
    [NotifyPropertyChangedFor(nameof(LinkTooltip))]
    private bool _isLinked = true;

    public string LinkGlyph => IsLinked ? "\uE71B" : "\uE77A"; // Link vs Unlink
    public string LinkTooltip => IsLinked ? "Monitors Linked (Sync All)" : "Monitors Independent (Individual)";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MasterGlyph))]
    [NotifyPropertyChangedFor(nameof(NightModeTooltip))]
    private bool _isNightMode;

    public string NightModeTooltip => IsNightMode ? "Switch to Day Mode (80%)" : "Switch to Night Mode (25%)";

    [ObservableProperty]
    private double _dayBrightness = 80.0;

    [ObservableProperty]
    private double _nightBrightness = 25.0;

    [ObservableProperty]
    private string _activeMonitorName = "Display";

    [ObservableProperty]
    private string _activeMonitorIcon = "\uE7F4";

    private string? _activeMonitorId;

    public bool IsCompactMode => Model.SpanY <= 1;

    public ObservableCollection<MonitorItemViewModel> Monitors { get; } = new();

    public BrightnessWidgetViewModel(TileModel model) : base(model)
    {
        _brightnessService = MonitorBrightnessService.Instance;

        _brightnessService.MonitorsChanged += OnBrightnessServiceMonitorsChanged;

        Model.PropertyChanged += OnModelPropertyChanged;

        _cursorTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                if (!_isHubVisible || Monitors.Count <= 1) return;
                var lastMonitors = _brightnessService.LastMonitors;
                if (lastMonitors.Count == 0) return;

                string? cursorMonitorId = _brightnessService.GetCurrentCursorMonitorId(lastMonitors);
                if (string.IsNullOrEmpty(cursorMonitorId)) return;

                // Diff before dispatch: check if active monitor changed
                if (string.Equals(_activeMonitorId, cursorMonitorId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                Application.Current?.Dispatcher.InvokeAsync(() => ProcessCursorMonitorChange(cursorMonitorId));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BrightnessWidget] Cursor tracking error: {ex.Message}");
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        RefreshAll();
    }

    protected override void LoadSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return;
        try
        {
            var settings = WidgetSerializer.Deserialize<BrightnessWidgetSettings>(settingsJson);
            if (settings != null)
            {
                IsLinked = settings.IsLinked;
                IsNightMode = settings.IsNightMode;
                DayBrightness = settings.DayBrightness > 0 ? settings.DayBrightness : 80.0;
                NightBrightness = settings.NightBrightness > 0 ? settings.NightBrightness : 25.0;
            }
        }
        catch { }
    }

    public override void SaveSettings()
    {
        var settings = new BrightnessWidgetSettings
        {
            IsLinked = IsLinked,
            IsNightMode = IsNightMode,
            DayBrightness = DayBrightness,
            NightBrightness = NightBrightness
        };
        Model.TargetPath = "brightness";
        Model.SettingsJson = WidgetSerializer.Serialize(settings);
    }

    partial void OnMasterBrightnessChanged(double value)
    {
        if (_isUpdatingMasterInternally) return;
        int targetInt = (int)Math.Round(Math.Clamp(value, 0.0, 100.0));

        if (IsLinked)
        {
            foreach (var monitor in Monitors)
            {
                if (monitor.IsSupported)
                {
                    monitor.UpdateBrightnessDirectly(targetInt);
                    _brightnessService.SetBrightnessThrottled(monitor.Id, (uint)targetInt);
                }
            }
        }
        else
        {
            var target = Monitors.FirstOrDefault(m => string.Equals(m.Id, _activeMonitorId, StringComparison.OrdinalIgnoreCase))
                         ?? Monitors.FirstOrDefault(m => m.IsCurrentCursorMonitor)
                         ?? Monitors.FirstOrDefault(m => m.IsPrimary)
                         ?? Monitors.FirstOrDefault();
            if (target != null && target.IsSupported)
            {
                target.UpdateBrightnessDirectly(targetInt);
                _brightnessService.SetBrightnessThrottled(target.Id, (uint)targetInt);
            }
        }

        // Remember user brightness adjustment in current mode
        if (IsNightMode)
        {
            NightBrightness = targetInt;
        }
        else
        {
            DayBrightness = targetInt;
        }
    }

    [RelayCommand]
    public void ToggleNightMode()
    {
        IsNightMode = !IsNightMode;
        double target = IsNightMode ? NightBrightness : DayBrightness;

        _isUpdatingMasterInternally = true;
        try
        {
            MasterBrightness = target;
        }
        finally
        {
            _isUpdatingMasterInternally = false;
        }

        OnMasterBrightnessChanged(target);
        SaveSettings();
    }

    [RelayCommand]
    public void ToggleLink()
    {
        IsLinked = !IsLinked;
        if (IsLinked)
        {
            // When re-linked, align all monitors to current MasterBrightness
            int targetInt = (int)Math.Round(MasterBrightness);
            foreach (var monitor in Monitors)
            {
                if (monitor.IsSupported)
                {
                    monitor.UpdateBrightnessDirectly(targetInt);
                    _brightnessService.SetBrightnessThrottled(monitor.Id, (uint)targetInt);
                }
            }
        }
        SaveSettings();
    }

    [RelayCommand]
    public void SelectMonitor(MonitorItemViewModel? monitor)
    {
        if (monitor == null) return;

        _activeMonitorId = monitor.Id;
        ActiveMonitorName = monitor.FriendlyName;
        ActiveMonitorIcon = monitor.DeviceTypeGlyph;

        // Pin current cursor screen so the 250ms timer does not override manual user selection
        var lastMonitors = _brightnessService.LastMonitors;
        _pinnedCursorMonitorId = _brightnessService.GetCurrentCursorMonitorId(lastMonitors);

        if (!IsLinked)
        {
            _isUpdatingMasterInternally = true;
            try
            {
                MasterBrightness = monitor.Brightness;
            }
            finally
            {
                _isUpdatingMasterInternally = false;
            }
        }
    }

    private void OnMonitorBrightnessChanged(string monitorId, int brightness)
    {
        if (IsLinked)
        {
            // Sync all monitors to this brightness
            foreach (var m in Monitors)
            {
                if (m.Id != monitorId && m.IsSupported)
                {
                    m.UpdateBrightnessDirectly(brightness);
                    _brightnessService.SetBrightnessThrottled(m.Id, (uint)brightness);
                }
            }

            _isUpdatingMasterInternally = true;
            try
            {
                MasterBrightness = brightness;
            }
            finally
            {
                _isUpdatingMasterInternally = false;
            }
        }
        else
        {
            var active = Monitors.FirstOrDefault(m => string.Equals(m.Id, _activeMonitorId, StringComparison.OrdinalIgnoreCase))
                         ?? Monitors.FirstOrDefault(m => m.IsCurrentCursorMonitor)
                         ?? Monitors.FirstOrDefault();
            if (active?.Id == monitorId)
            {
                _isUpdatingMasterInternally = true;
                try
                {
                    MasterBrightness = brightness;
                }
                finally
                {
                    _isUpdatingMasterInternally = false;
                }
            }
        }

        _brightnessService.SetBrightnessThrottled(monitorId, (uint)brightness);

        if (IsNightMode)
        {
            NightBrightness = brightness;
        }
        else
        {
            DayBrightness = brightness;
        }
    }

    private void ProcessCursorMonitorChange(string cursorMonitorId)
    {
        if (!_isHubVisible || Monitors.Count <= 1) return;

        MonitorItemViewModel? currentUnderCursor = null;

        foreach (var m in Monitors)
        {
            bool isCurrent = string.Equals(m.Id, cursorMonitorId, StringComparison.OrdinalIgnoreCase);
            if (m.IsCurrentCursorMonitor != isCurrent)
            {
                m.IsCurrentCursorMonitor = isCurrent;
            }
            if (isCurrent)
            {
                currentUnderCursor = m;
            }
        }

        if (currentUnderCursor != null && !string.Equals(_activeMonitorId, currentUnderCursor.Id, StringComparison.OrdinalIgnoreCase))
        {
            _activeMonitorId = currentUnderCursor.Id;
            ActiveMonitorName = currentUnderCursor.FriendlyName;
            ActiveMonitorIcon = currentUnderCursor.DeviceTypeGlyph;

            if (!IsLinked)
            {
                _isUpdatingMasterInternally = true;
                try
                {
                    MasterBrightness = currentUnderCursor.Brightness;
                }
                finally
                {
                    _isUpdatingMasterInternally = false;
                }
            }
        }
    }

    public void RefreshAll()
    {
        if (!_isHubVisible) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var monitors = await _brightnessService.GetConnectedMonitorsAsync().ConfigureAwait(false);
                if (!_isHubVisible) return;

                if (Application.Current?.Dispatcher is { } dispatcher)
                {
                    await dispatcher.InvokeAsync(() =>
                    {
                        UpdateMonitorsCollection(monitors);
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BrightnessWidget] RefreshAll error: {ex.Message}");
            }
        });
    }

    private void UpdateMonitorsCollection(IReadOnlyList<MonitorDevice> rawMonitors)
    {
        // Smooth in-place update if monitor list IDs match
        bool sameList = Monitors.Count == rawMonitors.Count &&
                        Monitors.Select(m => m.Id).SequenceEqual(rawMonitors.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);

        if (sameList)
        {
            for (int i = 0; i < rawMonitors.Count; i++)
            {
                var raw = rawMonitors[i];
                var existing = Monitors[i];
                if (Math.Abs(existing.Brightness - raw.CurrentBrightness) > 0.1)
                {
                    existing.UpdateBrightnessDirectly(raw.CurrentBrightness);
                }
            }
        }
        else
        {
            Monitors.Clear();
            foreach (var raw in rawMonitors)
            {
                var item = new MonitorItemViewModel(OnMonitorBrightnessChanged)
                {
                    Id = raw.Id,
                    DeviceName = raw.DeviceName,
                    FriendlyName = raw.FriendlyName,
                    DisplayIndex = raw.DisplayIndex,
                    IsInternal = raw.IsInternal,
                    IsPrimary = raw.IsPrimary,
                    IsSupported = raw.IsSupported
                };
                item.UpdateBrightnessDirectly(raw.CurrentBrightness);
                Monitors.Add(item);
            }
        }

        // Preserve active monitor if still connected, otherwise fallback to primary or first
        var active = Monitors.FirstOrDefault(m => string.Equals(m.Id, _activeMonitorId, StringComparison.OrdinalIgnoreCase))
                     ?? Monitors.FirstOrDefault(m => m.IsPrimary)
                     ?? Monitors.FirstOrDefault();
        if (active != null)
        {
            _activeMonitorId = active.Id;
            ActiveMonitorName = active.FriendlyName;
            ActiveMonitorIcon = active.DeviceTypeGlyph;
            if (!IsLinked)
            {
                _isUpdatingMasterInternally = true;
                try
                {
                    MasterBrightness = active.Brightness;
                }
                finally
                {
                    _isUpdatingMasterInternally = false;
                }
            }
        }
    }

    private void OnBrightnessServiceMonitorsChanged(object? sender, IReadOnlyList<MonitorDevice> updatedMonitors)
    {
        if (!_isHubVisible) return;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            UpdateMonitorsCollection(updatedMonitors);
        });
    }

    public override void Pause()
    {
        _isHubVisible = false;
        _cursorTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    public override void Resume()
    {
        _isHubVisible = true;
        _cursorTimer?.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        RefreshAll();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TileModel.SpanX) or nameof(TileModel.SpanY))
        {
            OnPropertyChanged(nameof(IsCompactMode));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Model.PropertyChanged -= OnModelPropertyChanged;
            _brightnessService.MonitorsChanged -= OnBrightnessServiceMonitorsChanged;
            _cursorTimer?.Dispose();
            _cursorTimer = null;
            Monitors.Clear();
        }

        base.Dispose(disposing);
    }
}
