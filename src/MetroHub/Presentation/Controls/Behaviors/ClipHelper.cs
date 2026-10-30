using System;
using System.Windows;
using System.Windows.Media;

namespace MetroHub.Presentation.Controls;

/// <summary>
/// High-performance attached behavior that applies a hardware-accelerated Direct3D
/// rounded rectangular clip to enforce corner radius boundaries on content containers,
/// preventing child visuals from bleeding while preserving concentric arc centers.
/// </summary>
public static class ClipHelper
{
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.RegisterAttached(
            "CornerRadius",
            typeof(CornerRadius),
            typeof(ClipHelper),
            new PropertyMetadata(new CornerRadius(0), OnGeometryChanged));

    public static CornerRadius GetCornerRadius(DependencyObject obj) => (CornerRadius)obj.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject obj, CornerRadius value) => obj.SetValue(CornerRadiusProperty, value);

    public static readonly DependencyProperty BorderThicknessProperty =
        DependencyProperty.RegisterAttached(
            "BorderThickness",
            typeof(Thickness),
            typeof(ClipHelper),
            new PropertyMetadata(new Thickness(0), OnGeometryChanged));

    public static Thickness GetBorderThickness(DependencyObject obj) => (Thickness)obj.GetValue(BorderThicknessProperty);
    public static void SetBorderThickness(DependencyObject obj, Thickness value) => obj.SetValue(BorderThicknessProperty, value);

    public static readonly DependencyProperty IsCornerClippedProperty =
        DependencyProperty.RegisterAttached(
            "IsCornerClipped",
            typeof(bool),
            typeof(ClipHelper),
            new PropertyMetadata(false, OnIsCornerClippedChanged));

    public static bool GetIsCornerClipped(DependencyObject obj) => (bool)obj.GetValue(IsCornerClippedProperty);
    public static void SetIsCornerClipped(DependencyObject obj, bool value) => obj.SetValue(IsCornerClippedProperty, value);

    private static void OnIsCornerClippedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;

        fe.Loaded -= OnElementLoaded;
        fe.Unloaded -= OnElementUnloaded;
        fe.SizeChanged -= OnElementSizeChanged;

        if ((bool)e.NewValue)
        {
            fe.Loaded += OnElementLoaded;
            fe.Unloaded += OnElementUnloaded;
            fe.SizeChanged += OnElementSizeChanged;
            UpdateClip(fe);
        }
        else
        {
            fe.Clip = null;
        }
    }

    private static void OnGeometryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement fe && GetIsCornerClipped(fe))
        {
            UpdateClip(fe);
        }
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe) UpdateClip(fe);
    }

    private static void OnElementUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe) fe.Clip = null;
    }

    private static void OnElementSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement fe) UpdateClip(fe);
    }

    private static void UpdateClip(FrameworkElement fe)
    {
        double width = fe.ActualWidth;
        double height = fe.ActualHeight;

        // Rule 7: Zero-size guard during measure/render passes
        if (width <= 0 || height <= 0)
        {
            fe.Clip = null;
            return;
        }

        CornerRadius cr = GetCornerRadius(fe);
        Thickness bt = GetBorderThickness(fe);

        // Rule 2 & 4: Inset clip bounds by BorderThickness to preserve concentric arc center (R_outer, R_outer)
        double left = bt.Left;
        double top = bt.Top;
        double right = Math.Max(left, width - bt.Right);
        double bottom = Math.Max(top, height - bt.Bottom);
        double insetWidth = right - left;
        double insetHeight = bottom - top;

        if (insetWidth <= 0 || insetHeight <= 0)
        {
            fe.Clip = null;
            return;
        }

        // Concentric inner radii: R_inner = max(0, R_outer - T)
        double rTL = Math.Max(0, cr.TopLeft - Math.Max(bt.Left, bt.Top));
        double rTR = Math.Max(0, cr.TopRight - Math.Max(bt.Right, bt.Top));
        double rBR = Math.Max(0, cr.BottomRight - Math.Max(bt.Right, bt.Bottom));
        double rBL = Math.Max(0, cr.BottomLeft - Math.Max(bt.Left, bt.Bottom));


        double maxR = Math.Min(insetWidth / 2.0, insetHeight / 2.0);
        rTL = Math.Min(rTL, maxR);
        rTR = Math.Min(rTR, maxR);
        rBR = Math.Min(rBR, maxR);
        rBL = Math.Min(rBL, maxR);

        Rect insetRect = new Rect(left, top, insetWidth, insetHeight);

        // Rule 3: Uniform -> Fast frozen RectangleGeometry; Asymmetric -> Frozen StreamGeometry with ArcTo
        if (Math.Abs(rTL - rTR) < 0.001 && Math.Abs(rTL - rBR) < 0.001 && Math.Abs(rTL - rBL) < 0.001)
        {
            var rectGeometry = new RectangleGeometry(insetRect, rTL, rTL);
            rectGeometry.Freeze();
            fe.Clip = rectGeometry;
        }
        else
        {
            var streamGeom = new StreamGeometry();
            using (StreamGeometryContext ctx = streamGeom.Open())
            {
                ctx.BeginFigure(new Point(insetRect.Left + rTL, insetRect.Top), isFilled: true, isClosed: true);

                // Top edge & Top-Right arc
                ctx.LineTo(new Point(insetRect.Right - rTR, insetRect.Top), isStroked: true, isSmoothJoin: false);
                if (rTR > 0)
                {
                    ctx.ArcTo(new Point(insetRect.Right, insetRect.Top + rTR), new Size(rTR, rTR), 0, false, SweepDirection.Clockwise, true, false);
                }

                // Right edge & Bottom-Right arc
                ctx.LineTo(new Point(insetRect.Right, insetRect.Bottom - rBR), isStroked: true, isSmoothJoin: false);
                if (rBR > 0)
                {
                    ctx.ArcTo(new Point(insetRect.Right - rBR, insetRect.Bottom), new Size(rBR, rBR), 0, false, SweepDirection.Clockwise, true, false);
                }

                // Bottom edge & Bottom-Left arc
                ctx.LineTo(new Point(insetRect.Left + rBL, insetRect.Bottom), isStroked: true, isSmoothJoin: false);
                if (rBL > 0)
                {
                    ctx.ArcTo(new Point(insetRect.Left, insetRect.Bottom - rBL), new Size(rBL, rBL), 0, false, SweepDirection.Clockwise, true, false);
                }

                // Left edge & Top-Left arc
                ctx.LineTo(new Point(insetRect.Left, insetRect.Top + rTL), isStroked: true, isSmoothJoin: false);
                if (rTL > 0)
                {
                    ctx.ArcTo(new Point(insetRect.Left + rTL, insetRect.Top), new Size(rTL, rTL), 0, false, SweepDirection.Clockwise, true, false);
                }
            }
            streamGeom.Freeze();
            fe.Clip = streamGeom;
        }
    }
}
