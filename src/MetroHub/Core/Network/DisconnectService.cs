using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace MetroHub.Core.Network;

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

    public async Task<bool> ToggleEthernetAdapterAsync(string? adapterName = null)
    {
        string targetName = adapterName ?? _lastDisabledAdapterName ?? "Ethernet";
        if (_isEthernetDisabled)
        {
            bool enabled = await RunElevatedAdapterCommandAsync("Enable-NetAdapter", targetName);
            if (enabled)
            {
                _isEthernetDisabled = false;
                EthernetDisabledStateChanged?.Invoke(false);
                UpdateOverallDisconnectedState();
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
                EthernetDisabledStateChanged?.Invoke(true);
                UpdateOverallDisconnectedState();
                return true;
            }
            return false;
        }
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
                    UpdateOverallDisconnectedState();
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
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -WindowStyle Hidden -Command \"{command} -Name '{adapterName}' -Confirm:$false\"",
                    UseShellExecute = true,
                    Verb = "runas" // Requests elevation via UAC prompt
                };

                using var proc = Process.Start(psi);
                if (proc == null) return false;

                proc.WaitForExit(5000);
                return proc.ExitCode == 0;
            }
            catch (Exception)
            {
                // User clicked "No" on UAC prompt or canceled
                return false;
            }
        });
    }
}
