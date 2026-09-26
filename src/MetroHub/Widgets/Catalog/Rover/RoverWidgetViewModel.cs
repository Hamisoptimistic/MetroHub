using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetroHub.Core.Models;
using MetroHub.Widgets.Serialization;

namespace MetroHub.Widgets.Catalog.Rover;

/// <summary>
/// High-performance ViewModel for the Rover (Windows XP Dog) desktop companion widget.
/// Coordinates the discrete-step animation engine, instant audio playback, Win32 idle sleep detection,
/// authentic speech balloons, and zero-overhead CPU quiescence.
/// </summary>
public sealed partial class RoverWidgetViewModel : WidgetViewModelBase
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    private readonly RoverAnimationEngine _engine = new();
    private readonly RoverAudioService _audioService = new();

    private readonly DispatcherTimer _idleCheckTimer;
    private readonly DispatcherTimer _ambientTimer;
    private readonly DispatcherTimer _speechDismissTimer;

    private RoverState _state = RoverState.Idle;
    private string _speechText = "Woof! Click me to play! 🐾";
    private bool _isSpeechVisible = true;
    private string _backgroundStyle = "FluentGlass";
    private int _sleepTimeoutMinutes = 2;
    private bool _showSpeechBubbles = true;
    private bool _wasUserIdle;

    public RoverAnimationEngine Engine => _engine;
    public RoverAudioService AudioService => _audioService;

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new[]
    {
        WidgetSize.Medium,  // 2x2: Pocket Companion
        WidgetSize.Wide,    // 4x2: Rover + Speech Balloon
        WidgetSize.Large,   // 4x4: Playpen Companion
        WidgetSize.Banner3  // 8x3: Wide Companion Banner
    };

    public RoverState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsSleeping));
                OnPropertyChanged(nameof(StatusLabel));
            }
        }
    }

    public bool IsSleeping => _state == RoverState.Sleeping;

    public string StatusLabel => _state switch
    {
        RoverState.Sleeping => "Sleeping (Zzz...)",
        RoverState.Petted => "Petted & Happy! ❤️",
        RoverState.Trick => "Doing a trick! 🌟",
        RoverState.Alert => "Alert & Ready",
        _ => "Watching your desktop"
    };

    public string SpeechText
    {
        get => _speechText;
        set => SetProperty(ref _speechText, value);
    }

    public bool IsSpeechVisible
    {
        get => _isSpeechVisible && _showSpeechBubbles;
        set => SetProperty(ref _isSpeechVisible, value);
    }

    public bool IsMuted
    {
        get => _audioService.IsMuted;
        set
        {
            if (_audioService.IsMuted != value)
            {
                _audioService.IsMuted = value;
                OnPropertyChanged(nameof(IsMuted));
                SaveSettings();
            }
        }
    }

    public bool IsMedium => Model.SpanX == 2 && Model.SpanY == 2;
    public bool IsWide => Model.SpanX == 4 && Model.SpanY <= 3;
    public bool IsLarge => Model.SpanX == 4 && Model.SpanY >= 4;
    public bool IsBanner => Model.SpanX >= 6;
    public bool IsXPBliss => string.Equals(_backgroundStyle, "XPBliss", StringComparison.OrdinalIgnoreCase);

    public void RefreshLayoutSize()
    {
        OnPropertyChanged(nameof(IsMedium));
        OnPropertyChanged(nameof(IsWide));
        OnPropertyChanged(nameof(IsLarge));
        OnPropertyChanged(nameof(IsBanner));
    }

    public override void Initialize(TileModel model)
    {
        base.Initialize(model);
        RefreshLayoutSize();
    }

    public string BackgroundStyle
    {
        get => _backgroundStyle;
        set
        {
            if (SetProperty(ref _backgroundStyle, value))
            {
                OnPropertyChanged(nameof(IsXPBliss));
                SaveSettings();
            }
        }
    }

    public RoverWidgetViewModel(TileModel model) : base(model)
    {
        _engine.SoundTriggered += OnSoundTriggered;

        // 1. Idle detection timer (checks user inactivity every 5 seconds)
        _idleCheckTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _idleCheckTimer.Tick += OnIdleCheckTick;

        // 2. Ambient behavior timer (Rover stretches, looks around every 18-30s)
        _ambientTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(20)
        };
        _ambientTimer.Tick += OnAmbientTimerTick;

        // 3. Speech dismiss timer
        _speechDismissTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(6)
        };
        _speechDismissTimer.Tick += (s, e) =>
        {
            _speechDismissTimer.Stop();
            if (_state != RoverState.Sleeping)
            {
                SpeechText = "Woof! Click me to play! 🐾";
            }
        };

        LoadSettings(model.SettingsJson);

        // Start in resting pose with 0% CPU
        _engine.SetStaticPose("RestPose");
        _idleCheckTimer.Start();
        _ambientTimer.Start();
    }

    private void OnSoundTriggered(string soundId)
    {
        _audioService.PlaySound(soundId);
    }

    /// <summary>
    /// Master tile click interaction handler.
    /// Clicking the tile wakes Rover if sleeping, or triggers petting / trick.
    /// </summary>
    public void Interact()
    {
        if (_state == RoverState.Sleeping)
        {
            WakeUp();
        }
        else
        {
            // Alternate between Pet and Trick on click
            if (RandomNumberGenerator.GetInt32(0, 3) == 0)
            {
                DoTrick();
            }
            else
            {
                Pet();
            }
        }
    }

    [RelayCommand]
    public void Pet()
    {
        if (_state == RoverState.Sleeping)
        {
            WakeUp();
            return;
        }

        State = RoverState.Petted;
        ShowSpeech(GetRandomPetSpeech());
        _audioService.PlayBark();

        // Play authentic ClickedOn or Pleased animation
        string anim = RandomNumberGenerator.GetInt32(0, 2) == 0 ? "ClickedOn" : "Pleased";
        _engine.Play(anim, loop: false, onComplete: () =>
        {
            State = RoverState.Idle;
            _engine.SetStaticPose("RestPose");
        });
    }

    [RelayCommand]
    public void DoTrick()
    {
        if (_state == RoverState.Sleeping)
        {
            WakeUp();
            return;
        }

        State = RoverState.Trick;
        string[] tricks = { "Congratulate", "Sports", "Celebrity", "Cooking", "CharacterSucceeds" };
        string chosenTrick = tricks[RandomNumberGenerator.GetInt32(0, tricks.Length)];

        ShowSpeech(chosenTrick switch
        {
            "Sports" => "Catch the ball, master! 🎾",
            "Cooking" => "Bon appétit! Chef Rover on duty! 👨‍🍳",
            "Celebrity" => "I'm a star! How do the shades look? 😎",
            "Congratulate" => "Good job today! Keep going! 🏆",
            _ => "Ta-da! Good doggy! 🐕"
        });

        if (chosenTrick == "Celebrity")
        {
            _audioService.PlaySound("8");
        }
        else
        {
            _audioService.PlayTrickSound();
        }

        _engine.Play(chosenTrick, loop: false, onComplete: () =>
        {
            State = RoverState.Idle;
            _engine.SetStaticPose("RestPose");
        });
    }

    [RelayCommand]
    public void TakeNap()
    {
        State = RoverState.Sleeping;
        SpeechText = "Zzz... Snoozing peacefully 😴";
        IsSpeechVisible = true;
        _speechDismissTimer.Stop();

        _engine.Play("LieDown", loop: false, onComplete: () =>
        {
            _engine.SetStaticPose("Sleeping");
        });
    }

    [RelayCommand]
    public void WakeUp()
    {
        State = RoverState.Alert;
        SpeechText = "Huh? I'm awake! Ready to play! 🐾";
        IsSpeechVisible = true;
        _speechDismissTimer.Stop();
        _speechDismissTimer.Start();
        _audioService.PlayBark();

        _engine.Play("WakeUp", loop: false, onComplete: () =>
        {
            State = RoverState.Idle;
            _engine.SetStaticPose("RestPose");
        });
    }

    [RelayCommand]
    public void ToggleMute()
    {
        IsMuted = !IsMuted;
        ShowSpeech(IsMuted ? "Quiet mode on! 🤫" : "Barks enabled! Woof! 🔊");
    }

    public void ShowSpeech(string text)
    {
        if (!_showSpeechBubbles) return;

        SpeechText = text;
        IsSpeechVisible = true;
        _speechDismissTimer.Stop();
        _speechDismissTimer.Start();
    }

    private void OnIdleCheckTick(object? sender, EventArgs e)
    {
        TimeSpan idleTime = GetUserIdleTime();
        bool isIdle = idleTime.TotalMinutes >= _sleepTimeoutMinutes;

        if (isIdle && !_wasUserIdle)
        {
            // User went idle -> Rover goes to sleep!
            _wasUserIdle = true;
            TakeNap();
        }
        else if (!isIdle && _wasUserIdle)
        {
            // User returned -> Rover wakes up!
            _wasUserIdle = false;
            WakeUp();
        }
    }

    private void OnAmbientTimerTick(object? sender, EventArgs e)
    {
        // Only trigger ambient behavior when MetroHub is active and Rover is idle (not sleeping or doing trick)
        if (_state != RoverState.Idle || _engine.IsRunning) return;

        int roll = RandomNumberGenerator.GetInt32(0, 100);
        if (roll < 45)
        {
            // Play natural ambient animation (sniff, look around, wag tail)
            string[] ambientAnims = { "Idle", "LookUp", "LookUpLeft", "Thinking" };
            string anim = ambientAnims[RandomNumberGenerator.GetInt32(0, ambientAnims.Length)];

            _engine.Play(anim, loop: false, onComplete: () =>
            {
                _engine.SetStaticPose("RestPose");
            });
        }

        // Randomize next interval between 15 and 30 seconds
        _ambientTimer.Interval = TimeSpan.FromSeconds(RandomNumberGenerator.GetInt32(15, 31));
    }

    private static TimeSpan GetUserIdleTime()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (GetLastInputInfo(ref lii))
        {
            uint idleTicks = (uint)Environment.TickCount - lii.dwTime;
            return TimeSpan.FromMilliseconds(idleTicks);
        }
        return TimeSpan.Zero;
    }

    private static string GetRandomPetSpeech()
    {
        string[] petReplies =
        {
            "*Happy panting* Woof! ❤️",
            "That's the sweet spot right behind the ears!",
            "Woof! You're doing great today!",
            "*Wags tail excitedly* 🐾",
            "Bark! Let's conquer the day!",
            "I love being your desktop companion! 😊"
        };
        return petReplies[RandomNumberGenerator.GetInt32(0, petReplies.Length)];
    }

    public override void Pause()
    {
        // MetroHub window is hidden: Halt all timers to ensure ZERO CPU usage
        _idleCheckTimer.Stop();
        _ambientTimer.Stop();
        _speechDismissTimer.Stop();
        _engine.Stop();
    }

    public override void Resume()
    {
        // MetroHub window is visible again: Restore idle monitoring and set rest pose
        _idleCheckTimer.Start();
        _ambientTimer.Start();
        if (_state == RoverState.Sleeping)
        {
            _engine.SetStaticPose("Sleeping");
        }
        else
        {
            _engine.SetStaticPose("RestPose");
        }
    }

    protected override void LoadSettings(string? settingsJson)
    {
        var settings = WidgetSerializer.Deserialize<RoverWidgetSettings>(settingsJson);
        if (settings != null)
        {
            _audioService.IsMuted = settings.IsMuted;
            _audioService.Volume = settings.Volume;
            _sleepTimeoutMinutes = Math.Clamp(settings.SleepTimeoutMinutes, 1, 30);
            _showSpeechBubbles = settings.ShowSpeechBubbles;
            _backgroundStyle = !string.IsNullOrWhiteSpace(settings.BackgroundStyle) ? settings.BackgroundStyle : "FluentGlass";

            OnPropertyChanged(nameof(IsMuted));
            OnPropertyChanged(nameof(BackgroundStyle));
        }
    }

    public override void SaveSettings()
    {
        var settings = new RoverWidgetSettings
        {
            IsMuted = _audioService.IsMuted,
            Volume = _audioService.Volume,
            SleepTimeoutMinutes = _sleepTimeoutMinutes,
            ShowSpeechBubbles = _showSpeechBubbles,
            BackgroundStyle = _backgroundStyle
        };
        Model.SettingsJson = WidgetSerializer.Serialize(settings);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _idleCheckTimer.Stop();
            _ambientTimer.Stop();
            _speechDismissTimer.Stop();
            _engine.Stop();
            _engine.SoundTriggered -= OnSoundTriggered;
            _audioService.Dispose();
        }
        base.Dispose(disposing);
    }
}
