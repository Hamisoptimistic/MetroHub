using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MetroHub.Core.Radio;

namespace MetroHub.Widgets.Catalog.Radio;

/// <summary>
/// GPU-accelerated 2D Fluid Mesh Reactive Aura for MetroHub radio station cards.
/// Combines a continuous full-bleed multi-color base spectrum with 4 floating radial color blooms
/// (Azure Blue, Lilac Violet, Neon Magenta, Radiant Orange).
/// Features unified fluid breathing across all colors and an adaptive beat-flux detector
/// that randomly pumps one of the colors on each kick drum hit.
/// Uses zero geometry transforms to ensure zero border gaps and zero bounding box seams.
/// </summary>
public partial class AtmosphericAuraControl : UserControl
{
    // ==========================================
    // Visual Tuning Constants
    // ==========================================
    // Blob Base Focal Centers (Normalized 0.0 - 1.0)
    private const float CyanBaseX = 0.25f, CyanBaseY = 0.22f, CyanBaseRadius = 0.80f;
    private const float VioletBaseX = 0.45f, VioletBaseY = 0.35f, VioletBaseRadius = 0.75f;
    private const float PinkBaseX = 0.52f, PinkBaseY = 0.72f, PinkBaseRadius = 0.80f;
    private const float OrangeBaseX = 0.80f, OrangeBaseY = 0.75f, OrangeBaseRadius = 0.75f;

    // Drift Motion Amplitudes (Normalized 0.0 - 1.0)
    private const float DriftAmpX = 0.09f;
    private const float DriftAmpY = 0.08f;

    // Base Fluid Cycle Period (Seconds)
    private const float CyclePeriod = 4.2f;

    // Breathing & Bass Pump Strengths
    private const float BreatheAmp = 0.09f;
    private const float PumpRadiusBoost = 0.32f;
    private const float PumpOpacityBoost = 0.38f;
    private const float ReleaseDecayTime = 0.16f; // 160ms decay
    private const float KickDebounceTime = 0.10f; // 100ms debounce

    // ==========================================
    // Dependency Properties
    // ==========================================
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive),
        typeof(bool),
        typeof(AtmosphericAuraControl),
        new PropertyMetadata(false, OnStateChanged));

    public static readonly DependencyProperty IsBufferingProperty = DependencyProperty.Register(
        nameof(IsBuffering),
        typeof(bool),
        typeof(AtmosphericAuraControl),
        new PropertyMetadata(false, OnStateChanged));

    public static readonly DependencyProperty CategoryProperty = DependencyProperty.Register(
        nameof(Category),
        typeof(string),
        typeof(AtmosphericAuraControl),
        new PropertyMetadata("ambient"));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public bool IsBuffering
    {
        get => (bool)GetValue(IsBufferingProperty);
        set => SetValue(IsBufferingProperty, value);
    }

    public string Category
    {
        get => (string)GetValue(CategoryProperty);
        set => SetValue(CategoryProperty, value);
    }

    private bool _isHooked;
    private DateTime _startTime = DateTime.UtcNow;
    private DateTime _lastFrameTime = DateTime.UtcNow;
    private long _lastRenderTicks;
    private static readonly long Throttled30FpsTicks = TimeSpan.FromMilliseconds(33).Ticks;

    // Adaptive beat detection & pump state
    private float _bassAvg = 0.15f;
    private float _prevBass;
    private float _kickEnv;
    private float _kickCooldown;
    private int _pumpingColorIndex = 2; // 0=Cyan, 1=Violet, 2=Pink, 3=Orange
    private static bool _isBatterySaver;

    static AtmosphericAuraControl()
    {
        try
        {
            UpdateBatterySaverState();
            Windows.System.Power.PowerManager.EnergySaverStatusChanged += (_, _) => UpdateBatterySaverState();
        }
        catch
        {
            _isBatterySaver = false;
        }
    }

    private static void UpdateBatterySaverState()
    {
        try
        {
            _isBatterySaver = Windows.System.Power.PowerManager.EnergySaverStatus == Windows.System.Power.EnergySaverStatus.On;
        }
        catch
        {
            _isBatterySaver = false;
        }
    }

    public AtmosphericAuraControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateHookState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnhookRendering();
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AtmosphericAuraControl control)
        {
            control.UpdateHookState();
        }
    }

    private void UpdateHookState()
    {
        if (!IsLoaded) return;

        bool shouldRender = IsActive || IsBuffering || (AuraRoot != null && AuraRoot.Opacity > 0.001);
        if (shouldRender)
        {
            if (AuraRoot != null && AuraRoot.Visibility != Visibility.Visible)
            {
                AuraRoot.Visibility = Visibility.Visible;
            }
            if (!_isHooked) HookRendering();
        }
        else
        {
            if (AuraRoot != null)
            {
                AuraRoot.Visibility = Visibility.Collapsed;
            }
            UnhookRendering();
        }
    }

    private void HookRendering()
    {
        if (_isHooked) return;
        _isHooked = true;
        _startTime = DateTime.UtcNow;
        _lastFrameTime = DateTime.UtcNow;
        CompositionTarget.Rendering += OnRendering;
    }

    private void UnhookRendering()
    {
        if (!_isHooked) return;
        _isHooked = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!IsVisible) return;

        if (_isBatterySaver)
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            if (nowTicks - _lastRenderTicks < Throttled30FpsTicks)
            {
                return;
            }
            _lastRenderTicks = nowTicks;
        }

        bool active = IsActive;
        bool buffering = IsBuffering;

        // 1. Smooth Fade-In and Fade-Out of the Aura Layer
        if (active || buffering)
        {
            if (AuraRoot.Visibility != Visibility.Visible)
            {
                AuraRoot.Visibility = Visibility.Visible;
            }
            if (AuraRoot.Opacity < 1.0)
            {
                AuraRoot.Opacity = Math.Min(1.0, AuraRoot.Opacity + 0.08);
            }
        }
        else
        {
            if (AuraRoot.Opacity > 0.0)
            {
                AuraRoot.Opacity = Math.Max(0.0, AuraRoot.Opacity - 0.06);
            }

            // Quiescence: Unhook and collapse when fully faded to 0 opacity
            if (AuraRoot.Opacity <= 0.001)
            {
                AuraRoot.Opacity = 0.0;
                AuraRoot.Visibility = Visibility.Collapsed;
                UnhookRendering();
                return;
            }
        }

        DateTime now = DateTime.UtcNow;
        double elapsed = (now - _startTime).TotalSeconds;
        float dt = Math.Clamp((float)(now - _lastFrameTime).TotalSeconds, 0.001f, 0.05f);
        _lastFrameTime = now;

        // 2. Continuous Unified Fluid Breathing (~4.2s cycle)
        double phase = elapsed * (Math.PI * 2.0 / CyclePeriod);
        float flowX = (float)(Math.Sin(phase * 0.9) * 0.10);
        float flowY = (float)(Math.Cos(phase * 0.7) * 0.08);
        float breathe = (float)(Math.Sin(phase) * BreatheAmp);

        // Whole-card spectrum base breathes and shifts
        BaseGradient.StartPoint = new Point(-0.15 + flowX - breathe, -0.15 + flowY - breathe);
        BaseGradient.EndPoint = new Point(1.10 + flowX + breathe, 1.10 + flowY + breathe);

        // Fluid Lissajous drift for individual accent blobs
        float driftCyanX = (float)(CyanBaseX + Math.Sin(phase * 0.8) * DriftAmpX);
        float driftCyanY = (float)(CyanBaseY + Math.Cos(phase * 0.7) * DriftAmpY);

        float driftVioletX = (float)(VioletBaseX + Math.Cos(phase * 0.85 + 1.2) * DriftAmpX);
        float driftVioletY = (float)(VioletBaseY + Math.Sin(phase * 0.75 + 0.8) * DriftAmpY);

        float driftPinkX = (float)(PinkBaseX + Math.Sin(phase * 0.8 + 2.1) * DriftAmpX);
        float driftPinkY = (float)(PinkBaseY + Math.Cos(phase * 0.9 + 1.7) * DriftAmpY);

        float driftOrangeX = (float)(OrangeBaseX + Math.Cos(phase * 0.75 + 3.4) * DriftAmpX);
        float driftOrangeY = (float)(OrangeBaseY + Math.Sin(phase * 0.85 + 2.9) * DriftAmpY);

        // 3. Render Buffering State: Gentle soothing wave
        if (buffering)
        {
            float p0 = (float)(Math.Sin(phase) * 0.5 + 0.5);
            float p1 = (float)(Math.Sin(phase + 1.57) * 0.5 + 0.5);
            float p2 = (float)(Math.Sin(phase + 3.14) * 0.5 + 0.5);
            float p3 = (float)(Math.Sin(phase + 4.71) * 0.5 + 0.5);

            BrushCyan.Center = BrushCyan.GradientOrigin = new Point(driftCyanX, driftCyanY);
            BrushCyan.RadiusX = BrushCyan.RadiusY = CyanBaseRadius + p0 * 0.10;
            RectCyan.Opacity = 0.40 + p0 * 0.20;

            BrushViolet.Center = BrushViolet.GradientOrigin = new Point(driftVioletX, driftVioletY);
            BrushViolet.RadiusX = BrushViolet.RadiusY = VioletBaseRadius + p1 * 0.10;
            RectViolet.Opacity = 0.38 + p1 * 0.20;

            BrushPink.Center = BrushPink.GradientOrigin = new Point(driftPinkX, driftPinkY);
            BrushPink.RadiusX = BrushPink.RadiusY = PinkBaseRadius + p2 * 0.10;
            RectPink.Opacity = 0.40 + p2 * 0.20;

            BrushOrange.Center = BrushOrange.GradientOrigin = new Point(driftOrangeX, driftOrangeY);
            BrushOrange.RadiusX = BrushOrange.RadiusY = OrangeBaseRadius + p3 * 0.10;
            RectOrange.Opacity = 0.38 + p3 * 0.20;

            _kickEnv = 0f;
            return;
        }

        // 4. Render Active Music Visualizer: Adaptive Beat-Flux Detection
        var audioService = RadioAudioService.Instance;
        bool hasData = audioService.GetSpectrumLevels(out float bass, out _, out _);
        if (!hasData && !active) bass = 0f;

        // Exponential Moving Average of bass floor (~1.5s time constant)
        float avgAlpha = 1f - MathF.Exp(-dt / 1.5f);
        _bassAvg = _bassAvg + avgAlpha * (bass - _bassAvg);
        if (_bassAvg < 0.05f) _bassAvg = 0.05f;

        // Instantaneous flux & adaptive kick trigger
        float bassFlux = bass - _prevBass;
        _prevBass = bass;

        bool isKick = (bass > 0.08f) && (bass > _bassAvg * 1.45f || bassFlux > Math.Max(0.04f, _bassAvg * 0.45f));

        // Debounced random color selection on beat
        _kickCooldown -= dt;
        if (isKick && _kickCooldown <= 0f)
        {
            int next;
            do
            {
                next = Random.Shared.Next(0, 4);
            } while (next == _pumpingColorIndex);

            _pumpingColorIndex = next;
            _kickCooldown = KickDebounceTime;
            _kickEnv = 1.0f; // Punch attack
        }

        // 160ms exponential release decay
        float decay = MathF.Exp(-dt / ReleaseDecayTime);
        _kickEnv *= decay;
        if (_kickEnv < 0.005f) _kickEnv = 0f;

        // Determine pump intensity for each individual color
        float pumpCyan = (_pumpingColorIndex == 0) ? _kickEnv : 0f;
        float pumpViolet = (_pumpingColorIndex == 1) ? _kickEnv : 0f;
        float pumpPink = (_pumpingColorIndex == 2) ? _kickEnv : 0f;
        float pumpOrange = (_pumpingColorIndex == 3) ? _kickEnv : 0f;

        // Apply dynamic radial accent blooms (Balanced opacities so all colors breathe together):
        // 1. Cyan (Top-Left)
        BrushCyan.Center = BrushCyan.GradientOrigin = new Point(driftCyanX, driftCyanY);
        BrushCyan.RadiusX = BrushCyan.RadiusY = CyanBaseRadius + breathe + (pumpCyan * PumpRadiusBoost);
        RectCyan.Opacity = Math.Clamp(0.50f + (breathe * 0.3f) + (pumpCyan * PumpOpacityBoost), 0.20f, 0.95f);

        // 2. Violet (Mid-Upper)
        BrushViolet.Center = BrushViolet.GradientOrigin = new Point(driftVioletX, driftVioletY);
        BrushViolet.RadiusX = BrushViolet.RadiusY = VioletBaseRadius - breathe + (pumpViolet * PumpRadiusBoost);
        RectViolet.Opacity = Math.Clamp(0.45f - (breathe * 0.3f) + (pumpViolet * PumpOpacityBoost), 0.20f, 0.95f);

        // 3. Hot Pink / Magenta (Bottom-Center)
        BrushPink.Center = BrushPink.GradientOrigin = new Point(driftPinkX, driftPinkY);
        BrushPink.RadiusX = BrushPink.RadiusY = PinkBaseRadius + breathe + (pumpPink * PumpRadiusBoost);
        RectPink.Opacity = Math.Clamp(0.50f + (breathe * 0.3f) + (pumpPink * PumpOpacityBoost), 0.20f, 0.95f);

        // 4. Radiant Orange (Bottom-Right)
        BrushOrange.Center = BrushOrange.GradientOrigin = new Point(driftOrangeX, driftOrangeY);
        BrushOrange.RadiusX = BrushOrange.RadiusY = OrangeBaseRadius - breathe + (pumpOrange * PumpRadiusBoost);
        RectOrange.Opacity = Math.Clamp(0.48f - (breathe * 0.3f) + (pumpOrange * PumpOpacityBoost), 0.20f, 0.95f);
    }
}
