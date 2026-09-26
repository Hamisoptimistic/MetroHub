using System.Threading.Tasks;
using MetroHub.Core.Radio;
using Xunit;

namespace MetroHub.Tests;

public class RadioAudioServiceTests
{
    [Fact]
    public void Volume_ClampsToZeroAndOne()
    {
        var service = RadioAudioService.Instance;

        service.SetVolume(-0.5);
        Assert.Equal(0.0, service.Volume);

        service.SetVolume(1.8);
        Assert.Equal(1.0, service.Volume);

        service.SetVolume(0.45);
        Assert.Equal(0.45, service.Volume);
    }

    [Fact]
    public void Mute_TogglesCorrectly()
    {
        var service = RadioAudioService.Instance;

        service.SetMuted(true);
        Assert.True(service.IsMuted);

        service.SetMuted(false);
        Assert.False(service.IsMuted);
    }

    [Fact]
    public async Task PlayStation_SetsCurrentStation_AfterDebounce()
    {
        var service = RadioAudioService.Instance;

        var station = new RadioStation
        {
            Id = "test_station",
            Name = "Test Ambient",
            StreamUrl = "http://ice1.somafm.com/dronezone-128-mp3",
            BitrateKbps = 128,
            Category = "ambient"
        };

        // Start playback
        var playTask = service.PlayStationAsync(station);
        await playTask;

        Assert.NotNull(service.CurrentStation);
        Assert.Equal("test_station", service.CurrentStation.Id);

        // Pause to avoid keeping socket open
        service.Pause();
        Assert.False(service.IsPlaying);
    }

    [Fact]
    public async Task LiveStreamPlayback_ConnectsAndStreamsSuccessfully()
    {
        var service = RadioAudioService.Instance;

        var station = new RadioStation
        {
            Id = "somafm_groovesalad",
            Name = "SomaFM Groove Salad",
            StreamUrl = "http://ice1.somafm.com/groovesalad-128-mp3",
            BitrateKbps = 128,
            Category = "ambient"
        };

        await service.PlayStationAsync(station);
        Assert.Equal("somafm_groovesalad", service.CurrentStation?.Id);

        // Wait briefly for WinRT media engine to transition to buffering or playing
        await Task.Delay(1000);
        Assert.True(service.IsBuffering || service.IsPlaying, "Stream should transition to Buffering or Playing");

        // Graceful pause and cleanup
        service.Pause();
        Assert.False(service.IsPlaying);
    }

    [Fact]
    public async Task BirdsongFM_LiveChunkedStream_ConnectsAndStreamsInstantly()
    {
        var service = RadioAudioService.Instance;

        var birdsongStation = new RadioStation
        {
            Id = "birdsong",
            Name = "Birdsong",
            StreamUrl = "https://a1.radio.co/s5c5da6a36/listen",
            BitrateKbps = 128,
            Category = "nature"
        };

        // Start playback on the chunked Radio.co stream that previously stalled in WinRT
        await service.PlayStationAsync(birdsongStation);

        Assert.NotNull(service.CurrentStation);
        Assert.Equal("birdsong", service.CurrentStation.Id);
        Assert.Null(service.LastErrorMessage);

        // Allow up to 3 seconds for BASS network pre-buffering to complete
        int waitedMs = 0;
        while (!service.IsPlaying && waitedMs < 3000)
        {
            await Task.Delay(200);
            waitedMs += 200;
        }

        Assert.True(service.IsPlaying, $"Birdsong FM should transition to Playing via BASS without stalling. Error: {service.LastErrorMessage}");
        Assert.False(service.IsBuffering, "Birdsong FM should have completed pre-buffering");

        // Clean pause
        service.Pause();
        Assert.False(service.IsPlaying);
    }

    [Fact]
    public async Task RadioCo_9128_LivePlayback_ConnectsSuccessfully()
    {
        var service = RadioAudioService.Instance;

        var station = new RadioStation
        {
            Id = "9128",
            Name = "9128.live",
            StreamUrl = "https://streams.radio.co/s0aa1e6f4a/listen",
            BitrateKbps = 320,
            Category = "ambient"
        };

        await service.PlayStationAsync(station);
        Assert.Equal("9128", service.CurrentStation?.Id);

        int waitedMs = 0;
        while (!service.IsPlaying && waitedMs < 3000)
        {
            await Task.Delay(200);
            waitedMs += 200;
        }

        Assert.True(service.IsPlaying, $"9128.live should be playing via BASS. Error: {service.LastErrorMessage}");

        service.Pause();
        Assert.False(service.IsPlaying);
    }
}
