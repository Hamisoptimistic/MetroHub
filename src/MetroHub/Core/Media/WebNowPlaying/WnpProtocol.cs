using System.Globalization;
using System.Text;

namespace MetroHub.Core.Media.WebNowPlaying;

/// <summary>Message types the WebNowPlaying browser extension pushes to an adapter (revision 3).</summary>
public enum WnpMessageType
{
    PlayerAdded = 0,
    PlayerUpdated = 1,
    PlayerRemoved = 2,
    EventResult = 3,
    UseDesktopPlayers = 4,
}

/// <summary>Commands an adapter may send to the extension. Values match the extension's Events enum.</summary>
public enum WnpEvent
{
    TrySetState = 0,
    TrySkipPrevious = 1,
    TrySkipNext = 2,
    TrySetPosition = 3,
    TrySetVolume = 4,
}

/// <summary>Player transport state. Numeric values match the wire protocol (0 = playing).</summary>
public enum WnpState
{
    Playing = 0,
    Paused = 1,
    Stopped = 2,
}

/// <summary>
/// The WebNowPlaying adapter wire format, communication revision 3 — the protocol the official
/// WebNowPlaying browser extension speaks with its adapters (Rainmeter, OBS, CLI, ...).
/// </summary>
/// <remarks>
/// <para>
/// The adapter hosts a WebSocket server on a local port; the extension dials out to
/// <c>ws://127.0.0.1:&lt;port&gt;</c>. On connect the adapter must announce itself with
/// <see cref="BuildHandshake"/> or the extension assumes a legacy protocol after one second.
/// </para>
/// <para>
/// Player state arrives as text frames <c>"&lt;type&gt; &lt;playerId&gt; &lt;payload&gt;"</c>, where the
/// payload is 26 pipe-separated fields (id, name, title, artist, album, cover, state, position,
/// duration, volume, rating, repeat, shuffle, rating system, available repeats, eight capability
/// flags, then three millisecond timestamps). A pipe inside a string is escaped as <c>\|</c>; an
/// explicitly empty string is encoded as <c>\x01</c>; an <b>absent</b> field (empty token) means
/// "keep the previous value", which is how partial updates work. Cover images travel as separate
/// binary frames: a little-endian Int32 player id followed by PNG bytes.
/// </para>
/// <para>
/// Commands travel the other way as <c>"&lt;playerId&gt; &lt;eventId&gt; &lt;event&gt; &lt;data&gt;"</c>.
/// </para>
/// </remarks>
public static class WnpProtocol
{
    public const string AdapterVersion = "1.0.0";
    public const string CommunicationRevision = "3";

    /// <summary>Fixed field count of the revision-3 player payload.</summary>
    public const int FieldCount = 26;

    // Payload field indices.
    public const int FieldId = 0;
    public const int FieldName = 1;
    public const int FieldTitle = 2;
    public const int FieldArtist = 3;
    public const int FieldAlbum = 4;
    public const int FieldCover = 5;
    public const int FieldState = 6;
    public const int FieldPosition = 7;
    public const int FieldDuration = 8;
    public const int FieldCanSetState = 15;
    public const int FieldCanSkipPrevious = 16;
    public const int FieldCanSkipNext = 17;
    public const int FieldCanSetPosition = 18;
    public const int FieldCreatedAt = 23;
    public const int FieldUpdatedAt = 24;
    public const int FieldActiveAt = 25;

    private const char Separator = '|';
    private const char Escape = '\\';
    private const char EmptyValue = '\u0001';

    /// <summary>First frame the adapter must send so the extension knows which revision to speak.</summary>
    public static string BuildHandshake() =>
        $"ADAPTER_VERSION {AdapterVersion};WNPLIB_REVISION {CommunicationRevision}";

    /// <summary>Builds an adapter → extension command frame.</summary>
    public static string BuildCommand(int playerId, int eventId, WnpEvent evt, int data) =>
        string.Create(CultureInfo.InvariantCulture, $"{playerId} {eventId} {(int)evt} {data}");

    /// <summary>
    /// Parses an extension → adapter event-result frame (<c>"3 &lt;eventId&gt; &lt;result&gt;"</c>).
    /// Result matches the library: 0 = pending, 1 = succeeded, 2 = failed.
    /// </summary>
    public static bool TryParseEventResult(string? text, out int eventId, out int result)
    {
        eventId = -1;
        result = 0;
        if (string.IsNullOrEmpty(text)) return false;

        // Expected: "3 <eventId> <result>"
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int rawType)
            || rawType != (int)WnpMessageType.EventResult)
        {
            return false;
        }

        return int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out eventId)
            && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    /// <summary>
    /// Parses one extension → adapter text frame. Returns false for frames that are not understood;
    /// recognised-but-ignored frames (event results, settings) return true with a default type.
    /// </summary>
    public static bool TryParseText(string? text, out WnpMessageType type, out int id, out string?[] fields)
    {
        type = WnpMessageType.PlayerAdded;
        id = -1;
        fields = new string?[FieldCount];

        if (string.IsNullOrEmpty(text)) return false;

        int firstSpace = text.IndexOf(' ');
        if (firstSpace <= 0) return false;
        if (!int.TryParse(text.AsSpan(0, firstSpace), NumberStyles.None, CultureInfo.InvariantCulture, out int rawType))
        {
            return false;
        }

        type = (WnpMessageType)rawType;
        string rest = text.Substring(firstSpace + 1);

        switch (type)
        {
            case WnpMessageType.PlayerAdded:
            case WnpMessageType.PlayerUpdated:
            {
                int secondSpace = rest.IndexOf(' ');
                if (secondSpace <= 0) return false;
                if (!int.TryParse(rest.AsSpan(0, secondSpace), NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                {
                    return false;
                }

                SplitFields(rest.AsSpan(secondSpace + 1), fields);
                return true;
            }

            case WnpMessageType.PlayerRemoved:
                return int.TryParse(rest.AsSpan(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id);

            default:
                return true;
        }
    }

    /// <summary>Splits the pipe-separated payload, honouring <c>\|</c> escapes.</summary>
    private static void SplitFields(ReadOnlySpan<char> payload, string?[] fields)
    {
        var token = new StringBuilder();
        int index = 0;

        for (int i = 0; i < payload.Length && index < FieldCount; i++)
        {
            char c = payload[i];
            if (c == Escape && i + 1 < payload.Length && payload[i + 1] == Separator)
            {
                token.Append(Separator);
                i++;
            }
            else if (c == Separator)
            {
                fields[index++] = CommitToken(token);
                token.Clear();
            }
            else
            {
                token.Append(c);
            }
        }

        // A payload without a trailing separator still carries a final field.
        if (index < FieldCount && token.Length > 0)
        {
            fields[index] = CommitToken(token);
        }
    }

    /// <summary>
    /// An empty token means the field was absent from a partial update → keep the previous value.
    /// The explicit <c>\x01</c> marker is how the extension encodes a genuinely empty string.
    /// </summary>
    private static string? CommitToken(StringBuilder token)
    {
        if (token.Length == 0) return null;
        if (token.Length == 1 && token[0] == EmptyValue) return string.Empty;
        return token.ToString();
    }
}
