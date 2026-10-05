using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MetroHub.Core.Radio;

namespace MetroHub.Widgets.Catalog.Radio;

/// <summary>
/// GPU-accelerated 2D Fluid Mesh Reactive Aura for MetroHub radio station cards.
/// Pure Kick & Snare Precision Engine:
///   1. Exclusive Triggering: ONLY authentic Kick drums and Snare backbeats trigger bloom and surge.
///      Zero shimmer, zero wobble, zero idle breathing, zero continuous bass drone swelling.
///   2. Kick Thump: Punchy bass thump with 5% base vector expansion, +25% bloom surge, and 220ms release.
///      Randomizes the focal color across the spectrum strictly on bass kick beats.
///   3. Snare Crack: Crisp, snappy luminescence pop (+22% opacity, 100ms fast release, zero color shift).
///   4. Zero White Blobs: 100% saturated pigments across all layers (no milky pastel stops).
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
    private const float MinOnsetFloor = 0.010f;       // Absolute floor to reject silence/hiss
    private const float KickRefractoryTime = 0.11f;    // 110ms refractory window between kicks
    private const float ColorSwitchInterval = 0.22f;   // 220ms minimum between color switches
    private const float KickReleaseDecay = 0.22f;      // ~220ms deep visceral kick release
    private const float FluxSigmaMultiplier = 1.4f;    // Threshold = mean + 1.4σ
    private const float KickRadiusBoost = 0.25f;       // +25% radius expansion on kick
    private const float KickOpacityBoost = 0.52f;      // Up to +52% luminescence flare on kick
    private const float BaseKickSwellAmp = 0.05f;      // +5% base gradient punch on kick

    // Snare Transient Detection Constants
    private const float SnareRefractoryTime = 0.12f;   // 120ms refractory window between snares
    private const float SnareReleaseDecay = 0.10f;     // ~100ms crisp, snappy crack release
    private const float SnareOpacityBoost = 0.45f;     // Crisp snappy luminescence pop on active bloom

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

    // Audio DSP State
    private float _midPeak = 0.25f;          // Running peak for mid auto-gain normalization

    // Kick 3-frame spectral flux state
    private readonly float[] _kickHistory = new float[HistoryLen];
    private int _kickHistoryIdx;
    private float _fluxMean = 0.010f;
    private float _fluxVar = 0.0001f;
    private float _kickEnv;
    private float _kickCooldown;
    private float _colorCooldown;
    private int _pumpingColorIndex = 2;      // 0=Cyan, 1=Violet, 2=Pink, 3=Orange

    // Snare transient state
    private readonly float[] _midHistory = new float[HistoryLen];
    private int _midHistoryIdx;
    private float _snareEnv;
    private float _snareCooldown;

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
            _snareEnv = 0f;
            Array.Clear(_kickHistory);
            Array.Clear(_midHistory);
            _kickHistoryIdx = 0;
            _midHistoryIdx = 0;
            return;
        }

        // 3. Sample Audio Levels
        var audioService = RadioAudioService.Instance;
        bool hasData = audioService.GetSpectrumLevels(out float rawKick, out _, out float rawMid, out float rawTreble);

        if (!hasData && !active)
        {
            rawKick = 0f;
            rawMid = 0f;
            rawTreble = 0f;
        }

        float peakDecay = MathF.Exp(-dt / 3.5f);
        _midPeak = Math.Max(rawMid, _midPeak * peakDecay);
        if (_midPeak < 0.20f) _midPeak = 0.20f;
        float normMid = Math.Clamp(rawMid / _midPeak, 0f, 1f);

        // =====================================================================
        // 4. KICK DRUM DETECTION (35 - 120 Hz)
        // =====================================================================
        _kickHistory[_kickHistoryIdx] = rawKick;
        _kickHistoryIdx = (_kickHistoryIdx + 1) % HistoryLen;

        float kickFloor = float.MaxValue;
        for (int i = 1; i <= 3; i++)
        {
            int idx = (_kickHistoryIdx - 1 - i + HistoryLen * 2) % HistoryLen;
            kickFloor = Math.Min(kickFloor, _kickHistory[idx]);
        }
        if (kickFloor == float.MaxValue) kickFloor = 0f;

        float kickFlux = Math.Max(0f, rawKick - kickFloor);

        // Adaptive statistical threshold for kick
        float fluxAlpha = 1f - MathF.Exp(-dt / 4.0f);
        float prevMean = _fluxMean;
        _fluxMean += fluxAlpha * (kickFlux - _fluxMean);
        float diff = kickFlux - prevMean;
        _fluxVar += fluxAlpha * (diff * diff - _fluxVar);
        float fluxSigma = MathF.Sqrt(Math.Max(0f, _fluxVar));
        float dynamicKickThreshold = Math.Max(MinOnsetFloor, _fluxMean + FluxSigmaMultiplier * fluxSigma);

        float midTransient = Math.Max(0f, rawMid - normMid * 0.85f);
        float kickRatio = Math.Clamp(kickFlux / Math.Max(0.01f, kickFlux + midTransient), 0f, 1f);

        _kickCooldown -= dt;
        _colorCooldown -= dt;

        bool isFluxKick = (_kickCooldown <= 0f) && (kickFlux >= dynamicKickThreshold);
        bool isDirectKick = (_kickCooldown <= 0f) && (rawKick > 0.26f && rawKick > kickFloor + 0.035f);
        bool isKick = (isFluxKick || isDirectKick) && (kickRatio > 0.28f);

        if (isKick)
        {
            float hitDelta = Math.Max(kickFlux, rawKick - kickFloor);
            float rawVelocity = Math.Clamp((hitDelta - dynamicKickThreshold * 0.35f) / Math.Max(0.025f, dynamicKickThreshold * 0.85f), 0.65f, 1.0f);
            float scaledVelocity = rawVelocity * (0.45f + 0.55f * kickRatio);

            _kickEnv = Math.Max(_kickEnv, scaledVelocity);
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
        // 5. SNARE / CLAP DETECTION (1.5 kHz - 8 kHz Upper Mid & Treble Crack)
        // =====================================================================
        _midHistory[_midHistoryIdx] = rawMid;
        _midHistoryIdx = (_midHistoryIdx + 1) % HistoryLen;

        float midFloor = float.MaxValue;
        for (int i = 1; i <= 3; i++)
        {
            int idx = (_midHistoryIdx - 1 - i + HistoryLen * 2) % HistoryLen;
            midFloor = Math.Min(midFloor, _midHistory[idx]);
        }
        if (midFloor == float.MaxValue) midFloor = 0f;

        float midFlux = Math.Max(0f, rawMid - midFloor);

        _snareCooldown -= dt;
        // Snare requires crisp mid rising edge + high frequency crack + low bass dominance
        bool isSnare = (_snareCooldown <= 0f) && !isKick &&
                       (midFlux > 0.030f && rawMid > 0.20f && rawTreble > 0.12f && kickRatio < 0.50f);

        if (isSnare)
        {
            float snareVelocity = Math.Clamp((midFlux - 0.02f) / 0.05f, 0.55f, 1.0f);
            _snareEnv = Math.Max(_snareEnv, snareVelocity);
            _snareCooldown = SnareRefractoryTime;
            // SNARES NEVER SWITCH COLOR
        }

        // =====================================================================
        // 6. ENVELOPE DECAYS (Zero Shimmer: Zero continuous note swell)
        // =====================================================================
        _kickEnv *= MathF.Exp(-dt / KickReleaseDecay);
        if (_kickEnv < 0.005f) _kickEnv = 0f;

        _snareEnv *= MathF.Exp(-dt / SnareReleaseDecay);
        if (_snareEnv < 0.005f) _snareEnv = 0f;

        // =====================================================================
        // 7. SURGE & BLOOM CALCULATION (Exclusively Kick & Snare)
        // =====================================================================
        // Kick delivers full visceral bloom expansion (+85%) and tight grounding (+15%)
        float kickShared = _kickEnv * 0.15f;
        float kickFocal  = _kickEnv * 0.85f;

        // Snare delivers snappy high-frequency luminescence pop on the active bloom (+45%)
        float snareFocal = _snareEnv * SnareOpacityBoost;

        float pumpCyan   = kickShared + ((_pumpingColorIndex == 0) ? (kickFocal + snareFocal) : 0f);
        float pumpViolet = kickShared + ((_pumpingColorIndex == 1) ? (kickFocal + snareFocal) : 0f);
        float pumpPink   = kickShared + ((_pumpingColorIndex == 2) ? (kickFocal + snareFocal) : 0f);
        float pumpOrange = kickShared + ((_pumpingColorIndex == 3) ? (kickFocal + snareFocal) : 0f);

        // Base gradient expands ONLY on Kick thump (zero wobble, zero shimmer)
        float baseKickSwell = _kickEnv * BaseKickSwellAmp;
        BaseGradient.StartPoint = new Point(-0.15 - baseKickSwell, -0.15 - baseKickSwell);
        BaseGradient.EndPoint = new Point(1.10 + baseKickSwell, 1.10 + baseKickSwell);

        // 8. Apply Pure Bloomed States (Rock-solid idle baseline; swells ONLY on kick/snare)
        // 1. Cyan (Top-Left quadrant)
        BrushCyan.Center = BrushCyan.GradientOrigin = new Point(CyanBaseX, CyanBaseY);
        BrushCyan.RadiusX = BrushCyan.RadiusY = CyanBaseRadius + (pumpCyan * KickRadiusBoost);
        RectCyan.Opacity = Math.Clamp(0.48f + (pumpCyan * KickOpacityBoost), 0.20f, 1.0f);

        // 2. Violet (Mid-Upper quadrant) - Saturated Royal Violet, Zero White Blob
        BrushViolet.Center = BrushViolet.GradientOrigin = new Point(VioletBaseX, VioletBaseY);
        BrushViolet.RadiusX = BrushViolet.RadiusY = VioletBaseRadius + (pumpViolet * KickRadiusBoost);
        RectViolet.Opacity = Math.Clamp(0.42f + (pumpViolet * KickOpacityBoost), 0.20f, 1.0f);

        // 3. Hot Pink / Magenta (Bottom-Center quadrant)
        BrushPink.Center = BrushPink.GradientOrigin = new Point(PinkBaseX, PinkBaseY);
        BrushPink.RadiusX = BrushPink.RadiusY = PinkBaseRadius + (pumpPink * KickRadiusBoost);
        RectPink.Opacity = Math.Clamp(0.48f + (pumpPink * KickOpacityBoost), 0.20f, 1.0f);

        // 4. Radiant Orange (Bottom-Right quadrant)
        BrushOrange.Center = BrushOrange.GradientOrigin = new Point(OrangeBaseX, OrangeBaseY);
        BrushOrange.RadiusX = BrushOrange.RadiusY = OrangeBaseRadius + (pumpOrange * KickRadiusBoost);
        RectOrange.Opacity = Math.Clamp(0.45f + (pumpOrange * KickOpacityBoost), 0.20f, 1.0f);
    }
}
