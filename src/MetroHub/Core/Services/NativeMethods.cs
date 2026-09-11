using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace MetroHub.Core.Services;

public static class NativeMethods
{
    #region DWM Mica & Backdrop

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;

        public MARGINS(int left, int right, int top, int bottom)
        {
            cxLeftWidth = left;
            cxRightWidth = right;
            cyTopHeight = top;
            cyBottomHeight = bottom;
        }
    }

    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_DONOTROUND = 1;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    public const int DWMSBT_AUTO = 0;
    public const int DWMSBT_NONE = 1;
    public const int DWMSBT_MAINWINDOW = 2; // Mica
    public const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic
    public const int DWMSBT_TABBEDWINDOW = 4; // Mica Alt

    public static void ApplyMica(IntPtr hwnd, bool dark = true, int backdrop = DWMSBT_MAINWINDOW)
    {
        try
        {
            int darkVal = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkVal, sizeof(int)) != 0)
            {
                int oldDarkAttr = 19;
                DwmSetWindowAttribute(hwnd, oldDarkAttr, ref darkVal, sizeof(int));
            }

            // Suppress rounded corners (force clean square corners)
            int cornerVal = DWMWCP_DONOTROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerVal, sizeof(int));

            // Suppress the 1px DWM window border completely (DWMWA_COLOR_NONE = 0xFFFFFFFE)
            int borderVal = DWMWA_COLOR_NONE;
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderVal, sizeof(int));

            MARGINS margins = new MARGINS(-1, -1, -1, -1);
            DwmExtendFrameIntoClientArea(hwnd, ref margins);

            int backdropVal = backdrop;
            int res = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropVal, sizeof(int));
            if (res != 0)
            {
                // Fallback for Windows 11 Build 22000 (21H2)
                int trueVal = 1;
                DwmSetWindowAttribute(hwnd, 1029, ref trueVal, sizeof(int));
            }

            // Inform DWM that the non-client frame and backdrop metrics have changed.
            // This forces DWM to recompute composition and apply the backdrop immediately on initial launch.
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        catch { }
    }

    #endregion

    #region Working Set Trimming

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    public static void FlushMemory()
    {
        Task.Run(() =>
        {
            try
            {
                GC.Collect(2, GCCollectionMode.Forced, false);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, false);
                EmptyWorkingSet(Process.GetCurrentProcess().Handle);
            }
            catch { }
        });
    }

    #endregion

    #region Hotkeys

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    #endregion

    #region Monitor & Work Area

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, int flags);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    public const int MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public static Rect GetActiveMonitorWorkArea()
    {
        try
        {
            GetCursorPos(out POINT cursor);
            IntPtr hMonitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
            MONITORINFO mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));

            if (GetMonitorInfo(hMonitor, ref mi))
            {
                return new Rect(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Width, mi.rcWork.Height);
            }
        }
        catch { }

        return SystemParameters.WorkArea;
    }

    #endregion

    #region Process Launching

    private static readonly object _launchLock = new();
    private static string? _lastLaunchKey;
    private static DateTime _lastLaunchTime = DateTime.MinValue;

    public static bool LaunchTarget(string path, string? args = null, bool runAsAdmin = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string launchKey = $"{path.Trim()}|{args?.Trim()}|{runAsAdmin}";
        lock (_launchLock)
        {
            var now = DateTime.UtcNow;
            if (string.Equals(_lastLaunchKey, launchKey, StringComparison.OrdinalIgnoreCase) &&
                (now - _lastLaunchTime).TotalMilliseconds < 800)
            {
                return false;
            }

            _lastLaunchKey = launchKey;
            _lastLaunchTime = now;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                Arguments = args ?? string.Empty,
                UseShellExecute = true
            };

            if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = "explorer.exe";
                psi.Arguments = $"\"{path}\"";
            }

            if (runAsAdmin)
            {
                psi.Verb = "runas";
            }

            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Window Activation & Foreground

    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(int dwProcessId);

    public const int ASFW_ANY = -1;

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    public const int SW_SHOW = 5;

    public static void ForceForeground(IntPtr hWnd)
    {
        try
        {
            IntPtr foreHwnd = GetForegroundWindow();
            uint foreThread = 0;
            if (foreHwnd != IntPtr.Zero)
            {
                foreThread = GetWindowThreadProcessId(foreHwnd, out _);
            }
            uint currentThread = GetCurrentThreadId();

            if (foreThread != 0 && foreThread != currentThread)
            {
                AttachThreadInput(currentThread, foreThread, true);
                ShowWindow(hWnd, SW_SHOW);
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
                AttachThreadInput(currentThread, foreThread, false);
            }
            else
            {
                ShowWindow(hWnd, SW_SHOW);
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
            }
        }
        catch { }
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xffff);
    public static readonly uint WM_SHOW_METROHUB = RegisterWindowMessage("WM_SHOW_METROHUB_WAKE");

    public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    #endregion
}
