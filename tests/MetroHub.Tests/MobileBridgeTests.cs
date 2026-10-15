using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Core.Services.MobileBridge;
using MetroHub.Widgets;
using MetroHub.Widgets.Catalog.PhoneBridge;
using MetroHub.Widgets.Registry;
using MetroHub.Widgets.Serialization;
using Xunit;

namespace MetroHub.Tests;

/// <summary>
/// End-to-end coverage of the phone LAN bridge over loopback: transport framing, token gate,
/// pin and upload routes, server-sent events, QR rendering, and the pairing widget.
/// </summary>
#pragma warning disable CA1001 // xUnit owns the lifecycle: IAsyncLifetime.DisposeAsync releases every field.
public sealed class MobileBridgeTests : IAsyncLifetime
{
    /// <summary>1x1 transparent PNG — the smallest thing that passes the image sniff.</summary>
    private const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=";

    private MobileBridgeService _bridge = null!;
    private HttpClient _client = null!;
    private HttpClientHandler _handler = null!;
    private string _sandbox = null!;
    private string _pairedPhonesPath = null!;
    private CompanionPinRequest? _lastPinRequest;
    private string? _lastImagePath;
    private string? _lastImageTitle;
    private int _lastImageSpanX;
    private int _lastImageSpanY;

    public async Task InitializeAsync()
    {
        _sandbox = Directory.CreateTempSubdirectory("MetroHub_BridgeTests_").FullName;
        _pairedPhonesPath = Path.Combine(_sandbox, "paired_phones.json");

        _bridge = new MobileBridgeService(_pairedPhonesPath);

        _bridge.PinTileHandler = (request, _) =>
        {
            _lastPinRequest = request;
            return Task.FromResult(new CompanionPinResult(
                Success: true,
                Duplicate: false,
                TileId: "tile-" + Guid.NewGuid().ToString("N"),
                Col: 1,
                Row: 2,
                Message: null));
        };

        _bridge.LocalImagePinHandler = (path, title, _, spanX, spanY, _) =>
        {
            _lastImagePath = path;
            _lastImageTitle = title;
            _lastImageSpanX = spanX;
            _lastImageSpanY = spanY;
            return Task.FromResult(new CompanionPinResult(
                Success: true,
                Duplicate: false,
                TileId: "img-" + Guid.NewGuid().ToString("N"),
                Col: 3,
                Row: 4,
                Message: null));
        };

        _bridge.WorkspacesHandler = () => new List<CompanionWorkspaceDto>
        {
            new("ws-1", "Work", true),
            new("ws-2", "Home", false)
        };

        // Port 0 asks the OS for a free port, so tests never collide with a real bridge.
        MobileBridgeStartResult start = await _bridge.StartOnAsync(IPAddress.Loopback, 0).ConfigureAwait(false);
        Assert.True(start.Started, start.Message);

        _handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = false
        };

        _client = new HttpClient(_handler, disposeHandler: false)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_bridge.Port}"),
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        _handler.Dispose();
        await _bridge.StopAsync().ConfigureAwait(false);
        _bridge.Dispose();

        try
        {
            Directory.Delete(_sandbox, recursive: true);
        }
        catch
        {
            // Sandbox cleanup is best effort.
        }
    }

    private string Token => _bridge.TokenValue;

    private HttpRequestMessage Authorized(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(MobileBridgeEndpoints.TokenHeaderName, Token);
        return request;
    }

    // ── Token ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Token_IsRandomAndComparedInFull()
    {
        MobileBridgeToken a = MobileBridgeToken.Create();
        MobileBridgeToken b = MobileBridgeToken.Create();

        Assert.NotEqual(a.Value, b.Value);
        Assert.Equal(32, a.Value.Length);
        Assert.True(a.Matches(a.Value));
        Assert.False(a.Matches(b.Value));
        Assert.False(a.Matches(null));
        Assert.False(a.Matches(string.Empty));
        Assert.False(a.Matches(a.Value.ToUpperInvariant()));
        Assert.False(a.Matches(a.Value + "0"));
    }

    // ── LAN address policy ────────────────────────────────────────────────────

    [Theory]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.4.9", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.168.1.24", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("172.15.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("169.254.10.10", false)]
    [InlineData("127.0.0.1", false)]
    public void IsPrivateIpv4_ClassifiesOnlyRfc1918(string address, bool expected)
    {
        Assert.Equal(expected, LanAddressResolver.IsPrivateIpv4(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("192.168.0.44", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("203.0.113.9", false)]
    [InlineData("8.8.4.4", false)]
    public void IsAllowedPeer_AcceptsLoopbackAndPrivateOnly(string address, bool expected)
    {
        Assert.Equal(expected, LanAddressResolver.IsAllowedPeer(IPAddress.Parse(address)));
    }

    [Fact]
    public void ResolveCandidates_NeverReturnsNonPrivateAddresses()
    {
        foreach (LanEndpoint endpoint in LanAddressResolver.ResolveCandidates())
        {
            Assert.True(IPAddress.TryParse(endpoint.IpAddress, out IPAddress? parsed));
            Assert.True(LanAddressResolver.IsPrivateIpv4(parsed!));
            Assert.False(string.IsNullOrWhiteSpace(endpoint.AdapterName));
        }
    }

    // ── Rate limiter ──────────────────────────────────────────────────────────

    [Fact]
    public void RateLimiter_AllowsUpToTheLimitThenBlocks()
    {
        var limiter = new MobileBridgeRateLimiter(3, TimeSpan.FromMinutes(1));

        Assert.True(limiter.TryAcquire("a"));
        Assert.True(limiter.TryAcquire("a"));
        Assert.True(limiter.TryAcquire("a"));
        Assert.False(limiter.TryAcquire("a"));

        // Independent keys have independent budgets.
        Assert.True(limiter.TryAcquire("b"));
    }

    [Fact]
    public void RateLimiter_WindowExpiryRestoresBudget()
    {
        var limiter = new MobileBridgeRateLimiter(1, TimeSpan.FromMilliseconds(60));

        Assert.True(limiter.TryAcquire("k"));
        Assert.False(limiter.TryAcquire("k"));
        Thread.Sleep(120);
        Assert.True(limiter.TryAcquire("k"));
    }

    // ── Embedded assets ───────────────────────────────────────────────────────

    [Fact]
    public void EmbeddedPhoneAssets_ArePresentAndTyped()
    {
        IReadOnlyList<string> names = MobileBridgeAssets.Names;

        foreach (string expected in new[]
                 {
                     "index.html", "app.css", "app.js", "manifest.webmanifest",
                     "sw.js", "icon-192.png", "icon-512.png"
                 })
        {
            Assert.Contains(expected, names);
        }

        Assert.True(MobileBridgeAssets.TryGet("/m/index.html", out byte[] html, out string htmlType));
        Assert.True(html.Length > 512);
        Assert.StartsWith("text/html", htmlType);

        Assert.True(MobileBridgeAssets.TryGet("/m/app.css", out byte[] css, out string cssType));
        Assert.StartsWith("text/css", cssType);
        Assert.True(css.Length > 512);

        Assert.True(MobileBridgeAssets.TryGet("/m/app.js", out byte[] js, out string jsType));
        Assert.StartsWith("application/javascript", jsType);
        Assert.True(js.Length > 512);

        Assert.False(MobileBridgeAssets.TryGet("/m/../appsettings.json", out _, out _));
        Assert.False(MobileBridgeAssets.TryGet("/m/nope.txt", out _, out _));
    }

    [Fact]
    public void PhoneShell_HasNoExternalDependencies()
    {
        Assert.True(MobileBridgeAssets.TryGet("/m/index.html", out byte[] html, out _));
        Assert.True(MobileBridgeAssets.TryGet("/m/app.css", out byte[] css, out _));
        Assert.True(MobileBridgeAssets.TryGet("/m/app.js", out byte[] js, out _));

        string shell = Encoding.UTF8.GetString(html);
        string style = Encoding.UTF8.GetString(css);
        string script = Encoding.UTF8.GetString(js);

        // The shell must not pull anything off the network. Absolute URLs that appear as data are
        // fine (an input placeholder hint, prefixing a bare domain the user typed); what must never
        // appear is an external reference.
        foreach (string content in new[] { shell, style, script })
        {
            Assert.DoesNotContain("//cdn", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("googleapis", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("@import", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("src=\"http", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("src='http", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("href=\"http", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("href='http", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("url(http", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fetch(\"http", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fetch('http", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("EventSource(\"http", content, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void PhoneShell_OffersTheThreeActionsAndAppInstallation()
    {
        Assert.True(MobileBridgeAssets.TryGet("/m/index.html", out byte[] html, out _));
        string shell = Encoding.UTF8.GetString(html);

        // Three large actions, each opening its own focused composer.
        Assert.Contains("data-action=\"link\"", shell, StringComparison.Ordinal);
        Assert.Contains("data-action=\"note\"", shell, StringComparison.Ordinal);
        Assert.Contains("data-action=\"photo\"", shell, StringComparison.Ordinal);
        Assert.Contains("id=\"composerSheet\"", shell, StringComparison.Ordinal);
        Assert.Contains("id=\"statusPill\"", shell, StringComparison.Ordinal);

        // The install card offers home-screen / PWA app install.
        Assert.Contains("id=\"installCard\"", shell, StringComparison.Ordinal);

        Assert.True(MobileBridgeAssets.TryGet("/m/app.js", out byte[] js, out _));
        string script = Encoding.UTF8.GetString(js);

        Assert.DoesNotContain("getUserMedia", script, StringComparison.Ordinal);
    }

    // ── QR ────────────────────────────────────────────────────────────────────

    [Fact]
    public void PairingQrCode_RendersAValidPng()
    {
        byte[]? png = PairingQrCode.TryRenderPng(
            "http://192.168.1.24:48850/m/?t=00112233445566778899aabbccddeeff",
            4);

        Assert.NotNull(png);
        Assert.True(png!.Length > 100);
        Assert.Equal(0x89, png[0]);
        Assert.Equal(0x50, png[1]);
        Assert.Equal(0x4E, png[2]);
        Assert.Equal(0x47, png[3]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void PairingQrCode_ReturnsNullWithoutAPayload(string? payload)
    {
        Assert.Null(PairingQrCode.TryRenderPng(payload!));
    }

    // ── Transport: health, shell, static assets ───────────────────────────────

    [Fact]
    public async Task Health_IsUnauthenticatedAndLeaksNothing()
    {
        using var response = await _client.GetAsync("/m/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        Assert.True(root.GetProperty("bridge").GetBoolean());
        Assert.True(root.GetProperty("requiresToken").GetBoolean());
        Assert.False(root.TryGetProperty("lanAddress", out _));
        Assert.False(root.TryGetProperty("deviceName", out _));
        Assert.False(root.TryGetProperty("port", out _));
    }

    [Fact]
    public async Task Shell_WithoutToken_Returns401Html()
    {
        using var response = await _client.GetAsync("/m/");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task Shell_WithToken_ServesAppAndUpgradesToCookie()
    {
        using var response = await _client.GetAsync($"/m/?t={Token}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType?.ToString() ?? string.Empty);

        string html = await response.Content.ReadAsStringAsync();
        Assert.Contains("MetroHub", html, StringComparison.Ordinal);

        Assert.Contains(
            response.Headers,
            header => header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase));

        // The upgraded cookie alone must satisfy the next request.
        using var follow = await _client.GetAsync("/m/api/context");
        Assert.Equal(HttpStatusCode.OK, follow.StatusCode);
    }

    [Fact]
    public async Task ShareTarget_ServesTheShellForAShareSheetEntry()
    {
        // The manifest points share_target at /m/share, so this must behave exactly like the shell
        // and it must survive the query parameters the share sheet appends.
        using var response = await _client.GetAsync(
            $"/m/share?t={Token}&url=https%3A%2F%2Fexample.com%2Fread&text=Read+later&title=Example");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType?.ToString() ?? string.Empty);

        string html = await response.Content.ReadAsStringAsync();
        Assert.Contains("app.js", html, StringComparison.Ordinal);

        // Pairing happens on this navigation too, so a shared link from a fresh scan still works.
        using var follow = await _client.GetAsync("/m/api/context");
        Assert.Equal(HttpStatusCode.OK, follow.StatusCode);
    }

    [Fact]
    public async Task StaticAssetsAreServedWithoutAToken()
    {
        using var css = await _client.GetAsync("/m/app.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);

        using var js = await _client.GetAsync("/m/app.js");
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);

        using var icon = await _client.GetAsync("/m/icon-192.png");
        Assert.Equal(HttpStatusCode.OK, icon.StatusCode);
        Assert.Equal("image/png", icon.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task RootRedirectsToThePhoneShell()
    {
        using var response = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/m/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task UnknownRoutesReturn404()
    {
        using var request = Authorized(HttpMethod.Get, "/m/api/does-not-exist");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Token gate ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Context_WithoutToken_Returns401()
    {
        using var response = await _client.GetAsync("/m/api/context");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Context_WithWrongToken_Returns401()
    {
        using var response = await _client.GetAsync($"/m/api/context?t={new string('f', Token.Length)}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Context_WithTokenHeader_ReturnsWorkspacesAndSizes()
    {
        using var request = Authorized(HttpMethod.Get, "/m/api/context");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = document.RootElement;

        Assert.Equal("Work", root.GetProperty("activeWorkspaceName").GetString());
        Assert.Equal(2, root.GetProperty("workspaces").GetArrayLength());

        List<(int X, int Y)> sizes = root.GetProperty("allowedSizes")
            .EnumerateArray()
            .Select(s => (s.GetProperty("spanX").GetInt32(), s.GetProperty("spanY").GetInt32()))
            .ToList();

        Assert.Contains((2, 2), sizes);
        Assert.Contains((4, 2), sizes);
        Assert.Equal(2, sizes.Count);
    }

    // ── Pin route ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pin_NonJsonContentType_IsRejected()
    {
        using var request = Authorized(HttpMethod.Post, "/m/api/pin");
        request.Content = new StringContent("{\"url\":\"https://example.com\"}", Encoding.UTF8, "text/plain");

        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_lastPinRequest);
    }

    [Fact]
    public async Task Pin_EmptyBody_IsRejected()
    {
        using var request = Authorized(HttpMethod.Post, "/m/api/pin");
        request.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Pin_WithoutAnyPayload_IsRejected()
    {
        using var response = await PostJsonAsync("{}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Pin_ValidLink_ReachesTheDesktopHandler()
    {
        using var response = await PostJsonAsync("{\"url\":\"https://example.com/article?id=42\",\"title\":\"Read later\"}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.NotNull(_lastPinRequest);
        Assert.Equal("https://example.com/article?id=42", _lastPinRequest!.Url);
        Assert.Equal("Read later", _lastPinRequest.Title);
        Assert.Equal(2, _lastPinRequest.SpanX);
        Assert.Equal(2, _lastPinRequest.SpanY);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task Pin_ExplicitSpanAndWorkspace_AreForwarded()
    {
        using var response = await PostJsonAsync(
            "{\"url\":\"https://example.com\",\"spanX\":4,\"spanY\":2,\"workspaceId\":\"ws-2\"}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(_lastPinRequest);
        Assert.Equal(4, _lastPinRequest!.SpanX);
        Assert.Equal(2, _lastPinRequest.SpanY);
        Assert.Equal("ws-2", _lastPinRequest.WorkspaceId);
    }

    [Theory]
    [InlineData("{\"url\":\"https://example.com\",\"spanX\":1,\"spanY\":1}")]
    [InlineData("{\"url\":\"https://example.com\",\"spanX\":8,\"spanY\":8}")]
    [InlineData("{\"url\":\"https://example.com\",\"spanX\":2,\"spanY\":4}")]
    public async Task Pin_IllegalSpans_AreRejected(string payload)
    {
        using var response = await PostJsonAsync(payload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_lastPinRequest);
    }

    [Theory]
    [InlineData("{\"url\":\"file:///C:/secret.txt\"}")]
    [InlineData("{\"url\":\"javascript:alert(1)\"}")]
    [InlineData("{\"url\":\"not-a-url\"}")]
    public async Task Pin_NonHttpSchemes_AreRejected(string payload)
    {
        using var response = await PostJsonAsync(payload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_lastPinRequest);
    }

    [Fact]
    public async Task Pin_UnknownWorkspace_Returns404()
    {
        using var response = await PostJsonAsync("{\"note\":\"hello\",\"workspaceId\":\"missing\"}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Pin_NoteOnly_CreatesANoteTile()
    {
        using var response = await PostJsonAsync("{\"note\":\"Milk, eggs, coffee\",\"title\":\"Shopping\"}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.NotNull(_lastPinRequest);
        Assert.Equal("Milk, eggs, coffee", _lastPinRequest!.Note);
        Assert.True(string.IsNullOrEmpty(_lastPinRequest.Url));
    }

    [Fact]
    public async Task Pin_OversizedBody_Returns413()
    {
        string huge = "{\"note\":\"" + new string('x', 70 * 1024) + "\"}";
        using var response = await PostJsonAsync(huge);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Pin_TitleAndNote_AreTruncatedToTheSharedLimits()
    {
        string longTitle = new('t', 400);
        string longNote = new('n', 9000);
        using var response = await PostJsonAsync($"{{\"note\":\"{longNote}\",\"title\":\"{longTitle}\"}}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(_lastPinRequest);
        Assert.Equal(CompanionPinValidator.MaxTitleLength, _lastPinRequest!.Title!.Length);
        Assert.Equal(CompanionPinValidator.MaxNoteLength, _lastPinRequest.Note!.Length);
    }

    [Fact]
    public async Task Pin_UrlWithEmbeddedCredentials_IsRejectedBySharedValidator()
    {
        using var response = await PostJsonAsync("{\"url\":\"https://user:secret@example.com/path\"}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_lastPinRequest);
    }

    [Fact]
    public async Task Pin_ThumbnailWithEmbeddedCredentials_IsRejected()
    {
        using var response = await PostJsonAsync(
            "{\"note\":\"x\",\"thumbnailUrl\":\"https://user:secret@example.com/t.png\"}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_lastPinRequest);
    }

    private async Task<HttpResponseMessage> PostJsonAsync(string json)
    {
        using var request = Authorized(HttpMethod.Post, "/m/api/pin");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _client.SendAsync(request).ConfigureAwait(false);
    }

    // ── Upload route ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_StoresContentAddressedFileAndPlacesATile()
    {
        byte[] png = Convert.FromBase64String(OnePixelPngBase64);

        using var first = await UploadAsync(png, "holiday-photo.png", spanX: 4, spanY: 2);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        Assert.NotNull(_lastImagePath);
        Assert.True(File.Exists(_lastImagePath));
        Assert.Equal("holiday-photo", _lastImageTitle);
        Assert.Equal(4, _lastImageSpanX);
        Assert.Equal(2, _lastImageSpanY);

        string firstPath = _lastImagePath!;
        Assert.StartsWith(AppPaths.PastedImagesDir, firstPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".png", firstPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(png, await File.ReadAllBytesAsync(firstPath));

        // Identical bytes must resolve to the identical content-addressed path.
        using var second = await UploadAsync(png, "holiday-photo.png", spanX: 2, spanY: 2);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstPath, _lastImagePath);
    }

    [Fact]
    public async Task Upload_UnsupportedContentType_Returns415()
    {
        using var request = Authorized(HttpMethod.Put, "/m/api/upload?name=x.txt");
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes("not an image"));
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");

        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Null(_lastImagePath);
    }

    [Fact]
    public async Task Upload_ClaimedImageThatIsNotAnImage_Returns415()
    {
        using var request = Authorized(HttpMethod.Put, "/m/api/upload?name=fake.png");
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes("definitely not a png file at all"));
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");

        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Null(_lastImagePath);
    }

    [Fact]
    public async Task Upload_EmptyBody_Returns400()
    {
        using var request = Authorized(HttpMethod.Put, "/m/api/upload?name=empty.png");
        request.Content = new ByteArrayContent(Array.Empty<byte>());
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");

        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_ClientSuppliedPathTraversal_IsNeverUsedOnDisk()
    {
        byte[] png = Convert.FromBase64String(OnePixelPngBase64);

        using var response = await UploadAsync(png, "../../../../evil.png", spanX: 2, spanY: 2);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.NotNull(_lastImagePath);
        string resolved = Path.GetFullPath(_lastImagePath!);
        Assert.StartsWith(Path.GetFullPath(AppPaths.PastedImagesDir), resolved, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("..", resolved, StringComparison.Ordinal);
        Assert.Equal("evil", _lastImageTitle);
    }

    [Fact]
    public async Task Upload_RejectedImageLeavesNoFileBehind()
    {
        string imagesDir = AppPaths.PastedImagesDir;
        Directory.CreateDirectory(imagesDir);
        int before = Directory.GetFiles(imagesDir, "phone_*.tmp").Length;

        using var request = Authorized(HttpMethod.Put, "/m/api/upload?name=fake.png");
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes("nope"));
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(before, Directory.GetFiles(imagesDir, "phone_*.tmp").Length);
    }

    private async Task<HttpResponseMessage> UploadAsync(byte[] payload, string name, int spanX, int spanY)
    {
        string path = $"/m/api/upload?name={Uri.EscapeDataString(name)}&spanX={spanX}&spanY={spanY}";
        using var request = Authorized(HttpMethod.Put, path);
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        return await _client.SendAsync(request).ConfigureAwait(false);
    }

    // ── Server-sent events ────────────────────────────────────────────────────

    [Fact]
    public async Task Events_StreamsPcPushesToThePhone()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        using var request = Authorized(HttpMethod.Get, "/m/api/events");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using Stream stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        // retry: is written immediately after registration, so reading it proves the subscriber is live.
        Assert.Equal("retry: 3000", await reader.ReadLineAsync(cts.Token));
        Assert.Equal(string.Empty, await reader.ReadLineAsync(cts.Token));

        Assert.Equal(1, _bridge.PhoneClientCount);

        int delivered = _bridge.PublishToPhone("Open on your phone", "https://example.com/read", null);
        Assert.Equal(1, delivered);

        Assert.Equal("event: push", await reader.ReadLineAsync(cts.Token));

        string? dataLine = await reader.ReadLineAsync(cts.Token);
        Assert.NotNull(dataLine);
        Assert.StartsWith("data: ", dataLine);

        using var document = JsonDocument.Parse(dataLine!["data: ".Length..]);
        Assert.Equal("Open on your phone", document.RootElement.GetProperty("title").GetString());
        Assert.Equal("https://example.com/read", document.RootElement.GetProperty("url").GetString());
        Assert.Equal(_bridge.DeviceName, document.RootElement.GetProperty("deviceName").GetString());
    }

    [Fact]
    public async Task Events_WithoutToken_Returns401()
    {
        using var response = await _client.GetAsync("/m/api/events");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Events_PhoneDisconnect_TearsDownWithoutLoggingAHandlerFailure()
    {
        // Regression: the enumerator returned by ChannelReader.ReadAllAsync implements
        // IAsyncDisposable but throws NotSupportedException when disposed, so disposing it at the end
        // of the stream logged a "[MobileBridge] Request handler failed" error on every phone hang-up.
        TimeSpan originalHeartbeat = MobileBridgeEndpoints.SseHeartbeat;
        Action<Exception, string> originalLogger = Safe.Logger;
        var logged = new List<Exception>();

        MobileBridgeEndpoints.SseHeartbeat = TimeSpan.FromMilliseconds(150);
        Safe.Logger = (ex, context) =>
        {
            if (context.Contains("MobileBridge", StringComparison.Ordinal))
            {
                lock (logged)
                {
                    logged.Add(ex);
                }
            }

            originalLogger(ex, context);
        };

        try
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
            {
                using var request = Authorized(HttpMethod.Get, "/m/api/events");
                using HttpResponseMessage response = await _client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                await using Stream stream = await response.Content.ReadAsStreamAsync(cts.Token);
                using var reader = new StreamReader(stream);
                Assert.Equal("retry: 3000", await reader.ReadLineAsync(cts.Token));
            }

            // The response is gone, so the next heartbeat write fails and the server tears the
            // subscriber down. That teardown is where the old code used to throw.
            for (int i = 0; i < 200 && _bridge.PhoneClientCount > 0; i++)
            {
                await Task.Delay(25);
            }

            Assert.Equal(0, _bridge.PhoneClientCount);

            lock (logged)
            {
                Assert.Empty(logged);
            }
        }
        finally
        {
            MobileBridgeEndpoints.SseHeartbeat = originalHeartbeat;
            Safe.Logger = originalLogger;
        }
    }

    [Fact]
    public void SseHub_CapsSubscribersAndUnregisters()
    {
        using var hub = new MobileBridgeSseHub();
        var clients = new List<MobileBridgeSseClient>();

        for (int i = 0; i < MobileBridgeSseHub.MaxClients; i++)
        {
            var client = new MobileBridgeSseClient();
            clients.Add(client);
            Assert.True(hub.TryRegister(client));
        }

        var overflow = new MobileBridgeSseClient();
        Assert.False(hub.TryRegister(overflow));
        Assert.Equal(MobileBridgeSseHub.MaxClients, hub.ClientCount);

        hub.Unregister(clients[0]);
        Assert.Equal(MobileBridgeSseHub.MaxClients - 1, hub.ClientCount);

        overflow.Dispose();
        foreach (var client in clients)
        {
            client.Dispose();
        }
    }

    [Fact]
    public void PublishToPhone_RecordsActivityAndReportsZeroWithoutSubscribers()
    {
        Assert.Equal(0, _bridge.PublishToPhone("note title", null, "a short note"));

        IReadOnlyList<MobileBridgeRecentItemDto> recent = _bridge.RecentItems;
        Assert.NotEmpty(recent);
        Assert.Equal("toPhone", recent[0].Direction);
        Assert.Equal("note", recent[0].Kind);
        Assert.Equal("note title", recent[0].Title);
    }

    // ── Transport hardening (raw sockets) ────────────────────────────────────

    [Fact]
    public async Task Transport_RejectsChunkedRequestBodies()
    {
        (string statusLine, _) = await SendRawAsync(
            "POST /m/api/pin HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n0\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 411", statusLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transport_RejectsAbsentRequestLine()
    {
        (string statusLine, _) = await SendRawAsync("\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 400", statusLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transport_RejectsOversizedHeaderBlock()
    {
        string padding = new string('a', 9000);
        (string statusLine, _) = await SendRawAsync($"GET /m/api/health HTTP/1.1\r\nX-Pad: {padding}\r\n\r\n");

        // The server stops reading at its header cap, so it answers 431 before the client has finished
        // writing. Either the 431 arrives first or the socket is reset mid-write; both are rejections,
        // and anything else (a hang, or a 200) is not.
        Assert.True(
            statusLine.StartsWith("HTTP/1.1 431", StringComparison.Ordinal) || statusLine == "(reset)",
            $"Unexpected status line: '{statusLine}'");
    }

    [Fact]
    public async Task Transport_RejectsAbsoluteFormRequestTarget()
    {
        (string statusLine, _) = await SendRawAsync(
            "GET http://example.com/m/api/health HTTP/1.1\r\nHost: x\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 400", statusLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transport_OtherMethodsOnKnownRoutesReturn404()
    {
        using var request = Authorized(HttpMethod.Delete, "/m/api/pin");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<(string StatusLine, string Body)> SendRawAsync(string requestText)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _bridge.Port);

        using NetworkStream stream = tcp.GetStream();
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(requestText));
            await stream.FlushAsync();
        }
        catch (IOException)
        {
            // A server that rejects a request while the client is still writing resets the socket.
            // That is a legitimate answer; the read below records whatever arrived.
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var received = new MemoryStream();
        byte[] buffer = new byte[4096];

        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(), cts.Token);
                if (read <= 0)
                {
                    break;
                }

                received.Write(buffer, 0, read);
            }
        }
        catch (OperationCanceledException)
        {
            // A response that never terminates is itself a failure; the asserts below catch it.
        }
        catch (IOException)
        {
            // Same reset case as above. Callers assert on the status line, so an unexpected reset
            // still fails them instead of being silently tolerated.
            if (received.Length == 0)
            {
                return ("(reset)", string.Empty);
            }
        }

        string text = Encoding.UTF8.GetString(received.ToArray());
        int firstBreak = text.IndexOf("\r\n", StringComparison.Ordinal);
        string statusLine = firstBreak > 0 ? text[..firstBreak] : text;
        int bodyStart = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        string body = bodyStart >= 0 ? text[(bodyStart + 4)..] : string.Empty;

        return (statusLine, body);
    }

    // ── Shared validation contract with the browser-extension companion ──────

    [Fact]
    public void SharedValidator_RejectsEverythingTheCompanionServiceRejects()
    {
        CompanionPinValidationResult missing = CompanionPinValidator.Validate(null);
        Assert.Null(missing.Request);
        Assert.Equal(400, missing.StatusCode);

        CompanionPinValidationResult badSpan = CompanionPinValidator.Validate(
            new CompanionPinRequest(Url: "https://example.com", Title: null, Note: null,
                WorkspaceId: null, SpanX: 3, SpanY: 3, ThumbnailUrl: null));
        Assert.Null(badSpan.Request);
        Assert.Equal(400, badSpan.StatusCode);

        CompanionPinValidationResult badThumb = CompanionPinValidator.Validate(
            new CompanionPinRequest(Url: "https://example.com", Title: null, Note: null,
                WorkspaceId: null, SpanX: null, SpanY: null, ThumbnailUrl: "file:///C:/x.png"));
        Assert.Null(badThumb.Request);
        Assert.Equal(400, badThumb.StatusCode);

        CompanionPinValidationResult ok = CompanionPinValidator.Validate(
            new CompanionPinRequest(Url: "https://example.com/", Title: "  Title  ", Note: null,
                WorkspaceId: null, SpanX: null, SpanY: null, ThumbnailUrl: null));
        Assert.NotNull(ok.Request);
        Assert.Equal(200, ok.StatusCode);
        Assert.Equal("Title", ok.Request!.Title);
        Assert.Equal(2, ok.Request.SpanX);
    }

    // ── Widget ───────────────────────────────────────────────────────────────

    [Fact]
    public void PhoneBridgeWidget_IsRegisteredWithViewMetadata()
    {
        var definition = WidgetRegistry.Get("phone_bridge");

        Assert.NotNull(definition);
        Assert.Equal("Phone Bridge", definition!.DisplayName);
        Assert.Equal(typeof(PhoneBridgeWidgetViewModel), definition.ViewModelType);
        Assert.Equal(typeof(PhoneBridgeWidgetView), definition.ViewType);
        Assert.Equal(WidgetSize.SquareLarge, definition.DefaultSize);
        Assert.Equal("Productivity", definition.Category);

        // Exactly one size: the pairing surface is designed for 6x6 and nothing else.
        Assert.Equal(new[] { WidgetSize.SquareLarge }, definition.AllowedSizes);

        var tile = new TileModel
        {
            Id = "phone_bridge_sizes",
            TileType = TileType.Widget,
            TargetPath = "phone_bridge"
        };

        var vm = new PhoneBridgeWidgetViewModel(tile);
        try
        {
            Assert.Equal(new[] { WidgetSize.SquareLarge }, vm.AllowedSizes);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void PhoneBridgeWidget_TracksTheBridgeStateAndOffersMenuItems()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = new TileModel
            {
                Id = "phone_bridge_tile",
                TileType = TileType.Widget,
                TargetPath = "phone_bridge",
                SpanX = 4,
                SpanY = 4
            };

            var vm = new PhoneBridgeWidgetViewModel(tile);
            try
            {
                Assert.Equal(MobileBridgeService.Instance.IsRunning, vm.IsRunning);
                Assert.Equal(!vm.IsRunning, vm.IsOffline);

                List<System.Windows.Controls.Control> items = vm.GetContextMenuItems().ToList();
                Assert.Equal(6, items.Count);
                Assert.All(items, item => Assert.IsType<System.Windows.Controls.MenuItem>(item));

                // The clipboard push entry is offered next to the pairing actions.
                Assert.Contains(
                    items.Cast<System.Windows.Controls.MenuItem>(),
                    item => (item.Header as string) == "Send clipboard to phone");

                // Forgetting pairings is only meaningful while phones are paired.
                System.Windows.Controls.MenuItem forget = items
                    .Cast<System.Windows.Controls.MenuItem>()
                    .Single(item => (item.Header as string)?.StartsWith("Forget", StringComparison.Ordinal) == true);
                Assert.False(forget.IsEnabled);

                string statusWhenIdle = vm.StatusText;
                Assert.False(string.IsNullOrWhiteSpace(statusWhenIdle));

                // The pairing caption only makes sense while the bridge is running.
                if (vm.IsRunning)
                {
                    Assert.False(string.IsNullOrWhiteSpace(vm.PairedText));
                }
                else
                {
                    Assert.Equal(string.Empty, vm.PairedText);
                }
            }
            finally
            {
                vm.Dispose();
                vm.Dispose();
            }
        });
    }

    [Fact]
    public void PhoneBridgeWidget_RestoresPersistedSettings()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = new TileModel
            {
                Id = "phone_bridge_persisted",
                TileType = TileType.Widget,
                TargetPath = "phone_bridge",
                SpanX = 8,
                SpanY = 4,
                SettingsJson = WidgetSerializer.Serialize(new PhoneBridgeWidgetSettings
                {
                    ShowQrCode = false,
                    ShowAddress = false,
                    LastPort = 49000
                })
            };

            var vm = new PhoneBridgeWidgetViewModel(tile);
            try
            {
                Assert.False(vm.ShowQrCode);
                Assert.False(vm.ShowAddress);
                Assert.Null(vm.QrImage);

                vm.SaveSettings();
                PhoneBridgeWidgetSettings? roundTripped =
                    WidgetSerializer.Deserialize<PhoneBridgeWidgetSettings>(tile.SettingsJson);

                Assert.NotNull(roundTripped);
                Assert.False(roundTripped!.ShowQrCode);
                Assert.False(roundTripped.ShowAddress);
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    [Fact]
    public void PhoneBridgeSettings_RoundTripThroughTheSourceGeneratedContext()
    {
        string json = WidgetSerializer.Serialize(new PhoneBridgeWidgetSettings
        {
            ShowQrCode = false,
            ShowAddress = true,
            LastPort = 48860
        });

        PhoneBridgeWidgetSettings? restored = WidgetSerializer.Deserialize<PhoneBridgeWidgetSettings>(json);

        Assert.NotNull(restored);
        Assert.False(restored!.ShowQrCode);
        Assert.True(restored.ShowAddress);
        Assert.Equal(48860, restored.LastPort);
    }

    // ── Desktop to phone push ─────────────────────────────────────────────────

    [Theory]
    [InlineData(true, true, false, nameof(PhonePushKind.Link))]
    [InlineData(true, false, true, nameof(PhonePushKind.NoteFile))]
    [InlineData(false, true, false, nameof(PhonePushKind.None))]
    [InlineData(false, false, true, nameof(PhonePushKind.None))]
    [InlineData(true, false, false, nameof(PhonePushKind.None))]
    public void PushComposer_ClassifiesTilesByPayload(bool running, bool isWebLink, bool isNote, string expected)
    {
        Assert.Equal(expected, MobileBridgePushComposer.ClassifyTile(running, isWebLink, isNote).ToString());
    }

    [Theory]
    [InlineData("https://example.com/a?b=1", true)]
    [InlineData("  http://192.168.1.5:48850/m/  ", true)]
    [InlineData("ftp://example.com/a", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://example.com/a and more", false)]
    [InlineData("https://example.com/a\nsecond line", false)]
    [InlineData("", false)]
    [InlineData("just a note", false)]
    public void PushComposer_RecognisesOnlySingleLineHttpUrls(string text, bool expected)
    {
        Assert.Equal(expected, MobileBridgePushComposer.IsHttpUrl(text));
    }

    [Fact]
    public void PushComposer_ClipboardUrlBecomesALink()
    {
        (string? url, string? note) = MobileBridgePushComposer.DescribeClipboardPush("https://example.com/read\n");

        Assert.Equal("https://example.com/read", url);
        Assert.Null(note);
    }

    [Fact]
    public void PushComposer_ClipboardTextBecomesANoteCappedToThePinLimit()
    {
        string longText = new('x', CompanionPinValidator.MaxNoteLength + 500);

        (string? url, string? note) = MobileBridgePushComposer.DescribeClipboardPush(longText);

        Assert.Null(url);
        Assert.NotNull(note);
        Assert.Equal(CompanionPinValidator.MaxNoteLength, note!.Length);
    }

    [Fact]
    public void PushComposer_EmptyClipboardProducesNothing()
    {
        (string? url, string? note) = MobileBridgePushComposer.DescribeClipboardPush("   \r\n  ");

        Assert.Null(url);
        Assert.Null(note);
    }

    [Fact]
    public void PublishToPhone_RecordsAQueuedItemEvenWithoutSubscribers()
    {
        int delivered = _bridge.PublishToPhone("Read later", "https://example.com/article", null);

        Assert.Equal(0, delivered);
        MobileBridgeRecentItemDto? queued = _bridge.RecentItems.FirstOrDefault(r => r.Direction == "toPhone");
        Assert.NotNull(queued);
        Assert.Equal("link", queued!.Kind);
        Assert.Equal("https://example.com/article", queued.Url);
    }

    [Fact]
    public void PublishToPhone_RefusesWhenTheSettingIsOff()
    {
        _bridge.AllowPushToPhone = false;
        try
        {
            Assert.Equal(0, _bridge.PublishToPhone("Read later", "https://example.com/article", null));
            Assert.DoesNotContain(_bridge.RecentItems, r => r.Direction == "toPhone");
        }
        finally
        {
            _bridge.AllowPushToPhone = true;
        }
    }

    [Fact]
    public async Task Context_ReportsThePushSettingInsteadOfHardcodingIt()
    {
        _bridge.AllowPushToPhone = false;
        try
        {
            using var request = Authorized(HttpMethod.Get, "/m/api/context");
            using var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.False(document.RootElement.GetProperty("canPushToPhone").GetBoolean());
        }
        finally
        {
            _bridge.AllowPushToPhone = true;
        }
    }

    // ── Staying paired, HTTPS, and the local CA ───────────────────────────────

    [Fact]
    public void PairedPhoneStore_KeepsPairingsAcrossReloads()
    {
        string path = Path.Combine(_sandbox, "paired-store.json");

        var store = PairedPhoneStore.Load(path);
        Assert.Equal(0, store.Count);

        (PairedPhone device, string token) = store.Pair("Android phone");
        Assert.Equal(1, store.Count);
        Assert.Equal("Android phone", device.Label);
        Assert.Equal(32, token.Length);

        // A fresh instance sees the same phone: this is what survives a MetroHub restart.
        var reloaded = PairedPhoneStore.Load(path);
        Assert.Equal(1, reloaded.Count);
        Assert.True(reloaded.TryAuthorize(token, out PairedPhone restored));
        Assert.Equal(device.Id, restored.Id);

        Assert.False(reloaded.TryAuthorize(MobileBridgeToken.Create().Value, out _));
        Assert.False(reloaded.TryAuthorize(null, out _));
        Assert.False(reloaded.TryAuthorize(string.Empty, out _));

        // Only the hash reaches the disk; the token itself stays in memory.
        Assert.DoesNotContain(token, File.ReadAllText(path), StringComparison.Ordinal);

        Assert.True(reloaded.Forget(device.Id));
        Assert.Equal(0, reloaded.Count);
        Assert.False(reloaded.TryAuthorize(token, out _));
    }

    [Fact]
    public void PairedPhoneStore_CapsDevicesAndUnpairsByToken()
    {
        var store = PairedPhoneStore.Load(Path.Combine(_sandbox, "paired-cap.json"));

        var tokens = new List<string>();
        for (int i = 0; i < PairedPhoneStore.MaxDevices + 3; i++)
        {
            tokens.Add(store.Pair($"Phone {i}").Token);
        }

        Assert.Equal(PairedPhoneStore.MaxDevices, store.Count);

        // The oldest pairings were evicted first, so only the newest survive.
        Assert.False(store.TryAuthorize(tokens[0], out _));
        Assert.True(store.TryAuthorize(tokens[^1], out _));

        // Unpairing by token removes exactly one phone.
        Assert.True(store.TryForgetByToken(tokens[^1]));
        Assert.Equal(PairedPhoneStore.MaxDevices - 1, store.Count);
        Assert.False(store.TryAuthorize(tokens[^1], out _));
    }

    [Fact]
    public async Task Pairing_IssuesADurableDeviceTokenThatOutlivesTheSessionToken()
    {
        // The first navigation from the QR code exchanges the rotating session token for a device
        // token, and that exchange is what stops the phone from having to scan again.
        using (var shellRequest = new HttpRequestMessage(HttpMethod.Get, $"/m/?t={Token}"))
        {
            shellRequest.Headers.Add("User-Agent", "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36");

            using HttpResponseMessage shell = await _client.SendAsync(shellRequest);
            Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        }

        Assert.Equal(1, _bridge.PairedPhoneCount);
        Assert.Equal("Android phone", _bridge.PairedPhones[0].Label);

        // The phone's copy of the token is the cookie the server just handed it.
        Cookie? pairing = _handler.CookieContainer
            .GetCookies(_client.BaseAddress!)[MobileBridgeEndpoints.TokenCookieName];

        Assert.NotNull(pairing);
        string deviceToken = pairing!.Value;
        Assert.NotEqual(Token, deviceToken);

        // A second bridge instance is a second app run: new session token, same pairing file.
        using var restarted = new MobileBridgeService(_pairedPhonesPath);
        try
        {
            MobileBridgeStartResult start = await restarted.StartOnAsync(IPAddress.Loopback, 0)
                .ConfigureAwait(false);
            Assert.True(start.Started, start.Message);
            Assert.NotEqual(Token, restarted.TokenValue);

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{restarted.Port}") };
            using var request = new HttpRequestMessage(HttpMethod.Get, "/m/api/context");
            request.Headers.Add(MobileBridgeEndpoints.TokenHeaderName, deviceToken);
            using HttpResponseMessage response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("Android phone", document.RootElement.GetProperty("pairedAs").GetString());
        }
        finally
        {
            await restarted.StopAsync().ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task Unpair_ForgetsTheCallingPhoneAndClearsTheCookie()
    {
        using (HttpResponseMessage shell = await _client.GetAsync($"/m/?t={Token}"))
        {
            Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        }

        Assert.Equal(1, _bridge.PairedPhoneCount);

        // The cookie jar carries the device token; the header still carries the session token.
        using var request = Authorized(HttpMethod.Post, "/m/api/unpair");
        using HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("success").GetBoolean());

        Assert.Equal(0, _bridge.PairedPhoneCount);

        string? setCookie = response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values)
            ? values.FirstOrDefault()
            : null;
        Assert.NotNull(setCookie);
        Assert.Contains("mh_token=;", setCookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Endpoints_EmitCorsAndPnaHeaders()
    {
        using var response = await _client.GetAsync("/m/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins));
        Assert.Contains("*", origins);
        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Private-Network", out var pna));
        Assert.Contains("true", pna);
    }

    [Fact]
    public async Task Endpoints_OptionsPreflight_Returns204WithAllowedHeaders()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/m/api/upload");
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins));
        Assert.Contains("*", origins);
        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Private-Network", out var pna));
        Assert.Contains("true", pna);
        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Headers", out var headers));
        Assert.Contains("Content-Type, X-MetroHub-Token", headers.FirstOrDefault() ?? string.Empty);
    }

    [Fact]
    public async Task Context_WithSessionToken_IssuesAndReturnsDeviceToken()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/m/api/context");
        request.Headers.Add(MobileBridgeEndpoints.TokenHeaderName, Token);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.TryGetProperty("deviceToken", out var deviceTokenProp));
        string? deviceToken = deviceTokenProp.GetString();
        Assert.False(string.IsNullOrEmpty(deviceToken));
        Assert.NotEqual(Token, deviceToken);
    }

}
#pragma warning restore CA1001
