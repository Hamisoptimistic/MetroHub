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
    public string IndicatorPillColor => IsConnected ? (HasInternet ? "#00E676" : "#FFB703") : (IsExpanded ? "#FFFFFF" : "Transparent");
    public string EyeGlyph => IsPasswordVisible ? "\uED1B" : "\uED1A"; // EyeOff / Eye
}

public partial class PhysicalAdapterItemViewModel : ObservableObject
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public PhysicalAdapterType AdapterType { get; init; } = PhysicalAdapterType.Ethernet;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleText))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IconOpacity))]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    private bool _isAdminEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleText))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleText))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string _linkSpeedString = "--";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleText))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleText))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _pendingStatusText;

    public bool CanToggle => !IsBusy;

    public string Glyph => AdapterType switch
    {
        PhysicalAdapterType.Wifi => "\uE701",
        PhysicalAdapterType.UsbTethering => "\uE88A",
        _ => "\uE839"
    };

    public double IconOpacity => IsAdminEnabled ? 1.0 : 0.4;

    public string StatusText
    {
        get
        {
            if (IsBusy && !string.IsNullOrEmpty(PendingStatusText))
                return PendingStatusText;
            if (!IsAdminEnabled) return "Disabled";
            if (IsConnected)
            {
                return string.IsNullOrWhiteSpace(LinkSpeedString) || LinkSpeedString == "--"
                    ? "Connected"
                    : $"Connected • {LinkSpeedString}";
            }
            return AdapterType == PhysicalAdapterType.Ethernet ? "Cable unplugged" : "Not connected";
        }
    }

    public string SubtitleText => $"{Description} • {StatusText}";
}

public sealed partial class NetworkWidgetViewModel : WidgetViewModelBase
{
    private readonly EthernetProvider _ethernetProvider = EthernetProvider.Instance;
    private readonly NativeWifiService _wifiService = NativeWifiService.Instance;
    private readonly ThroughputService _throughputService = ThroughputService.Instance;
    private readonly DisconnectService _disconnectService = DisconnectService.Instance;
    private readonly NetworkHealthService _healthService = NetworkHealthService.Instance;
    private readonly NetworkDataUsageService _dataUsageService = NetworkDataUsageService.Instance;
    private readonly SpeedTestService _speedTestService = new();

    private bool _isHubVisible = true;
    private bool _hasInitializedPanel;
    private CancellationTokenSource? _toastCts;
    private CancellationTokenSource? _reconnectCts;
    private CancellationTokenSource? _speedTestCts;
    private DateTime _lastWifiProbeTime = DateTime.MinValue;
    private DateTime _lastReachabilityProbeTime = DateTime.MinValue;
    private ConnectivityLevel? _lastNotifiedConnectivity;

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new[]
    {
        WidgetSize.Huge // 8x6 (508x380)
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEthernetPanel))]
    [NotifyPropertyChangedFor(nameof(IsWifiPanel))]
    [NotifyPropertyChangedFor(nameof(IsKillNetPanel))]
    [NotifyPropertyChangedFor(nameof(IsAdaptersPanel))]
    [NotifyPropertyChangedFor(nameof(IsSpeedPanel))]
    [NotifyPropertyChangedFor(nameof(IsDataUsagePanel))]
    [NotifyPropertyChangedFor(nameof(IsHotspotPanel))]
    private string _currentPanel = "Ethernet";

    partial void OnCurrentPanelChanged(string value)
    {
        if (!string.Equals(value, "Speed", StringComparison.OrdinalIgnoreCase))
        {
            CancelSpeedTest();
        }
        if (string.Equals(value, "Usage", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "DataUsage", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Hotspot", StringComparison.OrdinalIgnoreCase))
        {
            RefreshDataUsageAsync();
        }
    }

    public bool IsEthernetPanel => string.Equals(CurrentPanel, "Ethernet", StringComparison.OrdinalIgnoreCase);
    public bool IsWifiPanel => string.Equals(CurrentPanel, "Wifi", StringComparison.OrdinalIgnoreCase);
    public bool IsAdaptersPanel => string.Equals(CurrentPanel, "Adapters", StringComparison.OrdinalIgnoreCase) || string.Equals(CurrentPanel, "KillNet", StringComparison.OrdinalIgnoreCase);
    public bool IsKillNetPanel => IsAdaptersPanel;
    public bool IsSpeedPanel => string.Equals(CurrentPanel, "Speed", StringComparison.OrdinalIgnoreCase);
    public bool IsDataUsagePanel => string.Equals(CurrentPanel, "Usage", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(CurrentPanel, "DataUsage", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(CurrentPanel, "Hotspot", StringComparison.OrdinalIgnoreCase);
    public bool IsHotspotPanel => IsDataUsagePanel;

    // --- Dynamic Status Brushes for Modular WidgetTiles ---
    private static readonly Brush GreenIndicatorBrush = CreateFrozenBrush("#00E676");
    private static readonly Brush BlueIndicatorBrush = CreateFrozenBrush("#0091FF");
    private static readonly Brush RedIndicatorBrush = CreateFrozenBrush("#FF3B30");
    private static readonly Brush AmberIndicatorBrush = CreateFrozenBrush("#FFB703");
    private static readonly Brush PurpleIndicatorBrush = CreateFrozenBrush("#A855F7");
    private static readonly Brush MutedIndicatorBrush = CreateFrozenBrush("#80FFFFFF");
    private static readonly Brush WhiteIndicatorBrush = CreateFrozenBrush("#FFFFFF");

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

    public Brush AdaptersStatusBrush
    {
        get
        {
            if (PhysicalAdapters == null || PhysicalAdapters.Count == 0)
                return RedIndicatorBrush;
            bool anyConnected = PhysicalAdapters.Any(a => a.IsAdminEnabled && a.IsConnected);
            return anyConnected ? GreenIndicatorBrush : RedIndicatorBrush;
        }
    }

    public Brush KillNetStatusBrush => AdaptersStatusBrush;

    public Brush SpeedStatusBrush => WhiteIndicatorBrush;

    public Brush DataUsageStatusBrush => WhiteIndicatorBrush;
    public Brush HotspotStatusBrush => DataUsageStatusBrush;



    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedDownloadSpeed))]
    [NotifyPropertyChangedFor(nameof(FormattedUploadSpeed))]
    [NotifyPropertyChangedFor(nameof(SpeedUnitBadge))]
    private bool _isBitsMode = true; // Task Manager style (Mbps) by default

    public string SpeedUnitBadge => IsBitsMode ? "Mbps" : "MB/s";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocalOnlyNoInternet))]
    [NotifyPropertyChangedFor(nameof(HasVerifiedInternet))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusBrush))]
    [NotifyPropertyChangedFor(nameof(SpeedStatusBrush))]
    [NotifyPropertyChangedFor(nameof(EthernetActionText))]
    [NotifyPropertyChangedFor(nameof(EthernetActionSubtext))]
    [NotifyPropertyChangedFor(nameof(EthernetPanelTitle))]
    [NotifyPropertyChangedFor(nameof(EthernetPanelIcon))]
    [NotifyPropertyChangedFor(nameof(EthernetPanelTitleColor))]
    [NotifyPropertyChangedFor(nameof(EthernetTileHeader))]
    [NotifyPropertyChangedFor(nameof(EthernetTileIcon))]
    [NotifyPropertyChangedFor(nameof(EthernetTileSymbol))]
    [NotifyPropertyChangedFor(nameof(EthernetTileTooltip))]
    [NotifyPropertyChangedFor(nameof(EthernetPanelName))]
    [NotifyPropertyChangedFor(nameof(EthernetTurnedOffBannerTitle))]
    [NotifyPropertyChangedFor(nameof(UsageWiredTitle))]
    [NotifyPropertyChangedFor(nameof(UsageWiredIcon))]
    [NotifyPropertyChangedFor(nameof(EthernetUsageStatusText))]
    [NotifyPropertyChangedFor(nameof(EthernetAdapterSummary))]
    private bool _isEthernetConnected;

    [ObservableProperty]
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
    [NotifyPropertyChangedFor(nameof(IsLocalOnlyNoInternet))]
    [NotifyPropertyChangedFor(nameof(HasVerifiedInternet))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusBrush))]
    [NotifyPropertyChangedFor(nameof(WifiStatusBrush))]
    [NotifyPropertyChangedFor(nameof(SpeedStatusBrush))]
    private bool _isInternetDisconnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoWifiAdapter))]
    [NotifyPropertyChangedFor(nameof(WifiActionText))]
    [NotifyPropertyChangedFor(nameof(WifiActionSubtext))]
    [NotifyPropertyChangedFor(nameof(WifiStatusBrush))]
    [NotifyPropertyChangedFor(nameof(ShowWifiUsageDetails))]
    [NotifyPropertyChangedFor(nameof(ShowWifiNoAdapterText))]
    [NotifyPropertyChangedFor(nameof(WifiUsageTotalDisplay))]
    [NotifyPropertyChangedFor(nameof(WifiUsageDetailDisplay))]
    private bool _hasWifiAdapter;

    public bool HasNoWifiAdapter => !HasWifiAdapter;
    public bool ShowWifiUsageDetails => HasWifiAdapter;
    public bool ShowWifiNoAdapterText => !HasWifiAdapter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoEthernetAdapter))]
    [NotifyPropertyChangedFor(nameof(EthernetActionText))]
    [NotifyPropertyChangedFor(nameof(EthernetActionSubtext))]
    [NotifyPropertyChangedFor(nameof(EthernetStatusBrush))]
    [NotifyPropertyChangedFor(nameof(CanToggleEthernet))]
    [NotifyPropertyChangedFor(nameof(IsEthernetEnabled))]
    [NotifyPropertyChangedFor(nameof(IsEthernetDisabled))]
    [NotifyPropertyChangedFor(nameof(EthernetPanelTitle))]
    [NotifyPropertyChangedFor(nameof(EthernetPanelIcon))]
    [NotifyPropertyChangedFor(nameof(EthernetPanelTitleColor))]
    [NotifyPropertyChangedFor(nameof(ShowEthernetUsageDetails))]
    [NotifyPropertyChangedFor(nameof(ShowEthernetNoAdapterText))]
    [NotifyPropertyChangedFor(nameof(EthernetUsageTotalDisplay))]
    [NotifyPropertyChangedFor(nameof(EthernetUsageDetailDisplay))]
    private bool _hasEthernetAdapter;

    public bool HasNoEthernetAdapter => !HasEthernetAdapter;
    public bool ShowEthernetUsageDetails => HasEthernetAdapter;
    public bool ShowEthernetNoAdapterText => !HasEthernetAdapter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EthernetTileHeader))]
    [NotifyPropertyChangedFor(nameof(EthernetTileIcon))]
    [NotifyPropertyChangedFor(nameof(EthernetTileSymbol))]
    [NotifyPropertyChangedFor(nameof(EthernetTileTooltip))]
    [NotifyPropertyChangedFor(nameof(EthernetPanelName))]
    [NotifyPropertyChangedFor(nameof(EthernetTurnedOffBannerTitle))]
    [NotifyPropertyChangedFor(nameof(EthernetPanelIcon))]
    [NotifyPropertyChangedFor(nameof(EthernetActionSubtext))]
    [NotifyPropertyChangedFor(nameof(UsageWiredTitle))]
    [NotifyPropertyChangedFor(nameof(UsageWiredIcon))]
    [NotifyPropertyChangedFor(nameof(EthernetUsageStatusText))]
    [NotifyPropertyChangedFor(nameof(EthernetAdapterSummary))]
    private EthernetInfo _ethernet = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WifiUsageStatusText))]
    [NotifyPropertyChangedFor(nameof(WifiAdapterSummary))]
    private WifiConnectionDetails _wifiConnection = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedDownloadSpeed))]
    [NotifyPropertyChangedFor(nameof(FormattedUploadSpeed))]
    private ThroughputMetrics _throughput = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocalOnlyNoInternet))]
    [NotifyPropertyChangedFor(nameof(HasVerifiedInternet))]
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

    public bool IsLocalOnlyNoInternet =>
        (IsEthernetConnected || IsWifiConnected) &&
        !IsInternetDisconnected &&
        Health != null &&
        Health.Connectivity is ConnectivityLevel.LocalAccess or ConnectivityLevel.ConstrainedInternet;

    public bool HasVerifiedInternet =>
        (IsEthernetConnected || IsWifiConnected) &&
        !IsInternetDisconnected &&
        (Health == null || Health.Connectivity == ConnectivityLevel.InternetAccess);

    public string ActiveIpAddress => IsWifiConnected ? WifiConnection.IpAddress : Ethernet.IpAddress;

    // --- Accurate Data Usage Properties (Windows Settings Sync & Dual-Interface Analytics) ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSessionTimeframe))]
    [NotifyPropertyChangedFor(nameof(Is24HoursTimeframe))]
    [NotifyPropertyChangedFor(nameof(Is7DaysTimeframe))]
    [NotifyPropertyChangedFor(nameof(Is30DaysTimeframe))]
    [NotifyPropertyChangedFor(nameof(TotalCombinedBytes))]
    [NotifyPropertyChangedFor(nameof(TotalCombinedDisplay))]
    [NotifyPropertyChangedFor(nameof(TotalCombinedReceivedDisplay))]
    [NotifyPropertyChangedFor(nameof(TotalCombinedSentDisplay))]
    [NotifyPropertyChangedFor(nameof(TotalCombinedRxTxBadge))]
    [NotifyPropertyChangedFor(nameof(EthernetBarWidth))]
    [NotifyPropertyChangedFor(nameof(WifiBarWidth))]
    [NotifyPropertyChangedFor(nameof(EthernetRatioPercent))]
    [NotifyPropertyChangedFor(nameof(WifiRatioPercent))]
    [NotifyPropertyChangedFor(nameof(EthernetPercentDisplay))]
    [NotifyPropertyChangedFor(nameof(WifiPercentDisplay))]
    [NotifyPropertyChangedFor(nameof(EthernetUsageTotalDisplay))]
    [NotifyPropertyChangedFor(nameof(EthernetUsageDetailDisplay))]
    [NotifyPropertyChangedFor(nameof(WifiUsageTotalDisplay))]
    [NotifyPropertyChangedFor(nameof(WifiUsageDetailDisplay))]
    private DataUsageTimeframe _selectedDataUsageTimeframe = DataUsageTimeframe.Session;

    partial void OnSelectedDataUsageTimeframeChanged(DataUsageTimeframe value)
    {
        RefreshDataUsageAsync();
    }

    public bool IsSessionTimeframe => SelectedDataUsageTimeframe == DataUsageTimeframe.Session;
    public bool Is24HoursTimeframe => SelectedDataUsageTimeframe == DataUsageTimeframe.Last24Hours;
    public bool Is7DaysTimeframe => SelectedDataUsageTimeframe == DataUsageTimeframe.Last7Days;
    public bool Is30DaysTimeframe => SelectedDataUsageTimeframe == DataUsageTimeframe.Last30Days;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalCombinedBytes))]
    [NotifyPropertyChangedFor(nameof(TotalCombinedDisplay))]
    [NotifyPropertyChangedFor(nameof(TotalCombinedReceivedDisplay))]
    [NotifyPropertyChangedFor(nameof(TotalCombinedSentDisplay))]
    [NotifyPropertyChangedFor(nameof(TotalCombinedRxTxBadge))]
    [NotifyPropertyChangedFor(nameof(EthernetBarWidth))]
    [NotifyPropertyChangedFor(nameof(WifiBarWidth))]
    [NotifyPropertyChangedFor(nameof(EthernetRatioPercent))]
    [NotifyPropertyChangedFor(nameof(WifiRatioPercent))]
    [NotifyPropertyChangedFor(nameof(EthernetPercentDisplay))]
    [NotifyPropertyChangedFor(nameof(WifiPercentDisplay))]
    [NotifyPropertyChangedFor(nameof(EthernetUsageTotalDisplay))]
    [NotifyPropertyChangedFor(nameof(EthernetUsageDetailDisplay))]
    private DataUsageResult _ethernetDataUsage = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalCombinedBytes))]
    [NotifyPropertyChangedFor(nameof(TotalCombinedDisplay))]
    [NotifyPropertyChangedFor(nameof(TotalCombinedReceivedDisplay))]
    [NotifyPropertyChangedFor(nameof(TotalCombinedSentDisplay))]
    [NotifyPropertyChangedFor(nameof(TotalCombinedRxTxBadge))]
    [NotifyPropertyChangedFor(nameof(EthernetBarWidth))]
    [NotifyPropertyChangedFor(nameof(WifiBarWidth))]
    [NotifyPropertyChangedFor(nameof(EthernetRatioPercent))]
    [NotifyPropertyChangedFor(nameof(WifiRatioPercent))]
    [NotifyPropertyChangedFor(nameof(EthernetPercentDisplay))]
    [NotifyPropertyChangedFor(nameof(WifiPercentDisplay))]
    [NotifyPropertyChangedFor(nameof(WifiUsageTotalDisplay))]
    [NotifyPropertyChangedFor(nameof(WifiUsageDetailDisplay))]
    private DataUsageResult _wifiDataUsage = new();

    [ObservableProperty]
    private bool _isDataUsageRefreshing;

    // Combined Totals across both Ethernet and Wi-Fi
    public ulong TotalCombinedBytes => (EthernetDataUsage?.TotalBytes ?? 0) + (WifiDataUsage?.TotalBytes ?? 0);
    public ulong TotalCombinedReceivedBytes => (EthernetDataUsage?.BytesReceived ?? 0) + (WifiDataUsage?.BytesReceived ?? 0);
    public ulong TotalCombinedSentBytes => (EthernetDataUsage?.BytesSent ?? 0) + (WifiDataUsage?.BytesSent ?? 0);

    public string TotalCombinedDisplay
    {
        get
        {
            if (IsDataUsageRefreshing && TotalCombinedBytes == 0) return "Refreshing...";
            if (TotalCombinedBytes > 0) return DataUsageResult.FormatWindowsSettingsGigabytes(TotalCombinedBytes);
            return (HasEthernetAdapter || HasWifiAdapter) ? "0 MB" : "--";
        }
    }

    public string TotalCombinedReceivedDisplay => DataUsageResult.FormatWindowsSettingsGigabytes(TotalCombinedReceivedBytes);
    public string TotalCombinedSentDisplay => DataUsageResult.FormatWindowsSettingsGigabytes(TotalCombinedSentBytes);
    public string TotalCombinedRxTxBadge
    {
        get
        {
            if (TotalCombinedBytes > 0)
                return $"↓ {TotalCombinedReceivedDisplay}   ↑ {TotalCombinedSentDisplay}";
            return (HasEthernetAdapter || HasWifiAdapter) ? "↓ 0 MB   ↑ 0 MB" : "--";
        }
    }

    // Ratio & Progress Bar Proportions
    public double EthernetRatioPercent
    {
        get
        {
            if (TotalCombinedBytes == 0)
                return HasEthernetAdapter ? (HasWifiAdapter ? 50.0 : 100.0) : 0.0;
            return Math.Clamp((double)(EthernetDataUsage?.TotalBytes ?? 0) / TotalCombinedBytes * 100.0, 0.0, 100.0);
        }
    }

    public double WifiRatioPercent
    {
        get
        {
            if (TotalCombinedBytes == 0)
                return HasWifiAdapter ? (HasEthernetAdapter ? 50.0 : 100.0) : 0.0;
            return Math.Clamp((double)(WifiDataUsage?.TotalBytes ?? 0) / TotalCombinedBytes * 100.0, 0.0, 100.0);
        }
    }

    public string EthernetPercentDisplay => $"{Math.Round(EthernetRatioPercent)}%";
    public string WifiPercentDisplay => $"{Math.Round(WifiRatioPercent)}%";

    public GridLength EthernetBarWidth
    {
        get
        {
            double ethVal = (EthernetDataUsage?.TotalBytes ?? 0);
            double wifiVal = (WifiDataUsage?.TotalBytes ?? 0);
            if (ethVal == 0 && wifiVal == 0)
            {
                return new GridLength(HasEthernetAdapter ? 1 : 0.001, GridUnitType.Star);
            }
            return new GridLength(Math.Max(ethVal, 0.001), GridUnitType.Star);
        }
    }

    public GridLength WifiBarWidth
    {
        get
        {
            double ethVal = (EthernetDataUsage?.TotalBytes ?? 0);
            double wifiVal = (WifiDataUsage?.TotalBytes ?? 0);
            if (ethVal == 0 && wifiVal == 0)
            {
                return new GridLength(HasWifiAdapter ? 1 : 0.001, GridUnitType.Star);
            }
            return new GridLength(Math.Max(wifiVal, 0.001), GridUnitType.Star);
        }
    }

    // Wired / USB Tethering Interface Specifics
    public string UsageWiredTitle => IsActiveUsbTethering ? "USB Tethering" : "Ethernet";
    public string UsageWiredIcon => IsActiveUsbTethering ? "\uE8EA" : "\uE839";

    public string EthernetUsageTotalDisplay => (EthernetDataUsage?.TotalBytes ?? 0) > 0
        ? EthernetDataUsage!.FormattedTotal
        : (HasEthernetAdapter ? "0 MB" : "--");

    public string EthernetUsageDetailDisplay => (EthernetDataUsage?.TotalBytes ?? 0) > 0
        ? $"↓ {EthernetDataUsage!.FormattedReceived}   ↑ {EthernetDataUsage!.FormattedSent}"
        : (HasEthernetAdapter ? "↓ 0 MB   ↑ 0 MB" : "No active link");

    public string EthernetUsageStatusText
    {
        get
        {
            if (IsActiveUsbTethering)
                return "USB Connected";
            if (Ethernet?.IsConnected == true)
                return "Connected";
            return HasEthernetAdapter ? "Disconnected" : "No Adapter";
        }
    }

    public string EthernetAdapterSummary
    {
        get
        {
            if (IsActiveUsbTethering)
                return "USB Tethering Device";
            if (Ethernet?.IsConnected == true)
                return string.IsNullOrWhiteSpace(Ethernet.Description) ? "Ethernet Adapter" : Ethernet.Description;
            return HasEthernetAdapter ? "Ethernet Adapter (Offline)" : "No Ethernet Adapter";
        }
    }

    // Wi-Fi Interface Specifics
    public string WifiUsageTotalDisplay => (WifiDataUsage?.TotalBytes ?? 0) > 0
        ? WifiDataUsage!.FormattedTotal
        : (HasWifiAdapter ? "0 MB" : "--");

    public string WifiUsageDetailDisplay => (WifiDataUsage?.TotalBytes ?? 0) > 0
        ? $"↓ {WifiDataUsage!.FormattedReceived}   ↑ {WifiDataUsage!.FormattedSent}"
        : (HasWifiAdapter ? "↓ 0 MB   ↑ 0 MB" : "No active link");

    public string WifiUsageStatusText
    {
        get
        {
            if (WifiConnection?.IsConnected == true)
            {
                string ssid = string.IsNullOrWhiteSpace(WifiConnection.Ssid) ? "Connected" : WifiConnection.Ssid;
                return ssid;
            }
            return HasWifiAdapter ? "Disconnected" : "No Adapter";
        }
    }

    public string WifiAdapterSummary
    {
        get
        {
            if (WifiConnection?.IsConnected == true)
                return string.IsNullOrWhiteSpace(WifiConnection.AdapterDescription) ? "Wi-Fi Adapter" : WifiConnection.AdapterDescription;
            return HasWifiAdapter ? "Wi-Fi Adapter (Offline)" : "No Wi-Fi Adapter";
        }
    }



    private int _dataUsageSequenceId;

    public void RefreshDataUsageAsync()
    {
        int sequenceId = Interlocked.Increment(ref _dataUsageSequenceId);
        IsDataUsageRefreshing = true;
        var timeframe = SelectedDataUsageTimeframe;

        Task.Run(async () =>
        {
            try
            {
                var ethTask = _dataUsageService.QueryUsageAsync(NetworkKind.Ethernet, timeframe);
                var wifiTask = _dataUsageService.QueryUsageAsync(NetworkKind.Wifi, timeframe);
                await Task.WhenAll(ethTask, wifiTask).ConfigureAwait(false);

                var ethResult = await ethTask.ConfigureAwait(false);
                var wifiResult = await wifiTask.ConfigureAwait(false);

                // Discard obsolete result if a newer query sequence was requested
                if (sequenceId != Volatile.Read(ref _dataUsageSequenceId)) return;

                if (Application.Current?.Dispatcher is { } dispatcher)
                {
                    await dispatcher.InvokeAsync(() =>
                    {
                        if (sequenceId == Volatile.Read(ref _dataUsageSequenceId))
                        {
                            EthernetDataUsage = ethResult;
                            WifiDataUsage = wifiResult;
                        }
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NetworkWidgetViewModel] RefreshDataUsage error: {ex.Message}");
            }
            finally
            {
                if (sequenceId == Volatile.Read(ref _dataUsageSequenceId))
                {
                    if (Application.Current?.Dispatcher is { } dispatcher)
                    {
                        _ = dispatcher.InvokeAsync(() => IsDataUsageRefreshing = false);
                    }
                }
            }
        });
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

    public ObservableCollection<PhysicalAdapterItemViewModel> PhysicalAdapters { get; } = new();
    public bool HasPhysicalAdapters => PhysicalAdapters.Count > 0;
    public bool HasNoPhysicalAdapters => PhysicalAdapters.Count == 0;

    public NetworkWidgetViewModel(TileModel model) : base(model)
    {
        AvailableNetworks.CollectionChanged += (s, e) =>
        {
            OnPropertyChanged(nameof(HasAvailableNetworks));
            OnPropertyChanged(nameof(HasNoAvailableNetworks));
        };

        PhysicalAdapters.CollectionChanged += (s, e) =>
        {
            OnPropertyChanged(nameof(HasPhysicalAdapters));
            OnPropertyChanged(nameof(HasNoPhysicalAdapters));
            OnPropertyChanged(nameof(AdaptersStatusBrush));
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
        else if (IsAdaptersPanel)
        {
            RefreshPhysicalAdapters();
        }
        else if (IsSpeedPanel)
        {
            Task.Run(() => _healthService.EvaluateHealthAsync());
        }
    }



    [RelayCommand]
    public void ToggleSpeedUnit()
    {
        IsBitsMode = !IsBitsMode;
        SaveSettings();
    }

    // =========================================================================
    // SPEED TEST ENGINE (100% PURE C# .NET 9 MULTI-STREAM HTTP/2 DIAGNOSTICS)
    // =========================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSpeedTestRunning))]
    [NotifyPropertyChangedFor(nameof(IsSpeedTestNotRunning))]
    [NotifyPropertyChangedFor(nameof(IsSpeedTestIdle))]
    [NotifyPropertyChangedFor(nameof(IsNotSpeedTestIdle))]
    [NotifyPropertyChangedFor(nameof(IsSpeedTestCompleted))]
    [NotifyPropertyChangedFor(nameof(IsSpeedTestFailed))]
    [NotifyPropertyChangedFor(nameof(IsSpeedTestCompletedOrFailed))]
    [NotifyPropertyChangedFor(nameof(SpeedTestPhaseBadgeText))]
    [NotifyPropertyChangedFor(nameof(SpeedTestPhaseBadgeBrush))]
    [NotifyPropertyChangedFor(nameof(SpeedTestServerOrStatusDisplay))]
    [NotifyPropertyChangedFor(nameof(IsDownloadPhaseActive))]
    [NotifyPropertyChangedFor(nameof(IsUploadPhaseActive))]
    [NotifyPropertyChangedFor(nameof(IsLatencyPhaseActive))]
    [NotifyPropertyChangedFor(nameof(SpeedStatusBrush))]
    private SpeedTestPhase _speedTestPhase = SpeedTestPhase.Idle;

    public bool IsSpeedTestRunning => SpeedTestPhase is SpeedTestPhase.Connecting or SpeedTestPhase.Ping or SpeedTestPhase.Download or SpeedTestPhase.Upload;
    public bool IsSpeedTestNotRunning => !IsSpeedTestRunning;
    public bool IsSpeedTestIdle => SpeedTestPhase == SpeedTestPhase.Idle;
    public bool IsNotSpeedTestIdle => !IsSpeedTestIdle;
    public bool IsSpeedTestCompleted => SpeedTestPhase == SpeedTestPhase.Completed;
    public bool IsSpeedTestFailed => SpeedTestPhase == SpeedTestPhase.Failed;
    public bool IsSpeedTestCompletedOrFailed => SpeedTestPhase is SpeedTestPhase.Completed or SpeedTestPhase.Failed or SpeedTestPhase.Cancelled;
    public bool IsDownloadPhaseActive => SpeedTestPhase == SpeedTestPhase.Download;
    public bool IsUploadPhaseActive => SpeedTestPhase == SpeedTestPhase.Upload;
    public bool IsLatencyPhaseActive => SpeedTestPhase is SpeedTestPhase.Connecting or SpeedTestPhase.Ping;

    public string SpeedTestServerOrStatusDisplay
    {
        get
        {
            if (IsSpeedTestRunning)
                return SpeedTestStatusMessage;
            if (SpeedTestPhase == SpeedTestPhase.Completed)
                return $"{SpeedTestServerName} • Complete";
            return SpeedTestServerName;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedTestMainNumberDisplay))]
    private double _speedTestInstantaneousMbps;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedTestDownloadSubtitle))]
    [NotifyPropertyChangedFor(nameof(SpeedTestUploadSubtitle))]
    private double _speedTestPeakMbps;

    [ObservableProperty]
    private double _speedTestGaugeMbps;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedTestPingDisplay))]
    [NotifyPropertyChangedFor(nameof(SpeedTestMainNumberDisplay))]
    private double? _speedTestPingMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedTestJitterDisplay))]
    private double? _speedTestJitterMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedTestDownloadDisplay))]
    [NotifyPropertyChangedFor(nameof(SpeedTestDownloadSubtitle))]
    [NotifyPropertyChangedFor(nameof(SpeedTestMainNumberDisplay))]
    private double? _speedTestDownloadMbps;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedTestUploadDisplay))]
    [NotifyPropertyChangedFor(nameof(SpeedTestUploadSubtitle))]
    [NotifyPropertyChangedFor(nameof(SpeedTestMainNumberDisplay))]
    private double? _speedTestUploadMbps;

    [ObservableProperty]
    private string _speedTestStatusMessage = "Ready to test network speed";

    [ObservableProperty]
    private string _speedTestServerName = "Cloudflare Edge • Auto";

    [ObservableProperty]
    private double _speedTestPhaseProgress;

    [ObservableProperty]
    private bool _isMeteredNetwork;

    [ObservableProperty]
    private Brush _speedTestArcBrush = BlueIndicatorBrush;

    public string SpeedTestPingDisplay => SpeedTestPingMs.HasValue ? $"{SpeedTestPingMs.Value:0.#} ms" : "--";
    public string SpeedTestJitterDisplay => SpeedTestJitterMs.HasValue ? $"{SpeedTestJitterMs.Value:0.#} ms" : "--";
    public string SpeedTestDownloadDisplay => SpeedTestDownloadMbps.HasValue ? $"{SpeedTestDownloadMbps.Value:0.#}" : (IsDownloadPhaseActive ? $"{SpeedTestInstantaneousMbps:0.#}" : "--");
    public string SpeedTestUploadDisplay => SpeedTestUploadMbps.HasValue ? $"{SpeedTestUploadMbps.Value:0.#}" : (IsUploadPhaseActive ? $"{SpeedTestInstantaneousMbps:0.#}" : "--");
    public string SpeedTestDownloadShortDisplay => SpeedTestDownloadMbps.HasValue ? $"{SpeedTestDownloadMbps.Value:0.#}" : (IsDownloadPhaseActive ? $"{SpeedTestInstantaneousMbps:0.#}" : "--");
    public string SpeedTestUploadShortDisplay => SpeedTestUploadMbps.HasValue ? $"{SpeedTestUploadMbps.Value:0.#}" : (IsUploadPhaseActive ? $"{SpeedTestInstantaneousMbps:0.#}" : "--");

    public string SpeedTestDownloadSubtitle => SpeedTestDownloadMbps.HasValue
        ? $"Peak: {Math.Max(SpeedTestDownloadMbps.Value, SpeedTestPeakMbps):0.#} Mbps"
        : (IsDownloadPhaseActive ? "Measuring..." : "Pending");

    public string SpeedTestUploadSubtitle => SpeedTestUploadMbps.HasValue
        ? $"Peak: {Math.Max(SpeedTestUploadMbps.Value, SpeedTestPeakMbps):0.#} Mbps"
        : (IsUploadPhaseActive ? "Measuring..." : "Pending");

    public string SpeedTestMainNumberDisplay
    {
        get
        {
            if (SpeedTestPhase == SpeedTestPhase.Download || SpeedTestPhase == SpeedTestPhase.Upload)
                return SpeedTestInstantaneousMbps > 0 ? $"{SpeedTestInstantaneousMbps:0.#}" : "0.0";
            if (SpeedTestPhase == SpeedTestPhase.Completed)
                return SpeedTestDownloadMbps.HasValue ? $"{SpeedTestDownloadMbps.Value:0.#}" : "0.0";
            if (SpeedTestPhase == SpeedTestPhase.Ping)
                return SpeedTestPingMs.HasValue ? $"{SpeedTestPingMs.Value:0.#}" : "...";
            if (SpeedTestPhase == SpeedTestPhase.Connecting)
                return "...";
            return "0.0";
        }
    }

    public string SpeedTestMainUnitDisplay => SpeedTestPhase == SpeedTestPhase.Ping ? "ms" : "Mbps";

    public string SpeedTestPhaseBadgeText => SpeedTestPhase switch
    {
        SpeedTestPhase.Idle => "READY",
        SpeedTestPhase.Connecting => "CONNECTING",
        SpeedTestPhase.Ping => "LATENCY",
        SpeedTestPhase.Download => "DOWNLOAD",
        SpeedTestPhase.Upload => "UPLOAD",
        SpeedTestPhase.Completed => "COMPLETED",
        SpeedTestPhase.Cancelled => "CANCELLED",
        SpeedTestPhase.Failed => "FAILED",
        _ => "SPEED TEST"
    };

    public Brush SpeedTestPhaseBadgeBrush => SpeedTestPhase switch
    {
        SpeedTestPhase.Download or SpeedTestPhase.Completed => BlueIndicatorBrush,
        SpeedTestPhase.Upload => PurpleIndicatorBrush,
        SpeedTestPhase.Ping or SpeedTestPhase.Connecting => AmberIndicatorBrush,
        SpeedTestPhase.Failed => RedIndicatorBrush,
        _ => MutedIndicatorBrush
    };

    [RelayCommand]
    public async Task StartSpeedTestAsync()
    {
        if (IsSpeedTestRunning) return;

        CancelSpeedTest();

        _speedTestCts = new CancellationTokenSource();
        var token = _speedTestCts.Token;

        bool isMetered = SpeedTestService.IsCurrentConnectionMetered();
        IsMeteredNetwork = isMetered;
        if (isMetered)
        {
            ShowToast("Metered network detected: data-conserving mode active.");
        }

        SpeedTestPhase = SpeedTestPhase.Connecting;
        SpeedTestStatusMessage = isMetered ? "Connecting to edge server (metered network)..." : "Connecting to edge server...";
        SpeedTestInstantaneousMbps = 0;
        SpeedTestPeakMbps = 0;
        SpeedTestGaugeMbps = 0;
        SpeedTestPingMs = null;
        SpeedTestJitterMs = null;
        SpeedTestDownloadMbps = null;
        SpeedTestUploadMbps = null;
        SpeedTestPhaseProgress = 0;
        SpeedTestArcBrush = BlueIndicatorBrush;

        long lastTextUpdateTimestamp = 0;
        SpeedTestPhase lastReportedPhase = SpeedTestPhase.Connecting;

        var progress = new Progress<SpeedTestProgress>(p =>
        {
            long now = Stopwatch.GetTimestamp();
            bool phaseChanged = p.Phase != lastReportedPhase;
            lastReportedPhase = p.Phase;

            SpeedTestPhase = p.Phase;
            if (!string.IsNullOrEmpty(p.StatusMessage))
                SpeedTestStatusMessage = p.StatusMessage;

            if (p.IsMeteredConnection) IsMeteredNetwork = true;
            SpeedTestPeakMbps = p.PeakMbps;
            SpeedTestPhaseProgress = p.PhaseProgress;

            // Live gauge needle is ALWAYS 100% fluid and updated on every sample
            if (p.Phase == SpeedTestPhase.Download)
            {
                SpeedTestArcBrush = BlueIndicatorBrush;
                SpeedTestGaugeMbps = p.InstantaneousMbps;
            }
            else if (p.Phase == SpeedTestPhase.Upload)
            {
                SpeedTestArcBrush = PurpleIndicatorBrush;
                SpeedTestGaugeMbps = p.InstantaneousMbps;
            }
            else if (p.Phase == SpeedTestPhase.Completed)
            {
                SpeedTestArcBrush = BlueIndicatorBrush;
                SpeedTestGaugeMbps = SpeedTestDownloadMbps ?? 0.0;
            }
            else if (p.Phase is SpeedTestPhase.Idle or SpeedTestPhase.Cancelled or SpeedTestPhase.Failed)
            {
                SpeedTestGaugeMbps = 0;
            }

            // Immediately apply final completed metrics
            if (p.FinalDownloadMbps.HasValue) SpeedTestDownloadMbps = p.FinalDownloadMbps.Value;
            if (p.FinalUploadMbps.HasValue) SpeedTestUploadMbps = p.FinalUploadMbps.Value;

            // Decoupled numerical text throttling (~130ms) for human readability and flicker elimination
            bool isCompleted = p.Phase is SpeedTestPhase.Completed or SpeedTestPhase.Failed or SpeedTestPhase.Cancelled;
            double elapsedTextMs = (now - lastTextUpdateTimestamp) * 1000.0 / Stopwatch.Frequency;

            if (phaseChanged || isCompleted || elapsedTextMs >= 130.0)
            {
                lastTextUpdateTimestamp = now;

                if (p.PingMs.HasValue) SpeedTestPingMs = p.PingMs.Value;
                if (p.JitterMs.HasValue) SpeedTestJitterMs = p.JitterMs.Value;

                SpeedTestInstantaneousMbps = p.InstantaneousMbps;

                OnPropertyChanged(nameof(SpeedTestMainNumberDisplay));
                OnPropertyChanged(nameof(SpeedTestMainUnitDisplay));
                OnPropertyChanged(nameof(SpeedTestDownloadDisplay));
                OnPropertyChanged(nameof(SpeedTestUploadDisplay));
                OnPropertyChanged(nameof(SpeedTestDownloadShortDisplay));
                OnPropertyChanged(nameof(SpeedTestUploadShortDisplay));
                OnPropertyChanged(nameof(SpeedTestPingDisplay));
                OnPropertyChanged(nameof(SpeedTestJitterDisplay));
            }
        });

        try
        {
            await Task.Run(() => _speedTestService.RunTestAsync(progress, token), token);
        }
        catch (OperationCanceledException)
        {
            SpeedTestPhase = SpeedTestPhase.Idle;
            SpeedTestStatusMessage = "Ready to test network speed";
            SpeedTestGaugeMbps = 0;
        }
        catch (Exception ex)
        {
            SpeedTestPhase = SpeedTestPhase.Failed;
            SpeedTestStatusMessage = SpeedTestService.GetFriendlyErrorMessage(ex);
            SpeedTestGaugeMbps = 0;
        }
    }

    [RelayCommand]
    public void CancelSpeedTest()
    {
        if (_speedTestCts != null)
        {
            try
            {
                _speedTestCts.Cancel();
                _speedTestCts.Dispose();
            }
            catch { }
            _speedTestCts = null;
        }

        if (IsSpeedTestRunning)
        {
            SpeedTestPhase = SpeedTestPhase.Idle;
            SpeedTestStatusMessage = "Ready to test network speed";
            SpeedTestGaugeMbps = 0;
            OnPropertyChanged(nameof(SpeedTestMainNumberDisplay));
        }
    }

    [RelayCommand]
    public void ResetSpeedTest()
    {
        CancelSpeedTest();
        SpeedTestPhase = SpeedTestPhase.Idle;
        SpeedTestStatusMessage = "Ready to test network speed";
        SpeedTestInstantaneousMbps = 0;
        SpeedTestPeakMbps = 0;
        SpeedTestGaugeMbps = 0;
        SpeedTestPingMs = null;
        SpeedTestJitterMs = null;
        SpeedTestDownloadMbps = null;
        SpeedTestUploadMbps = null;
        SpeedTestPhaseProgress = 0;
        SpeedTestArcBrush = BlueIndicatorBrush;
        IsMeteredNetwork = false;
        OnPropertyChanged(nameof(SpeedTestMainNumberDisplay));
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
    [NotifyPropertyChangedFor(nameof(EthernetTileSymbol))]
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

    public SymbolRegular EthernetTileSymbol => IsActiveUsbTethering ? SymbolRegular.UsbPlug24 : SymbolRegular.Connector24;

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
    [NotifyPropertyChangedFor(nameof(IsWifiTransitioning))]
    private bool _isWifiRadioBusy;

    private bool? _optimisticWifiRadioTarget;

    public bool CanToggleWifiRadio => HasWifiAdapter && !IsWifiRadioBusy;
    public bool IsWifiRadioDisabled => !IsWifiRadioOn;
    public string WifiTurnedOffBannerTitle => "Wi-Fi is turned off";

    public bool IsWifiContentVisible => HasWifiAdapter && IsWifiRadioOn && !IsWifiRadioBusy;
    public bool IsWifiTurnedOffBannerVisible => HasWifiAdapter && IsWifiRadioDisabled && !IsWifiRadioBusy;
    public string WifiTransitionStatusText => IsWifiRadioOn ? "Turning on Wi-Fi..." : "Turning off Wi-Fi...";
    public bool IsWifiTransitioning => IsWifiRadioBusy;

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
        OnPropertyChanged(nameof(IsWifiTransitioning));
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
                OnPropertyChanged(nameof(EthernetTileSymbol));
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
        System.Security.SecureString? securePassword = null;

        if (parameter is Wpf.Ui.Controls.PasswordBox uiPb)
        {
            targetItem = uiPb.DataContext as WifiNetworkItemViewModel;
            string pass = uiPb.Password;
            if (!string.IsNullOrEmpty(pass))
            {
                securePassword = new System.Security.SecureString();
                foreach (char c in pass) securePassword.AppendChar(c);
                securePassword.MakeReadOnly();
            }
        }
        else if (parameter is System.Windows.Controls.PasswordBox pb)
        {
            targetItem = pb.DataContext as WifiNetworkItemViewModel;
            securePassword = pb.SecurePassword;
        }
        else if (parameter is WifiNetworkItemViewModel item)
        {
            targetItem = item;
            if (!string.IsNullOrEmpty(item.PasswordText))
            {
                securePassword = new System.Security.SecureString();
                foreach (char c in item.PasswordText) securePassword.AppendChar(c);
                securePassword.MakeReadOnly();
            }
        }

        if (targetItem == null) return;

        if (securePassword == null || securePassword.Length == 0)
        {
            targetItem.HasConnectionError = true;
            targetItem.ConnectionErrorMessage = "Password cannot be empty.";
            return;
        }

        if (targetItem.IsSecured && securePassword.Length < 8)
        {
            targetItem.HasConnectionError = true;
            targetItem.ConnectionErrorMessage = "Password must be at least 8 characters.";
            return;
        }

        // Memory hardening: Immediately clear password from UI and ViewModel
        if (parameter is Wpf.Ui.Controls.PasswordBox clearUiPb) clearUiPb.Clear();
        else if (parameter is System.Windows.Controls.PasswordBox clearPb) clearPb.Clear();
        targetItem.PasswordText = string.Empty;

        try
        {
            await ExecuteWifiConnectAsync(targetItem, securePassword);
        }
        finally
        {
            // Memory hardening: Immediately zero unmanaged DPAPI buffer
            securePassword.Dispose();
        }

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
                if (Application.Current?.Dispatcher is { } dispatcher)
                {
                    await dispatcher.InvokeAsync(() =>
                    {
                        RefreshAll();
                        if (IsEthernetConnected || IsWifiConnected)
                        {
                            isConnected = true;
                        }
                    });
                }

                if (isConnected) break;
            }
        }, token);
    }


    public void ShowToast(string message)
    {
        _toastCts?.Cancel();
        _toastCts?.Dispose();
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

    [ObservableProperty]
    private bool _isRefreshingAdaptersVisual;

    [RelayCommand]
    public async Task RefreshAdapters()
    {
        if (IsRefreshingAdaptersVisual) return;
        IsRefreshingAdaptersVisual = true;
        RefreshPhysicalAdapters();
        ShowToast("Scanning for network adapters...");
        try
        {
            await Task.Delay(750);
        }
        finally
        {
            IsRefreshingAdaptersVisual = false;
        }
    }

    private int _isScanningWifi;
    private CancellationTokenSource? _networkRefreshCts;
    private CancellationTokenSource? _probeCts = new();

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
                var token = _probeCts?.Token ?? CancellationToken.None;
                Task.Run(async () =>
                {
                    if (!_isHubVisible || token.IsCancellationRequested) return;
                    var wifiConn = _wifiService.GetCurrentConnectionDetails();
                    bool wifiHasWan = false;
                    if (wifiConn.IsConnected && _isHubVisible && !token.IsCancellationRequested)
                    {
                        wifiHasWan = await CheckAdapterHasInternetAsync(wifiConn.IpAddress, token);
                    }

                    if (!_isHubVisible || token.IsCancellationRequested) return;

                    Application.Current?.Dispatcher.InvokeAsync(() =>
                    {
                        if (!_isHubVisible || token.IsCancellationRequested) return;
                        WifiConnection = wifiConn;
                        IsWifiConnected = IsWifiRadioOn && wifiConn.IsConnected;
                        IsWifiNoInternet = IsWifiConnected && !wifiHasWan;
                        OnPropertyChanged(nameof(WifiPanelTitle));
                        OnPropertyChanged(nameof(WifiPanelSymbol));
                        OnPropertyChanged(nameof(WifiPanelTitleColor));
                    });
                }, token);
            }

            // 3. Disconnect state: True only if neither Ethernet nor Wi-Fi is actively connected
            IsInternetDisconnected = !IsEthernetConnected && !IsWifiConnected;

            // 4. Smart Panel default on first load
            if (!_hasInitializedPanel)
            {
                _hasInitializedPanel = true;
                ApplyConnectionDefaultPanel();
            }

            // 6. Evaluate health in background
            var healthToken = _probeCts?.Token ?? CancellationToken.None;
            Task.Run(async () =>
            {
                try
                {
                    if (!_isHubVisible || healthToken.IsCancellationRequested) return;
                    var h = await _healthService.EvaluateHealthAsync(healthToken);
                    if (!_isHubVisible || healthToken.IsCancellationRequested) return;
                    Application.Current?.Dispatcher.InvokeAsync(() =>
                    {
                        if (!_isHubVisible || healthToken.IsCancellationRequested) return;
                        Health = h;
                    });
                }
                catch { }
            }, healthToken);

            // 7. Refresh Data Usage (only when active panel is Usage to conserve CPU/battery)
            if (IsDataUsagePanel)
            {
                RefreshDataUsageAsync();
            }

            // Notify dependent specs
            OnPropertyChanged(nameof(IsLocalOnlyNoInternet));
            OnPropertyChanged(nameof(HasVerifiedInternet));
            OnPropertyChanged(nameof(ActiveIpAddress));
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
            OnPropertyChanged(nameof(EthernetTileSymbol));
            OnPropertyChanged(nameof(EthernetTileTooltip));
            OnPropertyChanged(nameof(EthernetPanelName));
            OnPropertyChanged(nameof(EthernetTurnedOffBannerTitle));
            OnPropertyChanged(nameof(UsageWiredTitle));
            OnPropertyChanged(nameof(UsageWiredIcon));
            OnPropertyChanged(nameof(EthernetUsageStatusText));
            OnPropertyChanged(nameof(EthernetAdapterSummary));
            OnPropertyChanged(nameof(WifiUsageStatusText));
            OnPropertyChanged(nameof(WifiAdapterSummary));
            OnPropertyChanged(nameof(IsWifiRadioOn));
            OnPropertyChanged(nameof(IsWifiRadioDisabled));
            OnPropertyChanged(nameof(IsWifiRadioEnabled));
            OnPropertyChanged(nameof(CanToggleWifiRadio));
            OnPropertyChanged(nameof(WifiPanelTitle));
            OnPropertyChanged(nameof(WifiPanelIcon));
            OnPropertyChanged(nameof(WifiPanelTitleColor));
            OnPropertyChanged(nameof(WifiTurnedOffBannerTitle));

            RefreshPhysicalAdapters();
            OnPropertyChanged(nameof(AdaptersStatusBrush));
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

    private int _isRefreshingAdapters;

    public void RefreshPhysicalAdapters()
    {
        if (Interlocked.CompareExchange(ref _isRefreshingAdapters, 1, 0) != 0) return;

        Task.Run(() =>
        {
            try
            {
                var rawAdapters = DisconnectService.GetPhysicalAdapters();
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        UpdatePhysicalAdaptersInPlace(rawAdapters);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _isRefreshingAdapters, 0);
                    }
                });
            }
            catch
            {
                Interlocked.Exchange(ref _isRefreshingAdapters, 0);
            }
        });
    }

    private void UpdatePhysicalAdaptersInPlace(List<PhysicalAdapterInfo> rawList)
    {
        if (rawList == null || rawList.Count == 0)
        {
            if (PhysicalAdapters.Count > 0)
            {
                PhysicalAdapters.Clear();
                OnPropertyChanged(nameof(HasPhysicalAdapters));
                OnPropertyChanged(nameof(HasNoPhysicalAdapters));
                OnPropertyChanged(nameof(AdaptersStatusBrush));
            }
            return;
        }

        var incomingByName = new Dictionary<string, PhysicalAdapterInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in rawList)
        {
            if (!incomingByName.ContainsKey(a.Name))
            {
                incomingByName[a.Name] = a;
            }
        }

        // 1. Remove obsolete adapters
        for (int i = PhysicalAdapters.Count - 1; i >= 0; i--)
        {
            var existing = PhysicalAdapters[i];
            if (!incomingByName.ContainsKey(existing.Name))
            {
                PhysicalAdapters.RemoveAt(i);
            }
        }

        // 2. Update existing adapters in place
        var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < PhysicalAdapters.Count; i++)
        {
            var existing = PhysicalAdapters[i];
            existingNames.Add(existing.Name);

            if (incomingByName.TryGetValue(existing.Name, out var updated))
            {
                if (!existing.IsBusy)
                {
                    if (existing.IsAdminEnabled != updated.IsAdminEnabled) existing.IsAdminEnabled = updated.IsAdminEnabled;
                }
                if (existing.IsConnected != updated.IsConnected) existing.IsConnected = updated.IsConnected;
                if (existing.LinkSpeedString != updated.LinkSpeedString) existing.LinkSpeedString = updated.LinkSpeedString;
            }
        }

        // 3. Add newly discovered adapters
        foreach (var a in rawList)
        {
            if (!existingNames.Contains(a.Name))
            {
                var itemVm = new PhysicalAdapterItemViewModel
                {
                    Id = a.Id,
                    Name = a.Name,
                    Description = a.Description,
                    AdapterType = a.AdapterType,
                    IsAdminEnabled = a.IsAdminEnabled,
                    IsConnected = a.IsConnected,
                    LinkSpeedString = a.LinkSpeedString
                };
                PhysicalAdapters.Add(itemVm);
                existingNames.Add(a.Name);
            }
        }

        OnPropertyChanged(nameof(HasPhysicalAdapters));
        OnPropertyChanged(nameof(HasNoPhysicalAdapters));
        OnPropertyChanged(nameof(AdaptersStatusBrush));
    }

    [RelayCommand]
    public async Task TogglePhysicalAdapter(PhysicalAdapterItemViewModel? adapter)
    {
        if (adapter == null || adapter.IsBusy) return;

        bool originalState = adapter.IsAdminEnabled;
        bool targetState = !originalState;

        // 1. Optimistic UI: immediately flip the toggle to target state and start spinner
        adapter.IsBusy = true;
        adapter.IsAdminEnabled = targetState;
        adapter.PendingStatusText = targetState ? "Enabling..." : "Disabling...";
        ShowToast($"Requesting Windows authorization to {(targetState ? "enable" : "disable")} {adapter.Name}...");

        try
        {
            using var _ = MainWindow.EnterDialogScope();

            // 2. Run background netsh command alongside a simulated smooth fluid transition
            // Fool the user with a deliberate animation & spinner feedback so it feels natural and authentic
            var commandTask = _disconnectService.SetAdapterAdminStateAsync(adapter.Name, targetState);
            var delayTask = Task.Delay(1200);

            await Task.WhenAll(commandTask, delayTask);
            bool success = await commandTask;

            if (success)
            {
                ShowToast($"{adapter.Name} {(targetState ? "enabled" : "disabled")}.");
            }
            else
            {
                // Revert optimistic state on cancel or failure
                adapter.IsAdminEnabled = originalState;
                ShowToast($"Action cancelled or failed for {adapter.Name}.");
            }
        }
        catch (Exception ex)
        {
            adapter.IsAdminEnabled = originalState;
            ShowToast($"Error: {ex.Message}");
        }
        finally
        {
            adapter.PendingStatusText = null;
            adapter.IsBusy = false;
            RefreshPhysicalAdapters();
            RefreshAll();
        }
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

        string candidateDown = IsBitsMode ? metrics.DownloadSpeedBitsString : metrics.DownloadSpeedBytesString;
        string candidateUp = IsBitsMode ? metrics.UploadSpeedBitsString : metrics.UploadSpeedBytesString;
        bool throughputChanged = candidateDown != FormattedDownloadSpeed || candidateUp != FormattedUploadSpeed;

        // Modular, zero-overhead internet reachability check (Smart Passive + throttled 60s fallback)
        bool shouldProbeReachability = (IsEthernetConnected || IsWifiConnected) && !IsInternetDisconnected &&
                                       (Health == null || Health.Connectivity != ConnectivityLevel.InternetAccess || DateTime.UtcNow - _lastReachabilityProbeTime > TimeSpan.FromSeconds(60));

        bool shouldProbeWifi = IsWifiConnected && !string.IsNullOrWhiteSpace(WifiConnection?.IpAddress) &&
                               (IsWifiNoInternet || DateTime.UtcNow - _lastWifiProbeTime > TimeSpan.FromSeconds(60));

        // If throughput and adapter states are identical, and no scheduled probes are due, skip UI dispatch completely
        if (!throughputChanged && !wifiChanged && !radioChanged && !ethChanged && !shouldProbeReachability && !shouldProbeWifi)
        {
            return;
        }

        if (throughputChanged || wifiChanged || radioChanged || ethChanged)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (throughputChanged)
                    {
                        Throughput = metrics;
                    }

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

                    if (!IsWifiConnected && IsWifiNoInternet)
                    {
                        IsWifiNoInternet = false;
                    }
                }
                catch { }
            });
        }

        if (shouldProbeReachability)
        {
            _lastReachabilityProbeTime = DateTime.UtcNow;
            double currentBps = metrics.DownloadBytesPerSec;
            bool currentHasInternet = Health != null && Health.Connectivity == ConnectivityLevel.InternetAccess;
            _ = Task.Run(async () =>
            {
                bool reachable = await _healthService.CheckPassiveOrActiveReachabilityAsync(currentBps, _isHubVisible, currentHasInternet);
                if (Application.Current?.Dispatcher is { } dispatcher)
                {
                    await dispatcher.InvokeAsync(() =>
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
                }
            });
        }

        if (shouldProbeWifi)
        {
            _lastWifiProbeTime = DateTime.UtcNow;
            string? wifiIp = WifiConnection?.IpAddress;
            if (!string.IsNullOrWhiteSpace(wifiIp))
            {
                var wifiProbeToken = _probeCts?.Token ?? CancellationToken.None;
                _ = Task.Run(async () =>
                {
                    if (!_isHubVisible || wifiProbeToken.IsCancellationRequested) return;
                    bool hasWan = await CheckAdapterHasInternetAsync(wifiIp, wifiProbeToken);
                    if (!_isHubVisible || wifiProbeToken.IsCancellationRequested) return;

                    if (Application.Current?.Dispatcher is { } dispatcher)
                    {
                        await dispatcher.InvokeAsync(() =>
                        {
                            if (!_isHubVisible || wifiProbeToken.IsCancellationRequested) return;
                            bool newNoInternet = !hasWan;
                            if (IsWifiNoInternet != newNoInternet)
                            {
                                IsWifiNoInternet = newNoInternet;
                            }
                        });
                    }
                }, wifiProbeToken);
            }
        }
    }

    private void NotifyReachabilityChanged()
    {
        var currentConnectivity = Health?.Connectivity;
        if (_lastNotifiedConnectivity.HasValue && _lastNotifiedConnectivity.Value == currentConnectivity)
        {
            return;
        }
        _lastNotifiedConnectivity = currentConnectivity;

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

        OnPropertyChanged(nameof(EthernetStatusBrush));
        OnPropertyChanged(nameof(WifiStatusBrush));
        OnPropertyChanged(nameof(WifiIndicatorDotBrush));
        OnPropertyChanged(nameof(EthernetTileHeader));
        OnPropertyChanged(nameof(EthernetTileIcon));
        OnPropertyChanged(nameof(EthernetTileSymbol));
        OnPropertyChanged(nameof(EthernetTileTooltip));
        OnPropertyChanged(nameof(EthernetPanelName));
        OnPropertyChanged(nameof(EthernetTurnedOffBannerTitle));
    }

    private void OnLatencyUpdated(LatencyMetrics latency)
    {
        if (!_isHubVisible) return;
        if (Health != null && Health.LatencyMs == latency.PingMs) return;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (Health != null && Health.LatencyMs != latency.PingMs)
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

        _lastReachabilityProbeTime = DateTime.MinValue;
        _lastWifiProbeTime = DateTime.MinValue;
        _lastNotifiedConnectivity = null;
        _ethernetProvider.InvalidateCache();

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
        _healthService.IsHubVisible = false;
        _networkRefreshCts?.Cancel();
        _probeCts?.Cancel();
        _probeCts?.Dispose();
        _probeCts = null;
        _throughputService.Pause();
        CancelSpeedTest();
    }

    public override void Resume()
    {
        _isHubVisible = true;
        _healthService.IsHubVisible = true;
        _probeCts?.Dispose();
        _probeCts = new CancellationTokenSource();
        _throughputService.Resume();
        RefreshAll();
    }

    private static async Task<bool> CheckAdapterHasInternetAsync(string ipAddress, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return false;
        if (string.IsNullOrWhiteSpace(ipAddress) || ipAddress == "--" || ipAddress == "0.0.0.0")
            return false;

        if (!System.Net.IPAddress.TryParse(ipAddress, out var localIp))
            return false;

        var targets = new[]
        {
            new System.Net.IPEndPoint(System.Net.IPAddress.Parse("1.1.1.1"), 53),
            new System.Net.IPEndPoint(System.Net.IPAddress.Parse("8.8.8.8"), 53)
        };

        foreach (var endpoint in targets)
        {
            if (cancellationToken.IsCancellationRequested) return false;

            try
            {
                using var socket = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Tcp);
                socket.Bind(new System.Net.IPEndPoint(localIp, 0));

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(600);
                await socket.ConnectAsync(endpoint, cts.Token).ConfigureAwait(false);
                if (socket.Connected)
                {
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested) return false;
            }
            catch
            {
                // Socket error on this endpoint, try next
            }
        }

        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelSpeedTest();
            _toastCts?.Cancel();
            _toastCts?.Dispose();

            _reconnectCts?.Cancel();
            _reconnectCts?.Dispose();

            _networkRefreshCts?.Cancel();
            _networkRefreshCts?.Dispose();

            _probeCts?.Cancel();
            _probeCts?.Dispose();
            _probeCts = null;

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
