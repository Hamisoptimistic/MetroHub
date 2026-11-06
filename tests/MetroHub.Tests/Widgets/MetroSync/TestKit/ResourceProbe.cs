using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.NetworkInformation;

namespace MetroHub.Tests.Widgets.MetroSync.TestKit;

public readonly record struct ResourceSnapshot(int Threads, int Handles, long ManagedBytes, int CloseWaitSockets);

public static class ResourceProbe
{
    public static void ForceGc()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        GC.Collect();
    }

    /// <param name="loopbackPort">If given, counts CLOSE_WAIT sockets touching that port (leaked half-closed connections).</param>
    public static ResourceSnapshot Capture(int? loopbackPort = null)
    {
        ForceGc();
        using var process = Process.GetCurrentProcess();
        var closeWait = loopbackPort is { } port
            ? IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
                .Count(c => c.State == TcpState.CloseWait &&
                            (c.LocalEndPoint.Port == port || c.RemoteEndPoint.Port == port))
            : 0;
        return new ResourceSnapshot(
            process.Threads.Count,
            process.HandleCount,
            GC.GetTotalMemory(forceFullCollection: true),
            closeWait);
    }

    /// <summary>Whole-process CPU as a percentage of total machine capacity over the window.</summary>
    public static async Task<double> MeasureCpuPercentAsync(TimeSpan window)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var start = Stopwatch.GetTimestamp();

        await Task.Delay(window).ConfigureAwait(false);

        process.Refresh();
        var cpuUsed = process.TotalProcessorTime - cpuBefore;
        var elapsed = Stopwatch.GetElapsedTime(start);
        return cpuUsed.TotalMilliseconds / (elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100.0;
    }
}

public static class LeakAssert
{
    /// <summary>
    /// Fails if any referenced object survives repeated full GCs.
    /// Create the objects in a [MethodImpl(NoInlining)] method and run tests in Release mode,
    /// otherwise the JIT can keep locals alive and produce false failures.
    /// </summary>
    public static async Task AssertCollectedAsync(IReadOnlyCollection<WeakReference> references, int attempts = 20)
    {
        for (var i = 0; i < attempts; i++)
        {
            ResourceProbe.ForceGc();
            if (references.All(r => !r.IsAlive)) return;
            await Task.Delay(50).ConfigureAwait(false);
        }

        var alive = references.Count(r => r.IsAlive);
        throw new InvalidOperationException($"{alive} of {references.Count} object(s) still alive after GC: probable leak.");
    }
}

/// <summary>Fails a test if any Task exception goes unobserved while the guard is active.</summary>
public sealed class UnobservedTaskGuard : IDisposable
{
    private readonly ConcurrentQueue<Exception> _errors = new();
    private readonly EventHandler<UnobservedTaskExceptionEventArgs> _handler;

    public UnobservedTaskGuard()
    {
        _handler = (_, e) =>
        {
            _errors.Enqueue(e.Exception);
            e.SetObserved();
        };
        TaskScheduler.UnobservedTaskException += _handler;
    }

    public void AssertNone()
    {
        ResourceProbe.ForceGc(); // unobserved exceptions surface when faulted tasks are finalized
        if (!_errors.IsEmpty) throw new AggregateException("Unobserved task exceptions:", _errors);
    }

    public void Dispose() => TaskScheduler.UnobservedTaskException -= _handler;
}

public static class Eventually
{
    public static async Task AssertAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() > deadline)
                throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0.#}s waiting for: {what}");
            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
