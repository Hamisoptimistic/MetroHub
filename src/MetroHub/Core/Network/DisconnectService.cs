using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace MetroHub.Core.Network;

public record WindowsInterfaceStatus(string Name, bool IsAdminEnabled, bool IsConnected);

public class DisconnectService
{
    private static readonly Lazy<DisconnectService> _instance = new(() => new DisconnectService());
    public static DisconnectService Instance => _instance.Value;

    private bool _isDisconnected;
    private bool _isEthernetDisabled;
    private string? _lastDisabledAdapterName;

    public bool IsDisconnected => _isDisconnected;
    public bool IsEthernetDisabled => _isEthernetDisabled;
    public string? LastDisabledAdapterName => _lastDisabledAdapterName;

    public event Action<bool>? DisconnectStateChanged;
    public event Action<bool>? EthernetDisabledStateChanged;

    private static Dictionary<string, WindowsInterfaceStatus> _cachedStatuses = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _lastCacheTime = DateTime.MinValue;
    private static readonly object _cacheLock = new();

    public static Dictionary<string, WindowsInterfaceStatus> GetCachedInterfaceStatuses(TimeSpan? maxAge = null)
    {
        lock (_cacheLock)
        {
            var limit = maxAge ?? TimeSpan.FromSeconds(3);
            if (DateTime.UtcNow - _lastCacheTime < limit && _cachedStatuses.Count > 0)
            {
                return new Dictionary<string, WindowsInterfaceStatus>(_cachedStatuses, StringComparer.OrdinalIgnoreCase);
            }
        }

        var fresh = GetAllInterfaceStatuses();
        lock (_cacheLock)
        {
            _cachedStatuses = fresh;
            _lastCacheTime = DateTime.UtcNow;
            return new Dictionary<string, WindowsInterfaceStatus>(_cachedStatuses, StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void InvalidateStatusCache()
    {
        lock (_cacheLock)
        {
            _lastCacheTime = DateTime.MinValue;
        }
    }

    public static Dictionary<string, WindowsInterfaceStatus> GetAllInterfaceStatuses()
    {
        var result = new Dictionary<string, WindowsInterfaceStatus>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = "interface show interface",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(1000);

                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 4 &&
                        (parts[0].Equals("Enabled", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("Disabled", StringComparison.OrdinalIgnoreCase)))
                    {
                        bool isAdminEnabled = parts[0].Equals("Enabled", StringComparison.OrdinalIgnoreCase);
                        bool isConnected = parts[1].Equals("Connected", StringComparison.OrdinalIgnoreCase);
                        string name = string.Join(" ", parts.Skip(3)).Trim();

                        result[name] = new WindowsInterfaceStatus(name, isAdminEnabled, isConnected);
                    }
                }
            }
        }
        catch { }
        return result;
    }

    public void SetEthernetDisabledState(bool isDisabled)
    {
        if (_isEthernetDisabled != isDisabled)
        {
            _isEthernetDisabled = isDisabled;
            EthernetDisabledStateChanged?.Invoke(isDisabled);
        }
    }

    public async Task<bool> ToggleEthernetAdapterAsync(string? adapterName = null)
    {
        return await Task.Run(async () =>
        {
            string targetName = !string.IsNullOrWhiteSpace(adapterName)
                ? adapterName
                : (!string.IsNullOrWhiteSpace(_lastDisabledAdapterName) ? _lastDisabledAdapterName : "Ethernet");

            // Synchronize with authoritative Windows Admin State from netsh
            var statuses = GetAllInterfaceStatuses();
            bool isCurrentlyDisabled = false;
            if (statuses.TryGetValue(targetName, out var st))
            {
                isCurrentlyDisabled = !st.IsAdminEnabled;
            }
            else if (statuses.TryGetValue("Ethernet", out var defaultEth))
            {
                isCurrentlyDisabled = !defaultEth.IsAdminEnabled;
                targetName = defaultEth.Name;
            }
            else
            {
                isCurrentlyDisabled = _isEthernetDisabled;
            }

            if (isCurrentlyDisabled)
            {
                bool enabled = await RunElevatedAdapterCommandAsync("Enable-NetAdapter", targetName);
                if (enabled)
                {
                    _isEthernetDisabled = false;
                    _isDisconnected = false;
                    InvalidateStatusCache();
                    EthernetDisabledStateChanged?.Invoke(false);
                    DisconnectStateChanged?.Invoke(false);
                    return true;
                }
                return false;
            }
            else
            {
                _lastDisabledAdapterName = targetName;
                bool disabled = await RunElevatedAdapterCommandAsync("Disable-NetAdapter", targetName);
                if (disabled)
                {
                    _isEthernetDisabled = true;
                    InvalidateStatusCache();
                    EthernetDisabledStateChanged?.Invoke(true);
                    UpdateOverallDisconnectedState();
                    return true;
                }
                return false;
            }
        });
    }

    public Task<bool> ToggleWifiConnectionAsync()
    {
        return Task.Run(() =>
        {
            var wifiDetails = NativeWifiService.Instance.GetCurrentConnectionDetails();
            if (wifiDetails.IsConnected)
            {
                bool success = NativeWifiService.Instance.Disconnect();
                UpdateOverallDisconnectedState();
                return success;
            }
            else
            {
                var networks = NativeWifiService.Instance.ScanAndGetAvailableNetworks();
                var known = networks.FirstOrDefault(n => n.IsProfileKnown);
                if (known != null)
                {
                    bool connected = NativeWifiService.Instance.QuickConnect(known.Ssid);
                    if (connected)
                    {
                        _isDisconnected = false;
                        DisconnectStateChanged?.Invoke(false);
                    }
                    else
                    {
                        UpdateOverallDisconnectedState();
                    }
                    return connected;
                }
                return false;
            }
        });
    }

    private void UpdateOverallDisconnectedState()
    {
        bool wifiConn = NativeWifiService.Instance.GetCurrentConnectionDetails().IsConnected;
        bool ethUp = EthernetProvider.Instance.GetActiveEthernetInfo().IsConnected;
        bool newDisconnected = !wifiConn && !ethUp;
        if (_isDisconnected != newDisconnected)
        {
            _isDisconnected = newDisconnected;
            DisconnectStateChanged?.Invoke(newDisconnected);
        }
    }

    public async Task<bool> ToggleInternetAsync()
    {
        if (_isDisconnected)
        {
            return await ReconnectInternetAsync();
        }
        else
        {
            return await DisconnectInternetAsync();
        }
    }

    public async Task<bool> DisconnectInternetAsync()
    {
        // 1. Check if Wi-Fi is the active internet path
        var wifiDetails = NativeWifiService.Instance.GetCurrentConnectionDetails();
        if (wifiDetails.IsConnected)
        {
            bool success = NativeWifiService.Instance.Disconnect();
            if (success)
            {
                _isDisconnected = true;
                DisconnectStateChanged?.Invoke(true);
                return true;
            }
        }

        // 2. If Ethernet is active, disable the adapter via elevated command
        var ethInfo = EthernetProvider.Instance.GetActiveEthernetInfo();
        if (ethInfo.IsConnected && !string.IsNullOrWhiteSpace(ethInfo.Name))
        {
            return await ToggleEthernetAdapterAsync(ethInfo.Name);
        }

        return false;
    }

    public async Task<bool> ReconnectInternetAsync()
    {
        if (_isEthernetDisabled)
        {
            return await ToggleEthernetAdapterAsync();
        }
        return await ToggleWifiConnectionAsync();
    }

    private static Task<bool> RunElevatedAdapterCommandAsync(string command, string adapterName)
    {
        return Task.Run(() =>
        {
            try
            {
                string adminState = command.StartsWith("Enable", StringComparison.OrdinalIgnoreCase) ? "ENABLED" : "DISABLED";

                // 1. Primary Engine: Native netsh.exe with SW_HIDE (ProcessWindowStyle.Hidden)
                // Ultra-fast (<20ms), does NOT trigger Windows Terminal, zero console window
                var netshPsi = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = $"interface set interface name=\"{adapterName}\" admin={adminState}",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                };

                using (var netshProc = Process.Start(netshPsi))
                {
                    if (netshProc != null)
                    {
                        // Give user plenty of time (15 seconds) to review and accept the UAC prompt
                        if (netshProc.WaitForExit(15000) && netshProc.ExitCode == 0)
                        {
                            return true;
                        }
                    }
                }

                // 2. Secondary Engine: Headless PowerShell fallback via conhost.exe
                var psPsi = new ProcessStartInfo
                {
                    FileName = "conhost.exe",
                    Arguments = $"--headless powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -Command \"{command} -Name '{adapterName}' -Confirm:$false\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                };

                using (var psProc = Process.Start(psPsi))
                {
                    if (psProc == null) return false;
                    return psProc.WaitForExit(15000) && psProc.ExitCode == 0;
                }
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // User explicitly clicked "No" or "Cancel" on UAC prompt.
                // Do NOT fall back to PowerShell to spam another prompt!
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        });
    }
}
