using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Audio;
using MetroHub.Core.Search.Native;
using MetroHub.Core.Services;
using Wpf.Ui.Controls;

namespace MetroHub.Core.Search;

/// <summary>
/// Cached snapshot entry for an active top-level window.
/// </summary>
public sealed record WindowSnapshotEntry(IntPtr Hwnd, string Title, string ProcessName, uint Pid);

/// <summary>
/// Cached snapshot entry for a running process.
/// </summary>
public sealed record ProcessSnapshotEntry(int Pid, string ProcessName, DateTime StartTime);

/// <summary>
/// High-performance shared 1.5-second cached snapshot of top-level windows and processes.
/// Keeps search drawer typing latency under 1ms without flooding Win32 enumeration APIs.
/// </summary>
public static class RunningSnapshot
{
    private static readonly object _lock = new();
    private static long _lastWindowScanTicks = 0;
    private static long _lastProcessScanTicks = 0;
    private static List<WindowSnapshotEntry> _cachedWindows = new();
    private static List<ProcessSnapshotEntry> _cachedProcesses = new();

    public static void Invalidate()
    {
        lock (_lock)
        {
            _lastWindowScanTicks = 0;
            _lastProcessScanTicks = 0;
        }
    }

    public static IReadOnlyList<WindowSnapshotEntry> GetWindows()
    {
        long now = Environment.TickCount64;
        lock (_lock)
        {
            if (now - _lastWindowScanTicks < 1500 && _cachedWindows.Count > 0)
            {
                return _cachedWindows;
            }
        }

        var list = new List<WindowSnapshotEntry>();
        var titleBuffer = new StringBuilder(512);

        NativeMethodsSearch.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;

            // Filter cloaked windows (DWMWA_CLOAKED)
            if (NativeMethodsSearch.DwmGetWindowAttribute(hwnd, NativeMethodsSearch.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            // Retrieve title with 50ms timeout to avoid hanging on unresponsive applications
            titleBuffer.Clear();
            IntPtr lResult;
            IntPtr ret = NativeMethodsSearch.SendMessageTimeout(
                hwnd,
                NativeMethodsSearch.WM_GETTEXT,
                (IntPtr)titleBuffer.Capacity,
                titleBuffer,
                NativeMethodsSearch.SMTO_ABORTIFHUNG,
                50,
                out lResult);

            if (ret == IntPtr.Zero || titleBuffer.Length == 0) return true;

            string title = titleBuffer.ToString().Trim();
            if (string.IsNullOrEmpty(title) || title == "Program Manager" || title == "MetroHub") return true;

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            string procName = string.Empty;
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                procName = proc.ProcessName;
            }
            catch { }

            list.Add(new WindowSnapshotEntry(hwnd, title, procName, pid));
            return true;
        }, IntPtr.Zero);

        lock (_lock)
        {
            _cachedWindows = list;
            _lastWindowScanTicks = now;
            return _cachedWindows;
        }
    }

    public static IReadOnlyList<ProcessSnapshotEntry> GetProcesses()
    {
        long now = Environment.TickCount64;
        lock (_lock)
        {
            if (now - _lastProcessScanTicks < 1500 && _cachedProcesses.Count > 0)
            {
                return _cachedProcesses;
            }
        }

        var list = new List<ProcessSnapshotEntry>();
        var procs = Process.GetProcesses();

        for (int i = 0; i < procs.Length; i++)
        {
            var p = procs[i];
            try
            {
                list.Add(new ProcessSnapshotEntry(p.Id, p.ProcessName, p.StartTime));
            }
            catch
            {
                list.Add(new ProcessSnapshotEntry(p.Id, p.ProcessName, DateTime.MinValue));
            }
            finally
            {
                p.Dispose();
            }
        }

        lock (_lock)
        {
            _cachedProcesses = list;
            _lastProcessScanTicks = now;
            return _cachedProcesses;
        }
    }
}

/// <summary>
/// Source adapter executing terminal and shell commands via reclaimed '>' prefix.
/// Launches Windows Terminal (wt.exe) or PowerShell with arguments without Everything IPC interference.
/// </summary>
public sealed class TerminalSource : ISearchSource
{
    public string SourceId => "terminal";
    public SourceState State => SourceState.Ready;

    private readonly QueryRouter _router;

    public TerminalSource(QueryRouter? router = null)
    {
        _router = router ?? new QueryRouter();
    }

    public Task<IReadOnlyList<Candidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(query.RawText))
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        var route = _router.Route(query.RawText);
        if (route.Kind != RouteKind.Terminal)
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        string cmd = route.Remainder.Trim();
        string displayName = string.IsNullOrEmpty(cmd)
            ? "Open terminal"
            : $"Run '> {cmd}'";

        var action = new CandidateAction(
            Id: "run_terminal",
            Title: "Run in terminal",
            KeyGestureHint: "Enter",
            ExecuteAsync: _ =>
            {
                LaunchTerminalCommand(cmd);
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            });

        var candidate = new Candidate(
            Id: "terminal:" + (string.IsNullOrEmpty(cmd) ? "open" : cmd),
            DisplayName: displayName,
            FullPathOrKey: cmd,
            Category: SearchCategory.Actions,
            SourceId: SourceId)
        {
            Icon = SymbolRegular.WindowConsole20,
            KindLabel = "Terminal",
            PrimaryAction = action
        };

        return Task.FromResult<IReadOnlyList<Candidate>>(new[] { candidate });
    }

    public static void LaunchTerminalCommand(string command)
    {
        try
        {
            bool hasWt = IsWindowsTerminalAvailable();
            var psi = new ProcessStartInfo
            {
                FileName = hasWt ? "wt.exe" : "powershell.exe",
                UseShellExecute = true
            };

            if (!string.IsNullOrWhiteSpace(command))
            {
                psi.ArgumentList.Add(hasWt ? "powershell.exe" : "-NoExit");
                if (hasWt)
                {
                    psi.ArgumentList.Add("-NoExit");
                }
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(command);
            }

            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Safe.Log("TerminalSource.LaunchTerminalCommand", ex);
        }
    }

    private static bool IsWindowsTerminalAvailable()
    {
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string wtPath = Path.Combine(localAppData, @"Microsoft\WindowsApps\wt.exe");
            return File.Exists(wtPath);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Source adapter providing open window switching with maximized-state preservation.
/// Triggers on "win " or universal query.
/// </summary>
public sealed class WindowWalkerSource : ISearchSource
{
    public string SourceId => "window_walker";
    public SourceState State => SourceState.Ready;

    private readonly QueryRouter _router;

    public WindowWalkerSource(QueryRouter? router = null)
    {
        _router = router ?? new QueryRouter();
    }

    public Task<IReadOnlyList<Candidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(query.RawText))
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        var route = _router.Route(query.RawText);
        string term = route.Kind == RouteKind.WindowWalker ? route.Remainder.Trim() : query.NormalizedText;
        if (string.IsNullOrEmpty(term))
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        string norm = SearchRanker.NormalizeText(term);
        var windows = RunningSnapshot.GetWindows();
        var results = new List<Candidate>();

        for (int i = 0; i < windows.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var w = windows[i];

            string titleNorm = SearchRanker.NormalizeText(w.Title);
            string procNorm = SearchRanker.NormalizeText(w.ProcessName);

            if (titleNorm.Contains(norm, StringComparison.Ordinal) || procNorm.Contains(norm, StringComparison.Ordinal))
            {
                results.Add(CreateWindowCandidate(w));
            }
        }

        return Task.FromResult<IReadOnlyList<Candidate>>(results);
    }

    private Candidate CreateWindowCandidate(WindowSnapshotEntry w)
    {
        return new Candidate(
            Id: $"win:hwnd:0x{w.Hwnd.ToInt64():X}",
            DisplayName: w.Title,
            FullPathOrKey: $"{w.ProcessName} (PID {w.Pid})",
            Category: SearchCategory.Actions,
            SourceId: SourceId)
        {
            Subtitle = "Switch to window",
            Icon = SymbolRegular.Window24,
            KindLabel = "Window",
            PrimaryAction = new CandidateAction(
                Id: "switch_window",
                Title: "Switch to window",
                KeyGestureHint: "Enter",
                Icon: new CandidateIcon(CandidateIconKind.Symbol, Symbol: SymbolRegular.Window24),
                ExecuteAsync: _ =>
                {
                    ActivateWindow(w.Hwnd);
                    return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                }),
            SecondaryActions = new[]
            {
                new CandidateAction(
                    Id: "close_window",
                    Title: "Close window",
                    KeyGestureHint: null,
                    Icon: new CandidateIcon(CandidateIconKind.Symbol, Symbol: SymbolRegular.Dismiss24),
                    IsDestructive: true,
                    BeginGroup: true,
                    ExecuteAsync: _ =>
                    {
                        try
                        {
                            NativeMethods.PostMessage(w.Hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                        }
                        catch (Exception ex)
                        {
                            Safe.Log("WindowWalker.CloseWindow", ex);
                        }
                        return Task.FromResult(new ActionResult(ActionOutcome.StayOpenAndRefresh));
                    })
            }
        };
    }

    public static void ActivateWindow(IntPtr hwnd)
    {
        try
        {
            // Only restore if minimized to preserve maximized window state
            if (NativeMethodsSearch.IsIconic(hwnd))
            {
                NativeMethodsSearch.ShowWindow(hwnd, NativeMethodsSearch.SW_RESTORE);
            }

            NativeMethods.SetForegroundWindow(hwnd);
        }
        catch (Exception ex)
        {
            Safe.Log("WindowWalker.ActivateWindow", ex);
        }
    }
}

/// <summary>
/// Source adapter terminating unneeded processes with denylist protection and tree termination.
/// Triggers on "kill ".
/// </summary>
public sealed class ProcessKillerSource : ISearchSource
{
    public string SourceId => "process_killer";
    public SourceState State => SourceState.Ready;

    internal static readonly FrozenSet<string> Denylist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "metrohub",
        "explorer",
        "csrss",
        "winlogon",
        "lsass",
        "smss",
        "dwm",
        "wininit",
        "services",
        "svchost",
        "system",
        "idle"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly QueryRouter _router;

    public ProcessKillerSource(QueryRouter? router = null)
    {
        _router = router ?? new QueryRouter();
    }

    public Task<IReadOnlyList<Candidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(query.RawText))
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        var route = _router.Route(query.RawText);
        if (route.Kind != RouteKind.ProcessKiller)
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        int currentPid = Environment.ProcessId;
        var processes = RunningSnapshot.GetProcesses();
        var results = new List<Candidate>();

        string target = route.Remainder.Trim();
        if (string.IsNullOrEmpty(target))
        {
            for (int i = 0; i < processes.Count && results.Count < 15; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;
                var p = processes[i];
                if (p.Pid == currentPid) continue;
                if (Denylist.Contains(p.ProcessName)) continue;
                results.Add(CreateProcessCandidate(p));
            }
            return Task.FromResult<IReadOnlyList<Candidate>>(results);
        }

        string normTarget = SearchRanker.NormalizeText(target);
        if (Denylist.Contains(normTarget))
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        for (int i = 0; i < processes.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var p = processes[i];

            if (p.Pid == currentPid) continue;
            if (Denylist.Contains(p.ProcessName)) continue;

            string pNorm = SearchRanker.NormalizeText(p.ProcessName);
            if (pNorm.StartsWith(normTarget, StringComparison.Ordinal) || pNorm.Contains(normTarget, StringComparison.Ordinal))
            {
                results.Add(CreateProcessCandidate(p));
            }
        }

        return Task.FromResult<IReadOnlyList<Candidate>>(results);
    }

    private Candidate CreateProcessCandidate(ProcessSnapshotEntry p)
    {
        return new Candidate(
            Id: $"kill:{p.Pid}:{p.StartTime.Ticks}",
            DisplayName: $"Kill {p.ProcessName} (PID {p.Pid})",
            FullPathOrKey: $"Terminate {p.ProcessName}.exe and entire process tree",
            Category: SearchCategory.Actions,
            SourceId: SourceId)
        {
            Subtitle = "Force close app",
            Icon = SymbolRegular.DismissCircle24,
            KindLabel = "Process",
            PrimaryAction = new CandidateAction(
                Id: "terminate_process",
                Title: $"Terminate {p.ProcessName}",
                KeyGestureHint: "Enter",
                Icon: new CandidateIcon(CandidateIconKind.Symbol, Symbol: SymbolRegular.DismissCircle24),
                IsDestructive: true,
                ExecuteAsync: _ =>
                {
                    bool killed = KillProcessSafely(p.Pid, p.StartTime);
                    RunningSnapshot.Invalidate();
                    string msg = killed ? $"Killed {p.ProcessName} (PID {p.Pid})" : $"Failed to kill {p.ProcessName}";
                    return Task.FromResult(new ActionResult(ActionOutcome.StayOpenAndRefresh, msg));
                }),
            SecondaryActions = new[]
            {
                new CandidateAction(
                    Id: "open_proc_location",
                    Title: "Open executable location",
                    KeyGestureHint: null,
                    Icon: new CandidateIcon(CandidateIconKind.Symbol, Symbol: SymbolRegular.FolderOpen24),
                    BeginGroup: true,
                    ExecuteAsync: _ =>
                    {
                        try
                        {
                            using var proc = Process.GetProcessById(p.Pid);
                            string? mainModule = proc.MainModule?.FileName;
                            if (!string.IsNullOrEmpty(mainModule) && File.Exists(mainModule))
                            {
                                Process.Start("explorer.exe", $"/select,\"{mainModule}\"");
                            }
                        }
                        catch (Exception ex)
                        {
                            Safe.Log("ProcessKiller.OpenLocation", ex);
                        }
                        return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                    })
            }
        };
    }

    internal static bool KillProcessSafely(int pid, DateTime expectedStartTime)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);

            if (expectedStartTime != DateTime.MinValue)
            {
                try
                {
                    if (Math.Abs((proc.StartTime - expectedStartTime).TotalSeconds) > 1.0)
                    {
                        return false;
                    }
                }
                catch { }
            }

            if (Denylist.Contains(proc.ProcessName)) return false;

            proc.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception ex)
        {
            Safe.Log("ProcessKiller.KillSafely", ex);
            return false;
        }
    }
}

/// <summary>
/// Source adapter providing system audio output device switching and volume adjustments.
/// Reuses Core/Audio/AudioService.cs.
/// </summary>
public sealed class AudioSource : ISearchSource
{
    public string SourceId => "audio";
    public SourceState State => SourceState.Ready;

    private readonly QueryRouter _router;

    public AudioSource(QueryRouter? router = null)
    {
        _router = router ?? new QueryRouter();
    }

    public Task<IReadOnlyList<Candidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(query.RawText))
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        var route = _router.Route(query.RawText);
        var results = new List<Candidate>();

        // 1. Volume setting ("vol <0-100>")
        if (route.Kind == RouteKind.Volume)
        {
            string rem = route.Remainder.Trim();
            if (int.TryParse(rem, NumberStyles.Integer, CultureInfo.InvariantCulture, out int level) && level >= 0 && level <= 100)
            {
                var action = new CandidateAction(
                    Id: "set_volume",
                    Title: $"Set volume to {level}%",
                    KeyGestureHint: "Enter",
                    ExecuteAsync: _ =>
                    {
                        AudioService.Instance.SetMasterVolume(level / 100f);
                        return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                    });

                var candidate = new Candidate(
                    Id: $"audio:volume:{level}",
                    DisplayName: $"Set volume to {level}%",
                    FullPathOrKey: $"Master Volume: {level}%",
                    Category: SearchCategory.Actions,
                    SourceId: SourceId)
                {
                    Subtitle = $"Set volume to {level}%",
                    Icon = SymbolRegular.Speaker224,
                    KindLabel = "Volume",
                    PrimaryAction = action
                };

                results.Add(candidate);
                return Task.FromResult<IReadOnlyList<Candidate>>(results);
            }

            // Fall through to general query if not a valid number (e.g. "vol 2 notes")
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        // 2. Playback device switcher ("audio")
        if (route.Kind == RouteKind.Audio)
        {
            try
            {
                var devices = AudioService.Instance.GetPlaybackDevices();
                for (int i = 0; i < devices.Count; i++)
                {
                    var d = devices[i];
                    string prefix = d.IsDefault ? "[Active] " : string.Empty;

                    var action = new CandidateAction(
                        Id: "switch_audio_device",
                        Title: $"Switch to {d.Name}",
                        KeyGestureHint: "Enter",
                        ExecuteAsync: _ =>
                        {
                            AudioService.Instance.SetDefaultPlaybackDevice(d.Id);
                            return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                        });

                    var candidate = new Candidate(
                        Id: $"audio:device:{d.Id}",
                        DisplayName: $"{prefix}{d.Name}",
                        FullPathOrKey: d.Name,
                        Category: SearchCategory.Actions,
                        SourceId: SourceId)
                    {
                        Icon = SymbolRegular.Speaker224,
                        KindLabel = "Audio",
                        PrimaryAction = action
                    };

                    results.Add(candidate);
                }
            }
            catch (Exception ex)
            {
                Safe.Log("AudioSource.SearchAsync", ex);
            }
        }

        return Task.FromResult<IReadOnlyList<Candidate>>(results);
    }
}
