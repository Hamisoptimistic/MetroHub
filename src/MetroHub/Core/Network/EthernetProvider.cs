using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using MetroHub.Core.Network.Interop;

namespace MetroHub.Core.Network;

public class EthernetProvider
{
    private static readonly Lazy<EthernetProvider> _instance = new(() => new EthernetProvider());
    public static EthernetProvider Instance => _instance.Value;

    private readonly Dictionary<string, DateTime> _fallbackConnectTimes = new(StringComparer.OrdinalIgnoreCase);

    public EthernetInfo GetActiveEthernetInfo()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();

            // 1. Find all candidate physical Ethernet interfaces
            var ethernetCandidates = interfaces
                .Where(IsCandidatePhysicalEthernet)
                .ToList();

            // Prioritize: (1) Connected with IPv4 Default Gateway, (2) Active USB Tethering that is Up, (3) Physical Ethernet (even if disconnected/disabled), (4) Dormant USB Tethering
            var activeInterface = ethernetCandidates
                .OrderByDescending(nic => nic.OperationalStatus == OperationalStatus.Up)
                .ThenByDescending(nic => HasIpv4Gateway(nic))
                .ThenByDescending(nic => nic.OperationalStatus == OperationalStatus.Up && IsUsbTetheringInterface(nic))
                .ThenByDescending(nic => !IsUsbTetheringInterface(nic))
                .FirstOrDefault();

            if (activeInterface == null)
            {
                return new EthernetInfo
                {
                    Name = "Ethernet",
                    Description = "No Ethernet adapter detected",
                    IsConnected = false
                };
            }

            return BuildEthernetInfo(activeInterface);
        }
        catch
        {
            return new EthernetInfo
            {
                Name = "Ethernet",
                Description = "No Ethernet adapter detected",
                IsConnected = false
            };
        }
    }

    public IReadOnlyList<EthernetInfo> GetAllEthernetInterfaces()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            return interfaces
                .Where(IsCandidatePhysicalEthernet)
                .Select(BuildEthernetInfo)
                .ToList();
        }
        catch
        {
            return Array.Empty<EthernetInfo>();
        }
    }

    private EthernetInfo BuildEthernetInfo(NetworkInterface nic)
    {
        var info = new EthernetInfo
        {
            Id = nic.Id,
            Name = nic.Name,
            Description = nic.Description,
            IsUsbTethering = IsUsbTetheringInterface(nic),
            IsConnected = false
        };

        try
        {
            var mac = nic.GetPhysicalAddress()?.GetAddressBytes();
            if (mac != null)
            {
                info.MacAddress = FormatMacAddress(mac);
            }
        }
        catch { }

        try
        {
            long speed = 0;
            try { speed = nic.Speed; } catch { }
            info.LinkSpeedBitsPerSecond = speed;
            info.LinkSpeedString = FormatSpeed(speed);

            var ipProps = nic.GetIPProperties();
            var ipv4Unicast = ipProps.UnicastAddresses
                .FirstOrDefault(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork && !IsLinkLocalOrLoopback(ua.Address.ToString()));

            info.IsConnected = nic.OperationalStatus == OperationalStatus.Up && ipv4Unicast != null;

            if (ipv4Unicast != null)
            {
                info.IpAddress = ipv4Unicast.Address.ToString();
                info.SubnetMask = ipv4Unicast.IPv4Mask?.ToString() ?? "--";
            }

            var gateway = ipProps.GatewayAddresses
                .FirstOrDefault(ga => ga.Address.AddressFamily == AddressFamily.InterNetwork);
            if (gateway != null)
            {
                info.Gateway = gateway.Address.ToString();
            }

            foreach (var dns in ipProps.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork))
            {
                info.DnsServers.Add(dns.ToString());
            }

            try
            {
                var stats = nic.GetIPStatistics();
                info.BytesReceived = (ulong)stats.BytesReceived;
                info.BytesSent = (ulong)stats.BytesSent;
            }
            catch { }

            info.LinkDuration = QueryLinkDuration(nic);
        }
        catch { }

        return info;
    }

    private TimeSpan QueryLinkDuration(NetworkInterface nic)
    {
        if (nic.OperationalStatus != OperationalStatus.Up)
        {
            _fallbackConnectTimes.Remove(nic.Id);
            return TimeSpan.Zero;
        }

        try
        {
            var ipProps = nic.GetIPProperties();
            var ipv4Props = ipProps.GetIPv4Properties();
            if (ipv4Props != null)
            {
                var row = new IpHelperInterop.MIB_IF_ROW2
                {
                    InterfaceIndex = (uint)ipv4Props.Index
                };

                int ret = IpHelperInterop.GetIfEntry2(ref row);
                if (ret == IpHelperInterop.NO_ERROR)
                {
                    ulong currentBootMs = IpHelperInterop.GetTickCount64();
                    if (row.LastChange == 0)
                    {
                        // Interface has been connected since system boot
                        return TimeSpan.FromMilliseconds(currentBootMs);
                    }
                    else
                    {
                        // row.LastChange is in 100-nanosecond units (ticks) since system boot
                        ulong currentTicks100Ns = currentBootMs * 10_000UL;
                        if (currentTicks100Ns >= row.LastChange)
                        {
                            ulong elapsed100Ns = currentTicks100Ns - row.LastChange;
                            return TimeSpan.FromTicks((long)elapsed100Ns);
                        }
                    }
                }
            }
        }
        catch { }

        // Fallback: tracked in-memory session time
        if (!_fallbackConnectTimes.TryGetValue(nic.Id, out var connectTime))
        {
            connectTime = DateTime.UtcNow;
            _fallbackConnectTimes[nic.Id] = connectTime;
        }

        var elapsed = DateTime.UtcNow - connectTime;
        return elapsed >= TimeSpan.Zero ? elapsed : TimeSpan.Zero;
    }

    public static bool IsUsbTetheringInterface(NetworkInterface nic)
    {
        string desc = nic.Description.ToLowerInvariant();
        string name = nic.Name.ToLowerInvariant();
        return desc.Contains("remote ndis") ||
               desc.Contains("rndis") ||
               desc.Contains("apple mobile device") ||
               desc.Contains("samsung mobile usb") ||
               desc.Contains("google usb") ||
               desc.Contains("usb ethernet") ||
               desc.Contains("tethering") ||
               name.Contains("tether");
    }

    private static bool IsCandidatePhysicalEthernet(NetworkInterface nic)
    {
        // Always accept USB tethering interfaces from Android / iPhone
        if (IsUsbTetheringInterface(nic))
        {
            return true;
        }

        // Must be Ethernet type
        if (nic.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
            nic.NetworkInterfaceType != NetworkInterfaceType.GigabitEthernet &&
            nic.NetworkInterfaceType != NetworkInterfaceType.FastEthernetFx &&
            nic.NetworkInterfaceType != NetworkInterfaceType.FastEthernetT)
        {
            return false;
        }

        string desc = nic.Description.ToLowerInvariant();
        string name = nic.Name.ToLowerInvariant();

        // Filter out virtual/software adapters and NDIS miniports
        if (desc.Contains("virtual") || desc.Contains("hyper-v") || desc.Contains("vmware") ||
            desc.Contains("virtualbox") || desc.Contains("tap-") || desc.Contains("vpn") ||
            desc.Contains("npcap") || desc.Contains("wsl") || desc.Contains("pseudo") ||
            desc.Contains("bluetooth") || desc.Contains("loopback") || desc.Contains("tailscale") ||
            desc.Contains("zerotier") || desc.Contains("wireguard") || desc.Contains("wan miniport") ||
            desc.Contains("miniport") || desc.Contains("lightweight filter") || desc.Contains("native mac layer") ||
            desc.Contains("kernel debug") || desc.Contains("packet scheduler") ||
            desc.Contains("multiplexor") || desc.Contains("teredo") || desc.Contains("isatap") ||
            desc.Contains("6to4") || desc.Contains("tunnel") || desc.Contains("pacer"))
        {
            return false;
        }

        if (name.Contains("vethernet") || name.Contains("wsl") || name.Contains("loopback") ||
            name.Contains("wan miniport") || name.Contains("miniport") || name.Contains("vpn"))
        {
            return false;
        }

        // Must have a real physical MAC address
        try
        {
            var macBytes = nic.GetPhysicalAddress()?.GetAddressBytes();
            if (macBytes == null || macBytes.Length < 6)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        return true;
    }

    private static bool HasIpv4Gateway(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().GatewayAddresses
                .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLinkLocalOrLoopback(string ip)
    {
        return ip.StartsWith("169.254.") || ip.StartsWith("127.");
    }

    private static string FormatMacAddress(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return "--";
        return string.Join("-", bytes.Select(b => b.ToString("X2")));
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
}
