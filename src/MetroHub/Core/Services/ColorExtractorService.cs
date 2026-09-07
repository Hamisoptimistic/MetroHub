using System;
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
    private class ColorBucket
    {
        public double TotalScore { get; set; }
        public long SumR { get; set; }
        public long SumG { get; set; }
        public long SumB { get; set; }
        public int Count { get; set; }
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
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(iconPath, UriKind.Absolute);
            bitmap.DecodePixelWidth = 32;
            bitmap.DecodePixelHeight = 32;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache | BitmapCreateOptions.DelayCreation;
            bitmap.EndInit();
            bitmap.Freeze();

            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            int stride = width * 4;
            byte[] pixels = new byte[height * stride];
            converted.CopyPixels(pixels, stride, 0);

            var buckets = new ColorBucket[12];
            for (int i = 0; i < 12; i++)
            {
                buckets[i] = new ColorBucket();
            }

            int validColorCount = 0;

            for (int i = 0; i <= pixels.Length - 4; i += 4)
            {
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];
                byte a = pixels[i + 3];

                // Ignore transparent background
                if (a < 128) continue;

                int max = Math.Max(r, Math.Max(g, b));
                int min = Math.Min(r, Math.Min(g, b));
                int delta = max - min;

                float saturation = max == 0 ? 0 : (float)delta / max;
                float value = max / 255f;

                // Filter out pure black / very dark shadows
                if (value < 0.15f) continue;

                // Filter out pure white or light desaturated highlights
                if (value > 0.95f && saturation < 0.12f) continue;

                // Filter out neutral grays (low saturation)
                if (saturation < 0.20f) continue;

                float hue = 0f;
                if (delta > 0)
                {
                    if (max == r) hue = ((g - b) / (float)delta) % 6f;
                    else if (max == g) hue = ((b - r) / (float)delta) + 2f;
                    else hue = ((r - g) / (float)delta) + 4f;
                    hue *= 60f;
                    if (hue < 0f) hue += 360f;
                }

                int bucketIndex = Math.Clamp((int)(hue / 30f), 0, 11);

                // Score: prioritize high saturation and balanced luminance
                double score = (saturation * 2.2) * (value >= 0.30f && value <= 0.88f ? 1.3 : 0.85);

                var bucket = buckets[bucketIndex];
                bucket.TotalScore += score;
                bucket.SumR += r;
                bucket.SumG += g;
                bucket.SumB += b;
                bucket.Count++;
                validColorCount++;
            }

            // Find bucket with highest accumulated vibrancy
            ColorBucket? bestBucket = null;
            double maxScore = 0;
            for (int i = 0; i < 12; i++)
            {
                if (buckets[i].TotalScore > maxScore && buckets[i].Count >= 2)
                {
                    maxScore = buckets[i].TotalScore;
                    bestBucket = buckets[i];
                }
            }

            if (bestBucket != null && bestBucket.Count > 0)
            {
                byte avgR = (byte)Math.Clamp(bestBucket.SumR / bestBucket.Count, 0, 255);
                byte avgG = (byte)Math.Clamp(bestBucket.SumG / bestBucket.Count, 0, 255);
                byte avgB = (byte)Math.Clamp(bestBucket.SumB / bestBucket.Count, 0, 255);

                return $"#{avgR:X2}{avgG:X2}{avgB:X2}";
            }

            // Grayscale / monochromatic icon fallback
            return GetFallbackSystemAccentHex();
        }
        catch
        {
            return GetFallbackSystemAccentHex();
        }
    }

    /// <summary>
    /// Returns Windows system accent color as #RRGGBB, or Fluent Blue (#60CDFF) fallback.
    /// </summary>
    public static string GetFallbackSystemAccentHex()
    {
        try
        {
            Color glass = SystemParameters.WindowGlassColor;
            if (glass.A > 0 && (glass.R > 20 || glass.G > 20 || glass.B > 20))
            {
                return $"#{glass.R:X2}{glass.G:X2}{glass.B:X2}";
            }
        }
        catch { }

        return "#60CDFF";
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
            catch { }
        }

        // Default neutral glass fallback if invalid
        return Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF);
    }

    /// <summary>
    /// Default transparent acrylic glass background: #1CFFFFFF (11% white).
    /// </summary>
    public static Color GetDefaultGlassColor() => Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF);
}
