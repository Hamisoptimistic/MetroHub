using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Core.Services.MobileBridge;
using MetroHub.Widgets.Serialization;
using Wpf.Ui.Controls;

namespace MetroHub.Widgets.Catalog.PhoneBridge;

/// <summary>
/// Pairing surface for the phone LAN bridge.
/// Shows the pairing QR code, the LAN endpoint, and live connection state, and lets the tile
/// switch the bridge on and off. Holds no timers of its own: connection counts refresh from the
/// shared widget heartbeat, and state changes arrive as events from <see cref="MobileBridgeService"/>.
/// </summary>
public sealed partial class PhoneBridgeWidgetViewModel : WidgetViewModelBase, IWidgetContextMenuProvider
{
    /// <summary>Six pixels per QR module keeps the code crisp at 4x4 without any rescaling.</summary>
    private const int QrPixelsPerModule = 6;

    private static readonly Brush OnlineBrush = CreateFrozenBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
    private static readonly Brush IdleBrush = CreateFrozenBrush(Color.FromRgb(0x9A, 0xA3, 0xB2));
    private static readonly Brush BusyBrush = CreateFrozenBrush(Color.FromRgb(0xFB, 0xBF, 0x24));

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private PhoneBridgeWidgetSettings _settings = new();
    private string? _qrSourceUrl;
    private bool _subscribed;
    private bool _disposed;

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new[]
    {
        WidgetSize.SquareLarge // 6x6 — QR plus pairing details, no other sizes by design
    };

    #region Observable state

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOffline))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(ToggleToolTip))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private int _connectedPhones;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PairedText))]
    private int _pairedPhoneCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PairedText))]
    private bool _isHttpsAvailable;

    [ObservableProperty]
    private string _addressText = string.Empty;

    [ObservableProperty]
    private string _hintText = "Turn the bridge on, then scan the QR code with your phone camera.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQrCode))]
    private BitmapSource? _qrImage;

    [ObservableProperty]
    private bool _showQrCode = true;

    [ObservableProperty]
    private bool _showAddress = true;

    #endregion

    #region Computed state

    public bool IsOffline => !IsRunning;

    public bool HasQrCode => QrImage != null;

    public string StatusText
    {
        get
        {
            if (IsBusy)
            {
                return "Starting…";
            }

            if (!IsRunning)
            {
                return "Off";
            }

            return ConnectedPhones switch
            {
                <= 0 => "Waiting for phone",
                1 => "1 phone connected",
                _ => $"{ConnectedPhones} phones connected"
            };
        }
    }

    public Brush StatusBrush => IsBusy ? BusyBrush : (IsRunning ? OnlineBrush : IdleBrush);

    /// <summary>
    /// One caption line: how many phones remember this PC, and whether the secure address is up (the
    /// secure address is what lets a phone install the page as an app and join the share sheet).
    /// </summary>
    public string PairedText
    {
        get
        {
            if (!IsRunning)
            {
                return string.Empty;
            }

            var devices = MobileBridgeService.Instance.PairedPhones;
            string paired = PairedPhoneCount switch
            {
                <= 0 => "No phones paired yet",
                1 when devices.Count == 1 && !string.IsNullOrWhiteSpace(devices[0].Label) => $"Paired: {devices[0].Label}",
                1 => "1 phone paired",
                _ => $"{PairedPhoneCount} phones paired"
            };

            return paired;
        }
    }

    public string ToggleToolTip =>
        IsRunning ? "Turn the phone bridge off" : "Turn the phone bridge on";

    #endregion

    public PhoneBridgeWidgetViewModel(TileModel model) : base(model)
    {
        LoadSettings(model.SettingsJson);
        Subscribe();
        RefreshFromService();
    }

    protected override void LoadSettings(string? settingsJson)
    {
        var settings = WidgetSerializer.Deserialize<PhoneBridgeWidgetSettings>(settingsJson);
        if (settings == null)
        {
            return;
        }

        _settings = settings;
        ShowQrCode = settings.ShowQrCode;
        ShowAddress = settings.ShowAddress;
    }

    public override void SaveSettings()
    {
        _settings.ShowQrCode = ShowQrCode;
        _settings.ShowAddress = ShowAddress;
        _settings.LastPort = MobileBridgeService.Instance.Port;

        Model.SettingsJson = WidgetSerializer.Serialize(_settings);
        NotifySettingsChanged();
    }

    #region Lifecycle

    public override void Resume()
    {
        Subscribe();
        RefreshFromService();
    }

    /// <summary>
    /// Live connection count and paired-phone count. Reads two fields and only raises change
    /// notifications when a value actually moved, so an idle tile costs nothing per second.
    /// </summary>
    public override void OnSecondTick(DateTime utcNow)
    {
        if (!IsRunning)
        {
            return;
        }

        int clients = MobileBridgeService.Instance.PhoneClientCount;
        if (clients != ConnectedPhones)
        {
            ConnectedPhones = clients;
        }

        int paired = MobileBridgeService.Instance.PairedPhoneCount;
        if (paired != PairedPhoneCount)
        {
            PairedPhoneCount = paired;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (disposing && _subscribed)
        {
            MobileBridgeService.Instance.StatusChanged -= OnBridgeStatusChanged;
            _subscribed = false;
        }

        base.Dispose(disposing);
    }

    private void Subscribe()
    {
        if (_subscribed || _disposed)
        {
            return;
        }

        MobileBridgeService.Instance.StatusChanged += OnBridgeStatusChanged;
        _subscribed = true;
    }

    private void OnBridgeStatusChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            // Bridge start/stop can be raised from a background continuation; bindings must update on the UI thread.
            dispatcher.InvokeAsync(SafeRefresh);
            return;
        }

        SafeRefresh();
    }

    private void SafeRefresh() => Safe.Try(RefreshFromService, context: "PhoneBridgeWidgetViewModel.Refresh");

    private void RefreshFromService()
    {
        if (_disposed)
        {
            return;
        }

        var bridge = MobileBridgeService.Instance;
        bool running = bridge.IsRunning;

        IsRunning = running;
        ConnectedPhones = running ? bridge.PhoneClientCount : 0;
        PairedPhoneCount = running ? bridge.PairedPhoneCount : 0;
        IsHttpsAvailable = running && bridge.IsHttpsAvailable;

        AddressText = running && !string.IsNullOrEmpty(bridge.LanAddress)
            ? $"{bridge.LanAddress}:{bridge.Port}"
            : string.Empty;

        RefreshQrCode(bridge.PairingUrl);

        if (!running)
        {
            HintText = "Turn the bridge on, then scan the QR code with your phone camera.";
        }
        else if (!HasQrCode)
        {
            HintText = "QR unavailable — open the address on your phone.";
        }
        else if (PairedPhoneCount > 0)
        {
            HintText = "Paired. Share links, notes, or photos from your phone.";
        }
        else
        {
            HintText = "Scan with your phone camera to pair and install the app.";
        }
    }

    private void RefreshQrCode(string? url)
    {
        if (!ShowQrCode || string.IsNullOrWhiteSpace(url))
        {
            _qrSourceUrl = null;
            QrImage = null;
            return;
        }

        if (string.Equals(_qrSourceUrl, url, StringComparison.Ordinal) && QrImage != null)
        {
            return;
        }

        byte[]? png = PairingQrCode.TryRenderPng(url, QrPixelsPerModule);
        QrImage = png == null ? null : CreateFrozenImage(png);
        _qrSourceUrl = QrImage == null ? null : url;
    }

    private BitmapImage? CreateFrozenImage(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "[PhoneBridgeWidget] Failed to decode pairing QR code");
            return null;
        }
    }

    #endregion

    #region Commands

    [RelayCommand]
    private async Task ToggleBridgeAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        RaiseStatusChanged();

        try
        {
            var bridge = MobileBridgeService.Instance;
            if (bridge.IsRunning)
            {
                await bridge.StopAsync().ConfigureAwait(true);
            }
            else
            {
                var result = await bridge.StartAsync(bridge.PreferredPort).ConfigureAwait(true);
                if (!result.Started)
                {
                    HintText = result.Message ?? "Could not start the bridge.";
                }
            }
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "[PhoneBridgeWidget] Bridge toggle failed");
            HintText = "Could not change the bridge state.";
        }
        finally
        {
            IsBusy = false;
            SafeRefresh();
            SaveSettings();
        }
    }

    [RelayCommand]
    private void CopyLink()
    {
        string? url = MobileBridgeService.Instance.PairingUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            HintText = "Turn the bridge on to get a pairing link.";
            return;
        }

        try
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, url);
            data.SetData(SafeClipboard.FormatExcludeFromMonitor, true);
            data.SetData(SafeClipboard.FormatCanIncludeInHistory, 0);
            data.SetData("CanUploadToCloudClipboard", 0);
            Clipboard.SetDataObject(data, copy: true);

            HintText = "Pairing link copied. Paste it into a message to your phone.";
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "[PhoneBridgeWidget] Clipboard unavailable");
            HintText = string.IsNullOrEmpty(AddressText)
                ? "Could not copy the link."
                : $"Could not copy. Open {AddressText} on your phone.";
        }
    }

    [RelayCommand]
    private void ToggleQr()
    {
        ShowQrCode = !ShowQrCode;
        _qrSourceUrl = null;
        RefreshFromService();
        SaveSettings();
    }

    [RelayCommand]
    private void ToggleAddress()
    {
        ShowAddress = !ShowAddress;
        SaveSettings();
    }

    [RelayCommand]
    private void SendClipboardToPhone()
    {
        var bridge = MobileBridgeService.Instance;
        if (!bridge.IsRunning)
        {
            HintText = "Turn the bridge on to send items to your phone.";
            return;
        }

        if (!bridge.AllowPushToPhone)
        {
            HintText = "PC to phone push is disabled in settings.";
            return;
        }

        try
        {
            var data = SafeClipboard.GetDataObject();
            if (data == null)
            {
                HintText = "Clipboard is empty.";
                return;
            }

            // Mirrors the paste guard: a password manager's marked content never leaves the machine.
            if (SafeClipboard.IsSensitiveData(data))
            {
                HintText = "Clipboard holds sensitive data, so it was not sent.";
                return;
            }

            (string? url, string? note) = MobileBridgePushComposer.DescribeClipboardPush(
                data.GetData(DataFormats.UnicodeText) as string);

            if (url == null && note == null)
            {
                HintText = "Clipboard has no text to send.";
                return;
            }

            int delivered = bridge.PublishToPhone(null, url, note);
            HintText = delivered > 0
                ? $"Sent to {delivered} phone(s)."
                : "Queued. It appears in the phone's list when it connects.";
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "[PhoneBridgeWidget] Clipboard push failed");
            HintText = "Could not read the clipboard.";
        }
    }

    [RelayCommand]
    private void ForgetPhones()
    {
        var bridge = MobileBridgeService.Instance;
        int count = bridge.PairedPhoneCount;

        if (count == 0)
        {
            HintText = "No phones are paired right now.";
            return;
        }

        string question = count == 1
            ? "Forget the paired phone? It must scan the QR code again."
            : $"Forget all {count} paired phones? They must scan the QR code again.";

        Window? owner = Application.Current?.MainWindow;
        System.Windows.MessageBoxResult answer = owner == null
            ? System.Windows.MessageBox.Show(
                question,
                "MetroHub",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question)
            : System.Windows.MessageBox.Show(
                owner,
                question,
                "MetroHub",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

        if (answer != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        int forgotten = bridge.ForgetPairedPhones();
        SafeRefresh();

        HintText = forgotten switch
        {
            0 => "No phones were paired.",
            1 => "Phone forgotten. It must scan the QR code again.",
            _ => $"{forgotten} phones forgotten. They must scan the QR code again."
        };
    }

    #endregion

    #region Context menu

    public IEnumerable<System.Windows.Controls.Control> GetContextMenuItems()
    {
        var toggle = new System.Windows.Controls.MenuItem
        {
            Header = IsRunning ? "Turn phone bridge off" : "Turn phone bridge on"
        };
        toggle.Click += (_, _) => ToggleBridgeCommand.Execute(null);

        var copy = new System.Windows.Controls.MenuItem
        {
            Header = "Copy pairing link",
            IsEnabled = IsRunning
        };
        copy.Click += (_, _) => CopyLinkCommand.Execute(null);

        var qr = new System.Windows.Controls.MenuItem
        {
            Header = "Show QR code",
            IsCheckable = true,
            IsChecked = ShowQrCode
        };
        qr.Click += (_, _) => ToggleQrCommand.Execute(null);

        var address = new System.Windows.Controls.MenuItem
        {
            Header = "Show address",
            IsCheckable = true,
            IsChecked = ShowAddress
        };
        address.Click += (_, _) => ToggleAddressCommand.Execute(null);

        var send = new System.Windows.Controls.MenuItem
        {
            Header = "Send clipboard to phone",
            IsEnabled = IsRunning && MobileBridgeService.Instance.AllowPushToPhone
        };
        send.Click += (_, _) => SendClipboardToPhoneCommand.Execute(null);

        int paired = MobileBridgeService.Instance.PairedPhoneCount;
        var forget = new System.Windows.Controls.MenuItem
        {
            Header = paired switch
            {
                <= 0 => "Forget paired phones",
                1 => "Forget paired phone",
                _ => $"Forget {paired} paired phones"
            },
            IsEnabled = IsRunning && paired > 0
        };
        forget.Click += (_, _) => ForgetPhonesCommand.Execute(null);

        return new System.Windows.Controls.Control[] { toggle, copy, send, qr, address, forget };
    }

    #endregion

    private void RaiseStatusChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
    }
}
