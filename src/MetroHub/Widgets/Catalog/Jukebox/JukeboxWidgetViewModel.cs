using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetroHub.Core.Models;
using MetroHub.Core.Radio;
using MetroHub.Widgets.Serialization;

namespace MetroHub.Widgets.Catalog.Jukebox;

/// <summary>
/// ViewModel for the YouTube Jukebox widget. Search → tap → stream via the shared
/// <see cref="IRadioAudioService"/> → track pins itself to uncapped history.
/// Pure MVVM: zero WPF UI element references.
/// </summary>
public sealed partial class JukeboxWidgetViewModel : WidgetViewModelBase
{
    private readonly IYoutubeAudioResolver _resolver;
    private readonly IJukeboxAudioService _player;
    private CancellationTokenSource? _searchCts;
    private long _searchEpoch;
    private string? _playingVideoId;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<YoutubeSearchHit> _searchResults = new();

    [ObservableProperty]
    private ObservableCollection<JukeboxTrack> _history = new();

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _statusMessage = "Type a song name and press Enter.";

    [ObservableProperty]
    private string? _currentVideoId;

    [ObservableProperty]
    private string _currentTitle = string.Empty;

    [ObservableProperty]
    private bool _isPlaying;

    /// <summary>True when the last search returned at least one hit.</summary>
    public bool HasResults => SearchResults.Count > 0;

    /// <summary>True when history holds at least one track.</summary>
    public bool HasHistory => History.Count > 0;

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new List<WidgetSize>
    {
        WidgetSize.Large,      // 4x4
        WidgetSize.LargeWide,  // 6x4
        WidgetSize.Mega,       // 8x4
        WidgetSize.PortraitXL, // 4x8 (default)
    };

    public JukeboxWidgetViewModel(TileModel model)
        : this(model, YoutubeAudioResolver.Instance, JukeboxAudioService.Instance)
    {
    }

    public JukeboxWidgetViewModel(TileModel model, IYoutubeAudioResolver resolver, IJukeboxAudioService player)
        : base(model)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _player = player ?? throw new ArgumentNullException(nameof(player));

        _player.PlaybackStateChanged += OnPlayerStateChanged;
        _player.EndOfStreamReached += OnEndOfStream;
        _player.ErrorOccurred += OnPlayerError;

        LoadSettings(model.SettingsJson);
    }

    [RelayCommand]
    public async Task SearchAsync()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var cts = _searchCts;
        long epoch = Interlocked.Increment(ref _searchEpoch);

        IsSearching = true;
        StatusMessage = "Searching…";
        JukeboxLog.Info($"Search: '{SearchText}'");

        try
        {
            var hits = await _resolver.SearchAsync(SearchText, 8, cts.Token);
            if (epoch != Volatile.Read(ref _searchEpoch))
            {
                JukeboxLog.Info("Search superseded, dropping stale results.");
                return;
            }

            JukeboxLog.Info($"Search returned {hits.Count} hit(s).");

            SearchResults = new ObservableCollection<YoutubeSearchHit>(hits);
            OnPropertyChanged(nameof(HasResults));
            if (hits.Count == 0 && !cts.IsCancellationRequested)
            {
                StatusMessage = string.IsNullOrWhiteSpace(SearchText)
                    ? "Type a song name and press Enter."
                    : "No results. Check your connection and try again.";
            }
            else
            {
                StatusMessage = string.Empty;
            }
        }
        finally
        {
            if (epoch == Volatile.Read(ref _searchEpoch))
            {
                IsSearching = false;
            }
        }
    }

    [RelayCommand]
    public async Task PlayResultAsync(YoutubeSearchHit? hit)
    {
        if (hit == null)
        {
            return;
        }

        StatusMessage = "Connecting…";
        JukeboxLog.Info($"Tap result: '{hit.Title}' ({hit.VideoId})");
        await PlayTrackAsync(hit.VideoId, hit.Title, hit.Channel, null, hit.Duration);
    }

    [RelayCommand]
    public async Task PlayHistoryAsync(JukeboxTrack? track)
    {
        if (track == null)
        {
            return;
        }

        StatusMessage = "Connecting…";
        JukeboxLog.Info($"Tap history: '{track.Title}' ({track.VideoId})");
        await PlayTrackAsync(track.VideoId, track.Title, track.Channel, track.ThumbnailUrl,
            track.DurationTicks > 0 ? new TimeSpan(track.DurationTicks) : null);
    }

    [RelayCommand]
    public void TogglePlayPause()
    {
        if (IsPlaying)
        {
            _player.Pause();
            return;
        }

        if (_player.HasTrack)
        {
            _player.Resume();
            return;
        }

        var track = History.FirstOrDefault(t => t.VideoId == CurrentVideoId) ?? History.FirstOrDefault();
        if (track != null)
        {
            _ = PlayHistoryAsync(track);
        }
    }

    [RelayCommand]
    public void RemoveTrack(JukeboxTrack? track)
    {
        if (track == null)
        {
            return;
        }

        History.Remove(track);
        OnPropertyChanged(nameof(HasHistory));
        SaveSettings();
    }

    [RelayCommand]
    public void ClearHistory()
    {
        History.Clear();
        OnPropertyChanged(nameof(HasHistory));
        SaveSettings();
    }

    private async Task PlayTrackAsync(string videoId, string title, string channel, string? thumbnailUrl, TimeSpan? duration)
    {
        _playingVideoId = videoId;
        StatusMessage = string.Empty;
        JukeboxLog.Info($"Play: '{title}' ({videoId}) resolving audio tiers.");

        JukeboxStreamSet? streams = null;
        try
        {
            streams = await _resolver.ResolveStreamsAsync(videoId);
        }
        catch (Exception ex)
        {
            JukeboxLog.Warn($"Resolve error for '{title}': {ex.Message}");
        }

        if (streams is null ||
            (string.IsNullOrWhiteSpace(streams.LowUrl) && string.IsNullOrWhiteSpace(streams.OpusUrl) && string.IsNullOrWhiteSpace(streams.AacUrl)))
        {
            JukeboxLog.Warn($"Resolve failed for '{title}' ({videoId}).");
            StatusMessage = "Could not resolve audio. YouTube may have changed — try again.";
            SyncPlayingState(_player.IsPlaying);
            return;
        }

        JukeboxLog.Info(
            $"Play: '{title}' ({videoId}) low={streams.LowUrl != null} opus={streams.OpusUrl != null} aac={streams.AacUrl != null}.");

        try
        {
            await _player.PlayUrlAsync(new JukeboxAudioUrls(streams.LowUrl, streams.OpusUrl, streams.AacUrl), title);
        }
        catch (Exception ex)
        {
            JukeboxLog.Warn($"PlayUrlAsync error for '{title}': {ex.Message}");
        }

        SyncPlayingState(_player.IsPlaying);

        PinToHistory(videoId, title, channel, thumbnailUrl, duration);
        CurrentVideoId = videoId;
        CurrentTitle = title;
        SaveSettings();
    }

    private void PinToHistory(string videoId, string title, string channel, string? thumbnailUrl, TimeSpan? duration)
    {
        var existing = History.FirstOrDefault(t => t.VideoId == videoId);
        int plays = 1;
        if (existing != null)
        {
            plays = existing.PlayCount + 1;
            History.Remove(existing);
        }

        History.Insert(0, new JukeboxTrack
        {
            VideoId = videoId,
            Title = title,
            Channel = channel,
            ThumbnailUrl = thumbnailUrl,
            DurationTicks = duration?.Ticks ?? 0,
            LastPlayedAt = DateTime.Now,
            PlayCount = plays,
        });
        OnPropertyChanged(nameof(HasHistory));
    }

    private void OnPlayerStateChanged(object? sender, bool playing)
    {
        SyncPlayingState(playing);
    }

    private void OnPlayerError(object? sender, string message)
    {
        StatusMessage = message;
    }

    private void OnEndOfStream(object? sender, EventArgs e)
    {
        var finishedId = _playingVideoId;
        if (finishedId == null)
        {
            return;
        }

        int index = History.ToList().FindIndex(t => t.VideoId == finishedId);
        if (History.Count == 0)
        {
            return;
        }

        var next = History[(index + 1) % History.Count];
        JukeboxLog.Info($"Autoplay next: '{next.Title}' ({next.VideoId})");
        _ = PlayHistoryAsync(next);
    }

    private void SyncPlayingState(bool playing)
    {
        if (IsPlaying != playing)
        {
            IsPlaying = playing;
        }
    }

    protected override void LoadSettings(string? settingsJson)
    {
        var settings = WidgetSerializer.Deserialize<JukeboxWidgetSettings>(settingsJson);
        if (settings == null)
        {
            return;
        }

        History = new ObservableCollection<JukeboxTrack>(settings.History ?? new List<JukeboxTrack>());
        OnPropertyChanged(nameof(HasHistory));
        CurrentVideoId = settings.LastVideoId;
        var current = History.FirstOrDefault(t => t.VideoId == CurrentVideoId);
        CurrentTitle = current?.Title ?? string.Empty;
    }

    public override void SaveSettings()
    {
        var settings = new JukeboxWidgetSettings
        {
            History = History.ToList(),
            LastVideoId = CurrentVideoId,
        };
        Model.TargetPath = "jukebox";
        Model.SettingsJson = WidgetSerializer.Serialize(settings);
        MainWindow.Current?.SaveGroupsAndLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;
            _player.PlaybackStateChanged -= OnPlayerStateChanged;
            _player.EndOfStreamReached -= OnEndOfStream;
            _player.ErrorOccurred -= OnPlayerError;
        }

        base.Dispose(disposing);
    }
}