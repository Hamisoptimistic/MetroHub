using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MetroHub.Core.Services;
using MetroHub.Presentation.Views;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MetroHub.Presentation.Dialogs;

/// <summary>
/// Unified base window for MetroHub modal dialogs (Add Web Link, Weather Location, Radio Station, Settings).
/// Encapsulates the standard two-column Metro layout:
/// - Left Column (155px): Frosted DWM Mica sidebar with hero icon or custom sidebar content.
/// - Right Column (*): Dark Obsidian form panel with Title, Subtitle, Close [X] button, and content slot.
/// Inherits directly from BorderlessFluentWindow to leverage native Windows 11 DWM Mica composition without frame disruption.
/// </summary>
public class MetroDialog : BorderlessFluentWindow
{
    static MetroDialog()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(MetroDialog),
            new FrameworkPropertyMetadata(typeof(MetroDialog)));
    }

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
    private NativeMethods.LowLevelMouseProc? _mouseHookProc;
    private IntPtr _mouseHook = IntPtr.Zero;
    private long _shownTimestamp;
    private bool _isClosing;

    /// <summary>
    /// When enabled, clicking outside the dialog or switching away automatically dismisses the dialog.
    /// Defaults to true for all standard MetroDialog modals.
    /// </summary>
    protected virtual bool EnableLightDismiss => true;

    /// <summary>
    /// Modal dialogs use WS_EX_TOOLWINDOW to prevent DWM composition flash on open
    /// and keep the dialog out of the Alt+Tab switcher and taskbar.
    /// </summary>
    protected override bool EnableToolWindowStyle => true;

    /// <summary>
    /// Optional element to focus when dialog loads. If null, the first focusable, editable TextBox in the visual tree is focused.
    /// </summary>
    protected virtual IInputElement? InitialFocusedElement => null;

    public MetroDialog()
    {
        Width = 620;
        Height = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(StyleProperty, typeof(MetroDialog));
        Loaded += OnMetroDialogLoaded;
    }

    private void OnMetroDialogLoaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (IsDismissing) return;
            var target = InitialFocusedElement ?? FindFirstFocusableInput(this);
            if (target is UIElement element && element.Focusable && element.IsEnabled)
            {
                element.Focus();
                Keyboard.Focus(element);
                if (element is TextBox tb)
                {
                    tb.SelectAll();
                }
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private static UIElement? FindFirstFocusableInput(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBox tb && tb.IsVisible && tb.Focusable && tb.IsEnabled && !tb.IsReadOnly)
            {
                return tb;
            }
            var nested = FindFirstFocusableInput(child);
            if (nested != null) return nested;
        }
        return null;
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
        DismissDialog();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DismissDialog();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    public void DismissDialog()
    {
        if (IsDismissing || _isClosing) return;
        _isClosing = true;
        IsDismissing = true;
        UninstallMouseHook();
        try
        {
            if (DialogResult == null)
            {
                try { DialogResult = false; } catch (InvalidOperationException) { }
            }
            Close();
        }
        catch (InvalidOperationException)
        {
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _isClosing = true;
        IsDismissing = true;
        UninstallMouseHook();
        base.OnClosing(e);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _shownTimestamp = Environment.TickCount64;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (EnableLightDismiss && Environment.TickCount64 - _shownTimestamp >= 200 && !IsDismissing && !_isClosing && IsLoaded)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (!IsDismissing && !_isClosing && IsLoaded)
                {
                    IntPtr hwnd = new WindowInteropHelper(this).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        IntPtr activeWnd = NativeMethods.GetActiveWindow();
                        if (activeWnd != IntPtr.Zero && (activeWnd == hwnd || NativeMethods.GetAncestor(activeWnd, NativeMethods.GA_ROOTOWNER) == hwnd))
                        {
                            return;
                        }
                    }

                    DismissDialog();
                }
            });
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosing = true;
        IsDismissing = true;
        UninstallMouseHook();
        base.OnClosed(e);
        _dialogScope?.Dispose();
        _dialogScope = null;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _dialogScope = MainWindow.EnterDialogScope();
        _shownTimestamp = Environment.TickCount64;

        if (EnableLightDismiss)
        {
            InstallMouseHook();
        }

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            // Responsive sizing: clamp to fit comfortably on smaller displays or high DPI before applying backdrop
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

            // Apply native DWM Acrylic immediately on final window metrics.
            // Samples and blurs background windows into a frosted glass effect.
            NativeMethods.ApplyMica(hwnd, dark: true, NativeMethods.DWMSBT_TRANSIENTWINDOW);
        }
    }

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return;
        _mouseHookProc = LowLevelMouseHookCallback;
        _mouseHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL,
            _mouseHookProc,
            IntPtr.Zero,
            0);
    }

    private void UninstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
            _mouseHookProc = null;
        }
    }

    private IntPtr LowLevelMouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)NativeMethods.WM_LBUTTONDOWN ||
                           wParam == (IntPtr)NativeMethods.WM_NCLBUTTONDOWN ||
                           wParam == (IntPtr)NativeMethods.WM_RBUTTONDOWN ||
                           wParam == (IntPtr)NativeMethods.WM_NCRBUTTONDOWN))
        {
            if (Environment.TickCount64 - _shownTimestamp >= 200 && !IsDismissing && !_isClosing && IsLoaded)
            {
                var hookStruct = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    IntPtr clickedWnd = NativeMethods.WindowFromPoint(hookStruct.pt);
                    if (clickedWnd != IntPtr.Zero)
                    {
                        if (clickedWnd == hwnd || NativeMethods.GetAncestor(clickedWnd, NativeMethods.GA_ROOTOWNER) == hwnd)
                        {
                            return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
                        }
                    }

                    if (NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect))
                    {
                        int x = hookStruct.pt.X;
                        int y = hookStruct.pt.Y;
                        bool isInside = x >= rect.Left && x <= rect.Right && y >= rect.Top && y <= rect.Bottom;
                        if (!isInside)
                        {
                            Dispatcher.InvokeAsync(DismissDialog);
                        }
                    }
                }
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Displays a standardized modal confirmation dialog (Title, Subtitle, Message, Detail, Cancel, Confirm)
    /// rendered inside the standard 620x360 MetroDialog frame with Mica/Obsidian composition.
    /// </summary>
    public static bool Confirm(
        Window? owner,
        string title,
        string message,
        string? detail = null,
        string confirmText = "Confirm",
        bool isDestructive = false,
        SymbolRegular? symbol = null,
        string? subtitle = null,
        string cancelText = "Cancel",
        bool showCancelButton = true)
    {
        return Confirm(owner, new MetroConfirmOptions
        {
            Title = title,
            Subtitle = subtitle,
            Message = message,
            Detail = detail,
            ConfirmText = confirmText,
            CancelText = cancelText,
            ShowCancelButton = showCancelButton,
            IsDestructive = isDestructive,
            Symbol = symbol
        });
    }

    /// <summary>
    /// Displays a standardized modal confirmation dialog using custom options.
    /// </summary>
    public static bool Confirm(Window? owner, MetroConfirmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var dlg = new MetroDialog
        {
            Owner = owner ?? Application.Current?.MainWindow,
            Title = options.Title,
            Subtitle = options.Subtitle,
        };

        if (options.Symbol.HasValue)
        {
            var icon = new SymbolIcon
            {
                Symbol = options.Symbol.Value,
                FontSize = 54,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (options.IsDestructive)
            {
                icon.SetResourceReference(SymbolIcon.ForegroundProperty, "StatusErrorBrush");
            }
            else
            {
                icon.SetResourceReference(SymbolIcon.ForegroundProperty, "SystemAccentColorPrimaryBrush");
            }

            dlg.SidebarContent = icon;
        }
        else if (options.SidebarIcon != null)
        {
            dlg.SidebarIcon = options.SidebarIcon;
        }

        var contentGrid = new Grid();
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var bodyStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };

        var messageBlock = new TextBlock
        {
            Text = options.Message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 8, 8)
        };
        messageBlock.SetResourceReference(TextBlock.FontSizeProperty, "TypeBodyStrongFontSize");
        messageBlock.SetResourceReference(TextBlock.FontWeightProperty, "TypeHeaderFontWeight");
        messageBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        bodyStack.Children.Add(messageBlock);

        if (!string.IsNullOrWhiteSpace(options.Detail))
        {
            var detailBlock = new TextBlock
            {
                Text = options.Detail,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18
            };
            detailBlock.SetResourceReference(TextBlock.FontSizeProperty, "TypeCaptionFontSize");
            detailBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            bodyStack.Children.Add(detailBlock);
        }

        Grid.SetRow(bodyStack, 0);
        contentGrid.Children.Add(bodyStack);

        var footerGrid = new Grid
        {
            Margin = new Thickness(0, 16, 0, 0)
        };
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        if (options.ShowCancelButton)
        {
            var cancelButton = new Button
            {
                Content = options.CancelText,
                Margin = new Thickness(0, 0, 10, 0),
                IsCancel = true
            };
            cancelButton.SetResourceReference(FrameworkElement.StyleProperty, "DefaultButtonStyle");
            cancelButton.Click += (_, _) =>
            {
                dlg.DialogResult = false;
                dlg.Close();
            };
            buttonPanel.Children.Add(cancelButton);
        }

        var confirmButton = new Button
        {
            Content = options.ConfirmText,
            IsDefault = true
        };

        if (options.IsDestructive)
        {
            confirmButton.SetResourceReference(Control.BackgroundProperty, "StatusErrorBrush");
            confirmButton.Foreground = Brushes.White;
            confirmButton.BorderThickness = new Thickness(0);
            confirmButton.Padding = new Thickness(16, 0, 16, 0);
            confirmButton.Height = 32;
            confirmButton.MinWidth = 100;
            confirmButton.Cursor = Cursors.Hand;
            confirmButton.SetResourceReference(Control.FontFamilyProperty, "AppFontFamily");
            confirmButton.SetResourceReference(Control.FontSizeProperty, "TypeBodyStrongFontSize");
            confirmButton.SetResourceReference(Control.FontWeightProperty, "TypeBodyStrongFontWeight");
        }
        else
        {
            confirmButton.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
        }

        confirmButton.Click += (_, _) =>
        {
            dlg.DialogResult = true;
            dlg.Close();
        };
        buttonPanel.Children.Add(confirmButton);

        footerGrid.Children.Add(buttonPanel);
        Grid.SetRow(footerGrid, 1);
        contentGrid.Children.Add(footerGrid);

        dlg.Content = contentGrid;
        return dlg.ShowDialog() == true;
    }
}

/// <summary>
/// Options for displaying a standardized Metro confirmation modal dialog.
/// </summary>
public sealed record MetroConfirmOptions
{
    public string Title { get; init; } = "Confirm";
    public string? Subtitle { get; init; }
    public string Message { get; init; } = "Are you sure you want to proceed?";
    public string? Detail { get; init; }
    public string ConfirmText { get; init; } = "Confirm";
    public string CancelText { get; init; } = "Cancel";
    public bool ShowCancelButton { get; init; } = true;
    public bool IsDestructive { get; init; }
    public SymbolRegular? Symbol { get; init; }
    public ImageSource? SidebarIcon { get; init; }
}
