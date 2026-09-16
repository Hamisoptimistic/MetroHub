using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;

namespace MetroHub.Core.Network;

public enum DataUsageTimeframe
{
    Session,
    Last24Hours,
    Last7Days,
    Last30Days
}

public class DataUsageResult
{
    public ulong BytesReceived { get; set; }
    public ulong BytesSent { get; set; }
    public ulong TotalBytes => BytesReceived + BytesSent;
    // Exactly matches Windows Settings convention (displays in GB with 2 decimals)
    public string FormattedTotal => FormatWindowsSettingsGigabytes(TotalBytes);
    public string FormattedReceived => FormatWindowsSettingsGigabytes(BytesReceived);
    public string FormattedSent => FormatWindowsSettingsGigabytes(BytesSent);
    public string FormattedDetail => $"↓ {FormattedReceived}   ↑ {FormattedSent}";
    public string FormattedFull => TotalBytes == 0 ? "0 MB" : $"{FormattedTotal}  (↓ {FormattedReceived}  ↑ {FormattedSent})";

    public static string FormatWindowsSettingsGigabytes(ulong bytes)
    {
        if (bytes == 0)
        {
            return "0 MB";
        }
        if (bytes >= 1024UL * 1024 * 1024)
        {
            return $"{(double)bytes / (1024UL * 1024 * 1024):0.00} GB";
        }
        if (bytes >= 1024UL * 1024)
        {
            return $"{(double)bytes / (1024UL * 1024):0.0} MB";
        }
        return $"{bytes / 1024UL} KB";
    }
}

public class NetworkDataUsageService
{
    private static readonly Lazy<NetworkDataUsageService> _instance = new(() => new NetworkDataUsageService());
    public static NetworkDataUsageService Instance => _instance.Value;

    private readonly Dictionary<string, (DateTime CachedAt, DataUsageResult Result)> _cache = new();
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromMinutes(1);

    public async Task<DataUsageResult> QueryUsageAsync(NetworkKind kind, DataUsageTimeframe timeframe)
    {
        if (timeframe == DataUsageTimeframe.Session)
        {
            if (kind == NetworkKind.Ethernet)
            {
                var eth = EthernetProvider.Instance.GetActiveEthernetInfo();
                return new DataUsageResult
                {
                    BytesReceived = eth.BytesReceived,
                    BytesSent = eth.BytesSent
                };
            }
            if (kind == NetworkKind.Wifi)
            {
                try
                {
                    var nics = NetworkInterface.GetAllNetworkInterfaces();
                    var wifiNic = nics.FirstOrDefault(nic =>
                        nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
                        nic.OperationalStatus == OperationalStatus.Up &&
                        !IsVirtualInterface(nic));

                    wifiNic ??= nics.FirstOrDefault(nic =>
                        nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
                        nic.OperationalStatus == OperationalStatus.Up);

                    if (wifiNic != null)
                    {
                        var stats = wifiNic.GetIPStatistics();
                        return new DataUsageResult
                        {
                            BytesReceived = (ulong)Math.Max(0L, stats.BytesReceived),
                            BytesSent = (ulong)Math.Max(0L, stats.BytesSent)
                        };
                    }
                }
                catch { }
            }
            return new DataUsageResult();
        }

        string cacheKey = $"{kind}_{timeframe}";
        lock (_cache)
        {
            if (_cache.TryGetValue(cacheKey, out var entry) && DateTime.UtcNow - entry.CachedAt < CacheExpiry)
            {
                return entry.Result;
            }
        }

        var result = await Task.Run(async () =>
        {
            try
            {
                var profiles = FindProfilesForKind(kind);
                if (profiles.Count == 0) return new DataUsageResult();

                var now = DateTimeOffset.Now;
                var startTime = timeframe switch
                {
                    DataUsageTimeframe.Last24Hours => now.AddHours(-24),
                    DataUsageTimeframe.Last7Days => now.AddDays(-7),
                    DataUsageTimeframe.Last30Days => now.AddDays(-30),
                    _ => now.AddDays(-30)
                };

                var states = new NetworkUsageStates
                {
                    Roaming = TriStates.DoNotCare,
                    Shared = TriStates.DoNotCare
                };

                var tasks = profiles.Select(async profile =>
                {
                    try
                    {
                        var usages = await profile.GetNetworkUsageAsync(startTime, now, DataUsageGranularity.Total, states);
                        ulong pRx = 0;
                        ulong pTx = 0;
                        if (usages != null)
                        {
                            foreach (var u in usages)
                            {
                                pRx += u.BytesReceived;
                                pTx += u.BytesSent;
                            }
                        }
                        return (Rx: pRx, Tx: pTx);
                    }
                    catch
                    {
                        return (Rx: 0UL, Tx: 0UL);
                    }
                });

                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                ulong totalRx = 0;
                ulong totalTx = 0;
                foreach (var res in results)
                {
                    totalRx += res.Rx;
                    totalTx += res.Tx;
                }

                return new DataUsageResult
                {
                    BytesReceived = totalRx,
                    BytesSent = totalTx
                };
            }
            catch
            {
                return new DataUsageResult();
            }
        });

        lock (_cache)
        {
            _cache[cacheKey] = (DateTime.UtcNow, result);
        }

        return result;
    }

    private static List<ConnectionProfile> FindProfilesForKind(NetworkKind kind)
    {
        var result = new List<ConnectionProfile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var profiles = NetworkInformation.GetConnectionProfiles();
            if (profiles != null)
            {
                foreach (var p in profiles)
                {
                    if (p != null && MatchesKind(p, kind))
                    {
                        string id = p.ProfileName ?? p.NetworkAdapter?.NetworkAdapterId.ToString() ?? Guid.NewGuid().ToString();
                        if (seen.Add(id))
                        {
                            result.Add(p);
                        }
                    }
                }
            }

            var defaultProfile = NetworkInformation.GetInternetConnectionProfile();
            if (defaultProfile != null && MatchesKind(defaultProfile, kind))
            {
                string id = defaultProfile.ProfileName ?? defaultProfile.NetworkAdapter?.NetworkAdapterId.ToString() ?? "DefaultProfile";
                if (seen.Add(id))
                {
                    result.Add(defaultProfile);
                }
            }
        }
        catch
        {
            // Ignore WinRT discovery errors
        }

        return result;
    }

    private static bool MatchesKind(ConnectionProfile p, NetworkKind kind)
    {
        if (p.NetworkAdapter == null) return false;
        uint type = p.NetworkAdapter.IanaInterfaceType;

        if (kind == NetworkKind.Wifi)
        {
            return p.IsWlanConnectionProfile || type == 71u;
        }

        if (kind == NetworkKind.Ethernet)
        {
            // 6 = ethernetCsmacd, 243 = wwanpp, 244 = wwanpp2 (often USB tethering RNDIS)
            return !p.IsWlanConnectionProfile && (type == 6u || type == 243u || type == 244u);
        }

        return false;
    }

    private static bool IsVirtualInterface(NetworkInterface nic)
    {
        string desc = (nic.Description ?? string.Empty).ToLowerInvariant();
        string name = (nic.Name ?? string.Empty).ToLowerInvariant();
        return desc.Contains("virtual") || desc.Contains("hyper-v") || desc.Contains("vmware") ||
               desc.Contains("virtualbox") || desc.Contains("tap-") || desc.Contains("vpn") ||
               desc.Contains("direct") || desc.Contains("pseudo") ||
               name.Contains("vethernet") || name.Contains("loopback");
    }
}
