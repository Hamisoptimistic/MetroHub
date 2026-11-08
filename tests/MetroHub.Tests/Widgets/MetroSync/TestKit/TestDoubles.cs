using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using MetroHub.Widgets.Catalog.MetroSync.Models;
using MetroHub.Widgets.Catalog.MetroSync.Services;

namespace MetroHub.Tests.Widgets.MetroSync.TestKit;

/// <summary>Durable-store stand-in. Survives engine "restarts" within one test, like a real database would.</summary>
public sealed class InMemoryItemIndex : IItemIndex
{
    private readonly ConcurrentDictionary<long, MetroSyncMessageItem> _items = new();
    private long _duplicates;

    public int Count => _items.Count;
    public long DuplicateUpserts => Interlocked.Read(ref _duplicates);
    public IReadOnlyList<long> UpdateIds => _items.Keys.OrderBy(k => k).ToArray();

    public ValueTask<bool> UpsertAsync(MetroSyncMessageItem item, CancellationToken ct)
    {
        var added = _items.TryAdd(item.UpdateId, item);
        if (!added) Interlocked.Increment(ref _duplicates);
        return ValueTask.FromResult(added);
    }
}

/// <summary>Maps text messages to TextPayloadItem and everything else to TextPayloadItem with fallback text.</summary>
public sealed class PassthroughNormalizer : IUpdateNormalizer
{
    public MetroSyncMessageItem? Normalize(RawUpdate update, DateTimeOffset receivedAt)
    {
        var root = update.Json;
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return null;

        var messageId = message.TryGetProperty("message_id", out var mid) && mid.TryGetInt64(out var v) ? v : 0;
        if (message.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
        {
            var str = text.GetString()!;
            if (str.Trim().Equals("/start", StringComparison.OrdinalIgnoreCase)) return null;
            return new TextPayloadItem
            {
                UpdateId = update.UpdateId,
                MessageId = messageId,
                Timestamp = receivedAt,
                Text = str
            };
        }

        return new TextPayloadItem
        {
            UpdateId = update.UpdateId,
            MessageId = messageId,
            Timestamp = receivedAt,
            Text = "[non-text]"
        };
    }

    public MetroSyncMessageItem? NormalizeOutbound(JsonElement message, DateTimeOffset sentAt) => null;
}

/// <summary>
/// Transport that plays a script: each GetUpdates call consumes one step.
/// When the script is empty it behaves like an idle long-poll (blocks until cancelled).
/// </summary>
public sealed class ScriptedTransport : ITelegramTransport
{
    private readonly ConcurrentQueue<Func<CancellationToken, Task<IReadOnlyList<RawUpdate>>>> _script = new();
    private readonly List<long> _offsets = new();
    private readonly List<int> _timeouts = new();
    private int _calls;

    public int PollCount => Volatile.Read(ref _calls);

    public IReadOnlyList<long> Offsets
    {
        get { lock (_offsets) return _offsets.ToArray(); }
    }

    public IReadOnlyList<int> Timeouts
    {
        get { lock (_offsets) return _timeouts.ToArray(); }
    }

    public ScriptedTransport Then(Func<CancellationToken, Task<IReadOnlyList<RawUpdate>>> step)
    {
        _script.Enqueue(step);
        return this;
    }

    public ScriptedTransport ThenThrow(Exception error) =>
        Then(_ => Task.FromException<IReadOnlyList<RawUpdate>>(error));

    public ScriptedTransport ThenReturn(params RawUpdate[] updates) =>
        Then(_ => Task.FromResult<IReadOnlyList<RawUpdate>>(updates));

    public Task<TelegramBotUser> GetMeAsync(CancellationToken ct) =>
        Task.FromResult(new TelegramBotUser(123456, true, "ScriptedBot", "scripted_bot"));

    public async Task<IReadOnlyList<RawUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        lock (_offsets)
        {
            _offsets.Add(offset);
            _timeouts.Add(timeoutSeconds);
        }

        if (_script.TryDequeue(out var step))
            return await step(ct).ConfigureAwait(false);

        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); // idle long-poll
        return Array.Empty<RawUpdate>();
    }

    public Task<TelegramFileInfo> GetFileAsync(string fileId, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task DownloadAsync(string filePath, Stream destination, long maxBytes, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<JsonDocument> SendMessageAsync(long chatId, string text, CancellationToken ct) =>
        Task.FromResult(JsonDocument.Parse("{\"ok\":true,\"result\":{\"message_id\":1}}"));

    public Task<JsonDocument> SendPhotoAsync(long chatId, Stream imageStream, string? caption, CancellationToken ct) =>
        Task.FromResult(JsonDocument.Parse("{\"ok\":true,\"result\":{\"message_id\":2}}"));
}

/// <summary>Wraps another transport and records every call. Used to prove "no downloads happened".</summary>
public sealed class RecordingTransport(ITelegramTransport inner) : ITelegramTransport
{
    private int _getFile;
    private int _download;
    private int _sendMessage;
    private int _sendPhoto;

    public int GetFileCalls => Volatile.Read(ref _getFile);
    public int DownloadCalls => Volatile.Read(ref _download);
    public int SendMessageCalls => Volatile.Read(ref _sendMessage);
    public int SendPhotoCalls => Volatile.Read(ref _sendPhoto);

    public Task<TelegramBotUser> GetMeAsync(CancellationToken ct) =>
        inner.GetMeAsync(ct);

    public Task<IReadOnlyList<RawUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct) =>
        inner.GetUpdatesAsync(offset, timeoutSeconds, ct);

    public Task<TelegramFileInfo> GetFileAsync(string fileId, CancellationToken ct)
    {
        Interlocked.Increment(ref _getFile);
        return inner.GetFileAsync(fileId, ct);
    }

    public Task DownloadAsync(string filePath, Stream destination, long maxBytes, CancellationToken ct)
    {
        Interlocked.Increment(ref _download);
        return inner.DownloadAsync(filePath, destination, maxBytes, ct);
    }

    public Task<JsonDocument> SendMessageAsync(long chatId, string text, CancellationToken ct)
    {
        Interlocked.Increment(ref _sendMessage);
        return inner.SendMessageAsync(chatId, text, ct);
    }

    public Task<JsonDocument> SendPhotoAsync(long chatId, Stream imageStream, string? caption, CancellationToken ct)
    {
        Interlocked.Increment(ref _sendPhoto);
        return inner.SendPhotoAsync(chatId, imageStream, caption, ct);
    }
}

/// <summary>
/// Deterministic controllable TimeProvider for timer and backoff verification.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly List<ManualTimer> _timers = [];
    private readonly object _lock = new();

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock) return _utcNow;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_lock)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            _timers.Add(timer);
            return timer;
        }
    }

    public void Advance(TimeSpan delta)
    {
        List<ManualTimer> toFire = [];
        lock (_lock)
        {
            _utcNow += delta;
            foreach (var timer in _timers)
            {
                if (timer.CheckDue(_utcNow))
                {
                    toFire.Add(timer);
                }
            }
        }

        foreach (var timer in toFire)
        {
            timer.Fire();
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period;
        private DateTimeOffset? _dueTime;
        private bool _disposed;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            _period = period;
            _dueTime = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + dueTime;
        }

        public bool CheckDue(DateTimeOffset now)
        {
            if (_disposed || _dueTime is null) return false;
            if (now >= _dueTime.Value)
            {
                if (_period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan)
                    _dueTime = now + _period;
                else
                    _dueTime = null;
                return true;
            }
            return false;
        }

        public void Fire()
        {
            if (!_disposed) _callback(_state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed) return false;
            _period = period;
            _dueTime = dueTime == Timeout.InfiniteTimeSpan ? null : _owner.GetUtcNow() + dueTime;
            return true;
        }

        public void Dispose()
        {
            _disposed = true;
            lock (_owner._lock) _owner._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
