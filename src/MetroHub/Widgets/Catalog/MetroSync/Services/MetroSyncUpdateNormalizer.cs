using System.Text.Json;
using MetroHub.Core.Services;
using MetroHub.Widgets.Catalog.MetroSync.Models;

namespace MetroHub.Widgets.Catalog.MetroSync.Services;

public sealed class MetroSyncUpdateNormalizer : IUpdateNormalizer
{
    public MetroSyncMessageItem? Normalize(RawUpdate update, DateTimeOffset receivedAt)
    {
        try
        {
            var root = update.Json;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.Object)
            {
                return new TextPayloadItem
                {
                    UpdateId = update.UpdateId,
                    MessageId = 0,
                    Timestamp = receivedAt,
                    IsOutgoing = false,
                    Text = "[Unsupported update]"
                };
            }

            return NormalizeMessage(message, receivedAt, isOutgoing: false, updateId: update.UpdateId);
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "MetroSyncUpdateNormalizer.NormalizeInbound");
            // Must never throw for malformed shapes
            return new TextPayloadItem
            {
                UpdateId = update.UpdateId,
                MessageId = 0,
                Timestamp = receivedAt,
                IsOutgoing = false,
                Text = "[Unsupported update]"
            };
        }
    }

    public MetroSyncMessageItem? NormalizeOutbound(JsonElement message, DateTimeOffset sentAt)
    {
        try
        {
            if (message.ValueKind == JsonValueKind.Object &&
                message.TryGetProperty("result", out var res) &&
                res.ValueKind == JsonValueKind.Object)
            {
                message = res;
            }

            if (message.ValueKind != JsonValueKind.Object)
            {
                return new TextPayloadItem
                {
                    UpdateId = 0,
                    MessageId = 0,
                    Timestamp = sentAt,
                    IsOutgoing = true,
                    Text = "[Unsupported update]"
                };
            }

            return NormalizeMessage(message, sentAt, isOutgoing: true, updateId: 0);
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "MetroSyncUpdateNormalizer.NormalizeOutbound");
            return new TextPayloadItem
            {
                UpdateId = 0,
                MessageId = 0,
                Timestamp = sentAt,
                IsOutgoing = true,
                Text = "[Unsupported update]"
            };
        }
    }

    public MetroSyncMessageItem? NormalizeMessage(
        JsonElement message,
        DateTimeOffset fallbackTimestamp,
        bool isOutgoing,
        long updateId)
    {
        long messageId = 0;
        var timestamp = fallbackTimestamp;

        if (message.TryGetProperty("message_id", out var mid) && mid.TryGetInt64(out var m))
        {
            messageId = m;
        }

        if (message.TryGetProperty("date", out var d) && d.TryGetInt64(out var sec) &&
            sec >= 0 && sec <= 253_402_300_799)
        {
            timestamp = DateTimeOffset.FromUnixTimeSeconds(sec);
        }

        // 1. Text
        var text = TryGetString(message, "text");
        if (text is not null)
        {
            if (!isOutgoing && text.Trim().Equals("/start", StringComparison.OrdinalIgnoreCase))
            {
                // A message that is exactly /start is acknowledged but not stored in history.
                return null;
            }

            return new TextPayloadItem
            {
                UpdateId = updateId,
                MessageId = messageId,
                Timestamp = timestamp,
                IsOutgoing = isOutgoing,
                Text = text
            };
        }

        // 2. Photo (array of PhotoSize)
        if (message.TryGetProperty("photo", out var photoElem) && photoElem.ValueKind == JsonValueKind.Array)
        {
            string? selectedFileId = null;
            var bestWidth = int.MaxValue;
            var maxWidth = int.MinValue;
            string? maxFileId = null;

            foreach (var size in photoElem.EnumerateArray())
            {
                if (size.ValueKind != JsonValueKind.Object) continue;
                var fileId = TryGetString(size, "file_id");
                if (string.IsNullOrEmpty(fileId)) continue;

                var width = size.TryGetProperty("width", out var w) && w.TryGetInt32(out var widthVal) ? widthVal : 0;

                if (width > maxWidth || maxFileId is null)
                {
                    maxWidth = width;
                    maxFileId = fileId;
                }

                if (width >= 200)
                {
                    if (width < bestWidth)
                    {
                        bestWidth = width;
                        selectedFileId = fileId;
                    }
                }
            }

            selectedFileId ??= maxFileId;
            maxFileId ??= selectedFileId;

            if (selectedFileId is not null)
            {
                var caption = TryGetString(message, "caption");

                return new ImagePayloadItem
                {
                    UpdateId = updateId,
                    MessageId = messageId,
                    Timestamp = timestamp,
                    IsOutgoing = isOutgoing,
                    ThumbnailFileId = selectedFileId,
                    FileId = maxFileId,
                    Caption = caption
                };
            }
        }

        // 3. Animation
        if (message.TryGetProperty("animation", out var animElem) && animElem.ValueKind == JsonValueKind.Object)
        {
            var fileId = TryGetString(animElem, "file_id");
            var fileName = TryGetString(animElem, "file_name");
            var caption = TryGetString(message, "caption") ?? TryGetString(animElem, "caption");
            var thumbFileId = ExtractThumbFileId(animElem);

            return new ImagePayloadItem
            {
                UpdateId = updateId,
                MessageId = messageId,
                Timestamp = timestamp,
                IsOutgoing = isOutgoing,
                ThumbnailFileId = thumbFileId ?? fileId,
                FileId = fileId,
                FileName = string.IsNullOrWhiteSpace(fileName) ? "animation.mp4" : fileName,
                Caption = caption,
                IsAnimation = true
            };
        }

        // 4. Document
        if (message.TryGetProperty("document", out var docElem) && docElem.ValueKind == JsonValueKind.Object)
        {
            var fileId = TryGetString(docElem, "file_id");
            var fileName = TryGetString(docElem, "file_name");
            var fileSize = docElem.TryGetProperty("file_size", out var fs) && fs.TryGetInt64(out var s) ? s : (long?)null;
            var mimeType = TryGetString(docElem, "mime_type");
            var caption = TryGetString(message, "caption");

            if (string.Equals(mimeType, "image/gif", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(fileName) && fileName.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)))
            {
                var thumbFileId = ExtractThumbFileId(docElem);
                return new ImagePayloadItem
                {
                    UpdateId = updateId,
                    MessageId = messageId,
                    Timestamp = timestamp,
                    IsOutgoing = isOutgoing,
                    ThumbnailFileId = thumbFileId ?? fileId,
                    FileId = fileId,
                    FileName = string.IsNullOrWhiteSpace(fileName) ? "animation.gif" : fileName,
                    Caption = caption,
                    IsAnimation = true
                };
            }

            return new FilePayloadItem
            {
                UpdateId = updateId,
                MessageId = messageId,
                Timestamp = timestamp,
                IsOutgoing = isOutgoing,
                FileId = fileId,
                FileName = string.IsNullOrWhiteSpace(fileName) ? "document" : fileName,
                FileSize = fileSize,
                MimeType = mimeType,
                Caption = caption
            };
        }

        // 5. Video
        if (message.TryGetProperty("video", out var vidElem) && vidElem.ValueKind == JsonValueKind.Object)
        {
            var fileId = TryGetString(vidElem, "file_id");
            var fileName = TryGetString(vidElem, "file_name");
            var fileSize = vidElem.TryGetProperty("file_size", out var fs) && fs.TryGetInt64(out var s) ? s : (long?)null;
            var mimeType = TryGetString(vidElem, "mime_type") ?? "video/mp4";
            var caption = TryGetString(message, "caption");

            return new FilePayloadItem
            {
                UpdateId = updateId,
                MessageId = messageId,
                Timestamp = timestamp,
                IsOutgoing = isOutgoing,
                FileId = fileId,
                FileName = string.IsNullOrWhiteSpace(fileName) ? "video.mp4" : fileName,
                FileSize = fileSize,
                MimeType = mimeType,
                Caption = caption
            };
        }

        // 6. Video note (round video message)
        if (message.TryGetProperty("video_note", out var vnElem) && vnElem.ValueKind == JsonValueKind.Object)
        {
            var fileId = TryGetString(vnElem, "file_id");
            var fileSize = vnElem.TryGetProperty("file_size", out var fs) && fs.TryGetInt64(out var s) ? s : (long?)null;

            return new FilePayloadItem
            {
                UpdateId = updateId,
                MessageId = messageId,
                Timestamp = timestamp,
                IsOutgoing = isOutgoing,
                FileId = fileId,
                FileName = "video_note.mp4",
                FileSize = fileSize,
                MimeType = "video/mp4",
                Caption = null
            };
        }

        // 7. Audio
        if (message.TryGetProperty("audio", out var audElem) && audElem.ValueKind == JsonValueKind.Object)
        {
            var fileId = TryGetString(audElem, "file_id");
            var fileName = TryGetString(audElem, "file_name");
            if (string.IsNullOrWhiteSpace(fileName))
            {
                var title = TryGetString(audElem, "title");
                if (!string.IsNullOrWhiteSpace(title))
                {
                    fileName = $"{title}.mp3";
                }
            }
            var fileSize = audElem.TryGetProperty("file_size", out var fs) && fs.TryGetInt64(out var s) ? s : (long?)null;
            var mimeType = TryGetString(audElem, "mime_type") ?? "audio/mpeg";
            var caption = TryGetString(message, "caption");

            return new FilePayloadItem
            {
                UpdateId = updateId,
                MessageId = messageId,
                Timestamp = timestamp,
                IsOutgoing = isOutgoing,
                FileId = fileId,
                FileName = string.IsNullOrWhiteSpace(fileName) ? "audio.mp3" : fileName,
                FileSize = fileSize,
                MimeType = mimeType,
                Caption = caption
            };
        }

        // 8. Voice
        if (message.TryGetProperty("voice", out var voiceElem) && voiceElem.ValueKind == JsonValueKind.Object)
        {
            var fileId = TryGetString(voiceElem, "file_id");
            var fileSize = voiceElem.TryGetProperty("file_size", out var fs) && fs.TryGetInt64(out var s) ? s : (long?)null;
            var mimeType = TryGetString(voiceElem, "mime_type") ?? "audio/ogg";
            var caption = TryGetString(message, "caption");

            return new FilePayloadItem
            {
                UpdateId = updateId,
                MessageId = messageId,
                Timestamp = timestamp,
                IsOutgoing = isOutgoing,
                FileId = fileId,
                FileName = "voice.ogg",
                FileSize = fileSize,
                MimeType = mimeType,
                Caption = caption
            };
        }

        // 9. Sticker
        if (message.TryGetProperty("sticker", out var stickerElem) && stickerElem.ValueKind == JsonValueKind.Object)
        {
            var fileId = TryGetString(stickerElem, "file_id");
            if (!string.IsNullOrEmpty(fileId))
            {
                var emoji = TryGetString(stickerElem, "emoji");
                var setName = TryGetString(stickerElem, "set_name");
                var thumbFileId = ExtractThumbFileId(stickerElem);

                bool isAnimated = stickerElem.TryGetProperty("is_animated", out var ia) && ia.ValueKind == JsonValueKind.True;
                bool isVideo = stickerElem.TryGetProperty("is_video", out var iv) && iv.ValueKind == JsonValueKind.True;

                if (thumbFileId == null && !isAnimated && !isVideo)
                {
                    thumbFileId = fileId;
                }

                return new ImagePayloadItem
                {
                    UpdateId = updateId,
                    MessageId = messageId,
                    Timestamp = timestamp,
                    IsOutgoing = isOutgoing,
                    FileId = fileId,
                    ThumbnailFileId = thumbFileId,
                    IsSticker = true,
                    Emoji = emoji,
                    Caption = null
                };
            }
        }

        // 10. Unsupported types fallback
        var notice = "[Unsupported message]";
        if (message.TryGetProperty("sticker", out var stkElem))
        {
            var emoji = stkElem.ValueKind == JsonValueKind.Object ? TryGetString(stkElem, "emoji") : null;
            notice = !string.IsNullOrWhiteSpace(emoji) ? $"[Sticker {emoji}]" : "[Sticker]";
        }
        else if (message.TryGetProperty("location", out _)) notice = "[Location]";
        else if (message.TryGetProperty("contact", out _)) notice = "[Contact]";
        else if (message.TryGetProperty("poll", out _)) notice = "[Poll]";
        else if (message.TryGetProperty("venue", out _)) notice = "[Venue]";
        else if (message.TryGetProperty("dice", out _)) notice = "[Dice]";
        else if (message.TryGetProperty("game", out _)) notice = "[Game]";

        return new TextPayloadItem
        {
            UpdateId = updateId,
            MessageId = messageId,
            Timestamp = timestamp,
            IsOutgoing = isOutgoing,
            Text = notice
        };
    }

    private static string? ExtractThumbFileId(JsonElement parent)
    {
        if (parent.TryGetProperty("thumbnail", out var thumbElem) || parent.TryGetProperty("thumb", out thumbElem))
        {
            if (thumbElem.ValueKind == JsonValueKind.Object)
            {
                return TryGetString(thumbElem, "file_id");
            }

            if (thumbElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in thumbElem.EnumerateArray())
                {
                    if (s.ValueKind == JsonValueKind.Object)
                    {
                        var id = TryGetString(s, "file_id");
                        if (!string.IsNullOrEmpty(id)) return id;
                    }
                }
            }
        }

        return null;
    }

    private static string? TryGetString(JsonElement obj, string propertyName)
    {
        return obj.TryGetProperty(propertyName, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
    }
}
