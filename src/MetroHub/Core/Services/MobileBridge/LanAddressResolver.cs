using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MetroHub.Core.Services.MobileBridge;

/// <summary>Resolved LAN endpoint advertised to the phone via the pairing QR code.</summary>
public sealed record LanEndpoint(string IpAddress, string AdapterName);

/// <summary>
/// Picks the single private IPv4 address a phone can reach over Wi-Fi or Ethernet.
/// Deliberately excludes virtual, tunnel, and container adapters (VPN, Hyper-V, WSL, Docker, Tailscale, ...).
/// </summary>
public static class LanAddressResolver
{
    private static readonly string[] VirtualAdapterMarkers =
    {
        "virtual", "hyper-v", "vmware", "virtualbox", "tap-", "vpn", "npcap", "wsl",
        "pseudo", "bluetooth", "loopback", "tailscale", "zerotier", "wireguard",
        "wan miniport", "miniport", "lightweight filter", "native mac layer",
        "kernel debug", "packet scheduler", "multiplexor", "teredo", "isatap",
        "6to4", "tunnel", "pacer", "vethernet", "docker", "container", "loopback adapter"
    };

    /// <summary>
    /// Resolves the best LAN endpoint, or returns false when no usable private address exists
    /// (for example when the machine is offline or only has loopback).
    /// </summary>
    public static bool TryResolve(out LanEndpoint endpoint)
    {
        endpoint = new LanEndpoint(string.Empty, string.Empty);

        IReadOnlyList<LanEndpoint> candidates = ResolveCandidates();
        if (candidates.Count == 0)
        {
            return false;
        }

        endpoint = candidates[0];
        return true;
    }

    /// <summary>
    /// Returns every reachable private IPv4 endpoint, best candidate first.
    /// Used for diagnostics and for letting the user pin a specific adapter.
    /// </summary>
    public static IReadOnlyList<LanEndpoint> ResolveCandidates()
    {
        var results = new List<(LanEndpoint Endpoint, int Rank, int InterfaceIndex)>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (IsVirtualAdapter(nic))
                {
                    continue;
                }

                IPInterfaceProperties properties;
                try
                {
                    properties = nic.GetIPProperties();
                }
                catch
                {
                    continue;
                }

                bool hasGateway = false;
                try
                {
                    hasGateway = properties.GatewayAddresses.Any(
                        g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                             IsPrivateIpv4(g.Address) &&
                             IsUsableGateway(g.Address));
                }
                catch
                {
                    hasGateway = false;
                }

                int interfaceIndex = 0;
                try
                {
                    interfaceIndex = properties.GetIPv4Properties()?.Index ?? 0;
                }
                catch
                {
                    interfaceIndex = 0;
                }

                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    if (!IsPrivateIpv4(unicast.Address))
                    {
                        continue;
                    }

                    int rank = 0;
                    if (hasGateway)
                    {
                        rank += 4;
                    }

                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet)
                    {
                        rank += 2;
                    }
                    else if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                    {
                        rank += 1;
                    }

                    results.Add((new LanEndpoint(unicast.Address.ToString(), nic.Name), rank, interfaceIndex));
                }
            }
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "[LanAddressResolver] Failed to enumerate network interfaces");
        }

        return results
            .OrderByDescending(r => r.Rank)
            .ThenBy(r => r.InterfaceIndex)
            .Select(r => r.Endpoint)
            .ToList();
    }

    /// <summary>True when the address is a routable RFC1918 private IPv4 address.</summary>
    public static bool IsPrivateIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        if (bytes[0] == 10)
        {
            return true; // 10.0.0.0/8
        }

        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
        {
            return true; // 172.16.0.0/12
        }

        if (bytes[0] == 192 && bytes[1] == 168)
        {
            return true; // 192.168.0.0/16
        }

        return false;
    }

    /// <summary>
    /// Defence in depth: rejects anything that is not a loopback or same-LAN private peer,
    /// even if the listener somehow ends up bound to a broader address.
    /// </summary>
    public static bool IsAllowedPeer(IPAddress peer)
    {
        if (peer == null)
        {
            return false;
        }

        if (peer.IsIPv4MappedToIPv6)
        {
            peer = peer.MapToIPv4();
        }

        if (IPAddress.IsLoopback(peer))
        {
            return true;
        }

        if (peer.AddressFamily == AddressFamily.InterNetwork)
        {
            return IsPrivateIpv4(peer);
        }

        if (peer.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fc00::/7 unique-local
            byte[] bytes = peer.GetAddressBytes();
            return bytes.Length == 16 && (bytes[0] & 0xFE) == 0xFC;
        }

        return false;
    }

    private static bool IsUsableGateway(IPAddress gateway)
    {
        byte[] bytes = gateway.GetAddressBytes();
        // A private gateway of 0.0.0.0 or a loopback gateway is not a real default route.
        if (bytes.Length != 4)
        {
            return false;
        }

        return !(bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0);
    }

    private static bool IsVirtualAdapter(NetworkInterface nic)
    {
        string name;
        string description;

        try
        {
            name = nic.Name.ToLowerInvariant();
            description = nic.Description.ToLowerInvariant();
        }
        catch
        {
            return true;
        }

        foreach (string marker in VirtualAdapterMarkers)
        {
            if (description.Contains(marker, StringComparison.Ordinal) ||
                name.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
