using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;

namespace MetroHub.Core.Network;

public sealed class NetworkHealthService
{
    private static readonly Lazy<NetworkHealthService> _instance = new(() => new NetworkHealthService());
    public static NetworkHealthService Instance => _instance.Value;

    public NetworkHealthStatus CurrentStatus { get; private set; } = new() { Connectivity = QueryFastConnectivity() };
    public event Action<NetworkHealthStatus>? HealthChanged;

    private DateTime _lastSuccessfulTrafficTime = DateTime.UtcNow;
    private DateTime _lastProbeTime = DateTime.MinValue;
    private bool _lastProbeResult = true;
    private readonly object _probeLock = new();

    /// <summary>
    /// Modular, zero-overhead reachability check usable across Ethernet, Wi-Fi, and any adapter.
    /// Rules:
    /// 1. When MetroHub is hidden, skips probing completely (0 CPU, 0 network).
    /// 2. If current state is LocalAccess (No Internet / Yellow):
    ///    - Passive local traffic is NEVER trusted (prevents USB/LAN broadcast noise from falsely indicating internet).
    ///    - Probes gently every 2.5s until real WAN access is verified.
    /// 3. If current state is InternetAccess (Green):
    ///    - If download throughput > 5 KB/s (5120 B/s), internet is confirmed passively (0 packets sent).
    ///    - If recent traffic was observed within last 5 seconds, skips probing.
    ///    - When idle for > 5s, gently probes once every 10 seconds.
    /// </summary>
    public async Task<bool> CheckPassiveOrActiveReachabilityAsync(double currentDownloadSpeedBps, bool isHubVisible, bool currentlyHasInternet)
    {
        if (!isHubVisible)
        {
            return _lastProbeResult;
        }

        // State Latching: When internet is down, local traffic is ignored.
        // It requires affirmative WAN probe success to recover.
        if (!currentlyHasInternet)
        {
            lock (_probeLock)
            {
                if (DateTime.UtcNow - _lastProbeTime < TimeSpan.FromSeconds(2.5))
                {
                    return false;
                }
                _lastProbeTime = DateTime.UtcNow;
            }

            bool recovered = await CheckInternetReachabilityAsync(800);
            _lastProbeResult = recovered;
            if (recovered)
            {
                _lastSuccessfulTrafficTime = DateTime.UtcNow;
            }
            return recovered;
        }

        // Rule 1: Passive Traffic Check when connected (Zero packets sent while streaming/browsing)
        if (currentDownloadSpeedBps > 5120)
        {
            _lastSuccessfulTrafficTime = DateTime.UtcNow;
            _lastProbeResult = true;
            return true;
        }

        // Rule 2: Recent traffic window (within 5s of active traffic, don't probe)
        if (DateTime.UtcNow - _lastSuccessfulTrafficTime < TimeSpan.FromSeconds(5))
        {
            return true;
        }

        // Rule 3: 10-second throttled active check only when traffic is idle
        lock (_probeLock)
        {
            if (DateTime.UtcNow - _lastProbeTime < TimeSpan.FromSeconds(10))
            {
                return _lastProbeResult;
            }
            _lastProbeTime = DateTime.UtcNow;
        }

        bool reachable = await CheckInternetReachabilityAsync(800);
        _lastProbeResult = reachable;
        if (reachable)
        {
            _lastSuccessfulTrafficTime = DateTime.UtcNow;
        }

        return reachable;
    }

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

    public static async Task<bool> CheckInternetReachabilityAsync(int timeoutMs = 800)
    {
        // 1. Primary Check: Direct TCP socket handshake to Cloudflare Anycast DNS (1.1.1.1:53)
        // Bypasses Windows DNS client cache completely — requires real WAN packet routing.
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var connectTask = client.ConnectAsync("1.1.1.1", 53);
            var completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs));
            if (completed == connectTask && client.Connected)
            {
                return true;
            }
        }
        catch { }

        // 2. Secondary Fallback: Direct TCP socket handshake to Google Anycast DNS (8.8.8.8:53)
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var connectTask = client.ConnectAsync("8.8.8.8", 53);
            var completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs));
            if (completed == connectTask && client.Connected)
            {
                return true;
            }
        }
        catch { }

        return false;
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

        bool hasTcpReachability = await CheckInternetReachabilityAsync(800);

        // If either Ping or raw TCP reaches the outside world, internet is verified
        if (successfulPings > 0 || hasTcpReachability)
        {
            if (status.Connectivity == ConnectivityLevel.LocalAccess)
            {
                status.Connectivity = ConnectivityLevel.InternetAccess;
            }
        }
        else
        {
            // Both Ping and raw TCP failed — demote stale Windows NCSI state
            status.Connectivity = ConnectivityLevel.LocalAccess;
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
