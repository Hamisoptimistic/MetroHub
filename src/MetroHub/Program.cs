using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace MetroHub;

public static class Program
{
    public const string MutexName = @"Local\MetroHub_App_SingleInstance_Mutex";
    public const string EventName = @"Local\MetroHub_App_Wake_Event";

    private static Mutex? _instanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        bool createdNew;
        try
        {
            _instanceMutex = new Mutex(true, MutexName, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // Another instance exists with higher or different integrity level (e.g. UAC)
            createdNew = false;
        }
        catch (Exception)
        {
            // Fallback to allow app launch if mutex creation fails unexpectedly
            createdNew = true;
        }

        if (!createdNew)
        {
            // Allow primary instance to bring itself to the foreground
            _ = StartupNative.AllowSetForegroundWindow(StartupNative.ASFW_ANY);

            // Signal wake event to existing primary instance
            try
            {
                if (EventWaitHandle.TryOpenExisting(EventName, out var wakeEvent))
                {
                    using (wakeEvent)
                    {
                        wakeEvent.Set();
                    }
                }
            }
            catch (Exception)
            {
                // Silently ignore wake signal failures
            }

            // Immediately dismiss Windows Explorer's IDC_APPSTARTING wait cursor
            _ = StartupNative.PeekMessage(out _, IntPtr.Zero, 0, 0, StartupNative.PM_NOREMOVE);

            ReleaseSingleInstanceMutex();
            return;
        }

        try
        {
            RunApplication();
        }
        finally
        {
            ReleaseSingleInstanceMutex();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunApplication()
    {
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    public static void ReleaseSingleInstanceMutex()
    {
        var mutex = Interlocked.Exchange(ref _instanceMutex, null);
        if (mutex != null)
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch (Exception)
            {
                // Mutex may not be owned or already released
            }
            finally
            {
                mutex.Dispose();
            }
        }
    }

    private static class StartupNative
    {
        public const int ASFW_ANY = -1;
        public const uint PM_NOREMOVE = 0x0000;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr Hwnd;
            public uint Message;
            public UIntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public POINT Pt;
            public uint Private;
        }

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AllowSetForegroundWindow(int dwProcessId);

        [DllImport("user32.dll", EntryPoint = "PeekMessageW", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PeekMessage(
            out MSG lpMsg,
            IntPtr hWnd,
            uint wMsgFilterMin,
            uint wMsgFilterMax,
            uint wRemoveMsg);
    }
}
