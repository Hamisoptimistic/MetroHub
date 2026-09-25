namespace MetroHub.Core.Media.WebNowPlaying;

/// <summary>
/// One player reported by the WebNowPlaying extension. Mutable on purpose: revision 3 sends partial
/// updates (absent fields keep their previous value), so state is folded in message by message.
/// </summary>
public sealed class WnpPlayer
{
    public WnpPlayer(int id) => Id = id;

    public int Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string Artist { get; private set; } = string.Empty;
    public string Album { get; private set; } = string.Empty;
    public WnpState State { get; private set; } = WnpState.Paused;

    /// <summary>Position in whole seconds — that is all the extension reports.</summary>
    public int Position { get; private set; }

    /// <summary>Duration in whole seconds; zero when the site does not declare one (live streams).</summary>
    public int Duration { get; private set; }

    public bool CanSetState { get; private set; }
    public bool CanSkipPrevious { get; private set; }
    public bool CanSkipNext { get; private set; }
    public bool CanSetPosition { get; private set; }
    public long ActiveAt { get; private set; }

    /// <summary>Folds one parsed player message into this instance (absent fields keep their value).</summary>
    public void ApplyFields(int id, string?[] fields)
    {
        Id = id;

        for (int i = 0; i < fields.Length && i < WnpProtocol.FieldCount; i++)
        {
            string? value = fields[i];
            if (value is null) continue;   // Absent from a partial update.

            switch (i)
            {
                case WnpProtocol.FieldId:
                    break;   // The standalone id token right after the message type is authoritative.
                case WnpProtocol.FieldName:
                    Name = value;
                    break;
                case WnpProtocol.FieldTitle:
                    Title = value;
                    break;
                case WnpProtocol.FieldArtist:
                    Artist = value;
                    break;
                case WnpProtocol.FieldAlbum:
                    Album = value;
                    break;
                case WnpProtocol.FieldCover:
                    break;   // Revision 3 delivers covers as binary frames; the text slot stays empty.
                case WnpProtocol.FieldState:
                    if (TryParseInt(value, out int state) && state is >= 0 and <= 2)
                    {
                        State = (WnpState)state;
                    }
                    break;
                case WnpProtocol.FieldPosition:
                    if (TryParseSeconds(value, out int position)) Position = Math.Max(0, position);
                    break;
                case WnpProtocol.FieldDuration:
                    if (TryParseSeconds(value, out int duration)) Duration = Math.Max(0, duration);
                    break;
                case WnpProtocol.FieldCanSetState:
                    CanSetState = value == "1";
                    break;
                case WnpProtocol.FieldCanSkipPrevious:
                    CanSkipPrevious = value == "1";
                    break;
                case WnpProtocol.FieldCanSkipNext:
                    CanSkipNext = value == "1";
                    break;
                case WnpProtocol.FieldCanSetPosition:
                    CanSetPosition = value == "1";
                    break;
                case WnpProtocol.FieldActiveAt:
                    if (TryParseLong(value, out long activeAt)) ActiveAt = activeAt;
                    break;
                default:
                    break;   // Volume, rating, repeat, shuffle, timestamps: parsed, but not used by the widget.
            }
        }
    }

    /// <summary>Immutable view handed to subscribers; value-equal so unchanged state never re-raises.</summary>
    public WnpPlayerSnapshot ToSnapshot() => new(
        Id, Name, Title, Artist, Album, State, Position, Duration,
        CanSetState, CanSkipPrevious, CanSkipNext, CanSetPosition, ActiveAt);

    private static bool TryParseSeconds(string value, out int result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;

        // 1. Whole integer seconds (standard rev3)
        if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int sec))
        {
            result = sec;
            return true;
        }

        // 2. Floating-point seconds (e.g. "124.5", "42.0")
        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double dSec))
        {
            result = (int)Math.Round(dSec, MidpointRounding.AwayFromZero);
            return true;
        }

        // 3. Formatted time "MM:SS" or "HH:MM:SS"
        if (value.Contains(':'))
        {
            string[] parts = value.Split(':');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int m) &&
                double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s))
            {
                result = (int)Math.Round(m * 60 + s, MidpointRounding.AwayFromZero);
                return true;
            }
            if (parts.Length == 3 &&
                int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int h) &&
                int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out m) &&
                double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out s))
            {
                result = (int)Math.Round(h * 3600 + m * 60 + s, MidpointRounding.AwayFromZero);
                return true;
            }
        }

        return false;
    }

    private static bool TryParseInt(string value, out int result) =>
        int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out result);

    private static bool TryParseLong(string value, out long result) =>
        long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out result);
}

/// <summary>
/// Immutable snapshot of a player at one instant. A record so the server can suppress
/// <c>ActivePlayerChanged</c> when nothing an observer could care about actually moved.
/// </summary>
public sealed record WnpPlayerSnapshot(
    int Id,
    string Name,
    string Title,
    string Artist,
    string Album,
    WnpState State,
    int PositionSeconds,
    int DurationSeconds,
    bool CanSetState,
    bool CanSkipPrevious,
    bool CanSkipNext,
    bool CanSetPosition,
    long ActiveAt);
