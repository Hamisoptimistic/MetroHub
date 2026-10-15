using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Common;

namespace MetroHub.Core.Services.MobileBridge;

/// <summary>Outcome of a bridge start attempt, including the reason when it fails.</summary>
public sealed record MobileBridgeStartResult(bool Started, string? Message);

/// <summary>What credential authorized a request.</summary>
public enum MobileBridgeCredential
{
    /// <summary>Nothing valid was presented.</summary>
    None,

    /// <summary>The rotating session token from the desktop's pairing QR code or link.</summary>
    Session,

    /// <summary>A durable device token minted when a phone paired and kept across restarts.</summary>
    Device
}

/// <summary>Result of authorizing a request, including which phone it belongs to.</summary>
public sealed record MobileBridgeAuthorization(MobileBridgeCredential Credential, PairedPhone? Device)
{
    public static MobileBridgeAuthorization Denied { get; } = new(MobileBridgeCredential.None, null);

    public bool IsAuthorized => Credential != MobileBridgeCredential.None;

    /// <summary>True when the request used the QR session token and should be upgraded to a device token.</summary>
    public bool IsSession => Credential == MobileBridgeCredential.Session;
}


/// <summary>
/// Owns the phone LAN bridge: LAN address discovery, port selection, the session token,
/// the raw HTTP listener, and the server-sent-events hub.
/// </summary>
/// <remarks>
/// Opt-in only. Nothing binds until <see cref="StartAsync"/> is called, the listener is bound to the
/// private LAN address only (never a wildcard), and the per-session token rotates on every start.
/// </remarks>
public sealed class MobileBridgeService : IDisposable
{
    public const int DefaultPort = 48850;

    private const int PortProbeCount = 5;
    private const int MaxRecentItems = 24;

    private static readonly Lazy<MobileBridgeService> InstanceFactory =
        new(() => new MobileBridgeService());

    private readonly object _lifecycleLock = new();
    private readonly object _recentLock = new();
    private readonly List<MobileBridgeRecentItemDto> _recent = new(MaxRecentItems);
    private readonly PairedPhoneStore _pairedPhones;

    private MobileHttpServer? _server;
    private MobileBridgeToken? _token;
    private string? _lanAddress;
    private int _port;
    private bool _disposed;

    public MobileBridgeService()
        : this(AppPaths.PairedPhonesPath)
    {
    }

    /// <summary>Test seam: keeps pairings out of the shared application data folder.</summary>
    internal MobileBridgeService(string pairedPhonesPath)
    {
        _pairedPhones = PairedPhoneStore.Load(pairedPhonesPath);
    }

    /// <summary>Compatibility constructor for tests.</summary>
    internal MobileBridgeService(string pairedPhonesPath, string? certificateDirectory)
        : this(pairedPhonesPath)
    {
    }

    public static MobileBridgeService Instance => InstanceFactory.Value;

    /// <summary>Fan-out hub for PC &#8594; phone pushes. Exposed so the widget can report subscriber counts.</summary>
    public MobileBridgeSseHub Hub { get; } = new();

    public bool IsRunning
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _server?.IsRunning == true;
            }
        }
    }

    /// <summary>Advertised LAN IPv4 address, or null while stopped.</summary>
    public string? LanAddress
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _lanAddress;
            }
        }
    }

    public int Port
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _port;
            }
        }
    }

    public string DeviceName => Environment.MachineName;

    public int PhoneClientCount => Hub.ClientCount;

    /// <summary>Phones that paired earlier and may reconnect without scanning a new QR code.</summary>
    public IReadOnlyList<PairedPhone> PairedPhones => _pairedPhones.Devices;

    public int PairedPhoneCount => _pairedPhones.Count;

    /// <summary>Hosted PWA companion URL on HTTPS.</summary>
    public string CompanionPwaUrl { get; set; } = "https://hamisoptimistic.github.io/MetroHub/phone/";

    /// <summary>Compatibility property.</summary>
    public int HttpsPort => 0;

    /// <summary>Compatibility property.</summary>
    public bool IsHttpsAvailable => false;

    /// <summary>Compatibility property.</summary>
    public string? SecureUrl => null;

    /// <summary>Compatibility property.</summary>
    public byte[]? CertificateAuthorityDer => null;

    /// <summary>Compatibility property.</summary>
    public string CertificateAuthorityThumbprint => string.Empty;

    /// <summary>
    /// Pairing URL embedded in the QR code. Points to the hosted PWA shell with local IP and token in the fragment.
    /// The fragment is never transmitted to the hosted server; it is consumed entirely on-device by the phone PWA.
    /// </summary>
    public string? PairingUrl
    {
        get
        {
            lock (_lifecycleLock)
            {
                if (_token == null || string.IsNullOrEmpty(_lanAddress) || _port == 0)
                {
                    return null;
                }

                if (!string.IsNullOrWhiteSpace(CompanionPwaUrl))
                {
                    string baseUrl = CompanionPwaUrl.TrimEnd('/');
                    return $"{baseUrl}/#host={_lanAddress}:{_port}&t={_token.Value}";
                }

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "http://{0}:{1}/m/?t={2}",
                    _lanAddress,
                    _port,
                    _token.Value);
            }
        }
    }

    /// <summary>Direct local HTTP pairing URL.</summary>
    public string? LocalPairingUrl
    {
        get
        {
            lock (_lifecycleLock)
            {
                if (_token == null || string.IsNullOrEmpty(_lanAddress) || _port == 0)
                {
                    return null;
                }

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "http://{0}:{1}/m/?t={2}",
                    _lanAddress,
                    _port,
                    _token.Value);
            }
        }
    }

    public string TokenValue
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _token?.Value ?? string.Empty;
            }
        }
    }

    /// <summary>Raised whenever the bridge starts or stops. Handlers run on the calling thread.</summary>
    public event Action? StatusChanged;

    /// <summary>
    /// Port requested on the next start (the bridge probes upward if it is taken).
    /// Seeded from AppSettings by the shell so the widget and the boot path agree.
    /// </summary>
    public int PreferredPort { get; set; } = DefaultPort;

    /// <summary>
    /// Optional override for tile placement. When unset the bridge reuses the browser-extension
    /// companion handler, so there is a single wiring point for both ingress surfaces.
    /// </summary>
    public Func<CompanionPinRequest, CancellationToken, Task<CompanionPinResult>>? PinTileHandler { get; set; }

    /// <summary>Optional override for placing an already-spooled local image file as a tile.</summary>
    public Func<string, string?, string?, int, int, CancellationToken, Task<CompanionPinResult>>? LocalImagePinHandler { get; set; }

    /// <summary>Optional override for workspace discovery.</summary>
    public Func<IReadOnlyList<CompanionWorkspaceDto>>? WorkspacesHandler { get; set; }

    /// <summary>
    /// Whether the desktop may push items to connected phones. Seeded from AppSettings by the shell;
    /// there is no UI toggle yet, so the default stays on.
    /// </summary>
    public bool AllowPushToPhone { get; set; } = true;

    /// <summary>Immutable snapshot of the recent in-memory activity log, newest first.</summary>
    public IReadOnlyList<MobileBridgeRecentItemDto> RecentItems
    {
        get
        {
            lock (_recentLock)
            {
                return _recent.ToArray();
            }
        }
    }

    /// <summary>
    /// Constant-time check of the rotating session token only. Returns false when the bridge is
    /// stopped or no token exists. Use <see cref="Authorize"/> for the full check that also accepts
    /// paired phones.
    /// </summary>
    public bool IsAuthorized(string? candidate)
    {
        MobileBridgeToken? token;
        lock (_lifecycleLock)
        {
            token = _token;
        }

        return token != null && token.Matches(candidate);
    }

    /// <summary>
    /// Full credential check: the session token from the QR code, or the durable token of a phone
    /// that paired earlier.
    /// </summary>
    public MobileBridgeAuthorization Authorize(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return MobileBridgeAuthorization.Denied;
        }

        if (IsAuthorized(candidate))
        {
            return new MobileBridgeAuthorization(MobileBridgeCredential.Session, null);
        }

        return _pairedPhones.TryAuthorize(candidate, out PairedPhone device)
            ? new MobileBridgeAuthorization(MobileBridgeCredential.Device, device)
            : MobileBridgeAuthorization.Denied;
    }

    /// <summary>
    /// Mints a durable device token for a phone that presented the session token, and returns it so
    /// the caller can hand it to that phone exactly once.
    /// </summary>
    public (PairedPhone Device, string Token) PairDevice(string? label)
    {
        (PairedPhone device, string token) = _pairedPhones.Pair(label);

        Serilog.Log.Information(
            "[MobileBridge] Paired a phone as {Label} ({Count} paired)",
            device.Label,
            _pairedPhones.Count);

        RaiseStatusChanged();
        return (device, token);
    }

    /// <summary>Forgets one paired phone by id.</summary>
    public bool ForgetPairedPhone(string? deviceId)
    {
        bool removed = _pairedPhones.Forget(deviceId);
        if (removed)
        {
            RaiseStatusChanged();
        }

        return removed;
    }

    /// <summary>Removes the pairing that owns <paramref name="token"/> (the phone unpairing itself).</summary>
    public bool UnpairDevice(string? token)
    {
        bool removed = _pairedPhones.TryForgetByToken(token);
        if (removed)
        {
            Serilog.Log.Information("[MobileBridge] A phone unpaired itself ({Count} left)", _pairedPhones.Count);
            RaiseStatusChanged();
        }

        return removed;
    }

    /// <summary>Forgets every paired phone. Returns how many pairings were dropped.</summary>
    public int ForgetPairedPhones()
    {
        int removed = _pairedPhones.ForgetAll();
        if (removed > 0)
        {
            Serilog.Log.Information("[MobileBridge] Forgot {Count} paired phone(s)", removed);
            RaiseStatusChanged();
        }

        return removed;
    }

    /// <summary>
    /// Starts the bridge on the machine's private LAN address. Fails closed when the machine has no
    /// usable private IPv4 address, so nothing is ever exposed on a wildcard or public interface.
    /// </summary>
    public Task<MobileBridgeStartResult> StartAsync(int preferredPort = DefaultPort, CancellationToken ct = default)
    {
        if (!LanAddressResolver.TryResolve(out LanEndpoint lan))
        {
            Serilog.Log.Warning("[MobileBridge] No reachable private LAN address was found.");
            return Task.FromResult(new MobileBridgeStartResult(
                false,
                "No LAN address found. Connect this PC to Wi-Fi or Ethernet and try again."));
        }

        if (!IPAddress.TryParse(lan.IpAddress, out IPAddress? bindAddress))
        {
            return Task.FromResult(new MobileBridgeStartResult(false, "The resolved LAN address was not usable."));
        }

        return StartCoreAsync(bindAddress, lan.IpAddress, preferredPort, ct);
    }

    /// <summary>
    /// Starts the bridge on an explicit address. Production always calls <see cref="StartAsync"/>;
    /// this overload exists so the transport and routing can be exercised over loopback.
    /// </summary>
    public Task<MobileBridgeStartResult> StartOnAsync(IPAddress bindAddress, int port, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bindAddress);
        return StartCoreAsync(bindAddress, bindAddress.ToString(), port, ct);
    }

    private Task<MobileBridgeStartResult> StartCoreAsync(
        IPAddress bindAddress,
        string lanAddress,
        int preferredPort,
        CancellationToken ct)
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return Task.FromResult(new MobileBridgeStartResult(false, "The bridge has been disposed."));
            }

            if (_server?.IsRunning == true)
            {
                return Task.FromResult(new MobileBridgeStartResult(true, null));
            }

            var token = MobileBridgeToken.Create();
            var endpointHandler = new MobileBridgeEndpoints(this);

            (MobileHttpServer? server, string? lastError) = TryBind(
                bindAddress,
                preferredPort,
                endpointHandler);

            if (server == null)
            {
                return Task.FromResult(new MobileBridgeStartResult(
                    false,
                    $"Could not open a port for the bridge. {lastError}"));
            }

            _server = server;
            _token = token;
            _lanAddress = lanAddress;
            _port = server.ActivePort;

            Serilog.Log.Information(
                "[MobileBridge] Bridge started on port {HttpPort} ({Paired} paired phones)",
                _port,
                _pairedPhones.Count);
        }

        RaiseStatusChanged();
        return Task.FromResult(new MobileBridgeStartResult(true, null));
    }

    /// <summary>
    /// Binds the first free port in the probe window. Returns the running server, or null with the
    /// last bind error for the caller's message.
    /// </summary>
    private static (MobileHttpServer? Server, string? Error) TryBind(
        IPAddress bindAddress,
        int preferredPort,
        MobileBridgeEndpoints handler)
    {
        int startPort = preferredPort > 0 ? preferredPort : 0;
        int attempts = startPort == 0 ? 1 : PortProbeCount;
        string? lastError = null;

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            int candidatePort = startPort + attempt;
            var candidate = new MobileHttpServer(bindAddress, candidatePort, handler.HandleAsync);

            try
            {
                candidate.Start();
                return (candidate, null);
            }
            catch (SocketException ex)
            {
                lastError = ex.Message;
                candidate.Dispose();
                Serilog.Log.Warning(
                    "[MobileBridge] Port {Port} unavailable: {Message}",
                    candidatePort,
                    ex.Message);
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                candidate.Dispose();
                Safe.Log(ex, $"[MobileBridge] Failed to bind port {candidatePort}");
            }
        }

        return (null, lastError);
    }

    public async Task StopAsync()
    {
        MobileHttpServer? server;

        lock (_lifecycleLock)
        {
            server = _server;
            _server = null;
            _token = null;
            _lanAddress = null;
            _port = 0;
        }

        Hub.DisconnectAll();

        if (server != null)
        {
            await server.StopAsync().ConfigureAwait(false);
            server.Dispose();
        }

        lock (_recentLock)
        {
            _recent.Clear();
        }

        Serilog.Log.Information("[MobileBridge] Bridge stopped.");
        RaiseStatusChanged();
    }

    /// <summary>Places a link or note tile through the shared companion pipeline.</summary>
    public Task<CompanionPinResult> PinAsync(CompanionPinRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        Func<CompanionPinRequest, CancellationToken, Task<CompanionPinResult>>? handler =
            PinTileHandler ?? LocalCompanionService.Instance.PinTileHandler;

        if (handler != null)
        {
            return handler(request, ct);
        }

        return Task.FromResult(new CompanionPinResult(
            Success: true,
            Duplicate: false,
            TileId: Guid.NewGuid().ToString("N"),
            Col: 0,
            Row: 0,
            Message: "Bridge ran without a desktop handler."));
    }

    /// <summary>Places a local image file (already spooled to disk) as a tile.</summary>
    public Task<CompanionPinResult> PinLocalImageAsync(
        string filePath,
        string? title,
        string? workspaceId,
        int spanX,
        int spanY,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return Task.FromResult(new CompanionPinResult(false, false, null, 0, 0, "Missing image path."));
        }

        Func<string, string?, string?, int, int, CancellationToken, Task<CompanionPinResult>>? handler =
            LocalImagePinHandler;

        if (handler != null)
        {
            return handler(filePath, title, workspaceId, spanX, spanY, ct);
        }

        return Task.FromResult(new CompanionPinResult(
            false,
            false,
            null,
            0,
            0,
            "Desktop tile placement is unavailable."));
    }

    public IReadOnlyList<CompanionWorkspaceDto> GetWorkspaces()
    {
        if (WorkspacesHandler != null)
        {
            return WorkspacesHandler();
        }

        if (LocalCompanionService.Instance.WorkspacesHandler != null)
        {
            return LocalCompanionService.Instance.WorkspacesHandler();
        }

        return new List<CompanionWorkspaceDto>
        {
            new("default", "Default Workspace", true)
        };
    }

    /// <summary>
    /// Pushes an item to every subscribed phone and records it in the activity log.
    /// Returns the number of live subscribers that received it, or 0 when push is disabled.
    /// </summary>
    public int PublishToPhone(string? title, string? url, string? note)
    {
        if (!AllowPushToPhone)
        {
            return 0;
        }

        string resolvedTitle;
        if (!string.IsNullOrWhiteSpace(title))
        {
            resolvedTitle = title;
        }
        else if (!string.IsNullOrWhiteSpace(url))
        {
            resolvedTitle = url;
        }
        else if (!string.IsNullOrWhiteSpace(note))
        {
            resolvedTitle = note.Length > 64 ? note[..64] : note;
        }
        else
        {
            resolvedTitle = "Item from PC";
        }

        string kind = !string.IsNullOrWhiteSpace(note)
            ? "note"
            : (!string.IsNullOrWhiteSpace(url) ? "link" : "text");

        var payload = new MobileBridgePushEventDto(
            Id: Guid.NewGuid().ToString("N"),
            Kind: kind,
            Title: resolvedTitle,
            Url: string.IsNullOrWhiteSpace(url) ? null : url,
            Note: string.IsNullOrWhiteSpace(note) ? null : note,
            DeviceName: DeviceName,
            SentAtUtc: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        string json = JsonSerializer.Serialize(
            payload,
            MobileBridgeJsonContext.Default.MobileBridgePushEventDto);

        int delivered = Hub.Publish("push", json);

        RecordRecent(new MobileBridgeRecentItemDto(
            Id: payload.Id,
            Direction: "toPhone",
            Kind: kind,
            Title: resolvedTitle,
            Url: payload.Url,
            Note: payload.Note,
            SentAtUtc: payload.SentAtUtc,
            Duplicate: false));

        return delivered;
    }

    /// <summary>Appends to the bounded in-memory activity log. Never persisted.</summary>
    public void RecordRecent(MobileBridgeRecentItemDto item)
    {
        if (item == null)
        {
            return;
        }

        lock (_recentLock)
        {
            _recent.Insert(0, item);
            if (_recent.Count > MaxRecentItems)
            {
                _recent.RemoveRange(MaxRecentItems, _recent.Count - MaxRecentItems);
            }
        }
    }

    private void RaiseStatusChanged()
    {
        try
        {
            StatusChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "[MobileBridge] StatusChanged handler failed");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopAsync().GetAwaiter().GetResult();
        Hub.Dispose();
    }
}
