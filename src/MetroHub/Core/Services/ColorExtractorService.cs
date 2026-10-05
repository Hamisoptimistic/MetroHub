using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MetroHub.Core.Services;

/// <summary>
/// High-performance icon accent color extractor.
/// Analyzes decoded icon bitmaps in &lt;1ms using raw BGRA buffer scanning,
/// clusters vibrant colors, and falls back gracefully for neutral/grayscale icons.
/// </summary>
public static class ColorExtractorService
{
    private struct ColorBucket
    {
        public double TotalScore;
        public long SumR;
        public long SumG;
        public long SumB;
        public int Count;
    }

    /// <summary>
    /// Extracts the dominant vibrant brand accent color from an icon file.
    /// Returns hex string formatted as #RRGGBB.
    /// </summary>
    public static string ExtractAccentColor(string? iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath))
        {
            return GetFallbackSystemAccentHex();
        }

        try
        {
            var bitmap = LoadDecodedBitmap(iconPath);
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);

            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            int stride = width * 4;
            int totalBytes = height * stride;
            byte[] pixels = ArrayPool<byte>.Shared.Rent(totalBytes);

            try
            {
                converted.CopyPixels(pixels, stride, 0);

                Span<ColorBucket> buckets = stackalloc ColorBucket[12];
                buckets.Clear();

                ProcessPixelBuffer(pixels.AsSpan(0, totalBytes), buckets);

                return GetDominantAccentHex(buckets) ?? GetFallbackSystemAccentHex();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pixels);
            }
        }
        catch (Exception)
        {
            return GetFallbackSystemAccentHex();
        }
    }

    private static BitmapImage LoadDecodedBitmap(string iconPath)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(iconPath, UriKind.Absolute);
        bitmap.DecodePixelWidth = 32;
        bitmap.DecodePixelHeight = 32;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache | BitmapCreateOptions.DelayCreation;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static void ProcessPixelBuffer(ReadOnlySpan<byte> pixels, Span<ColorBucket> buckets)
    {
        for (int i = 0; i <= pixels.Length - 4; i += 4)
        {
            byte b = pixels[i];
            byte g = pixels[i + 1];
            byte r = pixels[i + 2];
            byte a = pixels[i + 3];

            ProcessSinglePixel(r, g, b, a, buckets);
        }
    }

    private static void ProcessSinglePixel(byte r, byte g, byte b, byte a, Span<ColorBucket> buckets)
    {
        // Ignore transparent background
        if (a < 128) return;

        int max = Math.Max(r, Math.Max(g, b));
        int min = Math.Min(r, Math.Min(g, b));
        int delta = max - min;

        float saturation = max == 0 ? 0 : (float)delta / max;
        float value = max / 255f;

        // Filter out pure black, light desaturated highlights, and neutral grays
        if (value < 0.15f || (value > 0.95f && saturation < 0.12f) || saturation < 0.20f)
        {
            return;
        }

        float hue = CalculateHue(r, g, b, max, delta);
        int bucketIndex = Math.Clamp((int)(hue / 30f), 0, 11);

        // Score: prioritize high saturation and balanced luminance
        double luminanceMultiplier = (value >= 0.30f && value <= 0.88f) ? 1.3 : 0.85;
        double score = (saturation * 2.2) * luminanceMultiplier;

        ref var bucket = ref buckets[bucketIndex];
        bucket.TotalScore += score;
        bucket.SumR += r;
        bucket.SumG += g;
        bucket.SumB += b;
        bucket.Count++;
    }

    private static float CalculateHue(byte r, byte g, byte b, int max, int delta)
    {
        if (delta <= 0) return 0f;

        float hue;
        if (max == r)
        {
            hue = ((g - b) / (float)delta) % 6f;
        }
        else if (max == g)
        {
            hue = ((b - r) / (float)delta) + 2f;
        }
        else
        {
            hue = ((r - g) / (float)delta) + 4f;
        }

        hue *= 60f;
        if (hue < 0f) hue += 360f;
        return hue;
    }

    private static string? GetDominantAccentHex(ReadOnlySpan<ColorBucket> buckets)
    {
        int bestBucketIndex = -1;
        double maxScore = 0;

        for (int i = 0; i < buckets.Length; i++)
        {
            if (buckets[i].TotalScore > maxScore && buckets[i].Count >= 2)
            {
                maxScore = buckets[i].TotalScore;
                bestBucketIndex = i;
            }
        }

        if (bestBucketIndex >= 0 && buckets[bestBucketIndex].Count > 0)
        {
            ref readonly var bestBucket = ref buckets[bestBucketIndex];
            byte avgR = (byte)Math.Clamp(bestBucket.SumR / bestBucket.Count, 0, 255);
            byte avgG = (byte)Math.Clamp(bestBucket.SumG / bestBucket.Count, 0, 255);
            byte avgB = (byte)Math.Clamp(bestBucket.SumB / bestBucket.Count, 0, 255);

            return $"#{avgR:X2}{avgG:X2}{avgB:X2}";
        }

        return null;
    }

    /// <summary>
    /// Returns Windows system accent color as #RRGGBB, dynamically sourced from SystemAccentColorService.
    /// </summary>
    public static string GetFallbackSystemAccentHex()
    {
        return SystemAccentColorService.CurrentAccentHex;
    }

    /// <summary>
    /// Parses a hex color string into a Color with ~22% alpha for soft tinted glass.
    /// Default alpha: 0x38 (~22%).
    /// </summary>
    public static Color GetTintedColor(string? hexColor, byte alpha = 0x38)
    {
        if (!string.IsNullOrWhiteSpace(hexColor))
        {
            try
            {
                string cleanHex = hexColor.TrimStart('#');
                if (cleanHex.Length == 6)
                {
                    byte r = Convert.ToByte(cleanHex.Substring(0, 2), 16);
                    byte g = Convert.ToByte(cleanHex.Substring(2, 2), 16);
                    byte b = Convert.ToByte(cleanHex.Substring(4, 2), 16);
                    return Color.FromArgb(alpha, r, g, b);
                }
                else if (cleanHex.Length == 8)
                {
                    byte r = Convert.ToByte(cleanHex.Substring(2, 2), 16);
                    byte g = Convert.ToByte(cleanHex.Substring(4, 2), 16);
                    byte b = Convert.ToByte(cleanHex.Substring(6, 2), 16);
                    return Color.FromArgb(alpha, r, g, b);
                }
            }
            catch (Exception)
            {
                // Ignored: Fall through to neutral glass fallback for invalid hex formats
            }
        }

        // Default neutral glass fallback if invalid
        return Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF);
    }

    /// <summary>
    /// Default transparent acrylic glass background: #1CFFFFFF (11% white).
    /// </summary>
    public static Color GetDefaultGlassColor() => Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF);
}
