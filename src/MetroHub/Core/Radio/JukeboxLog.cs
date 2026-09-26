using System;
using System.Diagnostics;
using System.IO;
using ManagedBass;

namespace MetroHub.Core.Radio;

/// <summary>
/// Removable debug log for the jukebox pipeline (search → resolve → BASS open → play).
/// Writes to %LocalAppData%\MetroHub\Logs\jukebox.log + debugger output.
/// To strip completely: delete this file and the one-line JukeboxLog.* call sites.
/// </summary>
public static class JukeboxLog
{
    /// <summary>Master switch. Turn off (or delete this file) to silence all jukebox logging.</summary>
    public static bool Enabled = true;

    private static readonly object _gate = new();
    private static string? _logPath;

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        if (!Enabled)
        {
            return;
        }

        string line = $"{DateTime.Now:HH:mm:ss.fff} [Jukebox] [{level}] {message}";
        Debug.WriteLine(line);

        try
        {
            lock (_gate)
            {
                _logPath ??= GetLogPath();
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never break playback.
        }
    }

    private static string GetLogPath()
    {
        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDir, "MetroHub", "Logs", "jukebox.log");
    }
}
