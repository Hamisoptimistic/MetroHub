using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MetroHub.Core.Common;
using MetroHub.Core.Models;

namespace MetroHub.Core.Services;

/// <summary>
/// Provides zero-polling, kernel wait-backed tracking of active applications on Windows.
/// Reflects running state onto pinned tiles via <see cref="TileModel.IsRunning"/>.
/// </summary>
public sealed partial class RunningAppTrackerService : IDisposable
{
    private sealed class ResolvedTargetInfo
    {
        public string? FullExePath { get; init; }
        public string? ExeFileName { get; init; }
        public string? PackageFamilyName { get; init; }
        public string? SquirrelRootDir { get; init; }
        public string? SquirrelExeName { get; init; }
    }

    private sealed class TrackedTile
    {
        public TileModel Model { get; }
        public string? FullExePath { get; }
        public string? ExeFileName { get; }
        public string? PackageFamilyName { get; }
        public string? SquirrelRootDir { get; }
        public string? SquirrelExeName { get; }
        public HashSet<int> RunningPids { get; } = new();

        public TrackedTile(TileModel model, ResolvedTargetInfo info)
        {
            Model = model;
            FullExePath = info.FullExePath;
            ExeFileName = info.ExeFileName;
            PackageFamilyName = info.PackageFamilyName;
            SquirrelRootDir = info.SquirrelRootDir;
            SquirrelExeName = info.SquirrelExeName;
        }

        public bool Matches(string? procExePath, string? procPackageFamilyName)
        {
            // 1. Packaged / UWP application match via PackageFamilyName
            if (!string.IsNullOrEmpty(PackageFamilyName))
            {
                if (!string.IsNullOrEmpty(procPackageFamilyName) &&
                    string.Equals(PackageFamilyName, procPackageFamilyName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // Fallback: check if procExePath contains the PackageFamilyName parts
                if (!string.IsNullOrEmpty(procExePath))
                {
                    int underscore = PackageFamilyName.LastIndexOf('_');
                    if (underscore > 0)
                    {
                        string namePart = PackageFamilyName.Substring(0, underscore);
                        string pubPart = PackageFamilyName.Substring(underscore + 1);
                        if (procExePath.Contains(namePart, StringComparison.OrdinalIgnoreCase) &&
                            procExePath.Contains(pubPart, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(procExePath))
            {
                return false;
            }

            string procFileName = Path.GetFileName(procExePath);

            // 2. Exact full executable path match
            if (!string.IsNullOrEmpty(FullExePath))
            {
                if (string.Equals(FullExePath, procExePath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // 3. Squirrel / Electron stub match
            if (!string.IsNullOrEmpty(SquirrelExeName) && !string.IsNullOrEmpty(SquirrelRootDir))
            {
                if (string.Equals(SquirrelExeName, procFileName, StringComparison.OrdinalIgnoreCase) &&
                    procExePath.StartsWith(SquirrelRootDir, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // 4. Executable filename fallback (only when full path wasn't resolvable)
            if (string.IsNullOrEmpty(FullExePath) && !string.IsNullOrEmpty(ExeFileName))
            {
                if (string.Equals(ExeFileName, procFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private sealed class TrackedProcess
    {
        public int Pid { get; init; }
        public IntPtr ProcessHandle { get; init; }
        public IntPtr WaitHandle { get; init; }
        public string ExePath { get; init; } = string.Empty;
        public string? PackageFamilyName { get; init; }
    }

    private static readonly ConcurrentDictionary<string, ResolvedTargetInfo> s_resolvedCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly SettingsService _settingsService;
    private readonly Lock _syncLock = new();
    private readonly NativeMethods.WinEventDelegate _winEventDelegate;
    private readonly NativeMethods.WaitOrTimerCallback _waitOrTimerCallback;
    private IntPtr _hookShow = IntPtr.Zero;
    private IntPtr _hookForeground = IntPtr.Zero;
    private bool _isDisposed;
    private bool _isInitialized;

    private List<TrackedTile> _trackedTiles = new();
    private readonly Dictionary<int, TrackedProcess> _trackedProcesses = new();
    private readonly Queue<int> _unmatchedPidsOrder = new();
    private readonly HashSet<int> _unmatchedPids = new();
    private const int MaxUnmatchedPids = 128;

    internal bool AreHooksInstalled => _hookShow != IntPtr.Zero && _hookForeground != IntPtr.Zero;
    internal int TrackedTileCount => _trackedTiles.Count;
    internal int TrackedProcessCount => _trackedProcesses.Count;

    public RunningAppTrackerService(SettingsService? settingsService = null)
    {
        _settingsService = settingsService ?? SettingsService.Instance;
        _winEventDelegate = OnWinEvent;
        _waitOrTimerCallback = OnProcessExited;
        _settingsService.SettingChanged += OnSettingChanged;
    }

    /// <summary>
    /// Installs system WinEvent hooks on the UI thread and initializes tracking for the given tiles.
    /// </summary>
    public void Initialize(IEnumerable<TileModel>? initialTiles)
    {
        if (_isDisposed) return;
        _isInitialized = true;

        if (initialTiles != null)
        {
            UpdateTiles(initialTiles);
        }

        if (_settingsService.Current.ShowRunningAppIndicators)
        {
            InstallHooks();
        }
    }

    private void InstallHooks()
    {
        if (_isDisposed) return;

        lock (_syncLock)
        {
            if (_isDisposed || !_settingsService.Current.ShowRunningAppIndicators) return;

            if (_hookShow == IntPtr.Zero)
            {
                _hookShow = NativeMethods.SetWinEventHook(
                    NativeMethods.EVENT_OBJECT_SHOW,
                    NativeMethods.EVENT_OBJECT_SHOW,
                    IntPtr.Zero,
                    _winEventDelegate,
                    0,
                    0,
                    NativeMethods.WINEVENT_OUTOFCONTEXT);
            }

            if (_hookForeground == IntPtr.Zero)
            {
                _hookForeground = NativeMethods.SetWinEventHook(
                    NativeMethods.EVENT_SYSTEM_FOREGROUND,
                    NativeMethods.EVENT_SYSTEM_FOREGROUND,
                    IntPtr.Zero,
                    _winEventDelegate,
                    0,
                    0,
                    NativeMethods.WINEVENT_OUTOFCONTEXT);
            }
        }
    }

    private void UninstallHooks()
    {
        lock (_syncLock)
        {
            if (_hookShow != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_hookShow);
                _hookShow = IntPtr.Zero;
            }

            if (_hookForeground != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_hookForeground);
                _hookForeground = IntPtr.Zero;
            }
        }
    }

    private void OnSettingChanged(string? propertyName)
    {
        if (_isDisposed) return;
        if (!string.Equals(propertyName, nameof(AppSettings.ShowRunningAppIndicators), StringComparison.Ordinal)) return;

        if (_settingsService.Current.ShowRunningAppIndicators)
        {
            if (_isInitialized)
            {
                EnableTracking();
            }
        }
        else
        {
            DisableTracking();
        }
    }

    private void EnableTracking()
    {
        InstallHooks();
        Task.Run(ScanRunningWindows).FireAndForget("RunningAppTrackerService.EnableTrackingScan");
    }

    private void DisableTracking()
    {
        UninstallHooks();

        List<TrackedProcess> processesToClose;
        List<TileModel> tilesToTurnOff = new();

        lock (_syncLock)
        {
            processesToClose = new List<TrackedProcess>(_trackedProcesses.Values);
            _trackedProcesses.Clear();
            _unmatchedPids.Clear();
            _unmatchedPidsOrder.Clear();

            foreach (var tile in _trackedTiles)
            {
                tile.RunningPids.Clear();
                if (tile.Model.IsRunning)
                {
                    tilesToTurnOff.Add(tile.Model);
                }
            }
        }

        CleanupTrackedProcesses(processesToClose);

        if (tilesToTurnOff.Count > 0)
        {
            NotifyTilesRunning(tilesToTurnOff, false);
        }
    }

    private static void CleanupTrackedProcesses(List<TrackedProcess> processes)
    {
        if (processes.Count == 0) return;

        Task.Run(() =>
        {
            foreach (var proc in processes)
            {
                try
                {
                    if (proc.WaitHandle != IntPtr.Zero)
                    {
                        NativeMethods.UnregisterWaitEx(proc.WaitHandle, NativeMethods.INVALID_HANDLE_VALUE);
                    }
                }
                catch (Exception ex)
                {
                    Safe.Ignore(ex, "RunningAppTrackerService.CleanupTrackedProcesses.UnregisterWaitEx");
                }
                finally
                {
                    if (proc.ProcessHandle != IntPtr.Zero)
                    {
                        NativeMethods.CloseHandle(proc.ProcessHandle);
                    }
                }
            }
        }).FireAndForget("RunningAppTrackerService.CleanupTrackedProcesses");
    }

    /// <summary>
    /// Re-indexes pinned tiles (e.g. on workspace switch or collection modification)
    /// and updates their running state against actively tracked processes.
    /// </summary>
    public void UpdateTiles(IEnumerable<TileModel>? tiles)
    {
        if (_isDisposed || tiles == null) return;

        bool isTrackingEnabled = _settingsService.Current.ShowRunningAppIndicators;

        var newTrackedTiles = new List<TrackedTile>();

        foreach (var tile in tiles)
        {
            if (tile.TileType != TileType.App || string.IsNullOrWhiteSpace(tile.TargetPath))
            {
                tile.IsRunning = false;
                continue;
            }

            var tracked = CreateTrackedTile(tile);
            if (tracked != null)
            {
                newTrackedTiles.Add(tracked);
            }
        }

        if (!isTrackingEnabled)
        {
            lock (_syncLock)
            {
                _trackedTiles = newTrackedTiles;
                foreach (var t in _trackedTiles)
                {
                    t.RunningPids.Clear();
                    t.Model.IsRunning = false;
                }
            }
            return;
        }

        List<TileModel>? tilesToTurnOn = null;
        List<TileModel>? tilesToTurnOff = null;

        lock (_syncLock)
        {
            _unmatchedPids.Clear();
            _unmatchedPidsOrder.Clear();

            foreach (var trackedTile in newTrackedTiles)
            {
                foreach (var proc in _trackedProcesses.Values)
                {
                    if (trackedTile.Matches(proc.ExePath, proc.PackageFamilyName))
                    {
                        trackedTile.RunningPids.Add(proc.Pid);
                    }
                }

                bool shouldBeRunning = trackedTile.RunningPids.Count > 0;
                if (trackedTile.Model.IsRunning != shouldBeRunning)
                {
                    if (shouldBeRunning)
                    {
                        tilesToTurnOn ??= new List<TileModel>();
                        tilesToTurnOn.Add(trackedTile.Model);
                    }
                    else
                    {
                        tilesToTurnOff ??= new List<TileModel>();
                        tilesToTurnOff.Add(trackedTile.Model);
                    }
                }
            }

            _trackedTiles = newTrackedTiles;
        }

        if (tilesToTurnOn != null && tilesToTurnOn.Count > 0)
        {
            NotifyTilesRunning(tilesToTurnOn, true);
        }
        if (tilesToTurnOff != null && tilesToTurnOff.Count > 0)
        {
            NotifyTilesRunning(tilesToTurnOff, false);
        }

        // Perform initial or incremental window scan asynchronously
        Task.Run(ScanRunningWindows).FireAndForget("RunningAppTrackerService.UpdateTilesScan");
    }

    private void AddUnmatchedPid(int pid)
    {
        if (_unmatchedPids.Add(pid))
        {
            _unmatchedPidsOrder.Enqueue(pid);
            while (_unmatchedPidsOrder.Count > MaxUnmatchedPids)
            {
                int oldest = _unmatchedPidsOrder.Dequeue();
                _unmatchedPids.Remove(oldest);
            }
        }
    }

    private void ScanRunningWindows()
    {
        if (_isDisposed || !_settingsService.Current.ShowRunningAppIndicators) return;

        try
        {
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (_isDisposed || !_settingsService.Current.ShowRunningAppIndicators) return false;
                if (NativeMethods.IsWindow(hwnd))
                {
                    ProcessWindow(hwnd);
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "RunningAppTrackerService.ScanRunningWindows");
        }
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // Filter to top-level window objects (OBJID_WINDOW = 0)
        if (idObject != 0 || hwnd == IntPtr.Zero || _isDisposed || !_settingsService.Current.ShowRunningAppIndicators) return;

        // Dispatch off the hook message loop immediately
        Task.Run(() => ProcessWindow(hwnd)).FireAndForget("RunningAppTrackerService.OnWinEvent");
    }

    private void ProcessWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _isDisposed || !_settingsService.Current.ShowRunningAppIndicators) return;

        if (NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid) == 0 ||
            pid == 0 ||
            pid == (uint)Environment.ProcessId)
        {
            return;
        }

        int processId = (int)pid;

        // Handle UWP / Store applications hosted inside ApplicationFrameHost
        string className = NativeMethods.GetWindowClassName(hwnd);
        if (string.Equals(className, "ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase))
        {
            uint childPid = 0;
            NativeMethods.EnumChildWindows(hwnd, (childHwnd, _) =>
            {
                if (NativeMethods.GetWindowThreadProcessId(childHwnd, out uint cPid) != 0 &&
                    cPid != 0 &&
                    cPid != pid &&
                    cPid != (uint)Environment.ProcessId)
                {
                    childPid = cPid;
                    return false; // Found child process, stop enumeration
                }
                return true;
            }, IntPtr.Zero);

            if (childPid != 0)
            {
                processId = (int)childPid;
            }
            else
            {
                // Frame window without child app attached yet; do not track ApplicationFrameHost itself
                return;
            }
        }

        lock (_syncLock)
        {
            if (_isDisposed || !_settingsService.Current.ShowRunningAppIndicators) return;
            if (_trackedProcesses.ContainsKey(processId)) return;
            if (_unmatchedPids.Contains(processId)) return;
        }

        // Open kernel process handle with SYNCHRONIZE and PROCESS_QUERY_LIMITED_INFORMATION
        IntPtr hProcess = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.SYNCHRONIZE,
            false,
            processId);

        if (hProcess == IntPtr.Zero)
        {
            lock (_syncLock)
            {
                AddUnmatchedPid(processId);
            }
            return;
        }

        string? exePath = NativeMethods.TryGetProcessPath(hProcess);
        string? packageFamilyName = NativeMethods.TryGetPackageFamilyName(hProcess);

        if (string.IsNullOrEmpty(exePath) && string.IsNullOrEmpty(packageFamilyName))
        {
            NativeMethods.CloseHandle(hProcess);
            lock (_syncLock)
            {
                AddUnmatchedPid(processId);
            }
            return;
        }

        List<TileModel>? matchedTiles = null;
        lock (_syncLock)
        {
            if (_isDisposed || !_settingsService.Current.ShowRunningAppIndicators || _trackedProcesses.ContainsKey(processId))
            {
                NativeMethods.CloseHandle(hProcess);
                return;
            }

            foreach (var tile in _trackedTiles)
            {
                if (tile.Matches(exePath, packageFamilyName))
                {
                    matchedTiles ??= new List<TileModel>();
                    matchedTiles.Add(tile.Model);
                    tile.RunningPids.Add(processId);
                }
            }

            if (matchedTiles != null && matchedTiles.Count > 0)
            {
                // Register zero-polling kernel wait for process termination
                bool registered = NativeMethods.RegisterWaitForSingleObject(
                    out IntPtr waitHandle,
                    hProcess,
                    _waitOrTimerCallback,
                    new IntPtr(processId),
                    NativeMethods.INFINITE,
                    NativeMethods.WT_EXECUTEONLYONCE);

                if (registered)
                {
                    _trackedProcesses[processId] = new TrackedProcess
                    {
                        Pid = processId,
                        ProcessHandle = hProcess,
                        WaitHandle = waitHandle,
                        ExePath = exePath ?? string.Empty,
                        PackageFamilyName = packageFamilyName
                    };
                }
                else
                {
                    NativeMethods.CloseHandle(hProcess);
                }
            }
            else
            {
                NativeMethods.CloseHandle(hProcess);
                AddUnmatchedPid(processId);
            }
        }

        if (matchedTiles != null && matchedTiles.Count > 0)
        {
            NotifyTilesRunning(matchedTiles, true);
        }
    }

    private void OnProcessExited(IntPtr lpParameter, bool timerOrWaitFired)
    {
        if (_isDisposed || !_settingsService.Current.ShowRunningAppIndicators) return;

        int pid = lpParameter.ToInt32();
        TrackedProcess? tracked = null;
        List<TileModel>? tilesToTurnOff = null;

        lock (_syncLock)
        {
            if (_trackedProcesses.Remove(pid, out tracked))
            {
                foreach (var tile in _trackedTiles)
                {
                    if (tile.RunningPids.Remove(pid) && tile.RunningPids.Count == 0)
                    {
                        tilesToTurnOff ??= new List<TileModel>();
                        tilesToTurnOff.Add(tile.Model);
                    }
                }
            }
        }

        if (tracked != null)
        {
            IntPtr waitHandle = tracked.WaitHandle;
            IntPtr procHandle = tracked.ProcessHandle;

            Task.Run(() =>
            {
                try
                {
                    if (waitHandle != IntPtr.Zero)
                    {
                        NativeMethods.UnregisterWaitEx(waitHandle, IntPtr.Zero);
                    }
                }
                catch (Exception ex)
                {
                    Safe.Ignore(ex, "RunningAppTrackerService.UnregisterWaitEx");
                }
                finally
                {
                    if (procHandle != IntPtr.Zero)
                    {
                        NativeMethods.CloseHandle(procHandle);
                    }
                }
            }).FireAndForget("RunningAppTrackerService.CleanupExitedProcess");
        }

        if (tilesToTurnOff != null && tilesToTurnOff.Count > 0)
        {
            NotifyTilesRunning(tilesToTurnOff, false);
        }
    }

    private static void NotifyTilesRunning(List<TileModel> tiles, bool isRunning)
    {
        foreach (var t in tiles)
        {
            t.IsRunning = isRunning;
        }
    }

    private static TrackedTile? CreateTrackedTile(TileModel tile)
    {
        string target = tile.TargetPath?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(target)) return null;

        string tileArgs = tile.Arguments?.Trim() ?? string.Empty;
        string cacheKey = $"{target}|{tileArgs}";

        if (!s_resolvedCache.TryGetValue(cacheKey, out var info))
        {
            info = ResolveTargetInfo(target, tileArgs);
            s_resolvedCache.TryAdd(cacheKey, info);
        }

        return new TrackedTile(tile, info);
    }

    private static ResolvedTargetInfo ResolveTargetInfo(string target, string tileArgs)
    {
        string? fullExePath = null;
        string? exeFileName = null;
        string? packageFamilyName = null;
        string? squirrelRootDir = null;
        string? squirrelExeName = null;

        // 1. Check for UWP shell:AppsFolder or PackageFamilyName!AppId
        if (target.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase) ||
            (target.Contains('!') && !target.Contains('\\')))
        {
            string clean = target;
            if (clean.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean.Substring("shell:AppsFolder\\".Length);
            }
            int bang = clean.IndexOf('!');
            packageFamilyName = bang >= 0 ? clean.Substring(0, bang) : clean;
            return new ResolvedTargetInfo
            {
                PackageFamilyName = packageFamilyName?.ToLowerInvariant()
            };
        }

        // 2. Resolve known folder GUID if present
        string resolvedPath = IconExtractorService.ResolveKnownFolderGuid(target);
        if (File.Exists(resolvedPath))
        {
            target = resolvedPath;
        }

        // 3. Resolve Windows shortcut (.lnk)
        if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
        {
            try
            {
                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType != null)
                {
                    dynamic? shell = Activator.CreateInstance(shellType);
                    if (shell != null)
                    {
                        try
                        {
                            dynamic shortcut = shell.CreateShortcut(target);
                            string lnkTarget = shortcut.TargetPath?.ToString() ?? string.Empty;
                            string lnkArgs = shortcut.Arguments?.ToString() ?? string.Empty;
                            Marshal.FinalReleaseComObject(shortcut);

                            if (!string.IsNullOrWhiteSpace(lnkTarget))
                            {
                                string resolvedLnkTarget = IconExtractorService.ResolveKnownFolderGuid(lnkTarget);
                                target = File.Exists(resolvedLnkTarget) ? resolvedLnkTarget : lnkTarget;
                            }

                            if (!string.IsNullOrWhiteSpace(lnkArgs) && string.IsNullOrWhiteSpace(tileArgs))
                            {
                                tileArgs = lnkArgs;
                            }
                        }
                        finally
                        {
                            Marshal.FinalReleaseComObject(shell);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Safe.Ignore(ex, "RunningAppTrackerService.ResolveShortcut");
            }
        }

        // 4. Check for Squirrel / Electron Update.exe stub
        if (target.EndsWith("Update.exe", StringComparison.OrdinalIgnoreCase))
        {
            string? dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir))
            {
                squirrelRootDir = EnsureTrailingSlash(dir);
                squirrelExeName = ExtractSquirrelExeName(tileArgs, dir);
            }
        }

        // 5. Standard executable path
        if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            if (Path.IsPathRooted(target))
            {
                try
                {
                    fullExePath = Path.GetFullPath(target);
                }
                catch
                {
                    fullExePath = target;
                }
                exeFileName = Path.GetFileName(target);
            }
            else
            {
                exeFileName = target;
            }
        }
        else if (!string.IsNullOrEmpty(target) && !target.Contains('\\'))
        {
            exeFileName = target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? target : target + ".exe";
        }

        return new ResolvedTargetInfo
        {
            FullExePath = fullExePath?.ToLowerInvariant(),
            ExeFileName = exeFileName?.ToLowerInvariant(),
            PackageFamilyName = packageFamilyName?.ToLowerInvariant(),
            SquirrelRootDir = squirrelRootDir?.ToLowerInvariant(),
            SquirrelExeName = squirrelExeName?.ToLowerInvariant()
        };
    }

    [GeneratedRegex(@"--(?:processStart|process-start)[\s=:""]+(?<exe>[^""\s]+)", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SquirrelExeRegex();

    private static string? ExtractSquirrelExeName(string args, string dir)
    {
        if (!string.IsNullOrEmpty(args))
        {
            var match = SquirrelExeRegex().Match(args);
            if (match.Success)
            {
                var group = match.Groups["exe"];
                if (group.Success && !string.IsNullOrEmpty(group.Value))
                {
                    return Path.GetFileName(group.Value);
                }
            }
        }

        try
        {
            string parentName = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrEmpty(parentName))
            {
                string expectedExe = parentName + ".exe";
                var appDirs = Directory.GetDirectories(dir, "app-*");
                if (appDirs.Length > 0)
                {
                    return expectedExe;
                }
            }
        }
        catch (Exception ex)
        {
            Safe.Ignore(ex, "RunningAppTrackerService.ExtractSquirrelExeName");
        }

        return null;
    }

    private static string EnsureTrailingSlash(string path)
    {
        if (!path.EndsWith(Path.DirectorySeparatorChar) && !path.EndsWith(Path.AltDirectorySeparatorChar))
        {
            return path + Path.DirectorySeparatorChar;
        }
        return path;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _settingsService.SettingChanged -= OnSettingChanged;
        UninstallHooks();

        List<TrackedProcess> processesToClose;
        lock (_syncLock)
        {
            processesToClose = new List<TrackedProcess>(_trackedProcesses.Values);
            _trackedProcesses.Clear();
            _trackedTiles.Clear();
            _unmatchedPids.Clear();
            _unmatchedPidsOrder.Clear();
        }

        foreach (var proc in processesToClose)
        {
            try
            {
                if (proc.WaitHandle != IntPtr.Zero)
                {
                    NativeMethods.UnregisterWaitEx(proc.WaitHandle, NativeMethods.INVALID_HANDLE_VALUE);
                }
            }
            catch (Exception ex)
            {
                Safe.Ignore(ex, "RunningAppTrackerService.Dispose.UnregisterWaitEx");
            }

            try
            {
                if (proc.ProcessHandle != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(proc.ProcessHandle);
                }
            }
            catch (Exception ex)
            {
                Safe.Ignore(ex, "RunningAppTrackerService.Dispose.CloseHandle");
            }
        }
    }
}
