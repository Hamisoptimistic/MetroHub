using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetroHub.Core.Models;
using MetroHub.Widgets.Serialization;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;
using WindowsMediaController;

namespace MetroHub.Widgets.Catalog.Media;

/// <summary>
/// High-performance, event-driven Media Widget ViewModel powered by Dubya.WindowsMediaController.
/// Listens to OS SMTC events without polling timers. Idle CPU is 0.0%.
/// </summary>
public sealed partial class MediaWidgetViewModel : WidgetViewModelBase
{
    private MediaManager? _mediaManager;
    private MediaManager.MediaSession? _activeSession;
    private volatile bool _isMediaManagerStarted;

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new[]
    {
        WidgetSize.SlimWide,      // 4x1
        WidgetSize.ExtraWide,     // 6x2
        WidgetSize.PortraitLarge, // 4x6
        WidgetSize.Banner3,       // 8x3
        WidgetSize.Mega           // 8x4
    };

    [ObservableProperty]
    private string _title = "No media playing";

    [ObservableProperty]
    private string _artist = "Open Spotify, YouTube, or VLC";

    [ObservableProperty]
    private string _album = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAlbumRow))]
    private bool _hasAlbum;

    [ObservableProperty]
    private string _sourceName = "Media Player";

    [ObservableProperty]
    private ImageSource? _thumbnail;

    [ObservableProperty]
    private bool _hasThumbnail;

    /// <summary>
    /// Alias for Thumbnail to match widget naming standards.
    /// Setting this also updates HasThumbnail automatically.
    /// </summary>
    public ImageSource? AlbumArtSource
    {
        get => Thumbnail;
        set
        {
            Thumbnail = value;
            HasThumbnail = value != null;
        }
    }

    /// <summary>
    /// Retains compressed raw image bytes (~20 KB) in managed memory.
    /// Allows the heavy decoded WPF BitmapSource/DirectX surface to be released
    /// when MetroHub is hidden, and instantly re-decoded with 0ms latency when restored.
    /// </summary>
    private byte[]? _cachedThumbnailBytes;
    private int _currentThumbnailWidth;

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

    [ObservableProperty]
    private Brush _ambientGlowBrush = Brushes.Transparent;

    private MediaWidgetSettings _settings = new();
    private Color? _currentAuraColor;

    public bool IsAmbientGlowEnabled => _settings.IsAmbientGlowEnabled;

    public bool IsSlimMode => Model.SpanY == 1;
    public bool IsZuneMode => Model.SpanX == 4 && Model.SpanY == 6;
    public bool IsStandardMode => Model.SpanY > 1 && !IsZuneMode;

    public double AlbumArtSize => Model.SpanY switch
    {
        >= 4 => 132.0,
        3 => 104.0,
        _ => 64.0
    };

    public bool ShowAlbumRow => Model.SpanY >= 3 && HasAlbum;
    public Thickness TrackInfoMargin => Model.SpanY <= 2 ? new Thickness(16, 0, 96, 0) : new Thickness(20, 0, 175, 0);
    public Thickness AlbumArtMargin => Model.SpanY <= 2 ? new Thickness(0, 0, 16, 0) : new Thickness(0, 0, 20, 0);
    public double TrackTitleFontSize => Model.SpanY <= 2 ? 15.0 : 17.0;
    public double ArtistFontSize => Model.SpanY <= 2 ? 13.0 : 14.0;
    public Thickness PlaybackBarPadding => Model.SpanY <= 2 ? new Thickness(16, 0, 16, 0) : new Thickness(20, 0, 20, 0);
    public Thickness TransportControlsMargin => Model.SpanY <= 2 ? new Thickness(-6, 0, 0, 0) : new Thickness(-9.5, 0, 0, 0);
    public double FallbackWatermarkFontSize => Model.SpanY switch
    {
        >= 4 => 36.0,
        3 => 32.0,
        _ => 24.0
    };

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoMedia))]
    private bool _hasMedia;

    public bool HasNoMedia => !HasMedia;

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
    private long _suppressExternalPositionUpdatesUntil = 0;
    private bool _optimisticPlaybackTarget = false;
    private long _optimisticUntilTimestamp = 0;

    // Bulletproof sync: Track the freshest OS-reported LastUpdatedTime we've seen.
    private DateTimeOffset _lastAcceptedOsUpdateTime = DateTimeOffset.MinValue;
    private long _transientZeroDetectedAt = 0;

    // Bulletproof live stream detection & debounce state
    private bool _isLiveLocked = false;
    private TimeSpan _lastObservedDuration = TimeSpan.Zero;
    private long _zeroDurationDetectedAt = 0;
    private long _trackChangedAt = Stopwatch.GetTimestamp();

    // Lightweight 250ms timer used SOLELY to interpolate seekbar motion while playing non-live tracks
    private DispatcherTimer? _playbackTimer;
    private long _lastLocalTimestamp = Stopwatch.GetTimestamp();
    private TimeSpan _lastTimelinePosition = TimeSpan.Zero;
    private TimeSpan _trackDuration = TimeSpan.Zero;
    private double _playbackRate = 1.0;
    private bool _isScrubbing = false;
    private int _lastDisplayedPosSeconds = -1;
    private int _lastDisplayedDurSeconds = -1;
    private bool _isHubVisible = true;
    private TimeSpan _lastWidgetSeekPosition = TimeSpan.Zero;
    private long _lastWidgetSeekTimestamp = 0;

    public MediaWidgetViewModel(TileModel model) : base(model)
    {
        bool isValidSize = (model.SpanX == 8 && (model.SpanY == 4 || model.SpanY == 3)) ||
                           (model.SpanX == 6 && model.SpanY == 2) ||
                           (model.SpanX == 4 && (model.SpanY == 1 || model.SpanY == 6));
        if (!isValidSize)
        {
            model.SpanX = 8;
            model.SpanY = 4;
        }

        model.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(TileModel.SpanX) or nameof(TileModel.SpanY))
            {
                OnPropertyChanged(nameof(IsSlimMode));
                OnPropertyChanged(nameof(IsZuneMode));
                OnPropertyChanged(nameof(IsStandardMode));
                OnPropertyChanged(nameof(AlbumArtSize));
                OnPropertyChanged(nameof(ShowAlbumRow));
                OnPropertyChanged(nameof(TrackInfoMargin));
                OnPropertyChanged(nameof(AlbumArtMargin));
                OnPropertyChanged(nameof(TrackTitleFontSize));
                OnPropertyChanged(nameof(ArtistFontSize));
                OnPropertyChanged(nameof(PlaybackBarPadding));
                OnPropertyChanged(nameof(TransportControlsMargin));
                OnPropertyChanged(nameof(FallbackWatermarkFontSize));
            }
        };

        LoadSettings(model.SettingsJson);

        _playbackTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _playbackTimer.Tick += OnPlaybackTimerTick;

        InitializeMediaControllerAsync();
    }

    private async void InitializeMediaControllerAsync()
    {
        try
        {
            _mediaManager = new MediaManager();
            _mediaManager.OnAnySessionOpened += Manager_OnAnySessionOpened;
            _mediaManager.OnAnySessionClosed += Manager_OnAnySessionClosed;
            _mediaManager.OnFocusedSessionChanged += Manager_OnFocusedSessionChanged;
            _mediaManager.OnAnyPlaybackStateChanged += Manager_OnAnyPlaybackStateChanged;
            _mediaManager.OnAnyMediaPropertyChanged += Manager_OnAnyMediaPropertyChanged;
            _mediaManager.OnAnyTimelinePropertyChanged += Manager_OnAnyTimelinePropertyChanged;

            await _mediaManager.StartAsync();
            _isMediaManagerStarted = true;

            var initial = SafeGetFallbackSession();
            if (initial != null)
            {
                SetActiveSession(initial);
            }
            else
            {
                RunOnUi(ResetToNoMedia);
            }
        }
        catch (Exception ex)
        {
            _isMediaManagerStarted = false;
            Debug.WriteLine($"[MediaWidget] Init failed: {ex.Message}");
            RunOnUi(ResetToNoMedia);
        }
    }

    private MediaManager.MediaSession? SafeGetFallbackSession(string? excludeSessionId = null)
    {
        if (!_isMediaManagerStarted || _mediaManager == null)
        {
            return null;
        }

        try
        {
            var focused = _mediaManager.GetFocusedSession();
            if (focused != null && (excludeSessionId == null || focused.Id != excludeSessionId))
            {
                return focused;
            }

            var sessions = _mediaManager.CurrentMediaSessions;
            if (sessions != null)
            {
                return excludeSessionId == null
                    ? sessions.Values.FirstOrDefault()
                    : sessions.Values.FirstOrDefault(s => s.Id != excludeSessionId);
            }
        }
        catch (InvalidOperationException)
        {
            // Thrown if MediaManager has not completed StartAsync or is currently shutting down
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaWidget] SafeGetFallbackSession failed: {ex.Message}");
        }

        return null;
    }

    private void Manager_OnFocusedSessionChanged(MediaManager.MediaSession? mediaSession)
    {
        try
        {
            if (mediaSession != null)
            {
                SetActiveSession(mediaSession);
            }
            else
            {
                var next = SafeGetFallbackSession();
                if (next != null)
                {
                    SetActiveSession(next);
                }
                else
                {
                    RunOnUi(ResetToNoMedia);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaWidget] OnFocusedSessionChanged failed: {ex.Message}");
        }
    }

    private void Manager_OnAnySessionOpened(MediaManager.MediaSession mediaSession)
    {
        try
        {
            if (_activeSession == null)
            {
                SetActiveSession(mediaSession);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaWidget] OnAnySessionOpened failed: {ex.Message}");
        }
    }

    private void Manager_OnAnySessionClosed(MediaManager.MediaSession mediaSession)
    {
        try
        {
            if (_activeSession == null || _activeSession.Id == mediaSession.Id)
            {
                var next = SafeGetFallbackSession(excludeSessionId: mediaSession.Id);
                if (next != null)
                {
                    SetActiveSession(next);
                }
                else
                {
                    RunOnUi(ResetToNoMedia);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaWidget] OnAnySessionClosed failed: {ex.Message}");
        }
    }

    private void Manager_OnAnyPlaybackStateChanged(MediaManager.MediaSession mediaSession, GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo)
    {
        try
        {
            if (playbackInfo == null) return;

            // Auto-switch to newly playing session if our current session is inactive/different (Android music notification pattern)
            if (_activeSession == null || _activeSession.Id != mediaSession.Id)
            {
                if (playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    SetActiveSession(mediaSession);
                    return;
                }
            }

            if (_activeSession != null && _activeSession.Id == mediaSession.Id)
            {
                RunOnUi(() => ApplyPlaybackInfo(playbackInfo));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaWidget] OnAnyPlaybackStateChanged failed: {ex.Message}");
        }
    }

    private void Manager_OnAnyMediaPropertyChanged(MediaManager.MediaSession mediaSession, GlobalSystemMediaTransportControlsSessionMediaProperties mediaProperties)
    {
        try
        {
            if (_activeSession == null)
            {
                SetActiveSession(mediaSession);
                return;
            }

            if (_activeSession.Id == mediaSession.Id)
            {
                _ = ApplyMediaPropertiesAsync(mediaSession, mediaProperties);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaWidget] OnAnyMediaPropertyChanged failed: {ex.Message}");
        }
    }

    private void Manager_OnAnyTimelinePropertyChanged(MediaManager.MediaSession mediaSession, GlobalSystemMediaTransportControlsSessionTimelineProperties timelineProperties)
    {
        try
        {
            if (_activeSession != null && _activeSession.Id == mediaSession.Id)
            {
                RunOnUi(() => ApplyTimelineProperties(timelineProperties));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaWidget] OnAnyTimelinePropertyChanged failed: {ex.Message}");
        }
    }

    private void SetActiveSession(MediaManager.MediaSession session)
    {
        _activeSession = session;
        lock (_stateLock)
        {
            _currentTrackId = string.Empty;
            _isLiveLocked = false;
            _lastObservedDuration = TimeSpan.Zero;
            _trackDuration = TimeSpan.Zero;
            _lastTimelinePosition = TimeSpan.Zero;
            _lastLocalTimestamp = Stopwatch.GetTimestamp();
            _lastAcceptedOsUpdateTime = DateTimeOffset.MinValue;
            _lastWidgetSeekPosition = TimeSpan.Zero;
            _lastWidgetSeekTimestamp = 0;
            _zeroDurationDetectedAt = 0;
            _trackChangedAt = Stopwatch.GetTimestamp();
            _transientZeroDetectedAt = 0;
        }

        try
        {
            var control = session.ControlSession;
            if (control != null)
            {
                var playback = control.GetPlaybackInfo();
                var timeline = control.GetTimelineProperties();

                RunOnUi(() =>
                {
                    if (playback != null) ApplyPlaybackInfo(playback);
                    if (timeline != null) ApplyTimelineProperties(timeline);
                });

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var props = await control.TryGetMediaPropertiesAsync();
                        if (props != null && _activeSession?.Id == session.Id)
                        {
                            await ApplyMediaPropertiesAsync(session, props);
                        }
                    }
                    catch { }
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaWidget] SetActiveSession error: {ex.Message}");
        }
    }

    private async Task ApplyMediaPropertiesAsync(MediaManager.MediaSession session, GlobalSystemMediaTransportControlsSessionMediaProperties? props)
    {
        if (props == null) return;

        string cleanTitle = props.Title?.Trim() ?? string.Empty;
        string cleanArtist = props.Artist?.Trim() ?? string.Empty;
        string cleanAlbum = props.AlbumTitle?.Trim() ?? props.AlbumArtist?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(cleanTitle) && string.IsNullOrWhiteSpace(cleanArtist))
        {
            return;
        }

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

        string rawSource = session.Id ?? string.Empty;
        string cleanSource = ResolveSourceName(rawSource, cleanTitle, rawArtist);
        bool hasAlbum = !string.IsNullOrWhiteSpace(cleanAlbum);
        bool isMusicApp = IsKnownNonLiveSource(rawSource, cleanTitle, rawArtist);
        bool isLiveTitle = !isMusicApp && HasLiveTitleKeyword(cleanTitle);

        string newTrackId = $"{cleanArtist}|{cleanTitle}|{cleanAlbum}";
        bool isSameTrack = false;
        lock (_stateLock)
        {
            if (!string.Equals(_currentTrackId, newTrackId, StringComparison.Ordinal))
            {
                _currentTrackId = newTrackId;
                _currentThumbnailWidth = 0;
                _isLiveLocked = isLiveTitle;
                _lastObservedDuration = TimeSpan.Zero;
                _lastTimelinePosition = TimeSpan.Zero;
                _lastLocalTimestamp = Stopwatch.GetTimestamp();
                _lastAcceptedOsUpdateTime = DateTimeOffset.MinValue;
                _lastWidgetSeekPosition = TimeSpan.Zero;
                _lastWidgetSeekTimestamp = 0;
                _suppressExternalPositionUpdatesUntil = 0;
                _transientZeroDetectedAt = 0;
                _trackDuration = TimeSpan.Zero;
                _zeroDurationDetectedAt = 0;
                _trackChangedAt = Stopwatch.GetTimestamp();
            }
            else
            {
                isSameTrack = true;
                if (isLiveTitle)
                {
                    _isLiveLocked = true;
                }
            }
        }

        ImageSource? bmp = null;
        byte[]? rawBytes = null;
        Color seekbarColor = DefaultSeekbarColor;
        SolidColorBrush seekbarBrush = DefaultSeekbarBrush;
        Brush ambientGlowBrush = Brushes.Transparent;
        bool updateArtwork = false;

        if (props.Thumbnail != null)
        {
            (bmp, rawBytes) = await LoadThumbnailAsync(props.Thumbnail);
            if (bmp is BitmapSource bs)
            {
                int incomingWidth = bs.PixelWidth;
                lock (_stateLock)
                {
                    // Bloat-free Anti-Downgrade Guard:
                    // If YouTube Music / browser sends a low-res image (e.g. 120px) ~1s after
                    // sending the authentic high-res 544px image for the same track, keep the high-res one!
                    if (!isSameTrack || _currentThumbnailWidth == 0 || incomingWidth >= _currentThumbnailWidth)
                    {
                        _currentThumbnailWidth = incomingWidth;
                        updateArtwork = true;
                    }
                }

                if (updateArtwork)
                {
                    (seekbarBrush, seekbarColor) = ExtractSeekbarBrush(bs);
                    if (_settings.IsAmbientGlowEnabled)
                    {
                        ambientGlowBrush = CreateAlbumAuraGlow(seekbarColor);
                    }
                }
            }
            else if (!isSameTrack)
            {
                updateArtwork = true;
            }
        }
        else if (!isSameTrack)
        {
            updateArtwork = true;
        }

        RunOnUi(() =>
        {
            if (_activeSession?.Id != session.Id) return;

            Title = string.IsNullOrWhiteSpace(cleanTitle) ? "Unknown Track" : cleanTitle;
            Artist = cleanArtist;
            Album = cleanAlbum;
            HasAlbum = hasAlbum;
            SourceName = cleanSource;
            if (updateArtwork)
            {
                _cachedThumbnailBytes = rawBytes;
                Thumbnail = bmp;
                HasThumbnail = bmp != null;
                SeekbarBrush = seekbarBrush;
                SeekbarGlowColor = seekbarColor;
                _currentAuraColor = (bmp != null) ? seekbarColor : null;
                AmbientGlowBrush = (_settings.IsAmbientGlowEnabled && bmp != null) ? ambientGlowBrush : Brushes.Transparent;
            }
            HasMedia = true;

            if (_isLiveLocked)
            {
                IsLive = true;
                CanSeek = false;
                TimeDisplayString = string.Empty;
                DurationSeconds = 0;
                PositionSeconds = 0;
                ProgressRatio = 0.0;
                _playbackTimer?.Stop();
            }
        });
    }

    private void ApplyPlaybackInfo(GlobalSystemMediaTransportControlsSessionPlaybackInfo? playback)
    {
        if (playback == null) return;

        var controls = playback.Controls;
        if (controls != null)
        {
            CanPlayPause = controls.IsPlayPauseToggleEnabled;
            CanSkipNext = controls.IsNextEnabled;
            CanSkipPrevious = controls.IsPreviousEnabled;
            CanSeek = controls.IsPlaybackPositionEnabled;
        }

        bool isPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

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

        bool wasPlaying = IsPlaying;
        IsPlaying = isPlaying;

        if (_isHubVisible && isPlaying && HasMedia && !IsLive)
        {
            lock (_stateLock)
            {
                long now = Stopwatch.GetTimestamp();
                if (!wasPlaying || _playbackTimer?.IsEnabled != true)
                {
                    double elapsed = (double)(now - _lastLocalTimestamp) / Stopwatch.Frequency;
                    if (elapsed > 0 && wasPlaying)
                    {
                        double newPos = _lastTimelinePosition.TotalSeconds + (elapsed * _playbackRate);
                        if (DurationSeconds > 0 && newPos > DurationSeconds) newPos = DurationSeconds;
                        _lastTimelinePosition = TimeSpan.FromSeconds(newPos);
                        PositionSeconds = newPos;
                        ProgressRatio = DurationSeconds > 0 ? Math.Clamp(newPos / DurationSeconds, 0.0, 1.0) : 0.0;
                        UpdateTimeDisplay(PositionSeconds, DurationSeconds);
                    }
                    _lastLocalTimestamp = now;
                }
            }
            if (_playbackTimer?.IsEnabled != true)
            {
                _playbackTimer?.Start();
            }
        }
        else
        {
            if (wasPlaying && !isPlaying)
            {
                lock (_stateLock)
                {
                    long now = Stopwatch.GetTimestamp();
                    double elapsed = (double)(now - _lastLocalTimestamp) / Stopwatch.Frequency;
                    if (elapsed > 0)
                    {
                        double newPos = _lastTimelinePosition.TotalSeconds + (elapsed * _playbackRate);
                        if (DurationSeconds > 0 && newPos > DurationSeconds) newPos = DurationSeconds;
                        _lastTimelinePosition = TimeSpan.FromSeconds(newPos);
                        PositionSeconds = newPos;
                        ProgressRatio = DurationSeconds > 0 ? Math.Clamp(newPos / DurationSeconds, 0.0, 1.0) : 0.0;
                        UpdateTimeDisplay(PositionSeconds, DurationSeconds);
                    }
                    _lastLocalTimestamp = now;
                }
            }
            _playbackTimer?.Stop();
        }
    }

    private void ApplyTimelineProperties(GlobalSystemMediaTransportControlsSessionTimelineProperties? timeline)
    {
        if (timeline == null) return;

        try
        {
            TimeSpan newDuration = timeline.EndTime - timeline.StartTime;
            if (newDuration <= TimeSpan.Zero && timeline.MaxSeekTime > timeline.MinSeekTime)
            {
                newDuration = timeline.MaxSeekTime - timeline.MinSeekTime;
            }

            bool isMusicApp = IsKnownNonLiveSource(_activeSession?.Id, Title, Artist);
            bool canSeek = CanSeek;

            lock (_stateLock)
            {
                long now = Stopwatch.GetTimestamp();
                bool isLiveStream = _isLiveLocked;

                // 1. Title keyword signal (for non-music sources)
                if (!isLiveStream && !isMusicApp && HasLiveTitleKeyword(Title))
                {
                    isLiveStream = true;
                }

                // 2. Ultra-long broadcast threshold (YouTube limits uploads to 12h; >= 12h is always live/24-7)
                if (!isLiveStream && newDuration >= TimeSpan.FromHours(12))
                {
                    isLiveStream = true;
                }

                // 3. User's 5th signal: Twitch streams with a static DVR window (duration > 0 but seek is disabled)
                if (!isLiveStream && !canSeek && newDuration > TimeSpan.Zero && !isMusicApp)
                {
                    isLiveStream = true;
                }

                // 4. Expanding Duration: In a live stream with DVR (e.g. YouTube), the duration continuously expands with real time
                if (!isLiveStream && !isMusicApp && IsPlaying && _lastObservedDuration > TimeSpan.Zero && newDuration > TimeSpan.Zero)
                {
                    if (newDuration > _lastObservedDuration + TimeSpan.FromSeconds(1.5))
                    {
                        isLiveStream = true;
                    }
                }

                if (newDuration > TimeSpan.Zero)
                {
                    _lastObservedDuration = newDuration;
                }

                if (!isLiveStream)
                {
                    if (newDuration > TimeSpan.Zero)
                    {
                        IsLive = false;
                        CanSeek = canSeek;
                        _trackDuration = newDuration;
                        _zeroDurationDetectedAt = 0;
                    }
                    else if (newDuration <= TimeSpan.Zero)
                    {
                        if (_trackDuration > TimeSpan.Zero && !IsLive)
                        {
                            return; // Ignore transient zero duration if already established
                        }

                        double timeSinceTrackChange = (double)(now - _trackChangedAt) / Stopwatch.Frequency;
                        if (timeSinceTrackChange < 2.0 || canSeek || isMusicApp)
                        {
                            return; // Grace period or seekable or dedicated music app
                        }

                        if (_zeroDurationDetectedAt == 0)
                        {
                            _zeroDurationDetectedAt = now;
                            return;
                        }

                        double zeroDurationElapsed = (double)(now - _zeroDurationDetectedAt) / Stopwatch.Frequency;
                        if (zeroDurationElapsed >= 2.5 && !canSeek)
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
                    _isLiveLocked = true;
                    IsLive = true;
                    CanSeek = false;
                    _trackDuration = TimeSpan.Zero;
                    _transientZeroDetectedAt = 0;

                    if (!_isScrubbing)
                    {
                        DurationSeconds = 0;
                        PositionSeconds = 0;
                        ProgressRatio = 0.0;
                        TimeDisplayString = string.Empty;
                    }
                    _playbackTimer?.Stop();
                    return;
                }

                // ── REGULAR RECORDED TRACK ──
                IsLive = false;
                CanSeek = canSeek;
                _trackDuration = newDuration > TimeSpan.Zero ? newDuration : _trackDuration;
                _zeroDurationDetectedAt = 0;

                if (Stopwatch.GetTimestamp() < _suppressExternalPositionUpdatesUntil)
                {
                    return;
                }

                TimeSpan incomingPos = timeline.Position;
                DateTimeOffset incomingUpdateTime = timeline.LastUpdatedTime;

                // Freshness gate: Reject updates older than our freshest accepted update
                if (incomingUpdateTime > DateTimeOffset.MinValue &&
                    _lastAcceptedOsUpdateTime > DateTimeOffset.MinValue &&
                    incomingUpdateTime < _lastAcceptedOsUpdateTime)
                {
                    return;
                }

                TimeSpan calculatedPos = incomingPos;
                bool hasValidTimestamp = incomingUpdateTime > DateTimeOffset.MinValue && incomingUpdateTime.Year > 2000;

                if (IsPlaying && hasValidTimestamp)
                {
                    var diff = (DateTimeOffset.UtcNow - incomingUpdateTime).TotalSeconds;
                    if (diff >= 0 && diff < 86400)
                    {
                        calculatedPos += TimeSpan.FromSeconds(diff * _playbackRate);
                    }
                }
                else if (IsPlaying && !hasValidTimestamp)
                {
                    // Media player (Spotify, browser, etc.) does not supply LastUpdatedTime.
                    // If incomingPos is an echo of our own widget seek and we've already progressed past it,
                    // do not rewind back to the static seek anchor.
                    if (_lastWidgetSeekTimestamp > 0 && Math.Abs((incomingPos - _lastWidgetSeekPosition).TotalSeconds) <= 2.5)
                    {
                        if (_lastTimelinePosition >= _lastWidgetSeekPosition)
                        {
                            return;
                        }
                    }

                    // Also reject if incomingPos is behind our smoothly extrapolated position
                    if (_lastTimelinePosition > TimeSpan.Zero && calculatedPos <= _lastTimelinePosition)
                    {
                        double lag = (_lastTimelinePosition - calculatedPos).TotalSeconds;
                        if (lag < 90.0)
                        {
                            return; // Ignore stale un-timestamped rewind
                        }
                    }
                }

                // Anti-glitch: Detect transient zero/near-zero during buffering/seeks
                bool isTransientZero = calculatedPos <= TimeSpan.FromSeconds(1.5) &&
                                       _lastTimelinePosition >= TimeSpan.FromSeconds(2.5) &&
                                       _trackDuration > TimeSpan.FromSeconds(5.0);

                if (isTransientZero)
                {
                    if (_transientZeroDetectedAt == 0)
                    {
                        _transientZeroDetectedAt = Stopwatch.GetTimestamp();
                        return;
                    }
                    else if ((Stopwatch.GetTimestamp() - _transientZeroDetectedAt) / (double)Stopwatch.Frequency > 0.5)
                    {
                        isTransientZero = false;
                        _transientZeroDetectedAt = 0;
                    }
                    else
                    {
                        return;
                    }
                }
                _transientZeroDetectedAt = 0;

                if (hasValidTimestamp)
                {
                    _lastAcceptedOsUpdateTime = incomingUpdateTime;
                }

                _lastTimelinePosition = calculatedPos;
                _lastLocalTimestamp = Stopwatch.GetTimestamp();

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
            Debug.WriteLine($"[MediaWidget] ApplyTimelineProperties error: {ex.Message}");
        }
    }

    private void OnPlaybackTimerTick(object? sender, EventArgs e)
    {
        if (!_isHubVisible || !HasMedia || _isScrubbing || !IsPlaying || IsLive)
        {
            _playbackTimer?.Stop();
            return;
        }

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

    private void UpdateTimeDisplay(double pos, double dur)
    {
        if (IsLive)
        {
            if (!string.IsNullOrEmpty(TimeDisplayString))
            {
                TimeDisplayString = string.Empty;
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

            bool showHours = d >= 3600;
            TimeDisplayString = $"{FormatTimeSpan(p, showHours)} / {FormatTimeSpan(d, showHours)}";
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

    private static string FormatTimeSpan(int totalSeconds, bool includeHours)
    {
        int hours = totalSeconds / 3600;
        int minutes = (totalSeconds % 3600) / 60;
        int seconds = totalSeconds % 60;

        if (includeHours || hours > 0)
        {
            return $"{hours}:{minutes:D2}:{seconds:D2}";
        }
        return $"{minutes}:{seconds:D2}";
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
        var control = _activeSession?.ControlSession;
        if (control == null || DurationSeconds <= 0) return;

        ratio = Math.Clamp(ratio, 0.0, 1.0);
        double targetSeconds = ratio * DurationSeconds;
        long requestedTicks = (long)(targetSeconds * TimeSpan.TicksPerSecond);

        lock (_stateLock)
        {
            PositionSeconds = targetSeconds;
            ProgressRatio = ratio;
            _lastTimelinePosition = TimeSpan.FromSeconds(targetSeconds);
            _lastLocalTimestamp = Stopwatch.GetTimestamp();
            _lastWidgetSeekPosition = TimeSpan.FromSeconds(targetSeconds);
            _lastWidgetSeekTimestamp = Stopwatch.GetTimestamp();
            _suppressExternalPositionUpdatesUntil = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 2.5);
            _lastAcceptedOsUpdateTime = DateTimeOffset.MinValue;
        }
        UpdateTimeDisplay(PositionSeconds, DurationSeconds);

        try
        {
            await control.TryChangePlaybackPositionAsync(requestedTicks);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaWidget] Seek failed: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task TogglePlayPauseAsync()
    {
        var control = _activeSession?.ControlSession;
        if (control == null) return;

        try
        {
            bool targetState = !IsPlaying;
            _optimisticPlaybackTarget = targetState;
            _optimisticUntilTimestamp = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 1.5);
            IsPlaying = targetState;

            if (!targetState)
            {
                lock (_stateLock)
                {
                    long now = Stopwatch.GetTimestamp();
                    double elapsed = (double)(now - _lastLocalTimestamp) / Stopwatch.Frequency;
                    if (elapsed > 0)
                    {
                        double newPos = _lastTimelinePosition.TotalSeconds + (elapsed * _playbackRate);
                        if (DurationSeconds > 0 && newPos > DurationSeconds) newPos = DurationSeconds;
                        _lastTimelinePosition = TimeSpan.FromSeconds(newPos);
                        PositionSeconds = newPos;
                        ProgressRatio = DurationSeconds > 0 ? Math.Clamp(newPos / DurationSeconds, 0.0, 1.0) : 0.0;
                        UpdateTimeDisplay(PositionSeconds, DurationSeconds);
                    }
                    _lastLocalTimestamp = now;
                }
                _playbackTimer?.Stop();
            }
            else if (_isHubVisible && HasMedia && !IsLive)
            {
                _lastLocalTimestamp = Stopwatch.GetTimestamp();
                _playbackTimer?.Start();
            }

            await control.TryTogglePlayPauseAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaWidget] TogglePlayPause error: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task SkipNextAsync()
    {
        var control = _activeSession?.ControlSession;
        if (control == null) return;

        try
        {
            await control.TrySkipNextAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaWidget] SkipNext error: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task SkipPreviousAsync()
    {
        var control = _activeSession?.ControlSession;
        if (control == null) return;

        try
        {
            await control.TrySkipPreviousAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaWidget] SkipPrevious error: {ex.Message}");
        }
    }

    private void ResetToNoMedia()
    {
        _activeSession = null;
        _isLiveLocked = false;
        _lastObservedDuration = TimeSpan.Zero;
        _zeroDurationDetectedAt = 0;
        _trackChangedAt = Stopwatch.GetTimestamp();
        HasMedia = false;
        Title = "No media playing";
        Artist = "Open Spotify, YouTube, or VLC";
        Album = string.Empty;
        HasAlbum = false;
        SourceName = "Media Player";
        Thumbnail = null;
        HasThumbnail = false;
        _cachedThumbnailBytes = null;
        lock (_stateLock)
        {
            _currentThumbnailWidth = 0;
        }
        _currentAuraColor = null;
        AmbientGlowBrush = Brushes.Transparent;
        SeekbarBrush = DefaultSeekbarBrush;
        SeekbarGlowColor = DefaultSeekbarColor;
        IsPlaying = false;
        DurationSeconds = 0;
        PositionSeconds = 0;
        ProgressRatio = 0.0;
        TimeDisplayString = string.Empty;
        _lastDisplayedPosSeconds = -1;
        _lastDisplayedDurSeconds = -1;
        IsLive = false;
        CanSeek = true;
        _lastTimelinePosition = TimeSpan.Zero;
        _lastLocalTimestamp = Stopwatch.GetTimestamp();
        _lastWidgetSeekPosition = TimeSpan.Zero;
        _lastWidgetSeekTimestamp = 0;
        _playbackTimer?.Stop();
    }

    public override void Pause()
    {
        _isHubVisible = false;
        _playbackTimer?.Stop();

        lock (_stateLock)
        {
            if (IsPlaying && !IsLive && HasMedia)
            {
                long now = Stopwatch.GetTimestamp();
                double elapsed = (double)(now - _lastLocalTimestamp) / Stopwatch.Frequency;
                if (elapsed > 0)
                {
                    double newPos = _lastTimelinePosition.TotalSeconds + (elapsed * _playbackRate);
                    if (DurationSeconds > 0 && newPos > DurationSeconds)
                    {
                        newPos = DurationSeconds;
                    }
                    _lastTimelinePosition = TimeSpan.FromSeconds(newPos);
                    PositionSeconds = newPos;
                    ProgressRatio = DurationSeconds > 0 ? Math.Clamp(newPos / DurationSeconds, 0.0, 1.0) : 0.0;
                }
                _lastLocalTimestamp = now;
            }
        }
    }

    public override void Resume()
    {
        _isHubVisible = true;

        lock (_stateLock)
        {
            if (IsPlaying && !IsLive && HasMedia)
            {
                long now = Stopwatch.GetTimestamp();
                double elapsedWhileHidden = (double)(now - _lastLocalTimestamp) / Stopwatch.Frequency;
                if (elapsedWhileHidden > 0)
                {
                    double newPos = _lastTimelinePosition.TotalSeconds + (elapsedWhileHidden * _playbackRate);
                    if (DurationSeconds > 0 && newPos > DurationSeconds)
                    {
                        newPos = DurationSeconds;
                    }
                    _lastTimelinePosition = TimeSpan.FromSeconds(newPos);
                    PositionSeconds = newPos;
                    ProgressRatio = DurationSeconds > 0 ? Math.Clamp(newPos / DurationSeconds, 0.0, 1.0) : 0.0;
                    UpdateTimeDisplay(PositionSeconds, DurationSeconds);
                }
                _lastLocalTimestamp = now;
            }
        }

        RunOnUi(() =>
        {
            if (_cachedThumbnailBytes != null && Thumbnail == null && HasMedia)
            {
                var restored = CreateThumbnailFromBytes(_cachedThumbnailBytes);
                if (restored != null)
                {
                    Thumbnail = restored;
                    HasThumbnail = true;
                }
            }

            if (IsPlaying && HasMedia && !IsLive)
            {
                _playbackTimer?.Start();
            }
        });

        if (_activeSession != null)
        {
            try
            {
                var control = _activeSession.ControlSession;
                if (control != null)
                {
                    var playback = control.GetPlaybackInfo();
                    var timeline = control.GetTimelineProperties();
                    RunOnUi(() =>
                    {
                        if (playback != null) ApplyPlaybackInfo(playback);
                        if (timeline != null) ApplyTimelineProperties(timeline);
                    });
                }
            }
            catch { }
        }
    }

    protected override void LoadSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return;
        try
        {
            var parsed = WidgetSerializer.Deserialize<MediaWidgetSettings>(settingsJson);
            if (parsed != null)
            {
                _settings = parsed;
                OnPropertyChanged(nameof(IsAmbientGlowEnabled));
                if (!_settings.IsAmbientGlowEnabled)
                {
                    AmbientGlowBrush = Brushes.Transparent;
                }
            }
        }
        catch { }
    }

    public override void SaveSettings()
    {
        if (string.IsNullOrWhiteSpace(Model.TargetPath) || (Model.TargetPath != "zune" && Model.TargetPath != "media"))
        {
            Model.TargetPath = IsZuneMode ? "zune" : "media";
        }
        Model.SettingsJson = WidgetSerializer.Serialize(_settings);
        MainWindow.Current?.SaveGroupsAndLayout();
    }

    public void SetAmbientGlow(bool enabled)
    {
        if (_settings.IsAmbientGlowEnabled == enabled) return;
        _settings.IsAmbientGlowEnabled = enabled;
        SaveSettings();
        OnPropertyChanged(nameof(IsAmbientGlowEnabled));

        if (enabled && HasThumbnail && _currentAuraColor.HasValue)
        {
            AmbientGlowBrush = CreateAlbumAuraGlow(_currentAuraColor.Value);
        }
        else
        {
            AmbientGlowBrush = Brushes.Transparent;
        }
    }

    private void RunOnUi(Action action)
    {
        var app = Application.Current;
        if (app == null) return;
        if (app.Dispatcher.CheckAccess())
        {
            action();
        }
        else if (!app.Dispatcher.HasShutdownStarted)
        {
            app.Dispatcher.InvokeAsync(action);
        }
    }

    private static async Task<(ImageSource? Image, byte[]? RawBytes)> LoadThumbnailAsync(IRandomAccessStreamReference streamRef)
    {
        try
        {
            using var stream = await streamRef.OpenReadAsync();
            using var netStream = stream.AsStreamForRead();
            using var memory = new MemoryStream();
            await netStream.CopyToAsync(memory);
            byte[] bytes = memory.ToArray();
            memory.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = memory;
            bitmap.EndInit();
            bitmap.Freeze();

            if (bitmap.PixelWidth > bitmap.PixelHeight * 1.25)
            {
                int size = bitmap.PixelHeight;
                int xOffset = (bitmap.PixelWidth - size) / 2;
                var cropped = new CroppedBitmap(bitmap, new Int32Rect(xOffset, 0, size, size));
                cropped.Freeze();
                return (cropped, bytes);
            }

            return (bitmap, bytes);
        }
        catch
        {
            return (null, null);
        }
    }

    private static ImageSource? CreateThumbnailFromBytes(byte[] bytes)
    {
        try
        {
            using var memory = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = memory;
            bitmap.EndInit();
            bitmap.Freeze();

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

    private static RadialGradientBrush CreateAlbumAuraGlow(Color color)
    {
        // 4-stop radial gradient radiating from behind the album artwork,
        // softly dispersing across the obsidian card and fading to transparent.
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.82, 0.45),
            Center = new Point(0.82, 0.45),
            RadiusX = 1.30,
            RadiusY = 1.45,
            GradientStops = new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0x44, color.R, color.G, color.B), 0.0),  // Vivid core aura directly behind album
                new GradientStop(Color.FromArgb(0x28, color.R, color.G, color.B), 0.35), // Smooth luminous dispersion
                new GradientStop(Color.FromArgb(0x12, color.R, color.G, color.B), 0.70), // Subtle ambient bleed
                new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1.0)                    // Fades seamlessly into deep glass
            }
        };
        brush.Freeze();
        return brush;
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
                        seekbarColor = Color.FromRgb(240, 244, 255);
                    }
                    else
                    {
                        seekbarColor = Color.FromRgb(180, 215, 255);
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
            return (DefaultSeekbarBrush, DefaultSeekbarColor);
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

    public static bool HasLiveTitleKeyword(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;

        return title.Contains("● LIVE", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("• LIVE", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("[LIVE]", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("(LIVE)", StringComparison.OrdinalIgnoreCase) ||
               title.Contains(" LIVE ", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("LIVE STREAM", StringComparison.OrdinalIgnoreCase) ||
               title.StartsWith("LIVE:", StringComparison.OrdinalIgnoreCase) ||
               title.StartsWith("LIVE -", StringComparison.OrdinalIgnoreCase) ||
               title.StartsWith("LIVE |", StringComparison.OrdinalIgnoreCase) ||
               title.EndsWith(" LIVE", StringComparison.OrdinalIgnoreCase) ||
               title.EndsWith(" - LIVE", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsKnownNonLiveSource(string? appId, string? title, string? artist)
    {
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
               lower.Contains("microsoft.media.player") ||
               lower.Contains("vlc") ||
               lower.Contains("wmplayer") ||
               lower.Contains("potplayer") ||
               lower.Contains("mpc-hc") ||
               lower.Contains("mpc-be") ||
               lower.Contains("groove") ||
               lower.Contains("deezer") ||
               lower.Contains("qobuz") ||
               lower.Contains("amazonmusic");
    }

    public static string ResolveSourceName(string? appId, string? title, string? artist)
    {
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
        if (lower.Contains("edge") || lower.Contains("msedge")) return "Microsoft Edge";
        if (lower.Contains("chrome")) return "Google Chrome";
        if (lower.Contains("brave")) return "Brave";
        if (lower.Contains("firefox")) return "Firefox";
        if (lower.Contains("opera")) return "Opera";

        string? regName = TryResolveFromRegistry(appId);
        if (!string.IsNullOrWhiteSpace(regName))
        {
            return regName;
        }

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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _isMediaManagerStarted = false;
            if (_playbackTimer != null)
            {
                _playbackTimer.Stop();
                _playbackTimer.Tick -= OnPlaybackTimerTick;
                _playbackTimer = null;
            }

            if (_mediaManager != null)
            {
                try
                {
                    _mediaManager.OnAnySessionOpened -= Manager_OnAnySessionOpened;
                    _mediaManager.OnAnySessionClosed -= Manager_OnAnySessionClosed;
                    _mediaManager.OnFocusedSessionChanged -= Manager_OnFocusedSessionChanged;
                    _mediaManager.OnAnyPlaybackStateChanged -= Manager_OnAnyPlaybackStateChanged;
                    _mediaManager.OnAnyMediaPropertyChanged -= Manager_OnAnyMediaPropertyChanged;
                    _mediaManager.OnAnyTimelinePropertyChanged -= Manager_OnAnyTimelinePropertyChanged;
                    _mediaManager.Dispose();
                }
                catch { }
                _mediaManager = null;
            }

            _activeSession = null;
            Thumbnail = null;
            _cachedThumbnailBytes = null;
        }

        base.Dispose(disposing);
    }
}
