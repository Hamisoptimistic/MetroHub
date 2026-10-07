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
    private NativeMethods.LowLevelMouseProc? _mouseHookProc;
    private IntPtr _mouseHook = IntPtr.Zero;
    private long _shownTimestamp;
    private bool _isClosing;
    private bool _isDismissing;

    protected override bool EnableToolWindowStyle => true;

    public SettingsWindow()
    {
        InitializeComponent();
        DataContext = new SettingsShellViewModel();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _dialogScope = MainWindow.EnterDialogScope();
        _shownTimestamp = Environment.TickCount64;

        InstallMouseHook();

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
            NativeMethods.ApplyMica(hwnd, dark: true, NativeMethods.DWMSBT_TRANSIENTWINDOW);
        }
    }

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return;
        _mouseHookProc = LowLevelMouseHookCallback;
        _mouseHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL,
            _mouseHookProc,
            IntPtr.Zero,
            0);
    }

    private void UninstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
            _mouseHookProc = null;
        }
    }

    private IntPtr LowLevelMouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)NativeMethods.WM_LBUTTONDOWN ||
                           wParam == (IntPtr)NativeMethods.WM_NCLBUTTONDOWN ||
                           wParam == (IntPtr)NativeMethods.WM_RBUTTONDOWN ||
                           wParam == (IntPtr)NativeMethods.WM_NCRBUTTONDOWN))
        {
            if (Environment.TickCount64 - _shownTimestamp >= 200 && !_isDismissing && !_isClosing && IsLoaded)
            {
                var hookStruct = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    IntPtr clickedWnd = NativeMethods.WindowFromPoint(hookStruct.pt);
                    if (clickedWnd != IntPtr.Zero)
                    {
                        if (clickedWnd == hwnd || NativeMethods.GetAncestor(clickedWnd, NativeMethods.GA_ROOTOWNER) == hwnd)
                        {
                            return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
                        }
                    }

                    if (NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect))
                    {
                        int x = hookStruct.pt.X;
                        int y = hookStruct.pt.Y;
                        bool isInside = x >= rect.Left && x <= rect.Right && y >= rect.Top && y <= rect.Bottom;
                        if (!isInside)
                        {
                            Dispatcher.InvokeAsync(Dismiss);
                        }
                    }
                }
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _shownTimestamp = Environment.TickCount64;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (Environment.TickCount64 - _shownTimestamp >= 200 && !_isDismissing && !_isClosing && IsLoaded)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (!_isDismissing && !_isClosing && IsLoaded)
                {
                    IntPtr hwnd = new WindowInteropHelper(this).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        IntPtr activeWnd = NativeMethods.GetActiveWindow();
                        if (activeWnd != IntPtr.Zero && (activeWnd == hwnd || NativeMethods.GetAncestor(activeWnd, NativeMethods.GA_ROOTOWNER) == hwnd))
                        {
                            return;
                        }
                    }

                    Dismiss();
                }
            });
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Dismiss();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
            }
        }
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
        UninstallMouseHook();

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
        UninstallMouseHook();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosing = true;
        _isDismissing = true;
        UninstallMouseHook();
        base.OnClosed(e);
        _dialogScope?.Dispose();
        _dialogScope = null;
    }
}
