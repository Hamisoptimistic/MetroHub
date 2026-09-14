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
using MetroHub.Widgets.Serialization;

namespace MetroHub.Widgets.Catalog.Network;

public partial class WifiNetworkItemViewModel : ObservableObject
{
    public string Ssid { get; init; } = string.Empty;
    public int SignalQuality { get; init; }
    public int SignalBars { get; init; }
    public string SecurityType { get; init; } = "Open";
    public bool IsProfileKnown { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotConnected))]
    private bool _isConnected;

    public bool IsNotConnected => !IsConnected;

    public bool IsSecured => !string.Equals(SecurityType, "Open", StringComparison.OrdinalIgnoreCase);

    public string WifiGlyph => SignalBars switch
    {
        1 => "\uE872",
        2 => "\uE873",
        3 => "\uE874",
        _ => "\uE701"
    };
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
    private static readonly Brush GreenIndicatorBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00E676"));
    private static readonly Brush RedIndicatorBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF3B30"));
    private static readonly Brush AmberIndicatorBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFB703"));

    public Brush EthernetStatusBrush
    {
        get
        {
            if (IsInternetDisconnected || !IsEthernetConnected)
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
            if (IsInternetDisconnected || !IsWifiConnected)
                return RedIndicatorBrush;
            if (IsLocalOnlyNoInternet)
                return AmberIndicatorBrush;
            return GreenIndicatorBrush;
        }
    }

    public Brush KillNetStatusBrush => RedIndicatorBrush;

    public Brush SpeedStatusBrush
    {
        get
        {
            bool isLinked = IsEthernetConnected || IsWifiConnected;
            if (IsInternetDisconnected || !isLinked)
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
            if (IsEthernetConnected) return "\uE839";   // Ethernet Monitor
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
            if (IsEthernetConnected) return string.IsNullOrWhiteSpace(Ethernet.Description) ? "Ethernet" : Ethernet.Description;
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
                string speedInfo = !string.IsNullOrWhiteSpace(Ethernet.LinkSpeedString) && Ethernet.LinkSpeedString != "--" ? $"  •  {Ethernet.LinkSpeedString}" : "";
                return $"Connected{speedInfo}  •  {internetSuffix}";
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
            if (!Ethernet.IsConnected) return "Cable Unplugged";
            if (IsLocalOnlyNoInternet) return "No Internet";
            return "Connected";
        }
    }

    public string EthernetStatusTextColor
    {
        get
        {
            if (!HasEthernetAdapter || !Ethernet.IsConnected) return "#85FFFFFF";
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

    public NetworkWidgetViewModel(TileModel model) : base(model)
    {
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
    private bool _isEthernetAdapterDisabled;

    public string EthernetActionText => IsEthernetAdapterDisabled ? "Enable" : "Disable";

    public string EthernetActionSubtext
    {
        get
        {
            if (!HasEthernetAdapter)
                return "No Ethernet adapter detected on this PC";
            if (IsEthernetAdapterDisabled)
                return "Ethernet adapter is disabled • Click Enable to restore";
            if (IsEthernetConnected)
                return "Disable / disconnect Ethernet (Requires Admin privilege)";
            return "Cable unplugged • Disabling requires Admin privilege";
        }
    }

    public Brush EthernetIndicatorDotBrush =>
        (!HasEthernetAdapter || IsEthernetAdapterDisabled || !IsEthernetConnected)
            ? RedIndicatorBrush
            : GreenIndicatorBrush;

    public string WifiActionText => IsWifiConnected ? "Disconnect" : "Connect";

    public string WifiActionSubtext
    {
        get
        {
            if (!HasWifiAdapter)
                return "No Wi-Fi adapter detected on this PC";
            if (IsWifiConnected)
                return $"Connected to {WifiConnection.Ssid} • Click to disconnect";
            return "Not connected to any Wi-Fi network";
        }
    }

    public Brush WifiIndicatorDotBrush =>
        (!HasWifiAdapter || !IsWifiConnected)
            ? RedIndicatorBrush
            : GreenIndicatorBrush;

    [RelayCommand]
    public async Task ToggleEthernet()
    {
        if (!HasEthernetAdapter && !IsEthernetAdapterDisabled)
        {
            ShowToast("No Ethernet adapter detected.");
            return;
        }

        string actionName = IsEthernetAdapterDisabled ? "restore" : "suspend";
        ShowToast($"Requesting Windows authorization to {actionName} Ethernet adapter...");

        bool success = await _disconnectService.ToggleEthernetAdapterAsync(Ethernet.Name);
        if (success)
        {
            IsEthernetAdapterDisabled = _disconnectService.IsEthernetDisabled;
            ShowToast(IsEthernetAdapterDisabled 
                ? "Ethernet adapter suspended via administrative policy." 
                : "Ethernet adapter re-enabled. Connectivity restored.");
            RefreshAll();
        }
        else
        {
            ShowToast("Action canceled: Administrative authorization was declined.");
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
                ShowToast("Wi-Fi connected.");
                RefreshAll();
            }
            else
            {
                try
                {
                    Process.Start(new ProcessStartInfo("ms-availablenetworks:") { UseShellExecute = true });
                }
                catch { }
            }
        }
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
    public async Task ConnectWifi(WifiNetworkItemViewModel? item)
    {
        if (item == null || !HasWifiAdapter) return;

        if (item.IsProfileKnown)
        {
            ShowToast($"Connecting to {item.Ssid}...");
            bool success = _wifiService.QuickConnect(item.Ssid);
            if (success)
            {
                await Task.Delay(1500);
                RefreshAll();
                ShowToast($"Connected to {item.Ssid}.");
            }
            else
            {
                ShowToast($"Failed to connect to {item.Ssid}.");
            }
        }
        else
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-availablenetworks:")
                {
                    UseShellExecute = true
                });
            }
            catch { }
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

    [RelayCommand]
    public void RefreshWifi()
    {
        RefreshWifiNetworks();
        ShowToast("Scanning for networks...");
    }

    private int _isScanningWifi;
    private CancellationTokenSource? _networkRefreshCts;

    public void RefreshAll()
    {
        if (!_isHubVisible) return;

        try
        {
            // 1. Ethernet Info
            var eth = _ethernetProvider.GetActiveEthernetInfo();
            Ethernet = eth;
            IsEthernetConnected = eth.IsConnected;
            HasEthernetAdapter = eth.Description != "No Ethernet adapter detected";

            // 2. Wi-Fi Info
            HasWifiAdapter = _wifiService.HasWifiAdapter;
            if (!HasWifiAdapter)
            {
                IsWifiConnected = false;
                AvailableNetworks.Clear();
            }
            else
            {
                var wifiConn = _wifiService.GetCurrentConnectionDetails();
                WifiConnection = wifiConn;
                IsWifiConnected = wifiConn.IsConnected;
                RefreshWifiNetworks();
            }

            // 3. Disconnect state
            IsInternetDisconnected = _disconnectService.IsDisconnected;

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
        }
        catch { }
    }

    private void RefreshWifiNetworks()
    {
        if (!HasWifiAdapter) return;
        if (Interlocked.CompareExchange(ref _isScanningWifi, 1, 0) != 0) return;

        Task.Run(() =>
        {
            try
            {
                var rawList = _wifiService.ScanAndGetAvailableNetworks();
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        AvailableNetworks.Clear();
                        foreach (var net in rawList)
                        {
                            AvailableNetworks.Add(new WifiNetworkItemViewModel
                            {
                                Ssid = net.Ssid,
                                SignalQuality = net.SignalQuality,
                                SignalBars = net.SignalBars,
                                SecurityType = net.SecurityType,
                                IsProfileKnown = net.IsProfileKnown,
                                IsConnected = net.IsConnected
                            });
                        }
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

                // Dynamic heartbeat check for Ethernet adapter presence & link state
                var eth = _ethernetProvider.GetActiveEthernetInfo();
                bool hasEth = eth.Description != "No Ethernet adapter detected";
                if (hasEth != HasEthernetAdapter || eth.IsConnected != IsEthernetConnected)
                {
                    Ethernet = eth;
                    HasEthernetAdapter = hasEth;
                    IsEthernetConnected = eth.IsConnected;
                }
                else if (IsEthernetPanel && eth.IsConnected)
                {
                    Ethernet = eth;
                }
            }
            catch { }
        });
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
                    if (IsEthernetPanel || IsWifiPanel)
                    {
                        ApplyConnectionDefaultPanel();
                    }
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
        if (IsEthernetPanel || IsWifiPanel)
        {
            ApplyConnectionDefaultPanel();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toastCts?.Cancel();
            _toastCts?.Dispose();

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
