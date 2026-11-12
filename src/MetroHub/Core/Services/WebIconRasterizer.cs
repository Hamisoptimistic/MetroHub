using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MetroHub.Core.Services;

/// <summary>
/// High-performance rasterizer for web SVG icons and inline data URIs.
/// Renders vector graphics onto a dedicated background STA thread to ensure zero UI thread churn
/// and safe headless execution in unit tests.
/// </summary>
public static class WebIconRasterizer
{
    /// <summary>
    /// Sniffs if a byte sequence represents SVG XML (starts with &lt;svg or &lt;?xml containing &lt;svg).
    /// </summary>
    public static bool IsSvgBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4) return false;

        // Skip UTF-8 BOM if present
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bytes = bytes[3..];
        }

        // Skip leading ASCII whitespace
        int i = 0;
        while (i < bytes.Length && (bytes[i] == ' ' || bytes[i] == '\t' || bytes[i] == '\r' || bytes[i] == '\n'))
        {
            i++;
        }

        if (i >= bytes.Length) return false;
        bytes = bytes[i..];

        int lengthToInspect = Math.Min(bytes.Length, 8192);
        string header = Encoding.UTF8.GetString(bytes[..lengthToInspect]);
        return IsSvgHeader(header);
    }

    /// <summary>
    /// Sniffs if a header text represents SVG XML.
    /// </summary>
    public static bool IsSvgHeader(string header)
    {
        if (string.IsNullOrWhiteSpace(header)) return false;
        string trimmed = header.TrimStart();

        if (trimmed.StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
            return true;

        if ((trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith("<!--", StringComparison.OrdinalIgnoreCase)) &&
            trimmed.Contains("<svg", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Sniffs a downloaded file on disk. If it is raw SVG text disguised under another extension (e.g., .ico or .png),
    /// rasterizes it in-place to a genuine 256x256 PNG file.
    /// </summary>
    public static async Task<bool> TryRasterizeFileIfSvgAsync(string filePath, int targetSize = 256)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        try
        {
            byte[] headerBuffer = new byte[8192];
            int bytesRead;
            await using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bytesRead = await fs.ReadAsync(headerBuffer.AsMemory(0, headerBuffer.Length)).ConfigureAwait(false);
            }

            if (bytesRead <= 0 || !IsSvgBytes(headerBuffer.AsSpan(0, bytesRead)))
            {
                return false;
            }

            string svgText = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            return await RasterizeSvgToPngFileAsync(svgText, filePath, targetSize).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Safe.Ignore(ex, "WebIconRasterizer.TryRasterizeFileIfSvg");
            return false;
        }
    }

    /// <summary>
    /// Parses RFC 2397 data URIs into media type, base64 flag, and payload.
    /// E.g. data:image/svg+xml;base64,PHN2Zy...
    /// E.g. data:image/svg+xml,%3Csvg...
    /// </summary>
    public static bool TryParseDataUri(string dataUri, out string mediaType, out bool isBase64, out string payload)
    {
        mediaType = string.Empty;
        isBase64 = false;
        payload = string.Empty;

        if (string.IsNullOrWhiteSpace(dataUri) || !dataUri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;

        int commaIdx = dataUri.IndexOf(',');
        if (commaIdx < 0) return false;

        string metadata = dataUri.Substring(5, commaIdx - 5);
        payload = dataUri.Substring(commaIdx + 1);

        var parts = metadata.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 0 && !parts[0].Contains('='))
        {
            mediaType = parts[0].ToLowerInvariant();
        }

        foreach (var part in parts)
        {
            if (part.Equals("base64", StringComparison.OrdinalIgnoreCase))
            {
                isBase64 = true;
                break;
            }
        }

        return true;
    }

    /// <summary>
    /// Decodes a data URI (either raster base64 or inline SVG) and commits it to the destination file path.
    /// </summary>
    public static async Task<bool> TryProcessDataUriToFileAsync(string dataUri, string destinationPath, int targetSize = 256)
    {
        if (string.IsNullOrWhiteSpace(destinationPath) ||
            !TryParseDataUri(dataUri, out string mediaType, out bool isBase64, out string payload))
        {
            return false;
        }

        try
        {
            bool isSvg = mediaType.Contains("svg", StringComparison.OrdinalIgnoreCase);

            if (isBase64)
            {
                string cleanPayload = payload.Trim();
                if (cleanPayload.Contains('%'))
                {
                    try { cleanPayload = Uri.UnescapeDataString(cleanPayload); } catch { }
                }
                byte[] rawBytes = Convert.FromBase64String(cleanPayload);
                if (!isSvg)
                {
                    isSvg = IsSvgBytes(rawBytes);
                }

                if (isSvg)
                {
                    string svgText = Encoding.UTF8.GetString(rawBytes);
                    return await RasterizeSvgToPngFileAsync(svgText, destinationPath, targetSize).ConfigureAwait(false);
                }

                string? dir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string tmp = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllBytesAsync(tmp, rawBytes).ConfigureAwait(false);
                File.Move(tmp, destinationPath, overwrite: true);
                return true;
            }
            else
            {
                string text;
                try
                {
                    text = Uri.UnescapeDataString(payload);
                }
                catch (UriFormatException)
                {
                    text = payload;
                }

                if (!isSvg)
                {
                    isSvg = IsSvgHeader(text);
                }

                if (isSvg)
                {
                    return await RasterizeSvgToPngFileAsync(text, destinationPath, targetSize).ConfigureAwait(false);
                }

                return false;
            }
        }
        catch (Exception ex)
        {
            Safe.Ignore(ex, "WebIconRasterizer.TryProcessDataUriToFile");
            return false;
        }
    }

    /// <summary>
    /// Decodes a data URI into in-memory PNG/image bytes.
    /// </summary>
    public static async Task<byte[]?> TryProcessDataUriToBytesAsync(string dataUri, int targetSize = 256)
    {
        if (!TryParseDataUri(dataUri, out string mediaType, out bool isBase64, out string payload))
            return null;

        try
        {
            bool isSvg = mediaType.Contains("svg", StringComparison.OrdinalIgnoreCase);

            if (isBase64)
            {
                string cleanPayload = payload.Trim();
                if (cleanPayload.Contains('%'))
                {
                    try { cleanPayload = Uri.UnescapeDataString(cleanPayload); } catch { }
                }
                byte[] rawBytes = Convert.FromBase64String(cleanPayload);
                if (!isSvg)
                {
                    isSvg = IsSvgBytes(rawBytes);
                }

                if (isSvg)
                {
                    string svgText = Encoding.UTF8.GetString(rawBytes);
                    return await RasterizeSvgToPngBytesAsync(svgText, targetSize).ConfigureAwait(false);
                }

                return rawBytes;
            }
            else
            {
                string text;
                try
                {
                    text = Uri.UnescapeDataString(payload);
                }
                catch (UriFormatException)
                {
                    text = payload;
                }

                if (!isSvg)
                {
                    isSvg = IsSvgHeader(text);
                }

                if (isSvg)
                {
                    return await RasterizeSvgToPngBytesAsync(text, targetSize).ConfigureAwait(false);
                }

                return null;
            }
        }
        catch (Exception ex)
        {
            Safe.Ignore(ex, "WebIconRasterizer.TryProcessDataUriToBytes");
            return null;
        }
    }

    /// <summary>
    /// Rasterizes SVG XML text to a crisp PNG file at destinationPath using a background STA thread.
    /// </summary>
    public static async Task<bool> RasterizeSvgToPngFileAsync(string svgText, string destinationPath, int targetSize = 256)
    {
        if (string.IsNullOrWhiteSpace(svgText) || string.IsNullOrWhiteSpace(destinationPath))
            return false;

        bool? result = await RunInStaThreadAsync(() => RenderSvgToFileCore(svgText, destinationPath, targetSize)).ConfigureAwait(false);
        return result == true;
    }

    /// <summary>
    /// Rasterizes SVG XML text to genuine PNG bytes using a background STA thread.
    /// </summary>
    public static async Task<byte[]?> RasterizeSvgToPngBytesAsync(string svgText, int targetSize = 256)
    {
        if (string.IsNullOrWhiteSpace(svgText))
            return null;

        return await RunInStaThreadAsync(() => RenderSvgToPngBytesCore(svgText, targetSize)).ConfigureAwait(false);
    }

    private static bool IsMainUiThread()
    {
        try
        {
            return Application.Current?.Dispatcher?.CheckAccess() == true;
        }
        catch
        {
            return false;
        }
    }

    private static Task<T?> RunInStaThreadAsync<T>(Func<T> action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA && !IsMainUiThread())
        {
            try
            {
                return Task.FromResult<T?>(action());
            }
            catch (Exception ex)
            {
                Safe.Log(ex, "WebIconRasterizer.STAExecution");
                return Task.FromResult<T?>(default);
            }
        }

        var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staThread = new Thread(() =>
        {
            try
            {
                T result = action();
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                Safe.Log(ex, "WebIconRasterizer.STAWorker");
                tcs.TrySetResult(default);
            }
        })
        {
            IsBackground = true,
            Name = "MetroHub-WebIconRasterizer-STA"
        };

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();

        return tcs.Task;
    }

    private static bool RenderSvgToFileCore(string svgText, string destinationPath, int targetSize)
    {
        try
        {
            var rtb = RenderSvgToBitmap(svgText, targetSize);
            if (rtb == null) return false;

            string? dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmpPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(rtb));
                    encoder.Save(fs);
                }

                File.Move(tmpPath, destinationPath, overwrite: true);
                return true;
            }
            finally
            {
                if (File.Exists(tmpPath))
                {
                    try { File.Delete(tmpPath); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Safe.Ignore(ex, "WebIconRasterizer.RenderSvgToFileCore");
            return false;
        }
    }

    private static byte[]? RenderSvgToPngBytesCore(string svgText, int targetSize)
    {
        try
        {
            var rtb = RenderSvgToBitmap(svgText, targetSize);
            if (rtb == null) return null;

            using var ms = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Safe.Ignore(ex, "WebIconRasterizer.RenderSvgToPngBytesCore");
            return null;
        }
    }

    private static RenderTargetBitmap? RenderSvgToBitmap(string svgText, int targetSize)
    {
        if (string.IsNullOrWhiteSpace(svgText) || targetSize <= 0)
            return null;

        var renderer = new SVGImage.SVG.SVGRender
        {
            ExternalFileLoader = null
        };

        var drawing = renderer.LoadXmlDrawing(svgText);
        if (drawing == null)
            return null;

        Rect bounds = (renderer.SVG?.ViewBox is { IsEmpty: false, Width: > 0, Height: > 0 } vb)
            ? vb
            : drawing.Bounds;

        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            return null;

        double scale = Math.Min(targetSize / bounds.Width, targetSize / bounds.Height);
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
            return null;

        double scaledWidth = bounds.Width * scale;
        double scaledHeight = bounds.Height * scale;
        double offsetX = (targetSize - scaledWidth) / 2.0 - bounds.X * scale;
        double offsetY = (targetSize - scaledHeight) / 2.0 - bounds.Y * scale;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(offsetX, offsetY));
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawDrawing(drawing);
            dc.Pop();
            dc.Pop();
        }

        var rtb = new RenderTargetBitmap(targetSize, targetSize, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return rtb;
    }
}
