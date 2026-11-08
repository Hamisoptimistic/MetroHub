using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using MetroHub.Widgets.Catalog.MetroSync.Models;

namespace MetroHub.Widgets.Catalog.MetroSync.Services;

/// <summary>
/// Polling engine. One loop task, zero CPU while idle (awaiting a long-poll).
/// Delivery guarantee: gate -> normalize -> persist to index -> (next poll's offset acknowledges).
/// A crash at any point leaves each update either unacknowledged (redelivered, deduped by index)
/// or safely persisted.
/// </summary>
public sealed class ConduitEngine : IAsyncDisposable, IDisposable
{
    private readonly ITelegramTransport _transport;
    private readonly SenderGate _gate;
    private readonly IUpdateNormalizer _normalizer;
    private readonly IItemIndex _index;
    private readonly ConduitEngineOptions _opt;
    private readonly TimeProvider _time;
    private readonly Channel<MetroSyncMessageItem> _out;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _started;
    private int _disposed;
    private int _attempt;
    private long _offset;
    private long _dropped;
    private long _persisted;
    private long _redelivered;
    private volatile EngineState _state = EngineState.Stopped;

    public ConduitEngine(
        ITelegramTransport transport,
        SenderGate gate,
        IUpdateNormalizer normalizer,
        IItemIndex index,
        ConduitEngineOptions? options = null,
        TimeProvider? time = null)
    {
        _transport = transport;
        _gate = gate;
        _normalizer = normalizer;
        _index = index;
        _opt = options ?? new ConduitEngineOptions();
        _time = time ?? TimeProvider.System;
        _offset = _opt.InitialOffset;
        var capacity = Math.Max(1, _opt.ChannelCapacity);
        _out = Channel.CreateBounded<MetroSyncMessageItem>(new BoundedChannelOptions(capacity)
        {
            // The index is authoritative; dropping from the live feed is safe.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = false,
        });
    }

    /// <summary>Live feed of NEW items only (redeliveries are suppressed).</summary>
    public ChannelReader<MetroSyncMessageItem> Items => _out.Reader;

    public EngineState State => _state;
    public long Offset => Volatile.Read(ref _offset);
    public long DroppedCount => Interlocked.Read(ref _dropped);
    public long PersistedCount => Interlocked.Read(ref _persisted);
    public long RedeliveredCount => Interlocked.Read(ref _redelivered);

    /// <summary>Completes when the loop ends; faults on an unexpected exception (state Faulted).</summary>
    public Task Completion => _loop ?? Task.CompletedTask;

    /// <summary>Cleared on dispose. Subscribers must unsubscribe in their own teardown.</summary>
    public event Action<EngineState>? StateChanged;

    /// <summary>Fired whenever the acknowledged offset advances.</summary>
    public event Action<long>? OffsetChanged;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) return;

        var cts = new CancellationTokenSource();
        _cts = cts;
        SetState(EngineState.Connecting);
        _loop = Task.Run(() => RunAsync(cts.Token)); // Task.Run: never capture a UI sync context
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    watchdog.CancelAfter(_opt.PollWatchdog);

                    // Use a 0-second fast poll until Online to verify credentials, drain backlog,
                    // and flip state to Online instantly without blocking on a 25s idle wait.
                    var timeout = _state == EngineState.Online ? _opt.PollTimeoutSeconds : 0;
                    var batch = await _transport
                        .GetUpdatesAsync(Volatile.Read(ref _offset), timeout, watchdog.Token)
                        .ConfigureAwait(false);

                    foreach (var update in batch)
                    {
                        if (!_gate.IsAllowed(update))
                        {
                            Interlocked.Increment(ref _dropped); // silent: never reply, never log content
                            AdvanceOffset(update.UpdateId + 1);
                            continue;
                        }

                        var item = _normalizer.Normalize(update, _time.GetUtcNow());
                        if (item is null)
                        {
                            // A message like /start is acknowledged by offset advancement, but not stored.
                            AdvanceOffset(update.UpdateId + 1);
                            continue;
                        }

                        var isNew = await _index.UpsertAsync(item, ct).ConfigureAwait(false); // durable BEFORE ack
                        if (isNew)
                        {
                            Interlocked.Increment(ref _persisted);
                            _out.Writer.TryWrite(item);
                        }
                        else
                        {
                            Interlocked.Increment(ref _redelivered);
                        }

                        AdvanceOffset(update.UpdateId + 1);
                    }

                    _attempt = 0;
                    SetState(EngineState.Online);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (TelegramApiException e) when (e.ErrorCode == 401)
                {
                    SetState(EngineState.Unauthorized); // terminal: token revoked
                    return;
                }
                catch (TelegramApiException e) when (e.ErrorCode == 409)
                {
                    SetState(EngineState.Conflict); // another poller or an active webhook
                    await Task.Delay(_opt.ConflictDelay, _time, ct).ConfigureAwait(false);
                }
                catch (TelegramApiException e)
                {
                    SetState(EngineState.Backoff);
                    var wait = e.ErrorCode == 429 && e.RetryAfterSeconds is { } seconds
                        ? TimeSpan.FromSeconds(seconds)
                        : NextBackoff();
                    await Task.Delay(wait, _time, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (IsTransient(e))
                {
                    SetState(EngineState.Backoff);
                    await Task.Delay(NextBackoff(), _time, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // normal shutdown (cancellation raised from a delay inside a catch block)
        }
        catch
        {
            SetState(EngineState.Faulted);
            throw; // surfaces through Completion
        }
    }

    private TimeSpan NextBackoff()
    {
        _attempt++;
        return Backoff.Delay(_attempt, _opt.Jitter(), _opt.BackoffBase, _opt.BackoffCap);
    }

    // Reached for OperationCanceledException only when the per-poll watchdog fired (not shutdown).
    private static bool IsTransient(Exception e) =>
        e is HttpRequestException or IOException or SocketException or TimeoutException
            or JsonException or OperationCanceledException
        || (e.InnerException is not null && IsTransient(e.InnerException));

    private void AdvanceOffset(long newOffset)
    {
        if (newOffset > Volatile.Read(ref _offset))
        {
            Volatile.Write(ref _offset, newOffset);
            try { OffsetChanged?.Invoke(newOffset); }
            catch { /* a faulty subscriber must never kill the loop */ }
        }
    }

    private void SetState(EngineState state)
    {
        if (_state == state) return;
        _state = state;
        try { StateChanged?.Invoke(state); }
        catch { /* a faulty subscriber must never kill the loop */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return; // idempotent

        StateChanged = null; // 1. stop new notifications
        OffsetChanged = null;
        var cts = _cts;
        var loop = _loop;

        try { cts?.Cancel(); } // 2. aborts the in-flight HTTP read
        catch (ObjectDisposedException) { }

        var exited = true;
        if (loop is not null)
        {
            try { await loop.WaitAsync(_opt.ShutdownTimeout, _time).ConfigureAwait(false); } // 3. bounded wait
            catch (TimeoutException) { exited = false; }
            catch { /* loop faulted or was cancelled: observed here so nothing goes unobserved */ }
        }

        if (exited) cts?.Dispose(); // 4. only after the loop has exited
        _out.Writer.TryComplete();  // 5. release readers
        _state = EngineState.Stopped;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        StateChanged = null;
        OffsetChanged = null;
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { }

        _out.Writer.TryComplete();
        _state = EngineState.Stopped;
    }
}
