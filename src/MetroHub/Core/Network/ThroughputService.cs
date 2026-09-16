using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace MetroHub.Core.Network;

public sealed class ThroughputService : IDisposable
{
    private static readonly Lazy<ThroughputService> _instance = new(() => new ThroughputService());
    public static ThroughputService Instance => _instance.Value;

    private readonly object _lock = new();
    private Timer? _throughputTimer;
    private Timer? _latencyTimer;
    private bool _isRunning;
    private bool _isPaused;

    private string? _monitoredInterfaceId;
    private long _prevBytesReceived = -1;
    private long _prevBytesSent = -1;
    private DateTime _prevSampleTime = DateTime.MinValue;

    private const int MaxHistorySamples = 30;
    private readonly List<ThroughputSample> _history = new(MaxHistorySamples);

    public event Action<ThroughputMetrics>? ThroughputUpdated;
    public event Action<LatencyMetrics>? LatencyUpdated;

    public ThroughputMetrics CurrentThroughput { get; private set; } = new();
    public LatencyMetrics CurrentLatency { get; private set; } = new();

    public IReadOnlyList<ThroughputSample> History
    {
        get
        {
            lock (_lock)
            {
                return _history.ToArray();
            }
        }
    }

    public ThroughputService()
    {
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_isRunning) return;
            _isRunning = true;
            _isPaused = false;

            // Reset baselines
            _prevBytesReceived = -1;
            _prevBytesSent = -1;
            _prevSampleTime = DateTime.MinValue;
            _history.Clear();

            // 1000ms throughput timer
            _throughputTimer = new Timer(OnThroughputTick, null, 0, 1000);

            // 3000ms latency ping timer
            _latencyTimer = new Timer(OnLatencyTick, null, 500, 3000);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _isRunning = false;
            _throughputTimer?.Dispose();
            _throughputTimer = null;
            _latencyTimer?.Dispose();
            _latencyTimer = null;
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            _isPaused = true;
            _throughputTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _latencyTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (!_isRunning)
            {
                Start();
                return;
            }

            _isPaused = false;
            _prevBytesReceived = -1;
            _prevBytesSent = -1;
            _prevSampleTime = DateTime.MinValue;
            _throughputTimer?.Change(0, 1000);
            _latencyTimer?.Change(500, 3000);
        }
    }

    private void OnThroughputTick(object? state)
    {
        if (_isPaused || !_isRunning) return;

        try
        {
            var activeNic = FindActiveGatewayInterface();
            if (activeNic == null)
            {
                PublishZeroThroughput();
                return;
            }

            var now = DateTime.UtcNow;
            var stats = activeNic.GetIPStatistics();
            long bytesRecv = stats.BytesReceived;
            long bytesSent = stats.BytesSent;

            // If interface changed, reset baseline
            if (_monitoredInterfaceId != activeNic.Id || _prevBytesReceived < 0 || _prevBytesSent < 0)
            {
                _monitoredInterfaceId = activeNic.Id;
                _prevBytesReceived = bytesRecv;
                _prevBytesSent = bytesSent;
                _prevSampleTime = now;
                return;
            }

            double elapsedSec = (now - _prevSampleTime).TotalSeconds;
            if (elapsedSec <= 0.05) return;

            long deltaRecv = Math.Max(0, bytesRecv - _prevBytesReceived);
            long deltaSent = Math.Max(0, bytesSent - _prevBytesSent);

            _prevBytesReceived = bytesRecv;
            _prevBytesSent = bytesSent;
            _prevSampleTime = now;

            double downSpeedBytes = deltaRecv / elapsedSec;
            double upSpeedBytes = deltaSent / elapsedSec;

            var metrics = new ThroughputMetrics
            {
                DownloadBytesPerSec = downSpeedBytes,
                UploadBytesPerSec = upSpeedBytes,
                TotalBytesReceived = (ulong)bytesRecv,
                TotalBytesSent = (ulong)bytesSent
            };

            lock (_lock)
            {
                CurrentThroughput = metrics;
                if (_history.Count >= MaxHistorySamples)
                {
                    _history.RemoveAt(0);
                }
                _history.Add(new ThroughputSample
                {
                    DownloadBytesPerSec = downSpeedBytes,
                    UploadBytesPerSec = upSpeedBytes,
                    Timestamp = now
                });
            }

            ThroughputUpdated?.Invoke(metrics);
        }
        catch { }
    }

    private async void OnLatencyTick(object? state)
    {
        if (_isPaused || !_isRunning) return;

        try
        {
            var latency = await MeasureLatencyAsync();
            lock (_lock)
            {
                CurrentLatency = latency;
            }
            LatencyUpdated?.Invoke(latency);
        }
        catch { }
    }

    public static async Task<LatencyMetrics> MeasureLatencyAsync()
    {
        string[] targets = { "1.1.1.1", "8.8.8.8" };
        using var ping = new Ping();

        foreach (var host in targets)
        {
            try
            {
                var reply = await ping.SendPingAsync(host, 1200);
                if (reply != null && reply.Status == IPStatus.Success)
                {
                    return new LatencyMetrics
                    {
                        PingMs = reply.RoundtripTime,
                        TargetHost = host
                    };
                }
            }
            catch { }
        }

        // Try Default Gateway if internet DNS failed
        try
        {
            var gw = GetDefaultGatewayIp();
            if (!string.IsNullOrEmpty(gw))
            {
                var reply = await ping.SendPingAsync(gw, 800);
                if (reply != null && reply.Status == IPStatus.Success)
                {
                    return new LatencyMetrics
                    {
                        PingMs = reply.RoundtripTime,
                        TargetHost = "Gateway"
                    };
                }
            }
        }
        catch { }

        return new LatencyMetrics
        {
            PingMs = -1,
            TargetHost = targets[0]
        };
    }

    private void PublishZeroThroughput()
    {
        var zero = new ThroughputMetrics();
        lock (_lock)
        {
            CurrentThroughput = zero;
        }
        ThroughputUpdated?.Invoke(zero);
    }

    private static NetworkInterface? FindActiveGatewayInterface()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces();

        // 1. Prioritize real physical interfaces that are Up, not Loopback/Tunnel/Virtual, with an IPv4 Default Gateway
        var physicalCandidates = interfaces
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                          nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                          nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                          !IsVirtualAdapter(nic))
            .ToList();

        var withGateway = physicalCandidates.FirstOrDefault(nic =>
        {
            try
            {
                return nic.GetIPProperties().GatewayAddresses
                    .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
            }
            catch { return false; }
        });

        if (withGateway != null)
        {
            return withGateway;
        }

        // 2. Fallback to any active physical Ethernet or Wi-Fi
        var physicalFallback = physicalCandidates.FirstOrDefault(nic =>
            nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or
                                        NetworkInterfaceType.GigabitEthernet or
                                        NetworkInterfaceType.Wireless80211);
        if (physicalFallback != null)
        {
            return physicalFallback;
        }

        // 3. Ultimate fallback (e.g. running entirely inside a VM where only virtual NICs exist)
        return interfaces
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                          nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .FirstOrDefault(nic =>
            {
                try
                {
                    return nic.GetIPProperties().GatewayAddresses
                        .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                }
                catch { return false; }
            });
    }

    private static bool IsVirtualAdapter(NetworkInterface nic)
    {
        // Always accept USB tethering interfaces from Android / iPhone
        if (EthernetProvider.IsUsbTetheringInterface(nic)) return false;

        string desc = nic.Description.ToLowerInvariant();
        string name = nic.Name.ToLowerInvariant();

        return desc.Contains("virtual") || desc.Contains("hyper-v") || desc.Contains("vmware") ||
               desc.Contains("virtualbox") || desc.Contains("tap-") || desc.Contains("vpn") ||
               desc.Contains("npcap") || desc.Contains("wsl") || desc.Contains("pseudo") ||
               desc.Contains("bluetooth") || desc.Contains("loopback") || desc.Contains("tailscale") ||
               desc.Contains("zerotier") || desc.Contains("wireguard") || desc.Contains("wan miniport") ||
               desc.Contains("miniport") || desc.Contains("lightweight filter") || desc.Contains("native mac layer") ||
               desc.Contains("kernel debug") || desc.Contains("packet scheduler") ||
               desc.Contains("multiplexor") || desc.Contains("teredo") || desc.Contains("isatap") ||
               desc.Contains("6to4") || desc.Contains("tunnel") || desc.Contains("pacer") ||
               name.Contains("vethernet") || name.Contains("wsl") || name.Contains("loopback") ||
               name.Contains("wan miniport") || name.Contains("miniport") || name.Contains("vpn");
    }

    private static string? GetDefaultGatewayIp()
    {
        try
        {
            var nic = FindActiveGatewayInterface();
            if (nic == null) return null;
            var gw = nic.GetIPProperties().GatewayAddresses
                .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
            return gw?.Address.ToString();
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
