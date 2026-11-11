using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Services;

namespace MetroHub.Core.Network;

public sealed class ThroughputService : IDisposable
{
    private static readonly Lazy<ThroughputService> _instance = new(() => new ThroughputService());
    public static ThroughputService Instance => _instance.Value;

    private readonly Lock _lock = new();
    private Timer? _throughputTimer;
    private Timer? _latencyTimer;
    private CancellationTokenSource? _latencyCts;
    private bool _isRunning;
    private bool _isPaused;

    private NetworkInterface? _cachedActiveNic;
    private string? _monitoredInterfaceId;
    private long _prevBytesReceived = -1;
    private long _prevBytesSent = -1;
    private DateTime _prevSampleTime = DateTime.MinValue;

    private const int MaxHistorySamples = 30;
    private static readonly SearchValues<string> s_virtualNicDescKeywords = SearchValues.Create([
        "virtual", "hyper-v", "vmware", "virtualbox", "tap-", "vpn",
        "npcap", "wsl", "pseudo", "bluetooth", "loopback", "tailscale",
        "zerotier", "wireguard", "wan miniport", "miniport", "lightweight filter", "native mac layer",
        "kernel debug", "packet scheduler", "multiplexor", "teredo", "isatap",
        "6to4", "tunnel", "pacer"
    ], StringComparison.OrdinalIgnoreCase);
    private static readonly SearchValues<string> s_virtualNicNameKeywords = SearchValues.Create([
        "vethernet", "wsl", "loopback", "wan miniport", "miniport", "vpn"
    ], StringComparison.OrdinalIgnoreCase);
    private readonly Queue<ThroughputSample> _history = new(MaxHistorySamples);

    public event Action<ThroughputMetrics>? ThroughputUpdated;
    public event Action<LatencyMetrics>? LatencyUpdated;

    private readonly ThroughputMetrics _currentMetrics = new();
    public ThroughputMetrics CurrentThroughput { get; private set; }
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
        CurrentThroughput = _currentMetrics;
        try
        {
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        }
        catch (Exception ex)
        {
            Safe.LogThrottled(ex, "ThroughputService.NetworkChangeHooks");
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            _cachedActiveNic = null;
        }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        lock (_lock)
        {
            _cachedActiveNic = null;
        }
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

            // 30000ms latency ping timer (relaxed to avoid idle CPU / network socket churn)
            _latencyTimer = new Timer(OnLatencyTick, null, 2000, 30000);
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
            _latencyCts?.Cancel();
            _latencyCts?.Dispose();
            _latencyCts = null;
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            _isPaused = true;
            _throughputTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _latencyTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _latencyCts?.Cancel();
            _latencyCts?.Dispose();
            _latencyCts = null;
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
            _latencyTimer?.Change(2000, 30000);
        }
    }

    private void OnThroughputTick(object? state)
    {
        if (_isPaused || !_isRunning)
        {
            if (MetroHub.Core.Services.HubState.IsHidden)
            {
                MetroHub.Core.Services.HiddenDiagnosticsLogger.LogHiddenEvent("ThroughputService", "OnThroughputTick", "Skipped tick (properly paused)");
            }
            return;
        }

        try
        {
            NetworkInterface? activeNic;
            lock (_lock)
            {
                activeNic = _cachedActiveNic;
            }

            if (activeNic == null || activeNic.OperationalStatus != OperationalStatus.Up)
            {
                activeNic = FindActiveGatewayInterface();
                lock (_lock)
                {
                    _cachedActiveNic = activeNic;
                }
            }

            if (activeNic == null)
            {
                PublishZeroThroughput();
                return;
            }

            var now = DateTime.UtcNow;
            IPInterfaceStatistics stats;
            try
            {
                stats = activeNic.GetIPStatistics();
            }
            catch (Exception ex)
            {
                Safe.LogThrottled(ex, "ThroughputService.GetIPStatistics");
                lock (_lock)
                {
                    _cachedActiveNic = null;
                }
                return;
            }

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

            bool speedsChanged;
            lock (_lock)
            {
                speedsChanged = Math.Abs(_currentMetrics.DownloadBytesPerSec - downSpeedBytes) > 0.001 ||
                                Math.Abs(_currentMetrics.UploadBytesPerSec - upSpeedBytes) > 0.001 ||
                                _currentMetrics.TotalBytesReceived != (ulong)bytesRecv ||
                                _currentMetrics.TotalBytesSent != (ulong)bytesSent;

                if (speedsChanged)
                {
                    _currentMetrics.DownloadBytesPerSec = downSpeedBytes;
                    _currentMetrics.UploadBytesPerSec = upSpeedBytes;
                    _currentMetrics.TotalBytesReceived = (ulong)bytesRecv;
                    _currentMetrics.TotalBytesSent = (ulong)bytesSent;

                    CurrentThroughput = _currentMetrics;

                    while (_history.Count >= MaxHistorySamples)
                    {
                        _history.Dequeue();
                    }
                    _history.Enqueue(new ThroughputSample
                    {
                        DownloadBytesPerSec = downSpeedBytes,
                        UploadBytesPerSec = upSpeedBytes,
                        Timestamp = now
                    });
                }
            }

            if (speedsChanged)
            {
                ThroughputUpdated?.Invoke(_currentMetrics);
            }
        }
        catch (Exception ex)
        {
            Safe.LogThrottled(ex, "ThroughputService.OnSampleTick");
        }
    }

    private void OnLatencyTick(object? state)
    {
        MeasureLatencyTickAsync().FireAndForget("ThroughputService.MeasureLatencyTick");
    }

    private async Task MeasureLatencyTickAsync()
    {
        try
        {
            CancellationToken token;
            lock (_lock)
            {
                if (_isPaused || !_isRunning)
                {
                    if (MetroHub.Core.Services.HubState.IsHidden)
                    {
                        MetroHub.Core.Services.HiddenDiagnosticsLogger.LogHiddenEvent("ThroughputService", "OnLatencyTick", "Skipped tick (properly paused)");
                    }
                    return;
                }

                _latencyCts?.Cancel();
                _latencyCts?.Dispose();
                _latencyCts = new CancellationTokenSource();
                token = _latencyCts.Token;
            }

            var latency = await MeasureLatencyAsync(token).ConfigureAwait(false);
            if (token.IsCancellationRequested || _isPaused) return;

            lock (_lock)
            {
                CurrentLatency = latency;
            }
            LatencyUpdated?.Invoke(latency);
        }
        catch (Exception ex)
        {
            Safe.LogThrottled(ex, "ThroughputService.MeasureLatencyTick");
        }
    }

    public static async Task<LatencyMetrics> MeasureLatencyAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested || !NetworkInterface.GetIsNetworkAvailable() || !NetworkHealthService.HasActiveGateway())
        {
            return new LatencyMetrics { PingMs = -1, TargetHost = "--" };
        }

        string[] targets = { "1.1.1.1", "8.8.8.8" };
        using var ping = new Ping();

        foreach (var host in targets)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                using var reg = cancellationToken.Register(() => { try { ping.SendAsyncCancel(); } catch (Exception ex) { Safe.Ignore(ex, "ThroughputService.PingCancel"); } });
                var reply = await ping.SendPingAsync(host, 1200);
                if (cancellationToken.IsCancellationRequested) break;
                if (reply != null && reply.Status == IPStatus.Success)
                {
                    return new LatencyMetrics
                    {
                        PingMs = reply.RoundtripTime,
                        TargetHost = host
                    };
                }
            }
            catch (Exception ex)
            {
                Safe.LogThrottled(ex, "ThroughputService.PingHost");
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new LatencyMetrics { PingMs = -1, TargetHost = "--" };
        }

        // Try Default Gateway if internet DNS failed
        try
        {
            var gw = GetDefaultGatewayIp();
            if (!string.IsNullOrEmpty(gw) && !cancellationToken.IsCancellationRequested)
            {
                using var reg = cancellationToken.Register(() => { try { ping.SendAsyncCancel(); } catch (Exception ex) { Safe.Ignore(ex, "ThroughputService.PingCancel"); } });
                var reply = await ping.SendPingAsync(gw, 800);
                if (cancellationToken.IsCancellationRequested)
                {
                    return new LatencyMetrics { PingMs = -1, TargetHost = "--" };
                }
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
        catch (Exception ex)
        {
            Safe.LogThrottled(ex, "ThroughputService.PingGateway");
        }

        return new LatencyMetrics
        {
            PingMs = -1,
            TargetHost = "--"
        };
    }

    private void PublishZeroThroughput()
    {
        bool changed;
        lock (_lock)
        {
            changed = _currentMetrics.DownloadBytesPerSec != 0 || _currentMetrics.UploadBytesPerSec != 0;
            if (changed)
            {
                _currentMetrics.DownloadBytesPerSec = 0;
                _currentMetrics.UploadBytesPerSec = 0;
                CurrentThroughput = _currentMetrics;
            }
        }
        if (changed)
        {
            ThroughputUpdated?.Invoke(_currentMetrics);
        }
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
            catch (Exception ex)
            {
                Safe.LogThrottled(ex, "ThroughputService.GetIPProperties");
                return false;
            }
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
                catch (Exception ex)
                {
                    Safe.LogThrottled(ex, "ThroughputService.GetIPProperties");
                    return false;
                }
            });
    }

    private static bool IsVirtualAdapter(NetworkInterface nic)
    {
        // Always accept USB tethering interfaces from Android / iPhone
        if (EthernetProvider.IsUsbTetheringInterface(nic)) return false;

        return nic.Description.AsSpan().ContainsAny(s_virtualNicDescKeywords) ||
               nic.Name.AsSpan().ContainsAny(s_virtualNicNameKeywords);
    }

    private static string? GetDefaultGatewayIp()
    {
        try
        {
            var nic = Instance._cachedActiveNic ?? FindActiveGatewayInterface();
            if (nic == null) return null;
            var gw = nic.GetIPProperties().GatewayAddresses
                .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
            return gw?.Address.ToString();
        }
        catch (Exception ex)
        {
            Safe.LogThrottled(ex, "ThroughputService.GetDefaultGatewayIp");
            return null;
        }
    }

    public void Dispose()
    {
        Stop();
        try
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        }
        catch (Exception ex)
        {
            Safe.Ignore(ex, "ThroughputService.Dispose");
        }
    }
}
