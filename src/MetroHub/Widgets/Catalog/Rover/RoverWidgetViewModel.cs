using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetroHub.Core.Models;
using MetroHub.Widgets.Serialization;
using Windows.Media.Control;
using WindowsMediaController;

namespace MetroHub.Widgets.Catalog.Rover;

/// <summary>
/// High-performance ViewModel for the Rover (Windows XP Dog) desktop companion widget.
/// Features zero-polling Windows SMTC media playback awareness, single-click trick cycling
/// across all authentic poses, and realistic sleep/wake mechanics.
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

    // Windows Media SMTC integration (Zero-polling event driven)
    private MediaManager? _mediaManager;
    private volatile bool _isMediaManagerStarted;
    private bool _isPlayingMedia;

    private RoverState _state = RoverState.Idle;
    private string _backgroundStyle = "FluentGlass";
    private int _sleepTimeoutMinutes = 2;
    private bool _wasUserIdle;


    public RoverAnimationEngine Engine => _engine;
    public RoverAudioService AudioService => _audioService;

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new[]
    {
        WidgetSize.Wide  // 4x2: Centered Desktop Companion
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
        RoverState.ListeningToMusic => "Jamming to Music! 🎵",
        _ => "Watching your desktop"
    };

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

    public void ShowSpeech(string text)
    {
        // Speech box removed as per design: clean desktop companion pet
    }

    public void RefreshLayoutSize()
    {
        OnPropertyChanged(nameof(IsMedium));
        OnPropertyChanged(nameof(IsWide));
        OnPropertyChanged(nameof(IsLarge));
        OnPropertyChanged(nameof(IsBanner));
        OnPropertyChanged(nameof(IsXPBliss));
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

    #region Authentic Trick Catalog

    private readonly struct RoverTrick
    {
        public readonly string Animation;
        public readonly string? SoundId;

        public RoverTrick(string animation, string? soundId = null)
        {
            Animation = animation;
            SoundId = soundId;
        }
    }

    private static readonly RoverTrick[] TrickCatalog = new[]
    {
        new RoverTrick("Cooking", "3"),           // Chef hat & stirring pot
        new RoverTrick("Sports", "4"),            // Tennis ball toss & catch
        new RoverTrick("Books", "9"),             // Spectacles & reading study
        new RoverTrick("Congratulate", "6"),      // Trophy victory cheer
        new RoverTrick("Celebrity", "8"),         // Hollywood sunglasses with guitar riff
        new RoverTrick("CharacterSucceeds", "4"), // Victory celebratory dance
        new RoverTrick("Pleased", "3"),           // Happy tail wagging & panting
        new RoverTrick("Searching", "10"),        // Digging dirt with flying dust
        new RoverTrick("Embarrassed", "7"),       // Sheepish blushing
        new RoverTrick("Shopping", "2"),          // Shopping cart adventure
        new RoverTrick("Writing", "10"),          // Scribbling notes with pencil
        new RoverTrick("ImageSearching", "3"),    // Magnifying glass scan
        new RoverTrick("Travel", "4"),            // Travel driving pose
        new RoverTrick("Money", "6"),             // Golden coin discovery
        new RoverTrick("Show", "1"),              // Presentation flourish
        new RoverTrick("GetAttention", "4"),      // Two-paw wave
        new RoverTrick("Greet", "2"),             // Friendly bow & greeting
        new RoverTrick("Surprised", "7"),         // Surprise leap
        new RoverTrick("ClickedOn", "9")          // Quick attentive look
    };

    private int _trickCycleIndex;

    #endregion

    public RoverWidgetViewModel(TileModel model) : base(model)
    {
        _engine.SoundTriggered += OnSoundTriggered;

        // 1. Inactivity detection timer (checks user idle time every 5 seconds)
        _idleCheckTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _idleCheckTimer.Tick += OnIdleCheckTick;

        // 2. Ambient behavior timer (Rover stretches, looks around occasionally when idle)
        _ambientTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(20)
        };
        _ambientTimer.Tick += OnAmbientTimerTick;

        LoadSettings(model.SettingsJson);

        // Start in resting pose with 0% CPU
        _engine.SetStaticPose("RestPose");
        _idleCheckTimer.Start();
        _ambientTimer.Start();

        // Initialize zero-polling Windows Media SMTC monitor
        _ = InitMediaControllerAsync();
    }

    private void OnSoundTriggered(string soundId)
    {
        _audioService.PlaySound(soundId);
    }

    #region Media Playback Awareness (Windows SMTC)

    private async Task InitMediaControllerAsync()
    {
        try
        {
            _mediaManager = new MediaManager();
            _mediaManager.OnAnyPlaybackStateChanged += MediaManager_OnAnyPlaybackStateChanged;
            _mediaManager.OnAnySessionOpened += MediaManager_OnAnySessionOpened;
            _mediaManager.OnAnySessionClosed += MediaManager_OnAnySessionClosed;
            await _mediaManager.StartAsync();
            _isMediaManagerStarted = true;
            CheckCurrentMediaPlayback();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RoverWidget] MediaManager init failed: {ex.Message}");
        }
    }

    private void MediaManager_OnAnySessionOpened(MediaManager.MediaSession mediaSession)
    {
        CheckCurrentMediaPlayback();
    }

    private void MediaManager_OnAnySessionClosed(MediaManager.MediaSession mediaSession)
    {
        CheckCurrentMediaPlayback();
    }

    private void MediaManager_OnAnyPlaybackStateChanged(MediaManager.MediaSession mediaSession, GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo)
    {
        if (playbackInfo == null) return;
        bool isPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        App.Current?.Dispatcher?.InvokeAsync(() =>
        {
            if (isPlaying)
            {
                SetMediaPlaying(true);
            }
            else
            {
                CheckCurrentMediaPlayback();
            }
        });
    }

    private void CheckCurrentMediaPlayback()
    {
        if (!_isMediaManagerStarted || _mediaManager == null) return;
        try
        {
            bool anyPlaying = false;
            var focused = _mediaManager.GetFocusedSession();
            if (focused != null)
            {
                var info = focused.ControlSession?.GetPlaybackInfo();
                if (info != null && info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    anyPlaying = true;
                }
            }

            if (!anyPlaying)
            {
                var sessions = _mediaManager.CurrentMediaSessions;
                if (sessions != null)
                {
                    foreach (var kvp in sessions)
                    {
                        var info = kvp.Value.ControlSession?.GetPlaybackInfo();
                        if (info != null && info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        {
                            anyPlaying = true;
                            break;
                        }
                    }
                }
            }

            App.Current?.Dispatcher?.InvokeAsync(() => SetMediaPlaying(anyPlaying));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RoverWidget] CheckCurrentMediaPlayback error: {ex.Message}");
        }
    }

    private void SetMediaPlaying(bool isPlaying)
    {
        if (_isPlayingMedia == isPlaying) return;
        _isPlayingMedia = isPlaying;

        if (_isPlayingMedia)
        {
            // Music started playing!
            if (_state == RoverState.Sleeping)
            {
                // Wake up from sleep to listen to music
                State = RoverState.ListeningToMusic;
                _audioService.PlayBark();
                _engine.Play("WakeUp", loop: false, onComplete: () =>
                {
                    if (_isPlayingMedia && _state == RoverState.ListeningToMusic)
                    {
                        StartMusicGroove();
                    }
                    else
                    {
                        State = RoverState.Idle;
                        _engine.SetStaticPose("RestPose");
                    }
                });
            }
            else
            {
                State = RoverState.ListeningToMusic;
                StartMusicGroove();
            }
        }
        else
        {
            // Music paused or stopped
            if (_state == RoverState.ListeningToMusic)
            {
                State = RoverState.Idle;
                _engine.Stop();
                _engine.SetStaticPose("RestPose");
            }
        }
    }

    private void StartMusicGroove()
    {
        // Rover puts on his Hollywood shades with sound 8!
        _audioService.PlaySound("8");
        _engine.Play("Celebrity", loop: false, onComplete: () =>
        {
            if (_isPlayingMedia && _state == RoverState.ListeningToMusic)
            {
                // Continue happy jamming while music plays
                _engine.Play("Pleased", loop: true);
            }
        });
    }

    #endregion



    #region Single-Click Trick Cycling & Interactions

    /// <summary>
    /// Master tile click interaction handler.
    /// Wakes Rover if asleep, jams if music is playing, or cycles through his authentic trick repertoire.
    /// </summary>
    public void Interact()
    {
        if (_state == RoverState.Sleeping)
        {
            WakeUp();
            return;
        }

        if (_state == RoverState.ListeningToMusic)
        {
            // If music is playing, clicking triggers an energetic celebratory dance
            _audioService.PlayTrickSound();
            _engine.Play("CharacterSucceeds", loop: false, onComplete: () =>
            {
                if (_isPlayingMedia && _state == RoverState.ListeningToMusic)
                {
                    _engine.Play("Pleased", loop: true);
                }
            });
            return;
        }

        // Awake: Cycle to next trick in catalog
        State = RoverState.Trick;
        var trick = TrickCatalog[_trickCycleIndex % TrickCatalog.Length];
        _trickCycleIndex++;

        if (!string.IsNullOrEmpty(trick.SoundId))
        {
            _audioService.PlaySound(trick.SoundId);
        }
        else
        {
            _audioService.PlayBark();
        }

        _engine.Play(trick.Animation, loop: false, onComplete: () =>
        {
            if (_state == RoverState.Trick)
            {
                State = RoverState.Idle;
                _engine.SetStaticPose("RestPose");
            }
        });
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
        _audioService.PlayBark();

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
        Interact();
    }

    #endregion

    #region Sleep and Wake Mechanics

    [RelayCommand]
    public void TakeNap()
    {
        _wasUserIdle = true;
        State = RoverState.Sleeping;
        _ambientTimer.Stop();

        _engine.Play("LieDown", loop: false, onComplete: () =>
        {
            _engine.SetStaticPose("Sleeping");
        });
    }

    [RelayCommand]
    public void WakeUp()
    {
        _wasUserIdle = false;
        State = RoverState.Alert;
        _ambientTimer.Start();
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
    }

    private void OnIdleCheckTick(object? sender, EventArgs e)
    {
        TimeSpan idleTime = GetUserIdleTime();
        bool isIdle = idleTime.TotalMinutes >= _sleepTimeoutMinutes;

        if (isIdle && !_wasUserIdle)
        {
            // User went idle -> Rover curls up on his paws to sleep!
            TakeNap();
        }
        else if (!isIdle && _wasUserIdle && _state == RoverState.Sleeping)
        {
            // User resumed input -> Rover wakes up!
            WakeUp();
        }
    }

    private void OnAmbientTimerTick(object? sender, EventArgs e)
    {
        // Only trigger ambient behavior when MetroHub is active and Rover is idle (not sleeping, doing trick, or jamming to music)
        if (_state != RoverState.Idle || _engine.IsRunning || _isPlayingMedia) return;

        int roll = RandomNumberGenerator.GetInt32(0, 100);
        if (roll < 45)
        {
            string[] ambientAnims = { "Idle", "LookUp", "LookUpLeft", "Thinking" };
            string anim = ambientAnims[RandomNumberGenerator.GetInt32(0, ambientAnims.Length)];

            _engine.Play(anim, loop: false, onComplete: () =>
            {
                _engine.SetStaticPose("RestPose");
            });
        }

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

    #endregion

    #region Lifecycle and Settings

    public override void Pause()
    {
        // MetroHub window is hidden: Halt timers to guarantee 0.000% CPU usage
        _idleCheckTimer.Stop();
        _ambientTimer.Stop();
        _engine.Stop();
    }

    public override void Resume()
    {
        // MetroHub window is visible again: Restore timers
        _idleCheckTimer.Start();
        if (_state != RoverState.Sleeping)
        {
            _ambientTimer.Start();
            if (_isPlayingMedia)
            {
                StartMusicGroove();
            }
            else
            {
                _engine.SetStaticPose("RestPose");
            }
        }
        else
        {
            _engine.SetStaticPose("Sleeping");
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
            ShowSpeechBubbles = false,
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
            _engine.Stop();
            _engine.SoundTriggered -= OnSoundTriggered;

            _isMediaManagerStarted = false;
            if (_mediaManager != null)
            {
                try
                {
                    _mediaManager.OnAnyPlaybackStateChanged -= MediaManager_OnAnyPlaybackStateChanged;
                    _mediaManager.OnAnySessionOpened -= MediaManager_OnAnySessionOpened;
                    _mediaManager.OnAnySessionClosed -= MediaManager_OnAnySessionClosed;
                    _mediaManager.Dispose();
                }
                catch { }
                _mediaManager = null;
            }

            _audioService.Dispose();
        }
        base.Dispose(disposing);
    }

    #endregion
}
