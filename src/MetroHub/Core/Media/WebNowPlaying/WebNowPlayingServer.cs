using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MetroHub.Core.Services;

namespace MetroHub.Core.Media.WebNowPlaying;

/// <summary>
/// Opt-in WebNowPlaying adapter: hosts the WebSocket that the WebNowPlaying browser extension
/// connects to, tracks the players the extension reports, and exposes the one currently active so
/// the media widget can display and control browser media that Windows SMTC cannot see.
/// </summary>
/// <remarks>
/// <para>
/// Implements the official WebNowPlaying adapter protocol (communication revision 3). Enable it in
/// the widget's context menu, then add a custom adapter in the extension's settings:
/// host <c>127.0.0.1</c>, port <see cref="DefaultPort"/>.
/// </para>
/// <para>
/// Everything runs on background threads; subscribers must marshal to the UI thread themselves.
/// The server never throws to its caller: bind failures log and leave the adapter stopped.
/// </para>
/// </remarks>
public sealed class WebNowPlayingServer : IDisposable
{
    /// <summary>Loopback port the extension dials into by default (8974 = Rainmeter adapter).</summary>
    public const int DefaultPort = 8974;

    /// <summary>Secondary fallback port if 8974 is already bound by another app (e.g. Rainmeter).</summary>
    public const int FallbackPort = 8642;

    /// <summary>Event ids cycle in this range, matching the official library's 512-slot result table.</summary>
    public const int MaxEventId = 512;

    private readonly int _requestedPort;
    private int _actualPort;
    private readonly object _gate = new();
    private readonly Dictionary<int, WnpPlayer> _players = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private WnpWebSocket? _socket;
    private WnpPlayerSnapshot? _activeSnapshot;

    /// <summary>Full raw frames logged for the current connection (wire-format verification).</summary>
    private int _loggedRawFrames;

    /// <summary>
    /// Cycles 1..511 like the official library (<c>(id + 1) % 512</c>): the extension
    /// tracks results in a 512-slot table, so unbounded ids stop getting answers.
    /// </summary>
    private int _nextEventId = 1;
    private bool _disposed;

    /// <summary>Pending command results keyed by event id (mirrors wnp_wait_for_event_result).</summary>
    private readonly Dictionary<int, TaskCompletionSource<int>> _pendingResults = new();

    /// <summary>Answered commands kept briefly so a waiter that arrives after the answer still sees it.</summary>
    private readonly Dictionary<int, int> _completedResults = new();

    public WebNowPlayingServer(int port = DefaultPort)
    {
        _requestedPort = port;
        _actualPort = port;
    }

    /// <summary>The port currently bound by the listener (0 when stopped).</summary>
    public int BoundPort
    {
        get { lock (_gate) return _listener != null ? _actualPort : 0; }
    }

    /// <summary>
    /// Raised (on a background thread) whenever the active player's data or identity changes, and
    /// with <c>null</c> when the last player disappears or the extension disconnects.
    /// </summary>
    public event Action<WnpPlayerSnapshot?>? ActivePlayerChanged;

    /// <summary>Raised when the extension delivers a cover image: PNG bytes for the given player id.</summary>
    public event Action<int, byte[]>? CoverReceived;

    /// <summary>Raised when the extension connects or disconnects. Check <see cref="IsConnected"/>.</summary>
    public event Action? ConnectionChanged;

    /// <summary>
    /// Raised when the extension answers a command: event id + result (0 = pending, 1 = succeeded, 2 = failed).
    /// </summary>
    public event Action<int, int>? EventResultReceived;

    /// <summary>True while the listener is bound.</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _listener != null; }
    }

    /// <summary>True while the extension is connected.</summary>
    public bool IsConnected
    {
        get { lock (_gate) return _socket != null; }
    }

    /// <summary>Latest active-player snapshot (null when no browser player is reporting).</summary>
    public WnpPlayerSnapshot? ActivePlayer
    {
        get { lock (_gate) return _activeSnapshot; }
    }

    /// <summary>Binds the loopback port and starts accepting connections. Returns false on bind failure.</summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (_disposed || _listener != null) return !_disposed;

            int[] portsToTry = _requestedPort == DefaultPort
                ? [DefaultPort, FallbackPort]
                : [_requestedPort];

            foreach (int port in portsToTry)
            {
                try
                {
                    var listener = new TcpListener(IPAddress.Loopback, port);
                    listener.Start();
                    _listener = listener;
                    _actualPort = port;
                    _cts = new CancellationTokenSource();
                    _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
                    Debug.WriteLine($"[MediaWidget] WebNowPlaying listening on port {port}");
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MediaWidget] WebNowPlaying bind failed on port {port}: {ex.Message}");
                }
            }

            _listener = null;
            _cts = null;
            return false;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptSocketAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return;
                Debug.WriteLine($"[MediaWidget] WebNowPlaying accept failed: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
        }
    }

    public void Dispose()
    {
        Task? acceptLoop;
        CancellationTokenSource? cts;
        WnpWebSocket? socket;
        WnpPlayerSnapshot? released;
        Dictionary<int, TaskCompletionSource<int>> pending;

        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            acceptLoop = _acceptLoop;
            cts = _cts;
            socket = _socket;
            released = _activeSnapshot;
            pending = new Dictionary<int, TaskCompletionSource<int>>(_pendingResults);

            _acceptLoop = null;
            _cts = null;
            _socket = null;
            _activeSnapshot = null;
            _players.Clear();
            _pendingResults.Clear();

            try { _listener?.Stop(); } catch { }
            _listener = null;
        }

        // Unblock any waiter with "unknown" instead of hanging it for the full timeout.
        foreach (var tcs in pending.Values)
        {
            try { tcs.TrySetResult(0); } catch { }
        }

        try { cts?.Cancel(); } catch { }
        try { socket?.Dispose(); } catch { }
        try { acceptLoop?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        cts?.Dispose();
        _sendLock.Dispose();

        // Release any widget currently displaying browser media.
        if (released != null)
        {
            try { ActivePlayerChanged?.Invoke(null); } catch { }
        }
    }

    private async Task HandleClientAsync(Socket client, CancellationToken ct)
    {
        WnpWebSocket? socket = null;
        try
        {
            client.NoDelay = true;
            socket = await WnpWebSocket.AcceptAsync(client, ct);

            WnpWebSocket? previous;
            lock (_gate)
            {
                previous = _socket;
                _socket = socket;
            }
            previous?.Dispose();   // The extension replaced its connection: drop the old one.

            // Tell the extension which protocol we speak before it has to guess.
            await socket.SendTextAsync(WnpProtocol.BuildHandshake(), ct);
            lock (_gate) _loggedRawFrames = 0;
            HiddenDiagnosticsLogger.Log("[WNP] wire connected, handshake sent");
            try { ConnectionChanged?.Invoke(); } catch { }

            while (!ct.IsCancellationRequested)
            {
                WnpWsMessage? message = await socket.ReadMessageAsync(ct);
                if (message == null) break;

                if (message.Value.IsBinary)
                {
                    HandleCover(message.Value.Payload);
                }
                else
                {
                    string text = Encoding.UTF8.GetString(message.Value.Payload);
                    LogWireFrame(text);
                    if (TryApplyText(text, out WnpPlayerSnapshot? snapshot))
                    {
                        Raise(snapshot);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"[MediaWidget] WebNowPlaying connection error: {ex.Message}");
        }
        finally
        {
            WnpPlayerSnapshot? released = null;
            lock (_gate)
            {
                if (ReferenceEquals(_socket, socket))
                {
                    _socket = null;
                    _players.Clear();
                    released = _activeSnapshot;
                    _activeSnapshot = null;
                }
            }

            if (released != null)
            {
                Raise(null);
            }

            HiddenDiagnosticsLogger.Log("[WNP] wire disconnected");
            try { ConnectionChanged?.Invoke(); } catch { }

            try { socket?.Dispose(); } catch { }
            try { client.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Raw wire tap: every frame the extension sends lands in hidden_diagnostics.log.
    /// Event answers as one line; the first 3 player frames in full (field-order check);
    /// anything unparseable always (that is the "connected but no data" smoking gun).
    /// Pure classification — no state changes here.
    /// </summary>
    private void LogWireFrame(string text)
    {
        try
        {
            if (WnpProtocol.TryParseEventResult(text, out int eventId, out int result))
            {
                HiddenDiagnosticsLogger.Log($"[WNP] wire event-result ev={eventId} res={result}");
                return;
            }

            if (!WnpProtocol.TryParseText(text, out WnpMessageType type, out int id, out string?[] fields))
            {
                HiddenDiagnosticsLogger.Log($"[WNP] wire UNPARSED: {TruncateWire(text, 240)}");
                return;
            }

            bool full;
            lock (_gate)
            {
                full = _loggedRawFrames < 3;
                if (full) _loggedRawFrames++;
            }

            if (full)
            {
                HiddenDiagnosticsLogger.Log($"[WNP] wire raw type={type} id={id}: {TruncateWire(text, 420)}");
            }
        }
        catch
        {
            // Wire tap must never break the connection.
        }
    }

    private static string TruncateWire(string text, int max)
    {
        if (text.Length <= max) return text;
        return text.Substring(0, max) + $"…(+{text.Length - max})";
    }

    /// <summary>Folds one text frame into player state; returns the new active snapshot when it changed.</summary>
    private bool TryApplyText(string text, out WnpPlayerSnapshot? snapshot)
    {
        snapshot = null;

        // Command answers never touch player state: complete the waiter and notify.
        if (WnpProtocol.TryParseEventResult(text, out int resultEventId, out int result))
        {
            lock (_gate)
            {
                if (_pendingResults.TryGetValue(resultEventId, out var tcs))
                {
                    _pendingResults.Remove(resultEventId);
                    try { tcs.TrySetResult(result); } catch { }
                }
                // Stash for a waiter that hasn't arrived yet (bounded: drop oldest past 128).
                _completedResults[resultEventId] = result;
                while (_completedResults.Count > 128)
                {
                    using var e = _completedResults.GetEnumerator();
                    if (!e.MoveNext()) break;
                    _completedResults.Remove(e.Current.Key);
                }
            }
            try { EventResultReceived?.Invoke(resultEventId, result); } catch { }
            return false;
        }

        if (!WnpProtocol.TryParseText(text, out WnpMessageType type, out int id, out string?[] fields))
        {
            return false;
        }

        lock (_gate)
        {
            switch (type)
            {
                case WnpMessageType.PlayerAdded:
                case WnpMessageType.PlayerUpdated:
                    if (!_players.TryGetValue(id, out WnpPlayer? player))
                    {
                        player = new WnpPlayer(id);
                        _players[id] = player;
                    }
                    player.ApplyFields(id, fields);
                    break;

                case WnpMessageType.PlayerRemoved:
                    if (!_players.Remove(id)) return false;
                    break;

                default:
                    return false;   // Event results and settings frames: nothing to project.
            }

            WnpPlayerSnapshot? next = ComputeActiveLocked();
            if (next == _activeSnapshot) return false;

            _activeSnapshot = next;
            snapshot = next;
            return true;
        }
    }

    private void HandleCover(byte[] payload)
    {
        if (payload.Length < 5) return;   // 4-byte little-endian player id + at least one PNG byte.

        int playerId = payload[0] | (payload[1] << 8) | (payload[2] << 16) | (payload[3] << 24);
        var png = new byte[payload.Length - 4];
        Buffer.BlockCopy(payload, 4, png, 0, png.Length);

        try { CoverReceived?.Invoke(playerId, png); } catch { }
    }

    private void Raise(WnpPlayerSnapshot? snapshot)
    {
        try { ActivePlayerChanged?.Invoke(snapshot); } catch { }
    }

    /// <summary>
    /// Mirrors the extension's own active-player rule: among players that report a title, prefer the
    /// one that is Playing with the most recent <c>activeAt</c>; otherwise the most recently active.
    /// </summary>
    private WnpPlayerSnapshot? ComputeActiveLocked()
    {
        WnpPlayer? best = null;

        foreach (WnpPlayer player in _players.Values)
        {
            if (string.IsNullOrWhiteSpace(player.Title)) continue;
            if (best == null)
            {
                best = player;
                continue;
            }

            bool playerPlaying = player.State == WnpState.Playing;
            bool bestPlaying = best.State == WnpState.Playing;

            if (playerPlaying != bestPlaying)
            {
                if (playerPlaying) best = player;
            }
            else if (player.ActiveAt > best.ActiveAt)
            {
                best = player;
            }
        }

        return best?.ToSnapshot();
    }

    // ---- Commands: adapter → extension ---------------------------------------------------------

    /// <summary>Sends the current transport state (play/pause). Returns the event id, or 0 when not sent.</summary>
    public Task<int> SetStateAsync(int playerId, bool playing) =>
        SendCommandAsync(playerId, WnpEvent.TrySetState, playing ? 0 : 1);

    /// <summary>Seeks the given player to a whole-second position. Returns the event id, or 0 when not sent.</summary>
    public Task<int> SetPositionAsync(int playerId, int seconds) =>
        SendCommandAsync(playerId, WnpEvent.TrySetPosition, Math.Max(0, seconds));

    public Task<int> SkipNextAsync(int playerId) => SendCommandAsync(playerId, WnpEvent.TrySkipNext, 0);

    public Task<int> SkipPreviousAsync(int playerId) => SendCommandAsync(playerId, WnpEvent.TrySkipPrevious, 0);

    /// <summary>
    /// Waits for the extension's answer to a command (mirrors wnp_wait_for_event_result, 1s budget).
    /// Returns 0 = pending/timeout, 1 = succeeded, 2 = failed.
    /// The waiter is pre-registered by <see cref="SendCommandAsync"/> before the frame goes
    /// out, so an answer that arrives mid-send is never lost; a result that arrived before
    /// this call is replayed from the completed cache.
    /// </summary>
    public Task<int> WaitForEventResultAsync(int eventId, TimeSpan? timeout = null)
    {
        if (eventId <= 0) return Task.FromResult(0);
        Task<int>? waitTask;
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(0);
            if (_completedResults.TryGetValue(eventId, out int cached))
            {
                _completedResults.Remove(eventId);
                return Task.FromResult(cached);
            }
            if (_pendingResults.TryGetValue(eventId, out var tcs))
            {
                waitTask = tcs.Task;
            }
            else
            {
                return Task.FromResult(0);
            }
        }

        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(1);
        return WaitWithTimeoutAsync(waitTask, effectiveTimeout, eventId, this);
    }

    private static async Task<int> WaitWithTimeoutAsync(Task<int> task, TimeSpan timeout, int eventId, WebNowPlayingServer owner)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            int result = await task.WaitAsync(cts.Token).ConfigureAwait(false);
            lock (owner._gate) owner._completedResults.Remove(eventId);
            return result;
        }
        catch (OperationCanceledException)
        {
            return 0;   // No answer in budget: unknown, not proof of failure.
        }
    }

    private async Task<int> SendCommandAsync(int playerId, WnpEvent evt, int data)
    {
        WnpWebSocket? socket;
        int eventId;

        lock (_gate)
        {
            if (_disposed || _socket == null) return 0;
            socket = _socket;
            eventId = _nextEventId;
            _nextEventId = (_nextEventId + 1) % MaxEventId;
            if (_nextEventId <= 0) _nextEventId = 1;
            // Pre-register before the frame goes out: the extension can answer in <1ms.
            _pendingResults[eventId] = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        string message = WnpProtocol.BuildCommand(playerId, eventId, evt, data);

        try
        {
            await _sendLock.WaitAsync();
            try
            {
                await socket.SendTextAsync(message, CancellationToken.None);
            }
            finally
            {
                _sendLock.Release();
            }
            return eventId;
        }
        catch (Exception ex)
        {
            // A dead extension is not worth crashing over: the receive loop cleans the socket up.
            Debug.WriteLine($"[MediaWidget] WebNowPlaying command failed: {ex.Message}");
            lock (_gate)
            {
                _pendingResults.Remove(eventId);
                _completedResults.Remove(eventId);
            }
            return 0;
        }
    }
}

