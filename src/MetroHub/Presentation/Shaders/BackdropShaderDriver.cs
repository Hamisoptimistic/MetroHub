using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace MetroHub.Presentation.Shaders;

/// <summary>
/// Drives animation updates for MetroShaderEffect on WPF backdrops.
/// Manages the CompositionTarget.Rendering loop with a 60 FPS frame limiter to eliminate high CPU spikes
/// on high-refresh displays (144Hz - 240Hz), cached aspect ratio calculations, lifecycle gating
/// (0% CPU/GPU when hidden or minimized), bounded modulo time progression, and wall-clock time in register c0.z.
/// </summary>
public sealed class BackdropShaderDriver : IDisposable
{
    private const double MaxTimeModulo = 400.0 * Math.PI;
    private const double TargetFps = 60.0;
    private const double MinFrameIntervalSeconds = 1.0 / TargetFps;

    private readonly Window _window;
    private readonly FrameworkElement? _targetElement;
    private readonly Stopwatch _clock = new();
    private MetroShaderEffect? _activeEffect;
    private double _lastRenderTimeSeconds = -1.0;
    private double _aspectRatio = 16.0 / 9.0;
    private bool _isRendering;
    private bool _isDisposed;

    /// <summary>
    /// Indicates whether Direct3D Tier 2 (full hardware acceleration) is available.
    /// </summary>
    public static bool IsHardwareAccelerated => (RenderCapability.Tier >> 16) >= 2;

    /// <summary>
    /// Allows unit tests to bypass the hardware tier check in headless or virtualized environments.
    /// </summary>
    internal bool BypassHardwareTierCheck { get; set; }

    /// <summary>
    /// True when the CompositionTarget.Rendering render loop is active.
    /// </summary>
    public bool IsRendering => _isRendering;

    /// <summary>
    /// The currently active shader effect being driven, if any.
    /// </summary>
    public MetroShaderEffect? ActiveEffect => _activeEffect;

    /// <summary>
    /// The currently cached aspect ratio.
    /// </summary>
    public double AspectRatio => _aspectRatio;

    public BackdropShaderDriver(Window window, FrameworkElement? targetElement = null)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _targetElement = targetElement;

        _window.IsVisibleChanged += OnIsVisibleChanged;
        _window.StateChanged += OnStateChanged;
        _window.SizeChanged += OnSizeChanged;
        _window.Closed += OnWindowClosed;

        if (_targetElement != null)
        {
            _targetElement.SizeChanged += OnSizeChanged;
        }

        UpdateAspectRatio();
    }

    /// <summary>
    /// Attaches the shader effect and begins driving frames if the window is visible and hardware-accelerated.
    /// </summary>
    public void Attach(MetroShaderEffect effect)
    {
        if (_isDisposed) return;
        ArgumentNullException.ThrowIfNull(effect);

        _activeEffect = effect;
        _lastRenderTimeSeconds = -1.0;

        _window.PreviewMouseLeftButtonDown -= OnWindowMouseDown;
        _window.PreviewMouseLeftButtonDown += OnWindowMouseDown;

        UpdateAspectRatio();
        UpdateDriverState();
    }

    /// <summary>
    /// Detaches the active shader effect and terminates the render loop immediately.
    /// </summary>
    public void Detach()
    {
        _window.PreviewMouseLeftButtonDown -= OnWindowMouseDown;
        _activeEffect = null;
        StopRendering();
    }

    private void OnWindowMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_activeEffect == null || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;

        // Ignore clicks on foreground controls (tiles, buttons, inputs, drawers)
        if (e.OriginalSource is DependencyObject dep && IsInteractiveOrTile(dep))
        {
            return;
        }

        if (string.Equals(_activeEffect.ShaderName, "WaterCaustics", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(_activeEffect.ShaderName, "NeonClock", StringComparison.OrdinalIgnoreCase))
        {
            _activeEffect.CyclePalette(6);
        }
    }

    private static bool IsInteractiveOrTile(DependencyObject dep)
    {
        DependencyObject? current = dep;
        while (current != null)
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase ||
                current is System.Windows.Controls.Primitives.TextBoxBase ||
                current is System.Windows.Controls.Primitives.ScrollBar ||
                current is System.Windows.Controls.Primitives.Thumb ||
                string.Equals(current.GetType().Name, "TileControl", StringComparison.Ordinal) ||
                string.Equals(current.GetType().Name, "GroupHeaderControl", StringComparison.Ordinal) ||
                string.Equals(current.GetType().Name, "AllAppsDrawerControl", StringComparison.Ordinal) ||
                string.Equals(current.GetType().Name, "SidebarRailControl", StringComparison.Ordinal))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateDriverState();

    private void OnStateChanged(object? sender, EventArgs e) => UpdateDriverState();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateAspectRatio();

    private void OnWindowClosed(object? sender, EventArgs e) => Dispose();

    private void UpdateAspectRatio()
    {
        double width = _targetElement?.ActualWidth ?? 0;
        double height = _targetElement?.ActualHeight ?? 0;

        if (width <= 0 || height <= 0)
        {
            width = _window.ActualWidth;
            height = _window.ActualHeight;
        }

        if (width <= 0 || height <= 0)
        {
            width = SystemParameters.PrimaryScreenWidth;
            height = SystemParameters.PrimaryScreenHeight;
        }

        double aspect = (width > 0 && height > 0 && !double.IsNaN(width) && !double.IsNaN(height))
            ? (width / height)
            : (16.0 / 9.0);

        if (double.IsNaN(aspect) || double.IsInfinity(aspect) || aspect <= 0)
        {
            aspect = 16.0 / 9.0;
        }

        _aspectRatio = aspect;
    }

    private void UpdateDriverState()
    {
        if (_isDisposed) return;

        bool canAccelerate = IsHardwareAccelerated || BypassHardwareTierCheck;
        bool shouldRender = _activeEffect != null &&
                            _window.IsVisible &&
                            _window.WindowState != WindowState.Minimized &&
                            canAccelerate;

        if (shouldRender)
        {
            StartRendering();
        }
        else
        {
            StopRendering();
        }
    }

    private void StartRendering()
    {
        if (_isRendering) return;

        _lastRenderTimeSeconds = -1.0;
        _clock.Start();
        CompositionTarget.Rendering += OnRendering;
        _isRendering = true;
    }

    private void StopRendering()
    {
        if (!_isRendering) return;

        CompositionTarget.Rendering -= OnRendering;
        _clock.Stop();
        _isRendering = false;
        _lastRenderTimeSeconds = -1.0;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_activeEffect == null)
        {
            StopRendering();
            return;
        }

        double elapsedSeconds = _clock.Elapsed.TotalSeconds;

        // 60 FPS throttle: skip ticks on high-refresh monitors (144Hz/240Hz) to keep CPU load near 0%
        if (_lastRenderTimeSeconds >= 0 && (elapsedSeconds - _lastRenderTimeSeconds) < MinFrameIntervalSeconds)
        {
            return;
        }
        _lastRenderTimeSeconds = elapsedSeconds;

        double time = elapsedSeconds % MaxTimeModulo;
        double timeOfDaySeconds = DateTime.Now.TimeOfDay.TotalSeconds;
        _activeEffect.FrameState = new Point4D(time, _aspectRatio, timeOfDaySeconds, 0.0);

        try
        {
            Point mousePos = System.Windows.Input.Mouse.GetPosition(_window);
            double width = _window.ActualWidth > 0 ? _window.ActualWidth : SystemParameters.PrimaryScreenWidth;
            double height = _window.ActualHeight > 0 ? _window.ActualHeight : SystemParameters.PrimaryScreenHeight;
            double mx = Math.Clamp(mousePos.X / width, 0.0, 1.0);
            double my = Math.Clamp(mousePos.Y / height, 0.0, 1.0);
            double leftDown = System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed ? 1.0 : 0.0;
            _activeEffect.MouseState = new Point4D(mx, my, leftDown, 0.0);
        }
        catch
        {
            // Non-critical fallback if mouse position cannot be queried
        }
    }

    /// <summary>
    /// Pumps a single animation frame update. Exposed for unit testing and deterministic verification.
    /// </summary>
    internal void Tick(bool force = true)
    {
        if (force)
        {
            _lastRenderTimeSeconds = -1.0;
            UpdateAspectRatio();
        }
        OnRendering(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Detach();
        _window.IsVisibleChanged -= OnIsVisibleChanged;
        _window.StateChanged -= OnStateChanged;
        _window.SizeChanged -= OnSizeChanged;
        _window.Closed -= OnWindowClosed;

        if (_targetElement != null)
        {
            _targetElement.SizeChanged -= OnSizeChanged;
        }
    }
}
