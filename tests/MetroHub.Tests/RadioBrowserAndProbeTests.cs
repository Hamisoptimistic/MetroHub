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

    [Fact]
    public async Task RadioBrowserClient_ResolveWorkingUrl_ResolvesByUuidWhenAvailable()
    {
        string json = """
        [
          {
            "stationuuid": "test-uuid-999",
            "name": "Groove Salad",
            "url_resolved": "https://ice6.somafm.com/groovesalad-256-mp3",
            "bitrate": 256,
            "lastcheckok": 1
          }
        ]
        """;

        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("/byuuid/test-uuid-999"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = new RadioBrowserClient(new HttpClient(handler));
        string? resolved = await client.ResolveWorkingUrlAsync("test-uuid-999", "Groove Salad");

        Assert.Equal("https://ice6.somafm.com/groovesalad-256-mp3", resolved);
    }

    [Fact]
    public async Task RadioBrowserClient_ResolveWorkingUrl_FallsBackToNameWhenUuidNotFound()
    {
        string searchJson = """
        [
          {
            "stationuuid": "discovered-uuid-888",
            "name": "Defcon Radio",
            "url_resolved": "https://ice4.somafm.com/defcon-128-mp3",
            "bitrate": 128,
            "lastcheckok": 1
          }
        ]
        """;

        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("/byname/Defcon"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(searchJson, System.Text.Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = new RadioBrowserClient(new HttpClient(handler));
        string? resolved = await client.ResolveWorkingUrlAsync(null, "Defcon Radio");

        Assert.Equal("https://ice4.somafm.com/defcon-128-mp3", resolved);
    }

    [Fact]
    public async Task StreamUrlProbeService_ParsesHlsMasterPlaylistWithBandwidth()
    {
        string hlsContent = """
        #EXTM3U
        #EXT-X-VERSION:3
        #EXT-X-STREAM-INF:BANDWIDTH=119365,CODECS="mp4a.40.2"
        chunklist.m3u8
        """;

        var handler = new MockHttpMessageHandler(req =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK);
            res.Content = new StringContent(hlsContent, System.Text.Encoding.UTF8, "application/vnd.apple.mpegurl");
            return res;
        });

        var probeService = new StreamUrlProbeService(new HttpClient(handler));
        var result = await probeService.ProbeUrlAsync("https://air.pc.cdn.bitgravity.com/air/live/pbaudio126/playlist.m3u8");

        Assert.True(result.IsValid);
        Assert.True(result.IsPlaylist);
        Assert.Equal("https://air.pc.cdn.bitgravity.com/air/live/pbaudio126/playlist.m3u8", result.ResolvedStreamUrl);
        Assert.Equal("application/vnd.apple.mpegurl", result.ContentType);
        Assert.Equal(119, result.BitrateKbps);
        Assert.Equal("Pbaudio126", result.InferredName);
    }

    [Fact]
    public async Task StreamUrlProbeService_HeadFails_FallsBackToGetRange()
    {
        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Head)
            {
                throw new HttpRequestException("Received an invalid status line: 'RPC-ERR'");
            }

            var res = new HttpResponseMessage(HttpStatusCode.OK);
            res.Content = new StringContent(string.Empty, System.Text.Encoding.UTF8, "audio/aacp");
            res.Headers.TryAddWithoutValidation("icy-name", "Arnicity FM");
            res.Headers.TryAddWithoutValidation("icy-br", "64");
            return res;
        });

        var probeService = new StreamUrlProbeService(new HttpClient(handler));
        var result = await probeService.ProbeUrlAsync("https://usa9.fastcast4u.com/proxy/arnifm?mp=/1");

        Assert.True(result.IsValid);
        Assert.Equal("audio/aacp", result.ContentType);
        Assert.Equal("Arnicity FM", result.InferredName);
        Assert.Equal(64, result.BitrateKbps);
    }

    [Fact]
    public async Task StreamUrlProbeService_UnwrapsAsxPlaylist()
    {
        string asxContent = """
        <asx version="3.0">
          <title>Classical Heritage</title>
          <entry>
            <ref href="http://live.classical.org:8000/stream"/>
          </entry>
        </asx>
        """;

        var handler = new MockHttpMessageHandler(req =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK);
            res.Content = new StringContent(asxContent, System.Text.Encoding.UTF8, "video/x-ms-asf");
            return res;
        });

        var probeService = new StreamUrlProbeService(new HttpClient(handler));
        var result = await probeService.ProbeUrlAsync("https://example.com/stream.asx");

        Assert.True(result.IsValid);
        Assert.True(result.IsPlaylist);
        Assert.Equal("http://live.classical.org:8000/stream", result.ResolvedStreamUrl);
        Assert.Equal("Classical Heritage", result.InferredName);
    }

    [Fact]
    public async Task StreamUrlProbeService_UnwrapsXspfPlaylist()
    {
        string xspfContent = """
        <?xml version="1.0" encoding="UTF-8"?>
        <playlist version="1" xmlns="http://xspf.org/ns/0/">
          <title>Open Synthwave</title>
          <trackList>
            <track>
              <location>http://synth.fm/live.ogg</location>
            </track>
          </trackList>
        </playlist>
        """;

        var handler = new MockHttpMessageHandler(req =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK);
            res.Content = new StringContent(xspfContent, System.Text.Encoding.UTF8, "application/xspf+xml");
            return res;
        });

        var probeService = new StreamUrlProbeService(new HttpClient(handler));
        var result = await probeService.ProbeUrlAsync("https://example.com/synth.xspf");

        Assert.True(result.IsValid);
        Assert.True(result.IsPlaylist);
        Assert.Equal("http://synth.fm/live.ogg", result.ResolvedStreamUrl);
        Assert.Equal("Open Synthwave", result.InferredName);
    }

    [Fact]
    public async Task StreamUrlProbeService_ShoutcastRootHtml_ResolvesSemicolonMountpoint()
    {
        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.ToString().EndsWith("/;"))
            {
                var audioRes = new HttpResponseMessage(HttpStatusCode.OK);
                audioRes.Content = new StringContent(string.Empty, System.Text.Encoding.UTF8, "audio/mpeg");
                audioRes.Headers.TryAddWithoutValidation("icy-name", "Retro Beats");
                audioRes.Headers.TryAddWithoutValidation("icy-br", "192");
                return audioRes;
            }

            var htmlRes = new HttpResponseMessage(HttpStatusCode.OK);
            htmlRes.Content = new StringContent("<!DOCTYPE html><html><body>SHOUTcast Server</body></html>", System.Text.Encoding.UTF8, "text/html");
            return htmlRes;
        });

        var probeService = new StreamUrlProbeService(new HttpClient(handler));
        var result = await probeService.ProbeUrlAsync("http://retro.shoutcast.com:8000/");

        Assert.True(result.IsValid);
        Assert.Equal("http://retro.shoutcast.com:8000/;", result.ResolvedStreamUrl);
        Assert.Equal("Retro Beats", result.InferredName);
        Assert.Equal(192, result.BitrateKbps);
    }

    [Fact]
    public async Task LiveNetwork_ValidatesBitgravityHlsAndFastcast4u()
    {
        var probe = StreamUrlProbeService.Instance;

        var res1 = await probe.ProbeUrlAsync("https://air.pc.cdn.bitgravity.com/air/live/pbaudio126/playlist.m3u8");
        Assert.True(res1.IsValid);
        Assert.Equal("https://air.pc.cdn.bitgravity.com/air/live/pbaudio126/playlist.m3u8", res1.ResolvedStreamUrl);
        Assert.Equal("application/vnd.apple.mpegurl", res1.ContentType);

        var res2 = await probe.ProbeUrlAsync("https://usa9.fastcast4u.com/proxy/arnifm?mp=/1");
        Assert.True(res2.IsValid);
        Assert.Equal("https://usa9.fastcast4u.com/proxy/arnifm?mp=/1", res2.ResolvedStreamUrl);
        Assert.Equal("audio/aacp", res2.ContentType);
    }
}
