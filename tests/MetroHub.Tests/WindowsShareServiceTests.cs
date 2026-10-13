using System;
using System.Runtime.InteropServices;
using Xunit;
using MetroHub.Core.Models;
using MetroHub.Core.Services;

namespace MetroHub.Tests;

public class WindowsShareServiceTests
{
    [Fact]
    public void CanShare_FilteringRules_AreStrict()
    {
        var webTile = new TileModel
        {
            Title = "Google",
            TargetPath = "https://google.com",
            TileType = TileType.WebUrl
        };
        Assert.True(WindowsShareService.CanShare(webTile));

        var widgetTile = new TileModel
        {
            Title = "Clock",
            TileType = TileType.Widget
        };
        Assert.False(WindowsShareService.CanShare(widgetTile));

        var folderTile = new TileModel
        {
            Title = "Work Folder",
            TileType = TileType.Folder
        };
        Assert.False(WindowsShareService.CanShare(folderTile));

        var appTile = new TileModel
        {
            Title = "Registry Editor",
            TargetPath = @"C:\Windows\regedit.exe",
            TileType = TileType.App
        };
        Assert.False(WindowsShareService.CanShare(appTile));

        var lnkTile = new TileModel
        {
            Title = "Chrome Shortcut",
            TargetPath = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Chrome.lnk",
            TileType = TileType.App
        };
        Assert.False(WindowsShareService.CanShare(lnkTile));
    }

    [Fact]
    public void RoGetActivationFactory_CheckDirectly()
    {
        // Check if RoGetActivationFactory can be called and what it returns
        try
        {
            var interop = WindowsShareService.GetInteropForTesting();
            Assert.NotNull(interop);
        }
        catch (Exception ex)
        {
            Assert.Fail($"RoGetActivationFactory failed with: {ex.GetType().FullName}: {ex.Message}");
        }
    }

    [Fact]
    public void EnsureDataTransferManager_BindsSuccessfully_OnStaHwnd()
    {
        Exception? testException = null;
        var originalLogger = Safe.Logger;
        Safe.Logger = (ex, ctx) => testException = new Exception($"[Safe.Log: {ctx}] {ex.GetType().FullName}: {ex.Message}", ex);

        try
        {
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    var win = new System.Windows.Window();
                    var helper = new System.Windows.Interop.WindowInteropHelper(win);
                    IntPtr hwnd = helper.EnsureHandle();
                    Assert.NotEqual(IntPtr.Zero, hwnd);

                    var tile = new TileModel { Title = "Test Tile", TargetPath = "https://example.com", TileType = TileType.WebUrl };
                    WindowsShareService.ShowShareUI(tile, win);

                    var interop = (IDataTransferManagerInterop)WindowsShareService.GetInteropForTesting();
                    Guid iid = new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);
                    IntPtr dtmPtr = interop.GetForWindow(hwnd, ref iid);
                    Assert.NotEqual(IntPtr.Zero, dtmPtr);

                    var dtm = WinRT.MarshalInterface<Windows.ApplicationModel.DataTransfer.DataTransferManager>.FromAbi(dtmPtr);
                    Assert.NotNull(dtm);
                }
                catch (Exception ex)
                {
                    testException = ex;
                }
            });

            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (testException != null)
            {
                Assert.Fail($"ShowShareUI on STA HWND threw: {testException.GetType().FullName}: {testException.Message}\n{testException.StackTrace}");
            }
        }
        finally
        {
            Safe.Logger = originalLogger;
        }
    }
}
