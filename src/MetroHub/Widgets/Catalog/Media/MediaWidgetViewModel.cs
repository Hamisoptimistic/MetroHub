using System;
using System.Collections.Generic;
using System.IO;
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

namespace MetroHub.Widgets.Catalog.Media;

public partial class MediaWidgetViewModel : WidgetViewModelBase, IRecipient<HubVisibilityChangedMessage>
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private bool _isHubVisible = true;

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new[]
    {
        WidgetSize.Mega,    // 8x4
        WidgetSize.Banner3  // 8x3
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

    public MediaWidgetViewModel(TileModel model) : base(model)
    {
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
        _ = RefreshSessionAsync();
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
            _currentSession.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
        }

        _currentSession = newSession;

        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged += Session_MediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged += Session_PlaybackInfoChanged;
            await UpdateMediaDetailsAsync();
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
                IsPlaying = false;
            });
        }
    }

    private void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        _ = UpdateMediaDetailsAsync();
    }

    private void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        UpdatePlaybackInfo();
    }

    private void UpdatePlaybackInfo()
    {
        if (_currentSession == null) return;

        try
        {
            var playback = _currentSession.GetPlaybackInfo();
            if (playback != null)
            {
                bool playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var controls = playback.Controls;

                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    IsPlaying = playing;
                    CanPlayPause = controls?.IsPlayPauseToggleEnabled ?? true;
                    CanSkipNext = controls?.IsNextEnabled ?? true;
                    CanSkipPrevious = controls?.IsPreviousEnabled ?? true;
                });
            }
        }
        catch { }
    }

    private async Task UpdateMediaDetailsAsync()
    {
        if (!_isHubVisible || _currentSession == null) return;

        try
        {
            var props = await _currentSession.TryGetMediaPropertiesAsync();
            var playback = _currentSession.GetPlaybackInfo();
            string rawSource = _currentSession.SourceAppUserModelId ?? string.Empty;

            if (props == null || (string.IsNullOrWhiteSpace(props.Title) && string.IsNullOrWhiteSpace(props.Artist)))
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    HasMedia = false;
                    Title = "No media playing";
                    Artist = "Open Spotify, YouTube, or VLC";
                    Album = string.Empty;
                    HasAlbum = false;
                    SourceName = ResolveSourceName(rawSource, null, null);
                    Thumbnail = null;
                    HasThumbnail = false;
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

            string cleanSource = ResolveSourceName(rawSource, cleanTitle, rawArtist);
            bool hasAlbum = !string.IsNullOrWhiteSpace(cleanAlbum);

            bool playing = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var controls = playback?.Controls;

            ImageSource? bmp = null;
            if (props.Thumbnail != null)
            {
                bmp = await LoadThumbnailAsync(props.Thumbnail);
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Title = cleanTitle;
                Artist = cleanArtist;
                Album = cleanAlbum;
                HasAlbum = hasAlbum;
                SourceName = cleanSource;
                Thumbnail = bmp;
                HasThumbnail = bmp != null;
                IsPlaying = playing;
                HasMedia = true;
                CanPlayPause = controls?.IsPlayPauseToggleEnabled ?? true;
                CanSkipNext = controls?.IsNextEnabled ?? true;
                CanSkipPrevious = controls?.IsPreviousEnabled ?? true;
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
            bitmap.StreamSource = memory;
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
        if (_currentSession == null && _manager != null)
        {
            try { _currentSession = _manager.GetCurrentSession(); } catch { }
        }

        if (_currentSession != null)
        {
            try
            {
                IsPlaying = !IsPlaying;
                await _currentSession.TryTogglePlayPauseAsync();
                UpdatePlaybackInfo();
            }
            catch { }
        }
    }

    [RelayCommand]
    public async Task SkipNextAsync()
    {
        if (_currentSession == null && _manager != null)
        {
            try { _currentSession = _manager.GetCurrentSession(); } catch { }
        }

        if (_currentSession != null)
        {
            try
            {
                await _currentSession.TrySkipNextAsync();
            }
            catch { }
        }
    }

    [RelayCommand]
    public async Task SkipPreviousAsync()
    {
        if (_currentSession == null && _manager != null)
        {
            try { _currentSession = _manager.GetCurrentSession(); } catch { }
        }

        if (_currentSession != null)
        {
            try
            {
                await _currentSession.TrySkipPreviousAsync();
            }
            catch { }
        }
    }

    public override void Pause()
    {
        _isHubVisible = false;
    }

    public override void Resume()
    {
        _isHubVisible = true;
        _ = RefreshSessionAsync();
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
}
