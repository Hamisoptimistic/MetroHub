using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MetroHub.Core.Models;
using MetroHub.Widgets.Messaging;
using Windows.Media.Control;
using Windows.Storage.Streams;
using System.Windows.Threading;
using MetroHub.Widgets.Serialization;

namespace MetroHub.Widgets.Catalog.Media;

public sealed partial class MediaWidgetViewModel : WidgetViewModelBase
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new[]
    {
        WidgetSize.Banner3, // 8x3
        WidgetSize.Mega     // 8x4
    };

    [ObservableProperty]
    private string _title = "No media playing";

    [ObservableProperty]
    private string _artist = string.Empty;

    [ObservableProperty]
    private string _album = string.Empty;

    [ObservableProperty]
    private bool _hasAlbum;

    [ObservableProperty]
    private string _sourceName = "Media Player";

    [ObservableProperty]
    private ImageSource? _thumbnail;

    [ObservableProperty]
    private bool _hasThumbnail;

    private static readonly Color DefaultSeekbarColor = Color.FromRgb(0x4C, 0x9E, 0xFF);
    private static readonly SolidColorBrush DefaultSeekbarBrush = CreateFrozenSolidBrush(DefaultSeekbarColor);

    private static SolidColorBrush CreateFrozenSolidBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    [ObservableProperty]
    private SolidColorBrush _seekbarBrush = DefaultSeekbarBrush;

    [ObservableProperty]
    private Color _seekbarGlowColor = DefaultSeekbarColor;

    public double AlbumArtSize => Model.SpanY >= 4 ? 132.0 : 104.0;


    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _hasMedia;

    [ObservableProperty]
    private bool _canPlayPause = true;

    [ObservableProperty]
    private bool _canSkipNext = true;

    [ObservableProperty]
    private bool _canSkipPrevious = true;

    [ObservableProperty]
    private bool _canSeek = true;

    [ObservableProperty]
    private double _positionSeconds;

    [ObservableProperty]
    private double _durationSeconds;

    [ObservableProperty]
    private double _progressRatio;

    [ObservableProperty]
    private string _timeDisplayString = string.Empty;

    [ObservableProperty]
    private bool _isLive;

    private readonly object _stateLock = new();
    private string _currentTrackId = string.Empty;
    private long _updateEpoch = 0;
    private long _suppressExternalPositionUpdatesUntil = 0;
    private bool _optimisticPlaybackTarget = false;
    private long _optimisticUntilTimestamp = 0;
    private int _timerTickCount = 0;

    // Bulletproof sync: Track the freshest OS-reported LastUpdatedTime we've seen.
    // Any incoming update with an OLDER LastUpdatedTime is stale and rejected.
    private DateTimeOffset _lastAcceptedOsUpdateTime = DateTimeOffset.MinValue;
    // After a large position jump (seek), enter a recovery window where every
    // timer tick does a full OS query instead of local extrapolation.
    private long _seekRecoveryUntil = 0;
    private long _transientZeroDetectedAt = 0;

    // Bulletproof live stream detection & debounce state
    private long _zeroDurationDetectedAt = 0;
    private long _trackChangedAt = Stopwatch.GetTimestamp();
    private TimeSpan _lastObservedDuration = TimeSpan.Zero;
    private long _lastObservedDurationTimestamp = 0;
    private int _consecutiveGrowthSamples = 0;

    private DispatcherTimer? _playbackTimer;
    private long _lastLocalTimestamp = Stopwatch.GetTimestamp();
    private TimeSpan _lastTimelinePosition = TimeSpan.Zero;
    private TimeSpan _trackDuration = TimeSpan.Zero;
    private double _playbackRate = 1.0;
    private bool _isScrubbing = false;
    private CancellationTokenSource? _seekRecoveryCts;
    private int _lastDisplayedPosSeconds = -1;
    private int _lastDisplayedDurSeconds = -1;
    private bool _isHubVisible = true;
    private bool _hasPendingMetadataRefresh = false;

    public MediaWidgetViewModel(TileModel model) : base(model)
    {
        if (model.SpanX != 8 || (model.SpanY != 4 && model.SpanY != 3))
        {
            model.SpanX = 8;
            model.SpanY = 4;
        }

        model.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(TileModel.SpanX) or nameof(TileModel.SpanY))
            {
                OnPropertyChanged(nameof(AlbumArtSize));
            }
        };

        LoadSettings(model.SettingsJson);

        _playbackTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _playbackTimer.Tick += OnPlaybackTimerTick;

        InitializeAsync();
    }

    private void InitializeAsync()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (_manager != null)
                {
                    _manager.CurrentSessionChanged += Manager_CurrentSessionChanged;
                    await RefreshSessionAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaWidget] Failed to initialize session manager: {ex.Message}");
            }
        });
    }

    private void Manager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        if (!_isHubVisible)
        {
            _hasPendingMetadataRefresh = true;
            MetroHub.Core.Services.HiddenDiagnosticsLogger.LogHiddenEvent("MediaWidget", "Manager_CurrentSessionChanged", "Deferred until Hub shown");
            return;
        }

        if (Application.Current?.Dispatcher is Dispatcher disp && !disp.HasShutdownStarted)
        {
            disp.InvokeAsync(async () => await RefreshSessionAsync());
        }
    }

    public async Task RefreshSessionAsync()
    {
        if (_manager == null) return;

        GlobalSystemMediaTransportControlsSession? newSession = null;
        try
        {
            newSession = _manager.GetCurrentSession();
        }
        catch { }

        if (_currentSession != null)
        {
            try
            {
                _currentSession.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
                _currentSession.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged;
            }
            catch { }
        }

        _currentSession = newSession;

        lock (_stateLock)
        {
            _currentTrackId = string.Empty;
            _trackDuration = TimeSpan.Zero;
            _zeroDurationDetectedAt = 0;
            _trackChangedAt = Stopwatch.GetTimestamp();
            _lastObservedDuration = TimeSpan.Zero;
            _lastObservedDurationTimestamp = 0;
            _consecutiveGrowthSamples = 0;
            IsLive = false;
            CanSeek = true;
        }

        if (_currentSession != null)
        {
            try
            {
                _currentSession.MediaPropertiesChanged += Session_MediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged += Session_PlaybackInfoChanged;
                _currentSession.TimelinePropertiesChanged += Session_TimelinePropertiesChanged;
            }
            catch { }

            if (_isHubVisible)
            {
                await UpdateMediaDetailsAsync();
                SyncPlaybackState(_currentSession);
            }
            else
            {
                _hasPendingMetadataRefresh = true;
            }
        }
        else
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                HasMedia = false;
                Title = "No media playing";
                Artist = "Open Spotify, YouTube, or VLC";
                Album = string.Empty;
                HasAlbum = false;
                SourceName = "Media Player";
                Thumbnail = null;
                HasThumbnail = false;
                var fallbackSeekColor = Color.FromRgb(0x4C, 0x9E, 0xFF);
                var fallbackSeekBrush = new SolidColorBrush(fallbackSeekColor);
                fallbackSeekBrush.Freeze();
                SeekbarBrush = fallbackSeekBrush;
                SeekbarGlowColor = fallbackSeekColor;
                IsPlaying = false;
                DurationSeconds = 0;
                PositionSeconds = 0;
                ProgressRatio = 0.0;
                TimeDisplayString = string.Empty;
                _lastDisplayedPosSeconds = -1;
                _lastDisplayedDurSeconds = -1;
                IsLive = false;
                CanSeek = true;
                _playbackTimer?.Stop();
            });
        }
    }

    private void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        if (!_isHubVisible)
        {
            _hasPendingMetadataRefresh = true;
            MetroHub.Core.Services.HiddenDiagnosticsLogger.LogHiddenEvent("MediaWidget", "Session_MediaPropertiesChanged", "Deferred until Hub shown");
            return;
        }

        _ = UpdateMediaDetailsAsync();
    }

    private void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        if (!_isHubVisible)
        {
            MetroHub.Core.Services.HiddenDiagnosticsLogger.LogHiddenEvent("MediaWidget", "Session_PlaybackInfoChanged", "Skipped timer restart while hidden");
            return;
        }

        if (Application.Current?.Dispatcher is Dispatcher disp && !disp.HasShutdownStarted)
        {
            disp.InvokeAsync(() => SyncPlaybackState(sender));
        }
    }

    private void Session_TimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        if (!_isHubVisible)
        {
            return;
        }

        if (Application.Current?.Dispatcher is Dispatcher disp && !disp.HasShutdownStarted)
        {
            disp.InvokeAsync(() => SyncPlaybackState(sender));
        }
    }

    private void SyncPlaybackState(GlobalSystemMediaTransportControlsSession? targetSession = null)
    {
        var session = targetSession ?? _currentSession ?? _manager?.GetCurrentSession();
        if (session == null)
        {
            if (Application.Current?.Dispatcher is Dispatcher disp && !disp.CheckAccess())
            {
                disp.InvokeAsync(() => SyncPlaybackState(null));
                return;
            }
            IsPlaying = false;
            _playbackTimer?.Stop();
            return;
        }

        if (Application.Current?.Dispatcher is Dispatcher d && !d.CheckAccess())
        {
            d.InvokeAsync(() => SyncPlaybackState(session));
            return;
        }

        try
        {
            var playback = session.GetPlaybackInfo();
            var controls = playback?.Controls;
            bool isPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            if (Stopwatch.GetTimestamp() < _optimisticUntilTimestamp)
            {
                if (isPlaying == _optimisticPlaybackTarget)
                {
                    _optimisticUntilTimestamp = 0;
                }
                else
                {
                    isPlaying = _optimisticPlaybackTarget;
                }
            }

            if (controls != null)
            {
                CanPlayPause = controls.IsPlayPauseToggleEnabled;
                CanSkipNext = controls.IsNextEnabled;
                CanSkipPrevious = controls.IsPreviousEnabled;
                CanSeek = controls.IsPlaybackPositionEnabled;
            }

            IsPlaying = isPlaying;

            SyncTimelineProperties(session, playback);

            if (_isHubVisible && isPlaying && HasMedia)
            {
                if (_playbackTimer?.IsEnabled != true)
                {
                    _playbackTimer?.Start();
                }
            }
            else
            {
                _playbackTimer?.Stop();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaWidget] SyncPlaybackState error: {ex.Message}");
        }
    }

    private void SyncTimelineProperties(GlobalSystemMediaTransportControlsSession session, GlobalSystemMediaTransportControlsSessionPlaybackInfo? playbackInfo = null)
    {
        try
        {
            var timeline = session.GetTimelineProperties();
            var playback = playbackInfo ?? session.GetPlaybackInfo();
            if (timeline == null) return;

            double rate = playback?.PlaybackRate ?? 1.0;
            if (rate <= 0.0) rate = 1.0;

            TimeSpan newDuration = timeline.EndTime - timeline.StartTime;
            if (newDuration <= TimeSpan.Zero && timeline.MaxSeekTime > timeline.MinSeekTime)
            {
                newDuration = timeline.MaxSeekTime - timeline.MinSeekTime;
            }

            bool isPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (Stopwatch.GetTimestamp() < _optimisticUntilTimestamp)
            {
                isPlaying = _optimisticPlaybackTarget;
            }

            var controls = playback?.Controls;
            bool canSeek = controls?.IsPlaybackPositionEnabled ?? true;

            bool isMusicApp = IsKnownNonLiveSource(session.SourceAppUserModelId, Title, Artist);

            lock (_stateLock)
            {
                long now = Stopwatch.GetTimestamp();
                bool isLiveStream = false;

                if (isMusicApp)
                {
                    // ── DEDICATED MUSIC SOURCE EXEMPTION ──
                    // Spotify, Apple Music, Tidal, Foobar2000, and local media players never broadcast raw live streams.
                    // Zero duration is strictly transient buffering during track switches / seek operations.
                    if (newDuration > TimeSpan.Zero)
                    {
                        IsLive = false;
                        CanSeek = canSeek;
                        _trackDuration = newDuration;
                        _zeroDurationDetectedAt = 0;
                        _consecutiveGrowthSamples = 0;
                    }
                    else
                    {
                        // Buffering / loading: ignore zero duration if we already have an established duration
                        if (_trackDuration > TimeSpan.Zero)
                        {
                            return;
                        }
                        // During initial buffering of a new song, ensure IsLive is false and return
                        IsLive = false;
                        return;
                    }
                }
                else
                {
                    // ── GENERAL / BROWSER SOURCES (YouTube, Twitch, Kick, Web Radio, etc.) ──

                    if (newDuration > TimeSpan.Zero)
                    {
                        // 1. Absurd / dummy infinite durations (e.g. > 24 hours on internet radio / HLS streams)
                        if (newDuration > TimeSpan.FromHours(24) || newDuration >= TimeSpan.MaxValue - TimeSpan.FromDays(1))
                        {
                            isLiveStream = true;
                        }
                        else
                        {
                            // 2. Sliding-buffer DVR Live Stream Detection (e.g. YouTube Live with rewind capability)
                            // A normal video or song has a CONSTANT duration.
                            // A live DVR stream's duration GROWS in real-time alongside wall-clock time (~1s per second).
                            if (_lastObservedDuration > TimeSpan.Zero && _lastObservedDurationTimestamp > 0)
                            {
                                double elapsedWallClock = (double)(now - _lastObservedDurationTimestamp) / Stopwatch.Frequency;
                                double durationGrowth = (newDuration - _lastObservedDuration).TotalSeconds;

                                // If duration grew roughly in sync with real elapsed time (within 1.2s tolerance) over at least 1s
                                if (elapsedWallClock >= 1.0 && durationGrowth > 0.5 && Math.Abs(durationGrowth - elapsedWallClock) < 1.5)
                                {
                                    // Verify position is near the live edge (within 20s of the sliding buffer head)
                                    if (Math.Abs((timeline.EndTime - timeline.Position).TotalSeconds) < 20.0)
                                    {
                                        _consecutiveGrowthSamples++;
                                    }
                                }
                                else if (elapsedWallClock >= 1.0 && Math.Abs(durationGrowth) < 0.3)
                                {
                                    // Duration is completely static: regular recorded track/video!
                                    _consecutiveGrowthSamples = 0;
                                }
                            }

                            _lastObservedDuration = newDuration;
                            _lastObservedDurationTimestamp = now;

                            // Require 3 consecutive expanding samples spanning multiple seconds to confirm DVR live
                            if (_consecutiveGrowthSamples >= 3)
                            {
                                isLiveStream = true;
                            }
                        }
                    }
                    else
                    {
                        // ── ZERO / INDETERMINATE DURATION (newDuration <= TimeSpan.Zero) ──

                        // A. If we already established a positive duration on this track,
                        // Chromium sends transient EndTime <= 0 during seeks and buffering. Ignore that transient zero!
                        if (_trackDuration > TimeSpan.Zero && !IsLive)
                        {
                            return;
                        }

                        // B. Track Change Grace Period:
                        // Allow 1.5s after a track change or playback start for the browser/app to decode headers.
                        double timeSinceTrackChange = (double)(now - _trackChangedAt) / Stopwatch.Frequency;
                        if (timeSinceTrackChange < 1.5)
                        {
                            // Still within initial buffering window — DO NOT flag as live yet!
                            return;
                        }

                        // C. Debounce gate for unseekable live streams (Twitch, Kick, Web Radio):
                        // If duration remains 0 after the grace period, require at least 1.5s of persistent zero duration
                        // while actively playing or with seek disabled before declaring it a live stream.
                        if (_zeroDurationDetectedAt == 0)
                        {
                            _zeroDurationDetectedAt = now;
                            return;
                        }

                        double zeroDurationElapsed = (double)(now - _zeroDurationDetectedAt) / Stopwatch.Frequency;
                        if (zeroDurationElapsed >= 1.5 && (isPlaying || !canSeek))
                        {
                            isLiveStream = true;
                        }
                        else
                        {
                            return;
                        }
                    }
                }

                if (isLiveStream)
                {
                    IsLive = true;
                    CanSeek = false;
                    _trackDuration = TimeSpan.Zero;
                    _seekRecoveryUntil = 0;
                    _seekRecoveryCts?.Cancel();
                    _transientZeroDetectedAt = 0;

                    if (!_isScrubbing)
                    {
                        DurationSeconds = 0;
                        PositionSeconds = 0;
                        ProgressRatio = 0.0;
                        TimeDisplayString = "LIVE";
                    }
                    return;
                }

                // ── REGULAR RECORDED TRACK / VIDEO ──
                // Positive duration arrived and stream is not a live broadcast.
                // Clear live flags and accept valid track duration!
                IsLive = false;
                CanSeek = canSeek;
                _trackDuration = newDuration;
                _zeroDurationDetectedAt = 0;

                // If we're in a user-initiated seek suppression window, skip external updates entirely
                if (Stopwatch.GetTimestamp() < _suppressExternalPositionUpdatesUntil)
                {
                    return;
                }

                TimeSpan incomingPos = timeline.Position;
                DateTimeOffset incomingUpdateTime = timeline.LastUpdatedTime;

                // ── FRESHNESS GATE ──
                // Chromium/YouTube often fires stale snapshots (position=0 or old position)
                // during seek/pause/play transitions. The LastUpdatedTime reported by the OS
                // is the canonical freshness signal. If this update is OLDER than one we've
                // already accepted, it's stale → reject it.
                if (incomingUpdateTime > DateTimeOffset.MinValue &&
                    _lastAcceptedOsUpdateTime > DateTimeOffset.MinValue &&
                    incomingUpdateTime < _lastAcceptedOsUpdateTime)
                {
                    ScheduleSeekRecoveryPoll(session);
                    return;
                }


                TimeSpan calculatedPos = incomingPos;

                // Extrapolate position forward if playing (accounts for event delivery latency & browser batching)
                if (isPlaying && incomingUpdateTime > DateTimeOffset.MinValue)
                {
                    var diff = (DateTimeOffset.UtcNow - incomingUpdateTime).TotalSeconds;
                    if (diff >= 0)
                    {
                        calculatedPos += TimeSpan.FromSeconds(diff * rate);
                    }
                }

                // ── ANTI-GLITCH: Transient zero/near-zero detection ──
                // Check if the EXTRAPOLATED position calculatedPos is near zero while we were previously well into a track.
                // Note: We MUST check calculatedPos (not incomingPos), because Chromium browsers leave incomingPos at 00:00:00!
                bool isTransientZero = calculatedPos <= TimeSpan.FromSeconds(1.5) &&
                                       _lastTimelinePosition >= TimeSpan.FromSeconds(2.5) &&
                                       _trackDuration > TimeSpan.FromSeconds(5.0);

                if (isTransientZero)
                {
                    if (_transientZeroDetectedAt == 0)
                    {
                        _transientZeroDetectedAt = Stopwatch.GetTimestamp();
                    }
                    else if ((Stopwatch.GetTimestamp() - _transientZeroDetectedAt) / (double)Stopwatch.Frequency > 0.5)
                    {
                        // OS has consistently reported near-zero for >500ms.
                        // Assume it's a genuine user seek to the beginning, accept it.
                        isTransientZero = false;
                        _transientZeroDetectedAt = 0;
                    }
                }

                if (isTransientZero)
                {
                    ScheduleSeekRecoveryPoll(session);
                    return;
                }
                
                _transientZeroDetectedAt = 0;

                // ── ACCEPT THIS UPDATE ──
                if (incomingUpdateTime > DateTimeOffset.MinValue)
                {
                    _lastAcceptedOsUpdateTime = incomingUpdateTime;
                }

                // Detect large position jumps (seek) and enter recovery window
                // Only trigger if _lastTimelinePosition was already established (>0)
                double positionDelta = Math.Abs(calculatedPos.TotalSeconds - _lastTimelinePosition.TotalSeconds);
                if (_lastTimelinePosition > TimeSpan.Zero && positionDelta > 3.0 && _trackDuration > TimeSpan.FromSeconds(5.0))
                {
                    _seekRecoveryUntil = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 1.5);
                    ScheduleSeekRecoveryPoll(session);
                }

                _lastTimelinePosition = calculatedPos;
                _lastLocalTimestamp = Stopwatch.GetTimestamp();
                _playbackRate = rate;

                double durSec = _trackDuration.TotalSeconds;
                double posSec = Math.Clamp(calculatedPos.TotalSeconds, 0, durSec > 0 ? durSec : calculatedPos.TotalSeconds);

                if (!_isScrubbing)
                {
                    if (durSec > 0)
                    {
                        DurationSeconds = durSec;
                    }
                    PositionSeconds = posSec;
                    ProgressRatio = durSec > 0 ? Math.Clamp(posSec / durSec, 0.0, 1.0) : 0.0;
                    UpdateTimeDisplay(posSec, durSec);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaWidget] SyncTimelineProperties error: {ex.Message}");
        }
    }

    /// <summary>
    /// Schedules aggressive re-polling of the OS timeline to find the real position
    /// after a seek or glitch is detected. Uses increasing delays to catch the OS
    /// as it stabilizes. Each poll that finds a fresher LastUpdatedTime will accept
    /// and apply the position, automatically stopping further polls.
    /// </summary>
    private void ScheduleSeekRecoveryPoll(GlobalSystemMediaTransportControlsSession session)
    {
        _seekRecoveryCts?.Cancel();
        _seekRecoveryCts = new CancellationTokenSource();
        var token = _seekRecoveryCts.Token;

        // Enter recovery window: every timer tick will also do a full OS query
        _seekRecoveryUntil = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 1.5);

        _ = Task.Run(async () =>
        {
            // Aggressive polling with increasing backoff
            int[] delays = { 50, 100, 200, 400, 800 };
            foreach (int delay in delays)
            {
                try
                {
                    await Task.Delay(delay, token);
                    if (token.IsCancellationRequested) return;

                    if (Application.Current?.Dispatcher is Dispatcher disp && !disp.HasShutdownStarted)
                    {
                        await disp.InvokeAsync(() =>
                        {
                            if (token.IsCancellationRequested) return;
                            SyncTimelineProperties(session);
                        });
                    }
                }
                catch { }
            }
        }, token);
    }



    private void OnPlaybackTimerTick(object? sender, EventArgs e)
    {
        if (!_isHubVisible)
        {
            _playbackTimer?.Stop();
            MetroHub.Core.Services.HiddenDiagnosticsLogger.LogHiddenEvent("MediaWidget", "OnPlaybackTimerTick", "Stopped unexpected hidden timer tick");
            return;
        }

        if (!HasMedia || _isScrubbing)
        {
            return;
        }

        var session = _currentSession ?? _manager?.GetCurrentSession();
        if (session == null)
        {
            IsPlaying = false;
            _playbackTimer?.Stop();
            return;
        }

        // Direct OS query to ensure bulletproof synchronization
        var playback = session.GetPlaybackInfo();
        bool isActuallyPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        if (Stopwatch.GetTimestamp() < _optimisticUntilTimestamp)
        {
            if (isActuallyPlaying == _optimisticPlaybackTarget)
            {
                _optimisticUntilTimestamp = 0;
            }
            else
            {
                isActuallyPlaying = _optimisticPlaybackTarget;
            }
        }

        if (!isActuallyPlaying)
        {
            if (IsPlaying)
            {
                IsPlaying = false;
            }
            _playbackTimer?.Stop();
            return;
        }

        if (!IsPlaying)
        {
            IsPlaying = true;
        }

        _timerTickCount++;

        if (IsLive)
        {
            // For live streams, do not extrapolate against an indeterminate duration.
            // Gently check OS timeline every 2s (every 8th tick) to track changes or pauses.
            if (_timerTickCount % 8 == 0)
            {
                SyncTimelineProperties(session, playback);
            }
            return;
        }

        bool inSeekRecovery = Stopwatch.GetTimestamp() < _seekRecoveryUntil;

        // During seek recovery OR every 4th tick (1s cadence), do a full OS sync
        // to ensure we converge on the real position quickly
        if (inSeekRecovery || _timerTickCount % 4 == 0)
        {
            SyncTimelineProperties(session, playback);
        }
        else
        {
            // Local extrapolation between full syncs for smooth seekbar motion
            lock (_stateLock)
            {
                double elapsedSeconds = (double)(Stopwatch.GetTimestamp() - _lastLocalTimestamp) / Stopwatch.Frequency;
                if (elapsedSeconds < 0) elapsedSeconds = 0;

                double maxPos = DurationSeconds > 0 ? DurationSeconds : double.MaxValue;
                double currentPos = Math.Clamp(_lastTimelinePosition.TotalSeconds + (elapsedSeconds * _playbackRate), 0, maxPos);
                PositionSeconds = currentPos;
                ProgressRatio = DurationSeconds > 0 ? Math.Clamp(currentPos / DurationSeconds, 0.0, 1.0) : 0.0;
                UpdateTimeDisplay(PositionSeconds, DurationSeconds);
            }
        }
    }

    private void UpdateTimeDisplay(double pos, double dur)
    {
        if (IsLive)
        {
            if (TimeDisplayString != "LIVE")
            {
                TimeDisplayString = "LIVE";
            }
            _lastDisplayedPosSeconds = -1;
            _lastDisplayedDurSeconds = -1;
            return;
        }

        if (dur > 0)
        {
            int p = (int)Math.Max(0, pos);
            int d = (int)Math.Max(0, dur);

            if (p == _lastDisplayedPosSeconds && d == _lastDisplayedDurSeconds)
            {
                return;
            }

            _lastDisplayedPosSeconds = p;
            _lastDisplayedDurSeconds = d;
            TimeDisplayString = $"{p / 60}:{p % 60:D2} / {d / 60}:{d % 60:D2}";
        }
        else
        {
            if (_lastDisplayedPosSeconds != -1 || _lastDisplayedDurSeconds != -1)
            {
                _lastDisplayedPosSeconds = -1;
                _lastDisplayedDurSeconds = -1;
                TimeDisplayString = string.Empty;
            }
        }
    }

    public void StartScrubbing()
    {
        _isScrubbing = true;
    }

    public void StopScrubbing(double finalRatio)
    {
        _isScrubbing = false;
        _ = SeekToRatioAsync(finalRatio);
    }

    public async Task SeekToRatioAsync(double ratio)
    {
        if (IsLive || !CanSeek) return;
        var session = _currentSession ?? _manager?.GetCurrentSession();
        if (session == null || DurationSeconds <= 0) return;

        ratio = Math.Clamp(ratio, 0.0, 1.0);
        double targetSeconds = ratio * DurationSeconds;
        long requestedTicks = (long)(targetSeconds * TimeSpan.TicksPerSecond);

        lock (_stateLock)
        {
            PositionSeconds = targetSeconds;
            ProgressRatio = ratio;
            _lastTimelinePosition = TimeSpan.FromSeconds(targetSeconds);
            _lastLocalTimestamp = Stopwatch.GetTimestamp();
            // Suppress external updates for 1.2s while the OS processes our seek command
            _suppressExternalPositionUpdatesUntil = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 1.2);
            // Reset the freshness tracker so the next OS update is always accepted
            _lastAcceptedOsUpdateTime = DateTimeOffset.MinValue;
        }
        UpdateTimeDisplay(PositionSeconds, DurationSeconds);

        try
        {
            await session.TryChangePlaybackPositionAsync(requestedTicks);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaWidget] Seek failed: {ex.Message}");
        }
    }

    private async Task UpdateMediaDetailsAsync()
    {
        if (_currentSession == null) return;

        long epoch = Interlocked.Increment(ref _updateEpoch);

        try
        {
            var props = await _currentSession.TryGetMediaPropertiesAsync();
            if (epoch != _updateEpoch) return;

            string rawSource = _currentSession.SourceAppUserModelId ?? string.Empty;

            if (props == null || (string.IsNullOrWhiteSpace(props.Title) && string.IsNullOrWhiteSpace(props.Artist)))
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (epoch != _updateEpoch) return;

                    HasMedia = false;
                    Title = "No media playing";
                    Artist = "Open Spotify, YouTube, or VLC";
                    Album = string.Empty;
                    HasAlbum = false;
                    SourceName = ResolveSourceName(rawSource, null, null);
                    Thumbnail = null;
                    HasThumbnail = false;
                    var fallbackSeekColor = Color.FromRgb(0x4C, 0x9E, 0xFF);
                    var fallbackSeekBrush = new SolidColorBrush(fallbackSeekColor);
                    fallbackSeekBrush.Freeze();
                    SeekbarBrush = fallbackSeekBrush;
                    SeekbarGlowColor = fallbackSeekColor;
                    IsPlaying = false;
                    _playbackTimer?.Stop();
                });
                return;
            }

            string cleanTitle = props.Title?.Trim() ?? "Unknown Track";
            string cleanArtist = props.Artist?.Trim() ?? string.Empty;
            string cleanAlbum = props.AlbumTitle?.Trim() ?? props.AlbumArtist?.Trim() ?? string.Empty;

            // Strip redundant browser suffixes like " - YouTube"
            if (cleanTitle.EndsWith(" - YouTube", StringComparison.OrdinalIgnoreCase))
            {
                cleanTitle = cleanTitle.Substring(0, cleanTitle.Length - " - YouTube".Length).Trim();
            }

            // Clean YouTube Music "- Topic" suffix from artist name (e.g. "Deftones - Topic" -> "Deftones")
            string rawArtist = cleanArtist;
            if (cleanArtist.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase))
            {
                cleanArtist = cleanArtist.Substring(0, cleanArtist.Length - " - Topic".Length).Trim();
            }
            else if (cleanArtist.EndsWith("- Topic", StringComparison.OrdinalIgnoreCase))
            {
                cleanArtist = cleanArtist.Substring(0, cleanArtist.Length - "- Topic".Length).Trim();
            }

            // If artist is empty but title has " - ", split artist and track name (common on YouTube/browser streams)
            if (string.IsNullOrWhiteSpace(cleanArtist) && cleanTitle.Contains(" - "))
            {
                var parts = cleanTitle.Split(new[] { " - " }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    cleanArtist = parts[0].Trim();
                    cleanTitle = parts[1].Trim();
                }
            }

            string newTrackId = $"{cleanArtist}|{cleanTitle}|{cleanAlbum}";
            lock (_stateLock)
            {
                if (!string.Equals(_currentTrackId, newTrackId, StringComparison.Ordinal))
                {
                    _currentTrackId = newTrackId;
                    _lastTimelinePosition = TimeSpan.Zero;
                    _lastLocalTimestamp = Stopwatch.GetTimestamp();
                    _lastAcceptedOsUpdateTime = DateTimeOffset.MinValue;
                    _suppressExternalPositionUpdatesUntil = 0;
                    _seekRecoveryUntil = 0;
                    _transientZeroDetectedAt = 0;
                    _trackDuration = TimeSpan.Zero;
                    IsLive = false;
                    CanSeek = true;
                    _zeroDurationDetectedAt = 0;
                    _trackChangedAt = Stopwatch.GetTimestamp();
                    _lastObservedDuration = TimeSpan.Zero;
                    _lastObservedDurationTimestamp = 0;
                    _consecutiveGrowthSamples = 0;
                }
            }

            string cleanSource = ResolveSourceName(rawSource, cleanTitle, rawArtist);
            bool hasAlbum = !string.IsNullOrWhiteSpace(cleanAlbum);

            ImageSource? bmp = null;
            Color seekbarColor = Color.FromRgb(0x4C, 0x9E, 0xFF);
            var seekbarBrush = new SolidColorBrush(seekbarColor);
            seekbarBrush.Freeze();

            if (props.Thumbnail != null)
            {
                bmp = await LoadThumbnailAsync(props.Thumbnail);
                if (epoch != _updateEpoch) return;
                if (bmp is BitmapSource bs)
                {
                    (seekbarBrush, seekbarColor) = ExtractSeekbarBrush(bs);
                }
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (epoch != _updateEpoch) return;

                Title = cleanTitle;
                Artist = cleanArtist;
                Album = cleanAlbum;
                HasAlbum = hasAlbum;
                SourceName = cleanSource;
                Thumbnail = bmp;
                HasThumbnail = bmp != null;
                SeekbarBrush = seekbarBrush;
                SeekbarGlowColor = seekbarColor;
                HasMedia = true;

                // Sync controls and playback authoritatively from session
                SyncPlaybackState(_currentSession);
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaWidget] UpdateMediaDetails error: {ex.Message}");
        }
    }

    private static async Task<ImageSource?> LoadThumbnailAsync(IRandomAccessStreamReference streamRef)
    {
        try
        {
            using var stream = await streamRef.OpenReadAsync();
            using var netStream = stream.AsStreamForRead();
            using var memory = new MemoryStream();
            await netStream.CopyToAsync(memory);
            memory.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.StreamSource = memory;
            bitmap.DecodePixelWidth = 256;
            bitmap.EndInit();
            bitmap.Freeze();

            // If thumbnail is 16:9 YouTube video frame with pillarboxes,
            // center-crop to the square album cover to eliminate side pillarbox bars!
            if (bitmap.PixelWidth > bitmap.PixelHeight * 1.25)
            {
                int size = bitmap.PixelHeight;
                int xOffset = (bitmap.PixelWidth - size) / 2;
                var cropped = new CroppedBitmap(bitmap, new Int32Rect(xOffset, 0, size, size));
                cropped.Freeze();
                return cropped;
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static (SolidColorBrush, Color) ExtractSeekbarBrush(BitmapSource bitmap)
    {
        try
        {
            var thumb = new TransformedBitmap(bitmap, new ScaleTransform(16.0 / bitmap.PixelWidth, 16.0 / bitmap.PixelHeight));
            var converted = new FormatConvertedBitmap(thumb, PixelFormats.Bgra32, null, 0);
            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            int stride = width * 4;
            int totalBytes = height * stride;
            byte[] pixels = ArrayPool<byte>.Shared.Rent(totalBytes);

            try
            {
                converted.CopyPixels(pixels, stride, 0);

                double totalColorWeight = 0;
                double accR = 0, accG = 0, accB = 0;
                int validPixelCount = 0;
                double totalLum = 0;

                for (int i = 0; i <= totalBytes - 4; i += 4)
                {
                    byte b = pixels[i];
                    byte g = pixels[i + 1];
                    byte r = pixels[i + 2];
                    byte a = pixels[i + 3];
                    if (a < 128) continue;

                    int max = Math.Max(r, Math.Max(g, b));
                    int min = Math.Min(r, Math.Min(g, b));
                    int delta = max - min;

                    var (h, s, v) = RgbToHsv(r, g, b);
                    validPixelCount++;
                    totalLum += v;

                    if (delta >= 24 && s >= 0.20 && v >= 0.18 && v <= 0.95)
                    {
                        double weight = s * s * (1.0 - Math.Abs(v - 0.65));
                        accR += r * weight;
                        accG += g * weight;
                        accB += b * weight;
                        totalColorWeight += weight;
                    }
                }

                Color seekbarColor;
                bool isMonochrome = totalColorWeight < 0.05;

                if (!isMonochrome)
                {
                    byte avgR = (byte)Math.Clamp(accR / totalColorWeight, 0, 255);
                    byte avgG = (byte)Math.Clamp(accG / totalColorWeight, 0, 255);
                    byte avgB = (byte)Math.Clamp(accB / totalColorWeight, 0, 255);

                    var (h, s, v) = RgbToHsv(avgR, avgG, avgB);

                    double seekH = h;
                    double seekS = s;
                    double seekV = v;

                    // Blue-Indigo-Violet Range (195° - 275°) Compensation:
                    if (seekH >= 195.0 && seekH <= 275.0)
                    {
                        seekV = Math.Clamp(seekV * 1.60, 0.88, 1.0);
                        seekS = Math.Clamp(seekS * 0.82, 0.42, 0.78);
                    }
                    else
                    {
                        seekV = Math.Clamp(seekV * 1.35, 0.82, 0.98);
                        seekS = Math.Clamp(seekS * 0.95, 0.55, 0.90);
                    }
                    seekbarColor = ColorFromHsv(seekH, seekS, seekV);
                }
                else
                {
                    double avgLum = validPixelCount > 0 ? totalLum / validPixelCount : 0.8;
                    if (avgLum > 0.4)
                    {
                        seekbarColor = Color.FromRgb(240, 244, 255); // Crisp moonlight pearl-white
                    }
                    else
                    {
                        seekbarColor = Color.FromRgb(180, 215, 255); // Electric ice-blue
                    }
                }

                var seekbarBrush = new SolidColorBrush(seekbarColor);
                seekbarBrush.Freeze();
                return (seekbarBrush, seekbarColor);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pixels);
            }
        }
        catch
        {
            var fallbackSeekColor = Color.FromRgb(0x4C, 0x9E, 0xFF);
            var fallbackSeekbarBrush = new SolidColorBrush(fallbackSeekColor);
            fallbackSeekbarBrush.Freeze();
            return (fallbackSeekbarBrush, fallbackSeekColor);
        }
    }

    private static (double h, double s, double v) RgbToHsv(byte r, byte g, byte b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double delta = max - min;

        double h = 0;
        if (delta > 0)
        {
            if (max == rd) h = ((gd - bd) / delta) % 6.0;
            else if (max == gd) h = ((bd - rd) / delta) + 2.0;
            else h = ((rd - gd) / delta) + 4.0;
            h *= 60.0;
            if (h < 0) h += 360.0;
        }

        double s = max == 0 ? 0 : delta / max;
        double v = max;
        return (h, s, v);
    }

    private static Color ColorFromHsv(double hue, double saturation, double value)
    {
        int hi = Convert.ToInt32(Math.Floor(hue / 60)) % 6;
        double f = hue / 60 - Math.Floor(hue / 60);

        value = value * 255;
        byte v = (byte)Math.Clamp(value, 0, 255);
        byte p = (byte)Math.Clamp(value * (1 - saturation), 0, 255);
        byte q = (byte)Math.Clamp(value * (1 - f * saturation), 0, 255);
        byte t = (byte)Math.Clamp(value * (1 - (1 - f) * saturation), 0, 255);

        return hi switch
        {
            0 => Color.FromRgb(v, t, p),
            1 => Color.FromRgb(q, v, p),
            2 => Color.FromRgb(p, v, t),
            3 => Color.FromRgb(p, q, v),
            4 => Color.FromRgb(t, p, v),
            _ => Color.FromRgb(v, p, q),
        };
    }

    /// <summary>
    /// Determines whether the media source is a dedicated music player that never broadcasts raw live streams.
    /// (e.g. Spotify, Apple Music, Tidal, local music playback).
    /// </summary>
    public static bool IsKnownNonLiveSource(string? appId, string? title, string? artist)
    {
        // YouTube Music releases via browser / desktop
        if (artist != null && (artist.EndsWith("- Topic", StringComparison.OrdinalIgnoreCase) || artist.EndsWith("Topic", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(appId)) return false;

        string lower = appId.ToLowerInvariant();
        return lower.Contains("spotify") ||
               lower.Contains("applemusic") ||
               lower.Contains("itunes") ||
               lower.Contains("tidal") ||
               lower.Contains("foobar2000") ||
               lower.Contains("aimp") ||
               lower.Contains("musicbee") ||
               lower.Contains("zunemusic") ||
               lower.Contains("microsoft.zunemusic") ||
               lower.Contains("microsoft.media.player");
    }

    public static string ResolveSourceName(string? appId, string? title, string? artist)
    {
        // 1. Detect YouTube / YouTube Music
        if (artist != null && (artist.EndsWith("- Topic", StringComparison.OrdinalIgnoreCase) || artist.EndsWith("Topic", StringComparison.OrdinalIgnoreCase)))
        {
            return "YouTube Music";
        }
        if ((title != null && title.Contains("YouTube", StringComparison.OrdinalIgnoreCase)) ||
            (artist != null && artist.Contains("YouTube", StringComparison.OrdinalIgnoreCase)))
        {
            return "YouTube";
        }

        if (string.IsNullOrWhiteSpace(appId)) return "Media Player";

        string lower = appId.ToLowerInvariant();

        if (lower.Contains("spotify")) return "Spotify";
        if (lower.Contains("vlc")) return "VLC Media Player";
        if (lower.Contains("applemusic") || lower.Contains("itunes")) return "Apple Music";
        if (lower.Contains("tidal")) return "TIDAL";
        if (lower.Contains("foobar2000")) return "foobar2000";
        if (lower.Contains("aimp")) return "AIMP";
        if (lower.Contains("zunemusic") || lower.Contains("microsoft.zunemusic")) return "Groove Music";
        if (lower.Contains("microsoft.media.player")) return "Media Player";
        if (lower.Contains("zen")) return "Zen Browser";

        // Known browsers
        if (lower.Contains("edge") || lower.Contains("msedge")) return "Microsoft Edge";
        if (lower.Contains("chrome")) return "Google Chrome";
        if (lower.Contains("brave")) return "Brave";
        if (lower.Contains("firefox")) return "Firefox";
        if (lower.Contains("opera")) return "Opera";

        // 2. Query Windows Registry AppUserModelId (e.g. ZenToast-F0DC299D809B9700 or PWAs)
        string? regName = TryResolveFromRegistry(appId);
        if (!string.IsNullOrWhiteSpace(regName))
        {
            return regName;
        }

        // 3. Prevent raw hex hash names (e.g. "F0DC299D809B9700") from displaying
        if (IsHexOrHash(appId))
        {
            return "Web Browser";
        }

        string name = System.IO.Path.GetFileNameWithoutExtension(appId);
        int bang = name.IndexOf('!');
        if (bang >= 0 && bang < name.Length - 1)
        {
            name = name.Substring(bang + 1);
        }

        if (IsHexOrHash(name))
        {
            return "Web Browser";
        }

        return name;
    }

    private static bool IsHexOrHash(string str)
    {
        if (string.IsNullOrWhiteSpace(str)) return false;
        string s = str.Trim();
        if (s.Length >= 8 && s.Length <= 64)
        {
            bool allHex = true;
            foreach (char c in s)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                {
                    allHex = false;
                    break;
                }
            }
            if (allHex) return true;
        }
        return false;
    }

    private static string? TryResolveFromRegistry(string appId)
    {
        try
        {
            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId");
            if (root != null)
            {
                foreach (var subKeyName in root.GetSubKeyNames())
                {
                    if (subKeyName.IndexOf(appId, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        appId.IndexOf(subKeyName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        using var subKey = root.OpenSubKey(subKeyName);
                        var disp = subKey?.GetValue("DisplayName") as string;
                        if (!string.IsNullOrWhiteSpace(disp)) return disp;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    [RelayCommand]
    public async Task TogglePlayPauseAsync()
    {
        var session = _currentSession ?? _manager?.GetCurrentSession();
        if (session == null) return;

        try
        {
            bool targetState = !IsPlaying;

            _optimisticPlaybackTarget = targetState;
            _optimisticUntilTimestamp = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 1.5);
            IsPlaying = targetState;

            if (!targetState)
            {
                _playbackTimer?.Stop();
            }
            else
            {
                _lastLocalTimestamp = Stopwatch.GetTimestamp();
                _playbackTimer?.Start();
            }

            await session.TryTogglePlayPauseAsync();

            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 6; i++)
                {
                    await Task.Delay(100);
                    if (_currentSession == null) break;
                    var pb = _currentSession.GetPlaybackInfo();
                    if (pb != null && (pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) == targetState)
                    {
                        _optimisticUntilTimestamp = 0;
                        if (Application.Current?.Dispatcher is Dispatcher disp && !disp.HasShutdownStarted)
                        {
                            await disp.InvokeAsync(() => SyncPlaybackState(_currentSession));
                        }
                        break;
                    }
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaWidget] TogglePlayPause error: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task SkipNextAsync()
    {
        var session = _currentSession ?? _manager?.GetCurrentSession();
        if (session == null) return;

        try
        {
            await session.TrySkipNextAsync();
            _ = Task.Run(async () =>
            {
                await Task.Delay(150);
                if (Application.Current?.Dispatcher is Dispatcher disp && !disp.HasShutdownStarted)
                {
                    await disp.InvokeAsync(async () =>
                    {
                        await UpdateMediaDetailsAsync();
                        SyncPlaybackState(_currentSession);
                    });
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaWidget] SkipNext error: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task SkipPreviousAsync()
    {
        var session = _currentSession ?? _manager?.GetCurrentSession();
        if (session == null) return;

        try
        {
            await session.TrySkipPreviousAsync();
            _ = Task.Run(async () =>
            {
                await Task.Delay(150);
                if (Application.Current?.Dispatcher is Dispatcher disp && !disp.HasShutdownStarted)
                {
                    await disp.InvokeAsync(async () =>
                    {
                        await UpdateMediaDetailsAsync();
                        SyncPlaybackState(_currentSession);
                    });
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaWidget] SkipPrevious error: {ex.Message}");
        }
    }

    public override void Receive(HubVisibilityChangedMessage message)
    {
        base.Receive(message); // Routes to Pause()/Resume()
    }

    public override void Pause()
    {
        _isHubVisible = false;
        _playbackTimer?.Stop();
    }

    public override void Resume()
    {
        _isHubVisible = true;
        var session = _currentSession ?? _manager?.GetCurrentSession();
        if (session != null)
        {
            if (_hasPendingMetadataRefresh)
            {
                _hasPendingMetadataRefresh = false;
                _ = UpdateMediaDetailsAsync();
            }
            SyncTimelineProperties(session);
            SyncPlaybackState(session);
        }
        else if (_hasPendingMetadataRefresh)
        {
            _hasPendingMetadataRefresh = false;
            _ = RefreshSessionAsync();
        }
    }

    protected override void LoadSettings(string? settingsJson)
    {
    }

    public override void SaveSettings()
    {
        var settings = new MediaWidgetSettings();
        Model.TargetPath = "media";
        Model.SettingsJson = WidgetSerializer.Serialize(settings);
        MainWindow.Current?.SaveGroupsAndLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _seekRecoveryCts?.Cancel();
            _seekRecoveryCts?.Dispose();
            _seekRecoveryCts = null;

            if (_playbackTimer != null)
            {
                _playbackTimer.Stop();
                _playbackTimer.Tick -= OnPlaybackTimerTick;
                _playbackTimer = null;
            }

            if (_manager != null)
            {
                try
                {
                    _manager.CurrentSessionChanged -= Manager_CurrentSessionChanged;
                }
                catch { }
                _manager = null;
            }

            if (_currentSession != null)
            {
                try
                {
                    _currentSession.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
                    _currentSession.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
                    _currentSession.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged;
                }
                catch { }
                _currentSession = null;
            }

            Thumbnail = null;
        }

        base.Dispose(disposing);
    }
}
