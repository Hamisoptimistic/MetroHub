using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MetroHub.Core.Models;
using MetroHub.Core.Services;

namespace MetroHub;

public partial class MainWindow
{
    private DispatcherTimer? _sidebarIntentTimer;
    private DispatcherTimer? _sidebarGraceTimer;
    private RectangleGeometry? _canvasScissorGeom;
    private TranslateTransform? _canvasScissorTranslate;
    private TranslateTransform? SidebarRailTranslate => SidebarRail?.RenderTransform as TranslateTransform;

    private void SetupSidebarTimers()
    {
        _sidebarIntentTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _sidebarIntentTimer.Tick += OnSidebarIntentTimerTick;

        _sidebarGraceTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _sidebarGraceTimer.Tick += OnSidebarGraceTimerTick;
    }

    private void UpdateSidebarVisibilityInitial()
    {
        if (SidebarRailTranslate == null || SidebarRail == null) return;
        if (Settings.SidebarPinned)
        {
            SidebarRailTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            SidebarRailTranslate.X = 0;
            SidebarRail.SetPinnedState(true);
        }
        else
        {
            SidebarRailTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            SidebarRailTranslate.X = -52;
            SidebarRail.SetPinnedState(false);
        }
    }

    private void OnSidebarPinToggled(object? sender, EventArgs e)
    {
        StorageService.SaveSettings(Settings);
        if (Settings.SidebarPinned)
        {
            ShowSidebarRail();
        }
        else
        {
            if (SidebarRail != null && !SidebarRail.IsMouseOver && (AllAppsDrawer == null || !AllAppsDrawer.IsOpen))
            {
                _sidebarGraceTimer?.Start();
            }
        }
    }

    private void OnSidebarShortcutsChanged(object? sender, EventArgs e)
    {
        StorageService.SaveSettings(Settings);
    }

    private void OnLeftEdgeHoverStripMouseEnter(object sender, MouseEventArgs e)
    {
        if (Settings.SidebarPinned) return;
        _sidebarGraceTimer?.Stop();
        _sidebarIntentTimer?.Stop();
        _sidebarIntentTimer?.Start();
    }

    private void OnLeftEdgeHoverStripMouseLeave(object sender, MouseEventArgs e)
    {
        if (SidebarRailTranslate != null && SidebarRailTranslate.X <= -50)
        {
            _sidebarIntentTimer?.Stop();
        }
    }

    private void OnLeftEdgeHoverStripDragEnter(object sender, DragEventArgs e)
    {
        ShowSidebarRail();
    }

    private void OnSidebarIntentTimerTick(object? sender, EventArgs e)
    {
        _sidebarIntentTimer?.Stop();
        if ((LeftEdgeHoverStrip != null && LeftEdgeHoverStrip.IsMouseOver) ||
            (SidebarRail != null && SidebarRail.IsMouseOver))
        {
            ShowSidebarRail();
        }
        else
        {
            Point mousePos = Mouse.GetPosition(this);
            if (mousePos.X <= 48 && mousePos.X >= 0)
            {
                ShowSidebarRail();
            }
        }
    }

    private void OnSidebarRailMouseEnter(object sender, MouseEventArgs e)
    {
        _sidebarGraceTimer?.Stop();
        _sidebarIntentTimer?.Stop();
        if (SidebarRailTranslate != null && SidebarRailTranslate.X < 0)
        {
            ShowSidebarRail();
        }
    }

    private void OnSidebarRailMouseLeave(object sender, MouseEventArgs e)
    {
        if (Settings.SidebarPinned) return;
        if (AllAppsDrawer != null && AllAppsDrawer.IsOpen) return;
        if (IsDialogOpen) return;

        _sidebarGraceTimer?.Stop();
        _sidebarGraceTimer?.Start();
    }

    private void OnSidebarGraceTimerTick(object? sender, EventArgs e)
    {
        _sidebarGraceTimer?.Stop();

        if (Settings.SidebarPinned) return;
        if (AllAppsDrawer != null && AllAppsDrawer.IsOpen) return;
        if (IsDialogOpen) return;

        if (SidebarRail != null && SidebarRail.IsMouseOver) return;
        if (LeftEdgeHoverStrip != null && LeftEdgeHoverStrip.IsMouseOver) return;

        Point mousePos = Mouse.GetPosition(this);
        if (mousePos.X <= 48 && mousePos.X >= 0) return;

        HideSidebarRail();
    }

    public void ShowSidebarRail()
    {
        _sidebarGraceTimer?.Stop();
        if (SidebarRailTranslate == null || SidebarRail == null) return;

        if (Math.Abs(SidebarRailTranslate.X) < 0.1 && !SidebarRailTranslate.HasAnimatedProperties)
        {
            return;
        }

        SidebarRail.CacheMode = new BitmapCache { RenderAtScale = 1.0, SnapsToDevicePixels = true };

        var anim = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        int refreshRate = NativeMethods.GetScreenRefreshRate();
        Timeline.SetDesiredFrameRate(anim, refreshRate > 0 ? refreshRate : 100);

        anim.Completed += (s, e) =>
        {
            if (SidebarRail != null)
            {
                SidebarRail.CacheMode = null;
            }
            if (SidebarRailTranslate != null)
            {
                SidebarRailTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                SidebarRailTranslate.X = 0;
            }
        };

        SidebarRailTranslate.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    public void HideSidebarRail(bool immediate = false)
    {
        if (Settings.SidebarPinned) return;
        if (AllAppsDrawer != null && AllAppsDrawer.IsOpen) return;
        if (IsDialogOpen) return;
        if (SidebarRail == null || SidebarRailTranslate == null) return;

        if (immediate)
        {
            _sidebarIntentTimer?.Stop();
            _sidebarGraceTimer?.Stop();
            SidebarRailTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            SidebarRailTranslate.X = -52;
            SidebarRail.CacheMode = null;
            return;
        }

        if (SidebarRailTranslate.X <= -52 && !SidebarRailTranslate.HasAnimatedProperties)
        {
            return;
        }

        SidebarRail.CacheMode = new BitmapCache { RenderAtScale = 1.0, SnapsToDevicePixels = true };

        var anim = new DoubleAnimation
        {
            To = -52,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        int refreshRate = NativeMethods.GetScreenRefreshRate();
        Timeline.SetDesiredFrameRate(anim, refreshRate > 0 ? refreshRate : 100);

        anim.Completed += (s, e) =>
        {
            if (SidebarRail != null)
            {
                SidebarRail.CacheMode = null;
            }
            if (SidebarRailTranslate != null)
            {
                SidebarRailTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                SidebarRailTranslate.X = -52;
            }
        };

        SidebarRailTranslate.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private void OnSidebarAppsToggleRequested(object? sender, EventArgs e)
    {
        AllAppsDrawer?.Toggle();
    }

    private void OnDrawerOpened(object? sender, EventArgs e)
    {
        ShowSidebarRail();
        SidebarRail?.SetAppsDrawerActive(true);
        ApplyCanvasDrawerClip(true);
    }

    private void OnDrawerClosing(object? sender, EventArgs e)
    {
        SidebarRail?.SetAppsDrawerActive(false);
        ApplyCanvasDrawerClip(false);
    }

    private void OnDrawerClosed(object? sender, EventArgs e)
    {
        SidebarRail?.SetAppsDrawerActive(false);
        ApplyCanvasDrawerClip(false);
        if (!Settings.SidebarPinned)
        {
            if (SidebarRail != null && !SidebarRail.IsMouseOver)
            {
                _sidebarGraceTimer?.Start();
            }
        }
    }

    private void ApplyCanvasDrawerClip(bool isDrawerOpen)
    {
        if (MainContentAreaGrid == null) return;

        if (!isDrawerOpen)
        {
            MainContentAreaGrid.Clip = null;
            if (ContentScrollViewer != null) ContentScrollViewer.Clip = null;
            if (_canvasScissorTranslate != null)
            {
                _canvasScissorTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                _canvasScissorTranslate.X = 0;
            }
            return;
        }

        double drawerWidth = AllAppsDrawer?.DrawerWidth ?? 320.0;
        if (drawerWidth <= 0) drawerWidth = 320.0;

        _canvasScissorTranslate ??= new TranslateTransform(0, 0);
        _canvasScissorTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        _canvasScissorTranslate.X = drawerWidth;

        _canvasScissorGeom ??= new RectangleGeometry
        {
            Transform = _canvasScissorTranslate
        };

        double clipWidth = Math.Max(5000, (ActualWidth > 0 ? ActualWidth : 1920) + 1000);
        double clipHeight = Math.Max(5000, (ActualHeight > 0 ? ActualHeight : 1080) + 1000);
        _canvasScissorGeom.Rect = new Rect(0, 0, clipWidth, clipHeight);

        MainContentAreaGrid.Clip = _canvasScissorGeom;
    }

    private void OnDrawerAppPinRequested(object? sender, CatalogItemModel item)
    {
        PinCatalogItem(item, null);
    }

    private void OnDrawerAppLaunchRequested(object? sender, CatalogItemModel item)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(item.TargetPath))
            {
                NativeMethods.LaunchTarget(item.TargetPath, item.Arguments);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] Failed to launch app: {ex.Message}");
        }
    }

}
