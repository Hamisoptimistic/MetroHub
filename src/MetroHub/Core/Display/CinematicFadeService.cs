using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace MetroHub.Core.Display;

/// <summary>
/// Universal cinematic display transition engine for MetroHub.
/// Provides macOS-style smooth fade-out to black for Screen Off, Lock, Sleep, Restart, and Shut Down.
/// Incorporates multi-monitor virtual desktop coverage, non-activating window styles,
/// a 3.5s anti-freeze watchdog, SessionSwitch lock detection, PowerModeChanged sleep detection,
/// and Escape-key cancellation to guarantee zero screen freezes and zero hardware risk.
/// </summary>
public sealed class CinematicFadeService
{
    private static readonly Lazy<CinematicFadeService> _instance = new(() => new CinematicFadeService());
    public static CinematicFadeService Instance => _instance.Value;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    public static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    private const int HWND_BROADCAST = 0xFFFF;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const int SC_MONITORPOWER = 0xF170;
    private const int MONITOR_OFF = 2;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private Window? _activeOverlay;
    private readonly object _gate = new();
    private bool _isFading;

    /// <summary>
    /// Smoothly dims all connected screens to black, enters DPMS monitor standby, and dismisses overlay.
    /// </summary>
    public Task FadeAndTurnOffDisplaysAsync(int fadeDurationMs = 650)
    {
        return FadeAndExecuteAsync(() =>
        {
            PostMessage((IntPtr)HWND_BROADCAST, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)MONITOR_OFF);
        }, fadeDurationMs, holdDurationMs: 400);
    }

    /// <summary>
    /// Alias for backward-compatibility with Caffeine widget calls.
    /// </summary>
    public Task FadeAndTurnOffAsync() => FadeAndTurnOffDisplaysAsync(650);

    /// <summary>
    /// Universal cinematic fade execution method.
    /// Smoothly dims the display to pitch black over <paramref name="fadeDurationMs"/>,
    /// invokes the specified <paramref name="action"/> (Lock, Sleep, Shutdown, etc.),
    /// and safely dismisses the overlay with multi-layered safety guards.
    /// </summary>
    public async Task FadeAndExecuteAsync(Action action, int fadeDurationMs = 500, int holdDurationMs = 250)
    {
        if (action == null) return;

        lock (_gate)
        {
            if (_isFading) return;
            _isFading = true;
        }

        try
        {
            if (Application.Current?.Dispatcher == null)
            {
                action();
                return;
            }

            var tcs = new TaskCompletionSource<bool>();

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var overlay = CreateOverlayWindow();
                    _activeOverlay = overlay;

                    // Safety Net 1: Watchdog timer (closes overlay if action or shutdown halts)
                    var watchdogCts = new CancellationTokenSource();
                    _ = Task.Delay(3500, watchdogCts.Token).ContinueWith(_ =>
                    {
                        Application.Current?.Dispatcher.InvokeAsync(() =>
                        {
                            DismissOverlay(overlay);
                        });
                    }, TaskScheduler.Default);

                    // Safety Net 2: SystemEvents.SessionSwitch (close overlay when Windows locks)
                    SessionSwitchEventHandler? sessionHandler = null;
                    sessionHandler = (s, e) =>
                    {
                        if (e.Reason == SessionSwitchReason.SessionLock)
                        {
                            Application.Current?.Dispatcher.InvokeAsync(() =>
                            {
                                DismissOverlay(overlay);
                            });
                        }
                    };
                    SystemEvents.SessionSwitch += sessionHandler;

                    // Safety Net 3: SystemEvents.PowerModeChanged (close overlay when PC sleeps or resumes)
                    PowerModeChangedEventHandler? powerHandler = null;
                    powerHandler = (s, e) =>
                    {
                        Application.Current?.Dispatcher.InvokeAsync(() =>
                        {
                            DismissOverlay(overlay);
                        });
                    };
                    SystemEvents.PowerModeChanged += powerHandler;

                    // Safety Net 4: Escape key aborts fade
                    overlay.KeyDown += (s, e) =>
                    {
                        if (e.Key == Key.Escape)
                        {
                            watchdogCts.Cancel();
                            DismissOverlay(overlay);
                            tcs.TrySetCanceled();
                        }
                    };

                    // Clean up event handlers on close
                    overlay.Closed += (s, e) =>
                    {
                        try
                        {
                            watchdogCts.Cancel();
                            watchdogCts.Dispose();
                            if (sessionHandler != null) SystemEvents.SessionSwitch -= sessionHandler;
                            if (powerHandler != null) SystemEvents.PowerModeChanged -= powerHandler;
                        }
                        catch { }
                        _activeOverlay = null;
                    };

                    overlay.Show();

                    // Smooth cubic ease-in-out transition
                    var anim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(fadeDurationMs))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                    };
                    Timeline.SetDesiredFrameRate(anim, 120);

                    anim.Completed += async (s, e) =>
                    {
                        try
                        {
                            // Screen is now 100% black: execute target power/session action
                            action();

                            if (holdDurationMs > 0)
                            {
                                await Task.Delay(holdDurationMs).ConfigureAwait(true);
                            }

                            DismissOverlay(overlay);
                            tcs.TrySetResult(true);
                        }
                        catch (Exception ex)
                        {
                            DismissOverlay(overlay);
                            tcs.TrySetException(ex);
                        }
                    };

                    overlay.BeginAnimation(UIElement.OpacityProperty, anim);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            await tcs.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CinematicFadeService] Error: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _isFading = false;
            }
        }
    }

    private static Window CreateOverlayWindow()
    {
        double left = SystemParameters.VirtualScreenLeft;
        double top = SystemParameters.VirtualScreenTop;
        double width = SystemParameters.VirtualScreenWidth;
        double height = SystemParameters.VirtualScreenHeight;

        var overlay = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Black,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = true, // To receive Escape key abort if user cancels
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            Opacity = 0.0
        };

        overlay.SourceInitialized += (s, e) =>
        {
            var handle = new WindowInteropHelper(overlay).Handle;
            if (handle != IntPtr.Zero)
            {
                int exStyle = GetWindowLong(handle, GWL_EXSTYLE);
                SetWindowLong(handle, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            }
        };

        return overlay;
    }

    private static void DismissOverlay(Window? overlay)
    {
        if (overlay == null) return;
        try
        {
            overlay.BeginAnimation(UIElement.OpacityProperty, null);
            overlay.Close();
        }
        catch { }
    }
}
