// Treatment math ported from Zeron (crates/ui/src/new_thread_background_effects.rs,
// v0.2.113), MIT licensed. https://github.com/zeronsh/zeron

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MetroHub.Core.Services;

/// <summary>
/// High-performance CPU pixel processing engine for retro and ambient wallpaper treatments
/// (Dither, ASCII Art, Halftone Print, and Scanlines), ported from Zeron desktop.
/// All pixel processing executes off the UI thread via Parallel.For with zero-allocation buffers
/// and atomic disk caching.
/// </summary>
public static class WallpaperEffectRenderer
{
    private static readonly int[] Bayer =
    [
        0, 8, 2, 10,
        12, 4, 14, 6,
        3, 11, 1, 9,
        15, 7, 13, 5,
    ];

    private static readonly byte[][] Glyphs =
    [
        [0, 0, 0, 0, 0, 0, 0],         // index 0: ' '
        [0, 0, 0, 0, 0, 4, 0],         // index 1: '.'
        [0, 4, 0, 0, 4, 0, 0],         // index 2: ':'
        [0, 0, 0, 14, 0, 0, 0],        // index 3: '-'
        [0, 0, 14, 0, 14, 0, 0],       // index 4: '='
        [0, 4, 4, 31, 4, 4, 0],        // index 5: '+'
        [0, 21, 14, 31, 14, 21, 0],    // index 6: '*'
        [10, 10, 31, 10, 31, 10, 10],  // index 7: '#'
        [17, 2, 4, 4, 8, 16, 17],      // index 8: '/'
        [14, 17, 23, 21, 23, 16, 14],  // index 9: '@'
    ];

    private const int MaxProxyDimension = 2048;
    private const int MaxCacheFiles = 16;
    private static readonly object CacheLock = new();

    /// <summary>
    /// Checks if the effect name is one of the supported CPU pixel treatments.
    /// </summary>
    public static bool IsSupportedCpuEffect(string? effect) =>
        effect switch
        {
            "Dither" or "Ascii" or "Halftone" or "Scanlines" => true,
            _ => false
        };

    /// <summary>
    /// Asynchronously retrieves the cached effect image or renders it on a background thread.
    /// Returns a frozen, thread-safe BitmapSource ready for direct WPF rendering.
    /// </summary>
    public static async Task<BitmapSource?> GetOrRenderEffectAsync(
        string sourceImagePath,
        string effect,
        bool isLightTheme,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceImagePath) || !File.Exists(sourceImagePath))
        {
            return null;
        }

        if (!IsSupportedCpuEffect(effect))
        {
            return null;
        }

        string cacheDir = AppPaths.WallpaperEffectsCacheDir;
        AppPaths.EnsureDirectory(cacheDir);

        string cacheKey = ComputeCacheKey(sourceImagePath, effect, isLightTheme);
        string cacheFilePath = Path.Combine(cacheDir, $"{cacheKey}.png");

        // 1. Instant Cache Hit (Skip all CPU processing)
        if (File.Exists(cacheFilePath))
        {
            try
            {
                var cached = LoadFrozenBitmap(cacheFilePath);
                if (cached != null)
                {
                    return cached;
                }
            }
            catch
            {
                // If corrupt, fall through to regenerate
                try { File.Delete(cacheFilePath); } catch { }
            }
        }

        // 2. Off-thread generation
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            var proxyBitmap = LoadProxyBitmap(sourceImagePath);
            if (proxyBitmap == null) return null;

            int w = proxyBitmap.PixelWidth;
            int h = proxyBitmap.PixelHeight;
            int stride = w * 4;
            int byteCount = h * stride;

            byte[] src = new byte[byteCount];
            proxyBitmap.CopyPixels(src, stride, 0);

            byte[] dst = new byte[byteCount];
            var parallelOptions = new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Environment.ProcessorCount
            };

            switch (effect)
            {
                case "Scanlines":
                    ApplyScanlines(src, dst, w, h, isLightTheme, parallelOptions);
                    break;

                case "Dither":
                    ApplyDither(src, dst, w, h, parallelOptions);
                    break;

                case "Ascii":
                    byte[] lumaAscii = BuildLuma(src, w, h, parallelOptions);
                    ct.ThrowIfCancellationRequested();
                    ApplyAscii(src, lumaAscii, dst, w, h, isLightTheme, parallelOptions);
                    break;

                case "Halftone":
                    byte[] lumaHalftone = BuildLuma(src, w, h, parallelOptions);
                    ct.ThrowIfCancellationRequested();
                    ApplyHalftone(src, lumaHalftone, dst, w, h, isLightTheme, parallelOptions);
                    break;

                default:
                    return null;
            }

            ct.ThrowIfCancellationRequested();

            var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, dst, stride);
            result.Freeze();

            // Asynchronously save to cache on disk (best effort, non-blocking)
            SaveToDiskCacheSafe(result, cacheFilePath);

            return result;
        }, ct).ConfigureAwait(false);
    }

    private static string ComputeCacheKey(string sourcePath, string effect, bool isLightTheme)
    {
        string name = Path.GetFileNameWithoutExtension(sourcePath);
        long length = 0;
        long ticks = 0;

        try
        {
            var fi = new FileInfo(sourcePath);
            length = fi.Length;
            ticks = fi.LastWriteTimeUtc.Ticks;
        }
        catch { }

        // Dither is theme-independent per Zeron specification
        string themeTag = string.Equals(effect, "Dither", StringComparison.OrdinalIgnoreCase)
            ? "shared"
            : (isLightTheme ? "light" : "dark");

        return $"{name}_{length:X}_{ticks:X}_{effect.ToLowerInvariant()}_{themeTag}";
    }

    private static BitmapImage? LoadFrozenBitmap(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static BitmapSource? LoadProxyBitmap(string path)
    {
        try
        {
            int origWidth = 0;
            int origHeight = 0;

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var frame = BitmapFrame.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                origWidth = frame.PixelWidth;
                origHeight = frame.PixelHeight;
            }

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;

            if (origWidth > MaxProxyDimension || origHeight > MaxProxyDimension)
            {
                if (origWidth >= origHeight)
                {
                    bmp.DecodePixelWidth = MaxProxyDimension;
                }
                else
                {
                    bmp.DecodePixelHeight = MaxProxyDimension;
                }
            }

            bmp.EndInit();
            bmp.Freeze();

            if (bmp.Format == PixelFormats.Bgra32)
            {
                return bmp;
            }

            var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            return converted;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveToDiskCacheSafe(BitmapSource bitmap, string targetPath)
    {
        lock (CacheLock)
        {
            try
            {
                string? dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string tmpPath = targetPath + $".{Guid.NewGuid():N}.tmp";
                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    encoder.Save(fs);
                }

                File.Move(tmpPath, targetPath, overwrite: true);

                PruneCache(dir!);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WallpaperEffectRenderer] Cache save failed: {ex.Message}");
            }
        }
    }

    private static void PruneCache(string cacheDir)
    {
        try
        {
            var dir = new DirectoryInfo(cacheDir);
            var files = dir.GetFiles("*.png")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            if (files.Count > MaxCacheFiles)
            {
                foreach (var oldFile in files.Skip(MaxCacheFiles))
                {
                    try { oldFile.Delete(); } catch { }
                }
            }
        }
        catch { }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ToByte(float v) => (byte)Math.Clamp(v, 0f, 255f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ToByteRounded(float v) => (byte)Math.Clamp(v + 0.5f, 0f, 255f);

    public static byte[] BuildLuma(byte[] bgra, int w, int h, ParallelOptions? options = null)
    {
        var luma = new byte[w * h];
        var opt = options ?? new ParallelOptions();

        Parallel.For(0, h, opt, y =>
        {
            int rowBgra = y * w * 4;
            int rowLuma = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = rowBgra + (x * 4);
                luma[rowLuma + x] = (byte)(0.2126f * bgra[i + 2]   // R
                                         + 0.7152f * bgra[i + 1]   // G
                                         + 0.0722f * bgra[i]);     // B
            }
        });

        return luma;
    }

    public static void ApplyScanlines(byte[] src, byte[] dst, int w, int h, bool light, ParallelOptions? options = null)
    {
        var opt = options ?? new ParallelOptions();

        Parallel.For(0, h, opt, y =>
        {
            bool line = y % 3 == 0;
            int row = y * w * 4;
            int end = row + (w * 4);

            for (int i = row; i < end; i += 4)
            {
                if (line)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        float v = src[i + c];
                        dst[i + c] = ToByte(light ? v + (255f - v) * 0.48f : v * 0.52f);
                    }
                }
                else
                {
                    dst[i] = src[i];
                    dst[i + 1] = src[i + 1];
                    dst[i + 2] = src[i + 2];
                }
                dst[i + 3] = src[i + 3];
            }
        });
    }

    public static void ApplyDither(byte[] src, byte[] dst, int w, int h, ParallelOptions? options = null)
    {
        var opt = options ?? new ParallelOptions();

        Parallel.For(0, (h + 1) / 2, opt, yy =>
        {
            int y = yy * 2;
            for (int x = 0; x < w; x += 2)
            {
                int s = (Math.Min(y + 1, h - 1) * w + Math.Min(x + 1, w - 1)) * 4;
                float b = src[s];
                float g = src[s + 1];
                float r = src[s + 2];

                float peak = MathF.Max(r, MathF.Max(g, b));
                float threshold = (Bayer[((y / 2) % 4 * 4) + ((x / 2) % 4)] + 0.5f) / 16f;
                float gain = peak / 255f > threshold ? 255f / MathF.Max(peak, 1f) : 0.08f;

                byte nb = ToByteRounded(b * gain);
                byte ng = ToByteRounded(g * gain);
                byte nr = ToByteRounded(r * gain);

                for (int dy = 0; dy < 2 && y + dy < h; dy++)
                {
                    for (int dx = 0; dx < 2 && x + dx < w; dx++)
                    {
                        int d = (((y + dy) * w) + (x + dx)) * 4;
                        dst[d] = nb;
                        dst[d + 1] = ng;
                        dst[d + 2] = nr;
                        dst[d + 3] = src[d + 3];
                    }
                }
            }
        });
    }

    public static void ApplyAscii(byte[] src, byte[] luma, byte[] dst, int w, int h, bool light, ParallelOptions? options = null)
    {
        var opt = options ?? new ParallelOptions();

        Parallel.For(0, h, opt, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int sx = Math.Min((x / 6 * 6) + 3, w - 1);
                int sy = Math.Min((y / 8 * 8) + 4, h - 1);
                int sample = (sy * w) + sx;

                int density = light ? 255 - luma[sample] : luma[sample];
                int index = Math.Clamp((int)(MathF.Sqrt(density / 255f) * 9f), 0, 9);
                bool ink = x % 6 < 5 && y % 8 < 7
                    && (Glyphs[index][y % 8] & (1 << (4 - (x % 6)))) != 0;

                int i = ((y * w) + x) * 4;
                int c = sample * 4;
                float paper = light ? 255f : 0f;

                for (int ch = 0; ch < 3; ch++)
                {
                    float glyph = ink ? src[c + ch] : paper;
                    dst[i + ch] = ToByte((src[i + ch] * 0.60f) + (glyph * 0.40f));
                }
                dst[i + 3] = src[i + 3];
            }
        });
    }

    public static void ApplyHalftone(byte[] src, byte[] luma, byte[] dst, int w, int h, bool light, ParallelOptions? options = null)
    {
        var opt = options ?? new ParallelOptions();
        float paper = light ? 255f : 0f;

        Parallel.For(0, (h + 3) / 4, opt, yy =>
        {
            int y = yy * 4;
            for (int x = 0; x < w; x += 4)
            {
                int v = light ? 255 - luma[(y * w) + x] : luma[(y * w) + x];
                float radius = 2f * (0.3f + (0.7f * MathF.Sqrt(v / 255f)));
                int s = ((Math.Min(y + 2, h - 1) * w) + Math.Min(x + 2, w - 1)) * 4;
                float dotA = src[s + 3] / 255f;

                for (int dy = 0; dy < 4 && y + dy < h; dy++)
                {
                    for (int dx = 0; dx < 4 && x + dx < w; dx++)
                    {
                        float distance = MathF.Sqrt(((dx - 1.5f) * (dx - 1.5f)) + ((dy - 1.5f) * (dy - 1.5f)));
                        float coverage = Math.Clamp(radius + 0.5f - distance, 0f, 1f) * dotA;

                        int i = (((y + dy) * w) + (x + dx)) * 4;
                        for (int ch = 0; ch < 3; ch++)
                        {
                            float dot = (src[s + ch] * coverage) + (paper * (1f - coverage));
                            dst[i + ch] = ToByte((src[i + ch] * 0.60f) + (dot * 0.40f));
                        }
                        dst[i + 3] = src[i + 3];
                    }
                }
            }
        });
    }
}

