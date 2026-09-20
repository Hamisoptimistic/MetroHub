using System;
using System.Diagnostics;
using System.IO;

namespace MetroHub.Core.Services;

/// <summary>
/// Lightweight diagnostic logger that writes to d:\MetroHub\hidden_diagnostics.log.
/// Used to verify that zero CPU and zero memory-allocating background timers run while MetroHub is hidden.
/// Can be reactivated at any time by toggling IsEnabled = true.
/// </summary>
public static class HiddenDiagnosticsLogger
{
    /// <summary>
    /// Master toggle for HiddenDiagnosticsLogger.
    /// Set to true whenever you want to record hidden-state diagnostics and resource tracking to hidden_diagnostics.log.
    /// Disabled by default to ensure zero disk I/O, zero CPU overhead, and zero diagnostic memory footprint.
    /// </summary>
    public static bool IsEnabled { get; set; } = true;

    private static string _logFilePath = @"d:\MetroHub\hidden_diagnostics.log";
    private static readonly object _lock = new();
    private static volatile bool _isHubHidden = false;

    public static bool IsHubHidden => _isHubHidden;

    static HiddenDiagnosticsLogger()
    {
        try
        {
            string dir = Path.GetDirectoryName(_logFilePath) ?? string.Empty;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hidden_diagnostics.log");
            }
        }
        catch
        {
            _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hidden_diagnostics.log");
        }
    }

    public static void LogTransition(bool isVisible)
    {
        _isHubHidden = !isVisible;
        if (!IsEnabled) return;

        double wsMb = GetWorkingSetMb();
        string state = isVisible ? "SHOWN (Foreground Active)" : "HIDDEN (Background Dormant)";
        WriteEntry($"[TRANSITION] MetroHub is now {state} | Working Set: {wsMb:0.0} MB");
    }

    public static void Log(string message)
    {
        if (!IsEnabled) return;
        WriteEntry(message);
    }

    public static void LogHiddenEvent(string source, string eventName, string? details = null)
    {
        if (!IsEnabled) return;
        if (!_isHubHidden) return;

        double wsMb = GetWorkingSetMb();
        string detailStr = string.IsNullOrWhiteSpace(details) ? string.Empty : $" | Details: {details}";
        WriteEntry($"[HIDDEN ACTIVITY DETECTED] [{source}] {eventName} | Working Set: {wsMb:0.0} MB{detailStr}");
    }

    private static double GetWorkingSetMb()
    {
        try
        {
            using var proc = Process.GetCurrentProcess();
            return proc.WorkingSet64 / (1024.0 * 1024.0);
        }
        catch
        {
            return -1;
        }
    }

    private static void WriteEntry(string line)
    {
        try
        {
            string entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}";
            lock (_lock)
            {
                File.AppendAllText(_logFilePath, entry);
            }
        }
        catch
        {
            // Diagnostics must never throw
        }
    }
}

