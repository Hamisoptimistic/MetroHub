using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MetroHub.Core.AsciiArt;

/// <summary>
/// Precomputed geometry and colour descriptor for one halftone dot in the hexagonal lattice.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct HalftoneDotSpec(
    float Cx,
    float Cy,
    byte R,
    byte G,
    byte B,
    float BaseRadius,
    float Detail);

/// <summary>
/// High-density, 32-bit TrueColor hexagonal halftone renderer for user wallpapers.
/// <para>
/// The photograph is treated as ink resting on a vibrating membrane: every dot is
/// refracted along the wave gradient, drifted by a divergence-free curl field,
/// and tinted by a travelling specular sheen. Because the lattice compresses and
/// stretches locally, ink density changes on its own and the image breathes
/// internally instead of scaling as a whole. Colours are sampled from the
/// original position, so the picture never swims.
/// </para>
/// <para>Zero per-frame GC allocations.</para>
/// </summary>
public sealed class TrueColorHalftoneEngine
{
    private const byte CanvasRed = 0x0B;
    private const byte CanvasGreen = 0x0B;
    private const byte CanvasBlue = 0x0F;

    // Normalized surface normals for quantized gradient pairs. Removes a sqrt and
    // a divide per dot from the hot loop.
    private const int NormalLutSize = 64;
    private const int NormalLutStride = 3;
    private const float NormalLutScale = 32f;
    private const float NormalLutBias = 32f;

    // Dot radii are bucketed to 1/16 px and their sub-pixel phase to 1/4 px, so the
    // exact per-pixel footprint of a dot is a table lookup. Coverage is baked into
    // that table, which keeps all float math out of the pixel loop.
    private const float RadiusLevelScale = 16f;
    private const int StampPhases = 4;

    private readonly HalftoneDotSpec[] _dots;
    private readonly float[] _normalLut;
    private readonly int[] _stampStart;
    private readonly int[] _stampOpaqueEnd;
    private readonly int[] _entryOffset;
    private readonly sbyte[] _entryX;
    private readonly sbyte[] _entryY;
    private readonly byte[] _entryAlpha;
    private readonly int _stampMargin;
    private readonly int _maxRadiusLevel;
    private readonly float _maxRadius;
    private readonly LiquidPrintLook _look;

    public int Width { get; }
    public int Height { get; }

    public TrueColorHalftoneEngine(
        string imagePath,
        int targetWidth = 1920,
        int targetHeight = 1080,
        float cellSize = 6.0f,
        LiquidPrintLook? look = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetHeight, 1);

        // The footprint table stores per-entry offsets in sbyte, which covers a dot
        // radius of up to about 100 px; the shipped look uses 6.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cellSize, 32f);
        ArgumentOutOfRangeException.ThrowIfLessThan(cellSize, 1f);

        _look = look ?? LiquidPrintLook.Default;

        BitmapImage src = new();
        src.BeginInit();
        src.UriSource = new Uri(imagePath, UriKind.Absolute);
        src.CacheOption = BitmapCacheOption.OnLoad;
        src.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        src.DecodePixelWidth = targetWidth;
        src.EndInit();
        src.Freeze();

        FormatConvertedBitmap converted = new(src, PixelFormats.Bgra32, null, 0);
        converted.Freeze();

        // Render to targetWidth x targetHeight with UniformToFill (Cover)
        RenderTargetBitmap rtb = new(targetWidth, targetHeight, 96, 96, PixelFormats.Pbgra32);
        DrawingVisual visual = new();
        using (DrawingContext dc = visual.RenderOpen())
        {
            double imgW = converted.PixelWidth;
            double imgH = converted.PixelHeight;
            double scale = Math.Max((double)targetWidth / imgW, (double)targetHeight / imgH);
            double drawW = imgW * scale;
            double drawH = imgH * scale;
            double drawX = (targetWidth - drawW) * 0.5;
            double drawY = (targetHeight - drawH) * 0.5;

            dc.DrawImage(converted, new Rect(drawX, drawY, drawW, drawH));
        }
        rtb.Render(visual);

        int stride = targetWidth * 4;
        byte[] bgra = new byte[targetHeight * stride];
        rtb.CopyPixels(bgra, stride, 0);

        Width = targetWidth;
        Height = targetHeight;
        _maxRadius = (cellSize * 0.5f) * 1.08f;

        float stepX = cellSize;
        float stepY = cellSize * 0.8660254f; // Equilateral hexagonal spacing (sqrt(3)/2)

        int cols = (int)Math.Ceiling(targetWidth / stepX) + 1;
        int rows = (int)Math.Ceiling(targetHeight / stepY) + 1;

        List<HalftoneDotSpec> dotList = new(cols * rows);

        for (int row = 0; row < rows; row++)
        {
            float cy = (row + 0.5f) * stepY;
            if (cy < 0 || cy >= targetHeight) continue;

            float xOffset = (row & 1) * (stepX * 0.5f);

            for (int col = 0; col < cols; col++)
            {
                float cx = (col * stepX) + xOffset;
                if (cx < 0 || cx >= targetWidth) continue;

                int sampleX = Math.Clamp((int)cx, 0, targetWidth - 1);
                int sampleY = Math.Clamp((int)cy, 0, targetHeight - 1);
                int offset = (sampleY * stride) + (sampleX * 4);

                byte b = bgra[offset];
                byte g = bgra[offset + 1];
                byte r = bgra[offset + 2];

                float luma = ((0.2126f * r) + (0.7152f * g) + (0.0722f * b)) / 255.0f;

                // Perceptual tone curve lifts dark shadows gently and maps highlights cleanly
                float baseRadius = _maxRadius * MathF.Pow(luma, 0.70f);

                float detail = SobelDetail(bgra, stride, targetWidth, targetHeight, sampleX, sampleY);

                dotList.Add(new HalftoneDotSpec(cx, cy, r, g, b, baseRadius, detail));
            }
        }

        _dots = dotList.ToArray();
        _normalLut = BuildNormalLut(_look);
        _maxRadiusLevel = Math.Max(1, (int)(_maxRadius * 1.3f * RadiusLevelScale));
        BuildStampTable(
            out _stampStart,
            out _stampOpaqueEnd,
            out _entryOffset,
            out _entryX,
            out _entryY,
            out _entryAlpha,
            out _stampMargin);
    }

    /// <summary>
    /// Precomputes the exact pixel footprint of a dot for every 1/16 px radius bucket
    /// and every 1/4 px sub-pixel phase: a flat list of pixel offsets plus their
    /// 8-bit coverage. The render loop therefore contains no square roots, no
    /// divisions and no per-pixel float math at all, and each dot is one flat,
    /// fully independent loop instead of one short loop per scanline.
    /// <para>
    /// Entries are emitted fully-covered first, then partial coverage, so the render
    /// loop can blast the interior with plain stores before switching to blending.
    /// </para>
    /// </summary>
    private void BuildStampTable(
        out int[] stampStart,
        out int[] stampOpaqueEnd,
        out int[] entryOffset,
        out sbyte[] entryX,
        out sbyte[] entryY,
        out byte[] entryAlpha,
        out int stampMargin)
    {
        int maxLevel = _maxRadiusLevel;
        stampStart = new int[((maxLevel + 1) * StampPhases * StampPhases) + 1];
        stampOpaqueEnd = new int[stampStart.Length - 1];

        List<int> offsets = new(1 << 14);
        List<sbyte> xs = new(1 << 14);
        List<sbyte> ys = new(1 << 14);
        List<byte> alphas = new(1 << 14);

        List<int> solidOffsets = new(64);
        List<sbyte> solidX = new(64);
        List<sbyte> solidY = new(64);
        List<int> edgeOffsets = new(64);
        List<sbyte> edgeX = new(64);
        List<sbyte> edgeY = new(64);
        List<byte> edgeAlphas = new(64);

        int margin = 0;

        for (int level = 0; level <= maxLevel; level++)
        {
            float radius = MathF.Max(0.05f, level / RadiusLevelScale);
            float reach = radius + 0.5f;
            float reachSquared = reach * reach;
            float edgeScale = 128f / radius;
            int extent = (int)MathF.Ceiling(reach) + 1;
            if (extent > margin)
            {
                margin = extent;
            }

            for (int phaseY = 0; phaseY < StampPhases; phaseY++)
            {
                float centreY = (phaseY + 0.5f) * (1f / StampPhases);

                for (int phaseX = 0; phaseX < StampPhases; phaseX++)
                {
                    float centreX = (phaseX + 0.5f) * (1f / StampPhases);
                    int stamp = StampIndex(level, phaseX, phaseY);
                    stampStart[stamp] = offsets.Count;

                    solidOffsets.Clear();
                    solidX.Clear();
                    solidY.Clear();
                    edgeOffsets.Clear();
                    edgeX.Clear();
                    edgeY.Clear();
                    edgeAlphas.Clear();

                    for (int ky = -extent; ky <= extent; ky++)
                    {
                        float dy = ky - centreY;
                        float dySquared = dy * dy;

                        for (int kx = -extent; kx <= extent; kx++)
                        {
                            float dx = kx - centreX;
                            int alpha = (int)((reachSquared - ((dx * dx) + dySquared)) * edgeScale);
                            if (alpha <= 0)
                            {
                                continue;
                            }

                            if (alpha >= 255)
                            {
                                solidOffsets.Add((ky * Width) + kx);
                                solidX.Add((sbyte)kx);
                                solidY.Add((sbyte)ky);
                            }
                            else
                            {
                                edgeOffsets.Add((ky * Width) + kx);
                                edgeX.Add((sbyte)kx);
                                edgeY.Add((sbyte)ky);
                                edgeAlphas.Add((byte)alpha);
                            }
                        }
                    }

                    offsets.AddRange(solidOffsets);
                    xs.AddRange(solidX);
                    ys.AddRange(solidY);
                    stampOpaqueEnd[stamp] = offsets.Count;
                    offsets.AddRange(edgeOffsets);
                    xs.AddRange(edgeX);
                    ys.AddRange(edgeY);

                    // The coverage array stays index-aligned with the offset array; the
                    // solid entries above are only ever read through it by the border
                    // path, where 255 makes the blend reproduce the dot colour exactly.
                    for (int solid = 0; solid < solidOffsets.Count; solid++)
                    {
                        alphas.Add(255);
                    }

                    alphas.AddRange(edgeAlphas);
                }
            }
        }

        stampStart[^1] = offsets.Count;
        entryOffset = offsets.ToArray();
        entryX = xs.ToArray();
        entryY = ys.ToArray();
        entryAlpha = alphas.ToArray();
        stampMargin = margin;
    }

    /// <summary>Flat index of a (radius bucket, sub-pixel phase) footprint.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int StampIndex(int level, int phaseX, int phaseY) =>
        ((level * StampPhases) + phaseY) * StampPhases + phaseX;

    /// <summary>
    /// Precomputes normalized surface normals for quantized gradient pairs,
    /// removing a square root and a divide from every dot.
    /// </summary>
    private static float[] BuildNormalLut(LiquidPrintLook look)
    {
        float[] lut = new float[NormalLutSize * NormalLutSize * NormalLutStride];

        for (int y = 0; y < NormalLutSize; y++)
        {
            float gradientY = (y - NormalLutBias) / NormalLutScale;

            for (int x = 0; x < NormalLutSize; x++)
            {
                float gradientX = (x - NormalLutBias) / NormalLutScale;
                float nx = -gradientX * look.SpecularExaggeration;
                float ny = -gradientY * look.SpecularExaggeration;
                float inverseLength = 1f / MathF.Sqrt((nx * nx) + (ny * ny) + 1f);

                int index = ((y * NormalLutSize) + x) * NormalLutStride;
                lut[index] = nx * inverseLength;
                lut[index + 1] = ny * inverseLength;
                lut[index + 2] = inverseLength;
            }
        }

        return lut;
    }

    /// <summary>
    /// How much structure surrounds a pixel, in 0..1. Flat sky and still water
    /// stay calm while detailed areas carry the motion, so the whole photograph
    /// never wobbles like a cheap filter.
    /// </summary>
    private static float SobelDetail(
        byte[] bgra,
        int stride,
        int width,
        int height,
        int x,
        int y)
    {
        int left = Math.Max(0, x - 1);
        int right = Math.Min(width - 1, x + 1);
        int top = Math.Max(0, y - 1);
        int bottom = Math.Min(height - 1, y + 1);

        float tl = Luma(bgra, (top * stride) + (left * 4));
        float tc = Luma(bgra, (top * stride) + (x * 4));
        float tr = Luma(bgra, (top * stride) + (right * 4));
        float ml = Luma(bgra, (y * stride) + (left * 4));
        float mr = Luma(bgra, (y * stride) + (right * 4));
        float bl = Luma(bgra, (bottom * stride) + (left * 4));
        float bc = Luma(bgra, (bottom * stride) + (x * 4));
        float br = Luma(bgra, (bottom * stride) + (right * 4));

        float gx = (tr + (2f * mr) + br) - (tl + (2f * ml) + bl);
        float gy = (bl + (2f * bc) + br) - (tl + (2f * tc) + tr);

        float magnitude = MathF.Sqrt((gx * gx) + (gy * gy)) * (1f / (4f * 255f));
        return MathF.Sqrt(Math.Clamp(magnitude, 0f, 1f));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Luma(byte[] bgra, int offset) =>
        ((0.2126f * bgra[offset + 2]) + (0.7152f * bgra[offset + 1]) + (0.0722f * bgra[offset])) * (1f / 255f);

    /// <summary>
    /// Renders the live halftone over the pre-cleared canvas target. Pure pixel
    /// math with no allocations; the wave field supplies one refraction sample
    /// per dot.
    /// </summary>
    public void Render(Span<uint> target, in ReactiveState state, WaveField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (target.Length < Width * Height)
        {
            throw new ArgumentException("The target buffer is smaller than the engine surface.", nameof(target));
        }

        int width = Width;
        int height = Height;
        float inverseWidth = 1f / width;
        float inverseHeight = 1f / height;

        LiquidPrintLook look = _look;
        float idle = state.Idle;
        float energy = state.Energy;
        float pulse = state.Pulse;

        float idleMotion = 1f - ((1f - look.IdleScale) * idle);
        float displacement = look.DisplacementPixels * idleMotion;
        float transientDisplacement = look.TransientDisplacementPixels * idleMotion;
        float curlPixels = look.CurlPixels * idleMotion * (0.45f + (0.55f * energy));
        float radiusGain = look.RadiusWaveGain * (0.7f + (0.3f * energy));
        float swell = 1f + (look.PulseSwell * pulse);
        float specularStrength = look.SpecularStrength * (0.25f + (0.75f * energy));
        float detailFloor = look.DetailFloor;

        // The sheen light wanders with the musical breath so highlights never sit still.
        float lightAngle = -2.2f + (0.55f * state.Breath);
        float lightX = MathF.Cos(lightAngle) * 0.72f;
        float lightY = MathF.Sin(lightAngle) * 0.72f;
        const float LightZ = 0.66f;

        ReadOnlySpan<HalftoneDotSpec> dots = _dots.AsSpan();
        float maxRadius = _maxRadius * 1.3f;
        float[] normals = _normalLut;
        ref uint targetReference = ref MemoryMarshal.GetReference(target);

        for (int i = 0; i < dots.Length; i++)
        {
            ref readonly HalftoneDotSpec dot = ref dots[i];
            float baseRadius = dot.BaseRadius;
            if (baseRadius <= 0.2f)
            {
                continue;
            }

            float u = dot.Cx * inverseWidth;
            float v = dot.Cy * inverseHeight;
            field.Sample(u, v, out float wave, out float gradientX, out float gradientY, out float curlX, out float curlY);

            float detail = detailFloor + ((1f - detailFloor) * dot.Detail);
            float amplitude = (displacement + (transientDisplacement * pulse)) * detail;

            float cx = dot.Cx - (gradientX * amplitude) - (curlX * curlPixels * detail);
            float cy = dot.Cy - (gradientY * amplitude) - (curlY * curlPixels * detail);

            float radius = baseRadius * (1f + (radiusGain * wave)) * swell;
            if (radius > maxRadius)
            {
                radius = maxRadius;
            }
            else if (radius < 0.3f)
            {
                continue;
            }

            uint red = dot.R;
            uint green = dot.G;
            uint blue = dot.B;

            // Surface normal from a precomputed LUT, so the hot loop never takes a
            // square root or a divide per dot.
            int lutX = (int)((gradientX + 1f) * NormalLutScale);
            if (lutX < 0)
            {
                lutX = 0;
            }
            else if (lutX >= NormalLutSize)
            {
                lutX = NormalLutSize - 1;
            }

            int lutY = (int)((gradientY + 1f) * NormalLutScale);
            if (lutY < 0)
            {
                lutY = 0;
            }
            else if (lutY >= NormalLutSize)
            {
                lutY = NormalLutSize - 1;
            }

            int normalIndex = ((lutY * NormalLutSize) + lutX) * NormalLutStride;
            float lambert = (normals[normalIndex] * lightX)
                + (normals[normalIndex + 1] * lightY)
                + (normals[normalIndex + 2] * LightZ);

            if (lambert > 0f)
            {
                float specular = lambert * lambert * lambert * lambert
                    * specularStrength
                    * Math.Clamp(wave * 2f, 0f, 1f);

                if (specular > 0.004f)
                {
                    red += (uint)((255 - red) * specular);
                    green += (uint)((255 - green) * specular);
                    blue += (uint)((255 - blue) * specular);
                }
            }

            // Bucket the radius and the sub-pixel phase, then replay that bucket's
            // precomputed footprint. One flat loop per dot keeps every iteration
            // independent, which is what the per-scanline square roots prevented.
            uint packed = 0xFF000000u | (red << 16) | (green << 8) | blue;
            int level = (int)(radius * RadiusLevelScale);
            if (level < 1)
            {
                level = 1;
            }
            else if (level > _maxRadiusLevel)
            {
                level = _maxRadiusLevel;
            }

            // Floor rather than a truncating cast so dots that drift past the top or
            // left edge keep their sub-pixel phase instead of flipping to the far side
            // of the cell.
            int wholeX = (int)MathF.Floor(cx);
            int wholeY = (int)MathF.Floor(cy);
            int phaseX = (int)((cx - wholeX) * StampPhases);
            int phaseY = (int)((cy - wholeY) * StampPhases);
            if ((uint)phaseX >= StampPhases)
            {
                phaseX = StampPhases - 1;
            }
            if ((uint)phaseY >= StampPhases)
            {
                phaseY = StampPhases - 1;
            }

            int stamp = StampIndex(level, phaseX, phaseY);
            int entryEnd = _stampStart[stamp + 1];
            int opaqueEnd = _stampOpaqueEnd[stamp];
            int entry = _stampStart[stamp];

            if (wholeX >= _stampMargin && wholeX < width - _stampMargin
                && wholeY >= _stampMargin && wholeY < height - _stampMargin)
            {
                int baseIndex = (wholeY * width) + wholeX;

                // Interior first: a fully covered pixel is a plain store, so the blend
                // below only ever runs on the antialiased rim of the dot.
                for (; entry < opaqueEnd; entry++)
                {
                    Unsafe.Add(ref targetReference, baseIndex + _entryOffset[entry]) = packed;
                }

                for (; entry < entryEnd; entry++)
                {
                    uint alpha = _entryAlpha[entry];
                    uint inverseAlpha = 256u - alpha;
                    int targetIndex = baseIndex + _entryOffset[entry];
                    uint currentPixel = Unsafe.Add(ref targetReference, targetIndex);

                    uint outRed = (((red * alpha) + (((currentPixel >> 16) & 0xFF) * inverseAlpha)) >> 8);
                    uint outGreen = (((green * alpha) + (((currentPixel >> 8) & 0xFF) * inverseAlpha)) >> 8);
                    uint outBlue = (((blue * alpha) + ((currentPixel & 0xFF) * inverseAlpha)) >> 8);

                    Unsafe.Add(ref targetReference, targetIndex) = 0xFF000000u | (outRed << 16) | (outGreen << 8) | outBlue;
                }

                continue;
            }

            // Edge dots are only about 2% of the lattice, so they can afford a bounds
            // test per pixel instead of the margin clamp that would otherwise stack
            // every border dot onto the same column.
            for (; entry < entryEnd; entry++)
            {
                int px = wholeX + _entryX[entry];
                int py = wholeY + _entryY[entry];
                if ((uint)px >= (uint)width || (uint)py >= (uint)height)
                {
                    continue;
                }

                int targetIndex = (py * width) + px;
                uint alpha = _entryAlpha[entry];
                if (alpha == 255)
                {
                    Unsafe.Add(ref targetReference, targetIndex) = packed;
                    continue;
                }

                uint inverseAlpha = 256u - alpha;
                uint currentPixel = Unsafe.Add(ref targetReference, targetIndex);

                uint edgeRed = (((red * alpha) + (((currentPixel >> 16) & 0xFF) * inverseAlpha)) >> 8);
                uint edgeGreen = (((green * alpha) + (((currentPixel >> 8) & 0xFF) * inverseAlpha)) >> 8);
                uint edgeBlue = (((blue * alpha) + ((currentPixel & 0xFF) * inverseAlpha)) >> 8);

                Unsafe.Add(ref targetReference, targetIndex) = 0xFF000000u | (edgeRed << 16) | (edgeGreen << 8) | edgeBlue;
            }
        }
    }
}
