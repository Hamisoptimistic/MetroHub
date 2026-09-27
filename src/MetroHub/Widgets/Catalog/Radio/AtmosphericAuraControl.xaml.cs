using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MetroHub.Core.Radio;

namespace MetroHub.Widgets.Catalog.Radio;

/// <summary>
/// GPU-accelerated Chromatic Heat-Map Visualizer Control for MetroHub active radio tiles.
/// Blends a 3-layer perceptual heat gradient (Crimson Bass, Amber Mids, Golden Treble Shimmer)
/// driven directly by real-time SIMD FFT audio energy at native display refresh rates.
/// Uses zero WPF layout passes and zero per-frame heap allocations.
/// </summary>
public partial class AtmosphericAuraControl : UserControl
{
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
    private long _lastRenderTicks;
    private static readonly long Throttled30FpsTicks = TimeSpan.FromMilliseconds(33).Ticks;

    // Punch envelopes ride ON TOP of the slow swell — swell stays untouched.
    private float _kickEnv;   // Kick thump: fast pop, melts back into the swell.
    private float _snareEnv;  // Snare snap: mostly shimmer, tiny shake.
    private float _prevBass;
    private float _prevMid;
    private DateTime _lastPunchTime = DateTime.UtcNow;
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
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateHookState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnhookRendering();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Blob transforms use RenderTransformOrigin 0.5,0.5 — no per-size center math needed.
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

        bool shouldRender = IsActive || IsBuffering || AuraRoot.Opacity > 0.001;
        if (shouldRender && !_isHooked)
        {
            HookRendering();
        }
    }

    private void HookRendering()
    {
        if (_isHooked) return;
        _isHooked = true;
        _startTime = DateTime.UtcNow;
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

            // Complete Quiescence: Unhook when fully faded to 0 opacity
            if (AuraRoot.Opacity <= 0.001)
            {
                AuraRoot.Opacity = 0.0;
                UnhookRendering();
                return;
            }
        }

        // 2. Render Buffering State: all blobs breathe with phase offsets (fluid, not flat).
        if (buffering)
        {
            double elapsed = (DateTime.UtcNow - _startTime).TotalSeconds;
            float p0 = (float)(Math.Sin(elapsed * 3.5) * 0.5 + 0.5);
            float p1 = (float)(Math.Sin(elapsed * 3.5 + 2.1) * 0.5 + 0.5);
            float p2 = (float)(Math.Sin(elapsed * 3.5 + 4.2) * 0.5 + 0.5);

            BlobPink.Opacity = 0.65 + p0 * 0.35;
            BlobCyan.Opacity = 0.55 + p1 * 0.35;
            BlobViolet.Opacity = 0.50 + p2 * 0.35;
            BlobAmber.Opacity = 0.35 + p1 * 0.30;

            PinkScale.ScaleX = PinkScale.ScaleY = 1.0 + p0 * 0.06;
            CyanScale.ScaleX = CyanScale.ScaleY = 1.0 + p1 * 0.08;
            VioletScale.ScaleX = VioletScale.ScaleY = 1.0 + p2 * 0.08;
            AmberScale.ScaleX = AmberScale.ScaleY = 1.0 + p1 * 0.10;

            _kickEnv = 0f;
            _snareEnv = 0f;
            _prevBass = 0f;
            _prevMid = 0f;
            return;
        }

        // 3. Render Active Music Visualizer: Direct FFT Spectrum Reactive Mesh
        var audioService = RadioAudioService.Instance;
        bool hasData = audioService.GetSpectrumLevels(out float bass, out float mid, out float treble);

        if (!hasData && !active)
        {
            // Decaying to zero when stopped
            bass = 0f;
            mid = 0f;
            treble = 0f;
        }

        // Punch envelopes: rising-edge transients on top of the swell.
        // Kick ~180ms release, snare ~120ms — both melt back into the swell.
        DateTime now = DateTime.UtcNow;
        float dt = Math.Clamp((float)(now - _lastPunchTime).TotalSeconds, 0.001f, 0.1f);
        _lastPunchTime = now;
        float kickHit = Math.Max(0f, bass - _prevBass * 1.04f - 0.015f);
        float snareHit = Math.Max(0f, mid - _prevMid * 1.04f - 0.015f);
        _prevBass = bass;
        _prevMid = mid;
        float kickDecay = MathF.Exp(-dt / 0.18f);
        float snareDecay = MathF.Exp(-dt / 0.12f);
        _kickEnv = Math.Max(kickHit * 1.6f, _kickEnv * kickDecay);
        _snareEnv = Math.Max(snareHit * 1.6f, _snareEnv * snareDecay);
        _kickEnv = Math.Min(_kickEnv, 1f);
        _snareEnv = Math.Min(_snareEnv, 1f);

        // Slow fluid drift (your swell): each blob breathes at its own rate so the
        // mesh morphs like the reference instead of flashing as one sheet.
        double t = (now - _startTime).TotalSeconds;
        float driftPink = (float)(Math.Sin(t * 1.7) * 0.5 + 0.5);
        float driftCyan = (float)(Math.Sin(t * 2.3 + 2.1) * 0.5 + 0.5);
        float driftViolet = (float)(Math.Sin(t * 1.3 + 4.2) * 0.5 + 0.5);

        // PINK (kept red/pink): swell + kick punch. Biggest blob, bottom-anchored.
        BlobPink.Opacity = Math.Clamp(0.72f + bass * 0.28f, 0f, 1f);
        PinkScale.ScaleX = 1.0 + bass * 0.06 + _kickEnv * 0.16 + driftPink * 0.03;
        PinkScale.ScaleY = 1.0 + bass * 0.08 + _kickEnv * 0.18 + driftPink * 0.03;
        PinkShift.Y = -_kickEnv * 6.0;

        // CYAN: snare snap + subtle kick duck (the pump). Slides a little on hits.
        BlobCyan.Opacity = Math.Clamp(0.65f + mid * 0.30f + _snareEnv * 0.15f - _kickEnv * 0.10f, 0f, 1f);
        CyanScale.ScaleX = CyanScale.ScaleY = 1.0 + mid * 0.08 + _snareEnv * 0.10 + driftCyan * 0.04;
        CyanShift.X = (float)(Math.Sin(t * 1.1) * 3.0 + _snareEnv * 4.0);
        CyanShift.Y = (float)(Math.Cos(t * 0.9) * 2.0 - _snareEnv * 3.0);

        // VIOLET: slow morph, rides the bass swell lightly — never flashes, just flows.
        BlobViolet.Opacity = Math.Clamp(0.60f + driftViolet * 0.18f + bass * 0.15f, 0f, 1f);
        VioletScale.ScaleX = VioletScale.ScaleY = 1.0 + driftViolet * 0.07 + bass * 0.05;
        VioletShift.X = (float)Math.Sin(t * 0.7) * 5.0;
        VioletShift.Y = (float)Math.Cos(t * 0.6) * 3.0;

        // AMBER: treble shimmer fleck, flickers with hats + snare.
        BlobAmber.Opacity = Math.Clamp(0.35f + treble * 0.40f + _snareEnv * 0.15f, 0f, 1f);
        AmberScale.ScaleX = AmberScale.ScaleY = 1.0 + treble * 0.12 + driftCyan * 0.05;
        AmberShift.X = (float)Math.Sin(t * 1.9 + 1.0) * 3.0;
    }
}
