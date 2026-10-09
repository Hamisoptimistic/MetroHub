using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using MetroHub.Core.Models;

namespace MetroHub.Core.Services;

/// <summary>
/// Data transfer specification for a single tile created from clipboard content.
/// </summary>
public sealed class PasteItemSpec
{
    public string Title { get; init; } = string.Empty;
    public string TargetPath { get; init; } = string.Empty;
    public string? IconPath { get; set; }
    public TileType TileType { get; init; } = TileType.App;
    public int SpanX { get; init; } = 2;
    public int SpanY { get; init; } = 2;
}

/// <summary>
/// Immutable snapshot of clipboard payload for pure, deterministic transformation.
/// </summary>
public sealed record ClipboardSnapshot(
    IReadOnlyList<string>? Files,
    string? Text,
    BitmapSource? Image,
    bool IsSensitive
);

/// <summary>
/// Core service responsible for discriminating clipboard formats with strict priority,
/// applying safety caps, offloading file validation and encoding to background threads,
/// and generating tile specifications.
/// </summary>
public static class CanvasPasteService
{
    public const int MaxFileCount = 50;
    public const int MaxTextLength = 1024 * 1024; // 1 MB
    public const int MaxBitmapDimension = 8192;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Reads clipboard data on the caller's STA thread into an immutable snapshot.
    /// Returns null if the clipboard is empty, locked, or unavailable.
    /// </summary>
    public static ClipboardSnapshot? CaptureSnapshot(IDataObject? dataObject = null)
    {
        try
        {
            dataObject ??= SafeClipboard.GetDataObject();
            if (dataObject == null) return null;

            string[]? rawFiles = null;
            if (dataObject.GetDataPresent(DataFormats.FileDrop))
            {
                rawFiles = dataObject.GetData(DataFormats.FileDrop) as string[];
            }

            string? rawText = null;
            if (dataObject.GetDataPresent(DataFormats.UnicodeText) || dataObject.GetDataPresent(DataFormats.Text))
            {
                rawText = (dataObject.GetData(DataFormats.UnicodeText) ?? dataObject.GetData(DataFormats.Text)) as string;
            }

            BitmapSource? frozenImage = SafeClipboard.TryGetFrozenImage(dataObject);
            bool isSensitive = SafeClipboard.IsSensitiveData(dataObject);

            return new ClipboardSnapshot(rawFiles, rawText, frozenImage, isSensitive);
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "CanvasPasteService: Failed to capture clipboard snapshot");
            return null;
        }
    }

    /// <summary>
    /// Pure transformation of a clipboard snapshot into tile specifications.
    /// Performs format discrimination in strict priority: FileDrop -> URL -> Bitmap -> Text note.
    /// </summary>
    public static IReadOnlyList<PasteItemSpec>? ProcessSnapshot(ClipboardSnapshot? snapshot)
    {
        if (snapshot == null) return null;
        return ProcessClipboardDataCore(snapshot.Files, snapshot.Text, snapshot.Image, snapshot.IsSensitive);
    }

    /// <summary>
    /// Captures a clipboard snapshot on the STA thread and offloads file validation,
    /// image encoding, and I/O to a background thread.
    /// </summary>
    public static async Task<IReadOnlyList<PasteItemSpec>?> ExtractPasteItemsAsync(IDataObject? dataObject = null)
    {
        var snapshot = CaptureSnapshot(dataObject);
        if (snapshot == null) return null;

        return await Task.Run(() => ProcessSnapshot(snapshot)).ConfigureAwait(false);
    }

    private static IReadOnlyList<PasteItemSpec>? ProcessClipboardDataCore(
        IReadOnlyList<string>? rawFiles,
        string? rawText,
        BitmapSource? frozenImage,
        bool isSensitive)
    {
        // ── Priority 1: Files from Explorer (DataFormats.FileDrop) ──────────────
        if (rawFiles != null && rawFiles.Count > 0)
        {
            var fileSpecs = ProcessFiles(rawFiles);
            if (fileSpecs != null && fileSpecs.Count > 0)
            {
                return fileSpecs;
            }
        }

        // ── Priority 2: Strict URL Web Links ────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(rawText) && TryValidateWebUrl(rawText, out string normalizedUrl, out string urlTitle))
        {
            return new List<PasteItemSpec>
            {
                new()
                {
                    Title = urlTitle,
                    TargetPath = normalizedUrl,
                    TileType = TileType.WebUrl,
                    SpanX = 2,
                    SpanY = 2
                }
            };
        }

        // ── Priority 3: Raw Bitmaps / Screenshots ───────────────────────────────
        if (frozenImage != null)
        {
            var imageSpec = ProcessBitmap(frozenImage);
            if (imageSpec != null)
            {
                return new List<PasteItemSpec> { imageSpec };
            }
        }

        // ── Priority 4: Plain Text Notes (Non-URL text) ─────────────────────────
        if (!string.IsNullOrWhiteSpace(rawText) && !isSensitive)
        {
            var noteSpec = ProcessTextNote(rawText);
            if (noteSpec != null)
            {
                return new List<PasteItemSpec> { noteSpec };
            }
        }

        return null;
    }

    private static List<PasteItemSpec>? ProcessFiles(IReadOnlyList<string> files)
    {
        // Guard G7: Cap number of items to prevent canvas DOS. Oversized list rejected.
        if (files.Count > MaxFileCount)
        {
            return null;
        }

        var results = new List<PasteItemSpec>();

        foreach (string path in files)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            // Guard G6: Validate disk path, skip virtual/missing files gracefully
            bool isFile = File.Exists(path);
            bool isDir = Directory.Exists(path);
            if (!isFile && !isDir) continue;

            if (isFile && string.Equals(Path.GetExtension(path), ".url", StringComparison.OrdinalIgnoreCase))
            {
                string parsedUrl = ParseUrlFile(path);
                string title = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = WebFaviconService.InferTitleFromUrl(parsedUrl);
                }

                results.Add(new PasteItemSpec
                {
                    Title = title,
                    TargetPath = parsedUrl,
                    TileType = TileType.WebUrl,
                    SpanX = 2,
                    SpanY = 2
                });
            }
            else
            {
                string title = isDir
                    ? new DirectoryInfo(path).Name
                    : Path.GetFileNameWithoutExtension(path);
                string? iconPath = IconExtractorService.ExtractAndCacheIcon(path);

                results.Add(new PasteItemSpec
                {
                    Title = title,
                    TargetPath = path,
                    IconPath = iconPath,
                    TileType = isDir ? TileType.Folder : TileType.App,
                    SpanX = 2,
                    SpanY = 2
                });
            }
        }

        return results.Count > 0 ? results : null;
    }

    private static PasteItemSpec? ProcessBitmap(BitmapSource bitmap)
    {
        try
        {
            // Guard G7: Check dimensions against bounds
            if (bitmap.PixelWidth > MaxBitmapDimension || bitmap.PixelHeight > MaxBitmapDimension)
            {
                return null;
            }

            string targetDir = AppPaths.PastedImagesDir;
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            // Guard G8 & Content-Addressable Storage: encode to memory buffer and compute SHA-256 hash
            using var ms = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(ms);
            byte[] pngBytes = ms.ToArray();

            string fileName = GenerateImageFileName(pngBytes);
            string targetPath = Path.Combine(targetDir, fileName);

            // Fast deduplication: if identical image exists on disk, reuse it without rewriting
            if (!File.Exists(targetPath))
            {
                string tmpPath = Path.Combine(targetDir, $"{fileName}.tmp.{Guid.NewGuid():N}");
                try
                {
                    File.WriteAllBytes(tmpPath, pngBytes);
                    File.Move(tmpPath, targetPath, overwrite: false);
                }
                catch (IOException)
                {
                    Safe.Try(() => { if (File.Exists(tmpPath)) File.Delete(tmpPath); });
                }
            }

            string title = $"Image {DateTime.Now:yyyy-MM-dd HH.mm}";

            return new PasteItemSpec
            {
                Title = title,
                TargetPath = targetPath,
                IconPath = targetPath,
                TileType = TileType.App,
                SpanX = 2,
                SpanY = 2
            };
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "CanvasPasteService: Failed to encode and persist pasted bitmap");
            return null;
        }
    }

    private static PasteItemSpec? ProcessTextNote(string text)
    {
        try
        {
            // Guard G7: Cap text size to 1 MB. Oversized text rejected silently.
            if (text.Length > MaxTextLength)
            {
                return null;
            }

            string targetDir = AppPaths.PastedNotesDir;
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            // Guard G8: Deterministic unique filename
            string fileName = GenerateNoteFileName();
            string targetPath = Path.Combine(targetDir, fileName);

            File.WriteAllText(targetPath, text, Utf8NoBom);

            // Determine display title from first non-empty line
            string firstLine = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            string title = firstLine.Length > 24
                ? firstLine.Substring(0, 24).Trim() + "..."
                : firstLine.Trim();

            if (string.IsNullOrWhiteSpace(title))
            {
                title = $"Note {DateTime.Now:yyyy-MM-dd HH.mm}";
            }

            string? iconPath = IconExtractorService.ExtractAndCacheIcon(targetPath);

            return new PasteItemSpec
            {
                Title = title,
                TargetPath = targetPath,
                IconPath = iconPath,
                TileType = TileType.App,
                SpanX = 2,
                SpanY = 2
            };
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "CanvasPasteService: Failed to persist pasted text note");
            return null;
        }
    }

    /// <summary>
    /// Guard G3: Validates text strictly as a web URL.
    /// Requires a single trimmed line without spaces, a valid URI, and strict HTTP/HTTPS scheme.
    /// Rejects javascript:, file:, data:, bare filenames, userinfo, and multi-line snippets.
    /// </summary>
    public static bool TryValidateWebUrl(string? rawText, out string normalizedUrl, out string title)
    {
        normalizedUrl = string.Empty;
        title = string.Empty;

        if (string.IsNullOrWhiteSpace(rawText)) return false;

        string trimmed = rawText.Trim();

        if (trimmed.Length > MaxTextLength) return false;

        // Must be a single line
        if (trimmed.Contains('\n') || trimmed.Contains('\r')) return false;

        // Must not contain whitespace
        if (trimmed.Any(char.IsWhiteSpace)) return false;

        // Reject scheme-relative URLs and user credentials / email addresses
        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Contains('@')) return false;

        // Reject dangerous/unsupported schemes
        if (trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("ftp:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool hasHttp = trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                       trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        bool hasWww = trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase);

        string domainCandidate = trimmed;
        int slashIdx = domainCandidate.IndexOf('/');
        if (slashIdx > 0)
        {
            domainCandidate = domainCandidate.Substring(0, slashIdx);
        }
        int colonIdx = domainCandidate.IndexOf(':');
        if (colonIdx > 0 && !hasHttp)
        {
            domainCandidate = domainCandidate.Substring(0, colonIdx);
        }

        bool isKnownBrand = WebFaviconService.KnownBrands.ContainsKey(domainCandidate);

        // Only accept if explicit http/https, starts with www., or belongs to KnownBrands
        if (!hasHttp && !hasWww && !isKnownBrand)
        {
            return false;
        }

        string normalized = hasHttp
            ? trimmed
            : (hasWww ? "https://" + trimmed : "https://" + trimmed);

        if (Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri))
        {
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            {
                if (string.IsNullOrEmpty(uri.UserInfo) &&
                    !string.IsNullOrWhiteSpace(uri.Host) &&
                    uri.Host.Contains('.') &&
                    !uri.Host.EndsWith('.'))
                {
                    normalizedUrl = uri.AbsoluteUri;
                    title = WebFaviconService.InferTitleFromUrl(normalizedUrl);
                    return true;
                }
            }
        }

        return false;
    }

    internal static string GenerateImageFileName(ReadOnlySpan<byte> pngBytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(pngBytes, hash);
        return $"img_{Convert.ToHexStringLower(hash)}.png";
    }

    internal static string GenerateNoteFileName() => $"Note_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.txt";

    private static string ParseUrlFile(string urlFilePath)
    {
        try
        {
            foreach (var line in File.ReadAllLines(urlFilePath))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed.Substring(4).Trim();
                }
            }
        }
        catch { }

        return urlFilePath;
    }
}
