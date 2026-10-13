using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using MetroHub.Core.Models;

namespace MetroHub.Core.Services;

/// <summary>
/// COM interop interface to interact with Windows 10/11 DataTransferManager from Win32/WPF desktop HWND.
/// </summary>
[ComImport]
[Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDataTransferManagerInterop
{
    IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
    void ShowShareUIForWindow([In] IntPtr appWindow);
}

/// <summary>
/// Provides native Windows 11 Share flyout integration for MetroHub tiles (Web links, Images, Notes, and Files).
/// Completely offline, zero open network ports, 100% OS-managed via WinRT DataTransferManager.
/// </summary>
public static class WindowsShareService
{
    private static readonly Guid DataTransferManagerIid = new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    // Track active target tile for the current share request
    private static TileModel? _activeTile;
    private static DataTransferManager? _dtm;
    private static IntPtr _registeredHwnd = IntPtr.Zero;

    private static readonly System.Collections.Generic.HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".bat", ".cmd", ".msc", ".msi", ".dll", ".cpl", ".scr", ".ps1", ".vbs", ".reg"
    };

    /// <summary>
    /// Checks if a tile can be shared via the native Windows Share contract.
    /// Only allows Web URLs, Notes, and genuine user content files (images, documents).
    /// Disallows widgets, folders, applications, and executable tools.
    /// </summary>
    public static bool CanShare(TileModel? tile)
    {
        if (tile == null) return false;
        if (tile.TileType == TileType.Widget || tile.TileType == TileType.Folder) return false;

        // Web URLs & Notes with web source
        if (tile.TileType == TileType.WebUrl || tile.HasWebSource) return true;

        // Plain Text Notes
        if (tile.IsNote) return true;

        // Local content files (e.g. Pasted images, screenshots, documents)
        if (!string.IsNullOrWhiteSpace(tile.TargetPath) && File.Exists(tile.TargetPath))
        {
            string ext = Path.GetExtension(tile.TargetPath);
            if (string.IsNullOrEmpty(ext) || BlockedExtensions.Contains(ext))
            {
                return false;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Invokes the native Windows 11 Share UI flyout for the specified tile.
    /// </summary>
    public static void ShowShareUI(TileModel tile, Window? ownerWindow = null)
    {
        if (!CanShare(tile)) return;

        ownerWindow ??= Application.Current?.MainWindow;
        if (ownerWindow == null) return;

        IntPtr hwnd = new WindowInteropHelper(ownerWindow).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            _activeTile = tile;
            EnsureDataTransferManager(hwnd);

            // Cast to interop interface and display the native Windows Share pane
            var interop = (IDataTransferManagerInterop)MarshalInterfaceHelper.GetInterop();
            interop.ShowShareUIForWindow(hwnd);
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "WindowsShareService: Failed to display native Share UI");
        }
    }

    private static void EnsureDataTransferManager(IntPtr hwnd)
    {
        if (_dtm != null && _registeredHwnd == hwnd) return;

        try
        {
            if (_dtm != null)
            {
                try { _dtm.DataRequested -= OnDataRequested; } catch { }
                _dtm = null;
            }

            var interop = (IDataTransferManagerInterop)MarshalInterfaceHelper.GetInterop();
            Guid iid = DataTransferManagerIid;
            IntPtr dtmPtr = interop.GetForWindow(hwnd, ref iid);

            _dtm = WinRT.MarshalInterface<DataTransferManager>.FromAbi(dtmPtr);
            _registeredHwnd = hwnd;
            _dtm.DataRequested += OnDataRequested;
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "WindowsShareService: Failed to bind DataTransferManager to HWND");
        }
    }

    private static void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        var tile = _activeTile;
        if (tile == null) return;

        var request = args.Request;
        var data = request.Data;

        string title = !string.IsNullOrWhiteSpace(tile.Title) ? tile.Title : "MetroHub Tile";
        data.Properties.Title = title;
        data.Properties.Description = "Shared from MetroHub";

        // Case 1: Strict Web Link or Note with Web Source
        if (tile.TileType == TileType.WebUrl && !string.IsNullOrWhiteSpace(tile.TargetPath))
        {
            string url = tile.TargetPath.Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }

            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? webUri))
            {
                data.SetWebLink(webUri);
                data.SetText(webUri.AbsoluteUri);
                return;
            }
        }

        if (tile.HasWebSource && !string.IsNullOrWhiteSpace(tile.Arguments) &&
            Uri.TryCreate(tile.Arguments, UriKind.Absolute, out Uri? srcUri))
        {
            data.SetWebLink(srcUri);
            data.SetText(srcUri.AbsoluteUri);
            return;
        }

        // Case 2: Note Tile (Plain Text)
        if (tile.IsNote && File.Exists(tile.TargetPath))
        {
            try
            {
                string noteText = File.ReadAllText(tile.TargetPath);
                data.SetText(noteText);
                return;
            }
            catch (Exception ex)
            {
                Safe.Log(ex, "WindowsShareService: Failed to read note text for share");
            }
        }

        // Case 3: Local File or Image on disk (e.g. Pasted screenshot, image, document)
        if (!string.IsNullOrWhiteSpace(tile.TargetPath) && File.Exists(tile.TargetPath))
        {
            var deferral = request.GetDeferral();
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    string fullPath = Path.GetFullPath(tile.TargetPath);
                    StorageFile storageFile = await StorageFile.GetFileFromPathAsync(fullPath);
                    data.SetStorageItems(new[] { storageFile });
                }
                catch (Exception ex)
                {
                    Safe.Log(ex, "WindowsShareService: Failed to set storage items for file share");
                    if (!string.IsNullOrWhiteSpace(tile.TargetPath))
                    {
                        data.SetText(tile.TargetPath);
                    }
                }
                finally
                {
                    deferral.Complete();
                }
            });
            return;
        }

        // Fallback: Share whatever TargetPath or Title we have as text
        if (!string.IsNullOrWhiteSpace(tile.TargetPath))
        {
            data.SetText(tile.TargetPath);
        }
    }

    internal static object GetInteropForTesting() => MarshalInterfaceHelper.GetInterop();

    private static class MarshalInterfaceHelper
    {
        [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int WindowsCreateString(
            string sourceString,
            int length,
            out IntPtr hstring);

        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int WindowsDeleteString(IntPtr hstring);

        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int RoGetActivationFactory(
            IntPtr activatableClassId,
            [In] ref Guid iid,
            out IntPtr factory);

        private static readonly Guid IDataTransferManagerInteropIid = typeof(IDataTransferManagerInterop).GUID;

        public static object GetInterop()
        {
            const string classId = "Windows.ApplicationModel.DataTransfer.DataTransferManager";
            int hr = WindowsCreateString(classId, classId.Length, out IntPtr hstring);
            if (hr != 0 || hstring == IntPtr.Zero)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            try
            {
                Guid iid = IDataTransferManagerInteropIid;
                hr = RoGetActivationFactory(hstring, ref iid, out IntPtr factoryPtr);
                if (hr != 0 || factoryPtr == IntPtr.Zero)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                return Marshal.GetObjectForIUnknown(factoryPtr);
            }
            finally
            {
                WindowsDeleteString(hstring);
            }
        }
    }
}
