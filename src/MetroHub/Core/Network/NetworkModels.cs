using System;
using System.Collections.Generic;

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
    public bool IsConnected { get; set; }
    public string IpAddress { get; set; } = "--";
    public string SubnetMask { get; set; } = "--";
    public string Gateway { get; set; } = "--";
    public List<string> DnsServers { get; set; } = new();
    public long LinkSpeedBitsPerSecond { get; set; }
    public string LinkSpeedString { get; set; } = "--";
    public TimeSpan LinkDuration { get; set; } = TimeSpan.Zero;
    public string DurationString => FormatDuration(LinkDuration);
    public string MacAddress { get; set; } = "--";
    public ulong BytesReceived { get; set; }
    public ulong BytesSent { get; set; }

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
