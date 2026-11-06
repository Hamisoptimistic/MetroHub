using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MetroHub.Core.Services;

namespace MetroHub.Widgets.Catalog.MetroSync.Services;

/// <summary>
/// Downscaling and disk-caching service for MetroSync image thumbnails.
/// Guarantees that images are bounded to max 200px width/height and frozen for memory efficiency.
/// </summary>
public sealed class MetroSyncThumbnails
{
    public const int MaxThumbnailDimension = 200;

    private readonly string _directoryPath;
    private readonly string _thumbnailsDir;

    public static MetroSyncThumbnails Default { get; } = new();

    public string ThumbnailsDirectory => _thumbnailsDir;

    public MetroSyncThumbnails(string? baseDirectory = null)
    {
        _directoryPath = baseDirectory ?? Path.Combine(AppPaths.AppDataDir, "MetroSync");
        _thumbnailsDir = Path.Combine(_directoryPath, "Thumbnails");
    }

    /// <summary>
    /// Computes the disk path for a cached thumbnail image by message ID.
    /// </summary>
    public string GetThumbnailPath(long messageId)
    {
        return Path.Combine(_thumbnailsDir, $"{messageId}.jpg");
    }

    /// <summary>
    /// Decodes an image stream into a frozen BitmapImage, constrained to max 200px width/height.
    /// WIC downsampling ensures uncompressed full-resolution frames never reside in memory.
    /// </summary>
    public static BitmapImage CreateFrozenThumbnail(Stream stream, int maxDimension = MaxThumbnailDimension)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maxDimension <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDimension), "Max dimension must be positive.");
        }

        if (!stream.CanSeek)
        {
            using var memStream = new MemoryStream();
            stream.CopyTo(memStream);
            memStream.Position = 0;
            return CreateFrozenThumbnail(memStream, maxDimension);
        }

        long initialPos = stream.Position;
        int targetW = 0;
        int targetH = 0;

        try
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count > 0)
            {
                int origW = decoder.Frames[0].PixelWidth;
                int origH = decoder.Frames[0].PixelHeight;

                if (origW > maxDimension || origH > maxDimension)
                {
                    if (origW >= origH)
                    {
                        targetW = maxDimension;
                    }
                    else
                    {
                        targetH = maxDimension;
                    }
                }
            }
        }
        catch
        {
            targetW = maxDimension;
        }

        stream.Position = initialPos;

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;

        if (targetW > 0)
        {
            bmp.DecodePixelWidth = targetW;
        }
        else if (targetH > 0)
        {
            bmp.DecodePixelHeight = targetH;
        }

        bmp.StreamSource = stream;
        bmp.EndInit();
        bmp.Freeze();

        return bmp;
    }

    /// <summary>
    /// Downscales and caches an incoming image stream to Thumbnails\{messageId}.jpg.
    /// Returns the absolute path of the cached thumbnail, or null if decoding fails.
    /// </summary>
    public async Task<string?> SaveThumbnailAsync(long messageId, Stream imageStream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageStream);

        return await Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(_thumbnailsDir);
                string destPath = GetThumbnailPath(messageId);

                var bmp = CreateFrozenThumbnail(imageStream, MaxThumbnailDimension);

                string tmpPath = destPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
                    encoder.Frames.Add(BitmapFrame.Create(bmp));

                    using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        encoder.Save(fs);
                    }

                    File.Move(tmpPath, destPath, overwrite: true);
                    return destPath;
                }
                finally
                {
                    if (File.Exists(tmpPath))
                    {
                        try { File.Delete(tmpPath); } catch { }
                    }
                }
            }
            catch
            {
                return null;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downscales a clipboard BitmapSource to max 200px width/height and saves to Thumbnails\{messageId}.jpg.
    /// </summary>
    public string? SaveThumbnail(long messageId, BitmapSource bitmapSource)
    {
        ArgumentNullException.ThrowIfNull(bitmapSource);

        try
        {
            Directory.CreateDirectory(_thumbnailsDir);
            string destPath = GetThumbnailPath(messageId);

            BitmapSource safeSource = EnsureThreadSafeBitmap(bitmapSource);
            int origW = safeSource.PixelWidth;
            int origH = safeSource.PixelHeight;

            BitmapSource scaledSource;
            if (origW > MaxThumbnailDimension || origH > MaxThumbnailDimension)
            {
                double scale = origW >= origH
                    ? (double)MaxThumbnailDimension / origW
                    : (double)MaxThumbnailDimension / origH;

                var transformed = new TransformedBitmap(safeSource, new ScaleTransform(scale, scale));
                transformed.Freeze();
                scaledSource = transformed;
            }
            else
            {
                scaledSource = safeSource;
            }

            string tmpPath = destPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
                encoder.Frames.Add(BitmapFrame.Create(scaledSource));

                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    encoder.Save(fs);
                }

                File.Move(tmpPath, destPath, overwrite: true);
                return destPath;
            }
            finally
            {
                if (File.Exists(tmpPath))
                {
                    try { File.Delete(tmpPath); } catch { }
                }
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Downscales a clipboard BitmapSource asynchronously on a worker thread.
    /// Safely isolates and freezes the source on the calling thread to prevent thread ownership errors.
    /// </summary>
    public async Task<string?> SaveThumbnailAsync(long messageId, BitmapSource bitmapSource, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmapSource);

        BitmapSource safeSource = EnsureThreadSafeBitmap(bitmapSource);
        return await Task.Run(() => SaveThumbnail(messageId, safeSource), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads a cached thumbnail from disk, setting DecodePixelWidth to 200 and calling Freeze.
    /// Returns null if the file does not exist or fails to decode.
    /// </summary>
    public static BitmapImage? LoadThumbnail(string? filePath, int maxDimension = MaxThumbnailDimension)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || maxDimension <= 0)
        {
            return null;
        }

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return CreateFrozenThumbnail(fs, maxDimension);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Ensures that a BitmapSource is frozen and safe to pass across threads.
    /// If unfreezable (e.g. InteropBitmap), copies raw pixels into a newly allocated frozen BitmapSource.
    /// </summary>
    private static BitmapSource EnsureThreadSafeBitmap(BitmapSource source)
    {
        if (source.IsFrozen)
        {
            return source;
        }

        if (source.CanFreeze)
        {
            source.Freeze();
            return source;
        }

        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int bitsPerPixel = source.Format.BitsPerPixel;
        int stride = (width * bitsPerPixel + 7) / 8;
        byte[] pixels = new byte[height * stride];
        source.CopyPixels(pixels, stride, 0);

        var copy = BitmapSource.Create(width, height, source.DpiX, source.DpiY, source.Format, source.Palette, pixels, stride);
        copy.Freeze();
        return copy;
    }

    /// <summary>
    /// Loads the cached thumbnail for the specified messageId.
    /// </summary>
    public BitmapImage? LoadThumbnail(long messageId)
    {
        return LoadThumbnail(GetThumbnailPath(messageId));
    }

    public static string GetPath(long messageId) => Default.GetThumbnailPath(messageId);
}
