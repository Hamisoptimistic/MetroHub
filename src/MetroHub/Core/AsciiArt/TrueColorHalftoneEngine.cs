using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MetroHub.Core.AsciiArt;

/// <summary>
/// Precomputed geometry, color, and acoustic susceptibility for one halftone dot in the static hexagonal lattice.
/// Centers (Cx, Cy) are permanently locked to their static lattice coordinates.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct HalftoneDotSpec(
    float Cx,
    float Cy,
    byte R,
    byte G,
    byte B,
    float BaseRadius,
    float EffSusceptibility);

/// <summary>
/// Packed 8.8 fixed-point channel multipliers for the quantized 64-slice Z-State x 64-radial distance LUT.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
public readonly struct FerroLutEntry
{
    public readonly ushort R;
    public readonly ushort G;
    public readonly ushort B;

    public FerroLutEntry(ushort r, ushort g, ushort b)
    {
        R = r;
        G = g;
        B = b;
    }
}

/// <summary>
/// High-density, 32-bit TrueColor hexagonal halftone renderer for user wallpapers.
/// Implements the Ferro-Acoustic Toroidal Press &amp; Duffing Thump physical model.
/// Dot centers remain permanently fixed. All per-pixel math uses a pre-baked
/// 64x64 fixed-point LUT for zero square roots and zero allocations per frame.
/// </summary>
public sealed class TrueColorHalftoneEngine
{
    private const int ZSlices = 64;
    private const int UBins = 64;
    private const float MinZ = -0.28f;
    private const float MaxZ = 1.0f;
    private const float ZRange = MaxZ - MinZ;

    private readonly HalftoneDotSpec[] _dots;
    private readonly FerroLutEntry[] _ferroLut;
    private readonly float _maxRadius;

    public int Width { get; }
    public int Height { get; }

    public TrueColorHalftoneEngine(
        string imagePath,
        int targetWidth = 1920,
        int targetHeight = 1080,
        float cellSize = 6.0f)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetHeight, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cellSize, 32f);
        ArgumentOutOfRangeException.ThrowIfLessThan(cellSize, 1f);

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

        float halfWidth = targetWidth * 0.5f;
        float halfHeight = targetHeight * 0.5f;
        float invHalfW = 1.0f / halfWidth;
        float invHalfH = 1.0f / halfHeight;

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

                float rf = r * (1.0f / 255.0f);
                float gf = g * (1.0f / 255.0f);
                float bf = b * (1.0f / 255.0f);

                // Perceptual luminance Li in [0, 1]
                float luma = (0.2126f * rf) + (0.7152f * gf) + (0.0722f * bf);

                // Perceptual tone curve for resting dot size
                float baseRadius = _maxRadius * MathF.Pow(luma, 0.70f);

                // Color saturation Si in [0, 1]
                float maxC = Math.Max(rf, Math.Max(gf, bf));
                float minC = Math.Min(rf, Math.Min(gf, bf));
                float deltaC = maxC - minC;
                float sat = maxC > 0.0001f ? deltaC / maxC : 0f;

                // Acoustic susceptibility: chi_i = Li^1.6 * (0.4 + 0.6 * Si)
                float chi = MathF.Pow(luma, 1.6f) * (0.4f + (0.6f * sat));

                // Radial weight: W_radial(x0, y0) = 0.65 + 0.35 * cos(distFromCenter * PI / 2)
                float normX = (cx - halfWidth) * invHalfW;
                float normY = (cy - halfHeight) * invHalfH;
                float distCenter = Math.Clamp(MathF.Sqrt((normX * normX) + (normY * normY)), 0f, 1f);
                float wRadial = 0.65f + (0.35f * MathF.Cos(distCenter * (MathF.PI * 0.5f)));

                float effSusceptibility = chi * wRadial;

                dotList.Add(new HalftoneDotSpec(cx, cy, r, g, b, baseRadius, effSusceptibility));
            }
        }

        _dots = dotList.ToArray();
        _ferroLut = BuildFerroLut();
    }

    /// <summary>
    /// Pre-computes the quantized 64-slice Z-State x 64-radial distance LUT.
    /// Packs Q8 8.8 fixed-point multipliers into 24 KB, completely fitting into L1 cache.
    /// </summary>
    private static FerroLutEntry[] BuildFerroLut()
    {
        FerroLutEntry[] lut = new FerroLutEntry[ZSlices * UBins];

        for (int s = 0; s < ZSlices; s++)
        {
            // zLocal ranges from MinZ (-0.28) to MaxZ (+1.0)
            float zLocal = MinZ + (ZRange * s / (ZSlices - 1f));

            for (int ui = 0; ui < UBins; ui++)
            {
                float u = ui / (float)(UBins - 1); // 0.0 (center) to 1.0 (outer rim)

                // Antialiasing edge fade in the outer 15% of the radius
                float edgeAlpha = u > 0.85f ? Math.Clamp((1.0f - u) / 0.15f, 0f, 1f) : 1f;

                float rMult;
                float gMult;
                float bMult;

                if (zLocal > 0.02f)
                {
                    // 1. Inward press: Toroidal Cavitation & Radial Chromatic Dispersion
                    float cavityDepth = 0.72f * zLocal;
                    float rimBoost = 1.0f + (0.65f * zLocal);
                    float oneMinusU = 1.0f - u;
                    float ringProfile = (1.0f - (cavityDepth * oneMinusU * oneMinusU)) * rimBoost;

                    float dispersion = zLocal * 0.18f * (u - 0.5f);
                    rMult = ringProfile * (1.0f + dispersion) * edgeAlpha;
                    gMult = ringProfile * edgeAlpha;
                    bMult = ringProfile * (1.0f - dispersion) * edgeAlpha;
                }
                else if (zLocal < -0.001f)
                {
                    // 2. Elastic Convex Thump on Rebound
                    float bloomBoost = 1.0f + (-zLocal * 0.45f * (1.0f - (u * 0.5f)));
                    rMult = bloomBoost * edgeAlpha;
                    gMult = bloomBoost * edgeAlpha;
                    bMult = bloomBoost * edgeAlpha;
                }
                else
                {
                    // 3. Resting flat membrane
                    rMult = edgeAlpha;
                    gMult = edgeAlpha;
                    bMult = edgeAlpha;
                }

                // Convert to 8.8 fixed-point format (1.0 = 256)
                ushort rQ8 = (ushort)Math.Clamp((int)MathF.Round(rMult * 256f), 0, 65535);
                ushort gQ8 = (ushort)Math.Clamp((int)MathF.Round(gMult * 256f), 0, 65535);
                ushort bQ8 = (ushort)Math.Clamp((int)MathF.Round(bMult * 256f), 0, 65535);

                lut[(s * UBins) + ui] = new FerroLutEntry(rQ8, gQ8, bQ8);
            }
        }

        return lut;
    }

    /// <summary>
    /// Renders the ferro-acoustic halftone dots over the pre-cleared canvas target.
    /// Pure integer scanline math with zero square roots and zero allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Render(Span<uint> target, float zPiston)
    {
        if (target.Length < Width * Height)
        {
            throw new ArgumentException("The target buffer is smaller than the engine surface.", nameof(target));
        }

        int width = Width;
        int height = Height;
        zPiston = Math.Clamp(zPiston, MinZ, MaxZ);

        ReadOnlySpan<HalftoneDotSpec> dots = _dots.AsSpan();
        ReadOnlySpan<FerroLutEntry> lut = _ferroLut.AsSpan();

        const float sliceScale = (ZSlices - 1) / ZRange;

        for (int i = 0; i < dots.Length; i++)
        {
            ref readonly HalftoneDotSpec dot = ref dots[i];
            float baseRadius = dot.BaseRadius;
            if (baseRadius <= 0.25f)
            {
                continue;
            }

            float zLocal = zPiston * dot.EffSusceptibility;

            // Radial contraction on press / bloom on rebound:
            float radiusMult = 1.0f - (0.28f * zLocal);
            float rEff = baseRadius * radiusMult;
            if (rEff < 0.3f)
            {
                continue;
            }

            // Quantize zLocal to slice index [0, 63]
            int slice = Math.Clamp((int)MathF.Round((zLocal - MinZ) * sliceScale), 0, ZSlices - 1);
            int sliceBase = slice << 6;

            float rEffSq = rEff * rEff;
            float invREffSq = 1.0f / rEffSq;

            float cx = dot.Cx;
            float cy = dot.Cy;

            int minX = Math.Max(0, (int)MathF.Floor(cx - rEff));
            int maxX = Math.Min(width - 1, (int)MathF.Ceiling(cx + rEff));
            int minY = Math.Max(0, (int)MathF.Floor(cy - rEff));
            int maxY = Math.Min(height - 1, (int)MathF.Ceiling(cy + rEff));

            uint dotR = dot.R;
            uint dotG = dot.G;
            uint dotB = dot.B;

            for (int py = minY; py <= maxY; py++)
            {
                int rowOffset = py * width;
                float dy = py - cy;
                float dySq = dy * dy;
                if (dySq > rEffSq)
                {
                    continue;
                }

                for (int px = minX; px <= maxX; px++)
                {
                    float dx = px - cx;
                    float rSq = (dx * dx) + dySq;
                    if (rSq > rEffSq)
                    {
                        continue;
                    }

                    float u = rSq * invREffSq;
                    int uIdx = Math.Clamp((int)(u * 63f), 0, 63);

                    ref readonly FerroLutEntry lutEntry = ref lut[sliceBase + uIdx];

                    uint r = (dotR * lutEntry.R) >> 8;
                    uint g = (dotG * lutEntry.G) >> 8;
                    uint b = (dotB * lutEntry.B) >> 8;

                    if (r > 255) r = 255;
                    if (g > 255) g = 255;
                    if (b > 255) b = 255;

                    target[rowOffset + px] = 0xFF000000u | (r << 16) | (g << 8) | b;
                }
            }
        }
    }
}
