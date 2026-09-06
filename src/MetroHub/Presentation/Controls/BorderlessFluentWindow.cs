using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;
using MetroHub.Core.Services;
using Wpf.Ui.Controls;

namespace MetroHub.Presentation.Controls;

/// <summary>
/// A specialized FluentWindow that suppresses Windows 11 DWM non-client accent borders,
/// disables corner rounding, and ensures a completely borderless experience with Mica backdrop.
/// </summary>
public class BorderlessFluentWindow : FluentWindow
{
    private const int WM_ACTIVATE = 0x0006;
    private const int WM_NCACTIVATE = 0x0086;

    static BorderlessFluentWindow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(BorderlessFluentWindow),
            new FrameworkPropertyMetadata(typeof(FluentWindow)));
    }

    public BorderlessFluentWindow()
    {
        WindowCornerPreference = WindowCornerPreference.DoNotRound;
        WindowStyle = WindowStyle.None;
        BorderThickness = new Thickness(0);
        BorderBrush = System.Windows.Media.Brushes.Transparent;
        Background = System.Windows.Media.Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
    }

    protected override void OnBackdropTypeChanged(WindowBackdropType oldValue, WindowBackdropType newValue)
    {
        // Suppress WPF-UI's built-in backdrop manager which resets Background to solid #202020
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == BackgroundProperty && Background != System.Windows.Media.Brushes.Transparent)
        {
            SetCurrentValue(BackgroundProperty, System.Windows.Media.Brushes.Transparent);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        Background = System.Windows.Media.Brushes.Transparent;

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var source = HwndSource.FromHwnd(hwnd);
            if (source?.CompositionTarget != null)
            {
                source.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
            }
            source?.AddHook(HwndMessageHook);
        }

        ApplyBorderlessAttributes();

        // Enforce zero resize border and zero frame in WindowChrome so WPF-UI chrome does not draw edges
        var chrome = WindowChrome.GetWindowChrome(this);
        if (chrome != null)
        {
            chrome.ResizeBorderThickness = new Thickness(0);
            chrome.CaptionHeight = 0;
            chrome.CornerRadius = new CornerRadius(0);
            chrome.GlassFrameThickness = new Thickness(-1);
            chrome.NonClientFrameEdges = NonClientFrameEdges.None;
        }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        // Immediately suppress DWM accent border after WPF-UI / OS activation runs
        ApplyBorderlessAttributes();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        // Immediately suppress DWM accent border on deactivation
        ApplyBorderlessAttributes();
    }

    private IntPtr HwndMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCACTIVATE || msg == WM_ACTIVATE)
        {
            ApplyBorderlessAttributes();
        }

        return IntPtr.Zero;
    }

    public void ApplyBorderlessAttributes()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // 1. Force sharp corners (no rounded outer border)
        int cornerVal = NativeMethods.DWMWCP_DONOTROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerVal, sizeof(int));

        // 2. Suppress 1px DWM window border completely (DWMWA_COLOR_NONE = 0xFFFFFFFE)
        int borderVal = NativeMethods.DWMWA_COLOR_NONE;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref borderVal, sizeof(int));
    }
}
