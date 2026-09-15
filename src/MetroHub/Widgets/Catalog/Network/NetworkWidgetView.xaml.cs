using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MetroHub.Widgets.Catalog.Network;

public partial class NetworkWidgetView : UserControl
{
    public static readonly DependencyProperty AnimatedVerticalOffsetProperty =
        DependencyProperty.RegisterAttached(
            "AnimatedVerticalOffset",
            typeof(double),
            typeof(NetworkWidgetView),
            new FrameworkPropertyMetadata(0.0, OnAnimatedVerticalOffsetChanged));

    public static double GetAnimatedVerticalOffset(DependencyObject obj) => (double)obj.GetValue(AnimatedVerticalOffsetProperty);
    public static void SetAnimatedVerticalOffset(DependencyObject obj, double value) => obj.SetValue(AnimatedVerticalOffsetProperty, value);

    private static void OnAnimatedVerticalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer sv)
        {
            sv.ScrollToVerticalOffset((double)e.NewValue);
        }
    }

    public NetworkWidgetView()
    {
        InitializeComponent();
    }

    private void EthernetToggleSwitch_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (DataContext is NetworkWidgetViewModel vm && vm.CanToggleEthernet)
        {
            vm.ToggleEthernetCommand.Execute(null);
        }
    }

    private void EthernetToggleSwitch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter)
        {
            e.Handled = true;
            if (DataContext is NetworkWidgetViewModel vm && vm.CanToggleEthernet)
            {
                vm.ToggleEthernetCommand.Execute(null);
            }
        }
    }

    private void WifiPasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (DataContext is NetworkWidgetViewModel parentVm)
                parentVm.ConnectWithPasswordCommand.Execute(sender);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (DataContext is NetworkWidgetViewModel parentVm)
                parentVm.CancelWifiPasswordCommand.Execute(sender);
        }
    }

    private void AccordionDrawer_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is FrameworkElement drawer)
        {
            Dispatcher.InvokeAsync(() =>
            {
                EnsureItemFitsInView(drawer);

                var pb = FindVisualChild<Wpf.Ui.Controls.PasswordBox>(drawer);
                if (pb != null && pb.IsVisible)
                {
                    pb.Focus();
                }
            }, DispatcherPriority.Loaded);
        }
    }

    private void AccordionDrawer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement drawer) return;
        if (!drawer.IsVisible || drawer.ActualHeight <= 0) return;
        if (e.NewSize.Height <= e.PreviousSize.Height) return;

        // Continuously scroll the viewport as the drawer expands so the entire card fits cleanly
        EnsureItemFitsInView(drawer);
    }

    private void EnsureItemFitsInView(FrameworkElement drawer)
    {
        try
        {
            var scrollViewer = FindVisualParent<ScrollViewer>(drawer);
            var itemContainer = FindVisualParent<ListViewItem>(drawer);
            if (scrollViewer == null || itemContainer == null) return;
            if (scrollViewer.ViewportHeight <= 0) return;

            GeneralTransform transform = itemContainer.TransformToAncestor(scrollViewer);
            Point itemPos = transform.Transform(new Point(0, 0));
            double topInViewport = itemPos.Y;
            double bottomInViewport = topInViewport + itemContainer.ActualHeight;

            // Target bottom margin: 12px above viewport bottom so buttons have clear breathing room
            double overflow = bottomInViewport - (scrollViewer.ViewportHeight - 12.0);

            if (overflow > 0)
            {
                // Item extends past the bottom of the viewport: scroll down
                double targetOffset = scrollViewer.VerticalOffset + overflow;

                // Ensure scrolling down doesn't push the top of the item off-screen if it fits in viewport
                if (itemContainer.ActualHeight <= scrollViewer.ViewportHeight)
                {
                    double maxAllowedOffset = scrollViewer.VerticalOffset + topInViewport - 6.0;
                    targetOffset = Math.Min(targetOffset, maxAllowedOffset);
                }

                scrollViewer.ScrollToVerticalOffset(Math.Max(0.0, targetOffset));
            }
            else if (topInViewport < 6.0)
            {
                // Item is clipped above the top of the viewport: scroll up
                double targetOffset = scrollViewer.VerticalOffset + topInViewport - 6.0;
                scrollViewer.ScrollToVerticalOffset(Math.Max(0.0, targetOffset));
            }
        }
        catch
        {
            drawer.BringIntoView();
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T parent) return parent;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent == null) return null;
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild) return typedChild;
            var descendant = FindVisualChild<T>(child);
            if (descendant != null) return descendant;
        }
        return null;
    }
}

