using System.IO;
using System.Text.Json;
using MetroHub.Widgets.Catalog.MetroSync.Models;

namespace MetroHub.Widgets.Catalog.MetroSync.Services;

public enum EngineState
{
    Stopped,
    Connecting,
    Online,
    Backoff,
    Conflict,
    Unauthorized,
    Faulted
}

public sealed record TelegramBotUser(long Id, bool IsBot, string FirstName, string? Username);

public readonly record struct RawUpdate(long UpdateId, JsonElement Json);

public sealed record TelegramFileInfo(string FileId, string FileUniqueId, long? FileSize, string? FilePath);

// ── Ports ────────────────────────────────────────────────────────────────────

public interface ITelegramTransport
{
    Task<TelegramBotUser> GetMeAsync(CancellationToken ct);

    /// <param name="offset">0 means "do not confirm anything" (first call after restart).</param>
    Task<IReadOnlyList<RawUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct);

    Task<TelegramFileInfo> GetFileAsync(string fileId, CancellationToken ct);

    /// <summary>Streams to destination; throws <see cref="FileTooLargeException"/> past maxBytes.</summary>
    Task DownloadAsync(string filePath, Stream destination, long maxBytes, CancellationToken ct);
}

public interface IUpdateNormalizer
{
    /// <summary>Must never throw for any input; unsupported shapes become text notifications; /start returns null.</summary>
    MetroSyncMessageItem? Normalize(RawUpdate update, DateTimeOffset receivedAt);
}

public interface IItemIndex
{
    /// <returns>true if the item was new, false if it was already stored (redelivery).</returns>
    ValueTask<bool> UpsertAsync(MetroSyncMessageItem item, CancellationToken ct);
}

// ── Errors ───────────────────────────────────────────────────────────────────

public sealed class TelegramApiException(int errorCode, string description, int? retryAfterSeconds = null)
    : Exception($"Telegram API error {errorCode}: {description}")
{
    public int ErrorCode { get; } = errorCode;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

public sealed class FileTooLargeException(long limit) : Exception($"Payload exceeds {limit} bytes.");

// ── Iron Shield ──────────────────────────────────────────────────────────────

/// <summary>
/// Pure check on a minimal envelope. Runs BEFORE normalization, downloads, or content logging.
/// Passes only private-chat messages whose chat.id and from.id both equal the paired user.
/// </summary>
public sealed class SenderGate
{
    private readonly long _allowedUserId;

    public SenderGate(long allowedUserId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(allowedUserId);
        _allowedUserId = allowedUserId;
    }

    public bool IsAllowed(RawUpdate update)
    {
        var root = update.Json;
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return false;

        if (!message.TryGetProperty("chat", out var chat) || chat.ValueKind != JsonValueKind.Object) return false;
        if (!chat.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
            type.GetString() != "private") return false;
        if (!TryGetLong(chat, "id", out var chatId) || chatId != _allowedUserId) return false;

        if (!message.TryGetProperty("from", out var from) || from.ValueKind != JsonValueKind.Object) return false;
        if (!TryGetLong(from, "id", out var fromId) || fromId != _allowedUserId) return false;

        return true;
    }

    private static bool TryGetLong(JsonElement obj, string name, out long value)
    {
        value = 0;
        return obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out value);
    }
}

// ── Engine options & backoff ─────────────────────────────────────────────────

public static class Backoff
{
    /// <summary>
    /// Full-jitter exponential: jitter * min(cap, base * 2^(attempt-1)). attempt is 1-based.
    /// </summary>
    public static TimeSpan Delay(int attempt, double jitter, TimeSpan baseDelay, TimeSpan cap)
    {
        if (attempt < 1) attempt = 1;
        if (double.IsNaN(jitter) || jitter < 0.0) jitter = 0.0;
        else if (jitter > 1.0) jitter = 1.0;

        var exp = Math.Min(cap.TotalMilliseconds,
            baseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 30)));
        return TimeSpan.FromMilliseconds(exp * jitter);
    }
}

public sealed record ConduitEngineOptions
{
    public int PollTimeoutSeconds { get; init; } = 25;
    public TimeSpan PollWatchdog { get; init; } = TimeSpan.FromSeconds(40);
    public TimeSpan BackoffBase { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan BackoffCap { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan ConflictDelay { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public int ChannelCapacity { get; init; } = 64;
    public long InitialOffset { get; init; }

    /// <summary>Returns [0,1). Injectable so tests are deterministic.</summary>
    public Func<double> Jitter { get; init; } = static () => Random.Shared.NextDouble();
}
