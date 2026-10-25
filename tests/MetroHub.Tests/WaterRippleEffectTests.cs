using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using MetroHub.Presentation.Controllers;
using MetroHub.Presentation.Shaders;
using Xunit;

namespace MetroHub.Tests;

public sealed class WaterRippleEffectTests
{
    [Fact]
    public void BytecodeLoads_FromEmbeddedStream_Successfully()
    {
        WpfTestHost.RunSta(() =>
        {
            var effect = MetroShaderEffect.TryCreate("WaterRipple");
            Assert.NotNull(effect);

            Assert.NotNull(effect.PixelShader);
            Assert.True(effect.PixelShader.IsFrozen);
            Assert.Equal(MetroShaderEffect.DefaultWaterRippleParams, effect.EffectParams);
            Assert.Equal(18.0, effect.EffectParams.X);
            Assert.Equal(0.006, effect.EffectParams.Y);
            Assert.Equal(2.5, effect.EffectParams.Z);
            Assert.Equal(2.2, effect.EffectParams.W);
        });
    }

    [Fact]
    public void GetOrCreatePixelShader_CachesAndReusesInstance()
    {
        WpfTestHost.RunSta(() =>
        {
            var ps1 = MetroShaderEffect.GetOrCreatePixelShader("WaterRipple");
            var ps2 = MetroShaderEffect.GetOrCreatePixelShader("WaterRipple");

            Assert.NotNull(ps1);
            Assert.Same(ps1, ps2);
            Assert.True(ps1.IsFrozen);
        });
    }

    [Fact]
    public void MetroShaderEffect_CustomParameters_AreRetained()
    {
        WpfTestHost.RunSta(() =>
        {
            var custom = new Point4D(24.0, 0.012, 3.0, 1.8);
            var effect = new MetroShaderEffect("WaterRipple", custom);

            Assert.Equal(custom, effect.EffectParams);
            Assert.Equal("WaterRipple", effect.ShaderName);
        });
    }

    [Fact]
    public void GetOrCreatePixelShader_ThrowsOnInvalidShaderName()
    {
        WpfTestHost.RunSta(() =>
        {
            Assert.Throws<InvalidOperationException>(() =>
                MetroShaderEffect.GetOrCreatePixelShader("NonExistentShader_ABC123"));
        });
    }

    [Fact]
    public void BackdropShaderDriver_AttachAndDetach_ManagesActiveState()
    {
        WpfTestHost.RunSta(() =>
        {
            var window = new Window();
            using var driver = new BackdropShaderDriver(window) { BypassHardwareTierCheck = true };
            var effect = MetroShaderEffect.TryCreate("WaterRipple")!;

            Assert.False(driver.IsRendering);
            Assert.Null(driver.ActiveEffect);

            driver.Attach(effect);
            Assert.Same(effect, driver.ActiveEffect);

            // Window is not visible initially, so driver must not run rendering loop
            Assert.False(driver.IsRendering);

            driver.Detach();
            Assert.Null(driver.ActiveEffect);
            Assert.False(driver.IsRendering);
        });
    }

    [Fact]
    public void BackdropShaderDriver_ThrowsOnNullEffectOrWindow()
    {
        WpfTestHost.RunSta(() =>
        {
            Assert.Throws<ArgumentNullException>(() => new BackdropShaderDriver(null!));

            var window = new Window();
            using var driver = new BackdropShaderDriver(window);
            Assert.Throws<ArgumentNullException>(() => driver.Attach(null!));
        });
    }

    [Fact]
    public void BackdropShaderDriver_Tick_UpdatesFrameState()
    {
        WpfTestHost.RunSta(() =>
        {
            var window = new Window();
            using var driver = new BackdropShaderDriver(window) { BypassHardwareTierCheck = true };
            var effect = MetroShaderEffect.TryCreate("WaterRipple")!;
            driver.Attach(effect);

            // Initial FrameState
            Assert.Equal(0, effect.FrameState.X);

            driver.Tick();

            // FrameState.Y is aspect ratio (defaults to fallback 16/9 if window has no layout yet)
            Assert.True(effect.FrameState.Y > 0);
            Assert.True(Math.Abs(effect.FrameState.Y - (16.0 / 9.0)) < 0.001 || effect.FrameState.Y > 0.5);
        });
    }

    [Fact]
    public void BackdropShaderDriver_Tick_CalculatesTargetAspectRatio()
    {
        WpfTestHost.RunSta(() =>
        {
            var window = new Window();
            var target = new Border { Width = 2560, Height = 1080 };
            target.Measure(new Size(2560, 1080));
            target.Arrange(new Rect(0, 0, 2560, 1080));

            using var driver = new BackdropShaderDriver(window, target) { BypassHardwareTierCheck = true };
            var effect = MetroShaderEffect.TryCreate("WaterRipple")!;
            driver.Attach(effect);

            driver.Tick();

            double expectedAspect = 2560.0 / 1080.0;
            Assert.Equal(expectedAspect, effect.FrameState.Y, 4);
        });
    }

    [Fact]
    public void BackdropShaderDriver_Tick_GracefullyHandlesInvalidDimensions()
    {
        WpfTestHost.RunSta(() =>
        {
            var window = new Window { Width = 0, Height = 0 };
            var target = new Border { Width = 0, Height = 0 };

            using var driver = new BackdropShaderDriver(window, target) { BypassHardwareTierCheck = true };
            var effect = MetroShaderEffect.TryCreate("WaterRipple")!;
            driver.Attach(effect);

            driver.Tick();

            // Must fall back to 16/9 and not produce NaN or 0
            Assert.False(double.IsNaN(effect.FrameState.Y));
            Assert.False(double.IsInfinity(effect.FrameState.Y));
            Assert.True(effect.FrameState.Y > 0);
        });
    }

    [Fact]
    public void BackdropShaderDriver_WindowClose_AutomaticallyDisposes()
    {
        WpfTestHost.RunSta(() =>
        {
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = false
            };
            var driver = new BackdropShaderDriver(window) { BypassHardwareTierCheck = true };
            var effect = MetroShaderEffect.TryCreate("WaterRipple")!;
            driver.Attach(effect);

            window.Close();

            // Driver must be detached and disposed on window close
            Assert.Null(driver.ActiveEffect);
            Assert.False(driver.IsRendering);

            // Further Attach calls on disposed driver should be safe no-ops
            driver.Attach(effect);
            Assert.Null(driver.ActiveEffect);
        });
    }

    [Fact]
    public void BackdropShaderDriver_StateAndVisibilityTransitions_ControlRendering()
    {
        WpfTestHost.RunSta(() =>
        {
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = false
            };
            using var driver = new BackdropShaderDriver(window) { BypassHardwareTierCheck = true };
            var effect = MetroShaderEffect.TryCreate("WaterRipple")!;
            driver.Attach(effect);

            // Initially not visible
            Assert.False(driver.IsRendering);

            // Window shown -> rendering starts
            window.Show();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Assert.True(driver.IsRendering);

            // Window minimized -> rendering halts
            window.WindowState = WindowState.Minimized;
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Assert.False(driver.IsRendering);

            // Window restored -> rendering resumes
            window.WindowState = WindowState.Normal;
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Assert.True(driver.IsRendering);

            // Window hidden -> rendering halts
            window.Hide();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Assert.False(driver.IsRendering);

            window.Close();
        });
    }

    [Fact]
    public void BackdropShaderDriver_WithoutHardwareAcceleration_DoesNotRender()
    {
        WpfTestHost.RunSta(() =>
        {
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = false
            };
            // BypassHardwareTierCheck is false by default
            using var driver = new BackdropShaderDriver(window);
            var effect = MetroShaderEffect.TryCreate("WaterRipple")!;

            driver.Attach(effect);
            window.Show();

            if (!BackdropShaderDriver.IsHardwareAccelerated)
            {
                Assert.False(driver.IsRendering);
            }

            window.Close();
        });
    }

    [Fact]
    public void MetroShaderEffect_InputProperty_InitializedProperly()
    {
        WpfTestHost.RunSta(() =>
        {
            var effect = MetroShaderEffect.TryCreate("WaterRipple")!;
            Assert.NotNull(MetroShaderEffect.InputProperty);
            Assert.NotNull(effect.Input);
            Assert.Equal("ImplicitInputBrush", effect.Input.GetType().Name);
        });
    }

    [Fact]
    public void MetroShaderEffect_PatternMatchingFactory_ResolvesEffects()
    {
        WpfTestHost.RunSta(() =>
        {
            Assert.True(MetroShaderEffect.IsSupported("WaterRipple"));
            Assert.True(MetroShaderEffect.IsSupported("waterripple"));
            Assert.False(MetroShaderEffect.IsSupported("NonExistentEffect"));
            Assert.False(MetroShaderEffect.IsSupported(null));

            var effect = MetroShaderEffect.TryCreate("WaterRipple");
            Assert.NotNull(effect);
            Assert.Equal("WaterRipple", effect.ShaderName);
            Assert.Equal(MetroShaderEffect.DefaultWaterRippleParams, effect.EffectParams);

            var nonExistent = MetroShaderEffect.TryCreate("NonExistentEffect");
            Assert.Null(nonExistent);
        });
    }
}

