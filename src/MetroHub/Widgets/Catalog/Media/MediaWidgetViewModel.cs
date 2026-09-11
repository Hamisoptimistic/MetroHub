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

            string cleanSource = ResolveSourceName(rawSource, cleanTitle, cleanArtist);
            bool hasAlbum = !string.IsNullOrWhiteSpace(cleanAlbum);

            bool playing = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var controls = playback?.Controls;

            BitmapImage? bmp = null;
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

    private static async Task<BitmapImage?> LoadThumbnailAsync(IRandomAccessStreamReference streamRef)
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
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public static string ResolveSourceName(string? appId, string? title, string? artist)
    {
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

        // Browser playback: check for YouTube
        if (lower.Contains("chrome") || lower.Contains("edge") || lower.Contains("msedge") || lower.Contains("brave") || lower.Contains("firefox") || lower.Contains("opera"))
        {
            if ((title != null && title.Contains("YouTube", StringComparison.OrdinalIgnoreCase)) ||
                (artist != null && artist.Contains("YouTube", StringComparison.OrdinalIgnoreCase)) ||
                lower.Contains("youtube"))
            {
                return "YouTube";
            }
            if (lower.Contains("edge") || lower.Contains("msedge")) return "Microsoft Edge";
            if (lower.Contains("chrome")) return "Google Chrome";
            if (lower.Contains("brave")) return "Brave";
            if (lower.Contains("firefox")) return "Firefox";
            return "Web Browser";
        }

        string name = System.IO.Path.GetFileNameWithoutExtension(appId);
        int bang = name.IndexOf('!');
        if (bang >= 0 && bang < name.Length - 1)
        {
            name = name.Substring(bang + 1);
        }
        return name;
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
