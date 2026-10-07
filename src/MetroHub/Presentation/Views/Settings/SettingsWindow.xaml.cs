using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using MetroHub.Core.Services;

namespace MetroHub.Presentation.Views.Settings;

/// <summary>
/// Dedicated Settings Window host for MetroHub (Phase 1).
/// Provides the native DWM Acrylic backdrop, window drag region, and modal lifecycle.
/// </summary>
public partial class SettingsWindow : BorderlessFluentWindow
{
    public SettingsWindow()
    {
        InitializeComponent();
        Loaded += OnSettingsWindowLoaded;
        PreviewKeyDown += OnSettingsWindowKeyDown;
    }

    private void OnSettingsWindowLoaded(object sender, RoutedEventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            // Apply native Windows 11 DWM Acrylic composition (DWMSBT_TRANSIENTWINDOW = 3)
            NativeMethods.ApplyMica(hwnd, dark: true, NativeMethods.DWMSBT_TRANSIENTWINDOW);
        }
    }

    private void OnSettingsWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnCategoryCardClick(object sender, RoutedEventArgs e)
    {
        // Category selection hook for Phase 2 navigation transition
    }
}
