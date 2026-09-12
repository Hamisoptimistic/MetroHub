using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Media;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MetroHub.Core.Models;
using MetroHub.Widgets.Messaging;
using MetroHub.Widgets.Serialization;

namespace MetroHub.Widgets.Catalog.Pomodoro;

/// <summary>
/// ViewModel for the Fluent Pomodoro Focus Widget.
/// Follows the exact design and architectural patterns of MediaWidgetViewModel.
/// Supports 4x2 (ring-only), 8x3 (Banner3), and 8x4 (Mega) sizes with zero-drift timing.
/// </summary>
public partial class PomodoroWidgetViewModel : WidgetViewModelBase, IRecipient<HubVisibilityChangedMessage>
{
    private DispatcherTimer? _uiTimer;
    private Timer? _dormantBackgroundTimer;
    private DateTime _targetEndTime;
    private TimeSpan _remainingTime;
    private TimeSpan _totalPhaseDuration;
    private bool _isSettingsLoaded;

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new[]
    {
        WidgetSize.Wide,    // 4x2
        WidgetSize.Banner3, // 8x3
        WidgetSize.Mega     // 8x4
    };

    [ObservableProperty]
    private PomodoroPhase _phase = PomodoroPhase.Focus;

    [ObservableProperty]
    private bool _isRunning = false;

    [ObservableProperty]
    private string _phaseTitle = "Focus";

    [ObservableProperty]
    private string _timeString = "25:00";

    [ObservableProperty]
    private double _progressRatio = 0.0;

    [ObservableProperty]
    private int _currentRound = 1;

    [ObservableProperty]
    private int _totalRounds = 4;

    [ObservableProperty]
    private string _statusString = "⏱ Round 1 of 4 • 🔥 1 Streak";

    [ObservableProperty]
    private int _completedInCycle = 0;

    [ObservableProperty]
    private bool _cycleDot1Filled = false;

    [ObservableProperty]
    private bool _cycleDot2Filled = false;

    [ObservableProperty]
    private bool _cycleDot3Filled = false;

    [ObservableProperty]
    private bool _cycleDot4Filled = false;

    [ObservableProperty]
    private Color _glowColor = Color.FromRgb(0xFF, 0x4B, 0x4B); // Electric Coral

    [ObservableProperty]
    private Brush _glowSolidBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x4B, 0x4B));

    [ObservableProperty]
    private RadialGradientBrush _sensualRadialBrush = CreateSensualBrush(Color.FromRgb(0xFF, 0x4B, 0x4B));

    [ObservableProperty]
    private string _playPauseTooltip = "Start Focus";

    // Config settings
    public int FocusMinutes { get; set; } = 25;
    public int ShortBreakMinutes { get; set; } = 5;
    public int LongBreakMinutes { get; set; } = 15;
    public int LongBreakInterval { get; set; } = 4;
    public bool SoundEnabled { get; set; } = true;

    // Stats
    public int CompletedSessions { get; set; } = 0;
    public int TotalFocusMinutes { get; set; } = 0;
    public int CurrentStreak { get; set; } = 1;

    public bool IsRingOnly => Model.SpanX <= 4 && Model.SpanY <= 2;
    public double RingSize => Model.SpanY >= 4 ? 132.0 : (Model.SpanX <= 4 ? 108.0 : 100.0);

    public PomodoroWidgetViewModel(TileModel model) : base(model)
    {
        // Enforce valid supported sizes
        if (!((model.SpanX == 4 && model.SpanY == 2) ||
              (model.SpanX == 8 && (model.SpanY == 3 || model.SpanY == 4))))
        {
            model.SpanX = 8;
            model.SpanY = 4;
        }

        model.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(TileModel.SpanX) or nameof(TileModel.SpanY))
            {
                OnPropertyChanged(nameof(IsRingOnly));
                OnPropertyChanged(nameof(RingSize));
            }
        };

        LoadSettings(model.SettingsJson);
        _isSettingsLoaded = true;

        _totalPhaseDuration = TimeSpan.FromMinutes(FocusMinutes);
        _remainingTime = _totalPhaseDuration;

        UpdateVisualState();
        UpdateColorTheme();

        _uiTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _uiTimer.Tick += OnUiTimerTick;

        IsActive = true;
    }

    private void OnUiTimerTick(object? sender, EventArgs e)
    {
        if (!IsRunning) return;

        var now = DateTime.UtcNow;
        var diff = _targetEndTime - now;

        if (diff <= TimeSpan.Zero)
        {
            _remainingTime = TimeSpan.Zero;
            UpdateVisualState();
            OnPhaseCompleted();
        }
        else
        {
            _remainingTime = diff;
            UpdateVisualState();
        }
    }

    private void OnPhaseCompleted()
    {
        _uiTimer?.Stop();
        IsRunning = false;

        if (SoundEnabled)
        {
            try
            {
                SystemSounds.Asterisk.Play();
            }
            catch { }
        }

        AdvancePhase(isNaturalCompletion: true);
    }

    [RelayCommand]
    public void SkipPhase()
    {
        CancelDormantBackgroundTimer();
        _uiTimer?.Stop();
        IsRunning = false;

        AdvancePhase(isNaturalCompletion: false);
    }

    private void AdvancePhase(bool isNaturalCompletion)
    {
        if (Phase == PomodoroPhase.Focus)
        {
            if (isNaturalCompletion)
            {
                CompletedSessions++;
                TotalFocusMinutes += FocusMinutes;
                var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
                UpdateStreak(today);
            }

            CompletedInCycle++;

            if (CompletedInCycle >= LongBreakInterval)
            {
                Phase = PomodoroPhase.LongBreak;
                CurrentRound = LongBreakInterval;
                _totalPhaseDuration = TimeSpan.FromMinutes(LongBreakMinutes);
            }
            else
            {
                Phase = PomodoroPhase.ShortBreak;
                // Round number stays the same: this is the break earned for finishing CurrentRound
                _totalPhaseDuration = TimeSpan.FromMinutes(ShortBreakMinutes);
            }
        }
        else if (Phase == PomodoroPhase.ShortBreak)
        {
            // Moving from short break to next focus round
            Phase = PomodoroPhase.Focus;
            CurrentRound = CompletedInCycle + 1;
            _totalPhaseDuration = TimeSpan.FromMinutes(FocusMinutes);
        }
        else // LongBreak
        {
            // Completed full cycle; reset to a fresh round 1
            Phase = PomodoroPhase.Focus;
            CompletedInCycle = 0;
            CurrentRound = 1;
            _totalPhaseDuration = TimeSpan.FromMinutes(FocusMinutes);
        }

        _remainingTime = _totalPhaseDuration;
        UpdateColorTheme();
        UpdateVisualState();
        SaveSettings();
    }

    private void UpdateStreak(string today)
    {
        var settings = WidgetSerializer.Deserialize<PomodoroWidgetSettings>(Model.SettingsJson);
        if (settings?.LastActiveDate != null)
        {
            if (DateTime.TryParse(settings.LastActiveDate, out var lastDate))
            {
                var days = (DateTime.UtcNow.Date - lastDate.Date).Days;
                if (days == 1)
                {
                    CurrentStreak++;
                }
                else if (days > 1)
                {
                    CurrentStreak = 1;
                }
            }
        }
    }

    [RelayCommand]
    public void TogglePlayPause()
    {
        if (IsRunning)
        {
            // Pause
            _uiTimer?.Stop();
            CancelDormantBackgroundTimer();
            IsRunning = false;
        }
        else
        {
            // Start / Resume
            _targetEndTime = DateTime.UtcNow + _remainingTime;
            IsRunning = true;
            _uiTimer?.Start();
        }

        UpdateVisualState();
    }



    [RelayCommand]
    public void Reset()
    {
        CancelDormantBackgroundTimer();
        _uiTimer?.Stop();
        IsRunning = false;

        _remainingTime = _totalPhaseDuration;
        UpdateVisualState();
    }

    public void SetPreset(int focusMinutes, int breakMinutes, int longBreakMinutes = 15)
    {
        CancelDormantBackgroundTimer();
        _uiTimer?.Stop();
        IsRunning = false;

        FocusMinutes = focusMinutes;
        ShortBreakMinutes = breakMinutes;
        LongBreakMinutes = longBreakMinutes;

        Phase = PomodoroPhase.Focus;
        _totalPhaseDuration = TimeSpan.FromMinutes(FocusMinutes);
        _remainingTime = _totalPhaseDuration;

        UpdateColorTheme();
        UpdateVisualState();
        SaveSettings();
    }

    private void UpdateVisualState()
    {
        // 1. Time string (mm:ss)
        int totalSec = (int)Math.Ceiling(_remainingTime.TotalSeconds);
        if (totalSec < 0) totalSec = 0;
        int mins = totalSec / 60;
        int secs = totalSec % 60;
        TimeString = $"{mins:D2}:{secs:D2}";

        // 2. Progress ratio (0.0 to 1.0)
        if (_totalPhaseDuration.TotalSeconds > 0)
        {
            double elapsed = _totalPhaseDuration.TotalSeconds - _remainingTime.TotalSeconds;
            ProgressRatio = Math.Clamp(elapsed / _totalPhaseDuration.TotalSeconds, 0.0, 1.0);
        }
        else
        {
            ProgressRatio = 0.0;
        }

        // 3. Phase title
        PhaseTitle = Phase switch
        {
            PomodoroPhase.Focus => "Focus",
            PomodoroPhase.ShortBreak => "Short Break",
            PomodoroPhase.LongBreak => "Long Break",
            _ => "Focus"
        };

        // 4. Status string
        StatusString = $"⏱ Round {CurrentRound} of {TotalRounds}  •  🔥 {CurrentStreak} Streak";

        // 5. Tooltip
        PlayPauseTooltip = IsRunning
            ? $"Pause {PhaseTitle}"
            : $"Start {PhaseTitle}";

        // 6. Cycle dots: 1 dot per completed focus session in current set
        CycleDot1Filled = CompletedInCycle >= 1;
        CycleDot2Filled = CompletedInCycle >= 2;
        CycleDot3Filled = CompletedInCycle >= 3;
        CycleDot4Filled = CompletedInCycle >= 4;
    }

    private void UpdateColorTheme()
    {
        Color color = Phase switch
        {
            PomodoroPhase.Focus => Color.FromRgb(0xFF, 0x4B, 0x4B),      // Electric Coral
            PomodoroPhase.ShortBreak => Color.FromRgb(0x00, 0xD0, 0x84), // Mint Emerald
            PomodoroPhase.LongBreak => Color.FromRgb(0x9B, 0x51, 0xE0),  // Amethyst
            _ => Color.FromRgb(0xFF, 0x4B, 0x4B)
        };

        GlowColor = color;
        GlowSolidBrush = new SolidColorBrush(color);
        SensualRadialBrush = CreateSensualBrush(color);
    }

    private static RadialGradientBrush CreateSensualBrush(Color baseColor)
    {
        var brush = new RadialGradientBrush
        {
            Center = new System.Windows.Point(0.85, 0.45),
            GradientOrigin = new System.Windows.Point(0.85, 0.45),
            RadiusX = 0.65,
            RadiusY = 0.65
        };

        brush.GradientStops.Add(new GradientStop(Color.FromArgb(120, baseColor.R, baseColor.G, baseColor.B), 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(60, baseColor.R, baseColor.G, baseColor.B), 0.4));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, baseColor.R, baseColor.G, baseColor.B), 1.0));
        brush.Freeze();
        return brush;
    }

    public override void Pause()
    {
        // Hub is hidden: halt UI timer immediately to save GPU/CPU cycles
        _uiTimer?.Stop();

        if (IsRunning)
        {
            // Arm zero-CPU dormant OS background timer for the exact finish timestamp
            var delay = _targetEndTime - DateTime.UtcNow;
            if (delay <= TimeSpan.Zero)
            {
                OnPhaseCompleted();
            }
            else
            {
                _dormantBackgroundTimer = new Timer(_ =>
                {
                    App.Current?.Dispatcher?.Invoke(() =>
                    {
                        OnPhaseCompleted();
                    });
                }, null, delay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    public override void Resume()
    {
        CancelDormantBackgroundTimer();

        if (IsRunning)
        {
            var now = DateTime.UtcNow;
            var diff = _targetEndTime - now;

            if (diff <= TimeSpan.Zero)
            {
                OnPhaseCompleted();
            }
            else
            {
                _remainingTime = diff;
                UpdateVisualState();
                _uiTimer?.Start();
            }
        }
        else
        {
            UpdateVisualState();
        }
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

    private void CancelDormantBackgroundTimer()
    {
        _dormantBackgroundTimer?.Dispose();
        _dormantBackgroundTimer = null;
    }

    protected override void LoadSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return;

        var settings = WidgetSerializer.Deserialize<PomodoroWidgetSettings>(settingsJson);
        if (settings != null)
        {
            FocusMinutes = settings.FocusMinutes > 0 ? settings.FocusMinutes : 25;
            ShortBreakMinutes = settings.ShortBreakMinutes > 0 ? settings.ShortBreakMinutes : 5;
            LongBreakMinutes = settings.LongBreakMinutes > 0 ? settings.LongBreakMinutes : 15;
            LongBreakInterval = settings.LongBreakInterval > 0 ? settings.LongBreakInterval : 4;
            SoundEnabled = settings.SoundEnabled;
            CompletedSessions = settings.CompletedSessions;
            TotalFocusMinutes = settings.TotalFocusMinutes;
            CurrentStreak = settings.CurrentStreak > 0 ? settings.CurrentStreak : 1;
        }
    }

    public override void SaveSettings()
    {
        if (!_isSettingsLoaded) return;

        var settings = new PomodoroWidgetSettings
        {
            FocusMinutes = FocusMinutes,
            ShortBreakMinutes = ShortBreakMinutes,
            LongBreakMinutes = LongBreakMinutes,
            LongBreakInterval = LongBreakInterval,
            SoundEnabled = SoundEnabled,
            CompletedSessions = CompletedSessions,
            TotalFocusMinutes = TotalFocusMinutes,
            CurrentStreak = CurrentStreak,
            LastActiveDate = DateTime.UtcNow.ToString("yyyy-MM-dd")
        };

        var json = WidgetSerializer.Serialize(settings);
        Model.SettingsJson = json;
    }

    protected override void OnDeactivated()
    {
        _uiTimer?.Stop();
        CancelDormantBackgroundTimer();
        base.OnDeactivated();
    }
}
