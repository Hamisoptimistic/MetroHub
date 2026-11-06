using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MetroHub.Tests.Widgets.MetroSync.TestKit;

// ── Chaos ────────────────────────────────────────────────────────────────────

public abstract record ChaosAction;

/// <summary>Reply with a Telegram-style error body and HTTP status (401, 409, 429 + retry_after, 502...).</summary>
public sealed record RespondStatus(int Status, string Description, int? RetryAfter = null) : ChaosAction;

/// <summary>Abort the TCP connection without a response.</summary>
public sealed record ResetConnection : ChaosAction;

/// <summary>Wait, then handle the request normally.</summary>
public sealed record DelayThenContinue(TimeSpan By) : ChaosAction;

/// <summary>Send the start of a JSON body, then abort mid-stream.</summary>
public sealed record TruncateBody : ChaosAction;

/// <summary>Per-method queue of one-shot faults. Each request to that method consumes the next action.</summary>
public sealed class ChaosPlan
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<ChaosAction>> _queues = new();

    public void Script(string method, params ChaosAction[] actions)
    {
        var queue = _queues.GetOrAdd(method, _ => new ConcurrentQueue<ChaosAction>());
        foreach (var action in actions) queue.Enqueue(action);
    }

    internal ChaosAction? Next(string method) =>
        _queues.TryGetValue(method, out var q) && q.TryDequeue(out var action) ? action : null;
}

// ── Server ───────────────────────────────────────────────────────────────────

/// <summary>
/// A loopback Kestrel server that imitates the parts of the Telegram Bot API MetroSync uses:
/// getMe, getUpdates (long-poll, offset confirmation, 100-update pages), getFile, file download.
/// </summary>
public sealed class FakeTelegramServer : IAsyncDisposable
{
    public const string Token = "123456:TEST-TOKEN-NOT-REAL";

    private sealed record Stored(long UpdateId, DateTimeOffset EnqueuedAt, string Json);

    private readonly WebApplication _app;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly List<Stored> _queue = new();
    private readonly ConcurrentDictionary<string, (byte[] Content, long DeclaredSize)> _files = new();
    private readonly ConcurrentQueue<string> _requestLog = new();
    private TaskCompletionSource _signal = NewSignal();
    private long _nextUpdateId = 1000;
    private long _confirmedBelow;

    private FakeTelegramServer(WebApplication app, TimeProvider time)
    {
        _app = app;
        _time = time;
    }

    public Uri BaseAddress { get; private set; } = null!;
    public int Port => BaseAddress.Port;
    public ChaosPlan Chaos { get; } = new();

    /// <summary>Method names only (getMe, getUpdates, getFile, download). Never URLs, so tokens cannot leak here.</summary>
    public IReadOnlyCollection<string> RequestLog => _requestLog.ToArray();

    public int Requests(string method) => _requestLog.Count(m => m == method);

    public int PendingUpdates
    {
        get { lock (_gate) return _queue.Count; }
    }

    public static async Task<FakeTelegramServer> StartAsync(TimeProvider? time = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(2));

        var app = builder.Build();
        var server = new FakeTelegramServer(app, time ?? TimeProvider.System);
        app.Run(server.DispatchAsync);

        await app.StartAsync().ConfigureAwait(false);
        server.BaseAddress = new Uri(app.Urls.First());
        return server;
    }

    // ── Test-facing API ──────────────────────────────────────────────────────

    /// <summary>Queue an update. Returns its update_id.</summary>
    public long Enqueue(Func<long, object> build)
    {
        lock (_gate)
        {
            var id = _nextUpdateId++;
            _queue.Add(new Stored(id, _time.GetUtcNow(), JsonSerializer.Serialize(build(id))));
            var previous = _signal;
            _signal = NewSignal();
            previous.TrySetResult(); // wakes any parked long-poll
            return id;
        }
    }

    public void AddFile(string fileId, byte[] content, long? declaredSize = null) =>
        _files[fileId] = (content, declaredSize ?? content.Length);

    /// <summary>Simulates Telegram's 24-hour retention: drops unreceived updates older than age.</summary>
    public int ExpireOlderThan(TimeSpan age)
    {
        lock (_gate)
        {
            var cutoff = _time.GetUtcNow() - age;
            return _queue.RemoveAll(u => u.EnqueuedAt < cutoff);
        }
    }

    // ── Request handling ─────────────────────────────────────────────────────

    private async Task DispatchAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        var botPrefix = $"/bot{Token}/";
        var filePrefix = $"/file/bot{Token}/";

        if (path.StartsWith(botPrefix, StringComparison.Ordinal))
        {
            var method = path[botPrefix.Length..];
            _requestLog.Enqueue(method);
            if (await ApplyChaosAsync(method, ctx).ConfigureAwait(false)) return;

            switch (method)
            {
                case "getMe": await GetMeAsync(ctx).ConfigureAwait(false); break;
                case "getUpdates": await GetUpdatesAsync(ctx).ConfigureAwait(false); break;
                case "getFile": await GetFileAsync(ctx).ConfigureAwait(false); break;
                default: await WriteErrorAsync(ctx, 404, "Not Found").ConfigureAwait(false); break;
            }
        }
        else if (path.StartsWith(filePrefix, StringComparison.Ordinal))
        {
            _requestLog.Enqueue("download");
            if (await ApplyChaosAsync("download", ctx).ConfigureAwait(false)) return;
            await DownloadAsync(ctx, path[filePrefix.Length..]).ConfigureAwait(false);
        }
        else
        {
            _requestLog.Enqueue("unauthorized");
            await WriteErrorAsync(ctx, 401, "Unauthorized").ConfigureAwait(false);
        }
    }

    private async Task<bool> ApplyChaosAsync(string method, HttpContext ctx)
    {
        switch (Chaos.Next(method))
        {
            case null:
                return false;
            case RespondStatus r:
                await WriteErrorAsync(ctx, r.Status, r.Description, r.RetryAfter).ConfigureAwait(false);
                return true;
            case ResetConnection:
                ctx.Abort();
                return true;
            case DelayThenContinue d:
                try { await Task.Delay(d.By, ctx.RequestAborted).ConfigureAwait(false); }
                catch (OperationCanceledException) { return true; }
                return false;
            case TruncateBody:
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{\"ok\":true,\"result\":[{\"update_id\":").ConfigureAwait(false);
                await ctx.Response.Body.FlushAsync().ConfigureAwait(false);
                ctx.Abort();
                return true;
            default:
                return false;
        }
    }

    private static async Task GetMeAsync(HttpContext ctx)
    {
        var result = new
        {
            ok = true,
            result = new
            {
                id = 123456789L,
                is_bot = true,
                first_name = "MetroSyncBot",
                username = "metrosync_test_bot"
            }
        };

        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(result)).ConfigureAwait(false);
    }

    private async Task GetUpdatesAsync(HttpContext ctx)
    {
        var q = ctx.Request.Query;
        var offset = long.TryParse(q["offset"].ToString(), out var o) ? o : 0;
        var timeout = int.TryParse(q["timeout"].ToString(), out var t) ? Math.Clamp(t, 0, 60) : 0;
        var limit = int.TryParse(q["limit"].ToString(), out var l) ? Math.Clamp(l, 1, 100) : 100;
        var deadline = DateTime.UtcNow.AddSeconds(timeout);

        while (true)
        {
            Task wait;
            string? body = null;
            lock (_gate)
            {
                // Real semantics: a positive offset confirms (deletes) every update below it.
                if (offset > 0) _confirmedBelow = Math.Max(_confirmedBelow, offset);
                _queue.RemoveAll(u => u.UpdateId < _confirmedBelow);
                if (_queue.Count > 0)
                    body = "{\"ok\":true,\"result\":[" + string.Join(",", _queue.Take(limit).Select(u => u.Json)) + "]}";
                wait = _signal.Task;
            }

            if (body is not null)
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(body).ConfigureAwait(false);
                return;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{\"ok\":true,\"result\":[]}").ConfigureAwait(false);
                return;
            }

            try { await Task.WhenAny(wait, Task.Delay(remaining, ctx.RequestAborted)).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* client went away */ }
            if (ctx.RequestAborted.IsCancellationRequested) return;
        }
    }

    private async Task GetFileAsync(HttpContext ctx)
    {
        var fileId = ctx.Request.Query["file_id"].ToString();
        if (!_files.TryGetValue(fileId, out var file))
        {
            await WriteErrorAsync(ctx, 400, "Bad Request: invalid file_id").ConfigureAwait(false);
            return;
        }

        var result = new
        {
            ok = true,
            result = new
            {
                file_id = fileId,
                file_unique_id = "u-" + fileId,
                file_size = file.DeclaredSize,
                file_path = "files/" + fileId,
            },
        };
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(result)).ConfigureAwait(false);
    }

    private async Task DownloadAsync(HttpContext ctx, string filePath)
    {
        const string prefix = "files/";
        if (!filePath.StartsWith(prefix, StringComparison.Ordinal) ||
            !_files.TryGetValue(filePath[prefix.Length..], out var file))
        {
            await WriteErrorAsync(ctx, 404, "Not Found").ConfigureAwait(false);
            return;
        }

        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.ContentLength = file.Content.Length; // real size, even if getFile under-declared it
        await ctx.Response.Body.WriteAsync(file.Content, ctx.RequestAborted).ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(HttpContext ctx, int status, string description, int? retryAfter = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["error_code"] = status,
            ["description"] = description,
        };
        if (retryAfter is { } seconds) body["parameters"] = new { retry_after = seconds };

        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(body)).ConfigureAwait(false);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
