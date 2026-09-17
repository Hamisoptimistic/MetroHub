using System;
using System.Diagnostics;
using System.IO;

namespace MetroHub.Core.Services;

/// <summary>
/// Lightweight diagnostic logger that writes to d:\MetroHub\hidden_diagnostics.log.
/// Used to verify that zero CPU and zero memory-allocating background timers run while MetroHub is hidden.
/// </summary>
public static class HiddenDiagnosticsLogger
{
    private static string _logFilePath = @"d:\MetroHub\hidden_diagnostics.log";
    private static readonly object _lock = new();
    private static volatile bool _isHubHidden = false;

    public static bool IsHubHidden => _isHubHidden;

    static HiddenDiagnosticsLogger()
    {
        try
        {
            string dir = Path.GetDirectoryName(_logFilePath) ?? string.Empty;
            if (!Directory.Exists(dir))
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
        double wsMb = GetWorkingSetMb();
        string state = isVisible ? "SHOWN (Foreground Active)" : "HIDDEN (Background Dormant)";
        WriteEntry($"[TRANSITION] MetroHub is now {state} | Working Set: {wsMb:0.0} MB");
    }

    public static void LogHiddenEvent(string source, string eventName, string? details = null)
    {
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

