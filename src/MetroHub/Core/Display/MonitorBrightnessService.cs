using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace MetroHub.Core.Display;

/// <summary>
/// High-performance, hardware-safe service for controlling monitor brightness.
/// Handles external monitors via DDC/CI (dxva2.dll) and built-in laptop panels via WMI.
/// Features a debounced I2C background write queue to prevent monitor microcontroller lockups.
/// </summary>
public sealed class MonitorBrightnessService : IDisposable
{
    private static readonly Lazy<MonitorBrightnessService> _instance = new(() => new MonitorBrightnessService());
    public static MonitorBrightnessService Instance => _instance.Value;

    // Rate-limiting: 140ms throttle interval per monitor to protect slow I2C serial bus while ensuring smooth live scrubbing
    private const int ThrottleIntervalMs = 140;
    private readonly ConcurrentDictionary<string, byte> _pendingBrightness = new();
    private readonly ConcurrentDictionary<string, long> _lastWriteTicks = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _trailingTokens = new();
    private readonly SemaphoreSlim _hardwareLock = new(1, 1);
    private bool _isDisposed;

    public event EventHandler<IReadOnlyList<MonitorDevice>>? MonitorsChanged;
    private CancellationTokenSource? _displayChangeDebounceCts;

    public MonitorBrightnessService()
    {
        try
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }
        catch { }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_isDisposed) return;

        _displayChangeDebounceCts?.Cancel();
        _displayChangeDebounceCts?.Dispose();
        _displayChangeDebounceCts = new CancellationTokenSource();
        var token = _displayChangeDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                // Wait 400ms for Windows display driver mode switch to settle
                await Task.Delay(400, token).ConfigureAwait(false);
                if (token.IsCancellationRequested || _isDisposed) return;

                var updated = await GetConnectedMonitorsAsync().ConfigureAwait(false);
                MonitorsChanged?.Invoke(this, updated);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MonitorBrightnessService] Display change refresh error: {ex.Message}");
            }
        }, token);
    }

    /// <summary>
    /// Enumerates all active displays, detecting DDC/CI external monitors and internal WMI laptop screens.
    /// Runs on a background thread to prevent UI micro-stutters.
    /// </summary>
    public async Task<List<MonitorDevice>> GetConnectedMonitorsAsync()
    {
        return await Task.Run(() =>
        {
            var monitors = new List<MonitorDevice>();
            var rawMonitors = new List<(IntPtr hMonitor, RECT rc, bool isPrimary, string devName)>();

            // 1. Enumerate all desktop virtual display outputs
            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdcMon, ref RECT lprcMon, IntPtr dwData) =>
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                bool isPrimary = false;
                string devName = "Display";

                if (NativeMethods.GetMonitorInfo(hMon, ref mi))
                {
                    isPrimary = (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0;
                    devName = mi.szDevice ?? "Display";
                }

                rawMonitors.Add((hMon, lprcMon, isPrimary, devName));
                return true;
            }, IntPtr.Zero);

            // Sort monitors: Primary first, then left-to-right
            rawMonitors = rawMonitors
                .OrderByDescending(m => m.isPrimary)
                .ThenBy(m => m.rc.left)
                .ToList();

            // 2. Query WMI for internal laptop displays
            var internalDisplays = GetWmiInternalDisplays();

            int displayIndex = 1;
            var processedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < rawMonitors.Count; i++)
            {
                var (hMon, rc, isPrimary, devName) = rawMonitors[i];
                var bounds = new Rect(rc.left, rc.top, Math.Max(1, rc.right - rc.left), Math.Max(1, rc.bottom - rc.top));

                // 1. Check physical monitors via dxva2.dll for DDC/CI support (External monitors)
                if (NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint physicalCount) && physicalCount > 0)
                {
                    var physicalArray = new PHYSICAL_MONITOR[physicalCount];
                    if (NativeMethods.GetPhysicalMonitorsFromHMONITOR(hMon, physicalCount, physicalArray))
                    {
                        try
                        {
                            for (int p = 0; p < physicalCount; p++)
                            {
                                var pm = physicalArray[p];
                                string rawName = pm.szPhysicalMonitorDescription?.Trim() ?? "";
                                if (string.IsNullOrWhiteSpace(rawName)) rawName = $"Monitor {displayIndex}";

                                uint min = 0, cur = 50, max = 100;
                                bool supported = NativeMethods.GetMonitorBrightness(pm.hPhysicalMonitor, out min, out cur, out max);
                                uint percentBrightness = 50u;
                                if (supported)
                                {
                                    uint range = max > min ? (max - min) : 100u;
                                    percentBrightness = (uint)Math.Round(Math.Clamp(((cur - min) / (double)range) * 100.0, 0.0, 100.0));
                                }

                                string id = $"{devName}_{p}_{rawName}";
                                processedIds.Add(id);

                                monitors.Add(new MonitorDevice
                                {
                                    Id = id,
                                    DeviceName = devName,
                                    FriendlyName = CleanMonitorName(rawName),
                                    DisplayIndex = displayIndex++,
                                    CurrentBrightness = percentBrightness,
                                    MinBrightness = min,
                                    MaxBrightness = max > 0 ? max : 100u,
                                    IsInternal = false,
                                    IsPrimary = isPrimary,
                                    IsSupported = supported,
                                    PhysicalIndex = p,
                                    Bounds = bounds
                                });
                            }
                        }
                        finally
                        {
                            NativeMethods.DestroyPhysicalMonitors(physicalCount, physicalArray);
                        }
                        continue;
                    }
                }

                // 2. Physical count is 0: Check if this virtual display output corresponds to an internal laptop screen via WMI
                var internalMatch = internalDisplays.FirstOrDefault(w => !processedIds.Contains(w.Id));
                if (internalMatch != null)
                {
                    processedIds.Add(internalMatch.Id);
                    monitors.Add(new MonitorDevice
                    {
                        Id = internalMatch.Id,
                        DeviceName = devName,
                        FriendlyName = string.IsNullOrWhiteSpace(internalMatch.FriendlyName) ? "Built-in Display" : internalMatch.FriendlyName,
                        DisplayIndex = displayIndex++,
                        CurrentBrightness = internalMatch.CurrentBrightness,
                        MinBrightness = 0,
                        MaxBrightness = 100,
                        IsInternal = true,
                        IsPrimary = isPrimary,
                        IsSupported = true,
                        PhysicalIndex = 0,
                        WmiInstanceName = internalMatch.InstanceName,
                        Bounds = bounds
                    });
                    continue;
                }

                // 3. Fallback for displays where both DDC/CI and WMI returned 0 (e.g. virtual adapters)
                string fallbackId = $"{devName}_{i}";
                monitors.Add(new MonitorDevice
                {
                    Id = fallbackId,
                    DeviceName = devName,
                    FriendlyName = $"Display {displayIndex}",
                    DisplayIndex = displayIndex++,
                    CurrentBrightness = 50,
                    MinBrightness = 0,
                    MaxBrightness = 100,
                    IsInternal = false,
                    IsPrimary = isPrimary,
                    IsSupported = false,
                    PhysicalIndex = 0,
                    Bounds = bounds
                });
            }

            // Append any remaining internal displays detected via WMI that weren't matched to an active HMONITOR
            foreach (var remaining in internalDisplays.Where(w => !processedIds.Contains(w.Id)))
            {
                monitors.Add(new MonitorDevice
                {
                    Id = remaining.Id,
                    DeviceName = "Internal",
                    FriendlyName = remaining.FriendlyName,
                    DisplayIndex = displayIndex++,
                    CurrentBrightness = remaining.CurrentBrightness,
                    MinBrightness = 0,
                    MaxBrightness = 100,
                    IsInternal = true,
                    IsPrimary = false,
                    IsSupported = true,
                    PhysicalIndex = 0,
                    WmiInstanceName = remaining.InstanceName,
                    Bounds = new Rect(0, 0, 1920, 1080)
                });
            }

            _lastMonitors = monitors;
            return monitors;
        }).ConfigureAwait(false);
    }

    private volatile List<MonitorDevice> _lastMonitors = new();
    public IReadOnlyList<MonitorDevice> LastMonitors => _lastMonitors;

    /// <summary>
    /// Sets the brightness of a monitor by Id. Debounces calls so rapid slider motion does not flood the I2C bus.
    /// </summary>
    public void SetBrightnessThrottled(string monitorId, uint targetBrightness)
    {
        var monitor = _lastMonitors.FirstOrDefault(m => string.Equals(m.Id, monitorId, StringComparison.OrdinalIgnoreCase));
        if (monitor != null)
        {
            SetBrightnessThrottled(monitor, targetBrightness);
        }
    }

    /// <summary>
    /// Sets the brightness of a monitor. Uses a hybrid rate-limiting throttle (140ms) to ensure live visual feedback
    /// during active slider dragging while protecting the I2C serial bus, followed by a trailing write to guarantee the final release value.
    /// </summary>
    public void SetBrightnessThrottled(MonitorDevice monitor, uint targetBrightness)
    {
        byte brightnessByte = (byte)Math.Clamp(targetBrightness, 0, 100);
        _pendingBrightness[monitor.Id] = brightnessByte;

        long nowTicks = Environment.TickCount64;
        long lastTicks = _lastWriteTicks.GetOrAdd(monitor.Id, 0);

        // 1. If 140ms has elapsed since the last hardware write, perform live write immediately
        if (nowTicks - lastTicks >= ThrottleIntervalMs)
        {
            _lastWriteTicks[monitor.Id] = nowTicks;
            _ = Task.Run(async () =>
            {
                try
                {
                    if (_pendingBrightness.TryGetValue(monitor.Id, out byte b))
                    {
                        await ApplyHardwareBrightnessAsync(monitor, b).ConfigureAwait(false);
                    }
                }
                catch { }
            });
        }

        // 2. Always reset trailing debounce timer so the exact final resting position is applied
        if (_trailingTokens.TryGetValue(monitor.Id, out var existingCts))
        {
            existingCts.Cancel();
            existingCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _trailingTokens[monitor.Id] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ThrottleIntervalMs, cts.Token).ConfigureAwait(false);
                if (cts.Token.IsCancellationRequested || _isDisposed) return;

                if (_pendingBrightness.TryGetValue(monitor.Id, out byte latestBrightness))
                {
                    _lastWriteTicks[monitor.Id] = Environment.TickCount64;
                    await ApplyHardwareBrightnessAsync(monitor, latestBrightness).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MonitorBrightnessService] Write error: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Executes the physical brightness change immediately on the hardware channel.
    /// Accurately scales the 0-100 percentage to the monitor's native Min/Max hardware range,
    /// and addresses the exact physical monitor index for multi-panel virtual displays.
    /// </summary>
    private async Task ApplyHardwareBrightnessAsync(MonitorDevice monitor, byte brightness)
    {
        await _hardwareLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (monitor.IsInternal)
            {
                SetWmiBrightness(monitor.WmiInstanceName, brightness);
                return;
            }

            // Target external physical monitor via dxva2.dll
            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdcMon, ref RECT lprcMon, IntPtr dwData) =>
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (NativeMethods.GetMonitorInfo(hMon, ref mi) && string.Equals(mi.szDevice, monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    if (NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint count) && count > 0)
                    {
                        var physicalArray = new PHYSICAL_MONITOR[count];
                        if (NativeMethods.GetPhysicalMonitorsFromHMONITOR(hMon, count, physicalArray))
                        {
                            try
                            {
                                int targetIndex = Math.Clamp(monitor.PhysicalIndex, 0, (int)count - 1);
                                var pm = physicalArray[targetIndex];

                                // Interpolate 0-100% to the monitor's native hardware range [MinBrightness..MaxBrightness]
                                uint min = monitor.MinBrightness;
                                uint max = monitor.MaxBrightness > min ? monitor.MaxBrightness : 100u;
                                uint hwBrightness = min + (uint)Math.Round((brightness / 100.0) * (max - min));
                                hwBrightness = Math.Clamp(hwBrightness, min, max);

                                NativeMethods.SetMonitorBrightness(pm.hPhysicalMonitor, hwBrightness);
                            }
                            finally
                            {
                                NativeMethods.DestroyPhysicalMonitors(count, physicalArray);
                            }
                        }
                    }
                    return false; // Found target display
                }
                return true;
            }, IntPtr.Zero);
        }
        finally
        {
            _hardwareLock.Release();
        }
    }

    /// <summary>
    /// Identifies which monitor currently encloses the mouse pointer with inclusive edge bounds.
    /// </summary>
    public string? GetCurrentCursorMonitorId(IReadOnlyList<MonitorDevice> monitors)
    {
        if (monitors == null || monitors.Count == 0) return null;
        if (!NativeMethods.GetCursorPos(out POINT pt)) return null;

        foreach (var m in monitors)
        {
            if (pt.X >= m.Bounds.Left && pt.X <= m.Bounds.Right &&
                pt.Y >= m.Bounds.Top && pt.Y <= m.Bounds.Bottom)
            {
                return m.Id;
            }
        }
        return monitors.FirstOrDefault(m => m.IsPrimary)?.Id ?? monitors[0].Id;
    }

    #region WMI Internal Panel Support

    private sealed record WmiDisplayInfo(string Id, string InstanceName, string FriendlyName, uint CurrentBrightness);

    private static List<WmiDisplayInfo> GetWmiInternalDisplays()
    {
        var results = new List<WmiDisplayInfo>();
        try
        {
            var monitorNames = GetWmiMonitorNames();

            using var searcher = new ManagementObjectSearcher(@"\\.\root\wmi", "SELECT * FROM WmiMonitorBrightness");
            using var collection = searcher.Get();

            int idx = 0;
            foreach (ManagementObject obj in collection)
            {
                try
                {
                    var active = (bool)(obj["Active"] ?? false);
                    if (!active) continue;

                    string instanceName = obj["InstanceName"]?.ToString() ?? string.Empty;
                    var curObj = obj["CurrentBrightness"];
                    uint cur = curObj != null ? Convert.ToUInt32(curObj) : 50u;

                    // Match deterministic name from WmiMonitorID via InstanceName or device prefix
                    string? name = null;
                    if (!string.IsNullOrEmpty(instanceName))
                    {
                        if (!monitorNames.TryGetValue(instanceName, out name))
                        {
                            string prefix = instanceName.Split('_')[0];
                            name = monitorNames.FirstOrDefault(kvp => kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Value;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = idx == 0 ? "Built-in Display" : $"Internal Display {idx + 1}";
                    }

                    results.Add(new WmiDisplayInfo($"wmi_internal_{idx + 1}", instanceName, name, Math.Clamp(cur, 0u, 100u)));
                    idx++;
                }
                finally
                {
                    obj.Dispose();
                }
            }
        }
        catch { }
        return results;
    }

    private static Dictionary<string, string> GetWmiMonitorNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(@"\\.\root\wmi", "SELECT * FROM WmiMonitorID");
            using var collection = searcher.Get();

            foreach (ManagementObject obj in collection)
            {
                try
                {
                    string? instanceName = obj["InstanceName"]?.ToString();
                    if (string.IsNullOrWhiteSpace(instanceName)) continue;

                    if (obj["UserFriendlyName"] is ushort[] chars)
                    {
                        string name = DecodeEdidString(chars);
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            names[instanceName] = name;
                        }
                    }
                }
                finally
                {
                    obj.Dispose();
                }
            }
        }
        catch { }
        return names;
    }

    private static string DecodeEdidString(ushort[] chars)
    {
        if (chars == null || chars.Length == 0) return string.Empty;

        var bytes = new List<byte>(chars.Length);
        foreach (var c in chars)
        {
            if (c == 0 || c == 0x0A) break; // 0x0A is EDID LF terminator
            bytes.Add((byte)(c & 0xFF));
        }

        if (bytes.Count == 0) return string.Empty;

        try
        {
            // Decodes UTF-8 / ASCII cleanly without throwing on invalid sequences
            var utf8 = new UTF8Encoding(false, false);
            string decoded = utf8.GetString(bytes.ToArray()).Trim();
            if (!string.IsNullOrWhiteSpace(decoded)) return decoded;
        }
        catch { }

        return Encoding.ASCII.GetString(bytes.ToArray()).Trim();
    }

    private static void SetWmiBrightness(string? targetInstanceName, byte brightness)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"\\.\root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
            using var collection = searcher.Get();

            foreach (ManagementObject obj in collection)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(targetInstanceName))
                    {
                        string instanceName = obj["InstanceName"]?.ToString() ?? string.Empty;
                        if (!string.IsNullOrEmpty(instanceName) &&
                            !string.Equals(instanceName, targetInstanceName, StringComparison.OrdinalIgnoreCase) &&
                            !instanceName.StartsWith(targetInstanceName.Split('_')[0], StringComparison.OrdinalIgnoreCase))
                        {
                            continue; // Skip non-targeted display panels
                        }
                    }

                    obj.InvokeMethod("WmiSetBrightness", new object[] { 1u, brightness });
                }
                finally
                {
                    obj.Dispose();
                }
            }
        }
        catch { }
    }

    #endregion

    private static string CleanMonitorName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Display";

        string[] genericPnpIdentifiers = [
            "Generic PnP Monitor",
            "PnP-Monitor (Standard)",
            "Moniteur Plug-and-Play générique",
            "Monitor PnP genérico",
            "Monitor Plug and Play generico",
            "汎用 PnP モニター",
            "通用即插即用监视器",
            "一般 PnP 監視器",
            "Универсальный монитор PnP"
        ];

        foreach (var generic in genericPnpIdentifiers)
        {
            if (name.Contains(generic, StringComparison.OrdinalIgnoreCase))
            {
                name = name.Replace(generic, "Standard Display", StringComparison.OrdinalIgnoreCase);
                break;
            }
        }
        return name.Trim();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        }
        catch { }

        _displayChangeDebounceCts?.Cancel();
        _displayChangeDebounceCts?.Dispose();
        _displayChangeDebounceCts = null;

        foreach (var cts in _trailingTokens.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _trailingTokens.Clear();
        _hardwareLock.Dispose();
    }
}

#region Native Interop

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
internal struct PHYSICAL_MONITOR
{
    public IntPtr hPhysicalMonitor;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string szPhysicalMonitorDescription;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int left;
    public int top;
    public int right;
    public int bottom;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
internal struct MONITORINFOEX
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string szDevice;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
}

internal static class NativeMethods
{
    public const uint MONITORINFOF_PRIMARY = 0x00000001;

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);
}

#endregion
