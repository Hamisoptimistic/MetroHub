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

    [ObservableProperty]
    private int _signalQuality;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WifiSignalGlyph))]
    private int _signalBars;

    public string SecurityType { get; init; } = "Open";
    public WlanNative.DOT11_AUTH_ALGORITHM AuthAlgorithm { get; init; }
    public WlanNative.DOT11_CIPHER_ALGORITHM CipherAlgorithm { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBadgeText))]
    [NotifyPropertyChangedFor(nameof(SubtitleText))]
    [NotifyPropertyChangedFor(nameof(StatusBadgeColor))]
    [NotifyPropertyChangedFor(nameof(StatusBadgeBackground))]
    private bool _isProfileKnown;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WifiStandardDisplay))]
    [NotifyPropertyChangedFor(nameof(SecurityDetailsDisplay))]
    private WifiStandard _standard = WifiStandard.Unknown;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IndicatorPillColor))]
    [NotifyPropertyChangedFor(nameof(SubtitleText))]
    [NotifyPropertyChangedFor(nameof(SubtitleColor))]
    [NotifyPropertyChangedFor(nameof(StatusBadgeText))]
    [NotifyPropertyChangedFor(nameof(StatusBadgeColor))]
    [NotifyPropertyChangedFor(nameof(StatusBadgeBackground))]
    private bool _hasInternet = true;

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
    public string IndicatorPillColor => IsConnected ? (HasInternet ? "#00E676" : "#FFB703") : (IsExpanded ? "#FFFFFF" : "#60FFFFFF");
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
    private DateTime _lastWifiProbeTime = DateTime.MinValue;

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
            if (IsWifiNoInternet)
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
    public bool IsActiveUsbTethering => Ethernet != null && Ethernet.IsUsbTethering && Ethernet.IsConnected;

    public string HeroGlyph
    {
        get
        {
            if (IsInternetDisconnected) return "\uE774"; // Globe prohibited
            if (IsWifiConnected) return "\uE701";       // Wi-Fi signal
            if (IsEthernetConnected) return IsActiveUsbTethering ? "\uE8EA" : "\uE839";
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
                if (IsActiveUsbTethering) return "USB Tethering";
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
                string prefix = IsActiveUsbTethering ? "USB Connected" : "Connected";
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
            if (Ethernet == null || !Ethernet.IsConnected) return IsActiveUsbTethering ? "Device Unplugged" : "Cable Unplugged";
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
    public bool HasNoAvailableNetworks => HasWifiAdapter && IsWifiRadioOn && !IsWifiRadioBusy && AvailableNetworks.Count == 0;

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
    public string EthernetTileHeader => IsActiveUsbTethering ? "USB Tether" : "Ethernet";

    public string EthernetTileIcon => IsActiveUsbTethering ? "\uE8EA" : "\uE839";

    public string EthernetTileTooltip => IsActiveUsbTethering
        ? "USB Tethering Settings & Telemetry"
        : "Ethernet Settings & Telemetry";

    public string EthernetPanelName => IsActiveUsbTethering ? "USB Tethering" : "Ethernet";

    public string EthernetTurnedOffBannerTitle => $"{EthernetPanelName} is turned off";

    public string EthernetPanelIcon
    {
        get
        {
            if (!HasEthernetAdapter || IsEthernetDisabled || !IsEthernetConnected)
                return "\uEB55"; // Disconnected
            if (IsEthernetNoInternet)
                return "\uE774"; // Globe No Internet
            return IsActiveUsbTethering ? "\uE8EA" : "\uE839"; // Connected
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
            string name = IsActiveUsbTethering ? "USB Tethering" : "Ethernet";
            if (!HasEthernetAdapter)
                return $"No {name} adapter detected on this PC";
            if (IsEthernetAdapterDisabled)
                return $"{name} adapter is disabled • Click Enable to restore";
            if (IsEthernetConnected)
                return $"Disable / disconnect {name} (Requires Admin privilege)";
            return IsActiveUsbTethering
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
    [NotifyPropertyChangedFor(nameof(IsWifiContentVisible))]
    [NotifyPropertyChangedFor(nameof(IsWifiTurnedOffBannerVisible))]
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
    [NotifyPropertyChangedFor(nameof(IsWifiContentVisible))]
    [NotifyPropertyChangedFor(nameof(IsWifiTurnedOffBannerVisible))]
    [NotifyPropertyChangedFor(nameof(WifiTransitionStatusText))]
    [NotifyPropertyChangedFor(nameof(HasNoAvailableNetworks))]
    private bool _isWifiRadioBusy;

    private bool? _optimisticWifiRadioTarget;

    public bool CanToggleWifiRadio => HasWifiAdapter && !IsWifiRadioBusy;
    public bool IsWifiRadioDisabled => !IsWifiRadioOn;
    public string WifiTurnedOffBannerTitle => "Wi-Fi is turned off";

    public bool IsWifiContentVisible => HasWifiAdapter && IsWifiRadioOn && !IsWifiRadioBusy;
    public bool IsWifiTurnedOffBannerVisible => HasWifiAdapter && IsWifiRadioDisabled && !IsWifiRadioBusy;
    public string WifiTransitionStatusText => IsWifiRadioOn ? "Turning on Wi-Fi..." : "Turning off Wi-Fi...";

    public bool IsWifiRadioEnabled
    {
        get => _optimisticWifiRadioTarget ?? (HasWifiAdapter && IsWifiRadioOn);
        set
        {
            if (value == IsWifiRadioEnabled) return;
            _ = HandleWifiRadioToggleAsync(value);
        }
    }

    private void NotifyWifiStateProperties()
    {
        OnPropertyChanged(nameof(IsWifiRadioOn));
        OnPropertyChanged(nameof(IsWifiRadioEnabled));
        OnPropertyChanged(nameof(IsWifiRadioDisabled));
        OnPropertyChanged(nameof(CanToggleWifiRadio));
        OnPropertyChanged(nameof(IsWifiRadioBusy));
        OnPropertyChanged(nameof(IsWifiContentVisible));
        OnPropertyChanged(nameof(IsWifiTurnedOffBannerVisible));
        OnPropertyChanged(nameof(WifiTransitionStatusText));
        OnPropertyChanged(nameof(WifiPanelTitle));
        OnPropertyChanged(nameof(WifiPanelSymbol));
        OnPropertyChanged(nameof(WifiPanelIcon));
        OnPropertyChanged(nameof(WifiPanelTitleColor));
        OnPropertyChanged(nameof(WifiTurnedOffBannerTitle));
        OnPropertyChanged(nameof(HasNoAvailableNetworks));
        OnPropertyChanged(nameof(HasAvailableNetworks));
        OnPropertyChanged(nameof(WifiActionText));
        OnPropertyChanged(nameof(WifiActionSubtext));
        OnPropertyChanged(nameof(WifiStatusBrush));
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
            : (IsWifiNoInternet ? AmberIndicatorBrush : GreenIndicatorBrush);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WifiStatusBrush))]
    [NotifyPropertyChangedFor(nameof(WifiIndicatorDotBrush))]
    [NotifyPropertyChangedFor(nameof(WifiPanelTitle))]
    [NotifyPropertyChangedFor(nameof(WifiPanelTitleColor))]
    [NotifyPropertyChangedFor(nameof(WifiPanelSymbol))]
    [NotifyPropertyChangedFor(nameof(WifiPanelIcon))]
    private bool _isWifiNoInternet;

    partial void OnIsWifiNoInternetChanged(bool value)
    {
        string? currentSsid = WifiConnection?.Ssid;
        foreach (var net in AvailableNetworks)
        {
            if (net.IsConnected || (!string.IsNullOrEmpty(currentSsid) && string.Equals(net.Ssid, currentSsid, StringComparison.OrdinalIgnoreCase)))
            {
                net.HasInternet = !value;
            }
        }
    }

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

            string name = IsActiveUsbTethering ? "USB Tethering" : "Ethernet";
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
        _optimisticWifiRadioTarget = targetEnabled;
        IsWifiRadioOn = targetEnabled;

        NotifyWifiStateProperties();
        ShowToast(targetEnabled ? "Turning on Wi-Fi..." : "Turning off Wi-Fi...");

        try
        {
            if (targetEnabled)
            {
                // 1. Issue radio power-on command to driver
                await Task.Run(() => _wifiService.SetRadioState(true));

                // 2. Poll until driver/dongle confirms radio is active (up to 7.5s for USB dongles)
                for (int i = 0; i < 25; i++)
                {
                    await Task.Delay(300);
                    if (_wifiService.IsRadioOn)
                    {
                        break;
                    }
                    if (i > 0 && i % 5 == 0)
                    {
                        _ = Task.Run(() => _wifiService.SetRadioState(true));
                    }
                }

                IsWifiRadioOn = true;

                // 3. Scan and collect networks in background before dismissing progress ring
                await Task.Run(() =>
                {
                    try
                    {
                        var rawList = _wifiService.ScanAndGetAvailableNetworks(triggerScan: true);
                        Application.Current?.Dispatcher.InvokeAsync(() =>
                        {
                            UpdateAvailableNetworksInPlace(rawList);
                        });
                    }
                    catch { }
                });

                ShowToast("Wi-Fi turned on.");
            }
            else
            {
                // Disconnect and turn radio off
                await Task.Run(() =>
                {
                    try
                    {
                        _wifiService.Disconnect();
                        _wifiService.SetRadioState(false);
                    }
                    catch { }
                });

                for (int i = 0; i < 6; i++)
                {
                    await Task.Delay(200);
                    if (!_wifiService.IsRadioOn) break;
                }

                IsWifiRadioOn = false;
                IsWifiConnected = false;
                Application.Current?.Dispatcher.InvokeAsync(() => AvailableNetworks.Clear());
                ShowToast("Wi-Fi turned off.");
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Wi-Fi error: {ex.Message}");
            IsWifiRadioOn = _wifiService.IsRadioOn;
        }
        finally
        {
            _optimisticWifiRadioTarget = null;
            IsWifiRadioBusy = false;
            NotifyWifiStateProperties();
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
        // Instant UI Thread Execution
        if (item != null) item.IsConnected = false;
        IsWifiConnected = false;

        bool res = await Task.Run(() => _wifiService.Disconnect());
        if (res)
        {
            ShowToast("Disconnected from Wi-Fi.");
            RefreshAll();
        }
        else
        {
            ShowToast("Failed to disconnect Wi-Fi.");
            RefreshAll();
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
                // Instant UI Thread Execution:
                item.IsConnected = true;
                item.IsPasswordPromptOpen = false;
                item.IsExpanded = false;
                IsWifiConnected = true;

                // Move connected item to top immediately without destroying or recreating visual elements
                int currentIdx = AvailableNetworks.IndexOf(item);
                if (currentIdx > 0)
                {
                    AvailableNetworks.Move(currentIdx, 0);
                }
                foreach (var net in AvailableNetworks)
                {
                    if (!ReferenceEquals(net, item) && net.IsConnected)
                    {
                        net.IsConnected = false;
                    }
                }

                ShowToast($"Connected to {item.Ssid}.");
                RefreshAll();
            }
            else
            {
                item.HasConnectionError = true;
                item.ConnectionErrorMessage = "Incorrect password";
                ShowToast("Connection failed: Incorrect password");
            }
        }
        catch (Exception)
        {
            item.HasConnectionError = true;
            item.ConnectionErrorMessage = "Incorrect password";
            ShowToast("Connection failed: Incorrect password");
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
            else if (!IsWifiRadioBusy)
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
                Task.Run(async () =>
                {
                    var wifiConn = _wifiService.GetCurrentConnectionDetails();
                    bool wifiHasWan = false;
                    if (wifiConn.IsConnected)
                    {
                        wifiHasWan = await CheckAdapterHasInternetAsync(wifiConn.IpAddress);
                    }

                    Application.Current?.Dispatcher.InvokeAsync(() =>
                    {
                        WifiConnection = wifiConn;
                        IsWifiConnected = IsWifiRadioOn && wifiConn.IsConnected;
                        IsWifiNoInternet = IsWifiConnected && !wifiHasWan;
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
                        UpdateAvailableNetworksInPlace(rawList);
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

    private void UpdateAvailableNetworksInPlace(IReadOnlyList<WifiNetworkItem> rawList)
    {
        if (rawList == null || rawList.Count == 0)
        {
            if (AvailableNetworks.Count > 0)
            {
                AvailableNetworks.Clear();
                OnPropertyChanged(nameof(HasAvailableNetworks));
                OnPropertyChanged(nameof(HasNoAvailableNetworks));
            }
            return;
        }

        var incomingBySsid = new Dictionary<string, WifiNetworkItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var net in rawList)
        {
            if (!incomingBySsid.ContainsKey(net.Ssid))
            {
                incomingBySsid[net.Ssid] = net;
            }
        }

        // 1. Remove networks that are no longer present in the scan
        for (int i = AvailableNetworks.Count - 1; i >= 0; i--)
        {
            var existing = AvailableNetworks[i];
            if (!incomingBySsid.ContainsKey(existing.Ssid))
            {
                AvailableNetworks.RemoveAt(i);
            }
        }

        // 2. Update existing networks in-place without destroying visual tree
        var existingSsids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? currentConnectedSsid = WifiConnection?.Ssid;
        for (int i = 0; i < AvailableNetworks.Count; i++)
        {
            var existing = AvailableNetworks[i];
            existingSsids.Add(existing.Ssid);

            if (incomingBySsid.TryGetValue(existing.Ssid, out var updated))
            {
                if (existing.SignalQuality != updated.SignalQuality) existing.SignalQuality = updated.SignalQuality;
                if (existing.SignalBars != updated.SignalBars) existing.SignalBars = updated.SignalBars;
                if (existing.IsProfileKnown != updated.IsProfileKnown) existing.IsProfileKnown = updated.IsProfileKnown;
                if (existing.IsConnected != updated.IsConnected) existing.IsConnected = updated.IsConnected;
                if (existing.Standard != updated.Standard) existing.Standard = updated.Standard;

                bool isNetConnected = existing.IsConnected || (!string.IsNullOrEmpty(currentConnectedSsid) && string.Equals(existing.Ssid, currentConnectedSsid, StringComparison.OrdinalIgnoreCase));
                if (isNetConnected)
                {
                    existing.HasInternet = !IsWifiNoInternet;
                }

                bool shouldExpand = string.Equals(_activeExpandedSsid, existing.Ssid, StringComparison.OrdinalIgnoreCase);
                if (existing.IsExpanded != shouldExpand) existing.IsExpanded = shouldExpand;

                bool shouldPasswordOpen = shouldExpand && !existing.IsConnected && string.Equals(_activePasswordPromptSsid, existing.Ssid, StringComparison.OrdinalIgnoreCase);
                if (existing.IsPasswordPromptOpen != shouldPasswordOpen) existing.IsPasswordPromptOpen = shouldPasswordOpen;
            }
        }

        // 3. Append new networks that were discovered in this scan
        foreach (var net in rawList)
        {
            if (!existingSsids.Contains(net.Ssid))
            {
                bool isExpanded = string.Equals(_activeExpandedSsid, net.Ssid, StringComparison.OrdinalIgnoreCase);
                bool isPasswordOpen = isExpanded && !net.IsConnected && string.Equals(_activePasswordPromptSsid, net.Ssid, StringComparison.OrdinalIgnoreCase);
                bool isNetConnected = net.IsConnected || (!string.IsNullOrEmpty(currentConnectedSsid) && string.Equals(net.Ssid, currentConnectedSsid, StringComparison.OrdinalIgnoreCase));

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
                    IsConnected = isNetConnected,
                    HasInternet = isNetConnected ? !IsWifiNoInternet : true,
                    IsExpanded = isExpanded,
                    IsPasswordPromptOpen = isPasswordOpen
                });
                existingSsids.Add(net.Ssid);
            }
        }

        // 4. Ensure connected network is at index 0 without rebuilding the list
        int connectedIdx = -1;
        for (int i = 0; i < AvailableNetworks.Count; i++)
        {
            if (AvailableNetworks[i].IsConnected)
            {
                connectedIdx = i;
                break;
            }
        }
        if (connectedIdx > 0)
        {
            AvailableNetworks.Move(connectedIdx, 0);
        }

        OnPropertyChanged(nameof(HasAvailableNetworks));
        OnPropertyChanged(nameof(HasNoAvailableNetworks));
    }

    private void OnThroughputUpdated(ThroughputMetrics metrics)
    {
        if (!_isHubVisible) return;

        // Perform heartbeat hardware & interface checks on the background worker thread
        bool currentWifi = _wifiService.HasWifiAdapter;
        bool wifiChanged = currentWifi != HasWifiAdapter;

        bool currentRadio = currentWifi && _wifiService.IsRadioOn;
        bool radioChanged = currentWifi && (currentRadio != IsWifiRadioOn);

        var eth = _ethernetProvider.GetActiveEthernetInfo();
        bool hasEth = eth.Description != "No Ethernet adapter detected";
        bool ethChanged = (hasEth != HasEthernetAdapter) || (eth.IsConnected != IsEthernetConnected);

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                Throughput = metrics;
                SparklineSamples = _throughputService.History;

                if (wifiChanged)
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
                else if (radioChanged && !IsWifiRadioBusy)
                {
                    IsWifiRadioOn = currentRadio;
                    if (!currentRadio)
                    {
                        IsWifiConnected = false;
                        AvailableNetworks.Clear();
                    }
                    else
                    {
                        RefreshWifiNetworks();
                    }
                }

                if (ethChanged)
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

                // Dedicated Wi-Fi adapter internet verification (every 2.5s)
                if (IsWifiConnected && !string.IsNullOrWhiteSpace(WifiConnection?.IpAddress))
                {
                    if (DateTime.UtcNow - _lastWifiProbeTime > TimeSpan.FromSeconds(2.5))
                    {
                        _lastWifiProbeTime = DateTime.UtcNow;
                        string wifiIp = WifiConnection.IpAddress;
                        _ = Task.Run(async () =>
                        {
                            bool hasWan = await CheckAdapterHasInternetAsync(wifiIp);
                            await Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                bool newNoInternet = !hasWan;
                                if (IsWifiNoInternet != newNoInternet)
                                {
                                    IsWifiNoInternet = newNoInternet;
                                }
                            });
                        });
                    }
                }
                else if (!IsWifiConnected && IsWifiNoInternet)
                {
                    IsWifiNoInternet = false;
                }
            }
            catch { }
        });
    }

    private void NotifyReachabilityChanged()
    {
        if (IsWifiConnected && IsLocalOnlyNoInternet)
        {
            IsWifiNoInternet = true;
        }

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
        OnPropertyChanged(nameof(WifiIndicatorDotBrush));
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
            if (IsWifiConnected && (health.Connectivity is ConnectivityLevel.LocalAccess or ConnectivityLevel.ConstrainedInternet))
            {
                IsWifiNoInternet = true;
            }
            NotifyReachabilityChanged();
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

    private static async Task<bool> CheckAdapterHasInternetAsync(string ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress) || ipAddress == "--" || ipAddress == "0.0.0.0")
            return false;

        if (!System.Net.IPAddress.TryParse(ipAddress, out var localIp))
            return false;

        return await Task.Run(() =>
        {
            try
            {
                using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                socket.Bind(new System.Net.IPEndPoint(localIp, 0));
                var result = socket.BeginConnect(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("1.1.1.1"), 53), null, null);
                bool success = result.AsyncWaitHandle.WaitOne(600, true);
                if (success && socket.Connected)
                {
                    socket.EndConnect(result);
                    return true;
                }

                using var socket2 = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                socket2.Bind(new System.Net.IPEndPoint(localIp, 0));
                var result2 = socket2.BeginConnect(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("8.8.8.8"), 53), null, null);
                bool success2 = result2.AsyncWaitHandle.WaitOne(600, true);
                if (success2 && socket2.Connected)
                {
                    socket2.EndConnect(result2);
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        });
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
