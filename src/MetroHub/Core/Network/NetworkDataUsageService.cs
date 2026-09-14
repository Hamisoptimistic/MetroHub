using System;
using System.Collections.Generic;
using System.Linq;
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
    public string FormattedFull => TotalBytes == 0 ? "--" : $"{FormattedTotal}  (↓ {FormattedReceived}  ↑ {FormattedSent})";

    public static string FormatWindowsSettingsGigabytes(ulong bytes)
    {
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
                var profile = FindProfileForKind(kind);
                if (profile == null) return new DataUsageResult();

                var now = DateTimeOffset.Now;
                var startTime = timeframe switch
                {
                    // Windows Settings "Last 24 hours" starts from yesterday's cycle (36-40h bucket)
                    DataUsageTimeframe.Last24Hours => now.AddHours(-38),
                    DataUsageTimeframe.Last7Days => now.AddDays(-7),
                    DataUsageTimeframe.Last30Days => now.AddDays(-30),
                    _ => now.AddDays(-30)
                };

                var states = new NetworkUsageStates
                {
                    Roaming = TriStates.DoNotCare,
                    Shared = TriStates.DoNotCare
                };

                var usages = await profile.GetNetworkUsageAsync(startTime, now, DataUsageGranularity.Total, states);
                ulong rx = 0;
                ulong tx = 0;

                if (usages != null)
                {
                    foreach (var u in usages)
                    {
                        rx += u.BytesReceived;
                        tx += u.BytesSent;
                    }
                }

                return new DataUsageResult
                {
                    BytesReceived = rx,
                    BytesSent = tx
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

    private static ConnectionProfile? FindProfileForKind(NetworkKind kind)
    {
        try
        {
            uint targetIanaType = kind == NetworkKind.Ethernet ? 6u : 71u; // 6 = Ethernet, 71 = 802.11 WiFi

            var profiles = NetworkInformation.GetConnectionProfiles();
            if (profiles != null)
            {
                foreach (var p in profiles)
                {
                    if (p.NetworkAdapter != null && p.NetworkAdapter.IanaInterfaceType == targetIanaType)
                    {
                        return p;
                    }
                }
            }

            return NetworkInformation.GetInternetConnectionProfile();
        }
        catch
        {
            return null;
        }
    }
}
