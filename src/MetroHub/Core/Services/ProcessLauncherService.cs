using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using MetroHub.Core.Messaging;

namespace MetroHub.Core.Services;

/// <summary>
/// Provides resilient process, application, URL, and shell target launching
/// with debounce protection, working directory inference, and decoupled error notifications.
/// </summary>
public static class ProcessLauncherService
{
    private static readonly object _launchLock = new();
    private static string? _lastLaunchKey;
    private static DateTime _lastLaunchTime = DateTime.MinValue;

    /// <summary>
    /// Launches a target synchronously. Returns true if the process was started successfully.
    /// </summary>
    public static bool LaunchTarget(string path, string? args = null, bool runAsAdmin = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string launchKey = $"{path.Trim()}|{args?.Trim()}|{runAsAdmin}";
        lock (_launchLock)
        {
            var now = DateTime.UtcNow;
            if (string.Equals(_lastLaunchKey, launchKey, StringComparison.OrdinalIgnoreCase) &&
                (now - _lastLaunchTime).TotalMilliseconds < 800)
            {
                return false;
            }

            _lastLaunchKey = launchKey;
            _lastLaunchTime = now;
        }

        try
        {
            LaunchTargetCore(path, args, runAsAdmin);
            return true;
        }
        catch (Exception ex)
        {
            Safe.Log(ex, $"ProcessLauncherService.LaunchTarget({path})");
            return false;
        }
    }

    /// <summary>
    /// Launches a target asynchronously on a background task, emitting <see cref="TargetLaunchFailedMessage"/>
    /// via <see cref="WeakReferenceMessenger"/> upon failure.
    /// </summary>
    public static void LaunchTargetAsync(string path, string? args = null, bool runAsAdmin = false, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        string launchKey = $"{path.Trim()}|{args?.Trim()}|{runAsAdmin}";
        lock (_launchLock)
        {
            var now = DateTime.UtcNow;
            if (string.Equals(_lastLaunchKey, launchKey, StringComparison.OrdinalIgnoreCase) &&
                (now - _lastLaunchTime).TotalMilliseconds < 800)
            {
                return;
            }

            _lastLaunchKey = launchKey;
            _lastLaunchTime = now;
        }

        _ = Task.Run(() =>
        {
            try
            {
                LaunchTargetCore(path, args, runAsAdmin);
            }
            catch (System.ComponentModel.Win32Exception win32Ex) when (win32Ex.NativeErrorCode == 1223)
            {
                // 1223 = ERROR_CANCELLED: User clicked "No" / "Cancel" on UAC prompt. Not an error.
            }
            catch (Exception ex)
            {
                string title = !string.IsNullOrWhiteSpace(displayName)
                    ? displayName
                    : Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(title)) title = path;

                Safe.Log(ex, $"Failed to launch target '{title}' ({path})");
                WeakReferenceMessenger.Default.Send(new TargetLaunchFailedMessage(title, ex));
            }
        });
    }

    /// <summary>
    /// Normalizes URL and protocol paths (e.g. "www.google.com" or "example.ai" -> "https://...").
    /// </summary>
    public static string NormalizeTargetPath(string path)
    {
        string targetPath = path.Trim();
        if (targetPath.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            return "https://" + targetPath;
        }

        if (!targetPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !targetPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !targetPath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) &&
            !targetPath.Contains('\\') &&
            !File.Exists(targetPath) &&
            !Directory.Exists(targetPath) &&
            (targetPath.EndsWith(".com", StringComparison.OrdinalIgnoreCase) ||
             targetPath.EndsWith(".org", StringComparison.OrdinalIgnoreCase) ||
             targetPath.EndsWith(".net", StringComparison.OrdinalIgnoreCase) ||
             targetPath.EndsWith(".io", StringComparison.OrdinalIgnoreCase) ||
             targetPath.EndsWith(".tv", StringComparison.OrdinalIgnoreCase) ||
             targetPath.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ||
             targetPath.EndsWith(".ai", StringComparison.OrdinalIgnoreCase)))
        {
            return "https://" + targetPath;
        }

        return targetPath;
    }

    private static void LaunchTargetCore(string path, string? args, bool runAsAdmin)
    {
        string targetPath = NormalizeTargetPath(path);

        var psi = new ProcessStartInfo
        {
            FileName = targetPath,
            Arguments = args ?? string.Empty,
            UseShellExecute = true
        };

        string? workDir = ResolveWorkingDirectory(path);
        if (!string.IsNullOrWhiteSpace(workDir) && Directory.Exists(workDir))
        {
            psi.WorkingDirectory = workDir;
        }

        if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = "explorer.exe";
            psi.Arguments = $"\"{path}\"";
        }

        if (runAsAdmin)
        {
            psi.Verb = "runas";
        }

        Process.Start(psi);
    }

    /// <summary>
    /// Attempts to determine the most appropriate working directory for a given executable or shortcut target.
    /// </summary>
    public static string? ResolveWorkingDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            string kf = IconExtractorService.ResolveKnownFolderGuid(path);
            if (File.Exists(kf) || Directory.Exists(kf)) path = kf;

            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType != null)
                {
                    dynamic? shell = Activator.CreateInstance(shellType);
                    if (shell != null)
                    {
                        try
                        {
                            dynamic shortcut = shell.CreateShortcut(path);
                            string workDir = shortcut.WorkingDirectory?.ToString() ?? string.Empty;
                            string target = shortcut.TargetPath?.ToString() ?? string.Empty;
                            Marshal.FinalReleaseComObject(shortcut);

                            if (!string.IsNullOrWhiteSpace(workDir) && Directory.Exists(workDir))
                            {
                                return workDir;
                            }

                            if (!string.IsNullOrWhiteSpace(target))
                            {
                                string resolvedTarget = IconExtractorService.ResolveKnownFolderGuid(target);
                                if (File.Exists(resolvedTarget))
                                {
                                    return Path.GetDirectoryName(resolvedTarget);
                                }
                                if (File.Exists(target))
                                {
                                    return Path.GetDirectoryName(target);
                                }
                            }
                        }
                        finally
                        {
                            Marshal.FinalReleaseComObject(shell);
                        }
                    }
                }
                return Path.GetDirectoryName(path);
            }

            if (File.Exists(path))
            {
                return Path.GetDirectoryName(path);
            }

            if (Directory.Exists(path))
            {
                return path;
            }
        }
        catch (Exception ex)
        {
            Safe.Log(ex, $"ProcessLauncherService.ResolveWorkingDirectory({path})");
        }

        return null;
    }
}
