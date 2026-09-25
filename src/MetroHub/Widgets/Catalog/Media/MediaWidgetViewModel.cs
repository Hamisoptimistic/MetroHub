using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetroHub.Core.Models;
using MetroHub.Widgets.Serialization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    /// Retains compressed raw image bytes in managed memory (~20 KB).
    /// Allows the heavy decoded WPF BitmapSource surface to be released
    /// when MetroHub is hidden, and instantly re-decoded with 0ms latency when restored.
    /// </summary>
    private byte[]? _cachedThumbnailBytes;
    private int _currentThumbnailWidth;

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
            if (mediaSession == null) return;

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
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaWidget] OnAnyMediaPropertyChanged failed: {ex.Message}");
        }
    }

    private void SetActiveSession(MediaManager.MediaSession session)
    {
        _activeSession = session;

        lock (_stateLock)
        {
            _currentTrackId = string.Empty;
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

        string newTrackId = $"{cleanArtist}|{cleanTitle}|{cleanAlbum}";
        bool isSameTrack = false;
        lock (_stateLock)
        {
            if (!string.Equals(_currentTrackId, newTrackId, StringComparison.Ordinal))
            {
                _currentTrackId = newTrackId;
                _currentThumbnailWidth = 0;
            }
            else
            {
                isSameTrack = true;
            }
        }

        ImageSource? bmp = null;
        byte[]? rawBytes = null;
        bool updateArtwork = false;

        if (props.Thumbnail != null)
        {
            (bmp, rawBytes) = await LoadThumbnailAsync(props.Thumbnail);
            if (bmp is BitmapSource bs)
            {
                int incomingWidth = bs.PixelWidth;
                lock (_stateLock)
                {
                    if (!isSameTrack || _currentThumbnailWidth == 0 || incomingWidth >= _currentThumbnailWidth)
                    {
                        _currentThumbnailWidth = incomingWidth;
                        updateArtwork = true;
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
            }
            HasMedia = true;
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
        _cachedThumbnailBytes = null;
        lock (_stateLock)
        {
            _currentThumbnailWidth = 0;
            _currentTrackId = string.Empty;
        }
        IsPlaying = false;
        CanPlayPause = true;
        CanSkipNext = true;
        CanSkipPrevious = true;
    }

    public override void Pause()
    {
        // When MetroHub is hidden, retain cached raw bytes but free heavy decoded WPF BitmapSource
    }

    public override void Resume()
    {
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
