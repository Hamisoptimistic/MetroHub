using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Models;
using MetroHub.Core.Radio;
using MetroHub.Widgets.Catalog.Jukebox;
using MetroHub.Widgets.Serialization;
using Xunit;

namespace MetroHub.Tests;

/// <summary>
/// Offline-safe jukebox tests: history pinning/dedupe, settings round-trip, replay
/// re-resolution (fresh URL per tap), and autoplay-next on natural stream end.
/// YouTube network calls are never made (faked resolver + player).
/// </summary>
public sealed class JukeboxWidgetTests
{
    // Unit tests run in-process: keep them out of the user's real jukebox.log.
    static JukeboxWidgetTests() => JukeboxLog.Enabled = false;

    private sealed class FakeResolver : IYoutubeAudioResolver
    {
        public int ResolveCalls;
        public string LastResolvedId = string.Empty;

        public Task<IReadOnlyList<YoutubeSearchHit>> SearchAsync(string query, int topN = 8, CancellationToken ct = default)
        {
            IReadOnlyList<YoutubeSearchHit> hits = string.IsNullOrWhiteSpace(query)
                ? Array.Empty<YoutubeSearchHit>()
                : new[]
                {
                    new YoutubeSearchHit("vid-sofia", "Sofia", "Clairo", null, TimeSpan.FromMinutes(3).Add(TimeSpan.FromSeconds(24))),
                    new YoutubeSearchHit("vid-cover", "Sofia (cover)", "Someone", null, null),
                };
            return Task.FromResult(hits);
        }

        public Task<YoutubeAudioStream?> ResolveAudioUrlAsync(string videoId, CancellationToken ct = default)
        {
            ResolveCalls++;
            LastResolvedId = videoId;
            // Fresh URL per call, like real expiring YouTube URLs.
            return Task.FromResult<YoutubeAudioStream?>(
                new YoutubeAudioStream($"https://audio.test/{videoId}#{ResolveCalls}", "opus", 160));
        }

        public Task<YoutubeAudioStream?> ResolveAacFallbackAsync(string videoId, CancellationToken ct = default)
        {
            return Task.FromResult<YoutubeAudioStream?>(
                new YoutubeAudioStream($"https://audio.test/{videoId}#aac", "mp4a.40.2", 128));
        }
    }

    private sealed class FakePlayer : IJukeboxAudioService
    {
        public List<(string OpusUrl, string AacUrl, string Label)> PlayedUrls { get; } = new();
        public bool IsPlaying { get; private set; }
        public bool IsBuffering => false;
        public string CurrentLabel { get; private set; } = string.Empty;
        public bool HasTrack => PlayedUrls.Count > 0 && !_stopped;
        public double Volume { get; set; } = 0.5;
        public bool IsMuted { get; set; }
        public string? LastErrorMessage => null;

        private bool _stopped = true;

        public event EventHandler<bool>? PlaybackStateChanged;
        public event EventHandler? EndOfStreamReached;
        public event EventHandler<string>? ErrorOccurred;

        public Task PlayUrlAsync(JukeboxAudioUrls urls, string label, CancellationToken ct = default)
        {
            PlayedUrls.Add((urls.OpusUrl ?? string.Empty, urls.AacUrl ?? string.Empty, label));
            CurrentLabel = label;
            _stopped = false;
            IsPlaying = true;
            PlaybackStateChanged?.Invoke(this, true);
            return Task.CompletedTask;
        }

        public void Pause()
        {
            IsPlaying = false;
            PlaybackStateChanged?.Invoke(this, false);
        }

        public void Resume()
        {
            if (!_stopped)
            {
                IsPlaying = true;
                PlaybackStateChanged?.Invoke(this, true);
            }
        }

        public void Stop()
        {
            _stopped = true;
            IsPlaying = false;
            PlaybackStateChanged?.Invoke(this, false);
        }

        public void SetVolume(double volume) => Volume = volume;
        public void SetMuted(bool isMuted) => IsMuted = isMuted;

        public void RaiseNaturalEnd() => EndOfStreamReached?.Invoke(this, EventArgs.Empty);

        public void Dispose() { }
    }

    private static (JukeboxWidgetViewModel Vm, FakeResolver Resolver, FakePlayer Player, TileModel Model) Create()
    {
        var model = new TileModel { TargetPath = "jukebox" };
        var resolver = new FakeResolver();
        var player = new FakePlayer();
        return (new JukeboxWidgetViewModel(model, resolver, player), resolver, player, model);
    }

    [Fact]
    public async Task Search_Fills_Results()
    {
        var (vm, _, _, _) = Create();
        vm.SearchText = "sofia clairo";
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.SearchResults.Count);
        Assert.True(vm.HasResults);
        Assert.Equal("Sofia", vm.SearchResults[0].Title);
        Assert.Equal("3:24", vm.SearchResults[0].DurationText);
    }

    [Fact]
    public async Task PlayResult_Pins_History_And_Plays_Resolved_Url()
    {
        var (vm, resolver, player, _) = Create();
        vm.SearchText = "sofia";
        await vm.SearchCommand.ExecuteAsync(null);
        await vm.PlayResultCommand.ExecuteAsync(vm.SearchResults[0]);

        Assert.Single(player.PlayedUrls);
        Assert.Equal("https://audio.test/vid-sofia#1", player.PlayedUrls[0].OpusUrl);
        Assert.Contains("vid-sofia", player.PlayedUrls[0].AacUrl);
        Assert.Equal("Sofia", player.PlayedUrls[0].Label);
        Assert.Single(vm.History);
        Assert.Equal("vid-sofia", vm.History[0].VideoId);
        Assert.Equal(1, vm.History[0].PlayCount);
        Assert.True(vm.IsPlaying);
        Assert.Equal("vid-sofia", vm.CurrentVideoId);
        _ = resolver;
    }

    [Fact]
    public async Task Replay_ReResolves_Fresh_Url_And_Bumps_Play_Count()
    {
        var (vm, resolver, player, _) = Create();
        vm.SearchText = "sofia";
        await vm.SearchCommand.ExecuteAsync(null);
        await vm.PlayResultCommand.ExecuteAsync(vm.SearchResults[0]);

        await vm.PlayHistoryCommand.ExecuteAsync(vm.History[0]);

        Assert.Equal(2, resolver.ResolveCalls);
        Assert.Equal("https://audio.test/vid-sofia#1", player.PlayedUrls[0].OpusUrl);
        Assert.Equal("https://audio.test/vid-sofia#2", player.PlayedUrls[1].OpusUrl);
        Assert.Single(vm.History);
        Assert.Equal(2, vm.History[0].PlayCount);
    }

    [Fact]
    public async Task Natural_End_Autoplays_Next_History_Item()
    {
        var (vm, _, player, _) = Create();
        vm.SearchText = "sofia";
        await vm.SearchCommand.ExecuteAsync(null);
        await vm.PlayResultCommand.ExecuteAsync(vm.SearchResults[0]); // vid-sofia
        await vm.PlayResultCommand.ExecuteAsync(vm.SearchResults[1]); // vid-cover on top

        Assert.Equal("vid-cover", vm.History[0].VideoId);

        player.RaiseNaturalEnd();
        await Task.Delay(500);

        Assert.Equal(3, player.PlayedUrls.Count);
        Assert.Equal("Sofia", player.PlayedUrls[^1].Label);
    }

    [Fact]
    public async Task Remove_And_Clear_Update_History()
    {
        var (vm, _, _, _) = Create();
        vm.SearchText = "sofia";
        await vm.SearchCommand.ExecuteAsync(null);
        await vm.PlayResultCommand.ExecuteAsync(vm.SearchResults[0]);
        await vm.PlayResultCommand.ExecuteAsync(vm.SearchResults[1]);
        Assert.Equal(2, vm.History.Count);

        vm.RemoveTrackCommand.Execute(vm.History[0]);
        Assert.Single(vm.History);

        vm.ClearHistoryCommand.Execute(null);
        Assert.Empty(vm.History);
        Assert.False(vm.HasHistory);
    }

    [Fact]
    public async Task Settings_Round_Trip_Preserves_History()
    {
        var (vm, _, _, model) = Create();
        vm.SearchText = "sofia";
        await vm.SearchCommand.ExecuteAsync(null);
        await vm.PlayResultCommand.ExecuteAsync(vm.SearchResults[0]);
        Assert.NotNull(model.SettingsJson);

        var reloaded = WidgetSerializer.Deserialize<JukeboxWidgetSettings>(model.SettingsJson);
        Assert.NotNull(reloaded);
        Assert.Single(reloaded!.History);
        Assert.Equal("vid-sofia", reloaded.History[0].VideoId);
        Assert.Equal("vid-sofia", reloaded.LastVideoId);

        var vm2 = new JukeboxWidgetViewModel(model, new FakeResolver(), new FakePlayer());
        Assert.Single(vm2.History);
        Assert.Equal("Sofia", vm2.History[0].Title);
        Assert.Equal("vid-sofia", vm2.CurrentVideoId);
    }

    [Fact]
    public async Task Empty_Search_Returns_No_Results_Without_Network()
    {
        var (vm, _, _, _) = Create();
        vm.SearchText = "   ";
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Empty(vm.SearchResults);
        Assert.False(vm.HasResults);
    }
}
