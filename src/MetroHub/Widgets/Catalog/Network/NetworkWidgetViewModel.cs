using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
    private string _currentPanel = "Wifi";

    public bool IsEthernetPanel => string.Equals(CurrentPanel, "Ethernet", StringComparison.OrdinalIgnoreCase);
    public bool IsWifiPanel => string.Equals(CurrentPanel, "Wifi", StringComparison.OrdinalIgnoreCase);
    public bool IsKillNetPanel => string.Equals(CurrentPanel, "KillNet", StringComparison.OrdinalIgnoreCase);
    public bool IsSpeedPanel => string.Equals(CurrentPanel, "Speed", StringComparison.OrdinalIgnoreCase);
    public bool IsHotspotPanel => string.Equals(CurrentPanel, "Hotspot", StringComparison.OrdinalIgnoreCase);

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
    private bool _isInternetDisconnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoWifiAdapter))]
    private bool _hasWifiAdapter;

    public bool HasNoWifiAdapter => !HasWifiAdapter;

    [ObservableProperty]
    private EthernetInfo _ethernet = new();

    [ObservableProperty]
    private WifiConnectionDetails _wifiConnection = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedDownloadSpeed))]
    [NotifyPropertyChangedFor(nameof(FormattedUploadSpeed))]
    private ThroughputMetrics _throughput = new();

    [ObservableProperty]
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

    public string HeroGlyphColor
    {
        get
        {
            if (IsInternetDisconnected) return "#FF4C4C";
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
            if (IsWifiConnected)
            {
                string bandInfo = !string.IsNullOrWhiteSpace(WifiConnection.Band) && WifiConnection.Band != "--" ? $" ({WifiConnection.Band})" : "";
                return $"Connected  •  {WifiConnection.StandardString}{bandInfo}  •  Internet Access";
            }
            if (IsEthernetConnected)
            {
                string speedInfo = !string.IsNullOrWhiteSpace(Ethernet.LinkSpeedString) && Ethernet.LinkSpeedString != "--" ? $"  •  {Ethernet.LinkSpeedString}" : "";
                return $"Connected{speedInfo}  •  Internet Access";
            }
            return "Not connected to any network";
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

    protected override void LoadSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return;
        try
        {
            var settings = WidgetSerializer.Deserialize<NetworkWidgetSettings>(settingsJson);
            if (settings != null)
            {
                if (!string.IsNullOrWhiteSpace(settings.DefaultTab))
                {
                    CurrentPanel = settings.DefaultTab;
                    IsShowingNetworks = !string.Equals(settings.DefaultTab, "Specs", StringComparison.OrdinalIgnoreCase);
                }
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

    public void RefreshAll()
    {
        if (!_isHubVisible) return;

        // 1. Ethernet Info
        var eth = _ethernetProvider.GetActiveEthernetInfo();
        Ethernet = eth;
        IsEthernetConnected = eth.IsConnected;

        // 2. Wi-Fi Info
        HasWifiAdapter = _wifiService.HasWifiAdapter;
        if (!HasWifiAdapter)
        {
            IsWifiConnected = false;
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
            if (IsEthernetConnected && !IsWifiConnected)
            {
                CurrentPanel = "Ethernet";
            }
            else
            {
                CurrentPanel = "Wifi";
            }
        }

        // 5. Smart Deck default: If Wi-Fi is active, default to Networks; if Ethernet-only, default to Specs
        if (!HasWifiAdapter && !IsWifiConnected && IsEthernetConnected && IsShowingNetworks)
        {
            IsShowingNetworks = false;
        }

        // 5. Evaluate health in background
        Task.Run(async () =>
        {
            var h = await _healthService.EvaluateHealthAsync();
            Application.Current?.Dispatcher.InvokeAsync(() => Health = h);
        });

        // Notify dependent specs
        OnPropertyChanged(nameof(HeroGlyph));
        OnPropertyChanged(nameof(HeroGlyphColor));
        OnPropertyChanged(nameof(HeroTitle));
        OnPropertyChanged(nameof(HeroSubtitle));
        OnPropertyChanged(nameof(HeroIsConnected));
        OnPropertyChanged(nameof(ActiveAdapterDescription));
        OnPropertyChanged(nameof(ActiveIpAddress));
        OnPropertyChanged(nameof(ActiveSubnet));
        OnPropertyChanged(nameof(ActiveGateway));
        OnPropertyChanged(nameof(ActiveDns));
        OnPropertyChanged(nameof(ActiveLinkSpeed));
        OnPropertyChanged(nameof(ActiveUptime));
        OnPropertyChanged(nameof(ActiveMacAddress));
    }

    private void RefreshWifiNetworks()
    {
        if (!HasWifiAdapter) return;

        Task.Run(() =>
        {
            var rawList = _wifiService.ScanAndGetAvailableNetworks();
            Application.Current?.Dispatcher.InvokeAsync(() =>
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
            });
        });
    }

    private void OnThroughputUpdated(ThroughputMetrics metrics)
    {
        if (!_isHubVisible) return;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            Throughput = metrics;
            SparklineSamples = _throughputService.History;
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

    private void OnHealthServiceChanged(NetworkHealthStatus health)
    {
        if (!_isHubVisible) return;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            Health = health;
        });
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        if (!_isHubVisible) return;
        Application.Current?.Dispatcher.InvokeAsync(RefreshAll);
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!_isHubVisible) return;
        Application.Current?.Dispatcher.InvokeAsync(RefreshAll);
    }

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

            _throughputService.ThroughputUpdated -= OnThroughputUpdated;
            _throughputService.LatencyUpdated -= OnLatencyUpdated;
            _disconnectService.DisconnectStateChanged -= OnDisconnectStateChanged;
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
