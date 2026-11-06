using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MetroHub.Widgets.Catalog.MetroSync.Services;

public enum PairingStatus
{
    Success,
    IncorrectPin,
    Expired,
    RateLimited,
    Cancelled,
    Failed
}

public sealed record PairingResult(
    PairingStatus Status,
    long? UserId = null,
    string? Username = null,
    string? FirstName = null,
    string? ErrorMessage = null)
{
    public bool IsSuccess => Status == PairingStatus.Success;

    public static PairingResult Succeeded(long userId, string? username, string? firstName) =>
        new(PairingStatus.Success, userId, username, firstName);

    public static PairingResult IncorrectPinResult(string? message = null) =>
        new(PairingStatus.IncorrectPin, ErrorMessage: message ?? "Incorrect pairing PIN.");

    public static PairingResult ExpiredResult(string? message = null) =>
        new(PairingStatus.Expired, ErrorMessage: message ?? "Pairing PIN has expired.");

    public static PairingResult RateLimitedResult(string? message = null) =>
        new(PairingStatus.RateLimited, ErrorMessage: message ?? "Too many failed pairing attempts.");

    public static PairingResult CancelledResult() =>
        new(PairingStatus.Cancelled, ErrorMessage: "Pairing was cancelled.");

    public static PairingResult FailedResult(string message) =>
        new(PairingStatus.Failed, ErrorMessage: message);

    public void EnsureSuccess()
    {
        if (Status == PairingStatus.Success) return;
        if (Status == PairingStatus.Cancelled) throw new OperationCanceledException(ErrorMessage ?? "Pairing was cancelled.");
        if (Status == PairingStatus.Expired) throw new TimeoutException(ErrorMessage ?? "Pairing PIN has expired.");
        if (Status == PairingStatus.RateLimited) throw new InvalidOperationException(ErrorMessage ?? "Rate limit exceeded.");
        if (Status == PairingStatus.IncorrectPin) throw new InvalidOperationException(ErrorMessage ?? "Incorrect pairing PIN.");
        throw new InvalidOperationException(ErrorMessage ?? "Pairing failed.");
    }
}

public sealed class PairingAttemptFailedEventArgs(
    long userId,
    string? username,
    string? enteredCode,
    int attemptNumber,
    int maxAttempts) : EventArgs
{
    public long UserId { get; } = userId;
    public string? Username { get; } = username;
    public string? EnteredCode { get; } = enteredCode;
    public int AttemptNumber { get; } = attemptNumber;
    public int MaxAttempts { get; } = maxAttempts;
}

public enum PairingStepKind
{
    Ignored,
    IncorrectPin,
    Success
}

public sealed record PairingStepResult(
    PairingStepKind Kind,
    long UserId = 0,
    long ChatId = 0,
    string? Username = null,
    string? FirstName = null,
    string? EnteredCode = null)
{
    public static PairingStepResult Success(long userId, long chatId, string? username, string? firstName) =>
        new(PairingStepKind.Success, userId, chatId, username, firstName);

    public static PairingStepResult IncorrectPin(long userId, string? username, string? enteredCode) =>
        new(PairingStepKind.IncorrectPin, userId, 0, username, null, enteredCode);

    public static PairingStepResult Ignored() =>
        new(PairingStepKind.Ignored);
}

public sealed record CandidateSender(
    long UserId,
    long ChatId,
    string? Username,
    string? FirstName)
{
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(FirstName)
            ? FirstName
            : !string.IsNullOrWhiteSpace(Username)
                ? "@" + Username
                : UserId.ToString(CultureInfo.InvariantCulture);
}

public sealed record MetroSyncPairingOptions
{
    public TimeSpan ExpiryTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public int MaxFailedAttempts { get; init; } = 5;
    public int PollTimeoutSeconds { get; init; } = 2;
    public TimeSpan PollDelay { get; init; } = TimeSpan.FromMilliseconds(50);
    public string ConfirmationMessage { get; init; } = "MetroSync paired successfully with your desktop.";
}

/// <summary>
/// Manages secure PIN generation, handshake validation via Telegram Bot API long-polling,
/// 10-minute expiry enforcement, rate limiting on failed attempts, and credentials persistence.
/// </summary>
public sealed class MetroSyncPairingService
{
    private readonly MetroSyncSecurity _security;
    private readonly TimeProvider _timeProvider;
    private readonly MetroSyncPairingOptions _options;

    public string? ActivePin { get; private set; }
    public DateTimeOffset? PinCreatedAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public int FailedAttempts { get; private set; }
    public MetroSyncPairingOptions Options => _options;
    public MetroSyncSecurity Security => _security;

    public bool IsExpired =>
        PinCreatedAt.HasValue && (_timeProvider.GetUtcNow() - PinCreatedAt.Value >= _options.ExpiryTimeout);

    public event EventHandler<PairingAttemptFailedEventArgs>? AttemptFailed;

    public MetroSyncPairingService(
        MetroSyncSecurity? security = null,
        TimeProvider? timeProvider = null,
        MetroSyncPairingOptions? options = null)
    {
        _security = security ?? MetroSyncSecurity.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new MetroSyncPairingOptions();
    }

    /// <summary>
    /// Generates a cryptographically secure 6-digit numeric pairing PIN using <see cref="RandomNumberGenerator"/>.
    /// Range: 100000 - 999999.
    /// </summary>
    public static string GeneratePin()
    {
        int code = RandomNumberGenerator.GetInt32(100_000, 1_000_000);
        return code.ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Validates bot token with getMe. Throws TelegramApiException on 401 / 409.
    /// </summary>
    public static async Task<TelegramBotUser> ValidateTokenAsync(ITelegramTransport transport, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        return await transport.GetMeAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates bot token by creating a dedicated client.
    /// </summary>
    public static async Task<TelegramBotUser> ValidateTokenAsync(string botToken, Uri? apiBase = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botToken);
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = new TelegramHttpTransport(http, apiBase ?? new Uri("https://api.telegram.org"), botToken);
        return await ValidateTokenAsync(transport, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Manually saves pairing credentials into DPAPI vault for the manual ID fallback flow.
    /// </summary>
    public void SaveManualPairing(string botToken, long allowedUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botToken);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(allowedUserId);
        _security.SaveCredentials(botToken, allowedUserId);
    }

    /// <summary>
    /// Extracts a pairing code from message text. Accepts "/start &lt;code&gt;", "/start@bot &lt;code&gt;",
    /// formatted "/start 123 456", or direct 6-digit numeric PINs (with optional spaces/hyphens).
    /// Returns false if the message has no pairing code.
    /// </summary>
    public static bool TryExtractCode(string? text, [NotNullWhen(true)] out string? code)
    {
        code = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        if (trimmed.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                // Plain "/start" or "/start@bot" with no code attached
                return false;
            }

            // Supports "/start 123456", "/start 123 456", "/start 123-456"
            var argument = string.Join("", parts.Skip(1)).Replace("-", "");
            if (argument.Length == 6 && argument.All(char.IsAsciiDigit))
            {
                code = argument;
                return true;
            }

            code = parts[1].Trim();
            return true;
        }

        // Direct PIN entry: "123456", "123 456", "123-456"
        var normalized = trimmed.Replace(" ", "").Replace("-", "");
        if (normalized.Length == 6 && normalized.All(char.IsAsciiDigit))
        {
            code = normalized;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Validates a candidate PIN against the active PIN using constant-time comparison.
    /// Returns false if the candidate does not match or if the PIN has expired.
    /// </summary>
    public bool ValidatePin(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(ActivePin))
            return false;

        if (IsExpired)
            return false;

        var spanCandidate = candidate.Trim().AsSpan();
        var spanActive = ActivePin.Trim().AsSpan();

        if (spanCandidate.Length != spanActive.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            MemoryMarshal.AsBytes(spanCandidate),
            MemoryMarshal.AsBytes(spanActive));
    }

    /// <summary>
    /// Evaluates an incoming update against the active PIN.
    /// Non-private messages or updates without a pairing code are ignored without penalty.
    /// </summary>
    public PairingStepResult ProcessUpdate(RawUpdate update)
    {
        if (IsExpired)
        {
            return PairingStepResult.Ignored();
        }

        if (!TryExtractPrivateMessage(update, out var fromId, out var chatId, out var username, out var firstName, out var text))
        {
            return PairingStepResult.Ignored();
        }

        if (!TryExtractCode(text, out var receivedCode))
        {
            return PairingStepResult.Ignored();
        }

        if (FailedAttempts >= _options.MaxFailedAttempts)
        {
            return PairingStepResult.IncorrectPin(fromId, username, receivedCode);
        }

        if (ValidatePin(receivedCode))
        {
            return PairingStepResult.Success(fromId, chatId, username, firstName);
        }

        FailedAttempts++;
        AttemptFailed?.Invoke(this, new PairingAttemptFailedEventArgs(
            fromId,
            username,
            receivedCode,
            FailedAttempts,
            _options.MaxFailedAttempts));

        return PairingStepResult.IncorrectPin(fromId, username, receivedCode);
    }

    /// <summary>
    /// Runs a short-lived loop polling Telegram getUpdates for /start &lt;code&gt;.
    /// Validates received code against active PIN, sends confirmation message,
    /// stores credentials in DPAPI vault, and enforces 10-minute expiry timeout and rate limiting.
    /// </summary>
    public async Task<PairingResult> WaitForPairingAsync(
        ITelegramTransport transport,
        string botToken,
        string? pin = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentException.ThrowIfNullOrWhiteSpace(botToken);

        ActivePin = pin ?? GeneratePin();
        PinCreatedAt = _timeProvider.GetUtcNow();
        ExpiresAt = PinCreatedAt.Value + _options.ExpiryTimeout;
        FailedAttempts = 0;

        var seenUpdates = new HashSet<long>();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_timeProvider.GetUtcNow() >= ExpiresAt)
                {
                    return PairingResult.ExpiredResult("Pairing PIN has expired.");
                }

                if (FailedAttempts >= _options.MaxFailedAttempts)
                {
                    return PairingResult.RateLimitedResult("Too many failed pairing attempts.");
                }

                IReadOnlyList<RawUpdate> updates;
                try
                {
                    // Pairing mode never acknowledges updates (offset = 0)
                    updates = await transport.GetUpdatesAsync(0, _options.PollTimeoutSeconds, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return PairingResult.CancelledResult();
                }
                catch (TelegramApiException ex) when (ex.ErrorCode == 409)
                {
                    return PairingResult.FailedResult("Another app or webhook is already using this bot.");
                }
                catch (TelegramApiException ex) when (ex.ErrorCode == 401)
                {
                    return PairingResult.FailedResult("Token not valid.");
                }
                catch (TelegramApiException ex) when (ex.ErrorCode == 429)
                {
                    var delaySeconds = ex.RetryAfterSeconds ?? 1;
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), _timeProvider, ct).ConfigureAwait(false);
                    continue;
                }
                catch (TelegramApiException ex) when (ex.ErrorCode >= 500)
                {
                    // Transient server error (e.g. 502 Bad Gateway)
                    var delay = _options.PollDelay > TimeSpan.Zero ? _options.PollDelay : TimeSpan.FromMilliseconds(50);
                    await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);
                    continue;
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    // Transient network / socket / I/O blip
                    var delay = _options.PollDelay > TimeSpan.Zero ? _options.PollDelay : TimeSpan.FromMilliseconds(50);
                    await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);
                    continue;
                }

                foreach (var update in updates)
                {
                    if (!seenUpdates.Add(update.UpdateId))
                    {
                        continue;
                    }

                    if (_timeProvider.GetUtcNow() >= ExpiresAt)
                    {
                        return PairingResult.ExpiredResult("Pairing PIN has expired.");
                    }

                    var step = ProcessUpdate(update);
                    if (step.Kind == PairingStepKind.Success)
                    {
                        // Send confirmation message via Telegram Bot API on valid handshake
                        try
                        {
                            using var doc = await transport.SendMessageAsync(step.ChatId, _options.ConfirmationMessage, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            return PairingResult.CancelledResult();
                        }
                        catch (Exception)
                        {
                            // Best-effort confirmation: if Telegram fails to acknowledge outbound confirmation,
                            // do not abort or crash an already verified handshake.
                        }

                        // Store verified credentials in MetroSyncSecurity vault (credentials.dat)
                        _security.SaveCredentials(botToken, step.UserId);

                        return PairingResult.Succeeded(step.UserId, step.Username, step.FirstName);
                    }

                    if (FailedAttempts >= _options.MaxFailedAttempts)
                    {
                        return PairingResult.RateLimitedResult("Too many failed pairing attempts.");
                    }
                }

                if (_options.PollDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_options.PollDelay, _timeProvider, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return PairingResult.CancelledResult();
        }

        return PairingResult.CancelledResult();
    }

    /// <summary>
    /// Long-polls getUpdates in pairing mode (offset = 0, never acknowledges updates)
    /// looking for any private message from a sender not present in <paramref name="ignoredUserIds"/>.
    /// Returns the discovered candidate sender.
    /// </summary>
    public async Task<CandidateSender?> WaitForCandidateSenderAsync(
        ITelegramTransport transport,
        HashSet<long>? ignoredUserIds = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transport);

        while (!ct.IsCancellationRequested)
        {
            IReadOnlyList<RawUpdate> updates;
            try
            {
                // Pairing mode never acknowledges updates (offset = 0)
                updates = await transport.GetUpdatesAsync(0, _options.PollTimeoutSeconds, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch (TelegramApiException ex) when (ex.ErrorCode == 429)
            {
                var delaySeconds = ex.RetryAfterSeconds ?? 1;
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), _timeProvider, ct).ConfigureAwait(false);
                continue;
            }
            catch (TelegramApiException ex) when (ex.ErrorCode >= 500)
            {
                var delay = _options.PollDelay > TimeSpan.Zero ? _options.PollDelay : TimeSpan.FromMilliseconds(50);
                await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);
                continue;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                var delay = _options.PollDelay > TimeSpan.Zero ? _options.PollDelay : TimeSpan.FromMilliseconds(50);
                await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);
                continue;
            }

            foreach (var update in updates)
            {
                if (TryExtractPrivateMessage(update, out var fromId, out var chatId, out var username, out var firstName, out _))
                {
                    if (ignoredUserIds != null && ignoredUserIds.Contains(fromId))
                    {
                        continue;
                    }

                    return new CandidateSender(fromId, chatId, username, firstName);
                }
            }

            if (_options.PollDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.PollDelay, _timeProvider, ct).ConfigureAwait(false);
            }
        }

        return null;
    }

    private static bool IsTransient(Exception e) =>
        e is HttpRequestException or IOException or SocketException or TimeoutException
            or JsonException
        || (e.InnerException is not null && IsTransient(e.InnerException));

    private static bool TryExtractPrivateMessage(
        RawUpdate update,
        out long fromId,
        out long chatId,
        out string? username,
        out string? firstName,
        out string? text)
    {
        fromId = 0;
        chatId = 0;
        username = null;
        firstName = null;
        text = null;

        var root = update.Json;
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return false;

        if (!message.TryGetProperty("chat", out var chat) || chat.ValueKind != JsonValueKind.Object) return false;
        if (!chat.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
            !string.Equals(type.GetString(), "private", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!chat.TryGetProperty("id", out var chatIdProp) || !chatIdProp.TryGetInt64(out chatId)) return false;

        if (!message.TryGetProperty("from", out var from) || from.ValueKind != JsonValueKind.Object) return false;
        if (!from.TryGetProperty("id", out var fromIdProp) || !fromIdProp.TryGetInt64(out fromId)) return false;

        if (from.TryGetProperty("username", out var u) && u.ValueKind == JsonValueKind.String)
            username = u.GetString();

        if (from.TryGetProperty("first_name", out var fn) && fn.ValueKind == JsonValueKind.String)
            firstName = fn.GetString();

        if (message.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            text = t.GetString();

        return true;
    }
}
