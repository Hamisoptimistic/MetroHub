using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace MetroHub.Core.AsciiArt;

/// <summary>
/// Draws halftone packs (<see cref="AsciiRenderMode.Halftone"/>) with the same
/// algorithm as the ascii-magic-art canvas player: a dot grid denser than the
/// cell grid, bilinearly sampled brightness, percentile tone mapping, four dot
/// sizes with an ordered dither, and quarter-pixel antialiased stamps.
/// Pure pixel math over the pack arrays; no drawing engine, no per-frame
/// allocations. Not thread-safe: call from one thread (the worker thread).
/// </summary>
public sealed class HalftonePainter
{
    private const byte NoColor = 255;
    private const float InkLuminance = 0.88f;       // canvas.js L(null)
    private const float UnknownInkCoverage = 0.30f; // glyphs.js cover() fallback
    private const int HistogramBins = 512;
    private const int ToneLutSize = 1024;

    private static readonly float[] DotSizes = [0.2f, 0.5f, 0.85f, 1.32f];
    private static readonly int[] Bayer4 = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];

    private readonly AsciiPack _pack;
    private readonly AsciiHalftoneOptions _tuning;
    private readonly int _cellWidth;
    private readonly int _cellHeight;
    private readonly int _pixelWidth;
    private readonly int _pixelHeight;
    private readonly float[] _inkCoverage;
    private readonly float[] _luminance;
    private readonly uint[] _dotColors;
    private readonly float[] _brightness;
    private readonly byte[] _keyColor;
    private readonly int[] _histogram = new int[HistogramBins];
    private readonly float[] _toneLut = new float[ToneLutSize];
    private readonly Dictionary<(int Radius, int X, int Y), DiskMask> _masks = [];

    // Pre-allocated flat dot layers: zero per-frame GC allocations
    private readonly Dot[][] _layerDots;
    private readonly int[] _layerCounts = new int[DotSizes.Length];

    /// <summary>Creates a painter for one pack at one cell size, in device pixels.</summary>
    public HalftonePainter(AsciiPack pack, int cellWidth, int cellHeight)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentOutOfRangeException.ThrowIfLessThan(cellWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(cellHeight, 1);

        _pack = pack;
        _tuning = pack.HalftoneOptions;
        _cellWidth = cellWidth;
        _cellHeight = cellHeight;
        _pixelWidth = cellWidth * pack.Cols;
        _pixelHeight = cellHeight * pack.Rows;

        _inkCoverage = new float[pack.Glyphs.Length];
        for (int i = 0; i < _inkCoverage.Length; i++)
        {
            _inkCoverage[i] = pack.Glyphs[i] is ' ' or '\0' ? -1f : InkCoverage(pack.Glyphs[i]);
        }

        // Entry 0 is the default ink, which the canvas player treats as "no colour":
        // it uses the fixed ink luminance and still paints with the ink colour.
        _luminance = new float[pack.Colors.Length];
        _dotColors = new uint[pack.Colors.Length];
        for (int i = 0; i < pack.Colors.Length; i++)
        {
            Color color = pack.Colors[i];
            _luminance[i] = i == 0 ? InkLuminance : Luminance(color);
            _dotColors[i] = Brighten(color, _tuning.Saturation);
        }

        _brightness = new float[pack.Cells];
        _keyColor = new byte[pack.Cells];

        // Precompute gamma tone curve LUT: replaces 35,000 MathF.Pow calls per frame
        float gamma = _tuning.ToneCurve;
        for (int i = 0; i < ToneLutSize; i++)
        {
            float t = i / (float)(ToneLutSize - 1);
            _toneLut[i] = MathF.Pow(t, gamma);
        }

        // Pre-allocate dot layer buffers for max possible dots
        float unit = Math.Min(cellWidth, cellHeight);
        float step = unit / Math.Max(0.3f, _tuning.Density);
        int columns = Math.Max(1, (int)((_pixelWidth / step) + 0.5f));
        int rows = Math.Max(1, (int)((_pixelHeight / step) + 0.5f));
        int maxDots = columns * rows;

        _layerDots = new Dot[DotSizes.Length][];
        for (int i = 0; i < DotSizes.Length; i++)
        {
            _layerDots[i] = new Dot[maxDots];
        }
    }

    /// <summary>
    /// Paints one frame over the target buffer (at least <c>Cols * cellWidth</c> by
    /// <c>Rows * cellHeight</c> pixels). The caller fills the buffer with the
    /// canvas colour first; dots are composited over it.
    /// </summary>
    public void Render(Span<uint> target, int frameIndex)
    {
        Render(target, frameIndex, AudioFrame.Default);
    }

    /// <summary>
    /// Paints one frame with real-time acoustic physics modulation (ink bloom, multi-wave ripples, treble shimmer).
    /// </summary>
    public void Render(Span<uint> target, int frameIndex, in AudioFrame audio)
    {
        if (target.Length < _pixelWidth * _pixelHeight)
        {
            throw new ArgumentException("The target buffer is smaller than the scene.", nameof(target));
        }
        if ((uint)frameIndex >= (uint)_pack.FrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        BuildBrightness(frameIndex);
        if (TryNormalizeTone(out float low, out float high))
        {
            DrawDots(target, low, high, audio);
        }
    }

    /// <summary>Cell brightness = ink coverage x colour luminance, -1f for empty cells.</summary>
    private void BuildBrightness(int frameIndex)
    {
        int cells = _pack.Cells;
        int frameBase = frameIndex * cells;
        ReadOnlySpan<byte> glyphIds = _pack.GlyphIds.AsSpan(frameBase, cells);
        ReadOnlySpan<byte> colorIds = _pack.ColorIds.AsSpan(frameBase, cells);

        for (int i = 0; i < cells; i++)
        {
            int glyphId = glyphIds[i];
            float ink = glyphId < _inkCoverage.Length ? _inkCoverage[glyphId] : -1f;
            if (ink < 0f)
            {
                _brightness[i] = -1f;
                _keyColor[i] = NoColor;
                continue;
            }

            int colorId = colorIds[i];
            if (colorId >= _luminance.Length)
            {
                colorId = 0;
            }

            _brightness[i] = ink * _luminance[colorId];
            _keyColor[i] = (byte)colorId;
        }
    }

    /// <summary>
    /// Maps brightness through the frame's 4% and 98.5% percentiles, so every
    /// scene uses its full tonal spread. False when the frame has no ink at all.
    /// </summary>
    private bool TryNormalizeTone(out float low, out float high)
    {
        Array.Clear(_histogram);
        float maximum = 0f;
        int count = 0;
        foreach (float value in _brightness)
        {
            if (value > maximum)
            {
                maximum = value;
            }
            if (value >= 0f)
            {
                count++;
            }
        }

        if (maximum <= 0f || count == 0)
        {
            low = 0f;
            high = 1f;
            return false;
        }

        foreach (float value in _brightness)
        {
            if (value >= 0f)
            {
                _histogram[Math.Min(HistogramBins - 1, (int)(value / maximum * HistogramBins))]++;
            }
        }

        float lowQuantile = _tuning.ToneLow > 0f ? _tuning.ToneLow : 0.04f;
        float highQuantile = _tuning.ToneHigh > 0f ? _tuning.ToneHigh : 0.985f;
        low = Percentile(lowQuantile, maximum, count);
        high = Math.Max(Percentile(highQuantile, maximum, count), low + 1e-4f);
        return true;
    }

    private float Percentile(float quantile, float maximum, int count)
    {
        float wanted = quantile * count;
        int accumulated = 0;
        for (int bin = 0; bin < _histogram.Length; bin++)
        {
            accumulated += _histogram[bin];
            if (accumulated >= wanted)
            {
                return ((bin + 0.5f) / HistogramBins) * maximum;
            }
        }
        return maximum;
    }

    private void DrawDots(Span<uint> target, float low, float high, in AudioFrame audio)
    {
        Array.Clear(_layerCounts);

        float unit = Math.Min(_cellWidth, _cellHeight);
        float step = unit / Math.Max(0.3f, _tuning.Density);
        int columns = Math.Max(1, (int)((_pixelWidth / step) + 0.5f));
        int rows = Math.Max(1, (int)((_pixelHeight / step) + 0.5f));
        float stepX = (float)_pixelWidth / columns;
        float stepY = (float)_pixelHeight / rows;
        float toneRange = high - low;

        // Dynamic acoustic ink expansion from bass rhythm (gentle breathing)
        float dynamicScale = _tuning.DotScale * (1.0f + (audio.Bass * 0.20f));

        ReadOnlySpan<HalftoneAudioWave> waves = audio.ActiveWaves.Span;
        bool hasWaves = waves.Length > 0;

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                float pixelX = (column + 0.5f) * stepX;
                float pixelY = (row + 0.5f) * stepY;
                float cellX = (pixelX / _cellWidth) - 0.5f;
                float cellY = (pixelY / _cellHeight) - 0.5f;

                if (!TrySample(cellX, cellY, out float value, out byte key))
                {
                    continue;
                }

                float tone = Math.Clamp((value - low) / toneRange, 0f, 1f);
                int lutIndex = (int)(tone * (ToneLutSize - 1));
                tone = _toneLut[lutIndex];

                // Static Bayer ordered dither (no treble shimmer strobe)
                float ditherThreshold = (Bayer4[((row & 3) * 4) + (column & 3)] + 0.5f) / 16f;

                float scaled = (tone * DotSizes.Length) - 0.5f;
                int level = (int)MathF.Floor(scaled);
                if ((scaled - level) > ditherThreshold)
                {
                    level++;
                }

                if (level < 0)
                {
                    if (tone > 0.01f)
                    {
                        level = 0;
                    }
                    else
                    {
                        continue;
                    }
                }
                if (level >= DotSizes.Length)
                {
                    level = DotSizes.Length - 1;
                }

                float radius = 0.5f * unit * dynamicScale * DotSizes[level];

                // Multi-wave radial shockwave modulation (smooth organic water ripple)
                if (hasWaves)
                {
                    float waveMod = 0f;
                    for (int w = 0; w < waves.Length; w++)
                    {
                        ref readonly HalftoneAudioWave wave = ref waves[w];
                        float dx = cellX - wave.CenterX;
                        float dy = cellY - wave.CenterY;
                        float r = MathF.Sqrt((dx * dx) + (dy * dy));
                        float dist = r - (180f * wave.ElapsedSeconds);
                        // Gaussian pulse with sigma = 5.5 cells (2 * sigma^2 = 60)
                        float pulse = MathF.Exp(-(dist * dist) / 60f);
                        float amp = 0.15f * Math.Max(0f, 1f - (wave.ElapsedSeconds / 0.40f)) * wave.Intensity;
                        waveMod += amp * pulse;
                    }
                    radius *= 1.0f + Math.Min(0.20f, waveMod);
                }

                // Safety clamp: dot radius never exceeds 0.75 of cell size (maintains contrast and negative space)
                float maxRadius = 0.75f * unit;
                if (radius > maxRadius)
                {
                    radius = maxRadius;
                }

                if (radius < 0.3f)
                {
                    continue;
                }

                _layerDots[level][_layerCounts[level]++] = new Dot(pixelX, pixelY, radius, _dotColors[key]);
            }
        }

        // Big dots last, so overlapping neighbours merge like printed halftone.
        for (int level = 0; level < DotSizes.Length; level++)
        {
            int count = _layerCounts[level];
            Dot[] layer = _layerDots[level];
            for (int i = 0; i < count; i++)
            {
                Stamp(target, layer[i]);
            }
        }
    }

    /// <summary>
    /// Bilinear brightness at a dot centre (cell units: a cell's centre is its
    /// integer index) plus the colour of the nearest cell.
    /// </summary>
    private bool TrySample(float fx, float fy, out float value, out byte key)
    {
        int cols = _pack.Cols;
        int rows = _pack.Rows;
        int left = Math.Clamp((int)MathF.Floor(fx), 0, cols - 1);
        int top = Math.Clamp((int)MathF.Floor(fy), 0, rows - 1);
        int right = Math.Min(cols - 1, left + 1);
        int bottom = Math.Min(rows - 1, top + 1);
        float tx = Math.Clamp(fx - left, 0f, 1f);
        float ty = Math.Clamp(fy - top, 0f, 1f);

        float topLeft = Math.Max(0f, _brightness[(top * cols) + left]);
        value = (topLeft * (1f - tx) * (1f - ty))
            + (Math.Max(0f, _brightness[(top * cols) + right]) * tx * (1f - ty))
            + (Math.Max(0f, _brightness[(bottom * cols) + left]) * (1f - tx) * ty)
            + (Math.Max(0f, _brightness[(bottom * cols) + right]) * tx * ty);

        // JavaScript Math.round, which the canvas player uses throughout.
        int nearestColumn = Math.Clamp((int)MathF.Floor(fx + 0.5f), 0, cols - 1);
        int nearestRow = Math.Clamp((int)MathF.Floor(fy + 0.5f), 0, rows - 1);
        int nearest = (nearestRow * cols) + nearestColumn;
        bool empty = _brightness[nearest] < 0f && topLeft < 0f;
        key = _keyColor[nearest] != NoColor ? _keyColor[nearest] : _keyColor[(top * cols) + left];
        return !empty && key != NoColor;
    }

    private void Stamp(Span<uint> target, Dot dot)
    {
        int baseX = (int)MathF.Floor(dot.X);
        int baseY = (int)MathF.Floor(dot.Y);
        int radiusQuarters = (int)MathF.Floor((dot.Radius * 4f) + 0.5f);
        int offsetXQuarters = (int)MathF.Floor(((dot.X - baseX - 0.5f) * 4f) + 0.5f);
        int offsetYQuarters = (int)MathF.Floor(((dot.Y - baseY - 0.5f) * 4f) + 0.5f);
        DiskMask mask = GetMask(radiusQuarters, offsetXQuarters, offsetYQuarters);

        int left = baseX - mask.Radius;
        int top = baseY - mask.Radius;
        int size = (mask.Radius * 2) + 1;
        uint red = (dot.Color >> 16) & 0xFF;
        uint green = (dot.Color >> 8) & 0xFF;
        uint blue = dot.Color & 0xFF;

        for (int y = 0; y < size; y++)
        {
            int targetY = top + y;
            if ((uint)targetY >= (uint)_pixelHeight)
            {
                continue;
            }

            int rowBase = targetY * _pixelWidth;
            int maskBase = y * size;
            for (int x = 0; x < size; x++)
            {
                int targetX = left + x;
                if ((uint)targetX >= (uint)_pixelWidth)
                {
                    continue;
                }

                uint alpha = mask.Coverage[maskBase + x];
                if (alpha == 0)
                {
                    continue;
                }

                // Integer fixed-point blending: (fg * alpha + bg * (256 - alpha)) >> 8
                // Always force 0xFF000000 alpha to keep pixels completely opaque.
                uint pixel = target[rowBase + targetX];
                uint invAlpha = 256u - alpha;
                uint bgRed = (pixel >> 16) & 0xFF;
                uint bgGreen = (pixel >> 8) & 0xFF;
                uint bgBlue = pixel & 0xFF;

                uint outRed = ((red * alpha) + (bgRed * invAlpha)) >> 8;
                uint outGreen = ((green * alpha) + (bgGreen * invAlpha)) >> 8;
                uint outBlue = ((blue * alpha) + (bgBlue * invAlpha)) >> 8;

                target[rowBase + targetX] = 0xFF000000u | (outRed << 16) | (outGreen << 8) | outBlue;
            }
        }
    }

    /// <summary>
    /// One antialiased disk mask per (quarter-pixel radius, quarter-pixel centre
    /// offset). Quantising both keeps the cache small: a full scene uses a few
    /// hundred entries.
    /// </summary>
    private DiskMask GetMask(int radiusQuarters, int offsetXQuarters, int offsetYQuarters)
    {
        (int Radius, int X, int Y) key = (radiusQuarters, offsetXQuarters, offsetYQuarters);
        if (_masks.TryGetValue(key, out DiskMask cached))
        {
            return cached;
        }

        float radius = radiusQuarters / 4f;
        float centerX = offsetXQuarters / 4f;
        float centerY = offsetYQuarters / 4f;
        int edge = (int)MathF.Ceiling(radius + 1f);
        int size = (edge * 2) + 1;
        ushort[] coverage = new ushort[size * size];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int hits = 0;
                for (int sampleY = 0; sampleY < 4; sampleY++)
                {
                    for (int sampleX = 0; sampleX < 4; sampleX++)
                    {
                        float dx = (x - edge) + ((sampleX + 0.5f) / 4f) - centerX;
                        float dy = (y - edge) + ((sampleY + 0.5f) / 4f) - centerY;
                        if ((dx * dx) + (dy * dy) <= radius * radius)
                        {
                            hits++;
                        }
                    }
                }

                // Map [0..16] hits to [0..256] fixed-point alpha (hits << 4)
                coverage[(y * size) + x] = (ushort)(hits << 4);
            }
        }

        DiskMask mask = new(edge, coverage);
        _masks[key] = mask;
        return mask;
    }

    private static float InkCoverage(char glyph) => glyph switch
    {
        // Block ramp ink values from the ascii-magic-art canvas player.
        '▁' => 0.135f,
        '▂' => 0.257f,
        '▃' => 0.378f,
        '▄' => 0.510f,
        '▅' => 0.635f,
        '▆' => 0.757f,
        '▇' => 0.878f,
        '█' => 1.000f,
        // Shade blocks use their nominal density; other glyphs the engine fallback.
        '░' => 0.25f,
        '▒' => 0.50f,
        '▓' => 0.75f,
        _ => UnknownInkCoverage,
    };

    private static float Luminance(Color color) =>
        ((0.2126f * color.R) + (0.7152f * color.G) + (0.0722f * color.B)) / 255f;

    /// <summary>Brightens a colour toward its hue limit, like the canvas player's brighten().</summary>
    private static uint Brighten(Color color, float saturation)
    {
        int maximum = Math.Max(1, Math.Max((int)color.R, Math.Max((int)color.G, (int)color.B)));
        float factor = ((255f / maximum) * saturation) + (1f - saturation);
        uint red = Channel(color.R * factor);
        uint green = Channel(color.G * factor);
        uint blue = Channel(color.B * factor);
        return (red << 16) | (green << 8) | blue;
    }

    private static uint Channel(float value) => (uint)Math.Clamp(value + 0.5f, 0f, 255f);

    private readonly record struct Dot(float X, float Y, float Radius, uint Color);

    private readonly record struct DiskMask(int Radius, ushort[] Coverage);
}
