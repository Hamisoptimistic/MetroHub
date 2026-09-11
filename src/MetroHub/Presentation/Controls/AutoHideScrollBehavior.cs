using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MetroHub.Presentation.Controls;

/// <summary>
/// Attached behavior for ScrollViewer that automatically hides the scrollbar by default,
/// smoothly reveals it upon scrolling or hover, and fades it back out after inactivity.
/// </summary>
public static class AutoHideScrollBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(AutoHideScrollBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static readonly DependencyProperty ScrollStateProperty =
        DependencyProperty.RegisterAttached(
            "ScrollState",
            typeof(ScrollStateHolder),
            typeof(AutoHideScrollBehavior),
            new PropertyMetadata(null));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer scrollViewer) return;

        if ((bool)e.NewValue)
        {
            var holder = new ScrollStateHolder(scrollViewer);
            scrollViewer.SetValue(ScrollStateProperty, holder);
        }
        else
        {
            if (scrollViewer.GetValue(ScrollStateProperty) is ScrollStateHolder holder)
            {
                holder.Dispose();
                scrollViewer.ClearValue(ScrollStateProperty);
            }
        }
    }

    private sealed class ScrollStateHolder : IDisposable
    {
        private readonly ScrollViewer _scrollViewer;
        private ScrollBar? _verticalScrollBar;
        private readonly DispatcherTimer _hideTimer;
        private bool _isDisposed;

        public ScrollStateHolder(ScrollViewer scrollViewer)
        {
            _scrollViewer = scrollViewer;

            _hideTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(1200)
            };
            _hideTimer.Tick += OnHideTimerTick;

            _scrollViewer.Loaded += OnLoaded;
            _scrollViewer.Unloaded += OnUnloaded;
            _scrollViewer.ScrollChanged += OnScrollChanged;

            if (_scrollViewer.IsLoaded)
            {
                AttachScrollBar();
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            AttachScrollBar();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _hideTimer.Stop();
        }

        private void AttachScrollBar()
        {
            if (_verticalScrollBar != null) return;

            _verticalScrollBar = _scrollViewer.Template?.FindName("PART_VerticalScrollBar", _scrollViewer) as ScrollBar;
            if (_verticalScrollBar == null)
            {
                _verticalScrollBar = FindChild<ScrollBar>(_scrollViewer, "PART_VerticalScrollBar");
            }

            if (_verticalScrollBar != null)
            {
                _verticalScrollBar.Opacity = 0.0;
                _verticalScrollBar.MouseEnter += OnScrollBarMouseEnter;
                _verticalScrollBar.MouseLeave += OnScrollBarMouseLeave;
            }
        }

        private void OnScrollBarMouseEnter(object sender, MouseEventArgs e)
        {
            _hideTimer.Stop();
            AnimateOpacity(1.0, 100);
        }

        private void OnScrollBarMouseLeave(object sender, MouseEventArgs e)
        {
            _hideTimer.Stop();
            _hideTimer.Start();
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_isDisposed) return;

            // Only show when there is an actual vertical scroll change
            if (Math.Abs(e.VerticalChange) > 0.01)
            {
                if (_verticalScrollBar == null)
                {
                    AttachScrollBar();
                }

                if (_verticalScrollBar != null && _scrollViewer.ComputedVerticalScrollBarVisibility == Visibility.Visible)
                {
                    _hideTimer.Stop();
                    AnimateOpacity(1.0, 100);
                    _hideTimer.Start();
                }
            }
        }

        private void OnHideTimerTick(object? sender, EventArgs e)
        {
            _hideTimer.Stop();

            if (_verticalScrollBar == null) return;

            // Do not hide if the user is hovering over the scrollbar or actively dragging the thumb
            if (_verticalScrollBar.IsMouseOver || Mouse.LeftButton == MouseButtonState.Pressed)
            {
                _hideTimer.Start();
                return;
            }

            AnimateOpacity(0.0, 300);
        }

        private void AnimateOpacity(double toValue, double durationMs)
        {
            if (_verticalScrollBar == null) return;

            var anim = new DoubleAnimation
            {
                To = toValue,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            _verticalScrollBar.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        private static T? FindChild<T>(DependencyObject parent, string childName) where T : DependencyObject
        {
            if (parent == null) return null;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild && (child as FrameworkElement)?.Name == childName)
                {
                    return typedChild;
                }

                var found = FindChild<T>(child, childName);
                if (found != null) return found;
            }

            return null;
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _hideTimer.Stop();
            _scrollViewer.Loaded -= OnLoaded;
            _scrollViewer.Unloaded -= OnUnloaded;
            _scrollViewer.ScrollChanged -= OnScrollChanged;

            if (_verticalScrollBar != null)
            {
                _verticalScrollBar.MouseEnter -= OnScrollBarMouseEnter;
                _verticalScrollBar.MouseLeave -= OnScrollBarMouseLeave;
            }
        }
    }
}
