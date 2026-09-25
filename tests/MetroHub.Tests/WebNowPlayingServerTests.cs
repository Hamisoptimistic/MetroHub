using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using MetroHub.Core.Media.WebNowPlaying;
using Xunit;

namespace MetroHub.Tests;

/// <summary>
/// Loopback integration tests for the WebNowPlaying adapter: a real WebSocket client plays the role
/// of the browser extension, so the handshake, player tracking and command path are exercised over
/// the actual wire rather than through mocks.
/// </summary>
public sealed class WebNowPlayingServerTests : IDisposable
{
    /// <summary>Chosen well away from the built-in adapter ports (8974, 6534, 5468, 8698) and 8642.</summary>
    private const int Port = 18642;

    private readonly WebNowPlayingServer _server = new(Port);

    public void Dispose() => _server.Dispose();

    private static string FullPayload(string title, int position, int duration, long activeAt) =>
        $"7|YouTube|{title}|Artist|Album|\u0001|0|{position}|{duration}|" +
        "100|0|0|0|1|1|1|1|1|1|0|0|0|0|1700000000000|1700000001000|" + activeAt + "|";

    private static async Task<string> ReceiveTextAsync(ClientWebSocket socket)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();

        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("Server closed the socket unexpectedly.");
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }

        return Encoding.UTF8.GetString(message.ToArray());
    }

    private static Task SendTextAsync(ClientWebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    [Fact]
    public void FreshServer_ReportsNotRunning()
    {
        // Its own instance so the assertion cannot depend on test ordering with the started server.
        using var server = new WebNowPlayingServer(Port + 1);
        Assert.False(server.IsRunning);
        Assert.False(server.IsConnected);
        Assert.Null(server.ActivePlayer);
    }

    [Fact]
    public async Task ExtensionConnection_Handshakes_TracksPlayers_AndReceivesCommands()
    {
        var changes = new ConcurrentQueue<WnpPlayerSnapshot?>();
        using var changed = new SemaphoreSlim(0);
        _server.ActivePlayerChanged += snapshot =>
        {
            changes.Enqueue(snapshot);
            changed.Release();
        };

        Assert.True(_server.Start());
        Assert.True(_server.IsRunning);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{Port}"), CancellationToken.None);
        Assert.Equal(WebSocketState.Open, client.State);

        // The adapter must announce its protocol revision first, or the extension assumes legacy.
        Assert.Equal(WnpProtocol.BuildHandshake(), await ReceiveTextAsync(client));

        // PLAYER_ADDED projects to an active-player snapshot.
        await SendTextAsync(client, $"0 7 {FullPayload("Song A", 42, 300, activeAt: 1000)}");
        Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(5)), "No active-player change after PLAYER_ADDED.");
        Assert.True(changes.TryDequeue(out WnpPlayerSnapshot? first));
        Assert.NotNull(first);
        Assert.Equal("Song A", first!.Title);
        Assert.Equal(42, first.PositionSeconds);
        Assert.Equal(WnpState.Playing, first.State);
        Assert.True(first.CanSetPosition);

        // A partial update merges and re-raises with the new position.
        var partial = new string[WnpProtocol.FieldCount];
        Array.Fill(partial, string.Empty);
        partial[WnpProtocol.FieldId] = "7";
        partial[WnpProtocol.FieldPosition] = "43";
        await SendTextAsync(client, $"1 7 {string.Join("|", partial)}|");

        Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(5)), "No change after partial PLAYER_UPDATED.");
        Assert.True(changes.TryDequeue(out WnpPlayerSnapshot? updated));
        Assert.Equal(43, updated!.PositionSeconds);
        Assert.Equal("Song A", updated.Title);   // Absent fields keep their previous value.

        // Commands flow adapter → extension in the documented format.
        Task<string> commandTask = Task.Run(() => ReceiveTextAsync(client));
        await _server.SetPositionAsync(7, 43);
        string command = await commandTask.WaitAsync(TimeSpan.FromSeconds(5));

        string[] parts = command.Split(' ');
        Assert.Equal(4, parts.Length);
        Assert.Equal("7", parts[0]);   // player id
        Assert.Equal("3", parts[2]);   // event: TRY_SET_POSITION
        Assert.Equal("43", parts[3]);  // data: target seconds

        Task<string> toggleTask = Task.Run(() => ReceiveTextAsync(client));
        await _server.SetStateAsync(7, playing: false);
        string toggle = await toggleTask.WaitAsync(TimeSpan.FromSeconds(5));

        string[] toggleParts = toggle.Split(' ');
        Assert.Equal("7", toggleParts[0]);    // player id
        Assert.Equal("0", toggleParts[2]);    // event: TRY_SET_STATE
        Assert.Equal("1", toggleParts[3]);    // data: paused (0 would be playing)

        // PLAYER_REMOVED releases the display.
        await SendTextAsync(client, "2 7");
        Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(5)), "No release after PLAYER_REMOVED.");
        Assert.True(changes.TryDequeue(out WnpPlayerSnapshot? released));
        Assert.Null(released);

        Assert.Null(_server.ActivePlayer);

        client.Abort();
    }
}
