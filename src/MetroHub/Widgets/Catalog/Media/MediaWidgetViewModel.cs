using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetroHub.Core.Models;
using MetroHub.Widgets.Serialization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;
using WindowsMediaController;

namespace MetroHub.Widgets.Catalog.Media;

/// <summary>
/// High-performance, lightweight, event-driven Media Widget ViewModel powered by Dubya.WindowsMediaController.
/// Directly connects to Windows SMTC (System Media Transport Controls) without polling timers or web servers.
/// Idle CPU is 0.0%.
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

    /// <summary>
    /// Tiny (~64px) copy of the current artwork, used as the soft backdrop
    /// behind wide, letterboxed art on the Zune screen. Decoded from the same
    /// bytes as Thumbnail, so a square cover fills its own slot and leaves this
    /// null. Costs ~16 KB instead of the megabytes a second full-size surface
    /// would.
    /// </summary>
    [ObservableProperty]
    private ImageSource? _artworkBackdrop;

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
    /// Cap for the resident artwork byte cache: originals larger than this
    /// (e.g. 3000px Apple Music art) are re-encoded once at display size so
    /// the cache stays compact.
    /// </summary>
    private const int MaxCacheBytes = 512 * 1024;

    /// <summary>
    /// Artwork retry schedule for tracks whose SMTC metadata arrives before
    /// the player supplies artwork (typical browser behavior).
    /// </summary>
    private const int MaxArtworkRetries = 3;
    private static readonly int[] ArtworkRetryDelaysMs = { 1500, 4000, 8000 };

    /// <summary>
    /// Upper bound on remembered source-app stand-in fingerprints (browser and
    /// app icons). Browsers only ever expose a couple of these.
    /// </summary>
    private const int MaxStandInHashes = 16;

    /// <summary>
    /// Grace period before showing "No media playing" after a session close.
    /// Chromium tears down and recreates its SMTC session around every track
    /// change (and every widget-initiated skip), so an immediate reset wipes
    /// artwork and metadata for songs that are still playing.
    /// </summary>
    private const int NoMediaGraceMs = 2500;

    /// <summary>
    /// Retains compressed artwork bytes in managed memory (typically 20–80 KB,
    /// capped at MaxCacheBytes). Allows the heavy decoded WPF BitmapSource
    /// surface to be released when MetroHub is hidden, and instantly re-decoded
    /// with 0ms latency when restored.
    /// </summary>
    private byte[]? _cachedThumbnailBytes;

    /// <summary>
    /// Fingerprint and owning track of the artwork currently on screen.
    ///
    /// SMTC hands out a *sequence* of images per track: browsers publish a
    /// stand-in (their own icon / the page favicon) the moment a session opens,
    /// then swap in the real cover a second or two later. Ranking images by
    /// pixel width (the previous approach) loses that race whenever the
    /// stand-in happens to be the larger image — Brave's icon is 256px while
    /// YouTube's cover arrives at 150px, so the cover was decoded and then
    /// thrown away. Comparing fingerprints instead lets any *different* image
    /// through, which is exactly what the OS shell does.
    /// </summary>
    private ulong _currentArtHash;
    private string _currentArtTrackId = string.Empty;

    /// <summary>Displayed image is a known source-app stand-in.</summary>
    private bool _currentArtIsStandIn;

    /// <summary>Source pixel width of the displayed artwork.</summary>
    private int _currentArtSourceWidth;

    /// <summary>A delayed re-read returned this exact, non-stand-in image.</summary>
    private bool _currentArtConfirmed;

    /// <summary>True once any artwork was accepted for the current track.</summary>
    private bool _hasArtworkForCurrentTrack;

    /// <summary>
    /// First artwork seen for the last track that had artwork, plus that track's
    /// id. A fingerprint that opens two *different* tracks is by definition the
    /// source app's stand-in and never album art.
    /// </summary>
    private ulong _firstArtHash;
    private string _firstArtTrackId = string.Empty;

    /// <summary>Fingerprints proven to be stand-ins (browser and app icons).</summary>
    private readonly HashSet<ulong> _standInHashes = new();

    private MediaWidgetSettings _settings = new();

    public bool IsSlimMode => Model.SpanY == 1;
    public bool IsZuneMode => Model.SpanX == 4 && Model.SpanY == 6;
    public bool IsStandardMode => Model.SpanY > 1 && !IsZuneMode;

    public double AlbumArtSize => Model.SpanY switch
    {
        >= 4 => 132.0,
        3 => 104.0,
        _ => 64.0
    };

    /// <summary>
    /// Zune mode paints its artwork full-bleed in a 248pt square. 620px covers
    /// that 1:1 even on a 200% DPI display (496 device pixels) with headroom to
    /// spare; decoding wider than the slot only burns RAM.
    /// </summary>
    private const int ZuneArtDecodeWidth = 620;

    /// <summary>
    /// The largest artwork in the other layouts is 132pt; 384px covers it at
    /// 200% DPI (264 device pixels) and leaves room for UniformToFill.
    /// </summary>
    private const int StandardArtDecodeWidth = 384;

    /// <summary>
    /// Target decode width for the current layout, sized to what the layout can
    /// actually paint rather than to the source. A decoded WPF bitmap is a
    /// Pbgra32 surface costing width × height × 4 bytes (plus a GPU texture
    /// copy), so an oversized budget is pure waste: Zune used to ask for 1024px
    /// — 4.2 MB for a 248pt slot whose source is usually 150px — and now asks
    /// for 620px (1.5 MB, and ~2.8 MB even for a wide 16:9 source that decodes
    /// wider than the crop needs). Sources smaller than the budget decode at
    /// native size and are upscaled once by the renderer.
    ///
    /// Artwork-less instances never decode, so the budget is irrelevant to them.
    /// </summary>
    private int DecodeTargetWidth => IsZuneMode ? ZuneArtDecodeWidth : StandardArtDecodeWidth;

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

    private readonly object _stateLock = new();
    private string _currentTrackId = string.Empty;

    /// <summary>
    /// Track ID whose artwork retry is currently scheduled (null when none).
    /// Prevents stacked retry loops for the same track.
    /// </summary>
    private string? _pendingArtworkRetryTrackId;

    /// <summary>
    /// When false, this instance skips the entire SMTC artwork pipeline
    /// (thumbnail stream fetch, decode, crop, byte cache). Used by hosts
    /// that render no artwork, e.g. the QuickControls media strip.
    /// </summary>
    private readonly bool _loadArtwork;

    /// <summary>True when this instance fetches and caches album art.</summary>
    public bool LoadsArtwork => _loadArtwork;

    public MediaWidgetViewModel(TileModel model, bool loadArtwork = true) : base(model)
    {
        _loadArtwork = loadArtwork;

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
            LogArt($"focus changed → {(mediaSession?.Id ?? "null")} | active={_activeSession?.Id ?? "null"}");

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
                    LogArt($"focus lost, no fallback — grace period {NoMediaGraceMs}ms before reset");
                    _ = ScheduleNoMediaGraceCheckAsync(_activeSession?.Id ?? string.Empty);
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
            LogArt($"session opened: {mediaSession.Id} | active={_activeSession?.Id ?? "null"}");

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
            if (mediaSession == null) return;

            LogArt($"session closed: {mediaSession.Id} | active={_activeSession?.Id ?? "null"}");

            if (_activeSession == null || _activeSession.Id == mediaSession.Id)
            {
                var next = SafeGetFallbackSession(excludeSessionId: mediaSession.Id);
                if (next != null)
                {
                    SetActiveSession(next);
                }
                else
                {
                    // The session may be recreated milliseconds later (Chromium
                    // churns its SMTC session on every track change / skip).
                    // Grace-period instead of wiping artwork immediately.
                    LogArt($"active session closed — grace period {NoMediaGraceMs}ms before reset");
                    _ = ScheduleNoMediaGraceCheckAsync(mediaSession.Id);
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

            // Auto-switch to newly playing session if our current session is inactive or different
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
            if (mediaSession == null) return;

            if (_activeSession == null)
            {
                SetActiveSession(mediaSession);
                return;
            }

            if (_activeSession.Id == mediaSession.Id)
            {
                _ = ApplyMediaPropertiesAsync(mediaSession, mediaProperties);
            }
            else
            {
                LogArt($"props event IGNORED (different session): event={mediaSession.Id} active={_activeSession.Id} title=\"{mediaProperties.Title}\" thumb={(mediaProperties.Thumbnail != null ? "yes" : "no")}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaWidget] OnAnyMediaPropertyChanged failed: {ex.Message}");
        }
    }

    private void SetActiveSession(MediaManager.MediaSession session)
    {
        LogArt($"ACTIVE SESSION → \"{session.Id}\"");
        _activeSession = session;

        lock (_stateLock)
        {
            _currentTrackId = string.Empty;
            _pendingArtworkRetryTrackId = null;
        }

        try
        {
            var control = session.ControlSession;
            if (control != null)
            {
                var playback = control.GetPlaybackInfo();

                RunOnUi(() =>
                {
                    if (playback != null) ApplyPlaybackInfo(playback);
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

    private async Task ApplyMediaPropertiesAsync(
        MediaManager.MediaSession session,
        GlobalSystemMediaTransportControlsSessionMediaProperties? props,
        int settlingAttempt = 0)
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

        string newTrackId = $"{cleanArtist}|{cleanTitle}|{cleanAlbum}";
        lock (_stateLock)
        {
            if (!string.Equals(_currentTrackId, newTrackId, StringComparison.Ordinal))
            {
                _currentTrackId = newTrackId;
                _currentArtConfirmed = false;
                _hasArtworkForCurrentTrack = false;
                _pendingArtworkRetryTrackId = null;
            }
        }

        ImageSource? bmp = null;
        ImageSource? backdrop = null;
        byte[]? rawBytes = null;
        bool updateArtwork = false;
        int decodeTarget = DecodeTargetWidth;

        if (!_loadArtwork)
        {
            // Artwork-less mode (e.g. QuickControls embed): the host view only
            // renders track info and transport controls, so skip the SMTC
            // thumbnail fetch/decode/cache entirely. rawBytes stays null, which
            // also guarantees no stale byte cache can accumulate below.
        }
        else
        {
            byte[]? thumbnailBytes = props.Thumbnail != null
                ? await ReadThumbnailBytesAsync(props.Thumbnail)
                : null;

            if (thumbnailBytes == null)
            {
                // Track metadata arrived before artwork (browsers publish the
                // title first; the page supplies art asynchronously), or the
                // stream was swapped mid-track-change. Keep the previous art on
                // screen instead of flashing the placeholder — the settling
                // poll below heals it.
                LogArt($"art missing track=\"{newTrackId}\" (no thumbnail in SMTC snapshot)");
            }
            else
            {
                ulong artHash = Fingerprint(thumbnailBytes);

                // Header-only dimensions, no pixel decode. Publishers cycle
                // several size variants of the same cover (the diagnostic log
                // shows 150×150 followed by 120×120 for one track), so a
                // same-track replacement is only worth a repaint when it is
                // actually bigger.
                (int sourceWidth, _) = ReadSourceDimensions(thumbnailBytes);
                bool acceptArtwork = false;

                lock (_stateLock)
                {
                    if (_currentArtHash == artHash)
                    {
                        // The publisher re-sent the image that is already on
                        // screen: skip the decode and the repaint. A repeat seen
                        // by a *delayed* re-read means the publisher settled on
                        // this image, which lets the settling poll stop early —
                        // but only from the second attempt on, so a slow browser
                        // cannot confirm its stand-in before the cover lands.
                        _hasArtworkForCurrentTrack = true;

                        if (settlingAttempt >= 2 &&
                            !_currentArtIsStandIn &&
                            string.Equals(_currentArtTrackId, newTrackId, StringComparison.Ordinal))
                        {
                            _currentArtConfirmed = true;
                        }

                        LogArt($"art unchanged track=\"{newTrackId}\" bytes={thumbnailBytes.Length}");
                    }
                    else if (_standInHashes.Contains(artHash) && _currentArtHash != 0 && !_currentArtIsStandIn)
                    {
                        // Known stand-in (browser icon / page favicon) arriving
                        // after real artwork. Never let it cover the cover.
                        LogArt($"stand-in re-published track=\"{newTrackId}\" — ignored");
                    }
                    else if (sourceWidth > 0 &&
                             !_currentArtIsStandIn &&
                             _currentArtHash != 0 &&
                             string.Equals(_currentArtTrackId, newTrackId, StringComparison.Ordinal) &&
                             sourceWidth <= _currentArtSourceWidth)
                    {
                        // A smaller (or equal) variant of the cover that is
                        // already on screen for this track: keep the sharper
                        // one instead of downgrading the picture.
                        LogArt($"cover variant {sourceWidth}px ignored (showing {_currentArtSourceWidth}px) track=\"{newTrackId}\"");
                    }
                    else
                    {
                        acceptArtwork = true;
                    }
                }

                if (acceptArtwork)
                {
                    BitmapSource? decoded = DecodeArtwork(
                        thumbnailBytes,
                        decodeTarget,
                        out int originalWidth,
                        out int originalHeight);

                    if (decoded != null && originalWidth > 0)
                    {
                        bool isStandIn;

                        lock (_stateLock)
                        {
                            isStandIn = _standInHashes.Contains(artHash);

                            if (!_hasArtworkForCurrentTrack)
                            {
                                // A fingerprint that opens two different tracks is
                                // the source app's stand-in, not album art: a
                                // browser icon is byte-identical for every song,
                                // a cover is not.
                                if (artHash == _firstArtHash &&
                                    _firstArtTrackId.Length > 0 &&
                                    !string.Equals(_firstArtTrackId, newTrackId, StringComparison.Ordinal))
                                {
                                    if (_standInHashes.Count >= MaxStandInHashes)
                                    {
                                        _standInHashes.Clear();
                                    }

                                    _standInHashes.Add(artHash);
                                    isStandIn = true;
                                }

                                _firstArtHash = artHash;
                                _firstArtTrackId = newTrackId;
                            }

                            _currentArtHash = artHash;
                            _currentArtTrackId = newTrackId;
                            _currentArtIsStandIn = isStandIn;
                            _currentArtSourceWidth = originalWidth;
                            _currentArtConfirmed = false;
                            _hasArtworkForCurrentTrack = true;
                            updateArtwork = true;
                        }

                        bmp = decoded;
                        rawBytes = CacheArtworkBytes(thumbnailBytes, decoded);

                        // Non-square art (video frames, 4:3 scans) is letterboxed by
                        // the Zune screen, so it needs a soft fill for the square it
                        // does not cover. Square covers fill the screen on their own.
                        if (NeedsBackdrop(originalWidth, originalHeight))
                        {
                            backdrop = DecodeBackdrop(thumbnailBytes);
                        }

                        LogArt($"decoded track=\"{newTrackId}\" origW={originalWidth} origH={originalHeight} bytes={thumbnailBytes.Length} standIn={isStandIn} backdrop={(backdrop != null ? "yes" : "no")}");
                    }
                    else
                    {
                        // Decode failed — the thumbnail stream reference is often
                        // swapped by the player mid-track-change. DO NOT clear the
                        // visible artwork; the settling poll below heals it.
                        LogArt($"decode FAILED track=\"{newTrackId}\" (thumb present, stream open/decode error)");
                    }
                }
            }
        }

        // Keep a settling poll running for as long as the artwork on screen is
        // not proven stable. Every new image (the browser's stand-in, then the
        // real cover) is painted as it arrives, and the poll stops on its own
        // once a delayed re-read returns the same real image.
        bool scheduleRetry = false;
        if (_loadArtwork)
        {
            lock (_stateLock)
            {
                if (!_currentArtConfirmed && _pendingArtworkRetryTrackId != newTrackId)
                {
                    _pendingArtworkRetryTrackId = newTrackId;
                    scheduleRetry = true;
                }
            }
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
                // Only reached after a successful decode of this track's art:
                // the swap is atomic and the byte cache is refreshed. Failed
                // decodes never reach this path, so the previous artwork
                // (and cache) survive instead of flashing the placeholder.
                _cachedThumbnailBytes = rawBytes;
                Thumbnail = bmp;
                HasThumbnail = bmp != null;
                ArtworkBackdrop = backdrop;
            }
            HasMedia = true;
        });

        if (scheduleRetry)
        {
            _ = RetryMissingArtworkAsync(session, newTrackId);
        }
    }

    /// <summary>
    /// Appends a line to %LOCALAPPDATA%\MetroHub\media_art.log. Diagnostic
    /// only: artwork events are rare (a handful per track change), and the
    /// log self-caps at 256 KB. Never throws.
    /// </summary>
    private static void LogArt(string message)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MetroHub");

            string path = Path.Combine(dir, "media_art.log");

            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
            {
                File.Delete(path);
            }

            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never break playback.
        }
    }

    /// <summary>
    /// Re-reads a session whose artwork has not settled yet: either nothing has
    /// arrived, or the image on screen is still the publisher's stand-in
    /// (browsers hand SMTC their icon / the page favicon the moment a session
    /// opens and swap in the real cover a second or two later). Each attempt
    /// applies whatever SMTC currently holds, so the cover replaces the
    /// stand-in as soon as it is published, and the loop stops once a delayed
    /// re-read returns the same non-stand-in image. Aborts silently if the
    /// track or the focused session changed in the meantime.
    /// </summary>
    private async Task RetryMissingArtworkAsync(MediaManager.MediaSession session, string trackId)
    {
        try
        {
            int attemptCounter = 0;

            foreach (int delayMs in ArtworkRetryDelaysMs)
            {
                int attempt = ++attemptCounter;
                await Task.Delay(delayMs).ConfigureAwait(false);

                lock (_stateLock)
                {
                    if (_currentTrackId != trackId) return;
                    if (_currentArtConfirmed) return;
                }

                if (_activeSession?.Id != session.Id) return;

                var control = session.ControlSession;
                if (control == null) return;

                var props = await control.TryGetMediaPropertiesAsync();
                if (props?.Thumbnail == null)
                {
                    LogArt($"poll attempt={attempt} track=\"{trackId}\" still no thumbnail");
                    continue;
                }

                LogArt($"poll attempt={attempt} track=\"{trackId}\" re-reading thumbnail...");
                await ApplyMediaPropertiesAsync(session, props, settlingAttempt: attempt);

                lock (_stateLock)
                {
                    if (_currentTrackId != trackId) return;

                    if (_currentArtConfirmed)
                    {
                        LogArt($"artwork settled track=\"{trackId}\" on attempt={attempt}");
                        return; // publisher stopped changing the image
                    }
                }
            }

            // Polls exhausted. Only fall back to the placeholder when this track
            // never produced any artwork at all; a stand-in stays visible
            // (better than an empty box, and exactly what the OS shell shows).
            bool noArtworkForTrack;
            lock (_stateLock)
            {
                noArtworkForTrack = !_hasArtworkForCurrentTrack && _currentTrackId == trackId;
            }

            if (noArtworkForTrack && _activeSession?.Id == session.Id)
            {
                RunOnUi(() =>
                {
                    lock (_stateLock)
                    {
                        if (_currentTrackId != trackId || _hasArtworkForCurrentTrack) return;

                        LogArt($"GIVE UP track=\"{trackId}\" — showing placeholder");
                        Thumbnail = null;
                        HasThumbnail = false;
                        ArtworkBackdrop = null;
                        _cachedThumbnailBytes = null;
                        _currentArtHash = 0;
                        _currentArtTrackId = string.Empty;
                        _currentArtIsStandIn = false;
                        _currentArtSourceWidth = 0;
                    }
                });
            }
            else
            {
                LogArt($"polls exhausted track=\"{trackId}\" — keeping currently displayed art");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaWidget] Artwork retry failed: {ex.Message}");
        }
        finally
        {
            lock (_stateLock)
            {
                if (_pendingArtworkRetryTrackId == trackId)
                {
                    _pendingArtworkRetryTrackId = null;
                }
            }
        }
    }

    /// <summary>
    /// After the active session closes (or focus is lost), wait NoMediaGraceMs
    /// and reset to "No media playing" only if no session was re-activated in
    /// the meantime. Chromium recreates its SMTC session around every track
    /// change / skip, so the old instant-reset behavior wiped artwork and
    /// metadata for music that was still playing. Costs nothing while idle.
    /// </summary>
    private async Task ScheduleNoMediaGraceCheckAsync(string closedSessionId)
    {
        try
        {
            await Task.Delay(NoMediaGraceMs).ConfigureAwait(false);

            if (_activeSession != null)
            {
                LogArt($"grace period elapsed — session \"{_activeSession.Id}\" re-activated, no reset");
                return;
            }

            LogArt("grace period elapsed — no session returned, resetting to No media");
            RunOnUi(ResetToNoMedia);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaWidget] Grace check failed: {ex.Message}");
        }
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
        }

        IsPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
    }

    [RelayCommand]
    public async Task TogglePlayPauseAsync()
    {
        var control = _activeSession?.ControlSession;
        if (control == null) return;

        try
        {
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
        HasMedia = false;
        Title = "No media playing";
        Artist = "Open Spotify, YouTube, or VLC";
        Album = string.Empty;
        HasAlbum = false;
        SourceName = "Media Player";
        Thumbnail = null;
        HasThumbnail = false;
        ArtworkBackdrop = null;
        _cachedThumbnailBytes = null;
        lock (_stateLock)
        {
            _currentArtHash = 0;
            _currentArtTrackId = string.Empty;
            _currentArtIsStandIn = false;
            _currentArtSourceWidth = 0;
            _currentArtConfirmed = false;
            _hasArtworkForCurrentTrack = false;
            _firstArtHash = 0;
            _firstArtTrackId = string.Empty;
            _standInHashes.Clear();
            _currentTrackId = string.Empty;
            _pendingArtworkRetryTrackId = null;
        }
        IsPlaying = false;
        CanPlayPause = true;
        CanSkipNext = true;
        CanSkipPrevious = true;
    }

    public override void Pause()
    {
        // MetroHub hidden: drop the heavy decoded WIC surface (DirectX texture +
        // managed copy) but keep _cachedThumbnailBytes so Resume() restores the
        // artwork instantly without touching the original SMTC stream again.
        RunOnUi(() =>
        {
            Thumbnail = null;
            HasThumbnail = false;
            ArtworkBackdrop = null;
        });
    }

    public override void Resume()
    {
        RunOnUi(() =>
        {
            if (_cachedThumbnailBytes != null && Thumbnail == null && HasMedia)
            {
                var restored = CreateThumbnailFromBytes(_cachedThumbnailBytes, DecodeTargetWidth);
                if (restored != null)
                {
                    Thumbnail = restored;
                    HasThumbnail = true;

                    if (restored is BitmapSource bs &&
                        NeedsBackdrop(bs.PixelWidth, bs.PixelHeight))
                    {
                        ArtworkBackdrop = DecodeBackdrop(_cachedThumbnailBytes);
                    }
                }
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
                    RunOnUi(() =>
                    {
                        if (playback != null) ApplyPlaybackInfo(playback);
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

    /// <summary>
    /// Reads an SMTC thumbnail into memory. Returns null when the stream cannot
    /// be opened — players swap the stream reference mid-track-change, which
    /// surfaces here and is healed by the settling poll.
    /// </summary>
    private static async Task<byte[]?> ReadThumbnailBytesAsync(IRandomAccessStreamReference streamRef)
    {
        try
        {
            using var stream = await streamRef.OpenReadAsync();
            using var netStream = stream.AsStreamForRead();

            using var memory = new MemoryStream();
            await netStream.CopyToAsync(memory);

            return memory.Length > 0 ? memory.ToArray() : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[MediaWidget] ReadThumbnailBytesAsync failed: {ex.Message}");

            return null;
        }
    }

    /// <summary>
    /// Reads the pixel dimensions out of an encoded thumbnail without decoding
    /// any pixels. Returns (0, 0) when the header cannot be parsed, which
    /// callers treat as "unknown" and let the decoder decide.
    /// </summary>
    private static (int Width, int Height) ReadSourceDimensions(byte[] bytes)
    {
        try
        {
            using var memory = new MemoryStream(bytes);

            var frame = BitmapFrame.Create(
                memory,
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);

            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// Stable identity for a set of thumbnail bytes. Tells the publisher's
    /// stand-in apart from the real cover, and lets an image that is already on
    /// screen skip the decode and the repaint.
    /// </summary>
    private static ulong Fingerprint(byte[] bytes)
    {
        try
        {
            return BitConverter.ToUInt64(SHA256.HashData(bytes), 0);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Tiny copy of an artwork used as the soft fill behind letterboxed wide art.
    /// Stretched to the full square and blurred by the renderer, so a 64px decode
    /// (~16 KB) is indistinguishable from a full-resolution backdrop.
    /// </summary>
    private static BitmapSource? DecodeBackdrop(byte[] bytes)
    {
        try
        {
            using var memory = new MemoryStream(bytes);

            var bitmap = new BitmapImage();

            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = memory;
            bitmap.DecodePixelWidth = BackdropDecodeWidth;
            bitmap.EndInit();
            bitmap.Freeze();

            return bitmap;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[MediaWidget] DecodeBackdrop failed: {ex.Message}");

            return null;
        }
    }

    /// <summary>
    /// Compressed byte cache for Pause/Resume: the original SMTC bytes when they
    /// are small enough, otherwise one high-quality re-encode of the decoded
    /// art. The original full-resolution pixels are never retained.
    /// </summary>
    private static byte[]? CacheArtworkBytes(byte[] thumbnailBytes, BitmapSource decoded)
    {
        return thumbnailBytes.Length <= MaxCacheBytes
            ? thumbnailBytes
            : EncodeThumbnailCache(decoded);
    }

    /// <summary>
    /// Small stand-in copy of an artwork, decoded from the same bytes at
    /// BackdropDecodeWidth. The renderer stretches and blurs it across the full
    /// square, which is what fills the space non-square art does not cover.
    /// </summary>
    private const int BackdropDecodeWidth = 64;

    /// <summary>
    /// True when artwork cannot fill a square slot on its own, i.e. the Zune
    /// screen will letterbox it and wants a backdrop behind it. A couple of
    /// pixels of tolerance keeps a near-square cover from asking for one.
    /// </summary>
    private static bool NeedsBackdrop(int width, int height)
        => width > 0 && height > 0 && Math.Abs(width - height) > 2;

    /// <summary>
    /// Shared artwork decode pipeline: reads header dimensions (cheap, no full
    /// decode), decodes once at the layout's pixel budget, and hands back the
    /// image with its original aspect ratio intact, frozen and thread-safe.
    ///
    /// The aspect is deliberately preserved rather than centre-cropped here. A
    /// square crop throws the frame's composition away and leaves only the
    /// source's short side (an 83px sliver of a 150×83 video thumbnail) to be
    /// blown up ~3× on the Zune screen. Instead the Zune view letterboxes wide
    /// art over a blurred backdrop, and the square layouts crop it at render
    /// time through UniformToFill / ImageBrush — one resample either way.
    /// </summary>
    private static BitmapSource? DecodeArtwork(
        byte[] bytes,
        int decodeWidth,
        out int originalWidth,
        out int originalHeight)
    {
        originalWidth = 0;
        originalHeight = 0;

        try
        {
            using var memory = new MemoryStream(bytes);

            // 1. Header-only dimension read (no full pixel decode).
            try
            {
                var frame = BitmapFrame.Create(
                    memory,
                    BitmapCreateOptions.DelayCreation,
                    BitmapCacheOption.None);

                originalWidth = frame.PixelWidth;
                originalHeight = frame.PixelHeight;
            }
            catch
            {
                // Header unreadable: fall through and decode at native size.
                memory.Position = 0;
            }

            // 2. Decode at the layout's pixel budget. WPF scales during the
            //    decode (so no full-size intermediate exists) and preserves the
            //    aspect ratio, which lands square art at budget × budget and
            //    wide art at budget × budget/aspect — enough for the square
            //    layouts' centre crop and for Zune's full-width letterbox with
            //    headroom up to ~250% DPI.
            memory.Position = 0;

            var bitmap = new BitmapImage();

            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = memory;

            if (originalWidth > decodeWidth)
            {
                bitmap.DecodePixelWidth = decodeWidth;
            }

            bitmap.EndInit();

            // 3. Safety net: every mainstream codec honours DecodePixelWidth,
            //    but never hand back more pixels than the budget asked for if
            //    one ignores it.
            BitmapSource displaySource = bitmap;

            if (displaySource.PixelWidth > decodeWidth)
            {
                double ratio = (double)decodeWidth / displaySource.PixelWidth;

                displaySource = new TransformedBitmap(
                    displaySource,
                    new ScaleTransform(ratio, ratio));
            }

            bitmap.Freeze();
            displaySource.Freeze();

            return displaySource;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[MediaWidget] DecodeArtwork failed: {ex.Message}");

            return null;
        }
    }


    /// <summary>
    /// High-quality JPEG fallback for the Pause/Resume byte cache. Only used
    /// when the original SMTC bytes exceed MaxCacheBytes, so the single
    /// re-encode happens from full-resolution art and quality 95 keeps it
    /// visually lossless at display size.
    /// </summary>
    private static byte[]? EncodeThumbnailCache(BitmapSource bitmap)
    {
        try
        {
            using var output = new MemoryStream();

            var encoder = new JpegBitmapEncoder
            {
                QualityLevel = 95
            };

            encoder.Frames.Add(
                BitmapFrame.Create(bitmap));

            encoder.Save(output);

            return output.ToArray();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[MediaWidget] EncodeThumbnailCache failed: {ex.Message}");

            return null;
        }
    }

    /// <summary>
    /// Recreates a display-ready bitmap from the cached artwork bytes at the
    /// target decode size. Used when restoring after MetroHub was hidden
    /// (Pause/Resume cycle). Runs the exact same pipeline as the initial
    /// load, so a cache holding original full-resolution bytes decodes
    /// identically to the first decode.
    /// </summary>
    private static ImageSource? CreateThumbnailFromBytes(
        byte[] bytes,
        int decodeWidth)
    {
        return DecodeArtwork(bytes, decodeWidth, out _, out _);
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

            if (_mediaManager != null)
            {
                try
                {
                    _mediaManager.OnAnySessionOpened -= Manager_OnAnySessionOpened;
                    _mediaManager.OnAnySessionClosed -= Manager_OnAnySessionClosed;
                    _mediaManager.OnFocusedSessionChanged -= Manager_OnFocusedSessionChanged;
                    _mediaManager.OnAnyPlaybackStateChanged -= Manager_OnAnyPlaybackStateChanged;
                    _mediaManager.OnAnyMediaPropertyChanged -= Manager_OnAnyMediaPropertyChanged;
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
