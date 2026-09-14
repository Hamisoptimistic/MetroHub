using System;
using System.Collections.Generic;
using MetroHub.Core.Network.Interop;

namespace MetroHub.Core.Network;

public enum NetworkKind
{
    None,
    Ethernet,
    Wifi
}

public enum WifiStandard
{
    Unknown,
    Legacy, // 802.11a/b/g
    Wifi4,  // 802.11n (HT)
    Wifi5,  // 802.11ac (VHT)
    Wifi6,  // 802.11ax (HE)
    Wifi7   // 802.11be (EHT)
}

public class EthernetInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsUsbTethering { get; set; }
    public bool IsConnected { get; set; }
    public string IpAddress { get; set; } = "--";
    public string SubnetMask { get; set; } = "--";
    public string Gateway { get; set; } = "--";
    public List<string> DnsServers { get; set; } = new();
    public string DnsServersString => DnsServers != null && DnsServers.Count > 0 ? string.Join(", ", DnsServers) : "--";
    public long LinkSpeedBitsPerSecond { get; set; }
    public string LinkSpeedString { get; set; } = "--";
    public TimeSpan LinkDuration { get; set; } = TimeSpan.Zero;
    public string DurationString => FormatDuration(LinkDuration);
    public string MacAddress { get; set; } = "--";
    public ulong BytesReceived { get; set; }
    public ulong BytesSent { get; set; }
    public string DataUsageString => FormatDataUsage(BytesReceived, BytesSent);

    public static string FormatDataUsage(ulong rx, ulong tx)
    {
        if (rx == 0 && tx == 0) return "--";
        ulong total = rx + tx;
        return $"{FormatBytes(total)} (↓ {FormatBytes(rx)}  ↑ {FormatBytes(tx)})";
    }

    public static string FormatBytes(ulong bytes)
    {
        if (bytes >= 1024UL * 1024 * 1024 * 1024)
            return $"{(double)bytes / (1024UL * 1024 * 1024 * 1024):0.00} TB";
        if (bytes >= 1024UL * 1024 * 1024)
            return $"{(double)bytes / (1024UL * 1024 * 1024):0.00} GB";
        if (bytes >= 1024UL * 1024)
            return $"{(double)bytes / (1024UL * 1024):0.0} MB";
        if (bytes >= 1024UL)
            return $"{(double)bytes / 1024UL:0} KB";
        return $"{bytes} B";
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return "00:00:00";
        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}d {duration.Hours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";
        }
        return $"{duration.Hours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";
    }
}

public class WifiNetworkItem
{
    public string Ssid { get; set; } = string.Empty;
    public int SignalQuality { get; set; } // 0 - 100
    public int SignalBars => SignalQuality switch
    {
        >= 80 => 4,
        >= 55 => 3,
        >= 30 => 2,
        > 0 => 1,
        _ => 0
    };
    public string SecurityType { get; set; } = "Open";
    public WlanNative.DOT11_AUTH_ALGORITHM AuthAlgorithm { get; set; }
    public WlanNative.DOT11_CIPHER_ALGORITHM CipherAlgorithm { get; set; }
    public bool IsSecured => SecurityType != "Open" && AuthAlgorithm != WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_80211_OPEN;
    public bool IsEnterprise => AuthAlgorithm switch
    {
        WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_RSNA or
        WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_WPA or
        WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_WPA3 or
        WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_WPA3_ENT_192 => true,
        _ => false
    };
    public bool IsConnected { get; set; }
    public bool IsProfileKnown { get; set; }
    public string Bssid { get; set; } = string.Empty;
    public string FrequencyString { get; set; } = string.Empty; // e.g. "5 GHz"
    public int Channel { get; set; }
}

public class WifiConnectionDetails
{
    public string Ssid { get; set; } = string.Empty;
    public string AdapterDescription { get; set; } = string.Empty;
    public bool IsConnected { get; set; }
    public WifiStandard Standard { get; set; } = WifiStandard.Unknown;
    public string StandardString => Standard switch
    {
        WifiStandard.Wifi7 => "Wi-Fi 7 (802.11be)",
        WifiStandard.Wifi6 => "Wi-Fi 6 (802.11ax)",
        WifiStandard.Wifi5 => "Wi-Fi 5 (802.11ac)",
        WifiStandard.Wifi4 => "Wi-Fi 4 (802.11n)",
        WifiStandard.Legacy => "Legacy (802.11a/g)",
        _ => "Wi-Fi"
    };
    public string IpAddress { get; set; } = "--";
    public string SubnetMask { get; set; } = "--";
    public string Gateway { get; set; } = "--";
    public List<string> DnsServers { get; set; } = new();
    public int SignalQuality { get; set; }
    public int SignalBars => SignalQuality switch
    {
        >= 80 => 4,
        >= 55 => 3,
        >= 30 => 2,
        > 0 => 1,
        _ => 0
    };
    public string Band { get; set; } = "--"; // "2.4 GHz", "5 GHz", "6 GHz"
    public int Channel { get; set; }
    public string SecurityType { get; set; } = "--";
    public TimeSpan LinkDuration { get; set; } = TimeSpan.Zero;
    public string DurationString => EthernetInfo.FormatDuration(LinkDuration);
    public long ReceiveLinkSpeedBps { get; set; }
    public long TransmitLinkSpeedBps { get; set; }
    public string LinkSpeedString { get; set; } = "--";
}

public class ThroughputMetrics
{
    public double DownloadBytesPerSec { get; set; }
    public double UploadBytesPerSec { get; set; }
    public double DownloadBitsPerSec => DownloadBytesPerSec * 8.0;
    public double UploadBitsPerSec => UploadBytesPerSec * 8.0;

    public string DownloadSpeedBytesString => FormatBytesSpeed(DownloadBytesPerSec);
    public string UploadSpeedBytesString => FormatBytesSpeed(UploadBytesPerSec);

    public string DownloadSpeedBitsString => FormatBitsSpeed(DownloadBitsPerSec);
    public string UploadSpeedBitsString => FormatBitsSpeed(UploadBitsPerSec);

    public ulong TotalBytesReceived { get; set; }
    public ulong TotalBytesSent { get; set; }

    public static string FormatBytesSpeed(double bytesPerSec)
    {
        if (bytesPerSec <= 0) return "0 KB/s";
        if (bytesPerSec >= 1_000_000_000) return $"{bytesPerSec / 1_000_000_000.0:0.##} GB/s";
        if (bytesPerSec >= 1_000_000) return $"{bytesPerSec / 1_000_000.0:0.#} MB/s";
        if (bytesPerSec >= 1_000) return $"{bytesPerSec / 1_000.0:0.#} KB/s";
        return $"{(int)bytesPerSec} B/s";
    }

    public static string FormatBitsSpeed(double bitsPerSec)
    {
        if (bitsPerSec <= 0) return "0 Kbps";
        if (bitsPerSec >= 1_000_000_000) return $"{bitsPerSec / 1_000_000_000.0:0.##} Gbps";
        if (bitsPerSec >= 1_000_000) return $"{bitsPerSec / 1_000_000.0:0.#} Mbps";
        if (bitsPerSec >= 1_000) return $"{bitsPerSec / 1_000.0:0.#} Kbps";
        return $"{(int)bitsPerSec} bps";
    }
}

public class ThroughputSample
{
    public double DownloadBytesPerSec { get; init; }
    public double UploadBytesPerSec { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public class LatencyMetrics
{
    public long PingMs { get; set; } = -1;
    public string TargetHost { get; set; } = "1.1.1.1";
    public bool IsSuccess => PingMs >= 0;
    public string LatencyString => PingMs >= 0 ? $"{PingMs} ms" : "Timeout";
    public string QualityLabel => PingMs switch
    {
        < 0 => "Offline",
        <= 25 => "Excellent",
        <= 60 => "Good",
        <= 120 => "Fair",
        _ => "Poor"
    };
    public string QualityColorHex => PingMs switch
    {
        < 0 => "#FF4C4C",
        <= 25 => "#28D77B",
        <= 60 => "#3A86FF",
        <= 120 => "#FFB703",
        _ => "#FB8500"
    };
}

public enum ConnectivityLevel
{
    None,
    LocalAccess,
    ConstrainedInternet,
    InternetAccess
}

public class NetworkHealthStatus
{
    public ConnectivityLevel Connectivity { get; set; } = ConnectivityLevel.None;
    public string ConnectivityLabel => Connectivity switch
    {
        ConnectivityLevel.InternetAccess => "Internet Access",
        ConnectivityLevel.ConstrainedInternet => "Captive Portal (Login Required)",
        ConnectivityLevel.LocalAccess => "Local Only (No Internet)",
        _ => "Disconnected"
    };

    public string StatusBadgeColorHex => Connectivity switch
    {
        ConnectivityLevel.InternetAccess => "#28D77B",
        ConnectivityLevel.ConstrainedInternet => "#FFB703",
        ConnectivityLevel.LocalAccess => "#FB8500",
        _ => "#FF4C4C"
    };

    public bool HasInternet => Connectivity == ConnectivityLevel.InternetAccess;
    public bool HasDnsResolution { get; set; }
    public double DnsResolutionTimeMs { get; set; }
    public double PacketLossPercent { get; set; }
    public long LatencyMs { get; set; }
    public string HealthSummary { get; set; } = "Checking connectivity...";
}

