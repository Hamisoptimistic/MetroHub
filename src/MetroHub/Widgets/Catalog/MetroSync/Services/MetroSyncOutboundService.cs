using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using MetroHub.Widgets.Catalog.MetroSync.Models;

namespace MetroHub.Widgets.Catalog.MetroSync.Services;

/// <summary>
/// Service responsible for PC -> Phone outbound message dispatch.
/// Handles sending text, clipboard images, and photo streams to Telegram,
/// normalizing responses into MetroSyncMessageItem (IsOutgoing = true),
/// caching local thumbnails, and atomically appending sent items to history.jsonl.
/// </summary>
public sealed class MetroSyncOutboundService
{
    public const long MaxPhotoSizeBytes = 10 * 1024 * 1024; // 10 MB
    public const int MaxTextLength = 4096;

    private readonly ITelegramTransport _transport;
    private readonly MetroSyncUpdateNormalizer _normalizer;
    private readonly MetroSyncHistoryStore _historyStore;
    private readonly MetroSyncThumbnails _thumbnails;

    public MetroSyncOutboundService(
        ITelegramTransport transport,
        MetroSyncUpdateNormalizer normalizer,
        MetroSyncHistoryStore historyStore,
        MetroSyncThumbnails thumbnails)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(normalizer);
        ArgumentNullException.ThrowIfNull(historyStore);
        ArgumentNullException.ThrowIfNull(thumbnails);

        _transport = transport;
        _normalizer = normalizer;
        _historyStore = historyStore;
        _thumbnails = thumbnails;
    }

    /// <summary>
    /// Sends a text message to Telegram, normalizes response into TextPayloadItem (IsOutgoing = true),
    /// and appends it to history.jsonl. If sending fails, history remains untouched.
    /// </summary>
    public async Task<TextPayloadItem> SendTextAsync(long chatId, string text, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chatId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        if (text.Length > MaxTextLength)
        {
            throw new ArgumentOutOfRangeException(nameof(text), $"Text exceeds maximum length of {MaxTextLength} characters.");
        }

        using var doc = await _transport.SendMessageAsync(chatId, text, ct).ConfigureAwait(false);
        var item = _normalizer.NormalizeOutbound(doc.RootElement, DateTimeOffset.UtcNow);

        if (item is not TextPayloadItem textItem)
        {
            throw new InvalidOperationException("Failed to normalize outbound text message response.");
        }

        textItem.IsOutgoing = true;
        await _historyStore.AppendAsync(textItem, ct).ConfigureAwait(false);
        return textItem;
    }

    /// <summary>
    /// Sends an image stream to Telegram, normalizes response into ImagePayloadItem (IsOutgoing = true),
    /// generates a local 200px thumbnail, and appends it to history.jsonl.
    /// </summary>
    public async Task<ImagePayloadItem> SendPhotoAsync(
        long chatId,
        Stream imageStream,
        string? caption = null,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chatId);
        ArgumentNullException.ThrowIfNull(imageStream);

        if (caption is not null && caption.Length > MaxTextLength)
        {
            throw new ArgumentOutOfRangeException(nameof(caption), $"Caption exceeds maximum length of {MaxTextLength} characters.");
        }

        byte[] imageBytes;
        if (imageStream.CanSeek)
        {
            if (imageStream.Length > MaxPhotoSizeBytes)
            {
                throw new FileTooLargeException(MaxPhotoSizeBytes);
            }
            using var ms = new MemoryStream((int)imageStream.Length);
            await imageStream.CopyToAsync(ms, ct).ConfigureAwait(false);
            imageBytes = ms.ToArray();
        }
        else
        {
            using var ms = new MemoryStream();
            await imageStream.CopyToAsync(ms, ct).ConfigureAwait(false);
            if (ms.Length > MaxPhotoSizeBytes)
            {
                throw new FileTooLargeException(MaxPhotoSizeBytes);
            }
            imageBytes = ms.ToArray();
        }

        if (imageBytes.Length > MaxPhotoSizeBytes)
        {
            throw new FileTooLargeException(MaxPhotoSizeBytes);
        }

        using (var uploadStream = new MemoryStream(imageBytes, writable: false))
        {
            using var doc = await _transport.SendPhotoAsync(chatId, uploadStream, caption, ct).ConfigureAwait(false);
            var item = _normalizer.NormalizeOutbound(doc.RootElement, DateTimeOffset.UtcNow);

            if (item is not ImagePayloadItem photoItem)
            {
                throw new InvalidOperationException("Failed to normalize outbound photo message response.");
            }

            photoItem.IsOutgoing = true;

            // Generate and save local thumbnail (spec §7.4: do not re-download from Telegram)
            using (var thumbStream = new MemoryStream(imageBytes, writable: false))
            {
                var thumbPath = await _thumbnails.SaveThumbnailAsync(photoItem.MessageId, thumbStream, ct).ConfigureAwait(false);
                photoItem.ThumbnailFilePath = thumbPath;
            }

            await _historyStore.AppendAsync(photoItem, ct).ConfigureAwait(false);
            return photoItem;
        }
    }

    /// <summary>
    /// Sends a clipboard BitmapSource to Telegram, encoding to PNG/JPEG with a 10 MB limit,
    /// generating a local 200px thumbnail, and appending it to history.jsonl.
    /// </summary>
    public async Task<ImagePayloadItem> SendPhotoAsync(
        long chatId,
        BitmapSource bitmapSource,
        string? caption = null,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chatId);
        ArgumentNullException.ThrowIfNull(bitmapSource);

        var safeSource = MetroSyncThumbnails.EnsureThreadSafeBitmap(bitmapSource);
        var imageBytes = EncodeBitmap(safeSource);

        using (var uploadStream = new MemoryStream(imageBytes, writable: false))
        {
            using var doc = await _transport.SendPhotoAsync(chatId, uploadStream, caption, ct).ConfigureAwait(false);
            var item = _normalizer.NormalizeOutbound(doc.RootElement, DateTimeOffset.UtcNow);

            if (item is not ImagePayloadItem photoItem)
            {
                throw new InvalidOperationException("Failed to normalize outbound photo message response.");
            }

            photoItem.IsOutgoing = true;

            var thumbPath = await _thumbnails.SaveThumbnailAsync(photoItem.MessageId, safeSource, ct).ConfigureAwait(false);
            photoItem.ThumbnailFilePath = thumbPath;

            await _historyStore.AppendAsync(photoItem, ct).ConfigureAwait(false);
            return photoItem;
        }
    }

    /// <summary>
    /// Encodes a BitmapSource into byte array. First tries PNG; if larger than 10 MB,
    /// falls back to JPEG (Quality 85). If still > 10 MB, throws FileTooLargeException.
    /// </summary>
    public static byte[] EncodeBitmap(BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var safeSource = MetroSyncThumbnails.EnsureThreadSafeBitmap(bitmap);

        // Try PNG first
        using (var pngMs = new MemoryStream())
        {
            var pngEncoder = new PngBitmapEncoder();
            pngEncoder.Frames.Add(BitmapFrame.Create(safeSource));
            pngEncoder.Save(pngMs);
            if (pngMs.Length <= MaxPhotoSizeBytes)
            {
                return pngMs.ToArray();
            }
        }

        // Exceeds 10 MB: try JPEG (quality 85)
        using (var jpegMs = new MemoryStream())
        {
            var jpegEncoder = new JpegBitmapEncoder { QualityLevel = 85 };
            jpegEncoder.Frames.Add(BitmapFrame.Create(safeSource));
            jpegEncoder.Save(jpegMs);
            if (jpegMs.Length <= MaxPhotoSizeBytes)
            {
                return jpegMs.ToArray();
            }
        }

        throw new FileTooLargeException(MaxPhotoSizeBytes);
    }
}
