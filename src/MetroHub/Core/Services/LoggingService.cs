using System;
using System.IO;
using Serilog;
using Serilog.Events;

namespace MetroHub.Core.Services;

/// <summary>
/// Centralized bootstrap and lifecycle manager for Serilog structured logging.
/// Configures asynchronous rolling file persistence, IDE debug sink, and Seq ingestion
/// with zero-allocation/non-blocking fail-safe defaults.
/// </summary>
public static class LoggingService
{
    private static bool _initialized;
    private static readonly object _initLock = new();

    /// <summary>
    /// Initializes Serilog globally once per application lifetime.
    /// Safe to call multiple times; subsequent calls are no-ops.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;

        lock (_initLock)
        {
            if (_initialized) return;

            try
            {
                string logsDir = AppPaths.LogsDir;
                AppPaths.EnsureDirectory(logsDir);

                string logFilePathPattern = Path.Combine(logsDir, "app-.log");

                var logConfig = new LoggerConfiguration()
#if DEBUG
                    .MinimumLevel.Debug()
#else
                    .MinimumLevel.Information()
#endif
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                    .MinimumLevel.Override("System", LogEventLevel.Warning)
                    .Enrich.FromLogContext()
                    .WriteTo.Debug(
                        outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
                    .WriteTo.File(
                        path: logFilePathPattern,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 7,
                        fileSizeLimitBytes: 15 * 1024 * 1024,
                        rollOnFileSizeLimit: true,
                        buffered: true,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
                    .WriteTo.Seq(
                        serverUrl: "http://localhost:5341",
                        period: TimeSpan.FromSeconds(2));

                Log.Logger = logConfig.CreateLogger();
                _initialized = true;

                Log.Information("MetroHub logging initialized. Log path: {LogDir}", logsDir);
            }
            catch (Exception ex)
            {
                // Logging initialization must never bring down the host application.
                // Fall back to basic debug console output if initialization fails.
                System.Diagnostics.Debug.WriteLine($"[LoggingService] Failed to initialize Serilog: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Flushes all pending async log batches and releases file handles before process exit.
    /// </summary>
    public static void Shutdown()
    {
        try
        {
            Log.Information("MetroHub logging shutting down. Flushing sinks.");
            Log.CloseAndFlush();
        }
        catch
        {
            // Shutdown flush must never throw
        }
    }
}
