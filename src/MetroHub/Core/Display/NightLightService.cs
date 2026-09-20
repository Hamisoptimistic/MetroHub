using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MetroHub.Core.Display;

/// <summary>
/// Hardware-accelerated Night Light service using GPU display gamma ramps (SetDeviceGammaRamp).
/// Operates on GPU scanout lookup tables (LUTs) with zero per-frame CPU or rendering overhead.
/// Features multi-monitor enumeration, safe driver clamping, read-back verification,
/// multi-widget client ref-counting, and fail-safe process exit restoration.
/// </summary>
public sealed class NightLightService : IDisposable
{
    private static readonly Lazy<NightLightService> _instance = new(() => new NightLightService());
    public static NightLightService Instance => _instance.Value;

    #region Win32 P/Invoke Declarations

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct RAMP
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Red;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Green;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Blue;

        public static RAMP CreateLinear()
        {
            var ramp = new RAMP
            {
                Red = new ushort[256],
                Green = new ushort[256],
                Blue = new ushort[256]
            };
            for (int i = 0; i < 256; i++)
            {
                ushort val = (ushort)(i * 257);
                ramp.Red[i] = val;
                ramp.Green[i] = val;
                ramp.Blue[i] = val;
            }
            return ramp;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("gdi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CreateDC(string? lpszDriver, string lpszDevice, string? lpszOutput, IntPtr lpInitData);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool SetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool GetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);

    #endregion

    private readonly object _stateLock = new();
    private readonly HashSet<object> _registeredClients = new();
    private readonly RAMP _defaultRamp;

    private bool _isEnabled;
    private double _strength = 50.0; // 0 to 100
    private bool _isSupported = true;
    private bool _isDisposed;

    // Rate-limiting during fast slider scrubbing
    private CancellationTokenSource? _throttleCts;
    private readonly SemaphoreSlim _hardwareGate = new(1, 1);

    // Zero-allocation reusable transition ramp buffer for smooth zero-snap fading
    private RAMP _transitionRamp = new()
    {
        Red = new ushort[256],
        Green = new ushort[256],
        Blue = new ushort[256]
    };
    private double _currentAppliedStrength = 0.0;

    public event EventHandler? StateChanged;

    public bool IsEnabled
    {
        get
        {
            lock (_stateLock) return _isEnabled;
        }
    }

    public double Strength
    {
        get
        {
            lock (_stateLock) return _strength;
        }
    }

    public bool IsSupported
    {
        get
        {
            lock (_stateLock) return _isSupported;
        }
    }

    public NightLightService()
    {
        // Capture initial system ramp or fallback to standard linear 6500K
        _defaultRamp = CaptureCurrentRamp();

        // Verify if hardware gamma is supported on primary display
        _isSupported = VerifyHardwareSupport();

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
            if (_registeredClients.Count == 0 && _isEnabled)
            {
                // All widget instances removed: restore default gamma ramp
                _isEnabled = false;
                ApplyHardwareRampThrottled(false, _strength, smoothTransition: false);
                NotifyStateChanged();
            }
        }
    }

    #endregion

    #region State Controls

    public void SetEnabled(bool enabled)
    {
        lock (_stateLock)
        {
            if (_isEnabled == enabled) return;
            _isEnabled = enabled;
            ApplyHardwareRampThrottled(_isEnabled, _strength, smoothTransition: true);
        }
        NotifyStateChanged();
    }

    public void SetStrength(double strength)
    {
        double clamped = Math.Clamp(strength, 0.0, 100.0);
        lock (_stateLock)
        {
            if (Math.Abs(_strength - clamped) < 0.2) return;
            _strength = clamped;
            if (_isEnabled)
            {
                ApplyHardwareRampThrottled(true, _strength, smoothTransition: false);
            }
        }
        NotifyStateChanged();
    }

    #endregion

    #region Hardware Gamma Ramp Application

    private RAMP CaptureCurrentRamp()
    {
        IntPtr hdc = GetDC(IntPtr.Zero);
        try
        {
            if (hdc != IntPtr.Zero)
            {
                var ramp = new RAMP
                {
                    Red = new ushort[256],
                    Green = new ushort[256],
                    Blue = new ushort[256]
                };
                if (GetDeviceGammaRamp(hdc, ref ramp))
                {
                    return ramp;
                }
            }
        }
        catch { }
        finally
        {
            if (hdc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdc);
        }

        return RAMP.CreateLinear();
    }

    private bool VerifyHardwareSupport()
    {
        IntPtr hdc = GetDC(IntPtr.Zero);
        try
        {
            if (hdc == IntPtr.Zero) return false;
            var ramp = RAMP.CreateLinear();
            // Test if GetDeviceGammaRamp succeeds
            return GetDeviceGammaRamp(hdc, ref ramp);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hdc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    private void ApplyHardwareRampThrottled(bool isEnabled, double targetStrength, bool smoothTransition)
    {
        _throttleCts?.Cancel();
        _throttleCts?.Dispose();
        _throttleCts = new CancellationTokenSource();
        var token = _throttleCts.Token;

        Task.Run(async () =>
        {
            try
            {
                if (smoothTransition)
                {
                    double startStrength = _currentAppliedStrength;
                    double destStrength = isEnabled ? targetStrength : 0.0;

                    if (Math.Abs(startStrength - destStrength) < 0.5)
                    {
                        await _hardwareGate.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            if (!isEnabled)
                            {
                                var defaultRamp = _defaultRamp;
                                ApplyRampToAllMonitors(ref defaultRamp);
                                _currentAppliedStrength = 0.0;
                            }
                            else
                            {
                                PopulateWarmthRamp(ref _transitionRamp, destStrength);
                                ApplyRampToAllMonitors(ref _transitionRamp);
                                _currentAppliedStrength = destStrength;
                            }
                        }
                        finally
                        {
                            _hardwareGate.Release();
                        }
                        return;
                    }

                    const int totalSteps = 14;
                    const int intervalMs = 25; // 350ms total transition

                    for (int step = 1; step <= totalSteps; step++)
                    {
                        await Task.Delay(intervalMs, token).ConfigureAwait(false);
                        if (token.IsCancellationRequested || _isDisposed) return;

                        await _hardwareGate.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            double t = step / (double)totalSteps;
                            // SmoothStep cubic curve for organic, fluid transition (macOS / f.lux style)
                            double smoothT = t * t * (3.0 - 2.0 * t);
                            double cur = startStrength + (destStrength - startStrength) * smoothT;
                            _currentAppliedStrength = cur;

                            if (step == totalSteps && !isEnabled)
                            {
                                var defaultRamp = _defaultRamp;
                                ApplyRampToAllMonitors(ref defaultRamp);
                                _currentAppliedStrength = 0.0;
                            }
                            else
                            {
                                PopulateWarmthRamp(ref _transitionRamp, cur);
                                ApplyRampToAllMonitors(ref _transitionRamp);
                            }
                        }
                        finally
                        {
                            _hardwareGate.Release();
                        }
                    }
                }
                else
                {
                    // 16ms debounce (~60 FPS) to prevent driver queue starvation during scrubbing
                    await Task.Delay(16, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested || _isDisposed) return;

                    await _hardwareGate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        if (!isEnabled)
                        {
                            var defaultRamp = _defaultRamp;
                            ApplyRampToAllMonitors(ref defaultRamp);
                            _currentAppliedStrength = 0.0;
                        }
                        else
                        {
                            PopulateWarmthRamp(ref _transitionRamp, targetStrength);
                            ApplyRampToAllMonitors(ref _transitionRamp);
                            _currentAppliedStrength = targetStrength;
                        }
                    }
                    finally
                    {
                        _hardwareGate.Release();
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NightLightService] Apply error: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Populates a safe, monotonic display gamma ramp in-place without heap allocations.
    /// Clamped safely within Windows DWM and GPU driver acceptance limits (max ~38% blue reduction)
    /// to avoid silent driver rejection while delivering a rich, warm evening tint.
    /// </summary>
    public static void PopulateWarmthRamp(ref RAMP ramp, double strength)
    {
        double factor = Math.Clamp(strength, 0.0, 100.0) / 100.0;

        // Red: 1.0 (unchanged)
        // Green: drops by at most 12% at 100% strength (warm golden tone)
        // Blue: drops by at most 38% at 100% strength (safe within Windows Vista+ monotonicity clamps)
        double rScale = 1.0;
        double gScale = 1.0 - (0.12 * factor);
        double bScale = 1.0 - (0.38 * factor);

        for (int i = 0; i < 256; i++)
        {
            double baseVal = i * 257.0;
            ramp.Red[i] = (ushort)Math.Clamp(Math.Round(baseVal * rScale), 0.0, 65535.0);
            ramp.Green[i] = (ushort)Math.Clamp(Math.Round(baseVal * gScale), 0.0, 65535.0);
            ramp.Blue[i] = (ushort)Math.Clamp(Math.Round(baseVal * bScale), 0.0, 65535.0);
        }

        // Strict monotonicity guarantee required by WDDM display drivers
        for (int i = 1; i < 256; i++)
        {
            if (ramp.Red[i] < ramp.Red[i - 1]) ramp.Red[i] = ramp.Red[i - 1];
            if (ramp.Green[i] < ramp.Green[i - 1]) ramp.Green[i] = ramp.Green[i - 1];
            if (ramp.Blue[i] < ramp.Blue[i - 1]) ramp.Blue[i] = ramp.Blue[i - 1];
        }
    }

    /// <summary>
    /// Computes a safe, monotonic display gamma ramp.
    /// </summary>
    public static RAMP ComputeWarmthRamp(double strength)
    {
        var ramp = new RAMP
        {
            Red = new ushort[256],
            Green = new ushort[256],
            Blue = new ushort[256]
        };
        PopulateWarmthRamp(ref ramp, strength);
        return ramp;
    }

    private void ApplyRampToAllMonitors(ref RAMP ramp)
    {
        var devices = new List<string>();

        // Enumerate monitors to gather device names
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT rc, IntPtr data) =>
        {
            var info = new MONITORINFOEX();
            info.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
            if (GetMonitorInfo(hMonitor, ref info))
            {
                if (!string.IsNullOrEmpty(info.szDevice) && !devices.Contains(info.szDevice))
                {
                    devices.Add(info.szDevice);
                }
            }
            return true;
        }, IntPtr.Zero);

        bool anySuccess = false;

        // Apply per-monitor via dedicated Device Context
        foreach (var deviceName in devices)
        {
            IntPtr hdc = CreateDC("DISPLAY", deviceName, null, IntPtr.Zero);
            if (hdc != IntPtr.Zero)
            {
                try
                {
                    if (SetDeviceGammaRamp(hdc, ref ramp))
                    {
                        anySuccess = true;
                    }
                }
                finally
                {
                    DeleteDC(hdc);
                }
            }
        }

        // Fallback to desktop screen DC if per-monitor failed or no devices enumerated
        if (!anySuccess)
        {
            IntPtr hdc = GetDC(IntPtr.Zero);
            if (hdc != IntPtr.Zero)
            {
                try
                {
                    if (SetDeviceGammaRamp(hdc, ref ramp))
                    {
                        anySuccess = true;
                    }
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, hdc);
                }
            }
        }

        lock (_stateLock)
        {
            _isSupported = anySuccess;
        }
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
        RestoreDefaultSynchronous();
    }

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        RestoreDefaultSynchronous();
    }

    private void RestoreDefaultSynchronous()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _currentAppliedStrength = 0.0;
        try
        {
            var defaultRamp = _defaultRamp;
            ApplyRampToAllMonitors(ref defaultRamp);
        }
        catch { }
    }

    public void Dispose()
    {
        RestoreDefaultSynchronous();
        _throttleCts?.Cancel();
        _throttleCts?.Dispose();
        _hardwareGate.Dispose();
    }
}
