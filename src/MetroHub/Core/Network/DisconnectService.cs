using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace MetroHub.Core.Network;

public class DisconnectService
{
    private static readonly Lazy<DisconnectService> _instance = new(() => new DisconnectService());
    public static DisconnectService Instance => _instance.Value;

    private bool _isDisconnected;
    private string? _lastDisabledAdapterName;

    public bool IsDisconnected => _isDisconnected;
    public event Action<bool>? DisconnectStateChanged;

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

        // 2. If Ethernet is active, disable the adapter via elevated command (Option B)
        var ethInfo = EthernetProvider.Instance.GetActiveEthernetInfo();
        if (ethInfo.IsConnected && !string.IsNullOrWhiteSpace(ethInfo.Name))
        {
            _lastDisabledAdapterName = ethInfo.Name;
            bool disabled = await RunElevatedAdapterCommandAsync("Disable-NetAdapter", ethInfo.Name);
            if (disabled)
            {
                _isDisconnected = true;
                DisconnectStateChanged?.Invoke(true);
                return true;
            }
        }

        return false;
    }

    public async Task<bool> ReconnectInternetAsync()
    {
        // 1. If Ethernet adapter was previously disabled, re-enable it
        if (!string.IsNullOrWhiteSpace(_lastDisabledAdapterName))
        {
            bool enabled = await RunElevatedAdapterCommandAsync("Enable-NetAdapter", _lastDisabledAdapterName);
            if (enabled)
            {
                _isDisconnected = false;
                DisconnectStateChanged?.Invoke(false);
                return true;
            }
        }

        // 2. If Wi-Fi was disconnected, attempt reconnect to available known network
        var networks = NativeWifiService.Instance.ScanAndGetAvailableNetworks();
        var known = networks.FirstOrDefault(n => n.IsProfileKnown);
        if (known != null)
        {
            bool connected = NativeWifiService.Instance.QuickConnect(known.Ssid);
            if (connected)
            {
                _isDisconnected = false;
                DisconnectStateChanged?.Invoke(false);
                return true;
            }
        }

        _isDisconnected = false;
        DisconnectStateChanged?.Invoke(false);
        return true;
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
