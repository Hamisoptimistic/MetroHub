using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetroHub.Core.Models;
using MetroHub.Widgets;
using Wpf.Ui.Controls;

namespace MetroHub.Widgets.Catalog.Power;

/// <summary>
/// Professional ViewModel for 8x2 and 8x1 Power &amp; Session Widget.
/// Supports a 5-second in-tile Cancel window for all power actions (Lock, Sleep, Restart, Shut Down)
/// with live countdown and immediate dismissal, using crisp WPF-UI Fluent System Icons.
/// </summary>
public sealed partial class PowerWidgetViewModel : WidgetViewModelBase
{
    private static readonly SolidColorBrush RedBrush = new((Color)ColorConverter.ConvertFromString("#FF5252"));
    private static readonly SolidColorBrush WhiteBrush = Brushes.White;

    static PowerWidgetViewModel()
    {
        if (RedBrush.CanFreeze) RedBrush.Freeze();
    }

    private readonly DispatcherTimer _countdownTimer;
    private int _remainingSeconds = 0;
    private string? _pendingAction;

    public override IReadOnlyList<WidgetSize> AllowedSizes { get; } = new[]
    {
        WidgetSize.Banner,     // 8x2
        WidgetSize.SlimBanner  // 8x1
    };

    // --- Lock Tile State (LockClosed24 valid BMP glyph) ---
    [ObservableProperty]
    private string _lockHeader = "Lock";

    [ObservableProperty]
    private SymbolRegular _lockSymbol = SymbolRegular.LockClosed24;

    [ObservableProperty]
    private Brush _lockForeground = WhiteBrush;

    [ObservableProperty]
    private string _lockTooltip = "Lock";

    // --- Sleep Tile State (WeatherMoon24) ---
    [ObservableProperty]
    private string _sleepHeader = "Sleep";

    [ObservableProperty]
    private SymbolRegular _sleepSymbol = SymbolRegular.WeatherMoon24;

    [ObservableProperty]
    private Brush _sleepForeground = WhiteBrush;

    [ObservableProperty]
    private string _sleepTooltip = "Sleep";

    // --- Restart Tile State (ArrowCounterclockwise24) ---
    [ObservableProperty]
    private string _restartHeader = "Restart";

    [ObservableProperty]
    private SymbolRegular _restartSymbol = SymbolRegular.ArrowCounterclockwise24;

    [ObservableProperty]
    private Brush _restartForeground = WhiteBrush;

    [ObservableProperty]
    private string _restartTooltip = "Restart";

    // --- Shut Down Tile State (Power24) ---
    [ObservableProperty]
    private string _shutdownHeader = "Shut Down";

    [ObservableProperty]
    private SymbolRegular _shutdownSymbol = SymbolRegular.Power24;

    [ObservableProperty]
    private Brush _shutdownForeground = WhiteBrush;

    [ObservableProperty]
    private string _shutdownTooltip = "Shut Down";

    public bool IsCompactMode => Model.SpanY <= 1;

    public double IconFontSize => 24.0;

    public double HeaderFontSize => 11.5;

    public Thickness IconMargin => IsCompactMode ? new Thickness(0) : new Thickness(0, 0, 0, 5);

    public Visibility TextVisibility => IsCompactMode ? Visibility.Collapsed : Visibility.Visible;

    public PowerWidgetViewModel(TileModel model) : base(model)
    {
        _countdownTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _countdownTimer.Tick += (s, e) => OnCountdownTick();

        Model.PropertyChanged += OnModelPropertyChanged;
    }

    [RelayCommand]
    private void ExecuteAction(string? action)
    {
        if (string.IsNullOrEmpty(action)) return;

        // If this tile is already counting down, clicking it cancels the countdown!
        if (string.Equals(_pendingAction, action, StringComparison.OrdinalIgnoreCase))
        {
            CancelPendingCountdown();
            return;
        }

        // If another tile was counting down, cancel it and start on this one
        if (!string.IsNullOrEmpty(_pendingAction))
        {
            CancelPendingCountdown();
        }

        StartCountdown(action);
    }

    private void StartCountdown(string action)
    {
        _pendingAction = action;
        _remainingSeconds = 5;

        ApplyTileCountdownDisplay(action, _remainingSeconds);
        _countdownTimer.Start();
    }

    private void OnCountdownTick()
    {
        if (_remainingSeconds > 1)
        {
            _remainingSeconds--;
            if (!string.IsNullOrEmpty(_pendingAction))
            {
                ApplyTileCountdownDisplay(_pendingAction, _remainingSeconds);
            }
        }
        else
        {
            _countdownTimer.Stop();
            string act = _pendingAction ?? string.Empty;
            _pendingAction = null;
            _remainingSeconds = 0;
            ResetAllTileDisplays();

            ExecutePowerAction(act);
        }
    }

    private void ApplyTileCountdownDisplay(string action, int seconds)
    {
        string cancelText = $"Cancel ({seconds}s)";
        SymbolRegular cancelIcon = SymbolRegular.Dismiss24;

        switch (action)
        {
            case "Lock":
                LockHeader = cancelText;
                LockSymbol = cancelIcon;
                LockForeground = RedBrush;
                LockTooltip = "Click to cancel locking workstation";
                break;

            case "Sleep":
                SleepHeader = cancelText;
                SleepSymbol = cancelIcon;
                SleepForeground = RedBrush;
                SleepTooltip = "Click to cancel sleep mode";
                break;

            case "Restart":
                RestartHeader = cancelText;
                RestartSymbol = cancelIcon;
                RestartForeground = RedBrush;
                RestartTooltip = "Click to cancel restart";
                break;

            case "Shutdown":
                ShutdownHeader = cancelText;
                ShutdownSymbol = cancelIcon;
                ShutdownForeground = RedBrush;
                ShutdownTooltip = "Click to cancel shut down";
                break;
        }
    }

    private void ResetAllTileDisplays()
    {
        LockHeader = "Lock";
        LockSymbol = SymbolRegular.LockClosed24;
        LockForeground = WhiteBrush;
        LockTooltip = "Lock";

        SleepHeader = "Sleep";
        SleepSymbol = SymbolRegular.WeatherMoon24;
        SleepForeground = WhiteBrush;
        SleepTooltip = "Sleep";

        RestartHeader = "Restart";
        RestartSymbol = SymbolRegular.ArrowCounterclockwise24;
        RestartForeground = WhiteBrush;
        RestartTooltip = "Restart";

        ShutdownHeader = "Shut Down";
        ShutdownSymbol = SymbolRegular.Power24;
        ShutdownForeground = WhiteBrush;
        ShutdownTooltip = "Shut Down";
    }

    private void CancelPendingCountdown()
    {
        _countdownTimer.Stop();
        _remainingSeconds = 0;
        _pendingAction = null;
        ResetAllTileDisplays();
    }

    private static void ExecutePowerAction(string action)
    {
        try
        {
            switch (action)
            {
                case "Lock":
                    Process.Start(new ProcessStartInfo("rundll32.exe", "user32.dll,LockWorkStation") { UseShellExecute = true });
                    break;

                case "Sleep":
                    Process.Start(new ProcessStartInfo("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0") { UseShellExecute = true });
                    break;

                case "Restart":
                    Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });
                    break;

                case "Shutdown":
                    Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Power command execution failed: {ex.Message}");
        }
    }

    public override void Pause()
    {
        base.Pause();
        CancelPendingCountdown();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TileModel.SpanX) or nameof(TileModel.SpanY))
        {
            OnPropertyChanged(nameof(IsCompactMode));
            OnPropertyChanged(nameof(IconFontSize));
            OnPropertyChanged(nameof(HeaderFontSize));
            OnPropertyChanged(nameof(IconMargin));
            OnPropertyChanged(nameof(TextVisibility));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Model.PropertyChanged -= OnModelPropertyChanged;
            _countdownTimer.Stop();
        }
        base.Dispose(disposing);
    }
}
