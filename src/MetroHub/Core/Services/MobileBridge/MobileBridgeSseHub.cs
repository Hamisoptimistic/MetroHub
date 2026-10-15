using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MetroHub.Core.Services.MobileBridge;

/// <summary>A single server-sent-events subscriber connected from a phone.</summary>
public sealed class MobileBridgeSseClient : IDisposable
{
    private const int MaxQueuedFrames = 32;

    // DropOldest keeps a slow or backgrounded phone from ever growing the queue without bound.
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(MaxQueuedFrames)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    private int _disposed;

    public string? DeviceLabel { get; init; }

    public DateTime ConnectedUtc { get; } = DateTime.UtcNow;

    internal bool TryEnqueue(string frame) => _queue.Writer.TryWrite(frame);

    /// <summary>
    /// Reader for the queued frames. Exposed directly instead of as an async enumerable: the one
    /// produced by <c>ChannelReader.ReadAllAsync</c> implements <c>IAsyncDisposable</c> but throws
    /// <see cref="NotSupportedException"/> when disposed, which turned every phone disconnect into a
    /// logged handler failure.
    /// </summary>
    internal ChannelReader<string> Reader => _queue.Reader;

    internal void Complete()
    {
        try { _queue.Writer.TryComplete(); } catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Complete();
    }
}

/// <summary>
/// Fan-out hub for PC &#8594; phone pushes. Event-driven only: no timers, no polling, no per-client threads.
/// </summary>
public sealed class MobileBridgeSseHub : IDisposable
{
    public const int MaxClients = 8;

    private readonly List<MobileBridgeSseClient> _clients = new();
    private readonly object _lock = new();
    private bool _disposed;

    public int ClientCount
    {
        get
        {
            lock (_lock)
            {
                return _clients.Count;
            }
        }
    }

    /// <summary>Registers a subscriber, or returns false when the client cap is reached.</summary>
    public bool TryRegister(MobileBridgeSseClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        lock (_lock)
        {
            if (_disposed || _clients.Count >= MaxClients)
            {
                return false;
            }

            _clients.Add(client);
            return true;
        }
    }

    public void Unregister(MobileBridgeSseClient client)
    {
        if (client == null)
        {
            return;
        }

        lock (_lock)
        {
            _clients.Remove(client);
        }

        client.Dispose();
    }

    /// <summary>Queues one event frame to every subscriber. Never throws.</summary>
    public int Publish(string eventName, string jsonPayload)
    {
        string frame = $"event: {eventName}\ndata: {jsonPayload}\n\n";
        int delivered = 0;

        lock (_lock)
        {
            foreach (var client in _clients)
            {
                if (client.TryEnqueue(frame))
                {
                    delivered++;
                }
            }
        }

        return delivered;
    }

    public IReadOnlyList<MobileBridgeSseClient> SnapshotClients()
    {
        lock (_lock)
        {
            return _clients.ToArray();
        }
    }

    /// <summary>Drops every subscriber without disposing the hub itself, so it can be reused after a restart.</summary>
    public void DisconnectAll()
    {
        List<MobileBridgeSseClient> pending;

        lock (_lock)
        {
            pending = new List<MobileBridgeSseClient>(_clients);
            _clients.Clear();
        }

        foreach (var client in pending)
        {
            client.Dispose();
        }
    }

    public void Dispose()
    {
        List<MobileBridgeSseClient> pending;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pending = new List<MobileBridgeSseClient>(_clients);
            _clients.Clear();
        }

        foreach (var client in pending)
        {
            client.Dispose();
        }
    }
}
