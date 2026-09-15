using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetroHub.Core.Models;
using MetroHub.Core.Network;
using MetroHub.Core.Network.Interop;
using MetroHub.Widgets.Serialization;
using Wpf.Ui.Controls;

namespace MetroHub.Widgets.Catalog.Network;

public partial class WifiNetworkItemViewModel : ObservableObject
{
    public string Ssid { get; init; } = string.Empty;
    public int SignalQuality { get; init; }
    public int SignalBars { get; init; }
    public string SecurityType { get; init; } = "Open";
    public WlanNative.DOT11_AUTH_ALGORITHM AuthAlgorithm { get; init; }
    public WlanNative.DOT11_CIPHER_ALGORITHM CipherAlgorithm { get; init; }
    public bool IsProfileKnown { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotConnected))]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    [NotifyPropertyChangedFor(nameof(StatusBadgeText))]
    [NotifyPropertyChangedFor(nameof(SubtitleText))]
    [NotifyPropertyChangedFor(nameof(SubtitleColor))]
    [NotifyPropertyChangedFor(nameof(WifiIconColor))]
    [NotifyPropertyChangedFor(nameof(IndicatorPillColor))]
    private bool _isConnected;

    public bool IsNotConnected => !IsConnected;

    public bool IsSecured => !string.Equals(SecurityType, "Open", StringComparison.OrdinalIgnoreCase) &&
                             AuthAlgorithm != WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_80211_OPEN;

    public bool IsEnterprise => AuthAlgorithm switch
    {
        WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_RSNA or
        WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_WPA or
        WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_WPA3 or
        WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_WPA3_ENT or
        WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_WPA3_ENT_192 => true,
        _ => false
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    private bool _isConnecting;

    [ObservableProperty]
    private bool _isPasswordPromptOpen;

    [ObservableProperty]
    private string _connectionErrorMessage = string.Empty;

    [ObservableProperty]
    private bool _hasConnectionError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IndicatorPillColor))]
    private bool _isExpanded;

    // Password visibility toggle for the eye button
    [ObservableProperty]
    private bool _isPasswordVisible;

    [ObservableProperty]
    private string _passwordText = string.Empty;

    public WifiStandard Standard { get; init; } = WifiStandard.Unknown;

    public string WifiStandardDisplay => Standard switch
    {
        WifiStandard.Wifi7 => "Wi-Fi 7 (802.11be)",
        WifiStandard.Wifi6 => "Wi-Fi 6 (802.11ax)",
        WifiStandard.Wifi5 => "Wi-Fi 5 (802.11ac)",
        WifiStandard.Wifi4 => "Wi-Fi 4 (802.11n)",
        WifiStandard.Legacy => "Wi-Fi (802.11a/g)",
        _ => "Wi-Fi"
    };

    public string SecurityDetailsDisplay => $"{WifiStandardDisplay}  •  {(IsSecured ? "Secured" : "Open")}";

    public bool CanConnect => !IsConnecting && !IsConnected;

    public string StatusBadgeText => IsConnected ? "Connected" : (IsProfileKnown ? "Saved" : (IsEnterprise ? "Enterprise" : (IsSecured ? "Secured" : "Open")));

    public string SubtitleText
    {
        get
        {
            if (IsConnected) return "Connected";
            if (IsProfileKnown) return $"Saved  •  {SecurityType}";
            if (IsEnterprise) return $"Enterprise  •  {SecurityType}";
            return SecurityType;
        }
    }

    // Uniform Segoe Fluent Icons glyphs — same visual size, fewer arcs for lower signal
    public string WifiSignalGlyph => SignalBars switch
    {
        1 => "\uEC3D",  // Wi-Fi 1 bar
        2 => "\uEC3E",  // Wi-Fi 2 bars
        3 => "\uEC3F",  // Wi-Fi 3 bars
        _ => "\uE701"   // Wi-Fi full (4 bars)
    };

    public string WifiIconColor => "#FFFFFF";
    public string SubtitleColor => IsConnected ? "#A0FFFFFF" : "#80FFFFFF";
    public string StatusBadgeColor => IsConnected ? "#FFFFFF" : (IsProfileKnown ? "#0091FF" : (IsEnterprise ? "#FFB703" : "#B0FFFFFF"));
    public string StatusBadgeBackground => IsConnected ? "#1AFFFFFF" : (IsProfileKnown ? "#1A0091FF" : (IsEnterprise ? "#1AFFB703" : "#12FFFFFF"));
    public string IndicatorPillColor => IsConnected ? "#00E676" : (IsExpanded ? "#FFFFFF" : "#60FFFFFF");
    public string EyeGlyph => IsPasswordVisible ? "\uED1B" : "\uED1A"; // EyeOff / Eye
}

public partial class NetworkWidgetViewModel : WidgetViewModelBase
{
    private readonly EthernetProvider _ethernetProvider = EthernetProvider.Instance;
    private readonly NativeWifiService _wifiService = NativeWifiService.Instance;
    private readonly ThroughputService _throughputService = ThroughputService.Instance;
    private readonly DisconnectService _disconnectService = DisconnectService.Instance;
    private readonly NetworkHealthService _healthService = NetworkHealthService.Instance;
    private readonly NetworkDataUsageService _dataUsageService = NetworkDataUsageService.Instance;

    private bool _isHubVisible = true;
    private bool _hasInitializedPanel;
    private CancellationTokenSource? _toastCts;
    private CancellationTokenSource? _reconnectCts;

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new[]
    {
        WidgetSize.Huge // 8x6 (508x380)
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEthernetPanel))]
    [NotifyPropertyChangedFor(nameof(IsWifiPanel))]
    [NotifyPropertyChangedFor(nameof(IsKillNetPanel))]
    [NotifyPropertyChangedFor(nameof(IsSpeedPanel))]
    [NotifyPropertyChangedFor(nameof(IsHotspotPanel))]
    private string _currentPanel = "Ethernet";

    public bool IsEthernetPanel => string.Equals(CurrentPanel, "Ethernet", StringComparison.OrdinalIgnoreCase);
    public bool IsWifiPanel => string.Equals(CurrentPanel, "Wifi", StringComparison.OrdinalIgnoreCase);
    public bool IsKillNetPanel => string.Equals(CurrentPanel, "KillNet", StringComparison.OrdinalIgnoreCase);
    public bool IsSpeedPanel => string.Equals(CurrentPanel, "Speed", StringComparison.OrdinalIgnoreCase);
    public bool IsHotspotPanel => string.Equals(CurrentPanel, "Hotspot", StringComparison.OrdinalIgnoreCase);

    // --- Dynamic Status Brushes for Modular WidgetTiles ---
    private static readonly Brush GreenIndicatorBrush = CreateFrozenBrush("#00E676");
    private static readonly Brush RedIndicatorBrush = CreateFrozenBrush("#FF3B30");
    private static readonly Brush AmberIndicatorBrush = CreateFrozenBrush("#FFB703");

    private static Brush CreateFrozenBrush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public Brush EthernetStatusBrush
    {
        get
        {
            if (IsEthernetAdapterDisabled || !IsEthernetConnected)
                return RedIndicatorBrush;
            if (IsLocalOnlyNoInternet)
                return AmberIndicatorBrush;
            return GreenIndicatorBrush;
        }
    }

    public Brush WifiStatusBrush
    {
        get
        {
            if (!HasWifiAdapter || !IsWifiConnected)
                return RedIndicatorBrush;
            if (IsLocalOnlyNoInternet)
                return AmberIndicatorBrush;
            return GreenIndicatorBrush;
        }
    }

    public Brush KillNetStatusBrush => IsInternetDisconnected ? RedIndicatorBrush : GreenIndicatorBrush;

    public Brush SpeedStatusBrush
    {
        get
        {
            bool isLinked = IsEthernetConnected || IsWifiConnected;
            if (!isLinked)
                return RedIndicatorBrush;
            if (IsLocalOnlyNoInternet)
                return AmberIndicatorBrush;
            return GreenIndicatorBrush;
        }
    }

    public Brush HotspotStatusBrush => GreenIndicatorBrush;

    [ObservableProperty]
    private bool _isHotspotConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowingSpecs))]
    [NotifyPropertyChangedFor(nameof(DeckTitle))]
    private bool _isShowingNetworks = true;

    public bool IsShowingSpecs => !IsShowingNetworks;

    public string DeckTitle => IsShowingNetworks ? "AVAILABLE NETWORKS" : "ADAPTER SPECIFICATIONS";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedDownloadSpeed))]
    [NotifyPropertyChangedFor(nameof(FormattedUploadSpeed))]
    [NotifyPropertyChangedFor(nameof(SpeedUnitBadge))]
    private bool _isBitsMode = true; // Task Manager style (Mbps) by default

    public string SpeedUnitBadge => IsBitsMode ? "Mbps" : "MB/s";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroGlyph))]
    [NotifyPropertyChangedFor(nameof(HeroGlyphColor))]
    [NotifyPropertyChangedFor(nameof(HeroTitle))]
    [NotifyPropertyChangedFor(nameof(HeroSubtitle))]
    [NotifyPropertyChangedFor(nameof(HeroIsConnected))]
    [NotifyPropertyChangedFor(nameof(HeroActionText))]
    [NotifyPropertyChangedFor(nameof(HeroActionGlyph))]
    [NotifyPropertyChangedFor(nameof(HeroActionBackground))]
    [NotifyPropertyChangedFor(nameof(HeroActionForeground))]
    [NotifyPropertyChangedFor(nameof(HeroActionBorderBrush))]
    [NotifyPropertyChangedFor(nameof(IsLocalOnlyNoInternet))]
    [NotifyPropertyChangedFor(nameof(HasVerifiedInternet))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusText))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusTextColor))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusDotColor))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusBackground))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusBorderBrush))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusBrush))]
    [NotifyPropertyChangedFor(nameof(SpeedStatusBrush))]
    [NotifyPropertyChangedFor(nameof(EthernetActionText))]
    [NotifyPropertyChangedFor(nameof(EthernetActionSubtext))]
    [NotifyPropertyChangedFor(nameof(EthernetPanelTitle))]
    [NotifyPropertyChangedFor(nameof(EthernetPanelIcon))]
    [NotifyPropertyChangedFor(nameof(EthernetPanelTitleColor))]
    [NotifyPropertyChangedFor(nameof(EthernetTileHeader))]
    [NotifyPropertyChangedFor(nameof(EthernetTileIcon))]
    [NotifyPropertyChangedFor(nameof(EthernetTileTooltip))]
    [NotifyPropertyChangedFor(nameof(EthernetPanelName))]
    [NotifyPropertyChangedFor(nameof(EthernetTurnedOffBannerTitle))]
    private bool _isEthernetConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroGlyph))]
    [NotifyPropertyChangedFor(nameof(HeroGlyphColor))]
    [NotifyPropertyChangedFor(nameof(HeroTitle))]
    [NotifyPropertyChangedFor(nameof(HeroSubtitle))]
    [NotifyPropertyChangedFor(nameof(HeroIsConnected))]
    [NotifyPropertyChangedFor(nameof(HeroActionText))]
    [NotifyPropertyChangedFor(nameof(HeroActionGlyph))]
    [NotifyPropertyChangedFor(nameof(HeroActionBackground))]
    [NotifyPropertyChangedFor(nameof(HeroActionForeground))]
    [NotifyPropertyChangedFor(nameof(HeroActionBorderBrush))]
    [NotifyPropertyChangedFor(nameof(IsLocalOnlyNoInternet))]
    [NotifyPropertyChangedFor(nameof(HasVerifiedInternet))]
    [NotifyPropertyChangedFor(nameof(WifiStatusBrush))]
    [NotifyPropertyChangedFor(nameof(SpeedStatusBrush))]
    [NotifyPropertyChangedFor(nameof(WifiActionText))]
    [NotifyPropertyChangedFor(nameof(WifiActionSubtext))]
    [NotifyPropertyChangedFor(nameof(WifiPanelTitle))]
    [NotifyPropertyChangedFor(nameof(WifiPanelSymbol))]
    [NotifyPropertyChangedFor(nameof(WifiPanelIcon))]
    [NotifyPropertyChangedFor(nameof(WifiPanelTitleColor))]
    private bool _isWifiConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroGlyph))]
    [NotifyPropertyChangedFor(nameof(HeroGlyphColor))]
    [NotifyPropertyChangedFor(nameof(HeroTitle))]
    [NotifyPropertyChangedFor(nameof(HeroSubtitle))]
    [NotifyPropertyChangedFor(nameof(HeroIsConnected))]
    [NotifyPropertyChangedFor(nameof(HeroActionText))]
    [NotifyPropertyChangedFor(nameof(HeroActionGlyph))]
    [NotifyPropertyChangedFor(nameof(HeroActionBackground))]
    [NotifyPropertyChangedFor(nameof(HeroActionForeground))]
    [NotifyPropertyChangedFor(nameof(HeroActionBorderBrush))]
    [NotifyPropertyChangedFor(nameof(IsLocalOnlyNoInternet))]
    [NotifyPropertyChangedFor(nameof(HasVerifiedInternet))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusText))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusTextColor))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusDotColor))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusBackground))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusBorderBrush))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusBrush))]
    [NotifyPropertyChangedFor(nameof(WifiStatusBrush))]
    [NotifyPropertyChangedFor(nameof(SpeedStatusBrush))]
    private bool _isInternetDisconnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoWifiAdapter))]
    [NotifyPropertyChangedFor(nameof(WifiActionText))]
    [NotifyPropertyChangedFor(nameof(WifiActionSubtext))]
    [NotifyPropertyChangedFor(nameof(WifiStatusBrush))]
    private bool _hasWifiAdapter;

    public bool HasNoWifiAdapter => !HasWifiAdapter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoEthernetAdapter))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusText))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusTextColor))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusDotColor))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusBackground))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusBorderBrush))]
    [NotifyPropertyChangedFor(nameof(EthernetActionText))]
    [NotifyPropertyChangedFor(nameof(EthernetActionSubtext))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusBrush))]
    [NotifyPropertyChangedFor(nameof(CanToggleEthernet))]
    [NotifyPropertyChangedFor(nameof(IsEthernetEnabled))]
    [NotifyPropertyChangedFor(nameof(IsEthernetDisabled))]
    [NotifyPropertyChangedFor(nameof(EthernetPanelTitle))]
    [NotifyPropertyChangedFor(nameof(EthernetPanelIcon))]
    [NotifyPropertyChangedFor(nameof(EthernetPanelTitleColor))]
    private bool _hasEthernetAdapter;

    public bool HasNoEthernetAdapter => !HasEthernetAdapter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EthernetStatusText))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusTextColor))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusDotColor))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusBackground))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusBorderBrush))]
    [NotifyPropertyChangedFor(nameof(DataUsageTotalDisplay))]
    [NotifyPropertyChangedFor(nameof(DataUsageDetailDisplay))]
    [NotifyPropertyChangedFor(nameof(DataUsageFullDisplay))]
    [NotifyPropertyChangedFor(nameof(EthernetTileHeader))]
    [NotifyPropertyChangedFor(nameof(EthernetTileIcon))]
    [NotifyPropertyChangedFor(nameof(EthernetTileTooltip))]
    [NotifyPropertyChangedFor(nameof(EthernetPanelName))]
    [NotifyPropertyChangedFor(nameof(EthernetTurnedOffBannerTitle))]
    [NotifyPropertyChangedFor(nameof(EthernetPanelIcon))]
    [NotifyPropertyChangedFor(nameof(EthernetActionSubtext))]
    [NotifyPropertyChangedFor(nameof(HeroGlyph))]
    [NotifyPropertyChangedFor(nameof(HeroTitle))]
    [NotifyPropertyChangedFor(nameof(HeroSubtitle))]
    private EthernetInfo _ethernet = new();

    [ObservableProperty]
    private WifiConnectionDetails _wifiConnection = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedDownloadSpeed))]
    [NotifyPropertyChangedFor(nameof(FormattedUploadSpeed))]
    private ThroughputMetrics _throughput = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocalOnlyNoInternet))]
    [NotifyPropertyChangedFor(nameof(HasVerifiedInternet))]
    [NotifyPropertyChangedFor(nameof(HeroGlyphColor))]
    [NotifyPropertyChangedFor(nameof(HeroSubtitle))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusText))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusTextColor))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusDotColor))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusBackground))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusBorderBrush))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusBrush))]
    [NotifyPropertyChangedFor(nameof(WifiStatusBrush))]
    [NotifyPropertyChangedFor(nameof(SpeedStatusBrush))]
    [NotifyPropertyChangedFor(nameof(EthernetPanelTitle))]
    [NotifyPropertyChangedFor(nameof(EthernetPanelIcon))]
    [NotifyPropertyChangedFor(nameof(EthernetPanelTitleColor))]
    private NetworkHealthStatus _health = new();

    [ObservableProperty]
    private IReadOnlyList<ThroughputSample> _sparklineSamples = Array.Empty<ThroughputSample>();

    [ObservableProperty]
    private string _copyIpTooltip = "Copy IPv4";

    [ObservableProperty]
    private string _toastMessage = string.Empty;

    [ObservableProperty]
    private bool _isToastVisible;

    // --- Dynamic Hero Header Properties ---
    public string HeroGlyph
    {
        get
        {
            if (IsInternetDisconnected) return "\uE774"; // Globe prohibited
            if (IsWifiConnected) return "\uE701";       // Wi-Fi signal
            if (IsEthernetConnected) return (Ethernet != null && Ethernet.IsUsbTethering) ? "\uE8EA" : "\uE839";
            return "\uE774";
        }
    }

    public bool IsLocalOnlyNoInternet =>
        (IsEthernetConnected || IsWifiConnected) &&
        !IsInternetDisconnected &&
        Health != null &&
        Health.Connectivity is ConnectivityLevel.LocalAccess or ConnectivityLevel.ConstrainedInternet;

    public bool HasVerifiedInternet =>
        (IsEthernetConnected || IsWifiConnected) &&
        !IsInternetDisconnected &&
        (Health == null || Health.Connectivity == ConnectivityLevel.InternetAccess);

    public string HeroGlyphColor
    {
        get
        {
            if (IsInternetDisconnected) return "#FF4C4C";
            if (IsLocalOnlyNoInternet) return "#FFB703"; // Warning Amber/Yellow
            if (IsWifiConnected || IsEthernetConnected) return "#0091FF";
            return "#70FFFFFF";
        }
    }

    public string HeroTitle
    {
        get
        {
            if (IsInternetDisconnected) return "Internet Disconnected";
            if (IsWifiConnected) return string.IsNullOrWhiteSpace(WifiConnection.Ssid) ? "Wi-Fi" : WifiConnection.Ssid;
            if (IsEthernetConnected)
            {
                if (Ethernet != null && Ethernet.IsUsbTethering) return "USB Tethering";
                return string.IsNullOrWhiteSpace(Ethernet?.Description) ? "Ethernet" : Ethernet.Description;
            }
            return "No Active Connection";
        }
    }

    public string HeroSubtitle
    {
        get
        {
            if (IsInternetDisconnected) return "Hardware interface disabled • Click to Reconnect";
            string internetSuffix = IsLocalOnlyNoInternet ? "No Internet Access" : "Internet Access";
            if (IsWifiConnected)
            {
                string bandInfo = !string.IsNullOrWhiteSpace(WifiConnection.Band) && WifiConnection.Band != "--" ? $" ({WifiConnection.Band})" : "";
                return $"Connected  •  {WifiConnection.StandardString}{bandInfo}  •  {internetSuffix}";
            }
            if (IsEthernetConnected)
            {
                string prefix = (Ethernet != null && Ethernet.IsUsbTethering) ? "USB Connected" : "Connected";
                string speedInfo = !string.IsNullOrWhiteSpace(Ethernet?.LinkSpeedString) && Ethernet.LinkSpeedString != "--" ? $"  •  {Ethernet.LinkSpeedString}" : "";
                return $"{prefix}{speedInfo}  •  {internetSuffix}";
            }
            return "Not connected to any network";
        }
    }

    // --- Status Badge Properties ---
    public string EthernetStatusText
    {
        get
        {
            if (!HasEthernetAdapter) return "No Adapter";
            if (Ethernet == null || !Ethernet.IsConnected) return (Ethernet != null && Ethernet.IsUsbTethering) ? "Device Unplugged" : "Cable Unplugged";
            if (IsLocalOnlyNoInternet) return "No Internet";
            return "Connected";
        }
    }

    public string EthernetStatusTextColor
    {
        get
        {
            if (!HasEthernetAdapter || Ethernet == null || !Ethernet.IsConnected) return "#85FFFFFF";
            if (IsLocalOnlyNoInternet) return "#FFB703";
            return "#FFFFFF";
        }
    }

    public string EthernetStatusDotColor
    {
        get
        {
            if (!HasEthernetAdapter || !Ethernet.IsConnected) return "#75FFFFFF";
            if (IsLocalOnlyNoInternet) return "#FFB703";
            return "#00CC66";
        }
    }

    public string EthernetStatusBackground
    {
        get
        {
            if (!HasEthernetAdapter || !Ethernet.IsConnected) return "#12FFFFFF";
            if (IsLocalOnlyNoInternet) return "#25FFB703";
            return "#2500CC66";
        }
    }

    public string EthernetStatusBorderBrush
    {
        get
        {
            if (!HasEthernetAdapter || !Ethernet.IsConnected) return "#1AFFFFFF";
            if (IsLocalOnlyNoInternet) return "#50FFB703";
            return "#5000CC66";
        }
    }

    public bool HeroIsConnected => (IsWifiConnected || IsEthernetConnected) && !IsInternetDisconnected;

    public string HeroActionText => IsInternetDisconnected ? "Reconnect" : "Disconnect";
    public string HeroActionGlyph => IsInternetDisconnected ? "\uE895" : "\uE774";
    public string HeroActionBackground => IsInternetDisconnected ? "#22FF4C4C" : "#14FFFFFF";
    public string HeroActionForeground => IsInternetDisconnected ? "#FF4C4C" : "#D0FFFFFF";
    public string HeroActionBorderBrush => IsInternetDisconnected ? "#44FF4C4C" : "#24FFFFFF";

    // --- Dynamic Specs Properties ---
    public string ActiveAdapterDescription => IsWifiConnected ? WifiConnection.AdapterDescription : Ethernet.Description;
    public string ActiveIpAddress => IsWifiConnected ? WifiConnection.IpAddress : Ethernet.IpAddress;
    public string ActiveSubnet => IsWifiConnected ? WifiConnection.SubnetMask : Ethernet.SubnetMask;
    public string ActiveGateway => IsWifiConnected ? WifiConnection.Gateway : Ethernet.Gateway;
    public string ActiveDns
    {
        get
        {
            var servers = IsWifiConnected ? WifiConnection.DnsServers : Ethernet.DnsServers;
            return servers.Count > 0 ? string.Join(", ", servers) : "--";
        }
    }
    public string ActiveLinkSpeed => IsWifiConnected ? WifiConnection.LinkSpeedString : Ethernet.LinkSpeedString;
    public string ActiveUptime => IsWifiConnected ? WifiConnection.DurationString : Ethernet.DurationString;
    public string ActiveMacAddress => IsWifiConnected ? "--" : Ethernet.MacAddress;

    // --- Accurate Data Usage Properties (Windows Settings Sync) ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSessionTimeframe))]
    [NotifyPropertyChangedFor(nameof(Is24HoursTimeframe))]
    [NotifyPropertyChangedFor(nameof(Is7DaysTimeframe))]
    [NotifyPropertyChangedFor(nameof(Is30DaysTimeframe))]
    [NotifyPropertyChangedFor(nameof(DataUsageTotalDisplay))]
    [NotifyPropertyChangedFor(nameof(DataUsageDetailDisplay))]
    [NotifyPropertyChangedFor(nameof(DataUsageFullDisplay))]
    private DataUsageTimeframe _selectedDataUsageTimeframe = DataUsageTimeframe.Session;

    partial void OnSelectedDataUsageTimeframeChanged(DataUsageTimeframe value)
    {
        if (value != DataUsageTimeframe.Session)
        {
            Task.Run(async () =>
            {
                var usage = await _dataUsageService.QueryUsageAsync(NetworkKind.Ethernet, value);
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    HistoricalDataUsage = usage;
                }, System.Windows.Threading.DispatcherPriority.Background);
            });
        }
    }

    public bool IsSessionTimeframe => SelectedDataUsageTimeframe == DataUsageTimeframe.Session;
    public bool Is24HoursTimeframe => SelectedDataUsageTimeframe == DataUsageTimeframe.Last24Hours;
    public bool Is7DaysTimeframe => SelectedDataUsageTimeframe == DataUsageTimeframe.Last7Days;
    public bool Is30DaysTimeframe => SelectedDataUsageTimeframe == DataUsageTimeframe.Last30Days;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DataUsageTotalDisplay))]
    [NotifyPropertyChangedFor(nameof(DataUsageDetailDisplay))]
    [NotifyPropertyChangedFor(nameof(DataUsageFullDisplay))]
    private DataUsageResult _historicalDataUsage = new();

    public string DataUsageTotalDisplay
    {
        get
        {
            if (SelectedDataUsageTimeframe == DataUsageTimeframe.Session)
            {
                return Ethernet != null && (Ethernet.BytesReceived > 0 || Ethernet.BytesSent > 0)
                    ? DataUsageResult.FormatWindowsSettingsGigabytes(Ethernet.BytesReceived + Ethernet.BytesSent)
                    : "--";
            }
            return HistoricalDataUsage.TotalBytes > 0 ? HistoricalDataUsage.FormattedTotal : "--";
        }
    }

    public string DataUsageDetailDisplay
    {
        get
        {
            if (SelectedDataUsageTimeframe == DataUsageTimeframe.Session)
            {
                return Ethernet != null && (Ethernet.BytesReceived > 0 || Ethernet.BytesSent > 0)
                    ? $"↓ {DataUsageResult.FormatWindowsSettingsGigabytes(Ethernet.BytesReceived)}   ↑ {DataUsageResult.FormatWindowsSettingsGigabytes(Ethernet.BytesSent)}"
                    : "--";
            }
            return HistoricalDataUsage.TotalBytes > 0 ? HistoricalDataUsage.FormattedDetail : "--";
        }
    }

    public string DataUsageFullDisplay
    {
        get
        {
            if (SelectedDataUsageTimeframe == DataUsageTimeframe.Session)
            {
                return Ethernet?.DataUsageString ?? "--";
            }
            return HistoricalDataUsage.FormattedFull;
        }
    }

    [RelayCommand]
    public void SelectDataUsageTimeframe(string timeframeStr)
    {
        var timeframe = timeframeStr?.ToLowerInvariant() switch
        {
            "24h" or "last24hours" => DataUsageTimeframe.Last24Hours,
            "7d" or "last7days" => DataUsageTimeframe.Last7Days,
            "30d" or "last30days" => DataUsageTimeframe.Last30Days,
            _ => DataUsageTimeframe.Session
        };

        SelectedDataUsageTimeframe = timeframe;
    }

    // --- Formatted Speeds ---
    public string FormattedDownloadSpeed => IsBitsMode
        ? Throughput.DownloadSpeedBitsString
        : Throughput.DownloadSpeedBytesString;

    public string FormattedUploadSpeed => IsBitsMode
        ? Throughput.UploadSpeedBitsString
        : Throughput.UploadSpeedBytesString;

    public ObservableCollection<WifiNetworkItemViewModel> AvailableNetworks { get; } = new();
    public bool HasAvailableNetworks => AvailableNetworks.Count > 0;
    public bool HasNoAvailableNetworks => HasWifiAdapter && IsWifiRadioOn && AvailableNetworks.Count == 0;

    public NetworkWidgetViewModel(TileModel model) : base(model)
    {
        AvailableNetworks.CollectionChanged += (s, e) =>
        {
            OnPropertyChanged(nameof(HasAvailableNetworks));
            OnPropertyChanged(nameof(HasNoAvailableNetworks));
        };

        _throughputService.ThroughputUpdated += OnThroughputUpdated;
        _throughputService.LatencyUpdated += OnLatencyUpdated;
        _disconnectService.DisconnectStateChanged += OnDisconnectStateChanged;
        _disconnectService.EthernetDisabledStateChanged += OnEthernetDisabledStateChanged;
        _healthService.HealthChanged += OnHealthServiceChanged;

        try
        {
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        }
        catch { }

        RefreshAll();
        _throughputService.Start();
    }

    public override void Initialize(TileModel model)
    {
        base.Initialize(model);
        ApplyConnectionDefaultPanel();
    }

    public void ApplyConnectionDefaultPanel()
    {
        if (IsEthernetConnected)
        {
            CurrentPanel = "Ethernet";
        }
        else if (IsWifiConnected)
        {
            CurrentPanel = "Wifi";
        }
        else if (HasEthernetAdapter)
        {
            CurrentPanel = "Ethernet";
        }
        else if (HasWifiAdapter)
        {
            CurrentPanel = "Wifi";
        }
        else
        {
            CurrentPanel = "Ethernet";
        }
    }

    protected override void LoadSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return;
        try
        {
            var settings = WidgetSerializer.Deserialize<NetworkWidgetSettings>(settingsJson);
            if (settings != null)
            {
                IsBitsMode = settings.UseBitsPerSecond;
            }
        }
        catch { }
    }

    public override void SaveSettings()
    {
        var settings = new NetworkWidgetSettings
        {
            DefaultTab = CurrentPanel,
            UseBitsPerSecond = IsBitsMode
        };
        Model.TargetPath = "network";
        Model.SettingsJson = WidgetSerializer.Serialize(settings);
    }

    [RelayCommand]
    public void SelectPanel(string panel)
    {
        CurrentPanel = panel;
        SaveSettings();

        if (IsWifiPanel && HasWifiAdapter)
        {
            RefreshWifiNetworks();
        }
        else if (IsSpeedPanel)
        {
            Task.Run(() => _healthService.EvaluateHealthAsync());
        }
    }

    [RelayCommand]
    public void SwitchDeck(string deck)
    {
        IsShowingNetworks = string.Equals(deck, "Networks", StringComparison.OrdinalIgnoreCase);
        SaveSettings();

        if (IsShowingNetworks && HasWifiAdapter)
        {
            RefreshWifiNetworks();
        }
    }

    [RelayCommand]
    public void ToggleSpeedUnit()
    {
        IsBitsMode = !IsBitsMode;
        SaveSettings();
    }

    [RelayCommand]
    public async Task ToggleDisconnect()
    {
        if (!IsInternetDisconnected)
        {
            // Immediate visual feedback
            IsInternetDisconnected = true;
            ShowToast("Requesting Windows authorization to suspend adapter...");

            bool success = await _disconnectService.DisconnectInternetAsync();
            if (success)
            {
                IsInternetDisconnected = true;
                ShowToast("Network adapter suspended via administrative policy.");
                RefreshAll();
            }
            else
            {
                IsInternetDisconnected = false;
                ShowToast("Action canceled: Administrative authorization was declined.");
                RefreshAll();
            }
        }
        else
        {
            ShowToast("Requesting authorization to restore network adapter...");

            bool success = await _disconnectService.ReconnectInternetAsync();
            if (success)
            {
                IsInternetDisconnected = false;
                ShowToast("Network adapter re-enabled. Connectivity restored.");
                RefreshAll();
            }
            else
            {
                IsInternetDisconnected = true;
                ShowToast("Action canceled: Administrative authorization was declined.");
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EthernetActionText))]
    [NotifyPropertyChangedFor(nameof(EthernetActionSubtext))]
    [NotifyPropertyChangedFor(nameof(EthernetIndicatorDotBrush))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusBrush))]
    [NotifyPropertyChangedFor(nameof(CanToggleEthernet))]
    [NotifyPropertyChangedFor(nameof(IsEthernetEnabled))]
    [NotifyPropertyChangedFor(nameof(IsEthernetDisabled))]
    [NotifyPropertyChangedFor(nameof(EthernetPanelTitle))]
    [NotifyPropertyChangedFor(nameof(EthernetPanelIcon))]
    [NotifyPropertyChangedFor(nameof(EthernetPanelTitleColor))]
    [NotifyPropertyChangedFor(nameof(EthernetTileHeader))]
    [NotifyPropertyChangedFor(nameof(EthernetTileIcon))]
    [NotifyPropertyChangedFor(nameof(EthernetTileTooltip))]
    [NotifyPropertyChangedFor(nameof(EthernetPanelName))]
    [NotifyPropertyChangedFor(nameof(EthernetTurnedOffBannerTitle))]
    private bool _isEthernetAdapterDisabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleEthernet))]
    private bool _isEthernetBusy;

    public bool CanToggleEthernet => (HasEthernetAdapter || IsEthernetAdapterDisabled) && !IsEthernetBusy;

    public bool IsEthernetEnabled
    {
        get => HasEthernetAdapter && !IsEthernetAdapterDisabled;
        set
        {
            if (value == (HasEthernetAdapter && !IsEthernetAdapterDisabled)) return;
            _ = HandleEthernetToggleAsync(value);
        }
    }

    public bool IsEthernetDisabled => !IsEthernetEnabled;

    public bool IsEthernetNoInternet =>
        IsEthernetConnected &&
        Health != null &&
        Health.Connectivity is ConnectivityLevel.LocalAccess or ConnectivityLevel.ConstrainedInternet;

    // --- Dynamic USB Tethering & Ethernet Properties for Tile and Panel ---
    public string EthernetTileHeader => (Ethernet != null && Ethernet.IsUsbTethering) ? "USB Tether" : "Ethernet";

    public string EthernetTileIcon => (Ethernet != null && Ethernet.IsUsbTethering) ? "\uE8EA" : "\uE839";

    public string EthernetTileTooltip => (Ethernet != null && Ethernet.IsUsbTethering)
        ? "USB Tethering Settings & Telemetry"
        : "Ethernet Settings & Telemetry";

    public string EthernetPanelName => (Ethernet != null && Ethernet.IsUsbTethering) ? "USB Tethering" : "Ethernet";

    public string EthernetTurnedOffBannerTitle => $"{EthernetPanelName} is turned off";

    public string EthernetPanelIcon
    {
        get
        {
            if (!HasEthernetAdapter || IsEthernetDisabled || !IsEthernetConnected)
                return "\uEB55"; // Disconnected
            if (IsEthernetNoInternet)
                return "\uE774"; // Globe No Internet
            return (Ethernet != null && Ethernet.IsUsbTethering) ? "\uE8EA" : "\uE839"; // Connected
        }
    }

    public string EthernetPanelTitle
    {
        get
        {
            if (!HasEthernetAdapter || IsEthernetDisabled || !IsEthernetConnected)
                return "Disconnected";
            if (IsEthernetNoInternet)
                return "No Internet";
            return "Connected";
        }
    }

    public string EthernetPanelTitleColor
    {
        get
        {
            if (IsEthernetNoInternet)
                return "#FFB703"; // Warning Amber
            if (!HasEthernetAdapter || IsEthernetDisabled || !IsEthernetConnected)
                return "#85FFFFFF";
            return "#FFFFFF";
        }
    }

    public string EthernetActionText => IsEthernetAdapterDisabled ? "Enable" : "Disable";

    public string EthernetActionSubtext
    {
        get
        {
            string name = (Ethernet != null && Ethernet.IsUsbTethering) ? "USB Tethering" : "Ethernet";
            if (!HasEthernetAdapter)
                return $"No {name} adapter detected on this PC";
            if (IsEthernetAdapterDisabled)
                return $"{name} adapter is disabled • Click Enable to restore";
            if (IsEthernetConnected)
                return $"Disable / disconnect {name} (Requires Admin privilege)";
            return (Ethernet != null && Ethernet.IsUsbTethering)
                ? "USB device disconnected • Disabling requires Admin privilege"
                : "Cable unplugged • Disabling requires Admin privilege";
        }
    }

    public Brush EthernetIndicatorDotBrush =>
        (!HasEthernetAdapter || IsEthernetAdapterDisabled || !IsEthernetConnected)
            ? RedIndicatorBrush
            : GreenIndicatorBrush;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleWifiRadio))]
    [NotifyPropertyChangedFor(nameof(IsWifiRadioDisabled))]
    [NotifyPropertyChangedFor(nameof(IsWifiRadioEnabled))]
    [NotifyPropertyChangedFor(nameof(WifiPanelTitle))]
    [NotifyPropertyChangedFor(nameof(WifiPanelSymbol))]
    [NotifyPropertyChangedFor(nameof(WifiPanelIcon))]
    [NotifyPropertyChangedFor(nameof(WifiPanelTitleColor))]
    [NotifyPropertyChangedFor(nameof(WifiTurnedOffBannerTitle))]
    [NotifyPropertyChangedFor(nameof(WifiActionText))]
    [NotifyPropertyChangedFor(nameof(WifiActionSubtext))]
    [NotifyPropertyChangedFor(nameof(WifiStatusBrush))]
    private bool _isWifiRadioOn = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleWifiRadio))]
    private bool _isWifiRadioBusy;

    public bool CanToggleWifiRadio => HasWifiAdapter && !IsWifiRadioBusy;
    public bool IsWifiRadioDisabled => !IsWifiRadioOn;
    public string WifiTurnedOffBannerTitle => "Wi-Fi is turned off";

    public bool IsWifiRadioEnabled
    {
        get => HasWifiAdapter && IsWifiRadioOn;
        set
        {
            if (value == (HasWifiAdapter && IsWifiRadioOn)) return;
            _ = HandleWifiRadioToggleAsync(value);
        }
    }

    public string WifiActionText => IsWifiConnected ? "Disconnect" : "Connect";

    public string WifiActionSubtext
    {
        get
        {
            if (!HasWifiAdapter)
                return "No Wi-Fi adapter detected on this PC";
            if (IsWifiRadioDisabled)
                return "Wi-Fi is turned off • Toggle to turn on";
            if (IsWifiConnected)
                return $"Connected to {WifiConnection.Ssid}";
            return "Not connected to any Wi-Fi network";
        }
    }

    public Brush WifiIndicatorDotBrush =>
        (!HasWifiAdapter || IsWifiRadioDisabled || !IsWifiConnected)
            ? RedIndicatorBrush
            : GreenIndicatorBrush;

    public bool IsWifiNoInternet =>
        IsWifiConnected &&
        Health != null &&
        Health.Connectivity is ConnectivityLevel.LocalAccess or ConnectivityLevel.ConstrainedInternet;

    public SymbolRegular WifiPanelSymbol
    {
        get
        {
            if (!HasWifiAdapter || IsWifiRadioDisabled)
                return SymbolRegular.WifiOff24;
            if (!IsWifiConnected)
                return SymbolRegular.WifiOff24;
            if (IsWifiNoInternet)
                return SymbolRegular.Globe24;
            return SymbolRegular.Wifi124;
        }
    }

    public string WifiPanelIcon
    {
        get
        {
            if (!HasWifiAdapter || IsWifiRadioDisabled || !IsWifiConnected)
                return "\uEB55"; // Disconnected
            if (IsWifiNoInternet)
                return "\uE774"; // Globe No Internet
            return "\uE701";    // Wi-Fi signal
        }
    }

    public string WifiPanelTitle
    {
        get
        {
            if (!HasWifiAdapter)
                return "No Adapter";
            if (IsWifiRadioDisabled)
                return "Turned Off";
            if (!IsWifiConnected)
                return "Disconnected";
            if (IsWifiNoInternet)
                return "No Internet";
            return "Connected";
        }
    }

    public string WifiPanelTitleColor
    {
        get
        {
            if (IsWifiNoInternet)
                return "#FFB703"; // Warning Amber
            if (!HasWifiAdapter || IsWifiRadioDisabled || !IsWifiConnected)
                return "#85FFFFFF";
            return "#FFFFFF";
        }
    }

    [RelayCommand]
    public async Task ToggleEthernet() => await HandleEthernetToggleAsync(!IsEthernetEnabled);

    private async Task HandleEthernetToggleAsync(bool targetEnabled)
    {
        if (!HasEthernetAdapter && !IsEthernetAdapterDisabled)
        {
            ShowToast("No Ethernet adapter detected.");
            return;
        }

        if (IsEthernetBusy) return;
        IsEthernetBusy = true;

        try
        {
            using var _ = MainWindow.EnterDialogScope();

            string name = (Ethernet != null && Ethernet.IsUsbTethering) ? "USB Tethering" : "Ethernet";
            string actionName = targetEnabled ? "restore" : "suspend";
            ShowToast($"Requesting Windows authorization to {actionName} {name} adapter...");

            string targetAdapter = !string.IsNullOrWhiteSpace(Ethernet?.Name) && Ethernet.Name != "Ethernet"
                ? Ethernet.Name
                : (_disconnectService.LastDisabledAdapterName ?? "Ethernet");

            bool success = await _disconnectService.ToggleEthernetAdapterAsync(targetAdapter);
            if (success)
            {
                IsEthernetAdapterDisabled = _disconnectService.IsEthernetDisabled;
                if (IsEthernetAdapterDisabled)
                {
                    IsEthernetConnected = false;
                }
                OnPropertyChanged(nameof(IsEthernetEnabled));
                OnPropertyChanged(nameof(IsEthernetDisabled));
                OnPropertyChanged(nameof(EthernetStatusBrush));
                OnPropertyChanged(nameof(EthernetActionText));
                OnPropertyChanged(nameof(EthernetActionSubtext));
                OnPropertyChanged(nameof(EthernetPanelTitle));
                OnPropertyChanged(nameof(EthernetPanelIcon));
                OnPropertyChanged(nameof(EthernetPanelTitleColor));
                OnPropertyChanged(nameof(EthernetTileHeader));
                OnPropertyChanged(nameof(EthernetTileIcon));
                OnPropertyChanged(nameof(EthernetTileTooltip));
                OnPropertyChanged(nameof(EthernetPanelName));
                OnPropertyChanged(nameof(EthernetTurnedOffBannerTitle));

                ShowToast(IsEthernetAdapterDisabled 
                    ? $"{name} adapter disabled." 
                    : $"{name} adapter enabled.");

                await Task.Delay(100);
                RefreshAll();

                if (!IsEthernetAdapterDisabled)
                {
                    StartReconnectionMonitoring(10, 500);
                }
            }
            else
            {
                // Authorization declined - switch stays in its authentic original state
                OnPropertyChanged(nameof(IsEthernetEnabled));
                OnPropertyChanged(nameof(IsEthernetDisabled));
                ShowToast("Action canceled: Administrative authorization was declined.");
            }
        }
        finally
        {
            IsEthernetBusy = false;
        }
    }

    [RelayCommand]
    public async Task ToggleWifi()
    {
        if (!HasWifiAdapter)
        {
            ShowToast("No Wi-Fi adapter detected.");
            return;
        }

        if (IsWifiConnected)
        {
            IsWifiConnected = false;
            ShowToast("Disconnecting from Wi-Fi...");
            bool success = await _disconnectService.ToggleWifiConnectionAsync();
            if (success)
            {
                ShowToast("Wi-Fi disconnected.");
                RefreshAll();
                StartReconnectionMonitoring(5, 500);
            }
            else
            {
                IsWifiConnected = true;
                ShowToast("Failed to disconnect Wi-Fi.");
            }
        }
        else
        {
            ShowToast("Attempting Wi-Fi connection...");
            bool success = await _disconnectService.ToggleWifiConnectionAsync();
            if (success)
            {
                ShowToast("Wi-Fi connection requested.");
                RefreshAll();
                StartReconnectionMonitoring(10, 500);
            }
            else
            {
                ShowToast("Failed to reconnect to Wi-Fi.");
            }
        }
    }

    [RelayCommand]
    public async Task ToggleWifiRadio() => await HandleWifiRadioToggleAsync(!IsWifiRadioOn);

    public async Task HandleWifiRadioToggleAsync(bool targetEnabled)
    {
        if (!HasWifiAdapter)
        {
            ShowToast("No Wi-Fi adapter detected.");
            return;
        }

        if (IsWifiRadioBusy) return;
        IsWifiRadioBusy = true;

        // 1. Instant optimistic visual update (0ms UI latency!)
        IsWifiRadioOn = targetEnabled;
        if (!targetEnabled)
        {
            IsWifiConnected = false;
            AvailableNetworks.Clear();
        }

        OnPropertyChanged(nameof(IsWifiRadioOn));
        OnPropertyChanged(nameof(IsWifiRadioEnabled));
        OnPropertyChanged(nameof(IsWifiRadioDisabled));
        OnPropertyChanged(nameof(WifiPanelTitle));
        OnPropertyChanged(nameof(WifiPanelSymbol));
        OnPropertyChanged(nameof(WifiPanelIcon));
        OnPropertyChanged(nameof(WifiPanelTitleColor));
        OnPropertyChanged(nameof(WifiTurnedOffBannerTitle));
        OnPropertyChanged(nameof(HasNoAvailableNetworks));
        OnPropertyChanged(nameof(HasAvailableNetworks));

        ShowToast(targetEnabled ? "Turning on Wi-Fi..." : "Turning off Wi-Fi...");

        try
        {
            bool success = await Task.Run(() => _wifiService.SetRadioState(targetEnabled));

            if (success)
            {
                ShowToast(targetEnabled ? "Wi-Fi turned on." : "Wi-Fi turned off.");

                if (targetEnabled)
                {
                    // Delay slightly to let the driver initialize before querying BSS cache
                    await Task.Delay(200);
                    RefreshWifiNetworks(triggerScan: false);
                }
            }
            else
            {
                // Revert state on failure
                IsWifiRadioOn = _wifiService.IsRadioOn;
                OnPropertyChanged(nameof(IsWifiRadioOn));
                OnPropertyChanged(nameof(IsWifiRadioEnabled));
                OnPropertyChanged(nameof(IsWifiRadioDisabled));
                OnPropertyChanged(nameof(WifiPanelTitle));
                OnPropertyChanged(nameof(WifiPanelSymbol));
                OnPropertyChanged(nameof(WifiPanelTitleColor));
                ShowToast("Failed to update Wi-Fi radio state.");
            }
        }
        catch (Exception ex)
        {
            IsWifiRadioOn = _wifiService.IsRadioOn;
            OnPropertyChanged(nameof(IsWifiRadioOn));
            ShowToast($"Wi-Fi error: {ex.Message}");
        }
        finally
        {
            IsWifiRadioBusy = false;
        }
    }

    [RelayCommand]
    public async Task ConnectWifiNetwork(WifiNetworkItemViewModel? item)
    {
        if (item == null) return;

        if (item.IsConnected)
        {
            await DisconnectWifiNetwork(item);
            return;
        }

        if (item.IsEnterprise)
        {
            ShowToast("802.1X Enterprise networks require domain credentials.");
            return;
        }

        if (item.IsProfileKnown || !item.IsSecured)
        {
            await ExecuteWifiConnectAsync(item, null);
            return;
        }

        // Toggle inline password prompt for secured unknown network
        foreach (var net in AvailableNetworks)
        {
            if (!ReferenceEquals(net, item))
            {
                net.IsPasswordPromptOpen = false;
                net.HasConnectionError = false;
                net.ConnectionErrorMessage = string.Empty;
            }
        }

        item.IsPasswordPromptOpen = !item.IsPasswordPromptOpen;
        item.HasConnectionError = false;
        item.ConnectionErrorMessage = string.Empty;
    }

    // Track accordion state at class level so background refreshes don't clobber it
    private string? _activeExpandedSsid;
    private string? _activePasswordPromptSsid;

    [RelayCommand]
    public void ToggleNetworkExpand(WifiNetworkItemViewModel? item)
    {
        if (item == null) return;

        bool willExpand = !item.IsExpanded;

        foreach (var net in AvailableNetworks)
        {
            if (!ReferenceEquals(net, item))
            {
                net.IsExpanded = false;
                net.IsPasswordPromptOpen = false;
                net.IsPasswordVisible = false;
                net.PasswordText = string.Empty;
                net.HasConnectionError = false;
                net.ConnectionErrorMessage = string.Empty;
            }
        }

        item.IsExpanded = willExpand;

        if (willExpand)
        {
            // Only open password prompt when: unsaved AND secured AND not already connected
            if (!item.IsProfileKnown && item.IsSecured && !item.IsConnected)
            {
                item.IsPasswordPromptOpen = true;
            }
            item.HasConnectionError = false;
            item.ConnectionErrorMessage = string.Empty;
            _activeExpandedSsid = item.Ssid;
            _activePasswordPromptSsid = item.IsPasswordPromptOpen ? item.Ssid : null;
        }
        else
        {
            item.IsPasswordVisible = false;
            item.PasswordText = string.Empty;
            _activeExpandedSsid = null;
            _activePasswordPromptSsid = null;
        }
    }

    [RelayCommand]
    public void ForgetWifiNetwork(WifiNetworkItemViewModel? item)
    {
        if (item == null) return;

        bool res = _wifiService.ForgetProfile(item.Ssid);
        if (res)
        {
            ShowToast($"Forgot {item.Ssid}.");
            RefreshWifiNetworks(triggerScan: false);
        }
        else
        {
            ShowToast($"Could not forget {item.Ssid}.");
        }
    }

    [RelayCommand]
    public async Task ConnectWithPassword(object? parameter)
    {
        WifiNetworkItemViewModel? targetItem = null;
        string password = string.Empty;

        if (parameter is Wpf.Ui.Controls.PasswordBox uiPb)
        {
            targetItem = uiPb.DataContext as WifiNetworkItemViewModel;
            password = uiPb.Password;
        }
        else if (parameter is System.Windows.Controls.PasswordBox pb)
        {
            targetItem = pb.DataContext as WifiNetworkItemViewModel;
            password = pb.Password;
        }
        else if (parameter is WifiNetworkItemViewModel item)
        {
            targetItem = item;
            password = item.PasswordText;
        }

        if (targetItem == null) return;

        if (string.IsNullOrEmpty(password))
        {
            targetItem.HasConnectionError = true;
            targetItem.ConnectionErrorMessage = "Password cannot be empty.";
            return;
        }

        // Convert plain text to SecureString for the WLAN API
        var secure = new System.Security.SecureString();
        foreach (char c in password) secure.AppendChar(c);
        secure.MakeReadOnly();

        // Memory hardening: Immediately clear password from UI and ViewModel
        if (parameter is Wpf.Ui.Controls.PasswordBox clearUiPb) clearUiPb.Clear();
        else if (parameter is System.Windows.Controls.PasswordBox clearPb) clearPb.Clear();
        targetItem.PasswordText = string.Empty;

        await ExecuteWifiConnectAsync(targetItem, secure);

        if (!targetItem.HasConnectionError && targetItem.IsConnected)
        {
            targetItem.IsPasswordPromptOpen = false;
            targetItem.IsPasswordVisible = false;
        }
    }

    [RelayCommand]
    public void TogglePasswordVisibility(WifiNetworkItemViewModel? item)
    {
        if (item == null) return;
        item.IsPasswordVisible = !item.IsPasswordVisible;
    }

    [RelayCommand]
    public void CancelWifiPassword(object? parameter)
    {
        WifiNetworkItemViewModel? targetItem = null;
        if (parameter is Wpf.Ui.Controls.PasswordBox uiPb)
        {
            targetItem = uiPb.DataContext as WifiNetworkItemViewModel;
            uiPb.Clear();
        }
        else if (parameter is System.Windows.Controls.PasswordBox pb)
        {
            targetItem = pb.DataContext as WifiNetworkItemViewModel;
            pb.Clear();
        }
        else if (parameter is WifiNetworkItemViewModel item)
        {
            targetItem = item;
        }

        if (targetItem == null) return;
        targetItem.PasswordText = string.Empty;
        targetItem.IsPasswordVisible = false;
        targetItem.IsPasswordPromptOpen = false;
        targetItem.IsExpanded = false;
        targetItem.HasConnectionError = false;
        targetItem.ConnectionErrorMessage = string.Empty;
        _activeExpandedSsid = null;
        _activePasswordPromptSsid = null;
    }

    [RelayCommand]
    public async Task DisconnectWifiNetwork(WifiNetworkItemViewModel? item)
    {
        ShowToast("Disconnecting from Wi-Fi...");
        bool res = await Task.Run(() => _wifiService.Disconnect());
        if (res)
        {
            ShowToast("Disconnected from Wi-Fi.");
            if (item != null) item.IsConnected = false;
            IsWifiConnected = false;
            RefreshAll();
        }
        else
        {
            ShowToast("Failed to disconnect Wi-Fi.");
        }
    }

    private async Task ExecuteWifiConnectAsync(WifiNetworkItemViewModel item, System.Security.SecureString? securePassword)
    {
        item.IsConnecting = true;
        item.HasConnectionError = false;
        item.ConnectionErrorMessage = string.Empty;
        ShowToast($"Connecting to {item.Ssid}...");

        try
        {
            var (success, message) = await _wifiService.ConnectAsync(
                item.Ssid,
                securePassword,
                item.AuthAlgorithm,
                item.CipherAlgorithm,
                item.IsProfileKnown);

            if (success)
            {
                item.IsConnected = true;
                item.IsPasswordPromptOpen = false;
                ShowToast($"Connected to {item.Ssid}.");
                RefreshAll();
            }
            else
            {
                item.HasConnectionError = true;
                item.ConnectionErrorMessage = message;
                ShowToast($"Connection failed: {message}");
            }
        }
        catch (Exception ex)
        {
            item.HasConnectionError = true;
            item.ConnectionErrorMessage = ex.Message;
            ShowToast($"Connection error: {ex.Message}");
        }
        finally
        {
            item.IsConnecting = false;
        }
    }

    private void StartReconnectionMonitoring(int maxAttempts = 10, int intervalMs = 500)
    {
        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _reconnectCts = new CancellationTokenSource();
        var token = _reconnectCts.Token;

        Task.Run(async () =>
        {
            for (int i = 0; i < maxAttempts; i++)
            {
                await Task.Delay(intervalMs, token);
                if (token.IsCancellationRequested) return;

                bool isConnected = false;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    RefreshAll();
                    if (IsEthernetConnected || IsWifiConnected)
                    {
                        isConnected = true;
                    }
                });

                if (isConnected) break;
            }
        }, token);
    }


    public void ShowToast(string message)
    {
        _toastCts?.Cancel();
        _toastCts = new CancellationTokenSource();
        var token = _toastCts.Token;

        ToastMessage = message;
        IsToastVisible = true;

        Task.Delay(3500, token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                Application.Current?.Dispatcher.InvokeAsync(() => IsToastVisible = false);
            }
        }, TaskScheduler.Default);
    }

    [RelayCommand]
    public void ConnectWifi(WifiNetworkItemViewModel? item)
    {
        if (item == null || !HasWifiAdapter) return;

        if (item.IsProfileKnown)
        {
            ShowToast($"Connecting to {item.Ssid}...");
            bool success = _wifiService.QuickConnect(item.Ssid);
            if (success)
            {
                StartReconnectionMonitoring(10, 500);
                ShowToast($"Connected to {item.Ssid}.");
            }
            else
            {
                ShowToast($"Failed to connect to {item.Ssid}.");
            }
        }
        else
        {
            ShowToast($"Network '{item.Ssid}' requires a password.");
            ToggleNetworkExpand(item);
        }
    }

    [ObservableProperty]
    private string _copyEthernetIpTooltip = "Copy IPv4";

    [ObservableProperty]
    private string _copyEthernetMacTooltip = "Copy MAC";

    [RelayCommand]
    public void CopyEthernetIp()
    {
        try
        {
            string ip = Ethernet?.IpAddress ?? "--";
            if (!string.IsNullOrWhiteSpace(ip) && ip != "--")
            {
                Clipboard.SetText(ip);
                CopyEthernetIpTooltip = "Copied!";
                Task.Delay(1500).ContinueWith(_ =>
                {
                    Application.Current?.Dispatcher.InvokeAsync(() => CopyEthernetIpTooltip = "Copy IPv4");
                });
            }
        }
        catch { }
    }

    [RelayCommand]
    public void CopyEthernetMac()
    {
        try
        {
            string mac = Ethernet?.MacAddress ?? "--";
            if (!string.IsNullOrWhiteSpace(mac) && mac != "--")
            {
                Clipboard.SetText(mac);
                CopyEthernetMacTooltip = "Copied!";
                Task.Delay(1500).ContinueWith(_ =>
                {
                    Application.Current?.Dispatcher.InvokeAsync(() => CopyEthernetMacTooltip = "Copy MAC");
                });
            }
        }
        catch { }
    }

    [RelayCommand]
    public void CopyIp()
    {
        try
        {
            string ip = ActiveIpAddress;
            if (!string.IsNullOrWhiteSpace(ip) && ip != "--")
            {
                Clipboard.SetText(ip);
                CopyIpTooltip = "Copied!";
                Task.Delay(1500).ContinueWith(_ =>
                {
                    Application.Current?.Dispatcher.InvokeAsync(() => CopyIpTooltip = "Copy IPv4");
                });
            }
        }
        catch { }
    }

    [ObservableProperty]
    private bool _isScanningWifiVisual;

    [RelayCommand]
    public async Task RefreshWifi()
    {
        if (IsScanningWifiVisual) return;
        IsScanningWifiVisual = true;
        RefreshWifiNetworks(triggerScan: true);
        ShowToast("Scanning for networks...");
        try
        {
            await Task.Delay(850);
        }
        finally
        {
            IsScanningWifiVisual = false;
        }
    }

    private int _isScanningWifi;
    private CancellationTokenSource? _networkRefreshCts;

    public void RefreshAll()
    {
        if (!_isHubVisible) return;

        try
        {
            // 1. Fast synchronous in-memory interface detection (< 1ms, zero process spawning)
            var ethCandidates = _ethernetProvider.GetAllEthernetInterfaces();
            HasEthernetAdapter = ethCandidates.Count > 0;
            IsEthernetAdapterDisabled = _disconnectService.IsEthernetDisabled;

            // 2. Ethernet Info
            var eth = _ethernetProvider.GetActiveEthernetInfo();
            Ethernet = eth;
            IsEthernetConnected = !IsEthernetAdapterDisabled && eth.IsConnected;

            // 3. Defer netsh administrative state check to background so the UI thread NEVER stalls
            Task.Run(() =>
            {
                try
                {
                    var ifStatuses = DisconnectService.GetCachedInterfaceStatuses(TimeSpan.FromSeconds(5));
                    string ethName = !string.IsNullOrWhiteSpace(eth.Name) && eth.Name != "Ethernet"
                        ? eth.Name
                        : (_disconnectService.LastDisabledAdapterName ?? "Ethernet");

                    bool ethFoundInNetsh = ifStatuses.TryGetValue(ethName, out var ethStatus) ||
                                          ifStatuses.TryGetValue("Ethernet", out ethStatus);

                    if (ethFoundInNetsh && ethStatus != null)
                    {
                        bool isDisabled = !ethStatus.IsAdminEnabled;
                        if (isDisabled != _disconnectService.IsEthernetDisabled)
                        {
                            Application.Current?.Dispatcher.InvokeAsync(() =>
                            {
                                HasEthernetAdapter = true;
                                IsEthernetAdapterDisabled = isDisabled;
                                _disconnectService.SetEthernetDisabledState(isDisabled);
                                IsEthernetConnected = !isDisabled && eth.IsConnected;
                            });
                        }
                    }
                }
                catch { }
            });

            // 2. Wi-Fi Info
            HasWifiAdapter = _wifiService.HasWifiAdapter;
            if (!HasWifiAdapter)
            {
                IsWifiConnected = false;
                IsWifiRadioOn = false;
                AvailableNetworks.Clear();
            }
            else
            {
                IsWifiRadioOn = _wifiService.IsRadioOn;
                string? fastSsid = _wifiService.GetConnectedSsidFast();
                IsWifiConnected = IsWifiRadioOn && !string.IsNullOrEmpty(fastSsid);

                if (IsWifiRadioOn)
                {
                    RefreshWifiNetworks(triggerScan: false);
                }
                else
                {
                    AvailableNetworks.Clear();
                }

                // Hydrate full IP properties in background to never stall UI thread
                Task.Run(() =>
                {
                    var wifiConn = _wifiService.GetCurrentConnectionDetails();
                    Application.Current?.Dispatcher.InvokeAsync(() =>
                    {
                        WifiConnection = wifiConn;
                        IsWifiConnected = IsWifiRadioOn && wifiConn.IsConnected;
                        OnPropertyChanged(nameof(WifiPanelTitle));
                        OnPropertyChanged(nameof(WifiPanelSymbol));
                        OnPropertyChanged(nameof(WifiPanelTitleColor));
                    });
                });
            }

            // 3. Disconnect state: True only if neither Ethernet nor Wi-Fi is actively connected
            IsInternetDisconnected = !IsEthernetConnected && !IsWifiConnected;

            // 4. Smart Panel default on first load
            if (!_hasInitializedPanel)
            {
                _hasInitializedPanel = true;
                ApplyConnectionDefaultPanel();
            }

            // 5. Smart Deck default: If Wi-Fi is active, default to Networks; if Ethernet-only, default to Specs
            if (!HasWifiAdapter && !IsWifiConnected && IsEthernetConnected && IsShowingNetworks)
            {
                IsShowingNetworks = false;
            }

            // 6. Evaluate health in background
            Task.Run(async () =>
            {
                try
                {
                    var h = await _healthService.EvaluateHealthAsync();
                    Application.Current?.Dispatcher.InvokeAsync(() => Health = h);
                }
                catch { }
            });

            // Notify dependent specs
            OnPropertyChanged(nameof(HeroGlyph));
            OnPropertyChanged(nameof(HeroGlyphColor));
            OnPropertyChanged(nameof(HeroTitle));
            OnPropertyChanged(nameof(HeroSubtitle));
            OnPropertyChanged(nameof(HeroIsConnected));
            OnPropertyChanged(nameof(IsLocalOnlyNoInternet));
            OnPropertyChanged(nameof(HasVerifiedInternet));
            OnPropertyChanged(nameof(EthernetStatusText));
            OnPropertyChanged(nameof(EthernetStatusTextColor));
            OnPropertyChanged(nameof(EthernetStatusDotColor));
            OnPropertyChanged(nameof(EthernetStatusBackground));
            OnPropertyChanged(nameof(EthernetStatusBorderBrush));
            OnPropertyChanged(nameof(ActiveAdapterDescription));
            OnPropertyChanged(nameof(ActiveIpAddress));
            OnPropertyChanged(nameof(ActiveSubnet));
            OnPropertyChanged(nameof(ActiveGateway));
            OnPropertyChanged(nameof(ActiveDns));
            OnPropertyChanged(nameof(ActiveLinkSpeed));
            OnPropertyChanged(nameof(ActiveUptime));
            OnPropertyChanged(nameof(ActiveMacAddress));
            OnPropertyChanged(nameof(DataUsageTotalDisplay));
            OnPropertyChanged(nameof(DataUsageDetailDisplay));
            OnPropertyChanged(nameof(DataUsageFullDisplay));
            OnPropertyChanged(nameof(EthernetActionText));
            OnPropertyChanged(nameof(EthernetActionSubtext));
            OnPropertyChanged(nameof(WifiActionText));
            OnPropertyChanged(nameof(WifiActionSubtext));
            OnPropertyChanged(nameof(EthernetStatusBrush));
            OnPropertyChanged(nameof(WifiStatusBrush));
            OnPropertyChanged(nameof(KillNetStatusBrush));
            OnPropertyChanged(nameof(SpeedStatusBrush));
            OnPropertyChanged(nameof(IsEthernetEnabled));
            OnPropertyChanged(nameof(IsEthernetDisabled));
            OnPropertyChanged(nameof(EthernetPanelTitle));
            OnPropertyChanged(nameof(EthernetPanelIcon));
            OnPropertyChanged(nameof(EthernetPanelTitleColor));
            OnPropertyChanged(nameof(EthernetTileHeader));
            OnPropertyChanged(nameof(EthernetTileIcon));
            OnPropertyChanged(nameof(EthernetTileTooltip));
            OnPropertyChanged(nameof(EthernetPanelName));
            OnPropertyChanged(nameof(EthernetTurnedOffBannerTitle));
            OnPropertyChanged(nameof(IsWifiRadioOn));
            OnPropertyChanged(nameof(IsWifiRadioDisabled));
            OnPropertyChanged(nameof(IsWifiRadioEnabled));
            OnPropertyChanged(nameof(CanToggleWifiRadio));
            OnPropertyChanged(nameof(WifiPanelTitle));
            OnPropertyChanged(nameof(WifiPanelIcon));
            OnPropertyChanged(nameof(WifiPanelTitleColor));
            OnPropertyChanged(nameof(WifiTurnedOffBannerTitle));
        }
        catch { }
    }

    private void RefreshWifiNetworks(bool triggerScan = false)
    {
        if (!HasWifiAdapter || !IsWifiRadioOn) return;
        if (Interlocked.CompareExchange(ref _isScanningWifi, 1, 0) != 0) return;

        Task.Run(() =>
        {
            try
            {
                var rawList = _wifiService.ScanAndGetAvailableNetworks(triggerScan);
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        AvailableNetworks.Clear();
                        foreach (var net in rawList)
                        {
                            bool isExpanded = string.Equals(_activeExpandedSsid, net.Ssid, StringComparison.OrdinalIgnoreCase);
                            // Never show password prompt on a network that is already connected
                            bool isPasswordOpen = isExpanded
                                && !net.IsConnected
                                && string.Equals(_activePasswordPromptSsid, net.Ssid, StringComparison.OrdinalIgnoreCase);

                            AvailableNetworks.Add(new WifiNetworkItemViewModel
                            {
                                Ssid = net.Ssid,
                                SignalQuality = net.SignalQuality,
                                SignalBars = net.SignalBars,
                                SecurityType = net.SecurityType,
                                Standard = net.Standard,
                                AuthAlgorithm = net.AuthAlgorithm,
                                CipherAlgorithm = net.CipherAlgorithm,
                                IsProfileKnown = net.IsProfileKnown,
                                IsConnected = net.IsConnected,
                                IsExpanded = isExpanded,
                                IsPasswordPromptOpen = isPasswordOpen
                            });
                        }
                        OnPropertyChanged(nameof(HasAvailableNetworks));
                        OnPropertyChanged(nameof(HasNoAvailableNetworks));
                    }
                    catch { }
                });
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _isScanningWifi, 0);
            }
        });
    }

    private void OnThroughputUpdated(ThroughputMetrics metrics)
    {
        if (!_isHubVisible) return;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                Throughput = metrics;
                SparklineSamples = _throughputService.History;

                // 1-second dynamic heartbeat check for adapter presence (e.g. USB dongle hot-unplug / plug)
                bool currentWifi = _wifiService.HasWifiAdapter;
                if (currentWifi != HasWifiAdapter)
                {
                    HasWifiAdapter = currentWifi;
                    if (!currentWifi)
                    {
                        IsWifiConnected = false;
                        IsWifiRadioOn = false;
                        AvailableNetworks.Clear();
                        if (IsWifiPanel)
                        {
                            ApplyConnectionDefaultPanel();
                        }
                    }
                    else
                    {
                        RefreshAll();
                    }
                }
                else if (HasWifiAdapter && IsWifiPanel)
                {
                    bool radioState = _wifiService.IsRadioOn;
                    if (radioState != IsWifiRadioOn)
                    {
                        IsWifiRadioOn = radioState;
                        if (!radioState)
                        {
                            IsWifiConnected = false;
                            AvailableNetworks.Clear();
                        }
                        else
                        {
                            RefreshWifiNetworks();
                        }
                    }
                }

                // Dynamic heartbeat check for Ethernet adapter presence & link state
                var eth = _ethernetProvider.GetActiveEthernetInfo();
                bool hasEth = eth.Description != "No Ethernet adapter detected";
                if (hasEth != HasEthernetAdapter || eth.IsConnected != IsEthernetConnected)
                {
                    RefreshAll();
                }
                else if (IsEthernetPanel && eth.IsConnected)
                {
                    Ethernet = eth;
                }

                // Modular, zero-overhead internet reachability check (Smart Passive + gentle 10s fallback)
                if ((IsEthernetConnected || IsWifiConnected) && !IsInternetDisconnected)
                {
                    double currentBps = metrics.DownloadBytesPerSec;
                    bool currentHasInternet = Health != null && Health.Connectivity == ConnectivityLevel.InternetAccess;
                    _ = Task.Run(async () =>
                    {
                        bool reachable = await _healthService.CheckPassiveOrActiveReachabilityAsync(currentBps, _isHubVisible, currentHasInternet);
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            if (!reachable)
                            {
                                if (Health == null || Health.Connectivity != ConnectivityLevel.LocalAccess)
                                {
                                    Health = new NetworkHealthStatus
                                    {
                                        Connectivity = ConnectivityLevel.LocalAccess,
                                        HasDnsResolution = false,
                                        PacketLossPercent = 100,
                                        LatencyMs = -1,
                                        HealthSummary = "Connected to Local Network. No Internet Gateway."
                                    };
                                    NotifyReachabilityChanged();
                                }
                            }
                            else if (Health != null && Health.Connectivity == ConnectivityLevel.LocalAccess)
                            {
                                Health.Connectivity = ConnectivityLevel.InternetAccess;
                                NotifyReachabilityChanged();
                            }
                        });
                    });
                }
            }
            catch { }
        });
    }

    private void NotifyReachabilityChanged()
    {
        OnPropertyChanged(nameof(IsLocalOnlyNoInternet));
        OnPropertyChanged(nameof(HasVerifiedInternet));
        OnPropertyChanged(nameof(EthernetPanelTitle));
        OnPropertyChanged(nameof(EthernetPanelIcon));
        OnPropertyChanged(nameof(EthernetPanelTitleColor));
        OnPropertyChanged(nameof(WifiPanelTitle));
        OnPropertyChanged(nameof(WifiPanelTitleColor));
        OnPropertyChanged(nameof(HeroGlyph));
        OnPropertyChanged(nameof(HeroGlyphColor));
        OnPropertyChanged(nameof(HeroSubtitle));
        OnPropertyChanged(nameof(EthernetStatusText));
        OnPropertyChanged(nameof(EthernetStatusTextColor));
        OnPropertyChanged(nameof(EthernetStatusDotColor));
        OnPropertyChanged(nameof(EthernetStatusBrush));
        OnPropertyChanged(nameof(WifiStatusBrush));
        OnPropertyChanged(nameof(EthernetTileHeader));
        OnPropertyChanged(nameof(EthernetTileIcon));
        OnPropertyChanged(nameof(EthernetTileTooltip));
        OnPropertyChanged(nameof(EthernetPanelName));
        OnPropertyChanged(nameof(EthernetTurnedOffBannerTitle));
    }

    private void OnLatencyUpdated(LatencyMetrics latency)
    {
        if (!_isHubVisible) return;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (Health != null)
            {
                Health.LatencyMs = latency.PingMs;
            }
        });
    }

    private void OnDisconnectStateChanged(bool isDisconnected)
    {
        if (!_isHubVisible) return;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            IsInternetDisconnected = isDisconnected;
        });
    }

    private void OnEthernetDisabledStateChanged(bool isDisabled)
    {
        if (!_isHubVisible) return;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            IsEthernetAdapterDisabled = isDisabled;
            RefreshAll();
        });
    }

    private void OnHealthServiceChanged(NetworkHealthStatus health)
    {
        if (!_isHubVisible) return;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            Health = health;
            OnPropertyChanged(nameof(IsLocalOnlyNoInternet));
            OnPropertyChanged(nameof(HasVerifiedInternet));
            OnPropertyChanged(nameof(HeroGlyphColor));
            OnPropertyChanged(nameof(HeroSubtitle));
            OnPropertyChanged(nameof(EthernetStatusText));
            OnPropertyChanged(nameof(EthernetStatusTextColor));
            OnPropertyChanged(nameof(EthernetStatusDotColor));
            OnPropertyChanged(nameof(EthernetStatusBackground));
            OnPropertyChanged(nameof(EthernetStatusBorderBrush));
        });
    }

    private void OnNetworkChanged()
    {
        if (!_isHubVisible) return;

        _networkRefreshCts?.Cancel();
        _networkRefreshCts?.Dispose();
        _networkRefreshCts = new CancellationTokenSource();
        var token = _networkRefreshCts.Token;

        Task.Delay(350, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    RefreshAll();
                }
                catch { }
            });
        }, TaskScheduler.Default);
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => OnNetworkChanged();

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => OnNetworkChanged();

    public override void Pause()
    {
        _isHubVisible = false;
        _throughputService.Pause();
    }

    public override void Resume()
    {
        _isHubVisible = true;
        _throughputService.Resume();
        RefreshAll();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toastCts?.Cancel();
            _toastCts?.Dispose();

            _reconnectCts?.Cancel();
            _reconnectCts?.Dispose();

            _networkRefreshCts?.Cancel();
            _networkRefreshCts?.Dispose();

            _throughputService.ThroughputUpdated -= OnThroughputUpdated;
            _throughputService.LatencyUpdated -= OnLatencyUpdated;
            _disconnectService.DisconnectStateChanged -= OnDisconnectStateChanged;
            _disconnectService.EthernetDisabledStateChanged -= OnEthernetDisabledStateChanged;
            _healthService.HealthChanged -= OnHealthServiceChanged;

            try
            {
                NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
                NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            }
            catch { }

            _throughputService.Stop();
            AvailableNetworks.Clear();
        }

        base.Dispose(disposing);
    }
}
