using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Tests;
using MetroHub.Tests.Widgets.MetroSync.TestKit;
using MetroHub.Widgets.Catalog.MetroSync.Services;
using Xunit;

namespace MetroHub.Tests.Widgets.MetroSync;

[Collection("MetroSyncTests")]
public sealed class MetroSyncPairingServiceTests
{
    private static TelegramHttpTransport Http(FakeTelegramServer server, HttpClient client) =>
        new(client, server.BaseAddress, FakeTelegramServer.Token, allowInsecureLoopbackForTests: true);

    [Fact]
    public void GeneratePin_ReturnsValidSixDigitNumericPin()
    {
        for (var i = 0; i < 50; i++)
        {
            var pin = MetroSyncPairingService.GeneratePin();
            Assert.NotNull(pin);
            Assert.Equal(6, pin.Length);
            Assert.All(pin, c => Assert.True(char.IsAsciiDigit(c)));
            int value = int.Parse(pin, System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(value, 100_000, 999_999);
        }
    }

    [Theory]
    [InlineData("/start 123456", "123456")]
    [InlineData("/start@MyBot 654321", "654321")]
    [InlineData("/start   789012  ", "789012")]
    [InlineData("/start 123 456", "123456")]
    [InlineData("/start 123-456", "123456")]
    [InlineData("998877", "998877")]
    [InlineData("123 456", "123456")]
    [InlineData("123-456", "123456")]
    public void TryExtractCode_ValidInputs_ExtractsCorrectCode(string input, string expectedCode)
    {
        bool success = MetroSyncPairingService.TryExtractCode(input, out var code);
        Assert.True(success);
        Assert.Equal(expectedCode, code);
    }

    [Theory]
    [InlineData("/start")]
    [InlineData("/start@MyBot")]
    [InlineData("hello world")]
    [InlineData("123")]
    [InlineData("1234567")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryExtractCode_InvalidInputs_ReturnsFalse(string? input)
    {
        bool success = MetroSyncPairingService.TryExtractCode(input, out var code);
        Assert.False(success);
        Assert.Null(code);
    }

    [Fact]
    public async Task SuccessfulPairing_CompletesHandshake_SavesCredentials_AndSendsConfirmation()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);
        await using var server = await FakeTelegramServer.StartAsync();
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);

        var options = new MetroSyncPairingOptions
        {
            PollTimeoutSeconds = 1,
            PollDelay = TimeSpan.FromMilliseconds(10),
            ConfirmationMessage = "MetroSync paired successfully."
        };
        var service = new MetroSyncPairingService(security, TimeProvider.System, options);

        const string pin = "654321";
        const long userId = 88776655;

        var pairingTask = service.WaitForPairingAsync(transport, FakeTelegramServer.Token, pin, CancellationToken.None);

        // Enqueue pairing message from user via deep link /start 654321
        server.Enqueue(UpdateBuilder.Text(fromId: userId, text: $"/start {pin}", chatType: "private"));

        var result = await pairingTask;

        Assert.NotNull(result);
        Assert.Equal(PairingStatus.Success, result.Status);
        Assert.True(result.IsSuccess);
        Assert.Equal(userId, result.UserId);

        // Verify Telegram confirmation message was sent
        Assert.Equal(1, server.Requests("sendMessage"));

        // Verify DPAPI credentials vault stored the verified pairing
        bool loaded = security.TryLoadCredentials(out var credentials);
        Assert.True(loaded);
        Assert.NotNull(credentials);
        Assert.Equal(FakeTelegramServer.Token, credentials.BotToken);
        Assert.Equal(userId, credentials.AllowedUserId);
    }

    [Fact]
    public async Task SuccessfulPairing_RawPinMessage_CompletesHandshake()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);
        await using var server = await FakeTelegramServer.StartAsync();
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);

        var options = new MetroSyncPairingOptions
        {
            PollTimeoutSeconds = 1,
            PollDelay = TimeSpan.FromMilliseconds(10)
        };
        var service = new MetroSyncPairingService(security, TimeProvider.System, options);

        const string pin = "112233";
        const long userId = 99881122;

        var pairingTask = service.WaitForPairingAsync(transport, FakeTelegramServer.Token, pin, CancellationToken.None);

        // User typed just the 6-digit PIN directly in private chat
        server.Enqueue(UpdateBuilder.Text(fromId: userId, text: pin, chatType: "private"));

        var result = await pairingTask;

        Assert.True(result.IsSuccess);
        Assert.Equal(userId, result.UserId);
        Assert.Equal(1, server.Requests("sendMessage"));

        Assert.True(security.TryLoadCredentials(out var credentials));
        Assert.Equal(userId, credentials.AllowedUserId);
    }

    [Fact]
    public async Task SuccessfulPairing_FormattedPin_WithSpacesAndHyphens_Succeeds()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);
        await using var server = await FakeTelegramServer.StartAsync();
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);

        var options = new MetroSyncPairingOptions
        {
            PollTimeoutSeconds = 1,
            PollDelay = TimeSpan.FromMilliseconds(10)
        };
        var service = new MetroSyncPairingService(security, TimeProvider.System, options);

        const string pin = "556677";
        const long userId = 44332211;

        var pairingTask = service.WaitForPairingAsync(transport, FakeTelegramServer.Token, pin, CancellationToken.None);

        // User sent formatted input "/start 556 677"
        server.Enqueue(UpdateBuilder.Text(fromId: userId, text: "/start 556 677", chatType: "private"));

        var result = await pairingTask;

        Assert.True(result.IsSuccess);
        Assert.Equal(userId, result.UserId);
        Assert.True(security.TryLoadCredentials(out var credentials));
        Assert.Equal(userId, credentials.AllowedUserId);
    }

    [Fact]
    public async Task NonCodeMessages_InQueue_AreIgnored_AndDoNotIncrementFailedAttempts()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);
        await using var server = await FakeTelegramServer.StartAsync();
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);

        var options = new MetroSyncPairingOptions
        {
            PollTimeoutSeconds = 1,
            PollDelay = TimeSpan.FromMilliseconds(10),
            MaxFailedAttempts = 3 // Would fail if non-code messages were counted!
        };
        var service = new MetroSyncPairingService(security, TimeProvider.System, options);

        const string pin = "321654";
        const long userId = 99118822;

        var pairingTask = service.WaitForPairingAsync(transport, FakeTelegramServer.Token, pin, CancellationToken.None);

        // Enqueue 5 non-code messages (simulating chat history or user clicking around)
        server.Enqueue(UpdateBuilder.Text(fromId: userId, text: "Hello bot", chatType: "private"));
        server.Enqueue(UpdateBuilder.Photo(fromId: userId, caption: "Check this photo"));
        server.Enqueue(UpdateBuilder.Text(fromId: userId, text: "/start", chatType: "private")); // Plain /start without code
        server.Enqueue(UpdateBuilder.Text(fromId: userId, text: "How does pairing work?", chatType: "private"));
        server.Enqueue(UpdateBuilder.Text(fromId: userId, text: "1234567", chatType: "private")); // 7 digits (not PIN)

        // Then enqueue the actual valid pairing code
        server.Enqueue(UpdateBuilder.Text(fromId: userId, text: $"/start {pin}", chatType: "private"));

        var result = await pairingTask;

        Assert.True(result.IsSuccess);
        Assert.Equal(userId, result.UserId);
        Assert.Equal(0, service.FailedAttempts); // Non-code messages did NOT penalize the user!
        Assert.True(security.TryLoadCredentials(out var credentials));
        Assert.Equal(userId, credentials.AllowedUserId);
    }

    [Fact]
    public async Task SuccessfulPairing_WhenSendMessageFails_StillSavesCredentialsAndSucceeds()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);
        await using var server = await FakeTelegramServer.StartAsync();

        // Inject 502 Bad Gateway chaos on sendMessage
        server.Chaos.Script("sendMessage", new RespondStatus(502, "Bad Gateway"));

        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);

        var options = new MetroSyncPairingOptions
        {
            PollTimeoutSeconds = 1,
            PollDelay = TimeSpan.FromMilliseconds(10)
        };
        var service = new MetroSyncPairingService(security, TimeProvider.System, options);

        const string pin = "778899";
        const long userId = 66554433;

        var pairingTask = service.WaitForPairingAsync(transport, FakeTelegramServer.Token, pin, CancellationToken.None);

        server.Enqueue(UpdateBuilder.Text(fromId: userId, text: $"/start {pin}", chatType: "private"));

        var result = await pairingTask;

        // Even though Telegram failed to send the outbound confirmation message, handshake is valid and saved!
        Assert.True(result.IsSuccess);
        Assert.Equal(userId, result.UserId);
        Assert.True(security.TryLoadCredentials(out var credentials));
        Assert.Equal(userId, credentials.AllowedUserId);
    }

    [Fact]
    public async Task Pairing_WhenGetUpdatesFailsTransiently_RetriesAndRecovers()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);
        await using var server = await FakeTelegramServer.StartAsync();

        // First poll returns 502 Bad Gateway
        server.Chaos.Script("getUpdates", new RespondStatus(502, "Bad Gateway"));

        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);

        var options = new MetroSyncPairingOptions
        {
            PollTimeoutSeconds = 1,
            PollDelay = TimeSpan.FromMilliseconds(10)
        };
        var service = new MetroSyncPairingService(security, TimeProvider.System, options);

        const string pin = "445566";
        const long userId = 11223344;

        var pairingTask = service.WaitForPairingAsync(transport, FakeTelegramServer.Token, pin, CancellationToken.None);

        // After the first 502 failure, second poll will receive this update
        server.Enqueue(UpdateBuilder.Text(fromId: userId, text: $"/start {pin}", chatType: "private"));

        var result = await pairingTask;

        Assert.True(result.IsSuccess);
        Assert.Equal(userId, result.UserId);
        Assert.True(security.TryLoadCredentials(out _));
    }

    [Fact]
    public async Task IncorrectPin_IncrementsFailedAttempts_AndFailsWhenMaxAttemptsReached()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);
        await using var server = await FakeTelegramServer.StartAsync();
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);

        var options = new MetroSyncPairingOptions
        {
            PollTimeoutSeconds = 1,
            PollDelay = TimeSpan.FromMilliseconds(10),
            MaxFailedAttempts = 1
        };
        var service = new MetroSyncPairingService(security, TimeProvider.System, options);

        const string pin = "334455";
        const long strangerId = 12345;

        var pairingTask = service.WaitForPairingAsync(transport, FakeTelegramServer.Token, pin, CancellationToken.None);

        // Stranger attempts pairing with incorrect PIN
        server.Enqueue(UpdateBuilder.Text(fromId: strangerId, text: "/start 999999", chatType: "private"));

        var result = await pairingTask;

        Assert.False(result.IsSuccess);
        Assert.Equal(PairingStatus.RateLimited, result.Status);
        Assert.Equal(1, service.FailedAttempts);

        // Verify credentials NOT saved
        Assert.False(security.TryLoadCredentials(out _));

        // Verify no confirmation message sent
        Assert.Equal(0, server.Requests("sendMessage"));
    }

    [Fact]
    public async Task RateLimiting_MultipleFailedAttempts_LocksOut()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);
        await using var server = await FakeTelegramServer.StartAsync();
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);

        var options = new MetroSyncPairingOptions
        {
            PollTimeoutSeconds = 1,
            PollDelay = TimeSpan.FromMilliseconds(10),
            MaxFailedAttempts = 3
        };
        var service = new MetroSyncPairingService(security, TimeProvider.System, options);

        int eventCount = 0;
        service.AttemptFailed += (_, e) =>
        {
            eventCount++;
            Assert.Equal(eventCount, e.AttemptNumber);
            Assert.Equal(3, e.MaxAttempts);
        };

        const string pin = "123456";

        var pairingTask = service.WaitForPairingAsync(transport, FakeTelegramServer.Token, pin, CancellationToken.None);

        // Enqueue 3 failed attempts
        server.Enqueue(UpdateBuilder.Text(fromId: 101, text: "/start 000001", chatType: "private"));
        server.Enqueue(UpdateBuilder.Text(fromId: 102, text: "/start 000002", chatType: "private"));
        server.Enqueue(UpdateBuilder.Text(fromId: 103, text: "/start 000003", chatType: "private"));

        var result = await pairingTask;

        Assert.Equal(PairingStatus.RateLimited, result.Status);
        Assert.Equal(3, eventCount);
        Assert.Equal(3, service.FailedAttempts);
        Assert.False(security.TryLoadCredentials(out _));
        Assert.Equal(0, server.Requests("sendMessage"));
    }

    [Fact]
    public async Task ExpiredCode_ReturnsExpired_WhenTimeoutElapses()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);
        await using var server = await FakeTelegramServer.StartAsync();
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);

        var options = new MetroSyncPairingOptions
        {
            PollTimeoutSeconds = 0,
            PollDelay = TimeSpan.FromMilliseconds(5),
            ExpiryTimeout = TimeSpan.FromMilliseconds(50)
        };
        var service = new MetroSyncPairingService(security, TimeProvider.System, options);

        var result = await service.WaitForPairingAsync(transport, FakeTelegramServer.Token, "123456", CancellationToken.None);

        Assert.Equal(PairingStatus.Expired, result.Status);
        Assert.False(result.IsSuccess);
        Assert.True(service.IsExpired);
        Assert.False(security.TryLoadCredentials(out _));
    }

    [Fact]
    public async Task Cancellation_AbortsPollingCleanly()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);
        await using var server = await FakeTelegramServer.StartAsync();
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled

        var options = new MetroSyncPairingOptions { PollTimeoutSeconds = 1 };
        var service = new MetroSyncPairingService(security, TimeProvider.System, options);

        var result = await service.WaitForPairingAsync(transport, FakeTelegramServer.Token, "123456", cts.Token);

        Assert.Equal(PairingStatus.Cancelled, result.Status);
        Assert.False(result.IsSuccess);
        Assert.False(security.TryLoadCredentials(out _));
    }

    [Fact]
    public async Task NonPrivateChat_IsIgnored()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);
        await using var server = await FakeTelegramServer.StartAsync();
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);

        var options = new MetroSyncPairingOptions
        {
            PollTimeoutSeconds = 1,
            PollDelay = TimeSpan.FromMilliseconds(10)
        };
        var service = new MetroSyncPairingService(security, TimeProvider.System, options);

        const string pin = "889900";
        const long privateUser = 554433;

        var pairingTask = service.WaitForPairingAsync(transport, FakeTelegramServer.Token, pin, CancellationToken.None);

        // Enqueue group message with the PIN - should be ignored
        server.Enqueue(UpdateBuilder.Text(fromId: 9999, text: $"/start {pin}", chatType: "group", chatId: -100123456));

        // Then enqueue legitimate private message
        server.Enqueue(UpdateBuilder.Text(fromId: privateUser, text: $"/start {pin}", chatType: "private"));

        var result = await pairingTask;

        Assert.True(result.IsSuccess);
        Assert.Equal(privateUser, result.UserId);
        Assert.Equal(0, service.FailedAttempts); // Group message didn't trigger failed attempt
        Assert.Equal(1, server.Requests("sendMessage"));
    }

    [Fact]
    public void ManualPairingFallback_SavesCredentialsCorrectly()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);
        var service = new MetroSyncPairingService(security);

        const string botToken = "BOT_TOKEN_MANUAL_123";
        const long userId = 778899;

        service.SaveManualPairing(botToken, userId);

        bool success = security.TryLoadCredentials(out var creds);
        Assert.True(success);
        Assert.NotNull(creds);
        Assert.Equal(botToken, creds.BotToken);
        Assert.Equal(userId, creds.AllowedUserId);
    }

    [Fact]
    public void SaveManualPairing_InvalidArguments_ThrowsExpectedExceptions()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);
        var service = new MetroSyncPairingService(security);

        Assert.Throws<ArgumentException>(() => service.SaveManualPairing("", 12345));
        Assert.Throws<ArgumentException>(() => service.SaveManualPairing("   ", 12345));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.SaveManualPairing("token", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.SaveManualPairing("token", -5));
    }

    [Fact]
    public async Task ValidateToken_Success_ReturnsBotUser()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);

        var bot = await MetroSyncPairingService.ValidateTokenAsync(transport, CancellationToken.None);

        Assert.NotNull(bot);
        Assert.Equal(123456789L, bot.Id);
        Assert.True(bot.IsBot);
        Assert.Equal("MetroSyncBot", bot.FirstName);
        Assert.Equal("metrosync_test_bot", bot.Username);
    }

    [Fact]
    public async Task ValidateToken_401Unauthorized_ThrowsTelegramApiException()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        server.Chaos.Script("getMe", new RespondStatus(401, "Unauthorized"));

        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);

        var ex = await Assert.ThrowsAsync<TelegramApiException>(
            () => MetroSyncPairingService.ValidateTokenAsync(transport, CancellationToken.None));

        Assert.Equal(401, ex.ErrorCode);
    }

    [Fact]
    public async Task Pairing_409Conflict_ReturnsConflictError()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);
        await using var server = await FakeTelegramServer.StartAsync();
        server.Chaos.Script("getUpdates", new RespondStatus(409, "Conflict: terminated by other getUpdates"));

        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);

        var service = new MetroSyncPairingService(security);

        var result = await service.WaitForPairingAsync(transport, FakeTelegramServer.Token, "123456", CancellationToken.None);

        Assert.Equal(PairingStatus.Failed, result.Status);
        Assert.Contains("Another app or webhook is already using this bot", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidatePin_TimingSafe_AndChecksExpired()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);
        var service = new MetroSyncPairingService(security);

        // Before any PIN is active
        Assert.False(service.ValidatePin("123456"));

        // Generate and set active PIN via WaitForPairingAsync reflection or test options
        var options = new MetroSyncPairingOptions { ExpiryTimeout = TimeSpan.FromMilliseconds(20) };
        var activeService = new MetroSyncPairingService(security, TimeProvider.System, options);

        // Directly invoke WaitForPairingAsync with a cancelled token just to initialize ActivePin and ExpiresAt
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var client = TelegramHttpTransport.CreateDedicatedClient();
        var transport = new TelegramHttpTransport(client, new Uri("https://127.0.0.1"), "token", allowInsecureLoopbackForTests: true);
        _ = activeService.WaitForPairingAsync(transport, "token", "654321", cts.Token);

        Assert.True(activeService.ValidatePin("654321"));
        Assert.True(activeService.ValidatePin(" 654321 "));
        Assert.False(activeService.ValidatePin("654322"));
        Assert.False(activeService.ValidatePin("654"));
        Assert.False(activeService.ValidatePin(""));
        Assert.False(activeService.ValidatePin(null));

        // Sleep to let it expire
        Thread.Sleep(30);
        Assert.True(activeService.IsExpired);
        Assert.False(activeService.ValidatePin("654321")); // Expired PIN returns false!
    }

    [Fact]
    public void PairingResult_EnsureSuccess_BehavesCorrectly()
    {
        PairingResult.Succeeded(123, "user", "name").EnsureSuccess(); // Should not throw

        Assert.Throws<OperationCanceledException>(() => PairingResult.CancelledResult().EnsureSuccess());
        Assert.Throws<TimeoutException>(() => PairingResult.ExpiredResult().EnsureSuccess());
        Assert.Throws<InvalidOperationException>(() => PairingResult.RateLimitedResult().EnsureSuccess());
        Assert.Throws<InvalidOperationException>(() => PairingResult.IncorrectPinResult().EnsureSuccess());
        Assert.Throws<InvalidOperationException>(() => PairingResult.FailedResult("custom").EnsureSuccess());
    }
}
