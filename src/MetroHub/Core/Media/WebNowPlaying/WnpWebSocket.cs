using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace MetroHub.Core.Media.WebNowPlaying;

internal enum WsOpcode : byte
{
    Continuation = 0,
    Text = 1,
    Binary = 2,
    Close = 8,
    Ping = 9,
    Pong = 10,
}

/// <summary>A complete (reassembled) WebSocket message.</summary>
internal readonly record struct WnpWsMessage(bool IsBinary, byte[] Payload);

/// <summary>
/// Minimal server-side RFC 6455 implementation — exactly the subset the WebNowPlaying extension
/// needs: HTTP upgrade handshake, text/binary frames, masking, fragmentation, ping/pong and close.
/// </summary>
/// <remarks>
/// Implemented directly over a <see cref="TcpListener"/> instead of <c>HttpListener</c> so the
/// adapter never needs a URL ACL reservation or elevation to listen on loopback, and instead of a
/// package because everything required ships in the box.
/// </remarks>
internal sealed class WnpWebSocket : IDisposable
{
    private const int MaxHandshakeBytes = 8 * 1024;
    private const long MaxPayloadBytes = 64L * 1024 * 1024;

    private static readonly byte[] WebSocketGuid = Encoding.ASCII.GetBytes("258EAFA5-E914-47DA-95CA-C5AB0DC85B11");

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    private WnpWebSocket(Socket socket, NetworkStream stream)
    {
        _socket = socket;
        _stream = stream;
    }

    /// <summary>Reads the client's upgrade request and completes the WebSocket handshake.</summary>
    public static async Task<WnpWebSocket> AcceptAsync(Socket socket, CancellationToken ct)
    {
        var stream = new NetworkStream(socket, ownsSocket: false);
        string request = await ReadHandshakeAsync(stream, ct);

        string? key = FindHeader(request, "sec-websocket-key");
        if (key == null)
        {
            throw new InvalidDataException("WebSocket handshake is missing Sec-WebSocket-Key.");
        }

        byte[] accept = SHA1.HashData(Encoding.ASCII.GetBytes(key + Encoding.ASCII.GetString(WebSocketGuid)));
        string response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {Convert.ToBase64String(accept)}\r\n\r\n";

        byte[] responseBytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(responseBytes, ct);
        await stream.FlushAsync(ct);

        return new WnpWebSocket(socket, stream);
    }

    public Task SendTextAsync(string text, CancellationToken ct) =>
        SendFrameAsync(WsOpcode.Text, Encoding.UTF8.GetBytes(text), ct);

    /// <summary>
    /// Returns the next complete message, reassembling fragments and answering pings on the way.
    /// Null means the peer closed the connection or the stream ended.
    /// </summary>
    public async Task<WnpWsMessage?> ReadMessageAsync(CancellationToken ct)
    {
        MemoryStream? fragments = null;
        WsOpcode fragmentOpcode = WsOpcode.Text;

        try
        {
            while (true)
            {
                byte[] header = await ReadExactAsync(2, ct);
                bool final = (header[0] & 0x80) != 0;
                var opcode = (WsOpcode)(header[0] & 0x0F);
                bool masked = (header[1] & 0x80) != 0;

                long length = header[1] & 0x7F;
                if (length == 126)
                {
                    byte[] ext = await ReadExactAsync(2, ct);
                    length = (ext[0] << 8) | ext[1];
                }
                else if (length == 127)
                {
                    byte[] ext = await ReadExactAsync(8, ct);
                    length = 0;
                    for (int i = 0; i < 8; i++)
                    {
                        length = (length << 8) | ext[i];
                    }
                }

                if (length < 0 || length > MaxPayloadBytes)
                {
                    throw new InvalidDataException($"WebSocket frame too large: {length} bytes.");
                }

                byte[]? mask = masked ? await ReadExactAsync(4, ct) : null;
                byte[] payload = length == 0
                    ? Array.Empty<byte>()
                    : await ReadExactAsync((int)length, ct);

                if (mask != null)
                {
                    for (int i = 0; i < payload.Length; i++)
                    {
                        payload[i] ^= mask[i & 3];
                    }
                }

                switch (opcode)
                {
                    case WsOpcode.Ping:
                        await SendFrameAsync(WsOpcode.Pong, payload, ct);
                        continue;

                    case WsOpcode.Pong:
                        continue;

                    case WsOpcode.Close:
                        try { await SendFrameAsync(WsOpcode.Close, Array.Empty<byte>(), ct); } catch { }
                        return null;

                    case WsOpcode.Text:
                    case WsOpcode.Binary:
                        if (final)
                        {
                            return new WnpWsMessage(opcode == WsOpcode.Binary, payload);
                        }

                        fragments?.Dispose();
                        fragments = new MemoryStream();
                        fragments.Write(payload, 0, payload.Length);
                        fragmentOpcode = opcode;
                        continue;

                    case WsOpcode.Continuation:
                        if (fragments == null)
                        {
                            throw new InvalidDataException("Unexpected WebSocket continuation frame.");
                        }

                        fragments.Write(payload, 0, payload.Length);
                        if (final)
                        {
                            var complete = new WnpWsMessage(fragmentOpcode == WsOpcode.Binary, fragments.ToArray());
                            fragments.Dispose();
                            return complete;
                        }
                        continue;

                    default:
                        throw new InvalidDataException($"Unsupported WebSocket opcode 0x{(int)opcode:X2}.");
                }
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ObjectDisposedException)
        {
            return null;   // Peer went away: not an error worth surfacing.
        }
        finally
        {
            fragments?.Dispose();
        }
    }

    private static async Task<string> ReadHandshakeAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[1024];
        var accumulated = new MemoryStream();

        while (accumulated.Length < MaxHandshakeBytes)
        {
            int read = await stream.ReadAsync(buffer, ct);
            if (read == 0) throw new EndOfStreamException("Client closed before completing the handshake.");

            accumulated.Write(buffer, 0, read);
            byte[] all = accumulated.ToArray();

            for (int i = all.Length - 4; i >= 0; i--)
            {
                if (all[i] == '\r' && all[i + 1] == '\n' && all[i + 2] == '\r' && all[i + 3] == '\n')
                {
                    return Encoding.ASCII.GetString(all, 0, i + 4);
                }
            }
        }

        throw new InvalidDataException("WebSocket handshake header exceeded the size limit.");
    }

    private static string? FindHeader(string request, string name)
    {
        string[] lines = request.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            ReadOnlySpan<char> headerName = lines[i].AsSpan(0, colon).Trim();
            if (!headerName.Equals(name.AsSpan(), StringComparison.OrdinalIgnoreCase)) continue;
            return lines[i].Substring(colon + 1).Trim();
        }

        return null;
    }

    private async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
    {
        byte[] buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await _stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }

        return buffer;
    }

    private async Task SendFrameAsync(WsOpcode opcode, byte[] payload, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var frame = new MemoryStream();
            frame.WriteByte((byte)(0x80 | (byte)opcode));

            if (payload.Length < 126)
            {
                frame.WriteByte((byte)payload.Length);
            }
            else if (payload.Length <= ushort.MaxValue)
            {
                frame.WriteByte(126);
                frame.WriteByte((byte)(payload.Length >> 8));
                frame.WriteByte((byte)payload.Length);
            }
            else
            {
                frame.WriteByte(127);
                for (int i = 7; i >= 0; i--)
                {
                    frame.WriteByte((byte)((long)payload.Length >> (8 * i)));
                }
            }

            frame.Write(payload, 0, payload.Length);
            byte[] bytes = frame.ToArray();
            await _stream.WriteAsync(bytes, ct);
            await _stream.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stream.Dispose(); } catch { }
        try { _socket.Dispose(); } catch { }
        _writeLock.Dispose();
    }
}
