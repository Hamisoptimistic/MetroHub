using System;
using MetroHub.Core.Services;
using Xunit;

namespace MetroHub.Tests;

public class WallpaperEffectRendererTests
{
    [Theory]
    [InlineData("Dither", true)]
    [InlineData("Ascii", true)]
    [InlineData("Halftone", true)]
    [InlineData("Scanlines", true)]
    [InlineData("Blur", true)]
    [InlineData("None", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSupportedCpuEffect_ReturnsExpectedResults(string? effect, bool expected)
    {
        bool actual = WallpaperEffectRenderer.IsSupportedCpuEffect(effect);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildLuma_ComputesAccurateRec709Luminance()
    {
        // 2x2 image, BGRA32 format (B, G, R, A)
        int w = 2, h = 2;
        byte[] bgra =
        [
            // Pixel 0,0: Pure White
            255, 255, 255, 255,
            // Pixel 1,0: Pure Black
            0, 0, 0, 255,
            // Pixel 0,1: Pure Red
            0, 0, 255, 255,
            // Pixel 1,1: Pure Green
            0, 255, 0, 255,
        ];

        byte[] luma = WallpaperEffectRenderer.BuildLuma(bgra, w, h);

        Assert.Equal(4, luma.Length);
        Assert.Equal(255, luma[0]); // White
        Assert.Equal(0, luma[1]);   // Black
        Assert.Equal((byte)(0.2126f * 255), luma[2]); // Red
        Assert.Equal((byte)(0.7152f * 255), luma[3]); // Green
    }

    [Fact]
    public void ApplyScanlines_DarkensEveryThirdRowInDarkTheme()
    {
        int w = 2, h = 4;
        byte[] src = new byte[w * h * 4];
        // Fill all pixels with gray (100, 100, 100, 255)
        for (int i = 0; i < src.Length; i += 4)
        {
            src[i] = 100;     // B
            src[i + 1] = 100; // G
            src[i + 2] = 100; // R
            src[i + 3] = 255; // A
        }

        byte[] dst = new byte[src.Length];
        WallpaperEffectRenderer.ApplyScanlines(src, dst, w, h, light: false);

        // Row 0 (y = 0 % 3 == 0): darkened to 100 * 0.52 = 52
        Assert.Equal(52, dst[0]);
        Assert.Equal(52, dst[1]);
        Assert.Equal(52, dst[2]);
        Assert.Equal(255, dst[3]);

        // Row 1 (y = 1 % 3 != 0): untouched
        int row1 = 1 * w * 4;
        Assert.Equal(100, dst[row1]);
        Assert.Equal(100, dst[row1 + 1]);
        Assert.Equal(100, dst[row1 + 2]);
        Assert.Equal(255, dst[row1 + 3]);

        // Row 2 (y = 2 % 3 != 0): untouched
        int row2 = 2 * w * 4;
        Assert.Equal(100, dst[row2]);

        // Row 3 (y = 3 % 3 == 0): darkened to 52
        int row3 = 3 * w * 4;
        Assert.Equal(52, dst[row3]);
    }

    [Fact]
    public void ApplyScanlines_LiftsEveryThirdRowInLightTheme()
    {
        int w = 2, h = 3;
        byte[] src = new byte[w * h * 4];
        for (int i = 0; i < src.Length; i += 4)
        {
            src[i] = 100;
            src[i + 1] = 100;
            src[i + 2] = 100;
            src[i + 3] = 255;
        }

        byte[] dst = new byte[src.Length];
        WallpaperEffectRenderer.ApplyScanlines(src, dst, w, h, light: true);

        // Row 0: lifted: 100 + (255 - 100) * 0.48 = 100 + 74.4 = 174
        Assert.True(dst[0] > 170 && dst[0] < 180);
        Assert.Equal(255, dst[3]); // Alpha preserved
    }

    [Fact]
    public void ApplyDither_PreservesDimensionsAndAlpha()
    {
        int w = 8, h = 8;
        byte[] src = new byte[w * h * 4];
        for (int i = 0; i < src.Length; i += 4)
        {
            src[i] = (byte)(i % 256);
            src[i + 1] = (byte)((i * 2) % 256);
            src[i + 2] = (byte)((i * 3) % 256);
            src[i + 3] = 200; // Alpha
        }

        byte[] dst = new byte[src.Length];
        WallpaperEffectRenderer.ApplyDither(src, dst, w, h);

        Assert.Equal(src.Length, dst.Length);
        for (int i = 0; i < dst.Length; i += 4)
        {
            Assert.Equal(200, dst[i + 3]); // Alpha must remain exactly preserved
        }
    }

    [Fact]
    public void ApplyAscii_PreservesDimensionsAndAlpha()
    {
        int w = 12, h = 16;
        byte[] src = new byte[w * h * 4];
        for (int i = 0; i < src.Length; i += 4)
        {
            src[i] = 128;
            src[i + 1] = 64;
            src[i + 2] = 192;
            src[i + 3] = 255;
        }

        byte[] luma = WallpaperEffectRenderer.BuildLuma(src, w, h);
        byte[] dst = new byte[src.Length];
        WallpaperEffectRenderer.ApplyAscii(src, luma, dst, w, h, light: false);

        Assert.Equal(src.Length, dst.Length);
        for (int i = 0; i < dst.Length; i += 4)
        {
            Assert.Equal(255, dst[i + 3]);
        }
    }

    [Fact]
    public void ApplyHalftone_PreservesDimensionsAndAlpha()
    {
        int w = 8, h = 8;
        byte[] src = new byte[w * h * 4];
        for (int i = 0; i < src.Length; i += 4)
        {
            src[i] = 80;
            src[i + 1] = 120;
            src[i + 2] = 200;
            src[i + 3] = 240;
        }

        byte[] luma = WallpaperEffectRenderer.BuildLuma(src, w, h);
        byte[] dst = new byte[src.Length];
        WallpaperEffectRenderer.ApplyHalftone(src, luma, dst, w, h, light: false);

        Assert.Equal(src.Length, dst.Length);
        for (int i = 0; i < dst.Length; i += 4)
        {
            Assert.Equal(240, dst[i + 3]);
        }
    }

    [Fact]
    public void ApplyBlur_BlursImageAccurately()
    {
        int w = 32, h = 32;
        byte[] src = new byte[w * h * 4];
        byte[] dst = new byte[w * h * 4];

        // Fill left half (x < 16) with solid white (255)
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (x < 16)
                {
                    int idx = ((y * w) + x) * 4;
                    src[idx] = 255;
                    src[idx + 1] = 255;
                    src[idx + 2] = 255;
                    src[idx + 3] = 255;
                }
            }
        }

        WallpaperEffectRenderer.ApplyBlur(src, dst, w, h);

        // After blur, the step edge (x=15, 16) should smoothly transition
        int leftOfEdge = ((16 * w) + 15) * 4;
        int rightOfEdge = ((16 * w) + 16) * 4;

        Assert.True(dst[rightOfEdge] > 0, "Right of edge should receive diffused light");
        Assert.True(dst[leftOfEdge] < 255, "Left of edge should have diffused outward");
        Assert.True(dst[leftOfEdge] > dst[rightOfEdge], "Diffusion gradient should decrease across boundary");
    }
}

