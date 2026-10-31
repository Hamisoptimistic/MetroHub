using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MetroHub.Core.Services;
using Microsoft.Win32;

namespace MetroHub.Core.Search;

/// <summary>
/// Hardened launcher for Microsoft PowerToys Peek.
/// Resolves executable location safely with kernel handle cleanup, argument escaping,
/// dual-registry scope, per-process exception shielding, and self-healing caching.
/// </summary>
public static class PowerToysPeekLauncher
{
    private static readonly object _lock = new();
    private static string? _cachedExePath;
    private static DateTime _lastFailedCheck = DateTime.MinValue;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets whether PowerToys Peek is installed and available on the system.
    /// </summary>
    public static bool IsAvailable => GetPeekPath() != null;

    /// <summary>
    /// Resolves the absolute path to PowerToys.Peek.UI.exe using a multi-tier fallback.
    /// </summary>
    public static string? GetPeekPath()
    {
        lock (_lock)
        {
            if (CustomPeekPathResolver != null)
            {
                return CustomPeekPathResolver();
            }

            if (_cachedExePath != null)
            {
                if (File.Exists(_cachedExePath))
                {
                    return _cachedExePath;
                }

                _cachedExePath = null;
            }

            // Avoid hammering Registry/Process checks if PowerToys isn't installed,
            // but allow re-discovery after 1 minute in case the user installs it.
            if ((DateTime.UtcNow - _lastFailedCheck) < RetryInterval)
            {
                return null;
            }

            _cachedExePath = CustomFindPeekExe != null ? CustomFindPeekExe() : FindPeekExe();
            if (_cachedExePath == null)
            {
                _lastFailedCheck = DateTime.UtcNow;
            }

            return _cachedExePath;
        }
    }

    private static string? FindPeekExe()
    {
        // 1. Standard user-scope path (%LOCALAPPDATA%)
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string userPath = Path.Combine(localAppData, @"PowerToys\WinUI3Apps\PowerToys.Peek.UI.exe");
        if (File.Exists(userPath)) return userPath;

        // 2. Standard machine-scope path (%ProgramFiles%)
        string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string machinePath = Path.Combine(progFiles, @"PowerToys\WinUI3Apps\PowerToys.Peek.UI.exe");
        if (File.Exists(machinePath)) return machinePath;

        // 3. Registry App Paths (Check both HKCU and HKLM)
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\PowerToys.exe");
                if (key?.GetValue(null) is string appPath && File.Exists(appPath))
                {
                    string? dir = Path.GetDirectoryName(appPath);
                    if (dir != null)
                    {
                        string peekPath = Path.Combine(dir, @"WinUI3Apps\PowerToys.Peek.UI.exe");
                        if (File.Exists(peekPath)) return peekPath;
                    }
                }
            }
            catch
            {
                // Ignore registry access errors
            }
        }

        // 4. Registry Uninstall keys (Dual-registry & 64/32-bit scope for custom install locations)
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstallKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstallKey == null) continue;

                    foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                    {
                        using var subKey = uninstallKey.OpenSubKey(subKeyName);
                        if (subKey?.GetValue("DisplayName") is string name && name.Contains("PowerToys", StringComparison.OrdinalIgnoreCase))
                        {
                            if (subKey.GetValue("InstallLocation") is string installLoc && Directory.Exists(installLoc))
                            {
                                string peekPath = Path.Combine(installLoc, @"WinUI3Apps\PowerToys.Peek.UI.exe");
                                if (File.Exists(peekPath)) return peekPath;
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore registry access errors
                }
            }
        }

        // 5. Last resort: Running process inspection (Check Peek UI first, then PowerToys main process)
        foreach (string procName in new[] { "PowerToys.Peek.UI", "PowerToys" })
        {
            Process[] processes = Array.Empty<Process>();
            try
            {
                processes = Process.GetProcessesByName(procName);
                foreach (var proc in processes)
                {
                    try
                    {
                        string? exePath = TryGetProcessPath(proc);
                        if (exePath != null)
                        {
                            if (procName.Equals("PowerToys.Peek.UI", StringComparison.OrdinalIgnoreCase))
                            {
                                if (File.Exists(exePath)) return exePath;
                            }
                            else
                            {
                                string? dir = Path.GetDirectoryName(exePath);
                                if (dir != null)
                                {
                                    string peekPath = Path.Combine(dir, @"WinUI3Apps\PowerToys.Peek.UI.exe");
                                    if (File.Exists(peekPath)) return peekPath;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Shield against individual process inspection errors
                    }
                }
            }
            catch
            {
                // Ignore process enumeration errors
            }
            finally
            {
                foreach (var proc in processes)
                {
                    proc.Dispose();
                }
            }
        }

        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, [Out] StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private static string? TryGetProcessPath(Process proc)
    {
        try
        {
            if (proc.MainModule?.FileName is string path)
            {
                return path;
            }
        }
        catch
        {
            // MainModule throws Win32Exception (Access Denied) when PowerToys is running elevated.
            // Fall back to limited-query process image inspection which works across integrity levels.
        }

        return GetProcessPathViaLimitedQuery(proc.Id);
    }

    internal static string? GetProcessPathViaLimitedQuery(int pid)
    {
        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == IntPtr.Zero) return null;

        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (QueryFullProcessImageName(hProcess, 0, sb, ref size))
            {
                return sb.ToString();
            }
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    /// <summary>
    /// Launches PowerToys Peek preview for the specified file or folder path.
    /// </summary>
    /// <param name="targetPath">Absolute path of the target file or directory.</param>
    /// <returns>True if the preview process was successfully invoked; otherwise false.</returns>
    public static bool TryLaunchPreview(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return false;
        }

        targetPath = targetPath.Trim().Trim('"');

        if (!Path.IsPathFullyQualified(targetPath) ||
            !Path.Exists(targetPath))
        {
            return false;
        }

        string? peekExe = GetPeekPath();
        if (peekExe == null)
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = peekExe,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(targetPath);

            if (ProcessStarter != null)
            {
                return ProcessStarter(startInfo);
            }

            using var process = Process.Start(startInfo);
            return process != null;
        }
        catch (Exception ex)
        {
            Safe.Log("PowerToysPeekLauncher.TryLaunchPreview", ex);
            return false;
        }
    }

    internal static Func<string?>? CustomPeekPathResolver { get; set; }
    internal static Func<string?>? CustomFindPeekExe { get; set; }
    internal static Func<ProcessStartInfo, bool>? ProcessStarter { get; set; }

    internal static void ResetCacheForTesting()
    {
        lock (_lock)
        {
            _cachedExePath = null;
            _lastFailedCheck = DateTime.MinValue;
            CustomPeekPathResolver = null;
            CustomFindPeekExe = null;
            ProcessStarter = null;
        }
    }
}
