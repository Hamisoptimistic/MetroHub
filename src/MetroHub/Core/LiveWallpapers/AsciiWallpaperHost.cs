using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace MetroHub.Core.LiveWallpapers;

public sealed class AsciiWallpaperHost : FrameworkElement, IDisposable
{
    private const int LogicalCols = AsciiFrameBuffer.DefaultColumns;
    private const int LogicalRows = AsciiFrameBuffer.DefaultRows;
    private const int ShadeBuckets = AsciiFrameBuffer.ShadeLevels;
    private const int PaletteCount = AsciiFrameBuffer.MaxPalettes;

    private readonly AsciiFrameBuffer _buffer = new(LogicalCols, LogicalRows);
    private readonly AsciiGlyphLut _glyphLut;
    private readonly Brush[,] _paletteBrushes = new Brush[PaletteCount, ShadeBuckets];

    private readonly GlyphRun?[,] _rowGlyphCache = new GlyphRun?[LogicalRows, PaletteCount * ShadeBuckets];
    private readonly ushort[] _scratchGlyphs = new ushort[LogicalCols];
    private readonly double[] _scratchAdvances = new double[LogicalCols];

    private IAsciiWallpaperScene? _activeScene;
    private readonly Stopwatch _clock = new();
    private TimeSpan _lastFrameTime;
    private double _accumulator;
    private bool _isRenderingHooked;
    private bool _isDisposed;

    private double _cellWidth = 12.0;
    private double _cellHeight = 18.0;
    private double _emSize = 14.0;
    private float _pixelsPerDip = 1.0f;

    public bool IsBatterySaver { get; set; }

    public AsciiWallpaperHost()
    {
        IsHitTestVisible = false;
        Focusable = false;
        SnapsToDevicePixels = true;

        var typeface = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        if (!typeface.TryGetGlyphTypeface(out GlyphTypeface? glyphTypeface) || glyphTypeface is null)
        {
            typeface = new Typeface(new FontFamily("Courier New"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (!typeface.TryGetGlyphTypeface(out glyphTypeface) || glyphTypeface is null)
            {
                throw new InvalidOperationException("Monospace GlyphTypeface could not be resolved.");
            }
        }

        _glyphLut = new AsciiGlyphLut(glyphTypeface);
        InitializePalettes();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void InitializePalettes()
    {
        ReadOnlySpan<(byte R, byte G, byte B)> baseColors =
        [
            (56, 189, 248),  // Palette 0: Cyan / Ocean
            (34, 197, 94),   // Palette 1: Matrix Emerald
            (251, 146, 60),  // Palette 2: Synthwave Amber
            (168, 85, 247)   // Palette 3: Deep Space Violet
        ];

        for (int p = 0; p < PaletteCount; p++)
        {
            var (r, g, b) = baseColors[p];
            for (int s = 1; s < ShadeBuckets; s++)
            {
                float t = s / (float)(ShadeBuckets - 1);
                float intensity = MathF.Pow(t, 1.35f);
                byte alpha = (byte)Math.Clamp((int)(intensity * 235f + 20f), 0, 255);
                var brush = new SolidColorBrush(Color.FromArgb(alpha,
                    (byte)(r * intensity),
                    (byte)(g * intensity),
                    (byte)(b * intensity)));
                brush.Freeze();
                _paletteBrushes[p, s] = brush;
            }
        }
    }

    public void SetScene(string sceneId)
    {
        _activeScene?.Dispose();
        _activeScene = AsciiWallpaperRegistry.Create(sceneId);
        _activeScene.Initialize(LogicalCols, LogicalRows, _glyphLut);
        _buffer.Clear();
        _activeScene.Update(0f);
        _activeScene.Render(_buffer);
        _buffer.CommitFrame();
        _buffer.MarkAllDirty();
        InvalidateAllCachedRuns();
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        RecalculateMetrics(sizeInfo.NewSize);
    }

    private void RecalculateMetrics(Size size)
    {
        if (size.Width <= 16 || size.Height <= 16) return;

        try
        {
            _pixelsPerDip = (float)VisualTreeHelper.GetDpi(this).PixelsPerDip;
        }
        catch
        {
            _pixelsPerDip = 1.0f;
        }

        _cellWidth = size.Width / LogicalCols;
        _cellHeight = size.Height / LogicalRows;

        double advanceRatio = 0.55;
        if (_glyphLut.Typeface.AdvanceWidths.TryGetValue(_glyphLut.SpaceGlyph, out double adv) && adv > 0.01)
        {
            advanceRatio = adv;
        }

        double heightRatio = _glyphLut.Typeface.Height;
        if (heightRatio <= 0.01) heightRatio = 1.17;

        _emSize = Math.Max(4.0, Math.Min(_cellWidth / advanceRatio, _cellHeight / heightRatio));
        InvalidateAllCachedRuns();
        _buffer.MarkAllDirty();
        InvalidateVisual();
    }

    private void InvalidateAllCachedRuns() => Array.Clear(_rowGlyphCache);

    private void OnLoaded(object sender, RoutedEventArgs e) => UpdateLoopSubscription();
    private void OnUnloaded(object sender, RoutedEventArgs e) => UnhookRendering();
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateLoopSubscription();

    public void SetWindowActiveState(bool isWindowActiveAndVisible)
    {
        if (!isWindowActiveAndVisible) UnhookRendering();
        else UpdateLoopSubscription();
    }

    private void UpdateLoopSubscription()
    {
        if (_isDisposed || !IsLoaded || !IsVisible)
        {
            UnhookRendering();
            return;
        }

        if (!_isRenderingHooked)
        {
            _clock.Restart();
            _lastFrameTime = TimeSpan.Zero;
            _accumulator = 0;
            CompositionTarget.Rendering += OnCompositionTargetRendering;
            _isRenderingHooked = true;
        }
    }

    private void UnhookRendering()
    {
        if (_isRenderingHooked)
        {
            CompositionTarget.Rendering -= OnCompositionTargetRendering;
            _clock.Stop();
            _isRenderingHooked = false;
        }
    }

    private void OnCompositionTargetRendering(object? sender, EventArgs e)
    {
        if (_activeScene is null) return;

        TimeSpan now = _clock.Elapsed;
        double frameDt = (now - _lastFrameTime).TotalSeconds;
        _lastFrameTime = now;

        if (frameDt <= 0 || frameDt > 0.25) frameDt = 1.0 / 60.0;

        double targetStep = IsBatterySaver ? (1.0 / 30.0) : (1.0 / 60.0);
        _accumulator += frameDt;

        // 1.5ms tolerance prevents 60Hz VSync micro-jitter from skipping every other tick
        if (_accumulator < targetStep - 0.0015) return;

        float stepDt = (float)Math.Min(_accumulator, 0.05);
        _accumulator = Math.Max(0.0, _accumulator - targetStep);

        _activeScene.Update(stepDt);
        _activeScene.Render(_buffer);

        if (_buffer.CommitFrame())
        {
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_activeScene is null || ActualWidth <= 16 || ActualHeight <= 16) return;

        double baselineOffset = _glyphLut.Typeface.Baseline * _emSize;

        for (int r = 0; r < LogicalRows; r++)
        {
            if (_buffer.IsRowDirty(r))
            {
                RebuildRowGlyphRuns(r, baselineOffset);
            }

            for (int bucket = 1; bucket < PaletteCount * ShadeBuckets; bucket++)
            {
                GlyphRun? run = _rowGlyphCache[r, bucket];
                if (run is not null)
                {
                    int palette = bucket / ShadeBuckets;
                    int shade = bucket % ShadeBuckets;
                    if (shade > 0)
                    {
                        dc.DrawGlyphRun(_paletteBrushes[palette, shade], run);
                    }
                }
            }
        }

        _buffer.ClearDirtyFlags();
    }

    private void RebuildRowGlyphRuns(int row, double baselineOffset)
    {
        ReadOnlySpan<AsciiCell> cells = _buffer.GetRowSpan(row);
        double rowY = (row * _cellHeight) + baselineOffset;

        for (int p = 0; p < PaletteCount; p++)
        {
            for (int s = 1; s < ShadeBuckets; s++)
            {
                int bucket = (p * ShadeBuckets) + s;
                _rowGlyphCache[row, bucket] = null;

                int count = 0;
                int firstCol = -1;
                int prevCol = -1;

                for (int c = 0; c < LogicalCols; c++)
                {
                    AsciiCell cell = cells[c];
                    if (cell.Shade == s && cell.Palette == p && cell.GlyphIndex != 0)
                    {
                        if (firstCol == -1)
                        {
                            firstCol = c;
                        }
                        else
                        {
                            _scratchAdvances[count - 1] = (c - prevCol) * _cellWidth;
                        }

                        _scratchGlyphs[count] = cell.GlyphIndex;
                        _scratchAdvances[count] = _cellWidth;
                        prevCol = c;
                        count++;
                    }
                }

                if (count > 0)
                {
                    var glyphsCopy = new ushort[count];
                    var advancesCopy = new double[count];
                    Array.Copy(_scratchGlyphs, 0, glyphsCopy, 0, count);
                    Array.Copy(_scratchAdvances, 0, advancesCopy, 0, count);

                    var origin = new Point(firstCol * _cellWidth, rowY);
                    _rowGlyphCache[row, bucket] = new GlyphRun(
                        _glyphLut.Typeface,
                        bidiLevel: 0,
                        isSideways: false,
                        renderingEmSize: _emSize,
                        pixelsPerDip: _pixelsPerDip,
                        glyphIndices: glyphsCopy,
                        baselineOrigin: origin,
                        advanceWidths: advancesCopy,
                        glyphOffsets: null,
                        characters: null,
                        deviceFontName: null,
                        clusterMap: null,
                        caretStops: null,
                        language: null);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        UnhookRendering();
        _activeScene?.Dispose();
        _activeScene = null;
        InvalidateAllCachedRuns();
    }
}

