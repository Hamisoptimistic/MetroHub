using System;
using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MetroHub.Presentation.Controls;

/// <summary>
/// Reusable horizontal tab strip control designed for widget headers.
/// Features a hardware-accelerated sliding active indicator, fixed-width close buttons
/// with smooth hover reveals, and a scrollable strip with '+' immediately following tabs.
/// </summary>
public partial class WidgetTabStrip : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IEnumerable),
            typeof(WidgetTabStrip),
            new PropertyMetadata(null, OnItemsSourceChanged));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly DependencyProperty SelectedItemProperty =
        DependencyProperty.Register(
            nameof(SelectedItem),
            typeof(object),
            typeof(WidgetTabStrip),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedItemChanged));

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public static readonly DependencyProperty AddTabCommandProperty =
        DependencyProperty.Register(
            nameof(AddTabCommand),
            typeof(ICommand),
            typeof(WidgetTabStrip),
            new PropertyMetadata(null));

    public ICommand? AddTabCommand
    {
        get => (ICommand?)GetValue(AddTabCommandProperty);
        set => SetValue(AddTabCommandProperty, value);
    }

    public static readonly DependencyProperty CloseTabCommandProperty =
        DependencyProperty.Register(
            nameof(CloseTabCommand),
            typeof(ICommand),
            typeof(WidgetTabStrip),
            new PropertyMetadata(null));

    public ICommand? CloseTabCommand
    {
        get => (ICommand?)GetValue(CloseTabCommandProperty);
        set => SetValue(CloseTabCommandProperty, value);
    }

    public static readonly DependencyProperty MaxTabsProperty =
        DependencyProperty.Register(
            nameof(MaxTabs),
            typeof(int),
            typeof(WidgetTabStrip),
            new PropertyMetadata(10, OnMaxTabsChanged));

    public int MaxTabs
    {
        get => (int)GetValue(MaxTabsProperty);
        set => SetValue(MaxTabsProperty, value);
    }

    public static readonly DependencyProperty CanAddTabProperty =
        DependencyProperty.Register(
            nameof(CanAddTab),
            typeof(bool),
            typeof(WidgetTabStrip),
            new PropertyMetadata(true));

    public bool CanAddTab
    {
        get => (bool)GetValue(CanAddTabProperty);
        private set => SetValue(CanAddTabProperty, value);
    }

    public static readonly DependencyProperty AddTabToolTipProperty =
        DependencyProperty.Register(
            nameof(AddTabToolTip),
            typeof(string),
            typeof(WidgetTabStrip),
            new PropertyMetadata("New Tab (Ctrl+T)"));

    public string AddTabToolTip
    {
        get => (string)GetValue(AddTabToolTipProperty);
        private set => SetValue(AddTabToolTipProperty, value);
    }

    public WidgetTabStrip()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        IsVisibleChanged += OnStripIsVisibleChanged;

        PART_ItemsControl.ItemContainerGenerator.StatusChanged += (s, e) =>
        {
            if (PART_ItemsControl.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    if (!IsLoaded) return;
                    UpdateDividersVisibility();
                    // Animate if indicator is already visible (tab added mid-session);
                    // snap on first load when indicator width is still 0.
                    bool shouldAnimate = PART_SlidingIndicator != null
                        && PART_SlidingIndicator.Width > 0
                        && !double.IsNaN(PART_SlidingIndicator.Width);
                    UpdateIndicator(animate: shouldAnimate);
                }, DispatcherPriority.Loaded);
            }
        };

        UpdateCanAddTab();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopScrollAnimation();
        PART_SlidingIndicator?.BeginAnimation(UIElement.OpacityProperty, null);
        PART_SlidingIndicator?.BeginAnimation(FrameworkElement.WidthProperty, null);
        PART_IndicatorTransform?.BeginAnimation(TranslateTransform.XProperty, null);

        if (_sizeTrackedBorder != null)
        {
            _sizeTrackedBorder.SizeChanged -= OnActiveTabSizeChanged;
            _sizeTrackedBorder = null;
        }

        if (ItemsSource is INotifyCollectionChanged col)
        {
            col.CollectionChanged -= OnCollectionChanged;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ItemsSource is INotifyCollectionChanged col)
        {
            col.CollectionChanged -= OnCollectionChanged;
            col.CollectionChanged += OnCollectionChanged;
        }

        UpdateCanAddTab();
        UpdateScrollButtonsVisibility();
        UpdateDividersVisibility();
        Dispatcher.InvokeAsync(() =>
        {
            if (!IsLoaded) return;
            UpdateDividersVisibility();
            UpdateIndicator(animate: false);
        }, DispatcherPriority.Loaded);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateScrollButtonsVisibility();
        UpdateIndicator(animate: false);
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WidgetTabStrip control)
        {
            if (e.OldValue is INotifyCollectionChanged oldCol)
            {
                oldCol.CollectionChanged -= control.OnCollectionChanged;
            }
            if (e.NewValue is INotifyCollectionChanged newCol)
            {
                newCol.CollectionChanged += control.OnCollectionChanged;
            }
            control.UpdateCanAddTab();
            if (!control.IsLoaded) return;
            control.Dispatcher.InvokeAsync(() =>
            {
                if (!control.IsLoaded) return;
                control.UpdateDividersVisibility();
                control.UpdateIndicator(animate: false);
            }, DispatcherPriority.Loaded);
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A pending close whose tab is gone (closed elsewhere, Close Other Tabs, undo) must be
        // dropped, and its half-applied opacity animation cleared so a recycled container can't
        // render invisible.
        if (_pendingClose is { } pending && !IsTabPresent(pending.Tab))
        {
            pending.Root.BeginAnimation(UIElement.OpacityProperty, null);
            _pendingClose = null;
        }

        UpdateCanAddTab();
        // Don't call UpdateScrollButtonsVisibility() synchronously here —
        // layout hasn't happened yet so scroll metrics are stale.
        // ScrollChanged event will handle it after layout settles.
        if (!IsLoaded) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (!IsLoaded) return;
            UpdateDividersVisibility();
            UpdateScrollButtonsVisibility();
            // Animate indicator to new position after layout settles (smooth, not a snap)
            UpdateIndicator(animate: true);
        }, DispatcherPriority.Loaded);
    }

    private static void OnMaxTabsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WidgetTabStrip control)
        {
            control.UpdateCanAddTab();
        }
    }

    private static void OnSelectedItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WidgetTabStrip control)
        {
            control.ScrollSelectedIntoView();
            control.UpdateDividersVisibility();
            control.UpdateIndicator(animate: control.IsLoaded);
        }
    }

    public void UpdateIndicator(bool animate = true)
    {
        if (!IsLoaded || PART_SlidingIndicator == null || PART_IndicatorTransform == null || PART_TabStripHost == null)
        {
            return;
        }

        if (SelectedItem == null)
        {
            if (PART_SlidingIndicator.Opacity > 0.0)
            {
                var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(150))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                PART_SlidingIndicator.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }
            return;
        }

        var container = PART_ItemsControl.ItemContainerGenerator.ContainerFromItem(SelectedItem) as FrameworkElement;
        if (container == null || container.ActualWidth <= 0 || !container.IsDescendantOf(PART_TabStripHost))
        {
            return;
        }

        // Target TabBorder specifically to guarantee 100% exact alignment with the grey tab highlight
        var tabBorder = FindVisualChildByName<Border>(container, "TabBorder") ?? container;
        if (tabBorder.ActualWidth <= 0 || !tabBorder.IsDescendantOf(PART_TabStripHost))
        {
            return;
        }

        // Exactly one tab border carries this subscription at a time: subscribe to the new active
        // tab and release the previous one. Without the release, every tab ever selected keeps a
        // (no-op) handler for the lifetime of the strip.
        if (!ReferenceEquals(_sizeTrackedBorder, tabBorder))
        {
            if (_sizeTrackedBorder != null)
            {
                _sizeTrackedBorder.SizeChanged -= OnActiveTabSizeChanged;
            }
            _sizeTrackedBorder = tabBorder;
            tabBorder.SizeChanged += OnActiveTabSizeChanged;
        }

        Point point;
        try
        {
            point = tabBorder.TranslatePoint(new Point(0, 0), PART_TabStripHost);
        }
        catch
        {
            return;
        }

        double targetX = point.X;
        double targetWidth = tabBorder.ActualWidth;

        // Always snap width instantly (never animate it) — matches WidgetTiles pattern.
        // Animating width causes layout thrash and the "jumping tabs" effect.
        if (Math.Abs(PART_SlidingIndicator.Width - targetWidth) > 0.5 || double.IsNaN(PART_SlidingIndicator.Width))
        {
            PART_SlidingIndicator.BeginAnimation(FrameworkElement.WidthProperty, null);
            PART_SlidingIndicator.Width = targetWidth;
        }

        if (PART_SlidingIndicator.Opacity < 1.0)
        {
            PART_SlidingIndicator.BeginAnimation(UIElement.OpacityProperty, null);
            PART_SlidingIndicator.Opacity = 1.0;
        }

        if (animate && IsLoaded)
        {
            var xAnim = new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Timeline.SetDesiredFrameRate(xAnim, 120);
            PART_IndicatorTransform.BeginAnimation(TranslateTransform.XProperty, xAnim, HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            PART_IndicatorTransform.BeginAnimation(TranslateTransform.XProperty, null);
            PART_IndicatorTransform.X = targetX;
        }
    }

    private bool IsTabPresent(object tab)
    {
        if (ItemsSource == null) return false;
        foreach (object? item in ItemsSource)
        {
            if (ReferenceEquals(item, tab)) return true;
        }
        return false;
    }

    private void UpdateCanAddTab()
    {
        int count = 0;
        if (ItemsSource is ICollection col)
        {
            count = col.Count;
        }
        else if (ItemsSource != null)
        {
            foreach (var _ in ItemsSource) count++;
        }

        bool canAdd = count < MaxTabs;
        CanAddTab = canAdd;
        AddTabToolTip = canAdd ? "New Tab (Ctrl+T)" : $"Maximum tabs reached ({MaxTabs} max)";
    }

    private void TabBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext != null)
        {
            SelectedItem = element.DataContext;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // Don't trigger tab selection on close click
        if (sender is FrameworkElement element && element.DataContext != null)
        {
            object tab = element.DataContext;
            if (CloseTabCommand?.CanExecute(tab) != true) return;

            // One pending close per tab: re-clicking the same close button must not restart the
            // fade, which would fire a second Completed callback for the same tab.
            if (_pendingClose?.Tab is { } alreadyClosing && ReferenceEquals(alreadyClosing, tab))
            {
                return;
            }

            // Fade out closing tab before removal
            var tabRoot = FindVisualParent<Grid>(element, "TabRootGrid");
            if (tabRoot != null)
            {
                var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(120))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                _pendingClose = (tab, tabRoot);
                fadeOut.Completed += (_, _) =>
                {
                    // Only a still-pending close may execute. A collection change in the meantime
                    // (another close, Close Other Tabs, undo restoring the layout) may have removed
                    // or recycled this container — executing then would close an already-gone tab
                    // or act on a container that now belongs to a different one.
                    if (_pendingClose?.Tab is { } current && ReferenceEquals(current, tab))
                    {
                        _pendingClose = null;
                        CloseTabCommand.Execute(tab);
                    }
                };
                tabRoot.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }
            else
            {
                CloseTabCommand.Execute(tab);
            }
        }
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (CanAddTab && AddTabCommand?.CanExecute(null) == true)
        {
            AddTabCommand.Execute(null);
            // Scroll to end AFTER layout has fully settled (Loaded priority)
            // so chevron visibility and scroll metrics are correct.
            // ScrollChanged event handles UpdateScrollButtonsVisibility.
            Dispatcher.InvokeAsync(() =>
            {
                if (PART_ScrollViewer != null)
                {
                    AnimateScroll(PART_ScrollViewer.ScrollableWidth);
                }
            }, DispatcherPriority.Loaded);
        }
    }

    private EventHandler? _currentScrollAnimation;

    /// <summary>The tab border currently subscribed to <see cref="OnActiveTabSizeChanged"/>.</summary>
    private FrameworkElement? _sizeTrackedBorder;

    /// <summary>A close fade that has not executed yet (tab + its template root).</summary>
    private (object Tab, FrameworkElement Root)? _pendingClose;

    private void AnimateScroll(double targetOffset)
    {
        if (PART_ScrollViewer == null) return;

        StopScrollAnimation();

        targetOffset = Math.Max(0, Math.Min(PART_ScrollViewer.ScrollableWidth, targetOffset));
        double startOffset = PART_ScrollViewer.HorizontalOffset;
        if (Math.Abs(startOffset - targetOffset) < 0.5) return;

        var startTime = DateTime.UtcNow;
        var duration = TimeSpan.FromMilliseconds(220);

        void OnRendering(object? sender, EventArgs e)
        {
            var elapsed = DateTime.UtcNow - startTime;
            double progress = Math.Min(1.0, elapsed.TotalMilliseconds / duration.TotalMilliseconds);
            double eased = 1.0 - Math.Pow(1.0 - progress, 3);
            // Clamp against the *current* extent every frame: a tab added or closed mid-animation
            // changes ScrollableWidth, and a target captured at call time would land off the end.
            double target = Math.Min(targetOffset, Math.Max(0, PART_ScrollViewer.ScrollableWidth));
            double currentOffset = startOffset + (target - startOffset) * eased;
            PART_ScrollViewer.ScrollToHorizontalOffset(currentOffset);

            if (progress >= 1.0)
            {
                StopScrollAnimation();
                UpdateScrollButtonsVisibility();
            }
        }

        _currentScrollAnimation = OnRendering;
        CompositionTarget.Rendering += _currentScrollAnimation;
    }

    /// <summary>
    /// Detaches the per-frame scroll callback. <see cref="CompositionTarget.Rendering"/> is a static
    /// event that fires every frame whether or not the strip is on screen, so it is also released
    /// when the control is merely hidden (not just unloaded).
    /// </summary>
    private void StopScrollAnimation()
    {
        if (_currentScrollAnimation != null)
        {
            CompositionTarget.Rendering -= _currentScrollAnimation;
            _currentScrollAnimation = null;
        }
    }

    private void OnStripIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            StopScrollAnimation();
        }
    }

    private void ScrollSelectedIntoView()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!IsLoaded || SelectedItem == null || PART_ScrollViewer == null) return;
            var container = PART_ItemsControl.ItemContainerGenerator.ContainerFromItem(SelectedItem) as FrameworkElement;
            if (container == null || container.ActualWidth <= 0 || !container.IsDescendantOf(PART_ScrollViewer)) return;

            try
            {
                Point point = container.TranslatePoint(new Point(0, 0), PART_ScrollViewer);
                double itemLeft = point.X + PART_ScrollViewer.HorizontalOffset;
                double itemRight = itemLeft + container.ActualWidth;

                double viewLeft = PART_ScrollViewer.HorizontalOffset;
                double viewRight = viewLeft + PART_ScrollViewer.ViewportWidth;

                if (itemLeft < viewLeft)
                {
                    AnimateScroll(Math.Max(0, itemLeft - 10));
                }
                else if (itemRight > viewRight)
                {
                    AnimateScroll(Math.Min(PART_ScrollViewer.ScrollableWidth, itemRight - PART_ScrollViewer.ViewportWidth + 10));
                }
            }
            catch
            {
            }
        }, DispatcherPriority.Loaded);
    }

    private void UpdateScrollButtonsVisibility()
    {
        if (PART_ScrollViewer == null || PART_ScrollLeftButton == null || PART_ScrollRightButton == null)
            return;

        // Left chevron visible when scrolled right of start
        PART_ScrollLeftButton.Visibility = PART_ScrollViewer.HorizontalOffset > 0.5
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Right chevron visible when content overflows past right edge
        PART_ScrollRightButton.Visibility = PART_ScrollViewer.HorizontalOffset < (PART_ScrollViewer.ScrollableWidth - 0.5)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateScrollButtonsVisibility();
    }

    private void ScrollLeftButton_Click(object sender, RoutedEventArgs e)
    {
        if (PART_ScrollViewer == null) return;
        double newOffset = Math.Max(0, PART_ScrollViewer.HorizontalOffset - 120);
        AnimateScroll(newOffset);
    }

    private void ScrollRightButton_Click(object sender, RoutedEventArgs e)
    {
        if (PART_ScrollViewer == null) return;
        double newOffset = Math.Min(PART_ScrollViewer.ScrollableWidth, PART_ScrollViewer.HorizontalOffset + 120);
        AnimateScroll(newOffset);
    }

    private void OnActiveTabSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement fe && Equals(fe.DataContext, SelectedItem))
        {
            // Use animate: true so we don't kill an in-flight slide animation
            // (e.g. FontWeight SemiBold trigger causes tab width change mid-slide)
            UpdateIndicator(animate: true);
        }
    }

    private void UpdateDividersVisibility()
    {
        if (PART_ItemsControl == null) return;

        int count = PART_ItemsControl.Items.Count;
        int activeIndex = -1;
        if (SelectedItem != null)
        {
            activeIndex = PART_ItemsControl.Items.IndexOf(SelectedItem);
        }

        for (int i = 0; i < count; i++)
        {
            if (PART_ItemsControl.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement container)
            {
                var divider = FindVisualChildByName<Border>(container, "TabDivider");
                if (divider != null)
                {
                    // Hide divider if:
                    // 1. It is the last tab (before the '+' button)
                    // 2. This tab is active (no divider to its right)
                    // 3. The NEXT tab is active (no divider to the active tab's left)
                    bool shouldHide = (i == count - 1) || (i == activeIndex) || (i == activeIndex - 1);
                    divider.Visibility = shouldHide ? Visibility.Collapsed : Visibility.Visible;
                }
            }
        }
    }

    private static T? FindVisualChildByName<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && typed.Name == name)
            {
                return typed;
            }
            var result = FindVisualChildByName<T>(child, name);
            if (result != null) return result;
        }
        return null;
    }

    private static T? FindVisualParent<T>(DependencyObject child, string? name = null) where T : FrameworkElement
    {
        DependencyObject current = child;
        while (current != null)
        {
            current = VisualTreeHelper.GetParent(current);
            if (current is T typed && (name == null || typed.Name == name))
            {
                return typed;
            }
        }
        return null;
    }
}
