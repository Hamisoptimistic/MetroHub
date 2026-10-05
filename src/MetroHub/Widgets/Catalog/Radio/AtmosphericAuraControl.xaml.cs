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
/// Features a two-tier acoustic engine:
///   Tier 1: Continuous smoothed bass note coupling (+12% unified swell with fast attack & ~220ms release).
///   Tier 2: Velocity-scaled kick pump with hysteresis, auto-gain normalization, and 250ms color switch intervals.
/// Uses zero geometry transforms to ensure zero border gaps and zero bounding box seams.
/// </summary>
public partial class AtmosphericAuraControl : UserControl
{
    // ==========================================
    // Visual & Audio Physics Tuning Constants
    // ==========================================
    // Blob Base Focal Centers & Radii (Normalized 0.0 - 1.0)
    private const float CyanBaseX = 0.25f, CyanBaseY = 0.22f, CyanBaseRadius = 0.46f;
    private const float VioletBaseX = 0.45f, VioletBaseY = 0.35f, VioletBaseRadius = 0.42f;
    private const float PinkBaseX = 0.52f, PinkBaseY = 0.72f, PinkBaseRadius = 0.48f;
    private const float OrangeBaseX = 0.80f, OrangeBaseY = 0.75f, OrangeBaseRadius = 0.44f;

    // Drift Motion Amplitudes (Normalized 0.0 - 1.0)
    private const float DriftAmpX = 0.07f;
    private const float DriftAmpY = 0.06f;
    private const float CyclePeriod = 4.2f;
    private const float AmbientBreatheAmp = 0.04f;

    // Tier 1: Continuous Bass Groove Swell (Whole-tile musical breathing)
    private const float ContinuousSwellAmp = 0.08f;  // +8% size on continuous bass notes
    private const float ContinuousAttack = 0.04f;    // ~40ms fast attack
    private const float ContinuousRelease = 0.22f;   // ~220ms smooth release

    // Tier 2: Drone-Stripped Transient Beat Pump & Harmonic Pulse
    private const float FastAttack = 0.012f;         // ~12ms instant transient snap
    private const float FastRelease = 0.045f;        // ~45ms rapid decay
    private const float SlowBaselineAttack = 0.14f;  // ~140ms drone follower attack
    private const float SlowBaselineRelease = 0.35f; // ~350ms drone follower release
    private const float MinOnsetFloor = 0.024f;      // Absolute floor to reject silence/hiss
    private const float KickRefractoryTime = 0.11f;  // 110ms refractory window between kicks
    private const float ColorSwitchInterval = 0.25f; // 250ms minimum between color switches
    private const float PumpReleaseDecay = 0.16f;    // 160ms decay for kick envelope
    private const float PumpRadiusBoost = 0.24f;     // Up to +24% radius on maximum velocity kick (clean localized bloom)
    private const float PumpOpacityBoost = 0.45f;    // Up to +45% opacity on maximum velocity kick

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

    // Audio DSP State
    private float _bassPeak = 0.20f;         // Running peak for auto-gain normalization
    private float _midPeak = 0.15f;          // Running peak for mid auto-gain normalization
    private float _smoothedBass;             // Tier 1 smoothed bass envelope
    private float _fastBassEnv;              // Snappy attack bass envelope (~12ms)
    private float _slowBassBaseline;         // Drone tracking baseline (~140ms)
    private float _fastMidEnv;               // Snappy attack mid envelope (~10ms)
    private float _slowMidBaseline;          // Drone tracking mid baseline (~120ms)
    private float _fluxAvg = 0.025f;         // Moving average of transient flux for adaptive threshold
    private float _kickEnv;                  // Tier 2 velocity-scaled kick envelope
    private float _kickCooldown;             // Refractory timer (110ms)
    private float _colorCooldown;            // Color switch timer (250ms)
    private int _pumpingColorIndex = 2;      // 0=Cyan, 1=Violet, 2=Pink, 3=Orange
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

        double phase = elapsed * (Math.PI * 2.0 / CyclePeriod);
        float flowX = (float)(Math.Sin(phase * 0.9) * 0.10);
        float flowY = (float)(Math.Cos(phase * 0.7) * 0.08);

        // 2. Render Buffering State: Gentle soothing wave
        if (buffering)
        {
            float bBreathe = (float)(Math.Sin(phase) * AmbientBreatheAmp);
            float p0 = (float)(Math.Sin(phase) * 0.5 + 0.5);
            float p1 = (float)(Math.Sin(phase + 1.57) * 0.5 + 0.5);
            float p2 = (float)(Math.Sin(phase + 3.14) * 0.5 + 0.5);
            float p3 = (float)(Math.Sin(phase + 4.71) * 0.5 + 0.5);

            BaseGradient.StartPoint = new Point(-0.15 + flowX - bBreathe, -0.15 + flowY - bBreathe);
            BaseGradient.EndPoint = new Point(1.10 + flowX + bBreathe, 1.10 + flowY + bBreathe);

            BrushCyan.Center = BrushCyan.GradientOrigin = new Point(
                (float)(CyanBaseX + Math.Sin(phase * 0.8) * DriftAmpX),
                (float)(CyanBaseY + Math.Cos(phase * 0.7) * DriftAmpY));
            BrushCyan.RadiusX = BrushCyan.RadiusY = CyanBaseRadius + p0 * 0.05;
            RectCyan.Opacity = 0.40 + p0 * 0.20;

            BrushViolet.Center = BrushViolet.GradientOrigin = new Point(
                (float)(VioletBaseX + Math.Cos(phase * 0.85 + 1.2) * DriftAmpX),
                (float)(VioletBaseY + Math.Sin(phase * 0.75 + 0.8) * DriftAmpY));
            BrushViolet.RadiusX = BrushViolet.RadiusY = VioletBaseRadius + p1 * 0.05;
            RectViolet.Opacity = 0.38 + p1 * 0.20;

            BrushPink.Center = BrushPink.GradientOrigin = new Point(
                (float)(PinkBaseX + Math.Sin(phase * 0.8 + 2.1) * DriftAmpX),
                (float)(PinkBaseY + Math.Cos(phase * 0.9 + 1.7) * DriftAmpY));
            BrushPink.RadiusX = BrushPink.RadiusY = PinkBaseRadius + p2 * 0.05;
            RectPink.Opacity = 0.40 + p2 * 0.20;

            BrushOrange.Center = BrushOrange.GradientOrigin = new Point(
                (float)(OrangeBaseX + Math.Cos(phase * 0.75 + 3.4) * DriftAmpX),
                (float)(OrangeBaseY + Math.Sin(phase * 0.85 + 2.9) * DriftAmpY));
            BrushOrange.RadiusX = BrushOrange.RadiusY = OrangeBaseRadius + p3 * 0.05;
            RectOrange.Opacity = 0.38 + p3 * 0.20;

            _kickEnv = 0f;
            _fastBassEnv = 0f;
            _slowBassBaseline = 0f;
            _fastMidEnv = 0f;
            _slowMidBaseline = 0f;
            _fluxAvg = 0.025f;
            return;
        }

        // 3. Sample Audio Levels
        var audioService = RadioAudioService.Instance;
        bool hasData = audioService.GetSpectrumLevels(out float rawBass, out float rawMid, out _);
        if (!hasData && !active)
        {
            rawBass = 0f;
            rawMid = 0f;
        }

        // Auto Gain Normalization: Decaying running peak over ~3.5s
        float peakDecay = MathF.Exp(-dt / 3.5f);
        _bassPeak = Math.Max(rawBass, _bassPeak * peakDecay);
        if (_bassPeak < 0.08f) _bassPeak = 0.08f;
        float normBass = Math.Clamp(rawBass / _bassPeak, 0f, 1f);

        _midPeak = Math.Max(rawMid, _midPeak * peakDecay);
        if (_midPeak < 0.06f) _midPeak = 0.06f;
        float normMid = Math.Clamp(rawMid / _midPeak, 0f, 1f);

        // Tier 1: Continuous Note Envelope (Fast attack ~40ms, smooth release ~220ms)
        float attackCoeff = 1f - MathF.Exp(-dt / ContinuousAttack);
        float releaseCoeff = 1f - MathF.Exp(-dt / ContinuousRelease);
        if (normBass > _smoothedBass)
        {
            _smoothedBass += attackCoeff * (normBass - _smoothedBass);
        }
        else
        {
            _smoothedBass += releaseCoeff * (normBass - _smoothedBass);
        }

        // Tier 2: Dual Fast/Slow Envelopes for Continuous Drone Cancellation
        float fastAttackCoeff = 1f - MathF.Exp(-dt / FastAttack);
        float fastReleaseCoeff = 1f - MathF.Exp(-dt / FastRelease);
        float slowAttackCoeff = 1f - MathF.Exp(-dt / SlowBaselineAttack);
        float slowReleaseCoeff = 1f - MathF.Exp(-dt / SlowBaselineRelease);

        // Bass fast snap and slow drone baseline tracking
        if (normBass > _fastBassEnv)
            _fastBassEnv += fastAttackCoeff * (normBass - _fastBassEnv);
        else
            _fastBassEnv += fastReleaseCoeff * (normBass - _fastBassEnv);

        if (normBass > _slowBassBaseline)
            _slowBassBaseline += slowAttackCoeff * (normBass - _slowBassBaseline);
        else
            _slowBassBaseline += slowReleaseCoeff * (normBass - _slowBassBaseline);

        // Mid fast snap and slow baseline tracking (kick beater slap & pop handclap/snare)
        if (normMid > _fastMidEnv)
            _fastMidEnv += fastAttackCoeff * (normMid - _fastMidEnv);
        else
            _fastMidEnv += fastReleaseCoeff * (normMid - _fastMidEnv);

        if (normMid > _slowMidBaseline)
            _slowMidBaseline += slowAttackCoeff * (normMid - _slowMidBaseline);
        else
            _slowMidBaseline += slowReleaseCoeff * (normMid - _slowMidBaseline);

        // Drone-subtracted transient onset energy (combines bass thump + pop clap/snare snap)
        float bassOnset = Math.Max(0f, _fastBassEnv - _slowBassBaseline);
        float midOnset = Math.Max(0f, _fastMidEnv - _slowMidBaseline);
        float transientFlux = bassOnset + (midOnset * 0.50f);

        // Calculate bass dominance: distinguishes real low-end bass kicks from claps/snares
        float bassRatio = Math.Clamp(bassOnset / Math.Max(0.01f, bassOnset + midOnset), 0f, 1f);

        // Adaptive sensitivity: auto-calibrates to song dynamic range (~1.8s time constant)
        float fluxAvgCoeff = 1f - MathF.Exp(-dt / 1.8f);
        _fluxAvg += fluxAvgCoeff * (transientFlux - _fluxAvg);
        if (_fluxAvg < 0.015f) _fluxAvg = 0.015f;

        float dynamicThreshold = Math.Max(MinOnsetFloor, _fluxAvg * 1.35f);

        _kickCooldown -= dt;
        _colorCooldown -= dt;

        bool isHit = (_kickCooldown <= 0f) && (transientFlux >= dynamicThreshold);

        if (isHit)
        {
            // Dynamic velocity scaling with Bass Priority:
            // Heavy bass thumps get full 100% explosive power (hitScale ~ 1.0).
            // Claps/snares without bass are scaled down to a subtle ~35% rhythmic bounce.
            float rawVelocity = Math.Clamp((transientFlux - dynamicThreshold * 0.5f) / Math.Max(0.08f, dynamicThreshold * 1.5f), 0.30f, 1.0f);
            float hitScale = 0.32f + (0.68f * bassRatio);
            float scaledVelocity = rawVelocity * hitScale;

            _kickEnv = Math.Max(_kickEnv, scaledVelocity);
            _kickCooldown = KickRefractoryTime;

            // Color switches are tied to bass kicks for clean, musical transitions
            if (_colorCooldown <= 0f && bassRatio > 0.30f)
            {
                int next;
                do
                {
                    next = Random.Shared.Next(0, 4);
                } while (next == _pumpingColorIndex);

                _pumpingColorIndex = next;
                _colorCooldown = ColorSwitchInterval;
            }
        }

        // Exponential Release Decay for Kick Envelope (~160ms)
        float pumpDecay = MathF.Exp(-dt / PumpReleaseDecay);
        _kickEnv *= pumpDecay;
        if (_kickEnv < 0.005f) _kickEnv = 0f;

        // 4. Continuous Ambient Wave (~4.2s cycle) & Idle Calming
        float breathe = (float)(Math.Sin(phase) * AmbientBreatheAmp);

        float calm = 1f - 0.6f * _kickEnv;
        breathe *= calm;

        // Fluid Lissajous drift for individual accent blobs (calmed during a kick pump)
        float driftCyanX = (float)(CyanBaseX + Math.Sin(phase * 0.8) * DriftAmpX * calm);
        float driftCyanY = (float)(CyanBaseY + Math.Cos(phase * 0.7) * DriftAmpY * calm);

        float driftVioletX = (float)(VioletBaseX + Math.Cos(phase * 0.85 + 1.2) * DriftAmpX * calm);
        float driftVioletY = (float)(VioletBaseY + Math.Sin(phase * 0.75 + 0.8) * DriftAmpY * calm);

        float driftPinkX = (float)(PinkBaseX + Math.Sin(phase * 0.8 + 2.1) * DriftAmpX * calm);
        float driftPinkY = (float)(PinkBaseY + Math.Cos(phase * 0.9 + 1.7) * DriftAmpY * calm);

        float driftOrangeX = (float)(OrangeBaseX + Math.Cos(phase * 0.75 + 3.4) * DriftAmpX * calm);
        float driftOrangeY = (float)(OrangeBaseY + Math.Sin(phase * 0.85 + 2.9) * DriftAmpY * calm);

        // Determine pump intensity: Unified Harmonic Surge with High Contrast
        // The chosen color gets 100% full kick power, while the remaining 3 colors get a tight 20% supporting pulse.
        // This gives high dramatic contrast while preventing any layer from being masked.
        float sharedPump = _kickEnv * 0.20f;
        float focalBoost = _kickEnv * 0.80f;
        float pumpCyan   = sharedPump + ((_pumpingColorIndex == 0) ? focalBoost : 0f);
        float pumpViolet = sharedPump + ((_pumpingColorIndex == 1) ? focalBoost : 0f);
        float pumpPink   = sharedPump + ((_pumpingColorIndex == 2) ? focalBoost : 0f);
        float pumpOrange = sharedPump + ((_pumpingColorIndex == 3) ? focalBoost : 0f);

        // Tier 1 continuous note swell: applied to ALL blobs equally (+10 to +15% radius)
        float noteSwell = _smoothedBass * ContinuousSwellAmp;

        // Base gradient vector expands with continuous note swell + ambient wave + kick pulse
        float baseKickSwell = _kickEnv * 0.08f;
        BaseGradient.StartPoint = new Point(-0.15 + flowX - breathe - noteSwell * 0.5 - baseKickSwell, -0.15 + flowY - breathe - noteSwell * 0.5 - baseKickSwell);
        BaseGradient.EndPoint = new Point(1.10 + flowX + breathe + noteSwell * 0.5 + baseKickSwell, 1.10 + flowY + breathe + noteSwell * 0.5 + baseKickSwell);

        // Apply dynamic radial accent blooms:
        // 1. Cyan (Top-Left)
        BrushCyan.Center = BrushCyan.GradientOrigin = new Point(driftCyanX, driftCyanY);
        BrushCyan.RadiusX = BrushCyan.RadiusY = CyanBaseRadius + breathe + noteSwell + (pumpCyan * PumpRadiusBoost);
        RectCyan.Opacity = Math.Clamp(0.50f + (breathe * 0.3f) + (noteSwell * 0.15f) + (pumpCyan * PumpOpacityBoost), 0.20f, 1.0f);

        // 2. Violet (Mid-Upper)
        BrushViolet.Center = BrushViolet.GradientOrigin = new Point(driftVioletX, driftVioletY);
        BrushViolet.RadiusX = BrushViolet.RadiusY = VioletBaseRadius - breathe + noteSwell + (pumpViolet * PumpRadiusBoost);
        RectViolet.Opacity = Math.Clamp(0.45f - (breathe * 0.3f) + (noteSwell * 0.15f) + (pumpViolet * PumpOpacityBoost), 0.20f, 1.0f);

        // 3. Hot Pink / Magenta (Bottom-Center)
        BrushPink.Center = BrushPink.GradientOrigin = new Point(driftPinkX, driftPinkY);
        BrushPink.RadiusX = BrushPink.RadiusY = PinkBaseRadius + breathe + noteSwell + (pumpPink * PumpRadiusBoost);
        RectPink.Opacity = Math.Clamp(0.50f + (breathe * 0.3f) + (noteSwell * 0.15f) + (pumpPink * PumpOpacityBoost), 0.20f, 1.0f);

        // 4. Radiant Orange (Bottom-Right)
        BrushOrange.Center = BrushOrange.GradientOrigin = new Point(driftOrangeX, driftOrangeY);
        BrushOrange.RadiusX = BrushOrange.RadiusY = OrangeBaseRadius - breathe + noteSwell + (pumpOrange * PumpRadiusBoost);
        RectOrange.Opacity = Math.Clamp(0.48f - (breathe * 0.3f) + (noteSwell * 0.15f) + (pumpOrange * PumpOpacityBoost), 0.20f, 1.0f);
    }
}
