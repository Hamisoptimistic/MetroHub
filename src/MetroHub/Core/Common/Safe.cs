using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Serilog.Events;

namespace MetroHub.Core.Services;

/// <summary>
/// Centralized safety and observability dispatcher.
/// Handles Tier 1 (Critical), Tier 2 (Throttled Hot-Path), and Tier 3 (Intentional Ignore).
/// </summary>
public static class Safe
{
    // --- Compatibility & Logging Delegates ---
    public static Action<Exception, string> Logger { get; set; } = DefaultLog;

    private static void DefaultLog(Exception ex, string context) =>
        DefaultLogWithLevel(ex, context, LogEventLevel.Warning);

    private static void DefaultLogWithLevel(Exception ex, string context, LogEventLevel level)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(context))
                Serilog.Log.Write(level, ex, "Handled non-fatal error: {ExceptionType} - {Message}", ex.GetType().Name, ex.Message);
            else
                Serilog.Log.Write(level, ex, "Handled non-fatal error in [{Context}]: {ExceptionType} - {Message}", context, ex.GetType().Name, ex.Message);

            Debug.WriteLine(string.IsNullOrWhiteSpace(context)
                ? $"[SAFE_TRY_ERROR] [{level}] {ex.GetType().Name}: {ex.Message}"
                : $"[SAFE_TRY_ERROR] [{level}] [{context}] {ex.GetType().Name}: {ex.Message}");
        }
        catch { }
    }

    private static void LogFailure(Exception ex, string context, LogEventLevel level = LogEventLevel.Warning)
    {
        try { if (Logger == DefaultLog) DefaultLogWithLevel(ex, context, level); else Logger?.Invoke(ex, context); }
        catch { }
    }

    private static string ResolveContext(string? context, string member, string file, int line)
    {
        if (!string.IsNullOrWhiteSpace(context)) return context;
        string f = string.IsNullOrEmpty(file) ? "" : Path.GetFileNameWithoutExtension(file);
        if (!string.IsNullOrEmpty(f) && !string.IsNullOrEmpty(member))
            return line > 0 ? $"{f}.{member}:{line}" : $"{f}.{member}";
        return !string.IsNullOrEmpty(member) ? (line > 0 ? $"{member}:{line}" : member) : "";
    }

    public static void Log(string context, Exception ex, LogEventLevel level = LogEventLevel.Warning,
        [CallerMemberName] string m = "", [CallerFilePath] string f = "", [CallerLineNumber] int l = 0) =>
        Log(ex, context, level, m, f, l);

    public static void Log(Exception ex, string context = "", LogEventLevel level = LogEventLevel.Warning,
        [CallerMemberName] string m = "", [CallerFilePath] string f = "", [CallerLineNumber] int l = 0)
    {
        if (ex != null) LogFailure(ex, ResolveContext(context, m, f, l), level);
    }

    // --- Tier 1: Critical Error Logging (Cold Path, Crashes, User Actions) ---
    public static bool Try(Action action, string context = "", LogEventLevel level = LogEventLevel.Warning,
        [CallerMemberName] string m = "", [CallerFilePath] string f = "", [CallerLineNumber] int l = 0)
    {
        if (action == null) return false;
        try { action(); return true; }
        catch (Exception ex) { LogFailure(ex, ResolveContext(context, m, f, l), level); return false; }
    }

    public static T Try<T>(Func<T> func, T fallback, string context = "", LogEventLevel level = LogEventLevel.Warning,
        [CallerMemberName] string m = "", [CallerFilePath] string f = "", [CallerLineNumber] int l = 0)
    {
        if (func == null) return fallback;
        try { return func(); }
        catch (Exception ex) { LogFailure(ex, ResolveContext(context, m, f, l), level); return fallback; }
    }

    public static async Task<bool> TryAsync(Func<Task> actionAsync, string context = "", LogEventLevel level = LogEventLevel.Warning,
        [CallerMemberName] string m = "", [CallerFilePath] string f = "", [CallerLineNumber] int l = 0)
    {
        if (actionAsync == null) return false;
        try
        {
            Task? task = actionAsync();
            if (task == null) return false;
            await task.ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { LogFailure(ex, ResolveContext(context, m, f, l), level); return false; }
    }

    public static async Task<T> TryAsync<T>(Func<Task<T>> funcAsync, T fallback, string context = "", LogEventLevel level = LogEventLevel.Warning,
        [CallerMemberName] string m = "", [CallerFilePath] string f = "", [CallerLineNumber] int l = 0)
    {
        if (funcAsync == null) return fallback;
        try
        {
            Task<T>? task = funcAsync();
            if (task == null) return fallback;
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return fallback; }
        catch (Exception ex) { LogFailure(ex, ResolveContext(context, m, f, l), level); return fallback; }
    }

    // --- Tier 2: Throttled / Deduplicated Warnings (Hot-Path, Polling) ---
    private sealed class ThrottleState
    {
        public DateTime LastLoggedUtc;
        public int SuppressedCount;
        public readonly Lock Lock = new();
    }

    private static readonly ConcurrentDictionary<string, ThrottleState> _throttleTracker = new(StringComparer.Ordinal);
    private static readonly TimeSpan DefaultThrottleInterval = TimeSpan.FromSeconds(60);

    public static bool TryThrottled(Action action, string throttleKey, TimeSpan? interval = null, string context = "",
        [CallerMemberName] string m = "", [CallerFilePath] string f = "", [CallerLineNumber] int l = 0)
    {
        if (action == null) return false;
        try { action(); return true; }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { LogThrottled(ex, throttleKey, interval, context, m, f, l); return false; }
    }

    public static void LogThrottled(Exception ex, string throttleKey, TimeSpan? interval = null, string context = "",
        [CallerMemberName] string m = "", [CallerFilePath] string f = "", [CallerLineNumber] int l = 0)
    {
        if (ex == null || ex is OperationCanceledException) return;
        string callerCtx = ResolveContext(context, m, f, l);
        string key = !string.IsNullOrEmpty(throttleKey) ? throttleKey : (!string.IsNullOrEmpty(callerCtx) ? callerCtx : "Safe.Throttled");

        if (_throttleTracker.Count > 256) _throttleTracker.Clear();
        var state = _throttleTracker.GetOrAdd(key, _ => new ThrottleState());
        TimeSpan span = interval ?? DefaultThrottleInterval;
        bool shouldLog = false;
        int suppressed = 0;

        lock (state.Lock)
        {
            DateTime now = DateTime.UtcNow;
            if (state.LastLoggedUtc == DateTime.MinValue || (now - state.LastLoggedUtc) >= span)
            {
                shouldLog = true;
                suppressed = state.SuppressedCount;
                state.SuppressedCount = 0;
                state.LastLoggedUtc = now;
            }
            else { state.SuppressedCount++; }
        }

        if (shouldLog)
        {
            string fullCtx = !string.IsNullOrEmpty(callerCtx) && !string.Equals(callerCtx, key, StringComparison.Ordinal)
                ? $"[{key}] {callerCtx}" : $"[{key}]";
            if (suppressed > 0) fullCtx += $" ({suppressed} duplicate error(s) suppressed)";
            LogFailure(ex, fullCtx, LogEventLevel.Warning);
        }
        else
        {
            Debug.WriteLine($"[SAFE_THROTTLED] [{key}] Suppressed: {ex.GetType().Name} - {ex.Message}");
        }
    }

    // --- Tier 3: Intentional Ignore (Best-Effort Cleanup, Probes) ---
    public static void Ignore(Exception ex, string reason = "")
    {
        if (ex == null) return;
        Debug.WriteLine(string.IsNullOrEmpty(reason)
            ? $"[SAFE_IGNORE] {ex.GetType().Name}: {ex.Message}"
            : $"[SAFE_IGNORE] [{reason}] {ex.GetType().Name}: {ex.Message}");
    }

    public static void TryIgnore(Action action, string reason = "")
    {
        if (action == null) return;
        try { action(); }
        catch (Exception ex) { Ignore(ex, reason); }
    }

    // --- Fire-and-Forget Background Dispatch (MA0134 Replacement) ---
    public static void FireAndForget(this Task task, string context = "",
        [CallerMemberName] string m = "", [CallerFilePath] string f = "", [CallerLineNumber] int l = 0)
    {
        if (task == null) return;
        if (task.IsCompleted)
        {
            if (task.IsFaulted && task.Exception != null) HandleTaskException(task.Exception, context, m, f, l);
            return;
        }

        _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null) HandleTaskException(t.Exception, context, m, f, l);
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    public static void FireAndForget(Func<Task> taskProducer, string context = "",
        [CallerMemberName] string m = "", [CallerFilePath] string f = "", [CallerLineNumber] int l = 0)
    {
        if (taskProducer == null) return;
        try
        {
            Task? task = taskProducer();
            if (task != null) FireAndForget(task, context, m, f, l);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogFailure(ex, ResolveContext(context, m, f, l), LogEventLevel.Warning); }
    }

    public static void FireAndForget(this DispatcherOperation op, string context = "",
        [CallerMemberName] string m = "", [CallerFilePath] string f = "", [CallerLineNumber] int l = 0) =>
        op?.Task.FireAndForget(context, m, f, l);

    private static void HandleTaskException(AggregateException agg, string context, string member, string file, int line)
    {
        foreach (var inner in agg.Flatten().InnerExceptions)
        {
            if (inner is not OperationCanceledException)
                LogFailure(inner, ResolveContext(context, member, file, line), LogEventLevel.Warning);
        }
    }
}
