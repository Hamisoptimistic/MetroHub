using System;
using System.IO;

namespace MetroHub.Widgets.Catalog.Markdown;

/// <summary>
/// Removable debug log for the Markdown widget (same pattern as the old JukeboxLog:
/// one <see cref="Enabled"/> flag; delete this file + its call sites to strip it).
/// Writes to %LocalAppData%\MetroHub\Logs\markdown.log. Disabled in unit tests.
/// </summary>
internal static class MarkdownLog
{
    /// <summary>Log is truncated when it grows past this (rotated by deletion).</summary>
    private const long MaxLogBytes = 512 * 1024;

    /// <summary>Writes allowed between size checks (each check stats the file).</summary>
    private const int SizeCheckEveryWrites = 64;

    /// <summary>
    /// Off in Release builds: this is a developer diagnostic that writes to disk synchronously
    /// from the render path. Debug builds keep it on, and any caller can opt back in.
    /// </summary>
#if DEBUG
    public static bool Enabled { get; set; } = true;
#else
    public static bool Enabled { get; set; }
#endif

    private static readonly object _gate = new();
    private static string? _path;
    private static int _writesSinceSizeCheck = SizeCheckEveryWrites; // first write checks the size

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    private static void Write(string level, string message)
    {
        if (!Enabled)
        {
            return;
        }
        try
        {
            lock (_gate)
            {
                _path ??= Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MetroHub", "Logs", "markdown.log");
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                if (++_writesSinceSizeCheck >= SizeCheckEveryWrites)
                {
                    _writesSinceSizeCheck = 0;
                    var info = new FileInfo(_path);
                    if (info.Exists && info.Length > MaxLogBytes)
                    {
                        File.Delete(_path); // Growth guard: start fresh once too big.
                    }
                }
                File.AppendAllText(_path,
                    $"{DateTime.Now:HH:mm:ss.fff} [Markdown] [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the widget.
        }
    }
}
