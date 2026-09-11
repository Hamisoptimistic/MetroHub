using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using MetroHub.Core.Services;

namespace MetroHub;

public partial class App : Application
{
    private const string EventName = "MetroHub_App_Wake_Event";
    private const string MutexName = "MetroHub_App_SingleInstance_Mutex";
    private static Mutex? _singleInstanceMutex;
    private static bool _ownsMutex;
    private static EventWaitHandle? _wakeEvent;
    private TaskbarIcon? _notifyIcon;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Grant permission for this process and any instances to manage foreground window
        NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);

        _singleInstanceMutex = new Mutex(true, MutexName, out bool isNewInstance);
        _ownsMutex = isNewInstance;
        if (!isNewInstance)
        {
            // Broadcast the custom registered message directly into the running instance's message queue!
            NativeMethods.PostMessage(NativeMethods.HWND_BROADCAST, NativeMethods.WM_SHOW_METROHUB, IntPtr.Zero, IntPtr.Zero);

            // Signal the fallback wake event as well
            try
            {
                using var wakeEvent = EventWaitHandle.OpenExisting(EventName);
                wakeEvent.Set();
            }
            catch { }

            Shutdown();
            return;
        }

        try
        {
            _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            Task.Run(() =>
            {
                while (_wakeEvent.WaitOne())
                {
                    Dispatcher.Invoke(() =>
                    {
                        _mainWindow?.ShowScreen();
                    });
                }
            });
        }
        catch { }

        base.OnStartup(e);

        try
        {
            _mainWindow = new MainWindow();
            _mainWindow.ShowScreen();
        }
        catch (Exception ex)
        {
            string crashLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MetroHub", "crash.log");
            File.WriteAllText(crashLog, ex.ToString());
            MessageBox.Show($"MetroHub failed to start:\n\n{ex.Message}", "MetroHub Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        InitializeTrayIcon();
    }

    private void InitializeTrayIcon()
    {
        _notifyIcon = new TaskbarIcon
        {
            ToolTipText = "MetroHub (Ctrl + ` to toggle)"
        };

        try
        {
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(iconPath))
            {
                _notifyIcon.Icon = new Icon(iconPath);
            }
            else
            {
                _notifyIcon.Icon = SystemIcons.Application;
            }
        }
        catch
        {
            _notifyIcon.Icon = SystemIcons.Application;
        }

        _notifyIcon.TrayLeftMouseDown += (s, e) =>
        {
            _mainWindow?.Dispatcher.Invoke(() => _mainWindow.ToggleVisibility());
        };

        var menu = new ContextMenu();
        var openItem = new MenuItem { Header = "Open MetroHub" };
        openItem.Click += (s, e) => _mainWindow?.Dispatcher.Invoke(() => _mainWindow.ShowScreen());

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (s, e) => _mainWindow?.Dispatcher.Invoke(() => _mainWindow.ExitApplication());

        menu.Items.Add(openItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        _notifyIcon.ContextMenu = menu;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_notifyIcon != null)
        {
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        if (_singleInstanceMutex != null)
        {
            if (_ownsMutex)
            {
                try
                {
                    _singleInstanceMutex.ReleaseMutex();
                }
                catch { }
            }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }
}
