using System;
using System.IO;
using System.Threading;

namespace MetroHub.Core.Services;

/// <summary>
/// TEMPORARY unbuffered diagnostic sink for scroll/backdrop probing.
/// Writes straight to %TEMP%\MetroHub_ScrollTrace.log so measurements are readable
/// while the app is still running (the Serilog file sink is buffered and only
/// flushes on shutdown).
/// Delete together with the [ScrollPacing] / [BackdropMode] probes.
/// </summary>
internal static class ScrollTrace
{
    private static readonly Lock Sync = new();

    public static string FilePath { get; } =
        Path.Combine(Path.GetTempPath(), "MetroHub_ScrollTrace.log");

    /// <summary>
    /// Truncates the trace once per process so each run starts clean.
    /// </summary>
    static ScrollTrace()
    {
        try
        {
            File.WriteAllText(FilePath, $"=== MetroHub scroll trace {DateTime.Now:yyyy-MM-dd HH:mm:ss} pid={Environment.ProcessId} ==={Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never fault the UI thread.
        }
    }

    public static void Write(string message)
    {
        try
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
            lock (Sync)
            {
                File.AppendAllText(FilePath, line);
            }
        }
        catch
        {
            // Diagnostics must never fault the UI thread.
        }
    }
}
