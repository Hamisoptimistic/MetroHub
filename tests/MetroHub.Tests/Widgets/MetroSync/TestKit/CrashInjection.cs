using System.IO;
using System.Text.Json;
using MetroHub.Widgets.Catalog.MetroSync.Models;
using MetroHub.Widgets.Catalog.MetroSync.Services;

namespace MetroHub.Tests.Widgets.MetroSync.TestKit;

/// <summary>Simulates process death. Deliberately NOT a transient error, so the engine must not swallow it.</summary>
public sealed class InjectedCrashException(string point) : Exception($"Injected crash at '{point}'")
{
    public string Point { get; } = point;
}

/// <summary>
/// Counts crash points as code runs. With target = N, the Nth point (0-based) throws.
/// target = -1 never crashes (dry run: just counts how many points a scenario passes through).
/// </summary>
public sealed class CrashPlan(int target = -1)
{
    private int _hits;
    private int _fired;
    private string? _firedAt;

    public int PointsHit => Volatile.Read(ref _hits);
    public bool Fired => Volatile.Read(ref _fired) != 0;
    public string? FiredAt => _firedAt;

    public void Point(string name)
    {
        var index = Interlocked.Increment(ref _hits) - 1;
        if (index != target) return;
        _firedAt = name;
        Volatile.Write(ref _fired, 1);
        throw new InjectedCrashException(name);
    }
}

public sealed class CrashingIndex(IItemIndex inner, CrashPlan plan) : IItemIndex
{
    public async ValueTask<bool> UpsertAsync(MetroSyncMessageItem item, CancellationToken ct)
    {
        plan.Point("index.before-persist");
        var isNew = await inner.UpsertAsync(item, ct).ConfigureAwait(false);
        plan.Point("index.after-persist"); // persisted but not yet acknowledged to Telegram
        return isNew;
    }
}

public sealed class CrashingTransport(ITelegramTransport inner, CrashPlan plan) : ITelegramTransport
{
    public Task<TelegramBotUser> GetMeAsync(CancellationToken ct) => inner.GetMeAsync(ct);

    public async Task<IReadOnlyList<RawUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct)
    {
        plan.Point("poll.before");
        var batch = await inner.GetUpdatesAsync(offset, timeoutSeconds, ct).ConfigureAwait(false);
        plan.Point("poll.after"); // received but not yet processed
        return batch;
    }

    public Task<TelegramFileInfo> GetFileAsync(string fileId, CancellationToken ct) =>
        inner.GetFileAsync(fileId, ct);

    public Task DownloadAsync(string filePath, Stream destination, long maxBytes, CancellationToken ct) =>
        inner.DownloadAsync(filePath, destination, maxBytes, ct);

    public async Task<JsonDocument> SendMessageAsync(long chatId, string text, CancellationToken ct)
    {
        plan.Point("send.before");
        var res = await inner.SendMessageAsync(chatId, text, ct).ConfigureAwait(false);
        plan.Point("send.after");
        return res;
    }

    public async Task<JsonDocument> SendPhotoAsync(long chatId, Stream imageStream, string? caption, CancellationToken ct)
    {
        plan.Point("sendphoto.before");
        var res = await inner.SendPhotoAsync(chatId, imageStream, caption, ct).ConfigureAwait(false);
        plan.Point("sendphoto.after");
        return res;
    }
}

public static class CrashSweep
{
    /// <summary>
    /// Runs scenario once without crashing to count crash points, then once per point
    /// with a crash injected there. The scenario owns restart logic and must assert its own invariants
    /// (throw on violation). Returns the number of points exercised.
    /// </summary>
    public static async Task<int> RunAsync(Func<CrashPlan, Task> scenario, int maxPoints = 100)
    {
        var dry = new CrashPlan(-1);
        await scenario(dry).ConfigureAwait(false);
        var points = Math.Min(dry.PointsHit, maxPoints);

        for (var i = 0; i < points; i++)
        {
            var plan = new CrashPlan(i);
            try
            {
                await scenario(plan).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    $"Invariant violated with a crash at point {i} ('{plan.FiredAt ?? "not reached"}').", e);
            }
        }

        return points;
    }
}
