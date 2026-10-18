using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using MetroHub.Core.Display;
using Serilog;

namespace MetroHub.Core.Services;

/// <summary>
/// Unified Windows power management service supporting zero-privilege capability detection,
/// cinematic fade execution, and on-demand JIT elevation for hibernation configuration.
/// Single source of truth for all system power transitions (Lock, Sleep, Hibernate, Restart, Shutdown).
/// </summary>
public static class PowerService
{
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsPwrHibernateAllowed();

    /// <summary>
    /// Event raised whenever the system hibernation capability changes (e.g. enabled via Settings).
    /// </summary>
    public static event Action? HibernateStateChanged;

    /// <summary>
    /// Checks whether hibernation (S4 state) is supported and hiberfil.sys is active on Windows.
    /// Runs with standard user privileges (0 UAC prompts, ~0ms execution time).
    /// </summary>
    public static bool IsHibernateSupported()
    {
        try
        {
            return IsPwrHibernateAllowed();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to query Win32 IsPwrHibernateAllowed");
            return false;
        }
    }

    /// <summary>
    /// Locks the current Windows desktop session with cinematic fade.
    /// </summary>
    public static Task ExecuteLockAsync(int fadeDurationMs = 450, int holdDurationMs = 50)
    {
        return CinematicFadeService.Instance.FadeAndExecuteAsync(() =>
        {
            CinematicFadeService.LockWorkStation();
        }, fadeDurationMs, holdDurationMs);
    }

    /// <summary>
    /// Smoothly dims displays and puts the system into low-power Sleep (S3).
    /// </summary>
    public static Task ExecuteSleepAsync(int fadeDurationMs = 600, int holdDurationMs = 250)
    {
        return CinematicFadeService.Instance.FadeAndExecuteAsync(() =>
        {
            CinematicFadeService.SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false);
        }, fadeDurationMs, holdDurationMs);
    }

    /// <summary>
    /// Smoothly dims displays and puts the system into Hibernate (S4).
    /// Standard user privilege (SeShutdownPrivilege) is sufficient.
    /// </summary>
    public static Task ExecuteHibernateAsync(int fadeDurationMs = 650, int holdDurationMs = 300)
    {
        return CinematicFadeService.Instance.FadeAndExecuteAsync(() =>
        {
            CinematicFadeService.SetSuspendState(hibernate: true, forceCritical: false, disableWakeEvent: false);
        }, fadeDurationMs, holdDurationMs);
    }

    /// <summary>
    /// Smoothly dims displays and restarts the computer.
    /// </summary>
    public static Task ExecuteRestartAsync(int fadeDurationMs = 650, int holdDurationMs = 300)
    {
        return CinematicFadeService.Instance.FadeAndExecuteAsync(() =>
        {
            ProcessLauncherService.LaunchTarget("shutdown.exe", "/r /t 0");
        }, fadeDurationMs, holdDurationMs);
    }

    /// <summary>
    /// Smoothly dims displays and shuts down the computer.
    /// </summary>
    public static Task ExecuteShutdownAsync(int fadeDurationMs = 650, int holdDurationMs = 300)
    {
        return CinematicFadeService.Instance.FadeAndExecuteAsync(() =>
        {
            ProcessLauncherService.LaunchTarget("shutdown.exe", "/s /t 0");
        }, fadeDurationMs, holdDurationMs);
    }

    /// <summary>
    /// Smoothly dims displays and signs out the current Windows user.
    /// </summary>
    public static Task ExecuteSignOutAsync(int fadeDurationMs = 500, int holdDurationMs = 200)
    {
        return CinematicFadeService.Instance.FadeAndExecuteAsync(() =>
        {
            ProcessLauncherService.LaunchTarget("shutdown.exe", "/l");
        }, fadeDurationMs, holdDurationMs);
    }

    /// <summary>
    /// Requests on-demand administrator elevation to run 'powercfg.exe /hibernate on'.
    /// Only triggers a UAC prompt when explicitly requested by the user.
    /// Returns true if hibernation was successfully enabled.
    /// </summary>
    public static async Task<bool> RequestEnableHibernateAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = "/hibernate on",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
            }

            bool enabled = IsHibernateSupported();
            if (enabled)
            {
                HibernateStateChanged?.Invoke();
            }
            return enabled;
        }
        catch (Win32Exception ex)
        {
            // Win32Exception with NativeErrorCode 1223 = ERROR_CANCELLED (User dismissed UAC prompt)
            Log.Information("User dismissed UAC prompt for hibernation activation ({NativeErrorCode})", ex.NativeErrorCode);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to execute elevated powercfg /hibernate on");
            return false;
        }
    }

    /// <summary>
    /// Requests on-demand administrator elevation to run 'powercfg.exe /hibernate off'.
    /// Returns true if hibernation was successfully disabled.
    /// </summary>
    public static async Task<bool> RequestDisableHibernateAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = "/hibernate off",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
            }

            bool disabled = !IsHibernateSupported();
            if (disabled)
            {
                HibernateStateChanged?.Invoke();
            }
            return disabled;
        }
        catch (Win32Exception ex)
        {
            Log.Information("User dismissed UAC prompt for hibernation deactivation ({NativeErrorCode})", ex.NativeErrorCode);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to execute elevated powercfg /hibernate off");
            return false;
        }
    }
}
