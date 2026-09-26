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
        if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            BassScale.CenterX = e.NewSize.Width * 0.5;
            BassScale.CenterY = e.NewSize.Height;
        }
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

        // 2. Render Buffering State: Organic Breathing Glow in Warm Amber
        if (buffering)
        {
            double elapsed = (DateTime.UtcNow - _startTime).TotalSeconds;
            float pulse = (float)(Math.Sin(elapsed * 3.5) * 0.5 + 0.5);

            byte amberAlpha = (byte)((0.15f + pulse * 0.25f) * 255f);
            byte coralAlpha = (byte)(amberAlpha * 0.45f);
            byte goldAlpha = (byte)(amberAlpha * 0.65f);

            BassStop0.Color = Color.FromArgb(coralAlpha, 0xEF, 0x23, 0x3C);
            BassStop1.Color = Color.FromArgb((byte)(coralAlpha * 0.7f), 0xD9, 0x04, 0x29);

            MidStop0.Color = Color.FromArgb(amberAlpha, 0xF7, 0x7F, 0x00);
            MidStop1.Color = Color.FromArgb((byte)(amberAlpha * 0.6f), 0xFC, 0xBF, 0x49);

            TrebleStop0.Color = Color.FromArgb(goldAlpha, 0xFF, 0xE6, 0xA7);
            TrebleStop1.Color = Color.FromArgb((byte)(goldAlpha * 0.5f), 0xFF, 0xF3, 0xB0);

            BassScale.ScaleX = 1.0 + pulse * 0.03;
            BassScale.ScaleY = 1.0 + pulse * 0.04;
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

        // Bass Layer (Deep Coral / Crimson Base #D90429 / #EF233C)
        // Resting: 20% alpha; Peak: 70% alpha
        byte bAlpha0 = (byte)((0.20f + bass * 0.50f) * 255f);
        byte bAlpha1 = (byte)(bAlpha0 * 0.70f);
        BassStop0.Color = Color.FromArgb(bAlpha0, 0xEF, 0x23, 0x3C);
        BassStop1.Color = Color.FromArgb(bAlpha1, 0xD9, 0x04, 0x29);

        // Sub-bass physical tile expansion pulse
        BassScale.ScaleX = 1.0 + bass * 0.06;
        BassScale.ScaleY = 1.0 + bass * 0.08;

        // Mid Layer (Warm Amber / Sunset Orange Core #F77F00 / #FCBF49)
        // Resting: 18% alpha; Peak: 63% alpha
        byte mAlpha0 = (byte)((0.18f + mid * 0.45f) * 255f);
        byte mAlpha1 = (byte)(mAlpha0 * 0.60f);
        MidStop0.Color = Color.FromArgb(mAlpha0, 0xF7, 0x7F, 0x00);
        MidStop1.Color = Color.FromArgb(mAlpha1, 0xFC, 0xBF, 0x49);

        // Treble Layer (Golden Shimmer Radiance #FFE6A7 / #FFF3B0)
        // Resting: 12% alpha; Peak: 50% alpha
        byte tAlpha0 = (byte)((0.12f + treble * 0.38f) * 255f);
        byte tAlpha1 = (byte)(tAlpha0 * 0.50f);
        TrebleStop0.Color = Color.FromArgb(tAlpha0, 0xFF, 0xE6, 0xA7);
        TrebleStop1.Color = Color.FromArgb(tAlpha1, 0xFF, 0xF3, 0xB0);
    }
}
