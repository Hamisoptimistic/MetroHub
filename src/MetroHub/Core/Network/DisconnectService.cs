using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MetroHub.Core.Network.Interop;

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
                bool passedHeader = false;
                foreach (var line in lines)
                {
                    if (line.Contains("---"))
                    {
                        passedHeader = true;
                        continue;
                    }
                    if (!passedHeader) continue;

                    var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 4)
                    {
                        string adminStateStr = parts[0];
                        string connectStateStr = parts[1];
                        string name = string.Join(" ", parts.Skip(3)).Trim();

                        bool isAdminDisabled = adminStateStr.Equals("Disabled", StringComparison.OrdinalIgnoreCase) ||
                                               adminStateStr.Equals("Désactivé", StringComparison.OrdinalIgnoreCase) ||
                                               adminStateStr.Equals("Deaktiviert", StringComparison.OrdinalIgnoreCase) ||
                                               adminStateStr.Equals("Deshabilitado", StringComparison.OrdinalIgnoreCase) ||
                                               adminStateStr.Equals("Disabilitato", StringComparison.OrdinalIgnoreCase) ||
                                               adminStateStr.Equals("已禁用", StringComparison.OrdinalIgnoreCase) ||
                                               adminStateStr.Equals("Отключено", StringComparison.OrdinalIgnoreCase);
                        bool isAdminEnabled = !isAdminDisabled;

                        bool isConnected = connectStateStr.Equals("Connected", StringComparison.OrdinalIgnoreCase) ||
                                           connectStateStr.Equals("Connecté", StringComparison.OrdinalIgnoreCase) ||
                                           connectStateStr.Equals("Verbunden", StringComparison.OrdinalIgnoreCase) ||
                                           connectStateStr.Equals("Conectado", StringComparison.OrdinalIgnoreCase) ||
                                           connectStateStr.Equals("Connesso", StringComparison.OrdinalIgnoreCase) ||
                                           connectStateStr.Equals("已连接", StringComparison.OrdinalIgnoreCase) ||
                                           connectStateStr.Equals("Подключено", StringComparison.OrdinalIgnoreCase);

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

    private static readonly Regex ValidAdapterNameRegex = new(@"^[\p{L}\p{N}\s\-_.#()]+$", RegexOptions.Compiled);

    private static Task<bool> RunElevatedAdapterCommandAsync(string command, string adapterName)
    {
        // Guard against command injection: validate adapter name and command strictly
        if (string.IsNullOrWhiteSpace(adapterName) || adapterName.Length > 128 || !ValidAdapterNameRegex.IsMatch(adapterName))
        {
            return Task.FromResult(false);
        }

        bool isEnable = string.Equals(command, "Enable-NetAdapter", StringComparison.OrdinalIgnoreCase);
        bool isDisable = string.Equals(command, "Disable-NetAdapter", StringComparison.OrdinalIgnoreCase);
        if (!isEnable && !isDisable)
        {
            return Task.FromResult(false);
        }

        return Task.Run(() =>
        {
            try
            {
                string adminState = isEnable ? "ENABLED" : "DISABLED";
                string safeNetshName = adapterName.Replace("\"", "\\\"");

                // 1. Primary Engine: Native netsh.exe with SW_HIDE (ProcessWindowStyle.Hidden)
                // Ultra-fast (<20ms), does NOT trigger Windows Terminal, zero console window
                var netshPsi = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = $"interface set interface name=\"{safeNetshName}\" admin={adminState}",
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
                // Escape single quotes for PowerShell string literal safely
                string safePsName = adapterName.Replace("'", "''");
                string psCmd = isEnable ? "Enable-NetAdapter" : "Disable-NetAdapter";

                var psPsi = new ProcessStartInfo
                {
                    FileName = "conhost.exe",
                    Arguments = $"--headless powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -Command \"{psCmd} -Name '{safePsName}' -Confirm:$false\"",
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

    private static readonly Dictionary<string, (string Description, PhysicalAdapterType Type, string Mac)> _persistentHardwareCache = new(StringComparer.OrdinalIgnoreCase);

    public static List<PhysicalAdapterInfo> GetPhysicalAdapters()
    {
        var result = new List<PhysicalAdapterInfo>();
        var netshStatuses = GetAllInterfaceStatuses();
        NetworkInterface[] nics = Array.Empty<NetworkInterface>();
        try
        {
            nics = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch { }

        // 1. Update persistent hardware cache from all currently visible physical NICS
        foreach (var nic in nics)
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                continue;

            string desc = nic.Description;
            string name = nic.Name;
            string descLower = desc.ToLowerInvariant();
            string nameLower = name.ToLowerInvariant();

            // Filter out virtual / miniports
            if (descLower.Contains("virtual") || descLower.Contains("hyper-v") || descLower.Contains("vmware") ||
                descLower.Contains("virtualbox") || descLower.Contains("tap-") || descLower.Contains("vpn") ||
                descLower.Contains("npcap") || descLower.Contains("wsl") || descLower.Contains("pseudo") ||
                descLower.Contains("bluetooth") || descLower.Contains("tailscale") || descLower.Contains("zerotier") ||
                descLower.Contains("wireguard") || descLower.Contains("wan miniport") || descLower.Contains("miniport") ||
                descLower.Contains("lightweight filter") || descLower.Contains("native mac layer") ||
                descLower.Contains("kernel debug") || descLower.Contains("packet scheduler") ||
                descLower.Contains("multiplexor") || descLower.Contains("teredo") || descLower.Contains("isatap") ||
                descLower.Contains("6to4") || descLower.Contains("tunnel") || descLower.Contains("pacer"))
            {
                continue;
            }

            if (nameLower.Contains("vethernet") || nameLower.Contains("wsl") || nameLower.Contains("loopback") ||
                nameLower.Contains("wan miniport") || nameLower.Contains("miniport") || nameLower.Contains("vpn"))
            {
                continue;
            }

            PhysicalAdapterType type = PhysicalAdapterType.Ethernet;
            if (EthernetProvider.IsUsbTetheringInterface(nic))
            {
                type = PhysicalAdapterType.UsbTethering;
            }
            else if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            {
                type = PhysicalAdapterType.Wifi;
            }
            else if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                     nic.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet ||
                     nic.NetworkInterfaceType == NetworkInterfaceType.FastEthernetFx ||
                     nic.NetworkInterfaceType == NetworkInterfaceType.FastEthernetT)
            {
                type = PhysicalAdapterType.Ethernet;
            }
            else
            {
                continue;
            }

            string mac = "--";
            try
            {
                var bytes = nic.GetPhysicalAddress()?.GetAddressBytes();
                if (bytes != null && bytes.Length > 0)
                {
                    mac = string.Join("-", bytes.Select(b => b.ToString("X2")));
                }
            }
            catch { }

            _persistentHardwareCache[name] = (desc, type, mac);
        }

        // 2. Iterate netsh interfaces
        foreach (var kvp in netshStatuses)
        {
            string ifaceName = kvp.Key;
            var st = kvp.Value;

            // Match with active nic if available
            var matchingNic = nics.FirstOrDefault(n => string.Equals(n.Name, ifaceName, StringComparison.OrdinalIgnoreCase));

            string description = string.Empty;
            PhysicalAdapterType adapterType = PhysicalAdapterType.Ethernet;
            string mac = "--";
            bool isConnected = st.IsConnected;
            long speedBps = 0;

            if (matchingNic != null)
            {
                string descLower = matchingNic.Description.ToLowerInvariant();
                string nameLower = matchingNic.Name.ToLowerInvariant();

                // Skip virtual
                if (descLower.Contains("virtual") || descLower.Contains("hyper-v") || descLower.Contains("vmware") ||
                    descLower.Contains("virtualbox") || descLower.Contains("tap-") || descLower.Contains("vpn") ||
                    descLower.Contains("npcap") || descLower.Contains("wsl") || descLower.Contains("pseudo") ||
                    descLower.Contains("bluetooth") || descLower.Contains("tailscale") || descLower.Contains("zerotier") ||
                    descLower.Contains("wireguard") || descLower.Contains("wan miniport") || descLower.Contains("miniport") ||
                    descLower.Contains("lightweight filter") || descLower.Contains("native mac layer") ||
                    descLower.Contains("kernel debug") || descLower.Contains("packet scheduler") ||
                    descLower.Contains("multiplexor") || descLower.Contains("teredo") || descLower.Contains("isatap") ||
                    descLower.Contains("6to4") || descLower.Contains("tunnel") || descLower.Contains("pacer") ||
                    nameLower.Contains("vethernet") || nameLower.Contains("wsl") || nameLower.Contains("vpn"))
                {
                    continue;
                }

                description = matchingNic.Description;
                try
                {
                    var bytes = matchingNic.GetPhysicalAddress()?.GetAddressBytes();
                    if (bytes != null && bytes.Length > 0)
                    {
                        mac = string.Join("-", bytes.Select(b => b.ToString("X2")));
                    }
                }
                catch { }

                if (EthernetProvider.IsUsbTetheringInterface(matchingNic))
                {
                    adapterType = PhysicalAdapterType.UsbTethering;
                }
                else if (matchingNic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    adapterType = PhysicalAdapterType.Wifi;
                }
                else
                {
                    adapterType = PhysicalAdapterType.Ethernet;
                }

                isConnected = matchingNic.OperationalStatus == OperationalStatus.Up;
                speedBps = matchingNic.Speed;
            }
            else if (_persistentHardwareCache.TryGetValue(ifaceName, out var cached))
            {
                description = cached.Description;
                adapterType = cached.Type;
                mac = cached.Mac;
                isConnected = false;
            }
            else
            {
                string ifaceLower = ifaceName.ToLowerInvariant();
                if (ifaceLower.Contains("wi-fi") || ifaceLower.Contains("wlan") || ifaceLower.Contains("wireless"))
                {
                    adapterType = PhysicalAdapterType.Wifi;
                    description = "Wi-Fi Adapter";
                }
                else if (ifaceLower.Contains("ethernet") || ifaceLower.Contains("lan"))
                {
                    adapterType = PhysicalAdapterType.Ethernet;
                    description = "Ethernet Controller";
                }
                else
                {
                    continue;
                }
            }

            result.Add(new PhysicalAdapterInfo
            {
                Id = matchingNic?.Id ?? ifaceName,
                Name = ifaceName,
                Description = description,
                AdapterType = adapterType,
                IsAdminEnabled = st.IsAdminEnabled,
                IsConnected = isConnected,
                LinkSpeedBitsPerSecond = speedBps,
                LinkSpeedString = FormatSpeed(speedBps),
                MacAddress = mac
            });
        }

        // Fallback: If netsh returned nothing or failed, populate directly from physical NICs
        if (result.Count == 0 && nics.Length > 0)
        {
            foreach (var nic in nics)
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    continue;

                string descLower = nic.Description.ToLowerInvariant();
                string nameLower = nic.Name.ToLowerInvariant();

                if (descLower.Contains("virtual") || descLower.Contains("hyper-v") || descLower.Contains("vmware") ||
                    descLower.Contains("virtualbox") || descLower.Contains("tap-") || descLower.Contains("vpn") ||
                    descLower.Contains("npcap") || descLower.Contains("wsl") || descLower.Contains("pseudo") ||
                    descLower.Contains("bluetooth") || descLower.Contains("tailscale") || descLower.Contains("zerotier") ||
                    descLower.Contains("wireguard") || descLower.Contains("wan miniport") || descLower.Contains("miniport") ||
                    descLower.Contains("lightweight filter") || descLower.Contains("native mac layer") ||
                    descLower.Contains("kernel debug") || descLower.Contains("packet scheduler") ||
                    descLower.Contains("multiplexor") || descLower.Contains("teredo") || descLower.Contains("isatap") ||
                    descLower.Contains("6to4") || descLower.Contains("tunnel") || descLower.Contains("pacer") ||
                    nameLower.Contains("vethernet") || nameLower.Contains("wsl") || nameLower.Contains("vpn"))
                {
                    continue;
                }

                PhysicalAdapterType type = PhysicalAdapterType.Ethernet;
                if (EthernetProvider.IsUsbTetheringInterface(nic))
                {
                    type = PhysicalAdapterType.UsbTethering;
                }
                else if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    type = PhysicalAdapterType.Wifi;
                }
                else if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                         nic.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet ||
                         nic.NetworkInterfaceType == NetworkInterfaceType.FastEthernetFx ||
                         nic.NetworkInterfaceType == NetworkInterfaceType.FastEthernetT)
                {
                    type = PhysicalAdapterType.Ethernet;
                }
                else
                {
                    continue;
                }

                string mac = "--";
                try
                {
                    var bytes = nic.GetPhysicalAddress()?.GetAddressBytes();
                    if (bytes != null && bytes.Length > 0)
                        mac = string.Join("-", bytes.Select(b => b.ToString("X2")));
                }
                catch { }

                result.Add(new PhysicalAdapterInfo
                {
                    Id = nic.Id,
                    Name = nic.Name,
                    Description = nic.Description,
                    AdapterType = type,
                    IsAdminEnabled = true,
                    IsConnected = nic.OperationalStatus == OperationalStatus.Up,
                    LinkSpeedBitsPerSecond = nic.Speed,
                    LinkSpeedString = FormatSpeed(nic.Speed),
                    MacAddress = mac
                });
            }
        }

        return result;
    }

    private static string FormatSpeed(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0) return "--";
        if (bitsPerSecond >= 1_000_000_000)
        {
            double gbps = (double)bitsPerSecond / 1_000_000_000.0;
            return $"{gbps:0.#} Gbps";
        }
        if (bitsPerSecond >= 1_000_000)
        {
            double mbps = (double)bitsPerSecond / 1_000_000.0;
            return $"{mbps:0.#} Mbps";
        }
        if (bitsPerSecond >= 1_000)
        {
            double kbps = (double)bitsPerSecond / 1_000.0;
            return $"{kbps:0.#} Kbps";
        }
        return $"{bitsPerSecond} bps";
    }

    public async Task<bool> SetAdapterAdminStateAsync(string adapterName, bool enable)
    {
        return await Task.Run(async () =>
        {
            string command = enable ? "Enable-NetAdapter" : "Disable-NetAdapter";
            bool result = await RunElevatedAdapterCommandAsync(command, adapterName);
            InvalidateStatusCache();
            return result;
        });
    }
}
