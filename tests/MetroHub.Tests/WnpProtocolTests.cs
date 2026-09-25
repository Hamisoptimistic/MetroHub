using MetroHub.Core.Media.WebNowPlaying;
using Xunit;

namespace MetroHub.Tests;

/// <summary>
/// Protocol-level tests for the WebNowPlaying adapter wire format (communication revision 3),
/// covering the three things that silently break integrations: framing, partial-update merge
/// semantics, and the escaping rules for strings.
/// </summary>
public sealed class WnpProtocolTests
{
    /// <summary>Builds a full 26-field player payload exactly as the extension does.</summary>
    private static string FullPayload(
        string name = "YouTube",
        string title = "Song",
        string artist = "Artist",
        string album = "Album",
        string cover = "\u0001",
        int state = 0,
        int position = 42,
        int duration = 300,
        long activeAt = 1700000002000) =>
        $"7|{name}|{title}|{artist}|{album}|{cover}|{state}|{position}|{duration}|" +
        "100|0|0|0|1|1|1|1|1|1|0|0|0|0|1700000000000|1700000001000|" + activeAt + "|";

    /// <summary>Builds a partial payload where every unset field is an empty token (keep previous).</summary>
    private static string PartialPayload(params (int Index, string Value)[] set)
    {
        var tokens = new string[WnpProtocol.FieldCount];
        Array.Fill(tokens, string.Empty);
        foreach ((int index, string value) in set)
        {
            tokens[index] = value;
        }

        return string.Join("|", tokens) + "|";
    }

    [Fact]
    public void Handshake_AnnouncesRevision3()
    {
        Assert.Equal("ADAPTER_VERSION 1.0.0;WNPLIB_REVISION 3", WnpProtocol.BuildHandshake());
    }

    [Fact]
    public void PlayerAdded_ParsesEveryField()
    {
        bool ok = WnpProtocol.TryParseText($"0 7 {FullPayload()}", out WnpMessageType type, out int id, out string?[] fields);

        Assert.True(ok);
        Assert.Equal(WnpMessageType.PlayerAdded, type);
        Assert.Equal(7, id);

        var player = new WnpPlayer(id);
        player.ApplyFields(id, fields);

        Assert.Equal(7, player.Id);
        Assert.Equal("YouTube", player.Name);
        Assert.Equal("Song", player.Title);
        Assert.Equal("Artist", player.Artist);
        Assert.Equal("Album", player.Album);
        Assert.Equal(WnpState.Playing, player.State);
        Assert.Equal(42, player.Position);
        Assert.Equal(300, player.Duration);
        Assert.True(player.CanSetState);
        Assert.True(player.CanSkipPrevious);
        Assert.True(player.CanSkipNext);
        Assert.True(player.CanSetPosition);
        Assert.Equal(1700000002000L, player.ActiveAt);
    }

    [Fact]
    public void PlayerUpdated_PartialPayload_KeepsAbsentFields()
    {
        // Start from a full state, then apply a partial update that only moves position/duration.
        WnpPlayer player = new(7);
        Assert.True(WnpProtocol.TryParseText($"0 7 {FullPayload()}", out _, out int id, out string?[] full));
        player.ApplyFields(id, full);

        string partial = PartialPayload(
            (WnpProtocol.FieldId, "7"),
            (WnpProtocol.FieldPosition, "43"),
            (WnpProtocol.FieldDuration, "301"));
        Assert.True(WnpProtocol.TryParseText($"1 7 {partial}", out _, out id, out string?[] fields));

        Assert.Null(fields[WnpProtocol.FieldName]);   // Absent → keep.
        Assert.Equal("43", fields[WnpProtocol.FieldPosition]);

        player.ApplyFields(id, fields);

        Assert.Equal("YouTube", player.Name);         // Untouched by the partial update.
        Assert.Equal("Song", player.Title);
        Assert.Equal(43, player.Position);
        Assert.Equal(301, player.Duration);
        Assert.True(player.CanSetPosition);           // Capability flags survive too.
    }

    [Fact]
    public void Strings_EscapedPipeAndExplicitEmpty_RoundTrip()
    {
        // "A|B" travels escaped; a genuinely empty value travels as \x01.
        Assert.True(WnpProtocol.TryParseText($"0 7 {FullPayload(title: "A\\|B")}", out _, out int id, out string?[] added));
        var player = new WnpPlayer(id);
        player.ApplyFields(id, added);
        Assert.Equal("A|B", player.Title);

        Assert.True(WnpProtocol.TryParseText($"1 7 {PartialPayload((WnpProtocol.FieldTitle, "\u0001"))}", out _, out id, out string?[] cleared));
        player.ApplyFields(id, cleared);
        Assert.Equal(string.Empty, player.Title);
    }

    [Fact]
    public void PlayerRemoved_ParsesPlayerId()
    {
        Assert.True(WnpProtocol.TryParseText("2 7", out WnpMessageType type, out int id, out _));
        Assert.Equal(WnpMessageType.PlayerRemoved, type);
        Assert.Equal(7, id);
    }

    [Fact]
    public void EventResult_IsRecognisedButCarriesNoPlayer()
    {
        Assert.True(WnpProtocol.TryParseText("3 12 1", out WnpMessageType type, out int id, out _));
        Assert.Equal(WnpMessageType.EventResult, type);
        Assert.Equal(-1, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("0 7")]               // Player message without a payload separator
    [InlineData("x 7 title")]         // Non-numeric message type
    public void UnunderstoodFrames_AreRejected(string? text)
    {
        Assert.False(WnpProtocol.TryParseText(text, out _, out _, out _));
    }

    [Fact]
    public void Commands_UseTheDocumentedFormat()
    {
        Assert.Equal("7 5 3 42", WnpProtocol.BuildCommand(7, 5, WnpEvent.TrySetPosition, 42));
        Assert.Equal("7 6 0 0", WnpProtocol.BuildCommand(7, 6, WnpEvent.TrySetState, 0));     // 0 = play
        Assert.Equal("7 7 0 1", WnpProtocol.BuildCommand(7, 7, WnpEvent.TrySetState, 1));     // 1 = pause
        Assert.Equal("7 8 2 0", WnpProtocol.BuildCommand(7, 8, WnpEvent.TrySkipNext, 0));
        Assert.Equal("7 9 1 0", WnpProtocol.BuildCommand(7, 9, WnpEvent.TrySkipPrevious, 0));
    }

    [Theory]
    [InlineData("120", 120)]
    [InlineData("124.5", 125)]
    [InlineData("42.0", 42)]
    [InlineData("02:15", 135)]
    [InlineData("3:26.66", 207)]
    [InlineData("01:05:30", 3930)]
    public void PositionAndDuration_ParsesFloatsAndFormattedTimeStrings(string positionInput, int expectedSeconds)
    {
        string partial = PartialPayload(
            (WnpProtocol.FieldId, "7"),
            (WnpProtocol.FieldPosition, positionInput),
            (WnpProtocol.FieldDuration, "300"));

        Assert.True(WnpProtocol.TryParseText($"1 7 {partial}", out _, out int id, out string?[] fields));

        var player = new WnpPlayer(id);
        player.ApplyFields(id, fields);

        Assert.Equal(expectedSeconds, player.Position);
        Assert.Equal(300, player.Duration);
    }
}
