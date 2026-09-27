using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace MetroHub.Core.Radio;

/// <summary>
/// Thread-safe, growable byte buffer that lets a background downloader <see cref="Append"/> data
/// while BASS's "buffered" user-file download thread pulls it through <see cref="FileRead"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FileRead"/> blocks until data is available, and returns <c>0</c> <b>only</b> at a
/// genuine end of stream. In BASS a 0-byte read means EOF, so returning 0 while data is merely
/// late would make BASS end the track — exactly the "plays half a second then stops" failure.
/// </para>
/// <para>
/// The reader consumes bytes in order; <see cref="FileSeek"/> is supported within the
/// already-buffered region (WebM/Opus demuxing may seek near the start, never ahead of the
/// download in normal sequential playback).
/// </para>
/// </remarks>
public sealed class ChunkedAudioBuffer : IDisposable
{
    private const int SegmentSize = 256 * 1024;

    private readonly object _gate = new();
    private readonly List<byte[]> _segments = new();
    private readonly ManualResetEventSlim _dataAvailable = new(false);
    private readonly double _bytesPerSecond;

    private long _written;   // total bytes appended by the producer
    private long _readPos;   // current reader position
    private bool _completed; // producer reached a genuine end of stream
    private Exception? _fault;
    private bool _disposed;

    /// <param name="bytesPerSecond">
    /// Optional average stream bitrate in bytes/second, used to expose <see cref="BufferedSeconds"/>.
    /// Pass 0 when unknown.
    /// </param>
    public ChunkedAudioBuffer(double bytesPerSecond = 0)
    {
        _bytesPerSecond = Math.Max(0, bytesPerSecond);
    }

    /// <summary>Total bytes appended so far.</summary>
    public long BufferedBytes
    {
        get { lock (_gate) return _written; }
    }

    /// <summary>Bytes appended but not yet consumed by the reader.</summary>
    public long AvailableBytes
    {
        get { lock (_gate) return _written - _readPos; }
    }

    /// <summary>True once the producer signalled a genuine end of stream.</summary>
    public bool IsCompleted
    {
        get { lock (_gate) return _completed; }
    }

    /// <summary>Non-null when the producer failed (the stream will end early).</summary>
    public Exception? Fault
    {
        get { lock (_gate) return _fault; }
    }

    /// <summary>Approximate audio seconds buffered ahead of the reader (0 when the bitrate is unknown).</summary>
    public double BufferedSeconds
    {
        get
        {
            if (_bytesPerSecond <= 0) return 0;
            return AvailableBytes / _bytesPerSecond;
        }
    }

    /// <summary>Appends downloaded bytes. Safe to call from the download thread.</summary>
    public void Append(byte[] buffer, int offset, int count)
    {
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if (count <= 0) return;

        lock (_gate)
        {
            if (_disposed) return;

            int copied = 0;
            while (copied < count)
            {
                int segmentIndex = (int)(_written / SegmentSize);
                int segmentOffset = (int)(_written % SegmentSize);
                if (segmentIndex >= _segments.Count)
                {
                    _segments.Add(new byte[SegmentSize]);
                }

                byte[] segment = _segments[segmentIndex];
                int take = Math.Min(count - copied, SegmentSize - segmentOffset);
                Buffer.BlockCopy(buffer, offset + copied, segment, segmentOffset, take);
                _written += take;
                copied += take;
            }
        }

        _dataAvailable.Set();
    }

    /// <summary>Signals a genuine end of stream. The reader will drain remaining data, then see EOF.</summary>
    public void Complete()
    {
        lock (_gate) { _completed = true; }
        _dataAvailable.Set();
    }

    /// <summary>Signals that the producer failed. The reader treats it as an early end of stream.</summary>
    public void CompleteFaulted(Exception error)
    {
        lock (_gate)
        {
            _fault = error;
            _completed = true;
        }
        _dataAvailable.Set();
    }

    /// <summary>
    /// BASS <c>FILEREADPROC</c> entry point. Blocks until at least one byte is available, the
    /// stream ends, or <paramref name="timeoutMs"/> elapses. Returns 0 only at end of stream.
    /// </summary>
    public int FileRead(IntPtr buffer, int length, int timeoutMs = 30000)
    {
        if (length <= 0) return 0;
        long deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);

        while (true)
        {
            lock (_gate)
            {
                if (_disposed) return 0;

                int available = (int)Math.Min(_written - _readPos, length);
                if (available > 0)
                {
                    int copied = 0;
                    while (copied < available)
                    {
                        int segmentIndex = (int)(_readPos / SegmentSize);
                        int segmentOffset = (int)(_readPos % SegmentSize);
                        byte[] segment = _segments[segmentIndex];
                        int chunk = Math.Min(available - copied, SegmentSize - segmentOffset);
                        Marshal.Copy(segment, segmentOffset, IntPtr.Add(buffer, copied), chunk);
                        _readPos += chunk;
                        copied += chunk;
                    }
                    return available;
                }

                if (_completed)
                {
                    return 0; // genuine EOF (normal completion or fault)
                }
            }

            long remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                // The producer stalled for too long. Return EOF so BASS ends the track instead of
                // hanging its download thread forever; the service surfaces this as an interruption.
                JukeboxLog.Warn("ChunkedAudioBuffer stalled waiting for data; signalling EOF.");
                return 0;
            }

            _dataAvailable.Wait((int)Math.Min(remaining, 1000));
            _dataAvailable.Reset();
        }
    }

    /// <summary>
    /// BASS <c>FILESEEKPROC</c> entry point. Seeks to <paramref name="offset"/>; a forward seek
    /// past the downloaded region waits (bounded) for the producer to catch up rather than
    /// silently landing at the wrong position. Returns false only when the seek cannot happen.
    /// </summary>
    public bool FileSeek(long offset, int timeoutMs = 30000)
    {
        if (offset < 0) return false;
        long deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);

        while (true)
        {
            lock (_gate)
            {
                if (_disposed) return false;

                if (offset <= _written)
                {
                    _readPos = offset;
                    return true;
                }

                if (_completed)
                {
                    // Stream ended before the requested offset exists: cannot honour the seek.
                    return false;
                }
            }

            if (Environment.TickCount64 >= deadline)
            {
                JukeboxLog.Warn($"ChunkedAudioBuffer seek to {offset} timed out (buffered {BufferedBytes} bytes).");
                return false;
            }

            _dataAvailable.Wait(100);
            _dataAvailable.Reset();
        }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; }
        try { _dataAvailable.Set(); } catch { }
        try { _dataAvailable.Dispose(); } catch { }
        lock (_gate) { _segments.Clear(); }
    }
}
