using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using MetroHub.Core.Services;

namespace MetroHub.Presentation.Views.Settings;

/// <summary>
/// Dedicated Settings Window host for MetroHub.
/// Implements full MetroDialog DNA:
/// - Outside-click dismissal via WH_MOUSE_LL low-level mouse hook.
/// - Dynamic responsive sizing and clamping against monitor work area (960x640 ideal).
/// - MainWindow.EnterDialogScope() integration to prevent background deactivate conflicts.
/// - DWM Acrylic (DWMSBT_TRANSIENTWINDOW) composition.
/// - Titlebar drag movement and Esc key dismissal.
/// </summary>
public partial class SettingsWindow : BorderlessFluentWindow
{
    private IDisposable? _dialogScope;
    private long _shownTimestamp;
    private bool _isClosing;
    private bool _isDismissing;

    protected override bool EnableToolWindowStyle => true;

    public SettingsWindow()
    {
        WindowCornerPreference = Wpf.Ui.Controls.WindowCornerPreference.DoNotRound;
        InitializeComponent();
        DataContext = new SettingsShellViewModel();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _dialogScope = MainWindow.EnterDialogScope();
        _shownTimestamp = Environment.TickCount64;

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            // Responsive sizing: clamp to fit comfortably on smaller displays or high DPI scaling
            var workArea = NativeMethods.GetActiveMonitorWorkArea();
            if (workArea.Width <= 0 || workArea.Height <= 0)
            {
                workArea = new Rect(0, 0, SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);
            }

            if (workArea.Width > 0 && workArea.Height > 0)
            {
                Width = Math.Min(960, Math.Max(720, workArea.Width * 0.90));
                Height = Math.Min(640, Math.Max(460, workArea.Height * 0.85));
            }

            if (Owner != null && Owner.ActualWidth > 0 && Owner.ActualHeight > 0)
            {
                Left = Owner.Left + (Owner.ActualWidth - Width) / 2;
                Top = Owner.Top + (Owner.ActualHeight - Height) / 2;
            }
            else if (workArea.Width > 0 && workArea.Height > 0)
            {
                Left = workArea.Left + (workArea.Width - Width) / 2;
                Top = workArea.Top + (workArea.Height - Height) / 2;
            }

            // Ensure window stays strictly within workArea bounds
            if (workArea.Width > 0 && workArea.Height > 0)
            {
                Left = Math.Max(workArea.Left, Math.Min(Left, workArea.Right - Width));
                Top = Math.Max(workArea.Top, Math.Min(Top, workArea.Bottom - Height));
            }

            // Apply native Windows 11 DWM Acrylic composition (DWMSBT_TRANSIENTWINDOW = 3)
            int cornerPref = WindowCornerPreference switch
            {
                Wpf.Ui.Controls.WindowCornerPreference.Round => NativeMethods.DWMWCP_ROUND,
                Wpf.Ui.Controls.WindowCornerPreference.RoundSmall => NativeMethods.DWMWCP_ROUNDSMALL,
                _ => NativeMethods.DWMWCP_DONOTROUND
            };
            NativeMethods.ApplyMica(hwnd, dark: true, NativeMethods.DWMSBT_TRANSIENTWINDOW, cornerPref);
        }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _shownTimestamp = Environment.TickCount64;
    }



    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // If in detail view, Esc steps back to Home first; a second Esc closes the window.
            if (DataContext is SettingsShellViewModel shell && shell.IsInDetailView)
            {
                shell.NavigateHome();
                e.Handled = true;
                return;
            }

            Dismiss();
            e.Handled = true;
            return;
        }

        // Standard Windows navigation: Alt+Left or BrowserBack navigates back to Home
        if ((e.Key == Key.Left && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt) ||
            e.Key == Key.BrowserBack)
        {
            if (DataContext is SettingsShellViewModel shell && shell.IsInDetailView)
            {
                shell.NavigateHome();
                e.Handled = true;
                return;
            }
        }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        // Mouse Back button (XButton1) navigates back to Home
        if (e.ChangedButton == MouseButton.XButton1)
        {
            if (DataContext is SettingsShellViewModel shell && shell.IsInDetailView)
            {
                shell.NavigateHome();
                e.Handled = true;
                return;
            }
        }

        base.OnPreviewMouseDown(e);
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Dismiss();
    }

    public void Dismiss()
    {
        if (_isDismissing || _isClosing) return;
        _isClosing = true;
        _isDismissing = true;

        try
        {
            if (ComponentDispatcher.IsThreadModal)
            {
                DialogResult = false;
            }
        }
        catch
        {
            // Ignore if not modal or cannot set DialogResult
        }

        try
        {
            Close();
        }
        catch
        {
            // Ignore if already closing
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _isClosing = true;
        _isDismissing = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosing = true;
        _isDismissing = true;
        base.OnClosed(e);
        _dialogScope?.Dispose();
        _dialogScope = null;
    }
}
