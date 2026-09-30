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
    private static RegisteredWaitHandle? _registeredWakeHandle;
    private TaskbarIcon? _notifyIcon;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Register native BASS audio engine dynamic library resolver
        MetroHub.Core.Radio.BassLoader.Register();

        // Global crash logging
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            try
            {
                string crashLog = MetroHub.Core.Services.AppPaths.CrashLogPath;
                MetroHub.Core.Services.AppPaths.EnsureDirectory(crashLog);
                string entry = $"[{System.DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [AppDomain.UnhandledException]{System.Environment.NewLine}{args.ExceptionObject ?? "Unknown unhandled exception"}{System.Environment.NewLine}{System.Environment.NewLine}";
                File.AppendAllText(crashLog, entry);
            }
            catch { }
        };

        DispatcherUnhandledException += (s, args) =>
        {
            try
            {
                string crashLog = MetroHub.Core.Services.AppPaths.CrashLogPath;
                MetroHub.Core.Services.AppPaths.EnsureDirectory(crashLog);
                string entry = $"[{System.DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [DispatcherUnhandledException]{System.Environment.NewLine}{args.Exception}{System.Environment.NewLine}{System.Environment.NewLine}";
                File.AppendAllText(crashLog, entry);
            }
            catch { }
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            try
            {
                string crashLog = MetroHub.Core.Services.AppPaths.CrashLogPath;
                MetroHub.Core.Services.AppPaths.EnsureDirectory(crashLog);
                string entry = $"[{System.DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [TaskScheduler.UnobservedTaskException]{System.Environment.NewLine}{args.Exception}{System.Environment.NewLine}{System.Environment.NewLine}";
                File.AppendAllText(crashLog, entry);
                args.SetObserved();
            }
            catch { }
        };

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Grant permission for this process and any instances to manage foreground window
        NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);

        _singleInstanceMutex = new Mutex(true, MutexName, out bool isNewInstance);
        _ownsMutex = isNewInstance;
        if (!isNewInstance)
        {
            // Signal the existing running instance's wake event
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
            _registeredWakeHandle = ThreadPool.RegisterWaitForSingleObject(
                _wakeEvent,
                (state, timedOut) =>
                {
                    if (!timedOut)
                    {
                        Dispatcher.InvokeAsync(() =>
                        {
                            _mainWindow?.ShowScreen();
                        });
                    }
                },
                null,
                -1,
                false);
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
            string crashLog = MetroHub.Core.Services.AppPaths.CrashLogPath;
            MetroHub.Core.Services.AppPaths.EnsureDirectory(crashLog);
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

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Safe.Try(() => _mainWindow?.SaveGroupsAndLayout(), "App.OnSessionEnding.SaveGroupsAndLayout");
        Safe.Try(StorageService.Flush, "App.OnSessionEnding.StorageFlush");

        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Safe.Try(() => _mainWindow?.SaveGroupsAndLayout(), "App.OnExit.SaveGroupsAndLayout");
        Safe.Try(StorageService.Flush, "App.OnExit.StorageFlush");

        if (_notifyIcon != null)
        {
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        if (_registeredWakeHandle != null)
        {
            _registeredWakeHandle.Unregister(null);
            _registeredWakeHandle = null;
        }

        if (_wakeEvent != null)
        {
            _wakeEvent.Dispose();
            _wakeEvent = null;
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
