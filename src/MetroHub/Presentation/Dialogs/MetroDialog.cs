using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MetroHub.Core.Services;
using MetroHub.Presentation.Views;

namespace MetroHub.Presentation.Dialogs;

/// <summary>
/// Unified base window for MetroHub modal dialogs (Add Web Link, Weather Location, Radio Station, Settings).
/// Encapsulates the standard two-column Metro layout:
/// - Left Column (155px): Frosted DWM Acrylic sidebar with hero icon or custom sidebar content.
/// - Right Column (*): Dark Obsidian form panel with Title, Subtitle, Close [X] button, and content slot.
/// Inherits from BorderlessFluentWindow to provide immediate DWM Acrylic blur composition (via WM_NCACTIVATE),
/// borderless chrome, window dragging, Escape-key dismissal, and monitor work-area centering.
/// </summary>
public class MetroDialog : BorderlessFluentWindow
{
    static MetroDialog()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(MetroDialog),
            new FrameworkPropertyMetadata(typeof(MetroDialog)));
    }

    /// <summary>
    /// Dialogs are owned top-level modal windows, not shell overlays, so tool-window style is disabled.
    /// This preserves standard Win32 dialog focus routing and input processing.
    /// </summary>
    protected override bool EnableToolWindowStyle => false;

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(
            nameof(Subtitle),
            typeof(string),
            typeof(MetroDialog),
            new PropertyMetadata(null, OnSubtitleChanged));

    public static readonly DependencyProperty SubtitleVisibilityProperty =
        DependencyProperty.Register(
            nameof(SubtitleVisibility),
            typeof(Visibility),
            typeof(MetroDialog),
            new PropertyMetadata(Visibility.Collapsed));

    public static readonly DependencyProperty SidebarIconProperty =
        DependencyProperty.Register(
            nameof(SidebarIcon),
            typeof(ImageSource),
            typeof(MetroDialog),
            new PropertyMetadata(null, OnSidebarIconChanged));

    public static readonly DependencyProperty SidebarIconVisibilityProperty =
        DependencyProperty.Register(
            nameof(SidebarIconVisibility),
            typeof(Visibility),
            typeof(MetroDialog),
            new PropertyMetadata(Visibility.Collapsed));

    public static readonly DependencyProperty SidebarContentProperty =
        DependencyProperty.Register(
            nameof(SidebarContent),
            typeof(object),
            typeof(MetroDialog),
            new PropertyMetadata(null));

    public string? Subtitle
    {
        get => (string?)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public Visibility SubtitleVisibility
    {
        get => (Visibility)GetValue(SubtitleVisibilityProperty);
        private set => SetValue(SubtitleVisibilityProperty, value);
    }

    public ImageSource? SidebarIcon
    {
        get => (ImageSource?)GetValue(SidebarIconProperty);
        set => SetValue(SidebarIconProperty, value);
    }

    public Visibility SidebarIconVisibility
    {
        get => (Visibility)GetValue(SidebarIconVisibilityProperty);
        private set => SetValue(SidebarIconVisibilityProperty, value);
    }

    public object? SidebarContent
    {
        get => GetValue(SidebarContentProperty);
        set => SetValue(SidebarContentProperty, value);
    }

    private static void OnSubtitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MetroDialog dlg)
        {
            dlg.SubtitleVisibility = string.IsNullOrWhiteSpace(e.NewValue as string)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    private static void OnSidebarIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MetroDialog dlg)
        {
            dlg.SidebarIconVisibility = e.NewValue != null
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private IDisposable? _dialogScope;

    public MetroDialog()
    {
        Width = 620;
        Height = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, typeof(MetroDialog));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (GetTemplateChild("PART_CloseButton") is Button closeButton)
        {
            closeButton.Click -= OnCloseButtonClicked;
            closeButton.Click += OnCloseButtonClicked;
        }
    }

    private void OnCloseButtonClicked(object sender, RoutedEventArgs e)
    {
        if (DialogResult == null)
        {
            try { DialogResult = false; } catch (InvalidOperationException) { }
        }
        Close();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (DialogResult == null)
            {
                try { DialogResult = false; } catch (InvalidOperationException) { }
            }
            Close();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _dialogScope?.Dispose();
        _dialogScope = null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch
            {
            }
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _dialogScope = MainWindow.EnterDialogScope();

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            ApplyAcrylicBackdrop(hwnd);

            // Responsive sizing: clamp to fit comfortably on smaller displays or high DPI
            var workArea = SystemParameters.WorkArea;
            if (workArea.Width > 0 && workArea.Height > 0)
            {
                Width = Math.Min(Width, Math.Max(480, workArea.Width * 0.85));
                Height = Math.Min(Height, Math.Max(300, workArea.Height * 0.85));
            }

            if (Owner != null && Owner.ActualWidth > 0 && Owner.ActualHeight > 0)
            {
                Left = Owner.Left + (Owner.ActualWidth - Width) / 2;
                Top = Owner.Top + (Owner.ActualHeight - Height) / 2;
            }
            else if (workArea.Width > 0 && workArea.Height > 0)
            {
                Left = workArea.Left + (workArea.Width - Width) / 2;
                Top = workArea.Top + (workArea.Height - Height) / 2;
            }
        }
    }

    private static void ApplyAcrylicBackdrop(IntPtr hwnd)
    {
        try
        {
            int darkVal = 1;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkVal, sizeof(int));

            int cornerVal = NativeMethods.DWMWCP_DONOTROUND;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerVal, sizeof(int));

            int borderVal = NativeMethods.DWMWA_COLOR_NONE;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref borderVal, sizeof(int));

            NativeMethods.MARGINS margins = new(-1, -1, -1, -1);
            NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);

            int backdropVal = NativeMethods.DWMSBT_TRANSIENTWINDOW; // 3 = Acrylic
            int res = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropVal, sizeof(int));
            if (res != 0)
            {
                int trueVal = 1;
                NativeMethods.DwmSetWindowAttribute(hwnd, 1029, ref trueVal, sizeof(int));
            }

            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED);
        }
        catch
        {
        }
    }
}
