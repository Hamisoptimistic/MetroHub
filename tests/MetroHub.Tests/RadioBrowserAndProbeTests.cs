using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Radio;
using Xunit;

namespace MetroHub.Tests;

public class RadioBrowserAndProbeTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }

    [Fact]
    public void RadioBrowserStationDto_DeserializesCorrectly_AndPicksBestStreamUrl()
    {
        string json = """
        [
          {
            "stationuuid": "964e03d4-0601-11e8-ae97-52543be04c81",
            "name": "Lofi Girl",
            "url": "http://stream.lofigirl.com/stream",
            "url_resolved": "https://play.streamaudio.de:8000/lofigirl",
            "bitrate": 128,
            "codec": "MP3",
            "tags": "lofi,chillout,study",
            "countrycode": "FR",
            "lastcheckok": 1
          }
        ]
        """;

        var stations = JsonSerializer.Deserialize<System.Collections.Generic.List<RadioBrowserStationDto>>(json);
        Assert.NotNull(stations);
        Assert.Single(stations);

        var station = stations[0];
        Assert.Equal("964e03d4-0601-11e8-ae97-52543be04c81", station.StationUuid);
        Assert.Equal("Lofi Girl", station.Name);
        Assert.Equal("https://play.streamaudio.de:8000/lofigirl", station.BestStreamUrl);
        Assert.Equal(128, station.Bitrate);
        Assert.Equal(1, station.LastCheckOk);
    }

    [Fact]
    public async Task RadioBrowserClient_EmptyOrWhitespaceQuery_ReturnsEmptyListInstantly()
    {
        var client = new RadioBrowserClient();
        var resultEmpty = await client.SearchStationsAsync("");
        var resultWhitespace = await client.SearchStationsAsync("   ");

        Assert.Empty(resultEmpty);
        Assert.Empty(resultWhitespace);
    }

    [Fact]
    public async Task RadioBrowserClient_ExecutesMirrorFailover_WhenPrimaryTimesOut()
    {
        int attemptCount = 0;
        var handler = new MockHttpMessageHandler(req =>
        {
            attemptCount++;
            if (req.RequestUri?.Host.StartsWith("de1") == true)
            {
                // Primary mirror fails with 503
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            // Secondary mirror succeeds
            string json = """
            [
              {
                "stationuuid": "test-uuid-1",
                "name": "Mirror Two Radio",
                "url_resolved": "https://mirror2.com/live.mp3",
                "bitrate": 320,
                "lastcheckok": 1
              }
            ]
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var client = new RadioBrowserClient(new HttpClient(handler));
        var results = await client.SearchStationsAsync("jazz");

        Assert.NotNull(results);
        Assert.Single(results);
        Assert.Equal("Mirror Two Radio", results[0].Name);
        Assert.True(attemptCount >= 2, "Client should attempt next mirror on primary failure");
    }

    [Fact]
    public async Task StreamUrlProbeService_RejectsHtmlWebpage()
    {
        var handler = new MockHttpMessageHandler(req =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK);
            res.Content = new StringContent("<!DOCTYPE html><html><body>Webpage</body></html>", System.Text.Encoding.UTF8, "text/html");
            return res;
        });

        var probeService = new StreamUrlProbeService(new HttpClient(handler));
        var result = await probeService.ProbeUrlAsync("https://somafm.com/groovesalad/");

        Assert.False(result.IsValid);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("website page", result.ErrorMessage);
    }

    [Fact]
    public async Task StreamUrlProbeService_AcceptsDirectAudioStream()
    {
        var handler = new MockHttpMessageHandler(req =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK);
            res.Content = new StringContent(string.Empty, System.Text.Encoding.UTF8, "audio/mpeg");
            return res;
        });

        var probeService = new StreamUrlProbeService(new HttpClient(handler));
        var result = await probeService.ProbeUrlAsync("https://stream.nightride.fm/chillsynth.mp3");

        Assert.True(result.IsValid);
        Assert.Null(result.ErrorMessage);
        Assert.Equal("https://stream.nightride.fm/chillsynth.mp3", result.ResolvedStreamUrl);
        Assert.Equal("audio/mpeg", result.ContentType);
    }

    [Fact]
    public async Task StreamUrlProbeService_UnwrapsSingleStationPlsPlaylist()
    {
        string plsContent = """
        [playlist]
        NumberOfEntries=1
        File1=http://ice1.somafm.com/groovesalad-128-mp3
        Title1=SomaFM: Groove Salad
        Length1=-1
        Version=2
        """;

        var handler = new MockHttpMessageHandler(req =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK);
            res.Content = new StringContent(plsContent, System.Text.Encoding.UTF8, "audio/x-scpls");
            return res;
        });

        var probeService = new StreamUrlProbeService(new HttpClient(handler));
        var result = await probeService.ProbeUrlAsync("https://somafm.com/groovesalad.pls");

        Assert.True(result.IsValid);
        Assert.True(result.IsPlaylist);
        Assert.Equal(1, result.PlaylistTrackCount);
        Assert.Equal("http://ice1.somafm.com/groovesalad-128-mp3", result.ResolvedStreamUrl);
        Assert.Equal("SomaFM: Groove Salad", result.InferredName);
    }

    [Fact]
    public async Task StreamUrlProbeService_RejectsMassiveMultiChannelPlaylist()
    {
        string m3uContent = """
        #EXTM3U
        #EXTINF:-1,Channel 1
        http://stream1.com/live
        #EXTINF:-1,Channel 2
        http://stream2.com/live
        #EXTINF:-1,Channel 3
        http://stream3.com/live
        #EXTINF:-1,Channel 4
        http://stream4.com/live
        #EXTINF:-1,Channel 5
        http://stream5.com/live
        #EXTINF:-1,Channel 6
        http://stream6.com/live
        #EXTINF:-1,Channel 7
        http://stream7.com/live
        """;

        var handler = new MockHttpMessageHandler(req =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK);
            res.Content = new StringContent(m3uContent, System.Text.Encoding.UTF8, "application/x-mpegurl");
            return res;
        });

        var probeService = new StreamUrlProbeService(new HttpClient(handler));
        var result = await probeService.ProbeUrlAsync("https://example.com/massive_list.m3u");

        Assert.False(result.IsValid);
        Assert.True(result.IsPlaylist);
        Assert.Equal(7, result.PlaylistTrackCount);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("contains 7 channels", result.ErrorMessage);
    }
}
