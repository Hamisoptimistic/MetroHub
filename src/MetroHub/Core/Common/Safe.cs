using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace MetroHub.Core.Services;

/// <summary>
/// Small, robust execution helper for resilient action and function dispatch (Phase 3 T-20).
/// Runs operations, safely intercepts non-fatal Exceptions, logs them with rich context,
/// and returns success/failure or fallback results without crashing or silently swallowing.
/// </summary>
public static class Safe
{
    /// <summary>
    /// Global exception handler/logger delegate. Defaults to Serilog Log.Warning and Debug.WriteLine.
    /// Can be intercepted or replaced by unit tests.
    /// </summary>
    public static Action<Exception, string> Logger { get; set; } = DefaultLog;

    private static void DefaultLog(Exception ex, string context)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(context))
            {
                Serilog.Log.Warning(ex, "Handled non-fatal error: {ExceptionType} - {Message}", ex.GetType().Name, ex.Message);
            }
            else
            {
                Serilog.Log.Warning(ex, "Handled non-fatal error in [{Context}]: {ExceptionType} - {Message}", context, ex.GetType().Name, ex.Message);
            }

            Debug.WriteLine(string.IsNullOrWhiteSpace(context)
                ? $"[SAFE_TRY_ERROR] {ex.GetType().Name}: {ex.Message}"
                : $"[SAFE_TRY_ERROR] [{context}] {ex.GetType().Name}: {ex.Message}");
        }
        catch
        {
            // Logging failure must never escape and crash the host
        }
    }

    /// <summary>
    /// Executes an action safely. Catches any Exception, logs it with context, and returns false.
    /// Returns true if the action completes successfully.
    /// </summary>
    public static bool Try(Action action, string context = "")
    {
        if (action == null) return false;

        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            LogFailure(ex, context);
            return false;
        }
    }

    /// <summary>
    /// Evaluates a function safely. If successful, returns the computed value.
    /// If an exception occurs, logs it with context and returns the fallback value.
    /// </summary>
    public static T Try<T>(Func<T> func, T fallback, string context = "")
    {
        if (func == null) return fallback;

        try
        {
            return func();
        }
        catch (Exception ex)
        {
            LogFailure(ex, context);
            return fallback;
        }
    }

    /// <summary>
    /// Asynchronously executes an async action safely.
    /// Catches any Exception, logs it with context, and returns false.
    /// </summary>
    public static async Task<bool> TryAsync(Func<Task> actionAsync, string context = "")
    {
        if (actionAsync == null) return false;

        try
        {
            await actionAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            LogFailure(ex, context);
            return false;
        }
    }

    /// <summary>
    /// Asynchronously evaluates a function safely.
    /// If an exception occurs, logs it with context and returns the fallback value.
    /// </summary>
    public static async Task<T> TryAsync<T>(Func<Task<T>> funcAsync, T fallback, string context = "")
    {
        if (funcAsync == null) return fallback;

        try
        {
            return await funcAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogFailure(ex, context);
            return fallback;
        }
    }

    /// <summary>
    /// Explicitly logs an exception with context via the configured Safe logger.
    /// </summary>
    public static void Log(string context, Exception ex)
    {
        LogFailure(ex, context);
    }

    /// <summary>
    /// Explicitly logs an exception with context via the configured Safe logger.
    /// </summary>
    public static void Log(Exception ex, string context = "")
    {
        LogFailure(ex, context);
    }

    private static void LogFailure(Exception ex, string context)
    {
        try
        {
            Logger?.Invoke(ex, context);
        }
        catch
        {
            // Diagnostics must never throw
        }
    }
}
