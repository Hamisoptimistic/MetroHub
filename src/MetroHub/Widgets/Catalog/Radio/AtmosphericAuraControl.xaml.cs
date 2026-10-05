using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MetroHub.Core.Radio;

namespace MetroHub.Widgets.Catalog.Radio;

/// <summary>
/// GPU-accelerated 2D Fluid Mesh Reactive Aura for MetroHub radio station cards.
/// Pure Kick / Bass Precision Engine:
///   1. Pure Bass / Kick Thumping: Only authentic kick drum beats pump the visualizer (+25% radius, +50% opacity).
///   2. Zero Non-Bass Pumping: All snare, clap, mid, vocal, and treble pumping is completely removed.
///   3. Zero Shimmer: No continuous note swelling, no idle breathing, no vocal/guitar flutter.
///   4. Zero White Blobs: 100% saturated pigments across all layers (no milky pastel stops).
///   5. Bass-Only Color Switching: The focal color shifts across the spectrum strictly on authentic kick beats.
/// </summary>
public partial class AtmosphericAuraControl : UserControl
{
    // ==========================================
    // Visual & Audio Physics Tuning Constants
    // ==========================================
    // Blob Base Focal Centers & Radii (Normalized 0.0 - 1.0)
    // Wide seamless radii (0.75 - 0.80) ensure the gradient falloff extends beyond tile borders,
    // completely eliminating harsh spotlight circles and visible radial boundaries.
    private const float CyanBaseX = 0.25f, CyanBaseY = 0.22f, CyanBaseRadius = 0.78f;
    private const float VioletBaseX = 0.45f, VioletBaseY = 0.35f, VioletBaseRadius = 0.75f;
    private const float PinkBaseX = 0.52f, PinkBaseY = 0.72f, PinkBaseRadius = 0.80f;
    private const float OrangeBaseX = 0.80f, OrangeBaseY = 0.75f, OrangeBaseRadius = 0.76f;

    // Kick Transient Detection Constants
    private const float MinOnsetFloor = 0.040f;       // Robust floor to reject silence, hiss, and vocal flutter
    private const float KickRefractoryTime = 0.11f;    // 110ms refractory window between kicks
    private const float ColorSwitchInterval = 0.22f;   // 220ms minimum between color switches
    private const float KickReleaseDecay = 0.22f;      // ~220ms deep visceral kick release
    private const float FluxSigmaMultiplier = 1.6f;    // Threshold = mean + 1.6σ
    private const float PumpRadiusBoost = 0.25f;       // +25% radius expansion on kick
    private const float PumpOpacityBoost = 0.50f;      // Up to +50% luminescence flare on kick
    private const float BaseKickSwellAmp = 0.05f;      // +5% base gradient punch on kick impact

    // 4-frame ring buffer for transient edge detection
    private const int HistoryLen = 4;

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

    // Kick & Mid 4-frame spectral flux state
    private readonly float[] _kickHistory = new float[HistoryLen];
    private int _kickHistoryIdx;
    private readonly float[] _midHistory = new float[HistoryLen];
    private int _midHistoryIdx;
    private float _fluxMean = 0.010f;
    private float _fluxVar = 0.0001f;
    private float _kickEnv;
    private float _kickCooldown;
    private float _colorCooldown;
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

        // 2. Render Buffering State: Gentle soothing wave (zero audio required)
        if (buffering)
        {
            double bPhase = elapsed * (Math.PI * 2.0 / 4.0);
            float p0 = (float)(Math.Sin(bPhase) * 0.5 + 0.5);
            float p1 = (float)(Math.Sin(bPhase + 1.57) * 0.5 + 0.5);
            float p2 = (float)(Math.Sin(bPhase + 3.14) * 0.5 + 0.5);
            float p3 = (float)(Math.Sin(bPhase + 4.71) * 0.5 + 0.5);

            BaseGradient.StartPoint = new Point(-0.15, -0.15);
            BaseGradient.EndPoint = new Point(1.10, 1.10);

            BrushCyan.Center = BrushCyan.GradientOrigin = new Point(CyanBaseX, CyanBaseY);
            BrushCyan.RadiusX = BrushCyan.RadiusY = CyanBaseRadius + p0 * 0.04;
            RectCyan.Opacity = 0.40 + p0 * 0.15;

            BrushViolet.Center = BrushViolet.GradientOrigin = new Point(VioletBaseX, VioletBaseY);
            BrushViolet.RadiusX = BrushViolet.RadiusY = VioletBaseRadius + p1 * 0.04;
            RectViolet.Opacity = 0.38 + p1 * 0.15;

            BrushPink.Center = BrushPink.GradientOrigin = new Point(PinkBaseX, PinkBaseY);
            BrushPink.RadiusX = BrushPink.RadiusY = PinkBaseRadius + p2 * 0.04;
            RectPink.Opacity = 0.40 + p2 * 0.15;

            BrushOrange.Center = BrushOrange.GradientOrigin = new Point(OrangeBaseX, OrangeBaseY);
            BrushOrange.RadiusX = BrushOrange.RadiusY = OrangeBaseRadius + p3 * 0.04;
            RectOrange.Opacity = 0.38 + p3 * 0.15;

            _kickEnv = 0f;
            Array.Clear(_kickHistory);
            Array.Clear(_midHistory);
            _kickHistoryIdx = 0;
            _midHistoryIdx = 0;
            return;
        }

        // 3. Sample Audio Levels
        var audioService = RadioAudioService.Instance;
        bool hasData = audioService.GetSpectrumLevels(out float rawKick, out _, out float rawMid, out _);

        if (!hasData && !active)
        {
            rawKick = 0f;
            rawMid = 0f;
        }

        // =====================================================================
        // 4. KICK DRUM DETECTION (35 - 85 Hz)
        // =====================================================================
        _kickHistory[_kickHistoryIdx] = rawKick;
        _kickHistoryIdx = (_kickHistoryIdx + 1) % HistoryLen;

        float kickFloor = float.MaxValue;
        for (int i = 1; i <= 3; i++)
        {
            int idx = (_kickHistoryIdx - 1 - i + HistoryLen * 2) % HistoryLen;
            kickFloor = Math.Min(kickFloor, _kickHistory[idx]);
        }
        if (kickFloor >= float.MaxValue - 1f) kickFloor = 0f;

        float kickFlux = Math.Max(0f, rawKick - kickFloor);

        // Track mid-range transient flux (vocals, guitars, percussion)
        _midHistory[_midHistoryIdx] = rawMid;
        _midHistoryIdx = (_midHistoryIdx + 1) % HistoryLen;

        float midFloor = float.MaxValue;
        for (int i = 1; i <= 3; i++)
        {
            int idx = (_midHistoryIdx - 1 - i + HistoryLen * 2) % HistoryLen;
            midFloor = Math.Min(midFloor, _midHistory[idx]);
        }
        if (midFloor >= float.MaxValue - 1f) midFloor = 0f;

        float midFlux = Math.Max(0f, rawMid - midFloor);

        // Adaptive statistical threshold for kick
        float fluxAlpha = 1f - MathF.Exp(-dt / 4.0f);
        float prevMean = _fluxMean;
        _fluxMean += fluxAlpha * (kickFlux - _fluxMean);
        float diff = kickFlux - prevMean;
        _fluxVar += fluxAlpha * (diff * diff - _fluxVar);
        float fluxSigma = MathF.Sqrt(Math.Max(0f, _fluxVar));
        float dynamicKickThreshold = Math.Max(MinOnsetFloor, _fluxMean + FluxSigmaMultiplier * fluxSigma);

        _kickCooldown -= dt;
        _colorCooldown -= dt;

        // Strict Kick Filter:
        // 1. Kick flux must exceed adaptive statistical threshold and minimum floor (0.045f)
        // 2. Raw kick energy must be substantial (>= 0.28f)
        // 3. Low-end transient must dominate over mid transient (kickFlux >= midFlux * 1.15f)
        // 4. Low-end energy must be comparable or dominant vs vocal/mid energy (rawKick >= rawMid * 0.80f)
        // This mathematically ensures vocals, speech, guitars, brass, and pads cause ZERO motion.
        bool isKick = (_kickCooldown <= 0f) &&
                      (kickFlux >= dynamicKickThreshold) &&
                      (kickFlux >= 0.045f) &&
                      (rawKick >= 0.28f) &&
                      (kickFlux >= midFlux * 1.15f) &&
                      (rawKick >= rawMid * 0.80f);

        if (isKick)
        {
            float hitDelta = Math.Max(kickFlux, rawKick - kickFloor);
            float rawVelocity = Math.Clamp((hitDelta - 0.02f) / 0.08f, 0.70f, 1.0f);

            _kickEnv = Math.Max(_kickEnv, rawVelocity);
            _kickCooldown = KickRefractoryTime;

            // ONLY BASS KICKS SWITCH COLOR
            if (_colorCooldown <= 0f)
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

        // =====================================================================
        // 5. KICK ENVELOPE DECAY (Visceral ~220ms drum release)
        // =====================================================================
        _kickEnv *= MathF.Exp(-dt / KickReleaseDecay);
        if (_kickEnv < 0.005f) _kickEnv = 0f;

        // =====================================================================
        // 6. SURGE & BLOOM CALCULATION (Pure Kick / Bass Thumping Only)
        // =====================================================================
        // Only the active focal color pumps on authentic kick drum beats.
        // All other colors remain completely steady and grounded at their baseline.
        float pumpCyan   = (_pumpingColorIndex == 0) ? _kickEnv : 0f;
        float pumpViolet = (_pumpingColorIndex == 1) ? _kickEnv : 0f;
        float pumpPink   = (_pumpingColorIndex == 2) ? _kickEnv : 0f;
        float pumpOrange = (_pumpingColorIndex == 3) ? _kickEnv : 0f;

        // Base gradient expands ONLY on Kick thump (zero wobble, zero shimmer)
        float baseKickSwell = _kickEnv * BaseKickSwellAmp;
        BaseGradient.StartPoint = new Point(-0.15 - baseKickSwell, -0.15 - baseKickSwell);
        BaseGradient.EndPoint = new Point(1.10 + baseKickSwell, 1.10 + baseKickSwell);

        // 7. Apply Pure Bloomed States: baseline opacities with punchy kick surge
        // 1. Cyan (Top-Left quadrant)
        BrushCyan.Center = BrushCyan.GradientOrigin = new Point(CyanBaseX, CyanBaseY);
        BrushCyan.RadiusX = BrushCyan.RadiusY = CyanBaseRadius + (pumpCyan * PumpRadiusBoost);
        RectCyan.Opacity = Math.Clamp(0.50f + (pumpCyan * PumpOpacityBoost), 0.20f, 1.0f);

        // 2. Violet (Mid-Upper quadrant) - Saturated Royal Violet, Zero White Blob
        BrushViolet.Center = BrushViolet.GradientOrigin = new Point(VioletBaseX, VioletBaseY);
        BrushViolet.RadiusX = BrushViolet.RadiusY = VioletBaseRadius + (pumpViolet * PumpRadiusBoost);
        RectViolet.Opacity = Math.Clamp(0.45f + (pumpViolet * PumpOpacityBoost), 0.20f, 1.0f);

        // 3. Hot Pink / Magenta (Bottom-Center quadrant)
        BrushPink.Center = BrushPink.GradientOrigin = new Point(PinkBaseX, PinkBaseY);
        BrushPink.RadiusX = BrushPink.RadiusY = PinkBaseRadius + (pumpPink * PumpRadiusBoost);
        RectPink.Opacity = Math.Clamp(0.50f + (pumpPink * PumpOpacityBoost), 0.20f, 1.0f);

        // 4. Radiant Orange (Bottom-Right quadrant)
        BrushOrange.Center = BrushOrange.GradientOrigin = new Point(OrangeBaseX, OrangeBaseY);
        BrushOrange.RadiusX = BrushOrange.RadiusY = OrangeBaseRadius + (pumpOrange * PumpRadiusBoost);
        RectOrange.Opacity = Math.Clamp(0.48f + (pumpOrange * PumpOpacityBoost), 0.20f, 1.0f);
    }
}
