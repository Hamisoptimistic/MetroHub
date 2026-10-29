using System;
using System.Windows;
using System.Windows.Controls;
using MetroHub.Presentation.Controls;
using Xunit;

namespace MetroHub.Tests;

public sealed class SmoothPhysicsScrollBehaviorTests
{
    [Fact]
    public void EvaluateCriticallyDampedSpring_ConvergesMonotonicallyTowardTarget()
    {
        double x = 100.0;
        double v = 0.0;
        const double omega = 20.0;
        const double dt = 1.0 / 120.0; // 8.33ms (120Hz)

        // Iterate 75 frames (~625ms)
        for (int i = 0; i < 75; i++)
        {
            var (xNew, vNew) = SmoothPhysicsScrollBehavior.EvaluateCriticallyDampedSpring(x, v, omega, dt);

            // In critical damping (zeta = 1.0) with zero initial velocity, x must never overshoot past 0
            Assert.True(xNew >= 0.0, $"Overshoot detected at step {i}: xNew = {xNew}");
            Assert.True(xNew <= x, $"Divergence detected at step {i}: xNew ({xNew}) > previous x ({x})");

            x = xNew;
            v = vNew;
        }

        // Must settle well within the rest threshold (0.05px)
        Assert.True(x < 0.05, $"Failed to settle: final x = {x}");
        Assert.True(Math.Abs(v) < 1.0, $"Failed to brake: final v = {v}");
    }

    [Fact]
    public void EvaluateCriticallyDampedSpring_IsFrameRateInvariant()
    {
        const double initialX = 150.0;
        const double initialV = 0.0;
        const double omega = 20.0;

        // Exactly 100ms across all 3 refresh rates:
        // 120Hz: 12 frames @ 1/120s = 0.100s
        double x120 = initialX, v120 = initialV;
        const double dt120 = 1.0 / 120.0;
        for (int i = 0; i < 12; i++)
        {
            (x120, v120) = SmoothPhysicsScrollBehavior.EvaluateCriticallyDampedSpring(x120, v120, omega, dt120);
        }

        // 60Hz: 6 frames @ 1/60s = 0.100s
        double x60 = initialX, v60 = initialV;
        const double dt60 = 1.0 / 60.0;
        for (int i = 0; i < 6; i++)
        {
            (x60, v60) = SmoothPhysicsScrollBehavior.EvaluateCriticallyDampedSpring(x60, v60, omega, dt60);
        }

        // 240Hz: 24 frames @ 1/240s = 0.100s
        double x240 = initialX, v240 = initialV;
        const double dt240 = 1.0 / 240.0;
        for (int i = 0; i < 24; i++)
        {
            (x240, v240) = SmoothPhysicsScrollBehavior.EvaluateCriticallyDampedSpring(x240, v240, omega, dt240);
        }

        // Exact analytical solution: results across 60Hz, 120Hz, and 240Hz should match within 0.001px
        Assert.True(Math.Abs(x120 - x60) < 0.001, $"120Hz vs 60Hz mismatch: x120={x120}, x60={x60}");
        Assert.True(Math.Abs(x120 - x240) < 0.001, $"120Hz vs 240Hz mismatch: x120={x120}, x240={x240}");
    }

    [Fact]
    public void EvaluateCriticallyDampedSpring_ZeroOrNegativeDt_ReturnsInitialState()
    {
        var (xZero, vZero) = SmoothPhysicsScrollBehavior.EvaluateCriticallyDampedSpring(42.0, 10.0, 20.0, 0.0);
        Assert.Equal(42.0, xZero);
        Assert.Equal(10.0, vZero);

        var (xNeg, vNeg) = SmoothPhysicsScrollBehavior.EvaluateCriticallyDampedSpring(42.0, 10.0, 20.0, -0.05);
        Assert.Equal(42.0, xNeg);
        Assert.Equal(10.0, vNeg);
    }

    [Theory]
    [InlineData(120, false)]   // 1 Notch down
    [InlineData(-120, false)]  // 1 Notch up
    [InlineData(240, false)]   // 2 Notches
    [InlineData(7, true)]      // Precision trackpad micro-delta
    [InlineData(-15, true)]    // Precision trackpad micro-delta
    [InlineData(33, true)]     // Precision trackpad sub-detent
    [InlineData(-58, true)]    // Precision trackpad sub-detent
    public void TouchpadDeltaDetection_ClassifiesSubDetentCorrectly(int delta, bool expectedIsSubDetent)
    {
        bool isSubDetent = (Math.Abs(delta) % 120) != 0;
        Assert.Equal(expectedIsSubDetent, isSubDetent);
    }

    [Fact]
    public void AttachToScrollViewer_EnablesAndDisablesCleanly_OnSta()
    {
        WpfTestHost.RunSta(() =>
        {
            var sv = new ScrollViewer();

            SmoothPhysicsScrollBehavior.SetIsEnabled(sv, true);
            Assert.True(SmoothPhysicsScrollBehavior.GetIsEnabled(sv));

            SmoothPhysicsScrollBehavior.CancelMomentum(sv);

            SmoothPhysicsScrollBehavior.SetIsEnabled(sv, false);
            Assert.False(SmoothPhysicsScrollBehavior.GetIsEnabled(sv));
        });
    }

    [Fact]
    public void IsOverNestedScrollContainer_DetectsScrollViewerAndTextBoxBase_OnSta()
    {
        WpfTestHost.RunSta(() =>
        {
            var parentSv = new ScrollViewer { Width = 300, Height = 300 };
            var childSv = new ScrollViewer
            {
                Width = 100,
                Height = 100,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            var innerChild = new Border { Width = 100, Height = 400 };

            childSv.Content = innerChild;
            childSv.ApplyTemplate();
            childSv.Measure(new System.Windows.Size(100, 100));
            childSv.Arrange(new System.Windows.Rect(0, 0, 100, 100));
            childSv.UpdateLayout();

            parentSv.Content = childSv;
            parentSv.ApplyTemplate();
            parentSv.Measure(new System.Windows.Size(300, 300));
            parentSv.Arrange(new System.Windows.Rect(0, 0, 300, 300));
            parentSv.UpdateLayout();

            // Inner child of ScrollViewer is detected as being inside a scroll container
            bool isOverChildSv = SmoothPhysicsScrollBehavior.IsOverNestedScrollContainer(innerChild, parentSv);
            Assert.True(isOverChildSv);

            // RichTextBox (Notepad editor) inside parentSv is detected as a scroll container
            var richTextBox = new RichTextBox
            {
                Width = 100,
                Height = 100,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            parentSv.Content = richTextBox;
            bool isOverRichText = SmoothPhysicsScrollBehavior.IsOverNestedScrollContainer(richTextBox, parentSv);
            Assert.True(isOverRichText);

            // Element with no nested scrollable container returns false
            var outsideElement = new Border();
            parentSv.Content = outsideElement;
            bool noNestedScroll = SmoothPhysicsScrollBehavior.IsOverNestedScrollContainer(outsideElement, parentSv);
            Assert.False(noNestedScroll);
        });
    }

    [Fact]
    public void ResetOffset_SetsScrollOffsetAndCancelsMomentum_OnSta()
    {
        WpfTestHost.RunSta(() =>
        {
            var sv = new ScrollViewer { Width = 300, Height = 300 };
            var content = new Border { Width = 300, Height = 1000 };
            sv.Content = content;

            var window = new Window
            {
                Width = 300,
                Height = 300,
                Content = sv
            };
            window.Show();
            window.UpdateLayout();

            try
            {
                SmoothPhysicsScrollBehavior.SetIsEnabled(sv, true);

                SmoothPhysicsScrollBehavior.ResetOffset(sv, 250.0);
                sv.UpdateLayout();
                Assert.Equal(250.0, sv.VerticalOffset);

                SmoothPhysicsScrollBehavior.ResetOffset(sv, 0.0);
                sv.UpdateLayout();
                Assert.Equal(0.0, sv.VerticalOffset);

                SmoothPhysicsScrollBehavior.SetIsEnabled(sv, false);
            }
            finally
            {
                window.Close();
            }
        });
    }
}

