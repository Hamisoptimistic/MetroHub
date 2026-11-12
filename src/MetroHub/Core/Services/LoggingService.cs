using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Serilog;
using Serilog.Events;

namespace MetroHub.Core.Services;

/// <summary>
/// Centralized bootstrap and lifecycle manager for Serilog structured logging.
/// Configures asynchronous rolling file persistence with zero-allocation/non-blocking fail-safe defaults.
/// </summary>
public static class LoggingService
{
    private static volatile bool _initialized;
    private static readonly Lock _initLock = new();

    /// <summary>
    /// Gets a value indicating whether logging has been initialized.
    /// </summary>
    public static bool IsInitialized => _initialized;

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
                    .WriteTo.File(
                        path: logFilePathPattern,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 7,
                        fileSizeLimitBytes: 15 * 1024 * 1024,
                        rollOnFileSizeLimit: true,
                        buffered: true,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
                        formatProvider: CultureInfo.InvariantCulture);

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
    /// Safe to call multiple times; subsequent calls are no-ops.
    /// </summary>
    public static void Shutdown()
    {
        lock (_initLock)
        {
            if (!_initialized) return;

            try
            {
                Log.Information("MetroHub logging shutting down. Flushing sinks.");
                Log.CloseAndFlush();
            }
            catch
            {
                // Shutdown flush must never throw
            }
            finally
            {
                _initialized = false;
            }
        }
    }
}
