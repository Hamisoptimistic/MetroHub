using System;
using MetroHub.Core.Services;
using Xunit;

namespace MetroHub.Tests;

public class WallpaperEffectRendererTests
{
    [Theory]
    [InlineData("Blur", true)]
    [InlineData("None", false)]
    [InlineData("Dither", false)]
    [InlineData("Ascii", false)]
    [InlineData("Halftone", false)]
    [InlineData("Honeycomb", false)]
    [InlineData("Scanlines", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSupportedCpuEffect_ReturnsExpectedResults(string? effect, bool expected)
    {
        bool actual = WallpaperEffectRenderer.IsSupportedCpuEffect(effect);
        Assert.Equal(expected, actual);
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
