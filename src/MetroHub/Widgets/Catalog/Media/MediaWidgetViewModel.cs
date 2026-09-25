using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetroHub.Core.Media;
using MetroHub.Core.Media.WebNowPlaying;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
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

    /// <summary>Whether the opt-in WebNowPlaying (browser) adapter is enabled for this widget.</summary>
    public bool IsWebNowPlayingEnabled => _settings.WebNowPlayingEnabled;

    /// <summary>Loopback port the adapter is actually bound to (0 when stopped).</summary>
    [ObservableProperty]
    private int _wnpBoundPort;

    /// <summary>True while the browser extension has an open WebSocket to this widget's adapter.</summary>
    [ObservableProperty]
    private bool _wnpConnected;

    /// <summary>One-line connection status for menus and tooltips (port + connected state).</summary>
    [ObservableProperty]
    private string _wnpStatusText = "WebNowPlaying: off";

    /// <summary>Live feed readout for diagnosis (last snapshot + last seek answer). Shown in the tile menu.</summary>
    [ObservableProperty]
    private string _wnpDebugText = "no browser frames yet";

    /// <summary>
    /// Consecutive browser snapshots reporting non-seekable. A single bad frame during
    /// seek/buffer must not flip the clock into the live latch (which zeroes the bar).
    /// </summary>
    private int _wnpNonSeekableFrames;

    /// <summary>Last seek command event id + extension answer (-1/0 = no seek / no answer yet).</summary>
    private int _wnpLastSeekEventId = -1;
    private int _wnpLastSeekResult = -1;

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

    /// <summary>
    /// Content key (title|artist|album) without the player id. Two browser tabs playing the
    /// same video flap the active-player id back and forth; resetting the clock on an
    /// id-only flap pins the bar at 0:00, so the reset fires on content change only.
    /// </summary>
    private string _currentContentKey = string.Empty;
    private bool _isScrubbing = false;
    private int _lastDisplayedPosSeconds = -1;
    private int _lastDisplayedDurSeconds = -1;
    private bool _isHubVisible = true;

    /// <summary>
    /// The single authority for playback position. Every source snapshot goes in through
    /// <see cref="MediaPlaybackClock.Observe"/> and every displayed position comes out of
    /// <see cref="MediaPlaybackClock.Sample"/> — nothing else in this class may write the seekbar.
    /// </summary>
    private readonly MediaPlaybackClock _clock = new();

    // ---- Opt-in WebNowPlaying (browser) adapter -----------------------------------------------

    /// <summary>Null while the adapter is disabled; otherwise hosts the extension's WebSocket.</summary>
    private WebNowPlayingServer? _wnpServer;

    /// <summary>
    /// BUG FIX: one process-wide adapter, not one per widget. Every MediaWidgetViewModel —
    /// including the hidden one inside each QuickControls tile — used to bind its own
    /// TcpListener, so the 2nd+ widget landed on :8642 or failed while the extension fed
    /// only one port. All instances now share a single ref-counted server on one port.
    /// </summary>
    private static readonly object _wnpShareLock = new();
    private static WebNowPlayingServer? _wnpSharedServer;
    private static int _wnpSharedRefs;

    /// <summary>
    /// True while browser media owns the display. While set, every SMTC path is muted so two sources
    /// can never fight over the clock or the bound properties; releasing it resyncs from SMTC.
    /// </summary>
    private volatile bool _wnpOwnsDisplay;

    /// <summary>Latest browser player snapshot (UI thread only).</summary>
    private WnpPlayerSnapshot? _wnpPlayer;

    /// <summary>Stops authoritative refreshes piling up when the source is slow to answer.</summary>
    private bool _refreshInFlight;

    /// <summary>True while the per-frame render hook is subscribed.</summary>
    private bool _isRenderHookAttached;

    /// <summary>
    /// The last transport state the source reported. Timeline events carry no transport information of
    /// their own, so they borrow these rather than reading back the bound properties, which would be a
    /// feedback loop between the clock and the UI.
    /// </summary>
    private bool _lastReportedIsPlaying;
    private double _lastReportedRate = 1.0;
    private bool _lastReportedCanSeek = true;

    /// <summary>
    /// True while playback has stalled: the session still reports Playing, but the seekbar has spent
    /// its whole run-ahead budget and is holding still until the player reports real progress again.
    /// </summary>
    [ObservableProperty]
    private bool _isPlaybackStalled;

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

        ApplyWebNowPlayingSetting();

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
        // While WebNowPlaying owns the display, no SMTC session may take it over.
        if (_wnpOwnsDisplay) return;

        _activeSession = session;

        // A different session is a different context: no position state may survive the switch.
        lock (_stateLock)
        {
            _currentTrackId = string.Empty;
            _currentContentKey = string.Empty;
        }
        _clock.Reset(string.Empty);

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
        if (props == null || _wnpOwnsDisplay) return;

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

        string newTrackId = $"{cleanArtist}|{cleanTitle}|{cleanAlbum}";
        bool isSameTrack = false;
        bool trackChanged = false;
        string oldTrackId = string.Empty;
        lock (_stateLock)
        {
            if (!string.Equals(_currentTrackId, newTrackId, StringComparison.Ordinal))
            {
                oldTrackId = _currentTrackId;
                _currentTrackId = newTrackId;
                _currentThumbnailWidth = 0;
                trackChanged = true;
            }
            else
            {
                isSameTrack = true;
            }
        }

        if (trackChanged && !string.IsNullOrEmpty(oldTrackId))
        {
            // A new track starts cleanly at 0:00. The clock owns that reset, so no position, floor or
            // live latch from the previous item can leak into this one.
            _clock.Reset(newTrackId);
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

            // The clock decides whether this item is a broadcast; the display simply follows it.
            PushSample(Stopwatch.GetTimestamp());
        });
    }

    private void ApplyPlaybackInfo(GlobalSystemMediaTransportControlsSessionPlaybackInfo? playback)
    {
        if (playback == null || _wnpOwnsDisplay) return;

        var controls = playback.Controls;
        if (controls != null)
        {
            CanPlayPause = controls.IsPlayPauseToggleEnabled;
            CanSkipNext = controls.IsNextEnabled;
            CanSkipPrevious = controls.IsPreviousEnabled;
            CanSeek = controls.IsPlaybackPositionEnabled;
        }

        bool isPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        bool canSeek = controls?.IsPlaybackPositionEnabled ?? CanSeek;
        long now = Stopwatch.GetTimestamp();

        _lastReportedIsPlaying = isPlaying;
        _lastReportedRate = playback.PlaybackRate ?? 1.0;
        _lastReportedCanSeek = canSeek;

        // This event carries no timeline, so it must never move the anchor — feeding a position here is
        // exactly the cross-talk that used to produce 0:00 flashes. Transport state only.
        _clock.ObserveTransport(isPlaying, _lastReportedRate, canSeek, now);
        PushSample(now);
    }

    private void ApplyTimelineProperties(GlobalSystemMediaTransportControlsSessionTimelineProperties? timeline)
    {
        if (timeline == null || _wnpOwnsDisplay) return;

        try
        {
            TimeSpan duration = timeline.EndTime - timeline.StartTime;
            if (duration <= TimeSpan.Zero && timeline.MaxSeekTime > timeline.MinSeekTime)
            {
                duration = timeline.MaxSeekTime - timeline.MinSeekTime;
            }

            // Every decision this method used to make — freshness gating, transient-zero rejection,
            // live classification, run-ahead bookkeeping — now lives in the clock, in one place, and is
            // covered by unit tests. All that is left here is translation.
            ObserveAndPush(new MediaObservation
            {
                Position = timeline.Position,
                Duration = duration,
                Rate = _lastReportedRate,
                IsPlaying = _lastReportedIsPlaying,
                CanSeek = _lastReportedCanSeek,
                OsTimestamp = timeline.LastUpdatedTime,
                IsKnownNonLiveSource = IsKnownNonLiveSource(_activeSession?.Id, Title, Artist),
                HasLiveTitleKeyword = HasLiveTitleKeyword(Title),
                TrackId = _currentTrackId,
                ObservedAtTicks = Stopwatch.GetTimestamp()
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaWidget] ApplyTimelineProperties error: {ex.Message}");
        }
    }

    /// <summary>Feeds a source snapshot to the clock, then refreshes the display from it.</summary>
    private void ObserveAndPush(in MediaObservation observation)
    {
        _clock.Observe(in observation);
        MediaPlaybackSample sample = PushSample(observation.ObservedAtTicks);

        if (MediaSyncTraceLogger.IsEnabled)
        {
            MediaSyncTraceLogger.Record(in observation, in sample);
        }
    }

    /// <summary>
    /// Pushes the clock's current reading into the bound properties. THE ONLY writer of the seekbar:
    /// position, duration, progress, live state, stall state and time text all leave through here, so
    /// no two code paths can ever disagree about where the bar is.
    /// </summary>
    private MediaPlaybackSample PushSample(long nowTicks)
    {
        MediaPlaybackSample sample = _clock.Sample(nowTicks);

        IsPlaying = sample.IsPlaying;
        IsLive = sample.IsLive;
        IsPlaybackStalled = sample.IsStalled;

        if (sample.IsLive)
        {
            // A broadcast has no meaningful position or duration: show the bar as inert.
            CanSeek = false;
            DurationSeconds = 0;
            PositionSeconds = 0;
            ProgressRatio = 0.0;
            if (TimeDisplayString.Length != 0)
            {
                TimeDisplayString = string.Empty;
            }
            _lastDisplayedPosSeconds = -1;
            _lastDisplayedDurSeconds = -1;
            SyncRenderHook();
            return sample;
        }

        double durationSeconds = sample.Duration.TotalSeconds;
        if (!_isScrubbing)
        {
            DurationSeconds = durationSeconds;
            PositionSeconds = sample.Position.TotalSeconds;
            ProgressRatio = durationSeconds > 0
                ? Math.Clamp(sample.Position.TotalSeconds / durationSeconds, 0.0, 1.0)
                : 0.0;
            UpdateTimeDisplay(PositionSeconds, durationSeconds);
        }

        SyncRenderHook();

        if (sample.NeedsRefresh)
        {
            RequestAuthoritativeRefresh();
        }

        return sample;
    }

    /// <summary>
    /// Keeps the per-frame render hook in step with what the widget is actually doing. Attached only
    /// while something is moving, so a paused, hidden, broadcast or idle session costs zero frames.
    /// </summary>
    private void SyncRenderHook()
    {
        bool shouldRender = _isHubVisible && HasMedia && IsPlaying && !IsLive && !_isScrubbing;
        if (shouldRender == _isRenderHookAttached)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || !dispatcher.CheckAccess())
        {
            return;   // Not on the UI thread: the next UI-thread push reconciles it.
        }

        if (shouldRender)
        {
            CompositionTarget.Rendering += OnRenderingFrame;
            _isRenderHookAttached = true;
        }
        else
        {
            CompositionTarget.Rendering -= OnRenderingFrame;
            _isRenderHookAttached = false;
        }
    }

    /// <summary>
    /// Samples the clock once per composited frame (~60 Hz) instead of stepping a 250 ms timer. This is
    /// smoother and cheaper at the same time: no timer wake-ups at all, and the bar advances with the
    /// screen refresh rather than in visible quarter-second steps.
    /// </summary>
    private void OnRenderingFrame(object? sender, EventArgs e) => PushSample(Stopwatch.GetTimestamp());

    /// <summary>
    /// Asks the session for one authoritative read. Demand-driven and rate-limited, never a polling
    /// loop: the clock only asks when its anchor has gone stale or a seek needs confirming, and only
    /// one request is ever in flight.
    /// </summary>
    private void RequestAuthoritativeRefresh()
    {
        if (_refreshInFlight) return;

        // WebNowPlaying is a live push stream (250ms cadence): never re-apply an older cached snapshot.
        if (_wnpOwnsDisplay) return;

        var control = _activeSession?.ControlSession;
        if (control == null) return;

        _refreshInFlight = true;
        _ = Task.Run(() =>
        {
            GlobalSystemMediaTransportControlsSessionPlaybackInfo? playback = null;
            GlobalSystemMediaTransportControlsSessionTimelineProperties? timeline = null;

            try
            {
                playback = control.GetPlaybackInfo();
                timeline = control.GetTimelineProperties();
            }
            catch
            {
                // A session that vanished mid-query is not an error worth surfacing.
            }

            RunOnUi(() =>
            {
                _refreshInFlight = false;
                if (_activeSession?.ControlSession != control) return;
                if (playback != null) ApplyPlaybackInfo(playback);
                if (timeline != null) ApplyTimelineProperties(timeline);
            });
        });
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
        if (IsLive || !CanSeek)
        {
            if (_wnpOwnsDisplay)
            {
                MainWindow.Current?.ShowToast("Seek not available on this video (live / ad / no permission).", isError: true);
            }
            return;
        }

        if (_wnpOwnsDisplay)
        {
            WnpPlayerSnapshot? player = _wnpPlayer;
            if (player == null || DurationSeconds <= 0) return;

            ratio = Math.Clamp(ratio, 0.0, 1.0);
            double wnpTargetSeconds = ratio * DurationSeconds;
            int wnpTargetWhole = (int)Math.Round(wnpTargetSeconds);

            lock (_stateLock)
            {
                PositionSeconds = wnpTargetSeconds;
                ProgressRatio = ratio;
                IsPlaybackStalled = false;
            }

            _clock.NotifySeekRequested(TimeSpan.FromSeconds(wnpTargetSeconds));
            UpdateTimeDisplay(PositionSeconds, DurationSeconds);

            try
            {
                var server = _wnpServer;
                if (server == null)
                {
                    MainWindow.Current?.ShowToast("WebNowPlaying: not listening — seek not sent.", isError: true);
                    return;
                }

                int eventId = await server.SetPositionAsync(player.Id, wnpTargetWhole);
                if (eventId <= 0)
                {
                    MainWindow.Current?.ShowToast("WebNowPlaying: extension not connected — seek not sent.", isError: true);
                    return;
                }

                Debug.WriteLine($"[MediaWidget] WebNowPlaying seek → player {player.Id} :{server.BoundPort} to {wnpTargetWhole}s (event {eventId})");
                HiddenDiagnosticsLogger.Log($"[WNP] seek → player {player.Id} :{server.BoundPort} to {wnpTargetWhole}s (event {eventId})");

                // 0 = no answer in 1s (unknown), 1 = applied, 2 = rejected by the site.
                int result = await server.WaitForEventResultAsync(eventId, TimeSpan.FromSeconds(1));
                _wnpLastSeekEventId = eventId;
                _wnpLastSeekResult = result;
                Debug.WriteLine($"[MediaWidget] WebNowPlaying seek event {eventId} result={result} (0=timeout 1=ok 2=rejected)");
                HiddenDiagnosticsLogger.Log($"[WNP] seek event {eventId} result={result} (0=timeout 1=ok 2=rejected)");
                WnpDebugText = $"'{player.Name}' seek→{wnpTargetWhole}s ev={eventId} res={result} (0=timeout 1=ok 2=refused)";
                if (result == 2)
                {
                    MainWindow.Current?.ShowToast("Browser refused the seek (live / ad / no permission).", isError: true);
                }
                else if (result == 0)
                {
                    MainWindow.Current?.ShowToast("Browser didn't answer the seek — check the extension tab.", isError: true);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaWidget] WebNowPlaying seek failed: {ex.Message}");
            }
            return;
        }

        var control = _activeSession?.ControlSession;
        if (control == null || DurationSeconds <= 0) return;

        ratio = Math.Clamp(ratio, 0.0, 1.0);
        double targetSeconds = ratio * DurationSeconds;
        long requestedTicks = (long)(targetSeconds * TimeSpan.TicksPerSecond);

        lock (_stateLock)
        {
            PositionSeconds = targetSeconds;
            ProgressRatio = ratio;
            IsPlaybackStalled = false;
        }

        // The clock owns both the optimistic jump and the echo handling: the source's echo of this exact
        // request is trusted on sight, so the old suppression window and widget-echo rejection are gone.
        _clock.NotifySeekRequested(TimeSpan.FromSeconds(targetSeconds));
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
        if (_wnpOwnsDisplay)
        {
            WnpPlayerSnapshot? player = _wnpPlayer;
            if (player == null || _wnpServer == null) return;

            try
            {
                bool targetState = player.State != WnpState.Playing;
                long now = Stopwatch.GetTimestamp();

                _clock.NotifyTransportRequested(targetState, now);
                _lastReportedIsPlaying = targetState;
                IsPlaybackStalled = false;
                PushSample(now);

                await _wnpServer.SetStateAsync(player.Id, targetState);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaWidget] WebNowPlaying toggle failed: {ex.Message}");
            }
            return;
        }

        var control = _activeSession?.ControlSession;
        if (control == null) return;

        try
        {
            bool targetState = !IsPlaying;
            long now = Stopwatch.GetTimestamp();

            // The clock carries the intent through a short grace window, so the transport never flickers
            // while the OS processes the command asynchronously.
            _clock.NotifyTransportRequested(targetState, now);
            _lastReportedIsPlaying = targetState;
            IsPlaybackStalled = false;
            PushSample(now);

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
        if (_wnpOwnsDisplay)
        {
            if (_wnpPlayer is not { } player || _wnpServer == null) return;
            try { await _wnpServer.SkipNextAsync(player.Id); }
            catch (Exception ex) { Debug.WriteLine($"[MediaWidget] WebNowPlaying next failed: {ex.Message}"); }
            return;
        }

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
        if (_wnpOwnsDisplay)
        {
            if (_wnpPlayer is not { } player || _wnpServer == null) return;
            try { await _wnpServer.SkipPreviousAsync(player.Id); }
            catch (Exception ex) { Debug.WriteLine($"[MediaWidget] WebNowPlaying previous failed: {ex.Message}"); }
            return;
        }

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
        _clock.Reset(string.Empty);
        _lastReportedIsPlaying = false;
        _lastReportedRate = 1.0;
        _lastReportedCanSeek = true;
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
        IsPlaybackStalled = false;
        SyncRenderHook();
    }

    public override void Pause()
    {
        _isHubVisible = false;
        IsPlaybackStalled = false;

        // Detach the frame hook and freeze the display where it is. The clock keeps its anchor; the price
        // of a stale anchor while hidden is a single authoritative read when the hub comes back.
        SyncRenderHook();
    }

    public override void Resume()
    {
        _isHubVisible = true;

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

            // While hidden the clock deliberately stopped tracking, so its anchor may be stale. Pushing
            // now repaints immediately and asks the session for the truth when the gap was long enough.
            PushSample(Stopwatch.GetTimestamp());
        });

        if (!_wnpOwnsDisplay && _activeSession != null)
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
        Model.TargetPath = "media";
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

    // ---- WebNowPlaying (browser) source -------------------------------------------------------

    /// <summary>Toggles the opt-in WebNowPlaying adapter and persists the choice.</summary>
    public void SetWebNowPlaying(bool enabled)
    {
        if (_settings.WebNowPlayingEnabled == enabled) return;
        _settings.WebNowPlayingEnabled = enabled;
        SaveSettings();
        OnPropertyChanged(nameof(IsWebNowPlayingEnabled));
        ApplyWebNowPlayingSetting();
    }

    private void ApplyWebNowPlayingSetting()
    {
        if (_settings.WebNowPlayingEnabled)
        {
            StartWebNowPlaying();
        }
        else
        {
            StopWebNowPlaying();
        }
    }

    private void StartWebNowPlaying()
    {
        if (_wnpServer != null) return;

        WebNowPlayingServer? server;
        bool failed;
        lock (_wnpShareLock)
        {
            server = _wnpSharedServer;
            if (server == null)
            {
                var fresh = new WebNowPlayingServer();
                if (!fresh.Start())
                {
                    fresh.Dispose();
                    server = null;
                    failed = true;
                }
                else
                {
                    _wnpSharedServer = server = fresh;
                    failed = false;
                }
            }
            else
            {
                failed = false;
            }

            if (!failed) _wnpSharedRefs++;
        }

        if (failed || server == null)
        {
            // Port unavailable (another app already owns both): stay on SMTC, log, no crash.
            RunOnUi(() =>
            {
                WnpBoundPort = 0;
                WnpConnected = false;
                WnpStatusText = "WebNowPlaying: port busy (8974/8642)";
            });
            MainWindow.Current?.ShowToast("WebNowPlaying: port busy — browser adapter not listening.", isError: true);
            return;
        }

        server.ActivePlayerChanged += Wnp_OnActivePlayerChanged;
        server.CoverReceived += Wnp_OnCoverReceived;
        server.ConnectionChanged += Wnp_OnConnectionChanged;
        _wnpServer = server;
        RunOnUi(UpdateWnpStatus);
    }

    private void StopWebNowPlaying()
    {
        WebNowPlayingServer? server = _wnpServer;
        if (server == null) return;
        _wnpServer = null;

        server.ActivePlayerChanged -= Wnp_OnActivePlayerChanged;
        server.CoverReceived -= Wnp_OnCoverReceived;
        server.ConnectionChanged -= Wnp_OnConnectionChanged;

        // Hand THIS widget back to SMTC; the shared adapter stays up for other widgets.
        _wnpOwnsDisplay = false;
        _wnpPlayer = null;
        _wnpNonSeekableFrames = 0;
        RunOnUi(ResyncAfterWebNowPlaying);

        bool last;
        lock (_wnpShareLock)
        {
            _wnpSharedRefs--;
            last = _wnpSharedRefs <= 0;
            if (last)
            {
                _wnpSharedServer = null;
                _wnpSharedRefs = 0;
            }
        }
        if (last) server.Dispose();

        RunOnUi(() =>
        {
            WnpBoundPort = 0;
            WnpConnected = false;
            WnpStatusText = "WebNowPlaying: off";
        });
    }

    private void Wnp_OnConnectionChanged() => RunOnUi(UpdateWnpStatus);

    private void UpdateWnpStatus()
    {
        var server = _wnpServer;
        if (server == null || !server.IsRunning)
        {
            WnpBoundPort = 0;
            WnpConnected = false;
            WnpStatusText = _settings.WebNowPlayingEnabled
                ? "WebNowPlaying: not listening"
                : "WebNowPlaying: off";
            return;
        }

        WnpBoundPort = server.BoundPort;
        WnpConnected = server.IsConnected;
        WnpStatusText = WnpConnected
            ? $"WebNowPlaying: connected (:{WnpBoundPort})"
            : $"WebNowPlaying: listening :{WnpBoundPort} — point the extension here";
    }

    private void Wnp_OnActivePlayerChanged(WnpPlayerSnapshot? snapshot) =>
        RunOnUi(() =>
        {
            UpdateWnpStatus();
            ApplyWebNowPlayingSnapshot(snapshot);
        });

    /// <summary>
    /// Folds one browser-player snapshot into the display: metadata straight to the bound properties,
    /// position through the clock — the same single-writer rule SMTC snapshots follow.
    /// </summary>
    private void ApplyWebNowPlayingSnapshot(WnpPlayerSnapshot? snapshot)
    {
        if (snapshot == null)
        {
            if (!_wnpOwnsDisplay) return;

            _wnpOwnsDisplay = false;
            _wnpPlayer = null;
            ResyncAfterWebNowPlaying();
            return;
        }

        _wnpOwnsDisplay = true;
        _wnpPlayer = snapshot;

        string cleanTitle = snapshot.Title.Trim();
        string cleanArtist = snapshot.Artist.Trim();
        string cleanAlbum = snapshot.Album.Trim();

        string newTrackId = $"wnp|{snapshot.Id}|{cleanArtist}|{cleanTitle}|{cleanAlbum}";
        string newContentKey = $"{cleanArtist}|{cleanTitle}|{cleanAlbum}";
        bool trackChanged;
        bool contentChanged;
        string oldTrackId = string.Empty;
        lock (_stateLock)
        {
            trackChanged = !string.Equals(_currentTrackId, newTrackId, StringComparison.Ordinal);
            contentChanged = !string.Equals(_currentContentKey, newContentKey, StringComparison.Ordinal);
            if (trackChanged)
            {
                oldTrackId = _currentTrackId;
                _currentTrackId = newTrackId;
                _currentContentKey = newContentKey;
                _currentThumbnailWidth = 0;
            }
        }

        if (trackChanged && !string.IsNullOrEmpty(oldTrackId))
        {
            if (contentChanged)
            {
                // A different track starts clean: no position, floor or live latch may leak into it.
                _clock.Reset(newTrackId);
                _wnpNonSeekableFrames = 0;
            }
            else
            {
                // Id-only flap (two tabs, same video): keep the clock, or the bar pins at 0:00.
                Debug.WriteLine($"[MediaWidget] WNP id flap {oldTrackId} -> {newTrackId} (same content): clock kept.");
                HiddenDiagnosticsLogger.Log($"[WNP] id flap kept clock: {oldTrackId} -> {newTrackId}");
            }
        }

        Title = cleanTitle.Length == 0 ? "Unknown Track" : cleanTitle;
        Artist = cleanArtist;
        Album = cleanAlbum;
        HasAlbum = cleanAlbum.Length > 0;
        SourceName = string.IsNullOrWhiteSpace(snapshot.Name) ? "WebNowPlaying" : snapshot.Name;
        CanPlayPause = snapshot.CanSetState;
        CanSkipNext = snapshot.CanSkipNext;
        CanSkipPrevious = snapshot.CanSkipPrevious;
        HasMedia = true;

        bool isPlaying = snapshot.State == WnpState.Playing;

        // Hysteresis: the extension flickers CanSetPosition=0 / duration=0 for a frame
        // during seek and buffering. Feeding that straight to the clock latches IsLive
        // (non-seekable + known duration = broadcast) and zeroes the bar — the exact
        // "seek resets to zero" symptom. Require consecutive bad frames, keep the last
        // known duration, and never disqualify while our own seek is still pending.
        int effectiveDuration = snapshot.DurationSeconds > 0
            ? snapshot.DurationSeconds
            : (int)Math.Round(DurationSeconds);
        bool reportedCanSeek = snapshot.CanSetPosition && effectiveDuration > 0;

        bool canSeek;
        if (reportedCanSeek)
        {
            _wnpNonSeekableFrames = 0;
            canSeek = true;
        }
        else if (_clock.HasPendingSeek)
        {
            canSeek = true;   // Our seek is in flight: the echo hasn't arrived yet.
        }
        else if (_wnpNonSeekableFrames < 2)
        {
            _wnpNonSeekableFrames++;
            canSeek = CanSeek;   // Hold the previous state for up to 2 bad frames.
            if (!canSeek && DurationSeconds > 0)
            {
                // Stay seekable while we still have a known duration; the clock's
                // sticky duration survives the transient either way.
                canSeek = true;
            }
        }
        else
        {
            canSeek = false;
        }

        _lastReportedIsPlaying = isPlaying;
        _lastReportedRate = 1.0;
        _lastReportedCanSeek = canSeek;
        bool canSeekFlipped = CanSeek != canSeek;
        CanSeek = canSeek;

        // Anomaly trail for the "seek resets to zero" hunt: one line only when the
        // feed looks suspicious (capability flicker, zero position/duration mid-track).
        // Debug.WriteLine is invisible in Release, so suspicious frames also go to
        // hidden_diagnostics.log, which can be pasted without a debugger attached.
        if (canSeekFlipped || snapshot.DurationSeconds <= 0 || snapshot.PositionSeconds <= 0)
        {
            string anomaly = $"[WNP] snap id={snapshot.Id} pos={snapshot.PositionSeconds} dur={snapshot.DurationSeconds} " +
                $"canPos={snapshot.CanSetPosition} effSeek={canSeek} state={snapshot.State} pending={_clock.HasPendingSeek} shown={PositionSeconds:F1}/{DurationSeconds:F0}";
            Debug.WriteLine("[MediaWidget] " + anomaly);
            // Zero-position frames mid-track are the snap-to-zero suspect: always file them.
            // Capability flips are one line per flip. Healthy 1Hz traffic stays silent.
            if (snapshot.PositionSeconds <= 0 || canSeekFlipped)
            {
                HiddenDiagnosticsLogger.Log(anomaly);
            }
        }

        WnpDebugText = $"'{snapshot.Name}' pos={snapshot.PositionSeconds}/{snapshot.DurationSeconds}s " +
            $"canSeek={snapshot.CanSetPosition} state={snapshot.State} seekEv={_wnpLastSeekEventId} res={_wnpLastSeekResult}";

        ObserveAndPush(new MediaObservation
        {
            Position = TimeSpan.FromSeconds(snapshot.PositionSeconds),
            Duration = TimeSpan.FromSeconds(snapshot.DurationSeconds),
            Rate = 1.0,
            IsPlaying = isPlaying,
            CanSeek = canSeek,
            OsTimestamp = DateTimeOffset.MinValue,
            IsKnownNonLiveSource = false,
            HasLiveTitleKeyword = HasLiveTitleKeyword(Title),
            TrackId = newTrackId,
            ObservedAtTicks = Stopwatch.GetTimestamp()
        });
    }

    private void Wnp_OnCoverReceived(int playerId, byte[] png)
    {
        if (!_wnpOwnsDisplay) return;

        _ = Task.Run(() =>
        {
            try
            {
                if (CreateThumbnailFromBytes(png) is not BitmapSource bitmap) return;

                var (seekbarBrush, seekbarColor) = ExtractSeekbarBrush(bitmap);
                Brush glowBrush = _settings.IsAmbientGlowEnabled
                    ? CreateAlbumAuraGlow(seekbarColor)
                    : Brushes.Transparent;

                RunOnUi(() =>
                {
                    if (!_wnpOwnsDisplay || _wnpPlayer?.Id != playerId) return;

                    _cachedThumbnailBytes = png;
                    Thumbnail = bitmap;
                    HasThumbnail = true;
                    SeekbarBrush = seekbarBrush;
                    SeekbarGlowColor = seekbarColor;
                    _currentAuraColor = seekbarColor;
                    AmbientGlowBrush = glowBrush;
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaWidget] WebNowPlaying cover failed: {ex.Message}");
            }
        });
    }

    /// <summary>Hands the display back to SMTC after browser media stops reporting.</summary>
    private void ResyncAfterWebNowPlaying()
    {
        _lastReportedIsPlaying = false;
        _lastReportedRate = 1.0;
        _lastReportedCanSeek = true;
        _wnpNonSeekableFrames = 0;

        var session = SafeGetFallbackSession();
        if (session != null)
        {
            SetActiveSession(session);
        }
        else
        {
            ResetToNoMedia();
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

    private static LinearGradientBrush CreateAlbumAuraGlow(Color primaryColor)
    {
        var (h, s, v) = RgbToHsv(primaryColor.R, primaryColor.G, primaryColor.B);

        Color leftColor;
        Color rightColor;

        if (s < 0.15)
        {
            // Monochrome / desaturated: sophisticated steel to slate mist
            leftColor = Color.FromArgb(0xEE, 0x47, 0x55, 0x69);
            rightColor = Color.FromArgb(0xEE, 0x64, 0x74, 0x8B);
        }
        else
        {
            // Harmonic analogous color shift (+32 degrees on color wheel)
            // e.g. Amber -> Coral, Cyan -> Ocean Azure, Purple -> Electric Indigo
            double harmonicH = (h + 32.0) % 360.0;
            double harmonicS = Math.Clamp(s * 0.95, 0.60, 0.95);
            double harmonicV = Math.Clamp(v * 1.15, 0.85, 1.00);
            Color harmColor = ColorFromHsv(harmonicH, harmonicS, harmonicV);

            // Punchy primary under album artwork on right side
            Color punchyPrimary = ColorFromHsv(h, Math.Clamp(s * 1.15, 0.70, 0.98), Math.Clamp(v * 1.15, 0.85, 1.00));

            // Set alpha to 0xEE (matching Weather Horizon Aura exact vibrancy)
            leftColor = Color.FromArgb(0xEE, harmColor.R, harmColor.G, harmColor.B);
            rightColor = Color.FromArgb(0xEE, punchyPrimary.R, punchyPrimary.G, punchyPrimary.B);
        }

        // Exact horizontal horizon gradient (StartPoint="0,0", EndPoint="1,0")
        // Combined with the vertical OpacityMask on Border in XAML, this creates the
        // identical grounded, crisp, radiant bottom horizon as the Weather widget!
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            GradientStops = new GradientStopCollection
            {
                new GradientStop(leftColor, 0.0),
                new GradientStop(rightColor, 1.0)
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

            // WebNowPlaying first: mute the ownership flag so the release callback cannot resurrect
            // SMTC state on a widget that is going away.
            _wnpOwnsDisplay = false;
            _wnpPlayer = null;
            WebNowPlayingServer? wnpServer = _wnpServer;
            _wnpServer = null;
            if (wnpServer != null)
            {
                wnpServer.ActivePlayerChanged -= Wnp_OnActivePlayerChanged;
                wnpServer.CoverReceived -= Wnp_OnCoverReceived;
                wnpServer.ConnectionChanged -= Wnp_OnConnectionChanged;

                bool last;
                lock (_wnpShareLock)
                {
                    _wnpSharedRefs--;
                    last = _wnpSharedRefs <= 0;
                    if (last)
                    {
                        _wnpSharedServer = null;
                        _wnpSharedRefs = 0;
                    }
                }
                if (last) wnpServer.Dispose();
            }

            // Detach the frame hook first: a rendering callback firing into a half-disposed widget is
            // exactly the kind of leak this teardown exists to prevent.
            if (_isRenderHookAttached)
            {
                CompositionTarget.Rendering -= OnRenderingFrame;
                _isRenderHookAttached = false;
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
