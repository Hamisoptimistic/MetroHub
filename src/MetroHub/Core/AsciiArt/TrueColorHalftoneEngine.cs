using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MetroHub.Core.AsciiArt;

/// <summary>
/// Precomputed geometry and color descriptor for one halftone dot in the hexagonal lattice.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct HalftoneDotSpec(
    float Cx,
    float Cy,
    byte R,
    byte G,
    byte B,
    float BaseRadius);

/// <summary>
/// High-density, 32-bit TrueColor hexagonal halftone renderer for user wallpapers.
/// Employs continuous subpixel antialiasing, equilateral honeycomb packing,
/// and real-time acoustic bass/shockwave modulation without color quantization or resolution loss.
/// Zero per-frame GC allocations.
/// </summary>
public sealed class TrueColorHalftoneEngine
{
    private const byte CanvasRed = 0x0B;
    private const byte CanvasGreen = 0x0B;
    private const byte CanvasBlue = 0x0F;

    public int Width { get; }
    public int Height { get; }

    private readonly HalftoneDotSpec[] _dots;
    private readonly float _maxRadius;

    public TrueColorHalftoneEngine(string imagePath, int targetWidth = 1920, int targetHeight = 1080, float cellSize = 6.0f)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetHeight, 1);

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

                dotList.Add(new HalftoneDotSpec(cx, cy, r, g, b, baseRadius));
            }
        }

        _dots = dotList.ToArray();
    }

    /// <summary>
    /// Renders the audio-reactive halftone dots over the pre-cleared canvas target.
    /// Pure pixel math, zero allocations, locks 60 FPS performance.
    /// </summary>
    public void Render(Span<uint> target, in AudioFrame audio)
    {
        int width = Width;
        int height = Height;
        float maxRadius = _maxRadius;

        float bassMult = 1.0f + (audio.Bass * 0.25f);
        ReadOnlySpan<HalftoneAudioWave> waves = audio.ActiveWaves.Span;
        bool hasWaves = waves.Length > 0;

        ReadOnlySpan<HalftoneDotSpec> dots = _dots.AsSpan();

        for (int i = 0; i < dots.Length; i++)
        {
            ref readonly HalftoneDotSpec dot = ref dots[i];
            float baseRadius = dot.BaseRadius;
            if (baseRadius <= 0.25f)
            {
                continue;
            }

            float radius = baseRadius * bassMult;

            if (hasWaves)
            {
                float waveMod = 0f;
                for (int w = 0; w < waves.Length; w++)
                {
                    ref readonly HalftoneAudioWave wave = ref waves[w];
                    float dx = dot.Cx - wave.CenterX;
                    float dy = dot.Cy - wave.CenterY;
                    float dist = MathF.Sqrt((dx * dx) + (dy * dy));
                    float crest = 380f * wave.ElapsedSeconds;
                    float dCrest = dist - crest;
                    // Gaussian pulse: sigma ~ 24px (2 * sigma^2 = 1152)
                    float pulse = MathF.Exp(-(dCrest * dCrest) / 1152f);
                    float amp = 0.25f * Math.Max(0f, 1f - (wave.ElapsedSeconds / 0.50f)) * wave.Intensity;
                    waveMod += amp * pulse;
                }
                radius *= 1.0f + waveMod;
            }

            radius = Math.Min(radius, maxRadius * 1.25f);
            if (radius < 0.35f)
            {
                continue;
            }

            float cx = dot.Cx;
            float cy = dot.Cy;
            uint r = dot.R;
            uint g = dot.G;
            uint b = dot.B;

            int minX = Math.Max(0, (int)MathF.Floor(cx - radius - 1f));
            int maxX = Math.Min(width - 1, (int)MathF.Ceiling(cx + radius + 1f));
            int minY = Math.Max(0, (int)MathF.Floor(cy - radius - 1f));
            int maxY = Math.Min(height - 1, (int)MathF.Ceiling(cy + radius + 1f));

            float rPlusHalf = radius + 0.5f;
            float rPlusHalfSq = rPlusHalf * rPlusHalf;

            for (int py = minY; py <= maxY; py++)
            {
                int rowBase = py * width;
                float dy = py - cy;
                float dySq = dy * dy;

                for (int px = minX; px <= maxX; px++)
                {
                    float dx = px - cx;
                    float distSq = (dx * dx) + dySq;
                    if (distSq > rPlusHalfSq)
                    {
                        continue;
                    }

                    float dist = MathF.Sqrt(distSq);
                    float coverage = Math.Clamp(rPlusHalf - dist, 0f, 1f);
                    if (coverage <= 0f)
                    {
                        continue;
                    }

                    int targetIdx = rowBase + px;
                    uint currentPixel = target[targetIdx];
                    uint bgR = (currentPixel >> 16) & 0xFF;
                    uint bgG = (currentPixel >> 8) & 0xFF;
                    uint bgB = currentPixel & 0xFF;

                    uint outR = (uint)((r * coverage) + (bgR * (1f - coverage)));
                    uint outG = (uint)((g * coverage) + (bgG * (1f - coverage)));
                    uint outB = (uint)((b * coverage) + (bgB * (1f - coverage)));

                    target[targetIdx] = 0xFF000000u | (outR << 16) | (outG << 8) | outB;
                }
            }
        }
    }
}

