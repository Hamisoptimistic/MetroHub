using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;

namespace MetroHub.Core.Network;

public class NetworkHealthService
{
    private static readonly Lazy<NetworkHealthService> _instance = new(() => new NetworkHealthService());
    public static NetworkHealthService Instance => _instance.Value;

    public NetworkHealthStatus CurrentStatus { get; private set; } = new() { Connectivity = QueryFastConnectivity() };
    public event Action<NetworkHealthStatus>? HealthChanged;

    public NetworkHealthService()
    {
        try
        {
            Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        }
        catch { }
    }

    public static ConnectivityLevel QueryFastConnectivity()
    {
        try
        {
            var profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
            if (profile != null)
            {
                var level = profile.GetNetworkConnectivityLevel();
                return level switch
                {
                    NetworkConnectivityLevel.InternetAccess => ConnectivityLevel.InternetAccess,
                    NetworkConnectivityLevel.ConstrainedInternetAccess => ConnectivityLevel.ConstrainedInternet,
                    NetworkConnectivityLevel.LocalAccess => ConnectivityLevel.LocalAccess,
                    _ => ConnectivityLevel.None
                };
            }
            return NetworkInterface.GetIsNetworkAvailable()
                ? ConnectivityLevel.LocalAccess
                : ConnectivityLevel.None;
        }
        catch
        {
            return NetworkInterface.GetIsNetworkAvailable()
                ? ConnectivityLevel.LocalAccess
                : ConnectivityLevel.None;
        }
    }

    private async void OnNetworkStatusChanged(object sender)
    {
        await EvaluateHealthAsync();
    }

    public async Task<NetworkHealthStatus> EvaluateHealthAsync()
    {
        var status = new NetworkHealthStatus();

        // 1. Windows NCSI (Network Connectivity Status Indicator)
        status.Connectivity = QueryFastConnectivity();

        // 2. DNS Resolution Diagnostic
        var sw = Stopwatch.StartNew();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync("www.msftconnecttest.com");
            sw.Stop();
            status.HasDnsResolution = addresses != null && addresses.Length > 0;
            status.DnsResolutionTimeMs = sw.Elapsed.TotalMilliseconds;
        }
        catch
        {
            sw.Stop();
            status.HasDnsResolution = false;
            status.DnsResolutionTimeMs = -1;
        }

        // 3. Packet Loss & Latency Diagnostic (4 pings)
        using var ping = new Ping();
        int lostPackets = 0;
        long totalRtt = 0;
        int successfulPings = 0;
        const int pingCount = 3;

        for (int i = 0; i < pingCount; i++)
        {
            try
            {
                var reply = await ping.SendPingAsync("1.1.1.1", 1000);
                if (reply.Status == IPStatus.Success)
                {
                    successfulPings++;
                    totalRtt += reply.RoundtripTime;
                }
                else
                {
                    lostPackets++;
                }
            }
            catch
            {
                lostPackets++;
            }
        }

        status.PacketLossPercent = (double)lostPackets / pingCount * 100.0;
        status.LatencyMs = successfulPings > 0 ? totalRtt / successfulPings : -1;

        // If NCSI was uncertain, let ping confirm connectivity
        if (successfulPings > 0 && status.Connectivity == ConnectivityLevel.LocalAccess)
        {
            status.Connectivity = ConnectivityLevel.InternetAccess;
        }

        // Summary generation
        if (status.Connectivity == ConnectivityLevel.InternetAccess)
        {
            status.HealthSummary = status.PacketLossPercent == 0
                ? $"Connection Healthy ({status.LatencyMs} ms, 0% loss, DNS: {status.DnsResolutionTimeMs:0}ms)"
                : $"Degraded Connection ({status.PacketLossPercent:0}% packet loss)";
        }
        else if (status.Connectivity == ConnectivityLevel.ConstrainedInternet)
        {
            status.HealthSummary = "Captive Portal detected. Web login required.";
        }
        else if (status.Connectivity == ConnectivityLevel.LocalAccess)
        {
            status.HealthSummary = "Connected to Local Network. No Internet Gateway.";
        }
        else
        {
            status.HealthSummary = "Offline. Check network cable or Wi-Fi radio.";
        }

        CurrentStatus = status;
        HealthChanged?.Invoke(status);
        return status;
    }
}
