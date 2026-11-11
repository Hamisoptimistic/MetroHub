using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Serilog.Events;

namespace MetroHub.Core.Services;

/// <summary>
/// Centralized, high-reliability safety and observability dispatcher (Phase 2A).
/// Intercepts non-fatal exceptions, logs them with rich caller context, and supports
/// Tier 1 (Critical), Tier 2 (Throttled Hot-Path/Polling), and Tier 3 (Intentional Ignore).
/// </summary>
public static class Safe
{
    // =========================================================================
    // 0. COMPATIBILITY & DELEGATES (Preserves existing tests and call sites)
    // =========================================================================

    /// <summary>
    /// Global exception handler/logger delegate. Defaults to Serilog Log.Write and Debug.WriteLine.
    /// Can be intercepted or replaced by unit tests.
    /// </summary>
    public static Action<Exception, string> Logger { get; set; } = DefaultLog;

    private static void DefaultLog(Exception ex, string context) =>
        DefaultLogWithLevel(ex, context, LogEventLevel.Warning);

    private static void DefaultLogWithLevel(Exception ex, string context, LogEventLevel level)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(context))
            {
                Serilog.Log.Write(level, ex, "Handled non-fatal error: {ExceptionType} - {Message}", ex.GetType().Name, ex.Message);
            }
            else
            {
                Serilog.Log.Write(level, ex, "Handled non-fatal error in [{Context}]: {ExceptionType} - {Message}", context, ex.GetType().Name, ex.Message);
            }

            Debug.WriteLine(string.IsNullOrWhiteSpace(context)
                ? $"[SAFE_TRY_ERROR] [{level}] {ex.GetType().Name}: {ex.Message}"
                : $"[SAFE_TRY_ERROR] [{level}] [{context}] {ex.GetType().Name}: {ex.Message}");
        }
        catch
        {
            // Logging failure must never escape and crash the host
        }
    }

    private static void LogFailure(Exception ex, string context, LogEventLevel level = LogEventLevel.Warning)
    {
        try
        {
            if (Logger == DefaultLog)
            {
                DefaultLogWithLevel(ex, context, level);
            }
            else
            {
                Logger?.Invoke(ex, context);
            }
        }
        catch
        {
            // Diagnostics must never throw
        }
    }

    private static string ResolveContext(string? context, string memberName, string sourceFile, int lineNumber)
    {
        if (!string.IsNullOrWhiteSpace(context))
        {
            return context;
        }

        string fileName = string.IsNullOrEmpty(sourceFile) ? string.Empty : System.IO.Path.GetFileNameWithoutExtension(sourceFile);
        if (!string.IsNullOrEmpty(fileName) && !string.IsNullOrEmpty(memberName))
        {
            return lineNumber > 0 ? $"{fileName}.{memberName}:{lineNumber}" : $"{fileName}.{memberName}";
        }

        if (!string.IsNullOrEmpty(memberName))
        {
            return lineNumber > 0 ? $"{memberName}:{lineNumber}" : memberName;
        }

        return string.Empty;
    }

    /// <summary>
    /// Explicitly logs an exception with context via the configured Safe logger.
    /// Supports: Safe.Log("context", ex)
    /// </summary>
    public static void Log(
        string context,
        Exception ex,
        LogEventLevel level = LogEventLevel.Warning,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        if (ex == null) return;
        string ctx = ResolveContext(context, memberName, sourceFile, lineNumber);
        LogFailure(ex, ctx, level);
    }

    /// <summary>
    /// Explicitly logs an exception with context via the configured Safe logger.
    /// Supports: Safe.Log(ex, "context")
    /// </summary>
    public static void Log(
        Exception ex,
        string context = "",
        LogEventLevel level = LogEventLevel.Warning,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        if (ex == null) return;
        string ctx = ResolveContext(context, memberName, sourceFile, lineNumber);
        LogFailure(ex, ctx, level);
    }

    // =========================================================================
    // 1. TIER 1 - CRITICAL ERROR LOGGING (Cold Path, Crashes, User Actions)
    // =========================================================================

    /// <summary>
    /// Executes an action safely. Catches any Exception, logs it with context, and returns false.
    /// Returns true if the action completes successfully.
    /// </summary>
    public static bool Try(
        Action action,
        string context = "",
        LogEventLevel level = LogEventLevel.Warning,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        if (action == null) return false;

        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            string ctx = ResolveContext(context, memberName, sourceFile, lineNumber);
            LogFailure(ex, ctx, level);
            return false;
        }
    }

    /// <summary>
    /// Evaluates a function safely. If successful, returns the computed value.
    /// If an exception occurs, logs it with context and returns the fallback value.
    /// </summary>
    public static T Try<T>(
        Func<T> func,
        T fallback,
        string context = "",
        LogEventLevel level = LogEventLevel.Warning,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        if (func == null) return fallback;

        try
        {
            return func();
        }
        catch (Exception ex)
        {
            string ctx = ResolveContext(context, memberName, sourceFile, lineNumber);
            LogFailure(ex, ctx, level);
            return fallback;
        }
    }

    /// <summary>
    /// Asynchronously executes an async action safely. Automatically ignores OperationCanceledException.
    /// Catches any other Exception, logs it with context, and returns false.
    /// </summary>
    public static async Task<bool> TryAsync(
        Func<Task> actionAsync,
        string context = "",
        LogEventLevel level = LogEventLevel.Warning,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        if (actionAsync == null) return false;

        try
        {
            Task? task = actionAsync();
            if (task == null) return false;
            await task.ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            string ctx = ResolveContext(context, memberName, sourceFile, lineNumber);
            LogFailure(ex, ctx, level);
            return false;
        }
    }

    /// <summary>
    /// Asynchronously evaluates a function safely. Automatically ignores OperationCanceledException.
    /// If an exception occurs, logs it with context and returns the fallback value.
    /// </summary>
    public static async Task<T> TryAsync<T>(
        Func<Task<T>> funcAsync,
        T fallback,
        string context = "",
        LogEventLevel level = LogEventLevel.Warning,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        if (funcAsync == null) return fallback;

        try
        {
            Task<T>? task = funcAsync();
            if (task == null) return fallback;
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return fallback;
        }
        catch (Exception ex)
        {
            string ctx = ResolveContext(context, memberName, sourceFile, lineNumber);
            LogFailure(ex, ctx, level);
            return fallback;
        }
    }

    // =========================================================================
    // 2. TIER 2 - THROTTLED / DEDUPLICATED WARNINGS (Hot-Path, Render, Polling)
    // =========================================================================

    private sealed class ThrottleState
    {
        public DateTime LastLoggedUtc;
        public int SuppressedCount;
        public readonly Lock Lock = new();
    }

    private static readonly ConcurrentDictionary<string, ThrottleState> _throttleTracker = new(StringComparer.Ordinal);
    private static readonly TimeSpan DefaultThrottleInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Executes synchronous action with rate-limiting. Suppresses duplicate errors within the window
    /// and logs a summary of suppressed occurrences when the window elapses.
    /// </summary>
    public static bool TryThrottled(
        Action action,
        string throttleKey,
        TimeSpan? interval = null,
        string context = "",
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        if (action == null) return false;

        try
        {
            action();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogThrottled(ex, throttleKey, interval, context, memberName, sourceFile, lineNumber);
            return false;
        }
    }

    /// <summary>
    /// Evaluates synchronous function with rate-limiting. Suppresses duplicate errors within the window
    /// and logs a summary of suppressed occurrences when the window elapses.
    /// </summary>
    public static T TryThrottled<T>(
        Func<T> func,
        T fallback,
        string throttleKey,
        TimeSpan? interval = null,
        string context = "",
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        if (func == null) return fallback;

        try
        {
            return func();
        }
        catch (OperationCanceledException)
        {
            return fallback;
        }
        catch (Exception ex)
        {
            LogThrottled(ex, throttleKey, interval, context, memberName, sourceFile, lineNumber);
            return fallback;
        }
    }

    /// <summary>
    /// Executes asynchronous action with rate-limiting and automatic cancellation suppression.
    /// </summary>
    public static async Task<bool> TryAsyncThrottled(
        Func<Task> actionAsync,
        string throttleKey,
        TimeSpan? interval = null,
        string context = "",
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        if (actionAsync == null) return false;

        try
        {
            Task? task = actionAsync();
            if (task == null) return false;
            await task.ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogThrottled(ex, throttleKey, interval, context, memberName, sourceFile, lineNumber);
            return false;
        }
    }

    /// <summary>
    /// Evaluates asynchronous function with rate-limiting and automatic cancellation suppression.
    /// </summary>
    public static async Task<T> TryAsyncThrottled<T>(
        Func<Task<T>> funcAsync,
        T fallback,
        string throttleKey,
        TimeSpan? interval = null,
        string context = "",
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        if (funcAsync == null) return fallback;

        try
        {
            Task<T>? task = funcAsync();
            if (task == null) return fallback;
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return fallback;
        }
        catch (Exception ex)
        {
            LogThrottled(ex, throttleKey, interval, context, memberName, sourceFile, lineNumber);
            return fallback;
        }
    }

    /// <summary>
    /// Explicit throttled logging for hot loops, render updates, and polling timers.
    /// </summary>
    public static void LogThrottled(
        Exception ex,
        string throttleKey,
        TimeSpan? interval = null,
        string context = "",
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        if (ex == null) return;
        if (ex is OperationCanceledException) return;

        string callerCtx = ResolveContext(context, memberName, sourceFile, lineNumber);
        string key = !string.IsNullOrEmpty(throttleKey)
            ? throttleKey
            : (!string.IsNullOrEmpty(callerCtx) ? callerCtx : "Safe.Throttled");

        var state = _throttleTracker.GetOrAdd(key, _ => new ThrottleState());
        TimeSpan effectiveInterval = interval ?? DefaultThrottleInterval;
        bool shouldLog = false;
        int suppressed = 0;

        lock (state.Lock)
        {
            DateTime now = DateTime.UtcNow;
            if (state.LastLoggedUtc == DateTime.MinValue || (now - state.LastLoggedUtc) >= effectiveInterval)
            {
                shouldLog = true;
                suppressed = state.SuppressedCount;
                state.SuppressedCount = 0;
                state.LastLoggedUtc = now;
            }
            else
            {
                state.SuppressedCount++;
            }
        }

        if (shouldLog)
        {
            string fullCtx;
            if (!string.IsNullOrEmpty(callerCtx) && !string.Equals(callerCtx, key, StringComparison.Ordinal))
            {
                fullCtx = $"[{key}] {callerCtx}";
            }
            else
            {
                fullCtx = $"[{key}]";
            }

            if (suppressed > 0)
            {
                fullCtx = $"{fullCtx} ({suppressed} duplicate error(s) suppressed)";
            }

            LogFailure(ex, fullCtx, LogEventLevel.Warning);
        }
        else
        {
            try
            {
                Debug.WriteLine($"[SAFE_THROTTLED] [{key}] Suppressed: {ex.GetType().Name} - {ex.Message}");
            }
            catch
            {
            }
        }
    }

    // =========================================================================
    // 3. TIER 3 - INTENTIONAL IGNORE (Best-Effort Cleanup, Disposal, Probes)
    // =========================================================================

    /// <summary>
    /// Explicitly documents an intentional ignore with optional debug-only diagnostics.
    /// </summary>
    public static void Ignore(Exception ex, string reason = "")
    {
        if (ex == null) return;
        try
        {
            Debug.WriteLine(string.IsNullOrEmpty(reason)
                ? $"[SAFE_IGNORE] {ex.GetType().Name}: {ex.Message}"
                : $"[SAFE_IGNORE] [{reason}] {ex.GetType().Name}: {ex.Message}");
        }
        catch
        {
        }
    }

    /// <summary>
    /// Safely runs best-effort action, intentionally ignoring any exception.
    /// </summary>
    public static void TryIgnore(Action action, string reason = "")
    {
        if (action == null) return;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Ignore(ex, reason);
        }
    }

    /// <summary>
    /// Safely runs asynchronous best-effort action, intentionally ignoring any exception.
    /// </summary>
    public static async Task TryIgnoreAsync(Func<Task> actionAsync, string reason = "")
    {
        if (actionAsync == null) return;
        try
        {
            Task? task = actionAsync();
            if (task == null) return;
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Ignore(ex, reason);
        }
    }

    // =========================================================================
    // 4. FIRE-AND-FORGET BACKGROUND DISPATCH (MA0134 Replacement)
    // =========================================================================

    /// <summary>
    /// Extension method: Safely fires a background Task, logging unhandled exceptions and ignoring cancellations.
    /// </summary>
    public static void FireAndForget(
        this Task task,
        string context = "",
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        if (task == null) return;

        if (task.IsCompleted)
        {
            if (task.IsFaulted && task.Exception != null)
            {
                HandleTaskException(task.Exception, context, memberName, sourceFile, lineNumber);
            }
            return;
        }

        _ = task.ContinueWith(
            t =>
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    HandleTaskException(t.Exception, context, memberName, sourceFile, lineNumber);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Safely fires a background Task from a producer delegate, logging unhandled exceptions and ignoring cancellations.
    /// </summary>
    public static void FireAndForget(
        Func<Task> taskProducer,
        string context = "",
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        if (taskProducer == null) return;

        try
        {
            Task? task = taskProducer();
            if (task != null)
            {
                FireAndForget(task, context, memberName, sourceFile, lineNumber);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            string ctx = ResolveContext(context, memberName, sourceFile, lineNumber);
            LogFailure(ex, ctx, LogEventLevel.Warning);
        }
    }

    private static void HandleTaskException(
        AggregateException aggregateException,
        string context,
        string memberName,
        string sourceFile,
        int lineNumber)
    {
        if (aggregateException == null) return;
        var flattened = aggregateException.Flatten();
        foreach (var inner in flattened.InnerExceptions)
        {
            if (inner is OperationCanceledException)
            {
                continue;
            }

            string ctx = ResolveContext(context, memberName, sourceFile, lineNumber);
            LogFailure(inner, ctx, LogEventLevel.Warning);
        }
    }

    /// <summary>
    /// Safely dispatches action onto Application.Current Dispatcher with error logging.
    /// </summary>
    public static void DispatchAsync(
        Action action,
        DispatcherPriority priority = DispatcherPriority.Normal,
        string context = "",
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int lineNumber = 0) =>
        DispatchAsync(null, action, priority, context, memberName, sourceFile, lineNumber);

    /// <summary>
    /// Safely dispatches asynchronous action onto Application.Current Dispatcher with error logging.
    /// </summary>
    public static void DispatchAsync(
        Func<Task> asyncAction,
        DispatcherPriority priority = DispatcherPriority.Normal,
        string context = "",
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int lineNumber = 0) =>
        DispatchAsync(null, asyncAction, priority, context, memberName, sourceFile, lineNumber);

    /// <summary>
    /// Safely dispatches action onto WPF Dispatcher with error logging.
    /// </summary>
    public static void DispatchAsync(
        Dispatcher? dispatcher,
        Action action,
        DispatcherPriority priority = DispatcherPriority.Normal,
        string context = "",
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        if (action == null) return;
        var targetDispatcher = dispatcher ?? System.Windows.Application.Current?.Dispatcher;
        if (targetDispatcher == null || targetDispatcher.HasShutdownStarted)
        {
            Try(action, context, LogEventLevel.Warning, memberName, sourceFile, lineNumber);
            return;
        }

        try
        {
            _ = targetDispatcher.InvokeAsync(
                () => Try(action, context, LogEventLevel.Warning, memberName, sourceFile, lineNumber),
                priority);
        }
        catch (Exception ex)
        {
            string ctx = ResolveContext(context, memberName, sourceFile, lineNumber);
            LogFailure(ex, ctx, LogEventLevel.Warning);
        }
    }

    /// <summary>
    /// Safely dispatches asynchronous action onto WPF Dispatcher with error logging.
    /// </summary>
    public static void DispatchAsync(
        Dispatcher? dispatcher,
        Func<Task> asyncAction,
        DispatcherPriority priority = DispatcherPriority.Normal,
        string context = "",
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        if (asyncAction == null) return;
        var targetDispatcher = dispatcher ?? System.Windows.Application.Current?.Dispatcher;
        if (targetDispatcher == null || targetDispatcher.HasShutdownStarted)
        {
            _ = TryAsync(asyncAction, context, LogEventLevel.Warning, memberName, sourceFile, lineNumber);
            return;
        }

        try
        {
            _ = targetDispatcher.InvokeAsync(
                async () => await TryAsync(asyncAction, context, LogEventLevel.Warning, memberName, sourceFile, lineNumber).ConfigureAwait(false),
                priority);
        }
        catch (Exception ex)
        {
            string ctx = ResolveContext(context, memberName, sourceFile, lineNumber);
            LogFailure(ex, ctx, LogEventLevel.Warning);
        }
    }
}
