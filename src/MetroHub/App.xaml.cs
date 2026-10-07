using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using MetroHub.Core.Services;
using MetroHub.Presentation.Controllers;

namespace MetroHub;

public partial class App : Application
{
    private const string EventName = "MetroHub_App_Wake_Event";
    private const string MutexName = "MetroHub_App_SingleInstance_Mutex";
    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _wakeEvent;
    private RegisteredWaitHandle? _registeredWakeHandle;
    private TaskbarIcon? _notifyIcon;
    private MainWindow? _mainWindow;

    private static int _unhandledExceptionCount;
    private static DateTime _lastUnhandledExceptionTime = DateTime.MinValue;

    private static void LogCrash(string source, object? exception)
    {
        try
        {
            if (exception is Exception ex)
            {
                Serilog.Log.Fatal(ex, "Fatal crash in [{Source}]", source);
            }
            else
            {
                Serilog.Log.Fatal("Fatal crash in [{Source}]: {Exception}", source, exception);
            }

            string crashLog = AppPaths.CrashLogPath;
            AppPaths.EnsureDirectory(crashLog);

            var fi = new FileInfo(crashLog);
            if (fi.Exists && fi.Length > 2 * 1024 * 1024)
            {
                string oldLog = crashLog + ".old";
                if (File.Exists(oldLog)) File.Delete(oldLog);
                File.Move(crashLog, oldLog);
            }

            string entry = $"[{System.DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}]{System.Environment.NewLine}{exception ?? "Unknown unhandled exception"}{System.Environment.NewLine}{System.Environment.NewLine}";
            File.AppendAllText(crashLog, entry);

            if (exception is Exception exObj)
            {
                Safe.Log(source, exObj);
            }
        }
        catch
        {
            // Crash logging must never throw
        }
    }

    private static bool ShouldHandleDispatcherException()
    {
        var now = System.DateTime.UtcNow;
        if ((now - _lastUnhandledExceptionTime).TotalSeconds < 2)
        {
            _unhandledExceptionCount++;
            if (_unhandledExceptionCount > 4)
            {
                return false; // Cascading loop: allow process to terminate cleanly
            }
        }
        else
        {
            _unhandledExceptionCount = 1;
        }
        _lastUnhandledExceptionTime = now;
        return true;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Initialize centralized Serilog logging pipeline
        LoggingService.Initialize();

        // Register native BASS audio engine dynamic library resolver
        MetroHub.Core.Radio.BassLoader.Register();

        // Global crash logging
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            LogCrash("AppDomain.UnhandledException", args.ExceptionObject);
        };

        DispatcherUnhandledException += (s, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);

            if (ShouldHandleDispatcherException())
            {
                args.Handled = true;
                _mainWindow?.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        _mainWindow?.ShowToast($"Recovered from error: {args.Exception.Message}", isError: true);
                    }
                    catch (Exception ex)
                    {
                        // Suppress toast failure during crash recovery so it does not trigger a secondary crash
                        Serilog.Log.Debug(ex, "Failed to display unhandled exception toast notification");
                    }
                });
            }
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            LogCrash("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
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
            catch (Exception ex)
            {
                // First instance may be shutting down; proceed with shutdown cleanly
                Serilog.Log.Debug(ex, "Could not signal existing instance wake event");
            }

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
        catch (Exception ex)
        {
            // If named wait handle registration fails, continue booting without cross-process wake activation
            Serilog.Log.Warning(ex, "Failed to register single-instance wake event listener");
        }

        base.OnStartup(e);

        // Dynamically detect and synchronize Windows accent color with application theme
        SystemAccentColorService.Initialize();

        MetroHub.Core.Models.TileModel.WidgetViewModelFactory = MetroHub.Widgets.Registry.WidgetRegistry.CreateViewModelForTile;

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
            ToolTipText = "MetroHub (Ctrl + ` to toggle)",
            Icon = LoadTrayIcon()
        };

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

    /// <summary>
    /// Loads the tray icon at the exact frame the notification area asks for.
    /// Extracting the icon from the executable only yields its default 32 px
    /// frame, which the shell then has to rescale down to 16 px (100 % DPI) and
    /// blurs on the way. Assets/app.ico carries a hand-tuned frame per size, so
    /// the resource stream is the preferred source.
    /// </summary>
    private static Icon LoadTrayIcon()
    {
        try
        {
            var streamInfo = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (streamInfo?.Stream is { } stream)
            {
                using (stream)
                {
                    int size = NativeMethods.SmallIconSize;
                    return new Icon(stream, new System.Drawing.Size(size, size));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            Serilog.Log.Debug(ex, "Tray icon resource could not be read");
        }

        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                return Icon.ExtractAssociatedIcon(exePath) ?? SystemIcons.Application;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            Serilog.Log.Debug(ex, "Tray icon could not be extracted from the executable");
        }

        return SystemIcons.Application;
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Safe.Try(() => _mainWindow?.SaveGroupsAndLayout(), "App.OnSessionEnding.SaveGroupsAndLayout");
        Safe.Try(() => WorkspaceManager.Instance.FlushSync(), "App.OnSessionEnding.WorkspaceFlush");
        Safe.Try(StorageService.Flush, "App.OnSessionEnding.StorageFlush");
        LoggingService.Shutdown();

        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Safe.Try(() => _mainWindow?.SaveGroupsAndLayout(), "App.OnExit.SaveGroupsAndLayout");
        Safe.Try(() => WorkspaceManager.Instance.FlushSync(), "App.OnExit.WorkspaceFlush");
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
                catch (ApplicationException ex)
                {
                    // Mutex was already released or not owned on this thread
                    Serilog.Log.Debug(ex, "Mutex was not owned or already released on exit");
                }
            }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        LoggingService.Shutdown();

        base.OnExit(e);
    }
}
