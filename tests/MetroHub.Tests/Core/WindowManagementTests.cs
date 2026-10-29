using System;
using System.Linq;
using System.Windows;
using MetroHub.Core.Services;
using MetroHub.Presentation.Dialogs;
using MetroHub.Presentation.Views;
using Xunit;

namespace MetroHub.Tests;

/// <summary>
/// Unit tests for native window styles, taskbar exclusion, and modal dialog dismissal.
/// </summary>
public class WindowManagementTests
{
    [Fact]
    public void BorderlessFluentWindow_HasShowInTaskbarFalseByDefault()
    {
        WpfTestHost.RunSta(() =>
        {
            var window = new BorderlessFluentWindow();
            Assert.False(window.ShowInTaskbar);
            Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
            Assert.Equal(WindowStyle.None, window.WindowStyle);
        });
    }

    [Fact]
    public void MetroDialog_HasShowInTaskbarFalseByDefault()
    {
        WpfTestHost.RunSta(() =>
        {
            var dialog = new WebLinkDialog();
            Assert.False(dialog.ShowInTaskbar);
        });
    }

    [Fact]
    public void MetroDialog_DismissDialog_CanBeCalledSafelyMultipleTimes()
    {
        WpfTestHost.RunSta(() =>
        {
            var dialog = new WebLinkDialog();
            dialog.DismissDialog();
            dialog.DismissDialog();
            Assert.True(dialog.IsDismissing);
        });
    }

    [Fact]
    public void WindowCollection_SnapshotDismissal_DoesNotThrowCollectionModifiedException()
    {
        WpfTestHost.RunSta(() =>
        {
            var dlg1 = new WebLinkDialog();
            var dlg2 = new WebLinkDialog();
            var openWindows = new Window[] { dlg1, dlg2 }.ToList();

            var exception = Record.Exception(() =>
            {
                foreach (var window in openWindows)
                {
                    if (window is MetroDialog dialog)
                    {
                        dialog.DismissDialog();
                    }
                }
            });

            Assert.Null(exception);
            Assert.True(dlg1.IsDismissing);
            Assert.True(dlg2.IsDismissing);
        });
    }

    [Fact]
    public void ForceForeground_HandlesZeroHwndWithoutCrashing()
    {
        var exception = Record.Exception(() =>
        {
            NativeMethods.ForceForeground(IntPtr.Zero);
        });
        Assert.Null(exception);
    }

    [Fact]
    public void NativeMethods_DwmTransitionsForceDisabled_HasExpectedValue()
    {
        Assert.Equal(3, NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED);
    }

    [Fact]
    public void NativeMethods_GetScreenRefreshRate_ReturnsValidRefreshRate()
    {
        int rate = NativeMethods.GetScreenRefreshRate(IntPtr.Zero);
        Assert.InRange(rate, 30, 360);
    }

    [Fact]
    public void WebLinkDialog_Setup_PopulatesFieldsAndDestinationCorrectly()
    {
        WpfTestHost.RunSta(() =>
        {
            var dialog = new WebLinkDialog();
            dialog.Setup("https://github.com", "My GitHub", defaultCanvasOnly: true);

            Assert.Equal("https://github.com", dialog.WebLinkUrlInput.Text);
            Assert.Equal("My GitHub", dialog.WebLinkTitleInput.Text);
            Assert.True(dialog.WebLinkDestCanvasRadio.IsChecked);
            Assert.False(dialog.WebLinkDestBothRadio.IsChecked);
        });
    }

    [Fact]
    public void WebLinkDialog_Setup_DefaultsToBothWhenNotCanvasOnly()
    {
        WpfTestHost.RunSta(() =>
        {
            var dialog = new WebLinkDialog();
            dialog.Setup("https://github.com");

            Assert.Equal("https://github.com", dialog.WebLinkUrlInput.Text);
            Assert.Equal("Github", dialog.WebLinkTitleInput.Text);
            Assert.True(dialog.WebLinkDestBothRadio.IsChecked);
            Assert.False(dialog.WebLinkDestCanvasRadio.IsChecked);
        });
    }
}
