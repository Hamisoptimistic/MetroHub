using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using MetroHub.Tests.Widgets.MetroSync.TestKit;
using MetroHub.Widgets.Catalog.MetroSync.Services;
using Xunit;

namespace MetroHub.Tests.Widgets.MetroSync;

[Collection("MetroSyncTests")]
public sealed class EngineReliabilityTests
{
    private const long Allowed = 4242;
    private const long Stranger = 9999;

    private static ConduitEngineOptions FastOptions() => new()
    {
        PollTimeoutSeconds = 1,
        BackoffBase = TimeSpan.FromMilliseconds(10),
        BackoffCap = TimeSpan.FromMilliseconds(50),
        ConflictDelay = TimeSpan.FromMilliseconds(50),
        Jitter = () => 1.0,
    };

    private static TelegramHttpTransport Http(FakeTelegramServer server, HttpClient client) =>
        new(client, server.BaseAddress, FakeTelegramServer.Token, allowInsecureLoopbackForTests: true);

    private static ConduitEngine NewEngine(
        ITelegramTransport transport, IItemIndex index, ConduitEngineOptions? options = null, TimeProvider? time = null) =>
        new(transport, new SenderGate(Allowed), new PassthroughNormalizer(), index, options ?? FastOptions(), time);

    // ── Pure unit ────────────────────────────────────────────────────────────

    [Fact]
    public void Backoff_Delay_IsExponentialCappedAndJittered()
    {
        var b = TimeSpan.FromSeconds(1);
        var cap = TimeSpan.FromSeconds(60);

        Assert.Equal(TimeSpan.FromSeconds(1), Backoff.Delay(1, 1.0, b, cap));
        Assert.Equal(TimeSpan.FromSeconds(2), Backoff.Delay(2, 1.0, b, cap));
        Assert.Equal(TimeSpan.FromSeconds(32), Backoff.Delay(6, 1.0, b, cap));
        Assert.Equal(cap, Backoff.Delay(7, 1.0, b, cap));
        Assert.Equal(cap, Backoff.Delay(500, 1.0, b, cap));         // no overflow
        Assert.Equal(TimeSpan.Zero, Backoff.Delay(5, 0.0, b, cap)); // full jitter can be zero
        Assert.Equal(TimeSpan.FromSeconds(4), Backoff.Delay(4, 0.5, b, cap));
        Assert.Equal(TimeSpan.Zero, Backoff.Delay(1, double.NaN, b, cap)); // NaN does not throw
        Assert.Equal(TimeSpan.Zero, Backoff.Delay(1, -0.5, b, cap));       // negative jitter clamped
        Assert.Equal(TimeSpan.FromSeconds(1), Backoff.Delay(1, 1.5, b, cap)); // > 1.0 clamped
    }

    // ── Delivery guarantee ───────────────────────────────────────────────────

    [Fact]
    public async Task CrashAtEveryPoint_NeverLosesOrDuplicates()
    {
        const int total = 5;
        using var guard = new UnobservedTaskGuard();

        var points = await CrashSweep.RunAsync(async plan =>
        {
            await using var server = await FakeTelegramServer.StartAsync();
            using var http = TelegramHttpTransport.CreateDedicatedClient();
            var index = new InMemoryItemIndex(); // durable: survives the simulated restart

            var expected = Enumerable.Range(0, total)
                .Select(i => server.Enqueue(UpdateBuilder.Text(Allowed, $"m{i}")))
                .ToArray();

            // Run 1: with a crash injected at plan's target point (or none, for the dry run).
            await using (var first = NewEngine(
                new CrashingTransport(Http(server, http), plan), new CrashingIndex(index, plan)))
            {
                first.Start();
                await RunUntilCrashOrAllPersistedAsync(first, index, total);
            }

            // Run 2: "restart". Fresh engine, offset 0, same durable index, same server queue.
            await using (var second = NewEngine(Http(server, http), index))
            {
                second.Start();
                await Eventually.AssertAsync(
                    () => index.Count == total, TimeSpan.FromSeconds(10), "all updates persisted after restart");
            }

            Assert.Equal(expected.OrderBy(x => x).ToArray(), index.UpdateIds.ToArray()); // none lost
            Assert.Equal(total, index.Count);                                            // none duplicated
        });

        Assert.True(points > 0, "Dry run found no crash points; the harness is not wired to the engine.");
        guard.AssertNone();
    }

    private static async Task RunUntilCrashOrAllPersistedAsync(ConduitEngine engine, InMemoryItemIndex index, int total)
    {
        using var stop = new CancellationTokenSource();
        var persisted = Task.Run(async () =>
        {
            while (index.Count < total && !stop.IsCancellationRequested) await Task.Delay(10);
        });

        await Task.WhenAny(engine.Completion, persisted, Task.Delay(TimeSpan.FromSeconds(10)));
        stop.Cancel();
        await persisted;
    }

    // ── Chaos over the real HTTP stack ───────────────────────────────────────

    [Fact]
    public async Task TruncatedResetAndErrorResponses_AreRetried_WithoutLoss()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var index = new InMemoryItemIndex();

        server.Chaos.Script("getUpdates",
            new TruncateBody(),
            new ResetConnection(),
            new RespondStatus(502, "Bad Gateway"),
            new RespondStatus(429, "Too Many Requests", RetryAfter: 1));
        for (var i = 0; i < 3; i++) server.Enqueue(UpdateBuilder.Text(Allowed, $"m{i}"));

        var states = new ConcurrentQueue<EngineState>();
        await using var engine = NewEngine(Http(server, http), index);
        engine.StateChanged += states.Enqueue;
        engine.Start();

        await Eventually.AssertAsync(() => index.Count == 3, TimeSpan.FromSeconds(20), "3 updates despite faults");
        Assert.Contains(EngineState.Backoff, states);
        Assert.Equal(0, index.DuplicateUpserts); // faults happened before delivery, so no redelivery expected
    }

    // ── Iron Shield ──────────────────────────────────────────────────────────

    [Fact]
    public async Task UnauthorizedSenders_NeverReachIndexOrDownloads()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var recording = new RecordingTransport(Http(server, http));
        var index = new InMemoryItemIndex();

        var corpus = UpdateBuilder.HostileCorpus(Allowed, Stranger);
        foreach (var build in corpus) server.Enqueue(build);

        await using var engine = NewEngine(recording, index);
        engine.Start();
        await Eventually.AssertAsync(
            () => engine.DroppedCount == corpus.Count, TimeSpan.FromSeconds(10), "all hostile updates dropped");

        var legit = server.Enqueue(UpdateBuilder.Text(Allowed, "legit"));
        await Eventually.AssertAsync(() => index.Count == 1, TimeSpan.FromSeconds(10), "legit update persisted");

        Assert.Equal([legit], index.UpdateIds.ToArray());
        Assert.Equal(0, recording.GetFileCalls);
        Assert.Equal(0, recording.DownloadCalls);
        Assert.Equal(0, server.Requests("getFile"));
        Assert.Equal(0, server.Requests("download"));

        // Dropped updates were acknowledged: the server queue drains and nothing is redelivered.
        await Eventually.AssertAsync(() => server.PendingUpdates <= 1, TimeSpan.FromSeconds(10), "server queue drained");
        Assert.Equal(corpus.Count, engine.DroppedCount);
    }

    // ── State machine ────────────────────────────────────────────────────────

    [Fact]
    public async Task Backoff_FollowsExponentialSchedule_AndHonorsRetryAfter()
    {
        var time = new ManualTimeProvider();
        var transport = new ScriptedTransport()
            .ThenThrow(new TelegramApiException(502, "Bad Gateway"))                       // -> 1 s
            .ThenThrow(new TelegramApiException(502, "Bad Gateway"))                       // -> 2 s
            .ThenThrow(new TelegramApiException(429, "Too Many Requests", retryAfterSeconds: 7)) // -> exactly 7 s
            .ThenThrow(new HttpRequestException("network down"));                          // -> 4 s (3rd backoff)

        var options = new ConduitEngineOptions
        {
            Jitter = () => 1.0,
            BackoffBase = TimeSpan.FromSeconds(1),
            BackoffCap = TimeSpan.FromSeconds(60),
        };
        await using var engine = NewEngine(transport, new InMemoryItemIndex(), options, time);
        engine.Start();

        await StepAsync(1, TimeSpan.FromSeconds(1));
        await StepAsync(2, TimeSpan.FromSeconds(2));
        await StepAsync(3, TimeSpan.FromSeconds(7));
        await StepAsync(4, TimeSpan.FromSeconds(4));

        await Eventually.AssertAsync(() => transport.PollCount == 5, TimeSpan.FromSeconds(5), "5th poll after final delay");
        Assert.All(transport.Offsets, o => Assert.Equal(0, o)); // nothing acknowledged because nothing was received

        async Task StepAsync(int pollsSoFar, TimeSpan delay)
        {
            await Eventually.AssertAsync(() => transport.PollCount == pollsSoFar, TimeSpan.FromSeconds(5), $"poll {pollsSoFar}");
            await Task.Delay(50);                                          // let the engine register its timer
            time.Advance(delay - TimeSpan.FromMilliseconds(1));
            await Task.Delay(50);
            Assert.Equal(pollsSoFar, transport.PollCount);                 // not due yet
            time.Advance(TimeSpan.FromMilliseconds(1));                    // now due
        }
    }

    [Fact]
    public async Task Unauthorized401_IsTerminal_NoRetry()
    {
        var transport = new ScriptedTransport().ThenThrow(new TelegramApiException(401, "Unauthorized"));
        await using var engine = NewEngine(transport, new InMemoryItemIndex());
        engine.Start();

        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200); // would show up as extra polls if it retried

        Assert.Equal(EngineState.Unauthorized, engine.State);
        Assert.Equal(1, transport.PollCount);
    }

    [Fact]
    public async Task Conflict409_EntersConflictState_ThenRecovers()
    {
        var index = new InMemoryItemIndex();
        var transport = new ScriptedTransport()
            .ThenThrow(new TelegramApiException(409, "Conflict: terminated by other getUpdates request"))
            .ThenReturn(UpdateBuilder.ToRaw(1, UpdateBuilder.Text(Allowed, "hi")));

        var states = new ConcurrentQueue<EngineState>();
        await using var engine = NewEngine(transport, index);
        engine.StateChanged += states.Enqueue;
        engine.Start();

        await Eventually.AssertAsync(() => index.Count == 1, TimeSpan.FromSeconds(5), "recovered after conflict");
        Assert.Contains(EngineState.Conflict, states);
        Assert.Equal(EngineState.Online, engine.State);
    }

    [Fact]
    public async Task InitialOffset_PollsWithConfiguredOffset()
    {
        var transport = new ScriptedTransport()
            .ThenReturn(UpdateBuilder.ToRaw(501, UpdateBuilder.Text(Allowed, "test")));

        var options = new ConduitEngineOptions
        {
            InitialOffset = 500,
            PollTimeoutSeconds = 1
        };

        var index = new InMemoryItemIndex();
        await using var engine = NewEngine(transport, index, options);
        engine.Start();

        await Eventually.AssertAsync(() => index.Count == 1, TimeSpan.FromSeconds(5), "item persisted");
        Assert.Contains(500, transport.Offsets);
        Assert.Equal(502, engine.Offset);
    }

    [Fact]
    public async Task GetMe_ReturnsBotIdentity()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);

        var me = await transport.GetMeAsync(CancellationToken.None);
        Assert.True(me.IsBot);
        Assert.Equal("MetroSyncBot", me.FirstName);
        Assert.Equal("metrosync_test_bot", me.Username);
    }

    [Fact]
    public async Task OffsetChanged_FiresWheneverOffsetAdvances()
    {
        var transport = new ScriptedTransport()
            .ThenReturn(
                UpdateBuilder.ToRaw(101, UpdateBuilder.Text(Allowed, "first")),
                UpdateBuilder.ToRaw(102, UpdateBuilder.Text(Allowed, "second")));

        var offsetsFired = new List<long>();
        var index = new InMemoryItemIndex();
        await using var engine = NewEngine(transport, index);
        engine.OffsetChanged += offsetsFired.Add;
        engine.Start();

        await Eventually.AssertAsync(() => index.Count == 2, TimeSpan.FromSeconds(5), "2 items persisted");
        Assert.Contains(102, offsetsFired);
        Assert.Contains(103, offsetsFired);
        Assert.Equal(103, engine.Offset);
    }

    [Fact]
    public void TelegramHttpTransport_TrimsTokenWhitespace()
    {
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = new TelegramHttpTransport(
            http,
            new Uri("http://127.0.0.1:8080"),
            "  123456:my-token  ",
            allowInsecureLoopbackForTests: true);

        Assert.NotNull(transport);
    }

    [Fact]
    public async Task TelegramHttpTransport_ValidatesArguments()
    {
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = new TelegramHttpTransport(
            http,
            new Uri("http://127.0.0.1:8080"),
            "123456:token",
            allowInsecureLoopbackForTests: true);

        await Assert.ThrowsAsync<ArgumentException>(() => transport.GetFileAsync("", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => transport.DownloadAsync("valid/path", null!, 100, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => transport.DownloadAsync("valid/path", Stream.Null, -1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => transport.DownloadAsync("../evil/path", Stream.Null, 100, CancellationToken.None));
    }

    // ── Resources ────────────────────────────────────────────────────────────

    [Fact]
    public async Task IdleLongPoll_UsesNegligibleCpu()
    {
        ResourceProbe.ForceGc();
        await using var server = await FakeTelegramServer.StartAsync();
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var options = new ConduitEngineOptions { PollTimeoutSeconds = 25 };

        await using var engine = NewEngine(Http(server, http), new InMemoryItemIndex(), options);
        engine.Start();
        await Eventually.AssertAsync(() => server.Requests("getUpdates") >= 1, TimeSpan.FromSeconds(5), "poll parked");
        await Task.Delay(1000); // let JIT and startup work settle

        var cpu = await ResourceProbe.MeasureCpuPercentAsync(TimeSpan.FromSeconds(5));
        Assert.True(cpu < 1.0, $"Idle CPU was {cpu:0.000}% of machine capacity (limit 1%).");
    }

    [Fact]
    public async Task StartStopCycles_DoNotLeakThreadsHandlesOrObjects()
    {
        using var guard = new UnobservedTaskGuard();
        await using var server = await FakeTelegramServer.StartAsync();
        using var http = TelegramHttpTransport.CreateDedicatedClient();

        for (var i = 0; i < 5; i++) _ = await RunCycleAsync(server, http); // warm-up: pools, JIT, sockets
        var baseline = ResourceProbe.Capture(server.Port);

        var references = new List<WeakReference>();
        for (var i = 0; i < 200; i++) references.Add(await RunCycleAsync(server, http));

        await LeakAssert.AssertCollectedAsync(references);
        var after = ResourceProbe.Capture(server.Port);

        Assert.True(after.Threads - baseline.Threads <= 4, $"Threads {baseline.Threads} -> {after.Threads}");
        Assert.True(after.Handles - baseline.Handles <= 50, $"Handles {baseline.Handles} -> {after.Handles}");
        Assert.Equal(0, after.CloseWaitSockets);
        guard.AssertNone();
    }

    [MethodImpl(MethodImplOptions.NoInlining)] // keep the engine out of this frame's live locals
    private static async Task<WeakReference> RunCycleAsync(FakeTelegramServer server, HttpClient http)
    {
        var engine = NewEngine(Http(server, http), new InMemoryItemIndex());
        engine.StateChanged += _ => { }; // a subscriber that must not keep the engine alive
        engine.Start();
        await Task.Delay(20);
        await engine.DisposeAsync();
        return new WeakReference(engine);
    }
}
