using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Presentation.Controllers;
using Xunit;

namespace MetroHub.Tests;

[Collection("CompanionServiceTests")]
public sealed class CompanionServiceTests : IAsyncLifetime
{
    private LocalCompanionService? _service;
    private HttpClient? _client;
    private int _port;

    public async Task InitializeAsync()
    {
        _service = new LocalCompanionService();
        // Use an ephemeral or free port in range for test isolation
        int testPort = 48842;
        await _service.StartAsync(preferredPort: testPort).ConfigureAwait(false);
        _port = _service.ActivePort;

        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false
        };
        _client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_port}/"),
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_service != null)
        {
            await _service.StopAsync().ConfigureAwait(false);
            _service.Dispose();
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, string? origin = LocalCompanionService.PinnedChromeOrigin, bool includeClientHeader = true)
    {
        var request = new HttpRequestMessage(method, path);
        if (origin != null)
        {
            request.Headers.Add("Origin", origin);
        }
        if (includeClientHeader)
        {
            request.Headers.Add(LocalCompanionService.ClientHeaderName, LocalCompanionService.ClientHeaderExpected);
        }
        return request;
    }

    #region Security & Origin Tests

    [Fact]
    public async Task Origin_PinnedChromeExtension_Allowed()
    {
        using var request = CreateRequest(HttpMethod.Get, "/api/health", origin: LocalCompanionService.PinnedChromeOrigin);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(LocalCompanionService.PinnedChromeOrigin, response.Headers.GetValues("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Origin_MozExtension_Allowed()
    {
        string mozOrigin = "moz-extension://a533bf08-54b6-455b-b5bc-135b9c02506b";
        using var request = CreateRequest(HttpMethod.Get, "/api/health", origin: mozOrigin);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(mozOrigin, response.Headers.GetValues("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Origin_Missing_Allowed()
    {
        using var request = CreateRequest(HttpMethod.Get, "/api/health", origin: null);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Origin_Null_RejectedWith403()
    {
        using var request = CreateRequest(HttpMethod.Get, "/api/health", origin: "null");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Origin_WebOrigin_RejectedWith403()
    {
        using var request = CreateRequest(HttpMethod.Get, "/api/health", origin: "https://malicious-website.com");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Origin_UnauthorizedChromeExtension_RejectedWith403()
    {
        using var request = CreateRequest(HttpMethod.Get, "/api/health", origin: "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    #endregion

    #region Host Header & Anti-DNS Rebinding Tests

    [Fact]
    public async Task Host_Valid127001_Allowed()
    {
        using var request = CreateRequest(HttpMethod.Get, "/api/health");
        request.Headers.Host = $"127.0.0.1:{_port}";
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Host_DnsRebindHost_RejectedWith403()
    {
        using var request = CreateRequest(HttpMethod.Get, "/api/health");
        request.Headers.Host = "rebind-attack.com";
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    #endregion

    #region Header Gate & CORS Tests

    [Fact]
    public async Task OptionsPreflight_ExemptFromCustomHeader_Returns204WithCors()
    {
        using var request = CreateRequest(HttpMethod.Options, "/api/tiles", origin: LocalCompanionService.PinnedChromeOrigin, includeClientHeader: false);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.True(response.Headers.Contains("Access-Control-Allow-Methods"));
        Assert.True(response.Headers.Contains("Access-Control-Allow-Headers"));
        Assert.True(response.Headers.Contains("Vary"));
    }

    [Fact]
    public async Task Get_WithoutCustomHeader_RejectedWith400()
    {
        using var request = CreateRequest(HttpMethod.Get, "/api/health", includeClientHeader: false);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithoutCustomHeader_RejectedWith400()
    {
        using var request = CreateRequest(HttpMethod.Post, "/api/tiles", includeClientHeader: false);
        request.Content = new StringContent("{\"url\":\"https://example.com\"}", Encoding.UTF8, "application/json");
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CorsHeaders_PresentOnGetAndPostResponses()
    {
        using var getReq = CreateRequest(HttpMethod.Get, "/api/health", origin: LocalCompanionService.PinnedChromeOrigin);
        using var getRes = await _client!.SendAsync(getReq);

        Assert.Equal(HttpStatusCode.OK, getRes.StatusCode);
        Assert.Contains(LocalCompanionService.PinnedChromeOrigin, getRes.Headers.GetValues("Access-Control-Allow-Origin"));
        Assert.Contains("Origin", getRes.Headers.GetValues("Vary"));

        using var postReq = CreateRequest(HttpMethod.Post, "/api/tiles", origin: LocalCompanionService.PinnedChromeOrigin);
        postReq.Content = new StringContent("{\"url\":\"https://example.com\"}", Encoding.UTF8, "application/json");
        using var postRes = await _client!.SendAsync(postReq);

        Assert.Equal(HttpStatusCode.OK, postRes.StatusCode);
        Assert.Contains(LocalCompanionService.PinnedChromeOrigin, postRes.Headers.GetValues("Access-Control-Allow-Origin"));
        Assert.Contains("Origin", postRes.Headers.GetValues("Vary"));
    }

    #endregion

    #region Body Size & Validation Tests

    [Fact]
    public async Task Payload_Over64KB_Returns413()
    {
        using var request = CreateRequest(HttpMethod.Post, "/api/tiles");
        string bigTitle = new string('A', 65 * 1024);
        string json = $"{{\"url\":\"https://example.com\",\"title\":\"{bigTitle}\"}}";
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Url_NonHttpScheme_Returns400()
    {
        using var request = CreateRequest(HttpMethod.Post, "/api/tiles");
        request.Content = new StringContent("{\"url\":\"javascript:alert(1)\"}", Encoding.UTF8, "application/json");

        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Dimensions_InvalidSpan_Returns400()
    {
        using var request = CreateRequest(HttpMethod.Post, "/api/tiles");
        request.Content = new StringContent("{\"url\":\"https://example.com\",\"spanX\":3,\"spanY\":3}", Encoding.UTF8, "application/json");

        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Workspace_UnknownId_Returns404()
    {
        _service!.WorkspacesHandler = () => new List<CompanionWorkspaceDto>
        {
            new("ws-1", "Main", true)
        };

        using var request = CreateRequest(HttpMethod.Post, "/api/tiles");
        request.Content = new StringContent("{\"url\":\"https://example.com\",\"workspaceId\":\"invalid-ws\"}", Encoding.UTF8, "application/json");

        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Workspace_Missing_DefaultsToActive()
    {
        CompanionPinRequest? captured = null;
        _service!.PinTileHandler = (req, ct) =>
        {
            captured = req;
            return Task.FromResult(new CompanionPinResult(true, false, "t-1", 2, 4));
        };

        using var request = CreateRequest(HttpMethod.Post, "/api/tiles");
        request.Content = new StringContent("{\"url\":\"https://example.com\"}", Encoding.UTF8, "application/json");

        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(captured);
        Assert.Null(captured.WorkspaceId); // Defaults cleanly to active
    }

    [Fact]
    public async Task PinTile_DuplicateUrl_ReturnsDuplicateTrue()
    {
        _service!.PinTileHandler = (req, ct) => Task.FromResult(new CompanionPinResult(
            Success: true,
            Duplicate: true,
            TileId: "existing-123",
            Col: 8,
            Row: 2,
            Message: "Tile already exists in workspace"
        ));

        using var request = CreateRequest(HttpMethod.Post, "/api/tiles");
        request.Content = new StringContent("{\"url\":\"https://example.com\"}", Encoding.UTF8, "application/json");

        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"duplicate\":true", body);
        Assert.Contains("existing-123", body);
    }

    [Fact]
    public async Task UiThreadStalled_Returns503()
    {
        _service!.PinTileHandler = async (req, ct) =>
        {
            // Simulate UI thread frozen past the 2s timeout
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            return new CompanionPinResult(true, false, "t-1", 0, 0);
        };

        using var request = CreateRequest(HttpMethod.Post, "/api/tiles");
        request.Content = new StringContent("{\"url\":\"https://example.com\"}", Encoding.UTF8, "application/json");

        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("2", response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString());
    }

    #endregion

    #region Port Fallback Tests

    [Fact]
    public async Task PortBusy_FallsBackToNext()
    {
        // _service is already running on _port. Starting another service with preferredPort: _port
        // must automatically detect that _port is busy and fall back to the next free port.
        var fallbackService = new LocalCompanionService();
        await fallbackService.StartAsync(preferredPort: _port);

        try
        {
            Assert.True(fallbackService.IsRunning);
            Assert.NotEqual(_port, fallbackService.ActivePort);
            Assert.True(fallbackService.ActivePort > _port);
        }
        finally
        {
            await fallbackService.StopAsync();
            fallbackService.Dispose();
        }
    }

    #endregion

    #region URL Sanitizer Tests

    [Theory]
    [InlineData(
        "https://www.youtube.com/watch?v=dQw4w9WgXcQ&utm_source=twitter&utm_medium=social&si=xyz123",
        "https://www.youtube.com/watch?v=dQw4w9WgXcQ"
    )]
    [InlineData(
        "https://example.com/article?fbclid=abcdef&gclid=12345&q=dotnet",
        "https://example.com/article?q=dotnet"
    )]
    [InlineData(
        "https://example.com/page?utm_campaign=summer_sale",
        "https://example.com/page"
    )]
    [InlineData(
        "https://music.youtube.com/watch?v=abc&list=RDxyz",
        "https://music.youtube.com/watch?v=abc&list=RDxyz"
    )]
    public void UrlSanitizer_StripsUtm_PreservesYouTubeV(string input, string expected)
    {
        string actual = LocalCompanionService.SanitizeUrl(input);
        Assert.Equal(expected, actual);
    }

    #endregion

    #region Anti-SSRF Defense Tests

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.255.255.255", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("10.254.1.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("224.0.0.1", true)]
    [InlineData("240.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    [InlineData("93.184.216.34", false)]
    public void Ssrf_Ipv4_BlockVerification(string ipStr, bool shouldBlock)
    {
        var ip = IPAddress.Parse(ipStr);
        Assert.Equal(shouldBlock, LocalCompanionService.IsBlockedIp(ip));
    }

    [Theory]
    [InlineData("::1", true)]
    [InlineData("::", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("::ffff:192.168.1.1", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("fe80::1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("fd12:3456:789a::1", true)]
    [InlineData("2606:4700:4700::1111", false)]
    public void Ssrf_Ipv6_BlockVerification(string ipStr, bool shouldBlock)
    {
        var ip = IPAddress.Parse(ipStr);
        Assert.Equal(shouldBlock, LocalCompanionService.IsBlockedIp(ip));
    }

    [Fact]
    public async Task Ssrf_ManualRedirectToLoopback_Aborts()
    {
        // CompanionImageDownloader directly rejects attempts to connect to loopback
        string? result = await CompanionImageDownloader.DownloadImageAsync($"http://127.0.0.1:{_port}/api/health");
        Assert.Null(result);
    }

    #endregion

    #region Concurrency Tests

    [Fact]
    public async Task Concurrent50Pins_SerialPlacement()
    {
        int callCount = 0;
        _service!.PinTileHandler = (req, ct) =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(new CompanionPinResult(true, false, $"t-{Guid.NewGuid():N}", 0, 0));
        };

        var tasks = new List<Task<HttpResponseMessage>>();
        for (int i = 0; i < 50; i++)
        {
            var req = CreateRequest(HttpMethod.Post, "/api/tiles");
            req.Content = new StringContent($"{{\"url\":\"https://example.com/{i}\"}}", Encoding.UTF8, "application/json");
            tasks.Add(_client!.SendAsync(req));
        }

        var responses = await Task.WhenAll(tasks);
        foreach (var res in responses)
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            res.Dispose();
        }

        Assert.Equal(50, callCount);
    }

    #endregion

    #region Phase 2: TileManager & Companion Integration Tests

    private static TileManager CreateTestTileManager(
        ObservableCollection<TileModel> tiles,
        ObservableCollection<TileGroupModel> groups)
    {
        return new TileManager(
            tilesProvider: () => tiles,
            groupsProvider: () => groups,
            tilesListBoxProvider: () => null,
            contentScrollViewerProvider: () => null,
            windowWidthProvider: () => 1920.0,
            animateModifiedTilesAction: _ => { },
            updateGroupHeaderPositionsAction: () => { },
            updateLayoutMetricsAction: () => { },
            updateCanvasHeightAction: () => { },
            updateExposedAddSlotsAction: () => { },
            saveGroupsAndLayoutAction: () => { },
            cleanEmptyGroupsAndReflowAction: () => { },
            compactGroupGapsAction: () => { },
            flashLockedGroupAction: _ => { },
            hideDropSlotIndicatorAction: () => { },
            historyService: new LayoutHistoryService(),
            dispatcher: Dispatcher.CurrentDispatcher);
    }

    [Fact]
    public void PinWebLinkFromCompanion_NewLink_PlacesTileAndSavesLayout()
    {
        WpfTestHost.RunSta(() =>
        {
            var tiles = new ObservableCollection<TileModel>();
            var groups = new ObservableCollection<TileGroupModel>();
            var manager = CreateTestTileManager(tiles, groups);

            var req = new CompanionPinRequest("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
            var result = manager.PinWebLinkFromCompanionAsync(req, CancellationToken.None).GetAwaiter().GetResult();

            Assert.True(result.Success);
            Assert.False(result.Duplicate);
            Assert.NotNull(result.TileId);
            Assert.Single(tiles);

            var tile = tiles[0];
            Assert.Equal(result.TileId, tile.Id);
            Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", tile.TargetPath);
            Assert.Equal("YouTube", tile.Title);
            Assert.Equal(TileType.WebUrl, tile.TileType);
            Assert.Equal(2, tile.SpanX);
            Assert.Equal(2, tile.SpanY);
        });
    }

    [Fact]
    public void PinWebLinkFromCompanion_ExistingUrl_ReturnsDuplicateTrueWithoutAdding()
    {
        WpfTestHost.RunSta(() =>
        {
            var tiles = new ObservableCollection<TileModel>
            {
                new()
                {
                    Id = "existing-cricket",
                    Title = "Cricket Match",
                    TargetPath = "https://example.com/cricket",
                    TileType = TileType.WebUrl,
                    Col = 4,
                    Row = 2
                }
            };
            var groups = new ObservableCollection<TileGroupModel>();
            var manager = CreateTestTileManager(tiles, groups);

            var req = new CompanionPinRequest("https://example.com/cricket");
            var result = manager.PinWebLinkFromCompanionAsync(req, CancellationToken.None).GetAwaiter().GetResult();

            Assert.True(result.Success);
            Assert.True(result.Duplicate);
            Assert.Equal("existing-cricket", result.TileId);
            Assert.Equal(4, result.Col);
            Assert.Equal(2, result.Row);
            Assert.Single(tiles); // No duplicate added
        });
    }

    [Fact]
    public void PinWebLinkFromCompanion_4x2Span_PreservesDimensions()
    {
        WpfTestHost.RunSta(() =>
        {
            var tiles = new ObservableCollection<TileModel>();
            var groups = new ObservableCollection<TileGroupModel>();
            var manager = CreateTestTileManager(tiles, groups);

            var req = new CompanionPinRequest(
                Url: "https://example.com/wide",
                SpanX: 4,
                SpanY: 2
            );
            var result = manager.PinWebLinkFromCompanionAsync(req, CancellationToken.None).GetAwaiter().GetResult();

            Assert.True(result.Success);
            Assert.False(result.Duplicate);
            Assert.Single(tiles);
            Assert.Equal(4, tiles[0].SpanX);
            Assert.Equal(2, tiles[0].SpanY);
        });
    }

    [Fact]
    public void PinWebLinkFromCompanion_CustomTitle_AppliesCustomTitle()
    {
        WpfTestHost.RunSta(() =>
        {
            var tiles = new ObservableCollection<TileModel>();
            var groups = new ObservableCollection<TileGroupModel>();
            var manager = CreateTestTileManager(tiles, groups);

            var req = new CompanionPinRequest(
                Url: "https://www.youtube.com/watch?v=123",
                Title: "England vs Australia Finals"
            );
            var result = manager.PinWebLinkFromCompanionAsync(req, CancellationToken.None).GetAwaiter().GetResult();

            Assert.True(result.Success);
            Assert.Single(tiles);
            Assert.Equal("England vs Australia Finals", tiles[0].Title);
        });
    }

    #endregion

    #region Tile Group Tests

    [Fact]
    public async Task PostTileGroups_ValidPayload_Returns200AndGroupDetails()
    {
        CompanionTileGroupRequest? captured = null;
        _service!.PinTileGroupHandler = (req, ct) =>
        {
            captured = req;
            return Task.FromResult(new CompanionTileGroupResult(
                Success: true,
                GroupId: "grp-test-1",
                GroupTitle: req.GroupName ?? "Session",
                TilesAdded: req.Tiles?.Count ?? 0,
                Col: 0,
                Row: 0
            ));
        };

        string json = """
        {
            "groupName": "Research Session",
            "tiles": [
                { "url": "https://example.com/doc1", "title": "Doc 1" },
                { "url": "https://example.com/doc2", "title": "Doc 2" },
                { "url": "https://example.com/doc3", "title": "Doc 3" }
            ]
        }
        """;

        using var request = CreateRequest(HttpMethod.Post, "/api/tile-groups");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string resBody = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"success\":true", resBody);
        Assert.Contains("grp-test-1", resBody);
        Assert.Contains("\"tilesAdded\":3", resBody);
        Assert.NotNull(captured);
        Assert.Equal("Research Session", captured.GroupName);
        Assert.Equal(3, captured.Tiles!.Count);
    }

    [Fact]
    public async Task PostTileGroups_EmptyTilesList_Returns400()
    {
        using var request = CreateRequest(HttpMethod.Post, "/api/tile-groups");
        request.Content = new StringContent("{\"groupName\":\"Test\",\"tiles\":[]}", Encoding.UTF8, "application/json");

        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostTileGroups_OnlyInvalidUrls_Returns400()
    {
        string json = """
        {
            "groupName": "Test",
            "tiles": [
                { "url": "javascript:alert(1)" },
                { "url": "file:///C:/secrets.txt" }
            ]
        }
        """;
        using var request = CreateRequest(HttpMethod.Post, "/api/tile-groups");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostTileGroups_Over100Tabs_Returns400()
    {
        var items = new StringBuilder("{\"groupName\":\"Big\",\"tiles\":[");
        for (int i = 0; i < 101; i++)
        {
            if (i > 0) items.Append(',');
            items.Append($"{{\"url\":\"https://example.com/tab{i}\"}}");
        }
        items.Append("]}");

        using var request = CreateRequest(HttpMethod.Post, "/api/tile-groups");
        request.Content = new StringContent(items.ToString(), Encoding.UTF8, "application/json");

        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostTileGroups_FiltersOutInvalidUrlsWhileKeepingValid()
    {
        CompanionTileGroupRequest? captured = null;
        _service!.PinTileGroupHandler = (req, ct) =>
        {
            captured = req;
            return Task.FromResult(new CompanionTileGroupResult(true, "grp-1", "Mixed", req.Tiles?.Count ?? 0, 0, 0));
        };

        string json = """
        {
            "groupName": "Mixed",
            "tiles": [
                { "url": "https://example.com/valid1" },
                { "url": "about:blank" },
                { "url": "https://example.com/valid2" }
            ]
        }
        """;

        using var request = CreateRequest(HttpMethod.Post, "/api/tile-groups");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(captured);
        Assert.Equal(2, captured.Tiles!.Count);
    }

    [Fact]
    public void PinTileGroupFromCompanion_PlacesGroupAndTilesInGrid()
    {
        WpfTestHost.RunSta(() =>
        {
            var tiles = new ObservableCollection<TileModel>();
            var groups = new ObservableCollection<TileGroupModel>();
            var manager = CreateTestTileManager(tiles, groups);

            var req = new CompanionTileGroupRequest(
                GroupName: "Project Alpha",
                Tiles: new List<CompanionPinRequest>
                {
                    new("https://example.com/tab1", Title: "Tab 1"),
                    new("https://example.com/tab2", Title: "Tab 2"),
                    new("https://example.com/tab3", Title: "Tab 3"),
                    new("https://example.com/tab4", Title: "Tab 4"),
                    new("https://example.com/tab5", Title: "Tab 5")
                }
            );

            var result = manager.PinTileGroupFromCompanionAsync(req, CancellationToken.None).GetAwaiter().GetResult();

            Assert.True(result.Success);
            Assert.NotNull(result.GroupId);
            Assert.Equal("Project Alpha", result.GroupTitle);
            Assert.Equal(5, result.TilesAdded);

            Assert.Single(groups);
            Assert.Equal(result.GroupId, groups[0].Id);
            Assert.Equal("Project Alpha", groups[0].Title);

            Assert.Equal(5, tiles.Count);
            foreach (var t in tiles)
            {
                Assert.Equal(result.GroupId, t.Group);
                Assert.Equal("Project Alpha", t.SectionHeader);
                Assert.Equal(2, t.SpanX);
                Assert.Equal(2, t.SpanY);
                Assert.Equal(TileType.WebUrl, t.TileType);
            }

            // First 4 tiles fit in row 1 (columns 0, 2, 4, 6)
            Assert.Equal(0, tiles[0].Col);
            Assert.Equal(1, tiles[0].Row);
            Assert.Equal(2, tiles[1].Col);
            Assert.Equal(1, tiles[1].Row);
            Assert.Equal(4, tiles[2].Col);
            Assert.Equal(1, tiles[2].Row);
            Assert.Equal(6, tiles[3].Col);
            Assert.Equal(1, tiles[3].Row);

            // 5th tile wraps to row 3 (column 0)
            Assert.Equal(0, tiles[4].Col);
            Assert.Equal(3, tiles[4].Row);
        });
    }

    [Fact]
    public void PinTileGroupFromCompanion_DeduplicatesSameUrlsWithinSession()
    {
        WpfTestHost.RunSta(() =>
        {
            var tiles = new ObservableCollection<TileModel>();
            var groups = new ObservableCollection<TileGroupModel>();
            var manager = CreateTestTileManager(tiles, groups);

            var req = new CompanionTileGroupRequest(
                GroupName: "Duplicates Test",
                Tiles: new List<CompanionPinRequest>
                {
                    new("https://example.com/same-url"),
                    new("https://example.com/same-url/"),
                    new("https://example.com/different-url")
                }
            );

            var result = manager.PinTileGroupFromCompanionAsync(req, CancellationToken.None).GetAwaiter().GetResult();

            Assert.True(result.Success);
            Assert.Equal(2, result.TilesAdded); // 1 duplicate filtered
            Assert.Equal(2, tiles.Count);
        });
    }

    [Fact]
    public void PinTileGroupFromCompanion_PlacesBelowExistingContent()
    {
        WpfTestHost.RunSta(() =>
        {
            var tiles = new ObservableCollection<TileModel>
            {
                new()
                {
                    Id = "loose-1",
                    Title = "Existing Loose",
                    TargetPath = "https://example.com/existing",
                    TileType = TileType.WebUrl,
                    Col = 0,
                    Row = 2,
                    SpanX = 2,
                    SpanY = 2
                }
            };
            var groups = new ObservableCollection<TileGroupModel>();
            var manager = CreateTestTileManager(tiles, groups);

            var req = new CompanionTileGroupRequest(
                GroupName: "Appended Session",
                Tiles: new List<CompanionPinRequest>
                {
                    new("https://example.com/new1")
                }
            );

            var result = manager.PinTileGroupFromCompanionAsync(req, CancellationToken.None).GetAwaiter().GetResult();

            Assert.True(result.Success);
            Assert.Single(groups);
            // Existing tile bottom is row 4 (2 + 2). New group starts below at row 5.
            Assert.Equal(5, groups[0].Row);
        });
    }

    #endregion
}
