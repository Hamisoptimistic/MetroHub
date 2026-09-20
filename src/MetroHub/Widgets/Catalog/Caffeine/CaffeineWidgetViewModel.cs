using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetroHub.Core.Display;
using MetroHub.Core.Models;
using MetroHub.Core.Power;
using MetroHub.Widgets.Serialization;
using Wpf.Ui.Controls;

namespace MetroHub.Widgets.Catalog.Caffeine;

/// <summary>
/// High-performance ViewModel for the Caffeine & Light widget.
/// Manages system power sleep prevention and hardware GPU display gamma warmth
/// with zero idle CPU, dormant background timers, and full multi-instance ref-counting.
/// </summary>
public sealed partial class CaffeineWidgetViewModel : WidgetViewModelBase
{
    private readonly PowerAwakeService _powerService;
    private readonly NightLightService _nightLightService;

    private System.Threading.Timer? _uiTimer;
    private bool _isHubVisible = true;
    private string _lastFormattedCountdown = string.Empty;
    private CaffeineWidgetSettings _settings = new();

    #region Frozen Indicator Brushes (Freezable Hygiene)

    private static readonly Brush ActiveCaffeineBrush = CreateFrozenBrush(Color.FromRgb(0x00, 0xE6, 0x76));    // Electric Green
    private static readonly Brush ActiveNightLightBrush = CreateFrozenBrush(Color.FromRgb(0xFF, 0xB9, 0x00));  // Amber Warmth
    private static readonly Brush InactiveTileBrush = CreateFrozenBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)); // Subtle Translucent

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }

    #endregion

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new[]
    {
        WidgetSize.Banner3, // 8x3 Default (504x184 px)
        WidgetSize.Mega     // 8x4
    };

    #region Navigation & Tile Strip

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCaffeinePanel))]
    [NotifyPropertyChangedFor(nameof(IsNightLightPanel))]
    private string _currentPanel = "Caffeine";

    public bool IsCaffeinePanel => string.Equals(CurrentPanel, "Caffeine", StringComparison.OrdinalIgnoreCase);
    public bool IsNightLightPanel => string.Equals(CurrentPanel, "NightLight", StringComparison.OrdinalIgnoreCase);

    public SymbolRegular CaffeineTileSymbol => SymbolRegular.DrinkCoffee24;
    public SymbolRegular NightLightTileSymbol => SymbolRegular.Lightbulb24;

    public Brush CaffeineTileIndicatorBrush => _powerService.IsAwakeActive ? ActiveCaffeineBrush : InactiveTileBrush;
    public Brush NightLightTileIndicatorBrush => _nightLightService.IsEnabled ? ActiveNightLightBrush : InactiveTileBrush;

    public bool IsSteamAnimating => IsAwakeActive && _isHubVisible;
    public bool IsWarmSunAnimating => IsNightLightEnabled && _isHubVisible;

    #endregion

    #region Caffeine / Awake State & Properties

    public AwakeMode CurrentMode => _powerService.Mode;

    public bool IsModePassive => CurrentMode == AwakeMode.Passive;
    public bool IsModeIndefinite => CurrentMode == AwakeMode.Indefinite;
    public bool IsModeTimed => CurrentMode == AwakeMode.Timed;
    public bool IsAwakeActive => _powerService.IsAwakeActive;

    public string ModeTitle => "Mode";

    public string ModeSubtitle => "Choose how your PC stays awake.";

    public string ModeButtonText => CurrentMode switch
    {
        AwakeMode.Indefinite => "Indefinite",
        AwakeMode.Timed => HasCountdown ? CountdownText : "Timed",
        _ => "Off"
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCountdown))]
    [NotifyPropertyChangedFor(nameof(ModeButtonText))]
    private string _countdownText = string.Empty;

    public bool HasCountdown => !string.IsNullOrEmpty(CountdownText);

    public bool KeepScreenOn
    {
        get => _powerService.KeepScreenOn;
        set
        {
            if (_powerService.KeepScreenOn == value) return;
            _powerService.SetKeepScreenOn(value);
            _settings.KeepScreenOn = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    public bool CanToggleKeepScreenOn => IsAwakeActive;

    #endregion

    #region Night Light State & Properties

    public bool IsNightLightEnabled
    {
        get => _nightLightService.IsEnabled;
        set
        {
            if (_nightLightService.IsEnabled == value) return;
            _nightLightService.SetEnabled(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(NightLightTileIndicatorBrush));
            OnPropertyChanged(nameof(NightLightStatusSubtitle));
        }
    }

    public double NightLightStrength
    {
        get => _nightLightService.Strength;
        set
        {
            double clamped = Math.Clamp(value, 0.0, 100.0);
            if (Math.Abs(_nightLightService.Strength - clamped) < 0.2) return;
            _nightLightService.SetStrength(clamped);
            _settings.NightLightStrength = clamped;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(NightLightStrengthText));
            OnPropertyChanged(nameof(NightLightStatusSubtitle));
        }
    }

    public string NightLightStrengthText => $"{Math.Round(NightLightStrength)}%";

    public bool IsNightLightSupported => _nightLightService.IsSupported;

    public string NightLightStatusTitle => "Night Light";

    public string NightLightStatusSubtitle
    {
        get
        {
            if (!IsNightLightSupported)
            {
                return "Hardware gamma unsupported (HDR or driver limit)";
            }
            return "Reduce blue light to ease eye strain";
        }
    }

    public string NightLightSupportTooltip => IsNightLightSupported
        ? "Adjust display warmth to reduce eye strain in low-light environments"
        : "Display gamma ramps are disabled on this monitor (often caused by Windows HDR or hybrid GPU driver limits)";

    #endregion

    public CaffeineWidgetViewModel(TileModel model) : base(model)
    {
        _powerService = PowerAwakeService.Instance;
        _nightLightService = NightLightService.Instance;

        // Named event handlers for strict leak-free unsubscription
        _powerService.StateChanged += OnPowerServiceStateChanged;
        _nightLightService.StateChanged += OnNightLightServiceStateChanged;

        // Register client token for multi-widget ref-counting
        _powerService.RegisterClient(this);
        _nightLightService.RegisterClient(this);

        LoadSettings(model.SettingsJson);

        // Initialize 1 Hz UI countdown timer (fires off-UI thread via System.Threading.Timer)
        _uiTimer = new System.Threading.Timer(OnTimerTick, null, Timeout.Infinite, Timeout.Infinite);

        if (_powerService.Mode == AwakeMode.Timed)
        {
            StartUiCountdownTimer();
        }
    }

    #region Settings Serialization

    protected override void LoadSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return;
        try
        {
            var parsed = WidgetSerializer.Deserialize<CaffeineWidgetSettings>(settingsJson);
            if (parsed != null)
            {
                _settings = parsed;
                if (!string.IsNullOrEmpty(_settings.LastPanel))
                {
                    CurrentPanel = _settings.LastPanel;
                }
                _nightLightService.SetStrength(_settings.NightLightStrength);
                _powerService.SetKeepScreenOn(_settings.KeepScreenOn);
            }
        }
        catch { }
    }

    public override void SaveSettings()
    {
        try
        {
            _settings.LastPanel = CurrentPanel;
            Model.SettingsJson = WidgetSerializer.Serialize(_settings);
        }
        catch { }
    }

    partial void OnCurrentPanelChanged(string value)
    {
        SaveSettings();
    }

    #endregion

    #region Commands

    [RelayCommand]
    private void SetPassiveMode()
    {
        StopUiCountdownTimer();
        UpdateCountdownText(string.Empty);
        _powerService.SetPassive();
    }

    [RelayCommand]
    private void SetIndefiniteMode()
    {
        StopUiCountdownTimer();
        UpdateCountdownText(string.Empty);
        _powerService.SetIndefinite();
    }

    [RelayCommand]
    private void SetTimedMode(object? minutesParameter)
    {
        int minutes = 30;
        if (minutesParameter is int m)
        {
            minutes = m;
        }
        else if (minutesParameter is string s && int.TryParse(s, out int parsed))
        {
            minutes = parsed;
        }

        _settings.SelectedMinutes = minutes;
        SaveSettings();

        _powerService.SetTimed(TimeSpan.FromMinutes(minutes));
        StartUiCountdownTimer();
        OnTimerTick(null);
    }

    [RelayCommand]
    private void ToggleKeepScreenOn()
    {
        KeepScreenOn = !KeepScreenOn;
    }

    [RelayCommand]
    private void ToggleNightLight()
    {
        IsNightLightEnabled = !IsNightLightEnabled;
    }

    [RelayCommand]
    private async Task TurnOffDisplays()
    {
        await _powerService.TurnOffDisplaysAsync();
    }

    #endregion

    #region Event Handlers & Timer Logic

    private void OnPowerServiceStateChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(CurrentMode));
            OnPropertyChanged(nameof(ModeTitle));
            OnPropertyChanged(nameof(ModeSubtitle));
            OnPropertyChanged(nameof(ModeButtonText));
            OnPropertyChanged(nameof(IsModePassive));
            OnPropertyChanged(nameof(IsModeIndefinite));
            OnPropertyChanged(nameof(IsModeTimed));
            OnPropertyChanged(nameof(IsAwakeActive));
            OnPropertyChanged(nameof(IsSteamAnimating));
            OnPropertyChanged(nameof(CanToggleKeepScreenOn));
            OnPropertyChanged(nameof(KeepScreenOn));
            OnPropertyChanged(nameof(CaffeineTileIndicatorBrush));

            if (_powerService.Mode == AwakeMode.Timed)
            {
                StartUiCountdownTimer();
                OnTimerTick(null);
            }
            else
            {
                StopUiCountdownTimer();
                UpdateCountdownText(string.Empty);
            }
        });
    }

    private void OnNightLightServiceStateChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(IsNightLightEnabled));
            OnPropertyChanged(nameof(IsWarmSunAnimating));
            OnPropertyChanged(nameof(NightLightStrength));
            OnPropertyChanged(nameof(NightLightStrengthText));
            OnPropertyChanged(nameof(IsNightLightSupported));
            OnPropertyChanged(nameof(NightLightStatusSubtitle));
            OnPropertyChanged(nameof(NightLightTileIndicatorBrush));
        });
    }

    private void StartUiCountdownTimer()
    {
        if (!_isHubVisible) return;
        _uiTimer?.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    private void StopUiCountdownTimer()
    {
        _uiTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void OnTimerTick(object? _)
    {
        if (!_isHubVisible) return;

        var remaining = _powerService.TimeRemaining;
        if (!remaining.HasValue || remaining.Value <= TimeSpan.Zero)
        {
            UpdateCountdownText(string.Empty);
            return;
        }

        var rem = remaining.Value;
        string formatted = rem.TotalHours >= 1
            ? $"{Math.Floor(rem.TotalHours)}h {rem.Minutes:D2}m {rem.Seconds:D2}s"
            : $"{rem.Minutes:D2}m {rem.Seconds:D2}s";

        UpdateCountdownText(formatted);
    }

    private void UpdateCountdownText(string formatted)
    {
        // Rule 9 (Diff Before Dispatch): Marshal only when the formatted countdown text changes
        if (string.Equals(_lastFormattedCountdown, formatted, StringComparison.Ordinal)) return;
        _lastFormattedCountdown = formatted;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            CountdownText = formatted;
            OnPropertyChanged(nameof(ModeButtonText));
        });
    }

    #endregion

    #region Lifecycle: Pause & Resume

    public override void Pause()
    {
        base.Pause();
        _isHubVisible = false;
        // Stop UI countdown ticks completely while hidden in tray
        StopUiCountdownTimer();
        OnPropertyChanged(nameof(IsSteamAnimating));
        OnPropertyChanged(nameof(IsWarmSunAnimating));
    }

    public override void Resume()
    {
        base.Resume();
        _isHubVisible = true;

        // Refresh UI state synchronously on restore
        OnPropertyChanged(nameof(CurrentMode));
        OnPropertyChanged(nameof(ModeTitle));
        OnPropertyChanged(nameof(ModeSubtitle));
        OnPropertyChanged(nameof(ModeButtonText));
        OnPropertyChanged(nameof(IsAwakeActive));
        OnPropertyChanged(nameof(IsSteamAnimating));
        OnPropertyChanged(nameof(IsNightLightEnabled));
        OnPropertyChanged(nameof(IsWarmSunAnimating));
        OnPropertyChanged(nameof(NightLightStrength));
        OnPropertyChanged(nameof(CaffeineTileIndicatorBrush));
        OnPropertyChanged(nameof(NightLightTileIndicatorBrush));

        if (_powerService.Mode == AwakeMode.Timed)
        {
            StartUiCountdownTimer();
            OnTimerTick(null);
        }
    }

    #endregion

    #region Teardown & IDisposable

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SaveSettings();

            // Stop and dispose countdown timer
            StopUiCountdownTimer();
            _uiTimer?.Dispose();
            _uiTimer = null;

            // Unsubscribe named event handlers to eliminate leaks
            _powerService.StateChanged -= OnPowerServiceStateChanged;
            _nightLightService.StateChanged -= OnNightLightServiceStateChanged;

            // Unregister client tokens
            _powerService.UnregisterClient(this);
            _nightLightService.UnregisterClient(this);
        }
        base.Dispose(disposing);
    }

    #endregion
}
