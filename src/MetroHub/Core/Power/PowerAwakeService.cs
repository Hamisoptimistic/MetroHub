using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace MetroHub.Core.Power;

public enum AwakeMode
{
    Passive = 0,    // Keep using selected power plan (default / off)
    Indefinite = 1, // Keep awake indefinitely
    Timed = 2       // Keep awake for a time interval
}

/// <summary>
/// Thread-safe, hardware-level power execution state manager.
/// Uses a dedicated long-lived thread to own SetThreadExecutionState locks,
/// ensuring locks are thread-affine, never leaked to the thread pool, and clean up on exit.
/// Implements multi-widget ref-counting and process exit safety nets.
/// </summary>
public sealed class PowerAwakeService : IDisposable
{
    private static readonly Lazy<PowerAwakeService> _instance = new(() => new PowerAwakeService());
    public static PowerAwakeService Instance => _instance.Value;

    [Flags]
    private enum EXECUTION_STATE : uint
    {
        ES_AWAYMODE_REQUIRED = 0x00000040,
        ES_CONTINUOUS = 0x80000000,
        ES_DISPLAY_REQUIRED = 0x00000002,
        ES_SYSTEM_REQUIRED = 0x00000001
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private const int HWND_BROADCAST = 0xFFFF;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const int SC_MONITORPOWER = 0xF170;
    private const int MONITOR_OFF = 2;

    private readonly object _stateLock = new();
    private readonly HashSet<object> _registeredClients = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _workerThread;

    private volatile bool _isRunning = true;
    private AwakeMode _mode = AwakeMode.Passive;
    private bool _keepScreenOn = false;
    private DateTime? _targetExpiryUtc;
    private bool _isDisposed;

    public event EventHandler? StateChanged;

    public AwakeMode Mode
    {
        get
        {
            lock (_stateLock) return _mode;
        }
    }

    public bool KeepScreenOn
    {
        get
        {
            lock (_stateLock) return _keepScreenOn;
        }
    }

    public bool IsAwakeActive
    {
        get
        {
            lock (_stateLock) return _mode != AwakeMode.Passive;
        }
    }

    public DateTime? TargetExpiryUtc
    {
        get
        {
            lock (_stateLock) return _targetExpiryUtc;
        }
    }

    public TimeSpan? TimeRemaining
    {
        get
        {
            lock (_stateLock)
            {
                if (_mode != AwakeMode.Timed || !_targetExpiryUtc.HasValue) return null;
                var rem = _targetExpiryUtc.Value - DateTime.UtcNow;
                return rem > TimeSpan.Zero ? rem : TimeSpan.Zero;
            }
        }
    }

    public PowerAwakeService()
    {
        // Dedicated thread guarantees thread-affinity for SetThreadExecutionState
        _workerThread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "MetroHub.CaffeineAwake"
        };
        _workerThread.Start();

        try
        {
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        }
        catch { }
    }

    #region Client Registration / Ref-Counting

    public void RegisterClient(object client)
    {
        if (client == null) return;
        lock (_stateLock)
        {
            _registeredClients.Add(client);
        }
    }

    public void UnregisterClient(object client)
    {
        if (client == null) return;
        lock (_stateLock)
        {
            _registeredClients.Remove(client);
            if (_registeredClients.Count == 0 && _mode != AwakeMode.Passive)
            {
                // All widget instances removed: reset to default power plan
                _mode = AwakeMode.Passive;
                _targetExpiryUtc = null;
                _signal.Set();
                NotifyStateChanged();
            }
        }
    }

    #endregion

    #region State Controls

    public void SetPassive()
    {
        lock (_stateLock)
        {
            if (_mode == AwakeMode.Passive && !_targetExpiryUtc.HasValue) return;
            _mode = AwakeMode.Passive;
            _targetExpiryUtc = null;
            _signal.Set();
        }
        NotifyStateChanged();
    }

    public void SetIndefinite()
    {
        lock (_stateLock)
        {
            if (_mode == AwakeMode.Indefinite && !_targetExpiryUtc.HasValue) return;
            _mode = AwakeMode.Indefinite;
            _targetExpiryUtc = null;
            _signal.Set();
        }
        NotifyStateChanged();
    }

    public void SetTimed(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            SetPassive();
            return;
        }

        lock (_stateLock)
        {
            _mode = AwakeMode.Timed;
            _targetExpiryUtc = DateTime.UtcNow + duration;
            _signal.Set();
        }
        NotifyStateChanged();
    }

    public void SetKeepScreenOn(bool keepOn)
    {
        lock (_stateLock)
        {
            if (_keepScreenOn == keepOn) return;
            _keepScreenOn = keepOn;
            _signal.Set();
        }
        NotifyStateChanged();
    }

    /// <summary>
    /// Powers down all connected displays immediately via standard VESA DPMS (D3 standby).
    /// Background processes, downloads, rendering, and audio playback continue uninterrupted.
    /// Incorporates a 500ms grace delay to prevent mouse click micro-jitter from instantly waking screens.
    /// </summary>
    public async Task TurnOffDisplaysAsync()
    {
        if (_keepScreenOn)
        {
            SetKeepScreenOn(false);
        }

        // 500ms grace period so user has time to release mouse button and prevent optical sensor micro-jitter
        await Task.Delay(500).ConfigureAwait(false);

        try
        {
            PostMessage((IntPtr)HWND_BROADCAST, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)MONITOR_OFF);
        }
        catch { }
    }

    #endregion

    #region Dedicated Worker Thread Loop

    private void WorkerLoop()
    {
        while (_isRunning)
        {
            AwakeMode mode;
            bool keepScreenOn;
            DateTime? targetExpiry;

            lock (_stateLock)
            {
                mode = _mode;
                keepScreenOn = _keepScreenOn;
                targetExpiry = _targetExpiryUtc;
            }

            EXECUTION_STATE flags = EXECUTION_STATE.ES_CONTINUOUS;
            int waitMs = Timeout.Infinite;

            if (mode == AwakeMode.Indefinite)
            {
                flags |= EXECUTION_STATE.ES_SYSTEM_REQUIRED;
                if (keepScreenOn) flags |= EXECUTION_STATE.ES_DISPLAY_REQUIRED;
                waitMs = Timeout.Infinite;
            }
            else if (mode == AwakeMode.Timed && targetExpiry.HasValue)
            {
                var remaining = targetExpiry.Value - DateTime.UtcNow;
                if (remaining > TimeSpan.Zero)
                {
                    flags |= EXECUTION_STATE.ES_SYSTEM_REQUIRED;
                    if (keepScreenOn) flags |= EXECUTION_STATE.ES_DISPLAY_REQUIRED;
                    waitMs = (int)Math.Min(remaining.TotalMilliseconds, int.MaxValue);
                }
                else
                {
                    // Timed duration expired
                    lock (_stateLock)
                    {
                        _mode = AwakeMode.Passive;
                        _targetExpiryUtc = null;
                    }
                    flags = EXECUTION_STATE.ES_CONTINUOUS;
                    waitMs = Timeout.Infinite;
                    NotifyStateChanged();
                }
            }
            else
            {
                flags = EXECUTION_STATE.ES_CONTINUOUS;
                waitMs = Timeout.Infinite;
            }

            // Apply state flags strictly on this dedicated thread
            try
            {
                SetThreadExecutionState(flags);
            }
            catch { }

            // Sleep in kernel wait with 0% CPU until signal or timer expiration
            _signal.WaitOne(waitMs);
        }

        // Guaranteed cleanup on termination
        try
        {
            SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
        }
        catch { }
    }

    #endregion

    private void NotifyStateChanged()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch { }
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        Shutdown();
    }

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        Shutdown();
    }

    private void Shutdown()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _isRunning = false;
        try
        {
            _signal.Set();
            if (_workerThread.IsAlive)
            {
                _workerThread.Join(400);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        Shutdown();
        _signal.Dispose();
    }
}
