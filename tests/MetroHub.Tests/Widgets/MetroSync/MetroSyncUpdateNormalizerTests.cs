using System.Text.Json;
using MetroHub.Tests.Widgets.MetroSync.TestKit;
using MetroHub.Widgets.Catalog.MetroSync.Models;
using MetroHub.Widgets.Catalog.MetroSync.Services;
using Xunit;

namespace MetroHub.Tests.Widgets.MetroSync;

public sealed class MetroSyncUpdateNormalizerTests
{
    private readonly MetroSyncUpdateNormalizer _normalizer = new();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    [Fact]
    public void Normalize_PlainTextMessage_ReturnsTextPayloadItem()
    {
        var raw = UpdateBuilder.ToRaw(10, UpdateBuilder.Text(42, "hello world"));
        var item = _normalizer.Normalize(raw, _now);

        var textItem = Assert.IsType<TextPayloadItem>(item);
        Assert.Equal(10, textItem.UpdateId);
        Assert.Equal(10, textItem.MessageId);
        Assert.False(textItem.IsOutgoing);
        Assert.Equal("hello world", textItem.Text);
    }

    [Theory]
    [InlineData("/start")]
    [InlineData("/START")]
    [InlineData("  /start  ")]
    public void Normalize_StartCommand_ReturnsNull(string command)
    {
        var raw = UpdateBuilder.ToRaw(11, UpdateBuilder.Text(42, command));
        var item = _normalizer.Normalize(raw, _now);

        Assert.Null(item);
    }

    [Fact]
    public void Normalize_UrlMessage_ReturnsTextPayloadItemWithUrl()
    {
        var raw = UpdateBuilder.ToRaw(12, UpdateBuilder.UrlMessage(42, "https://github.com/dotnet"));
        var item = _normalizer.Normalize(raw, _now);

        var textItem = Assert.IsType<TextPayloadItem>(item);
        Assert.Equal("https://github.com/dotnet", textItem.Text);
    }

    [Fact]
    public void Normalize_Photo_SelectsSmallestWidthAtLeast200()
    {
        // UpdateBuilder.Photo provides sizes: 51x51, 180x180, 450x450, 1280x1280
        var raw = UpdateBuilder.ToRaw(13, UpdateBuilder.Photo(42, caption: "nice pic"));
        var item = _normalizer.Normalize(raw, _now);

        var imageItem = Assert.IsType<ImagePayloadItem>(item);
        Assert.Equal("p450", imageItem.FileId);
        Assert.Equal("nice pic", imageItem.Caption);
        Assert.False(imageItem.IsOutgoing);
    }

    [Fact]
    public void Normalize_Photo_WhenNoSizeAtLeast200_SelectsLargestAvailable()
    {
        var raw = UpdateBuilder.ToRaw(14, id => new
        {
            update_id = id,
            message = new
            {
                message_id = id,
                date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                from = new { id = 42, is_bot = false, first_name = "T" },
                chat = new { id = 42, type = "private" },
                photo = new[]
                {
                    new { file_id = "tiny", width = 50, height = 50 },
                    new { file_id = "medium_small", width = 150, height = 150 }
                }
            }
        });

        var item = _normalizer.Normalize(raw, _now);
        var imageItem = Assert.IsType<ImagePayloadItem>(item);
        Assert.Equal("medium_small", imageItem.FileId);
    }

    [Fact]
    public void Normalize_Document_ReturnsFilePayloadItem()
    {
        var raw = UpdateBuilder.ToRaw(15, UpdateBuilder.Document(42, "report.pdf", 2_100_000, caption: "Q3 financial"));
        var item = _normalizer.Normalize(raw, _now);

        var fileItem = Assert.IsType<FilePayloadItem>(item);
        Assert.Equal("report.pdf", fileItem.FileName);
        Assert.Equal(2_100_000, fileItem.FileSize);
        Assert.Equal("application/octet-stream", fileItem.MimeType);
        Assert.Equal("Q3 financial", fileItem.Caption);
        Assert.False(fileItem.IsOutgoing);
    }

    [Fact]
    public void Normalize_Video_ReturnsFilePayloadItem()
    {
        var raw = UpdateBuilder.ToRaw(16, UpdateBuilder.Video(42, "clip.mp4", 5_000_000, caption: "funny video"));
        var item = _normalizer.Normalize(raw, _now);

        var fileItem = Assert.IsType<FilePayloadItem>(item);
        Assert.Equal("clip.mp4", fileItem.FileName);
        Assert.Equal(5_000_000, fileItem.FileSize);
        Assert.Equal("video/mp4", fileItem.MimeType);
        Assert.Equal("funny video", fileItem.Caption);
    }

    [Fact]
    public void Normalize_Audio_ReturnsFilePayloadItem()
    {
        var raw = UpdateBuilder.ToRaw(17, UpdateBuilder.Audio(42, "SongTitle", 3_500_000));
        var item = _normalizer.Normalize(raw, _now);

        var fileItem = Assert.IsType<FilePayloadItem>(item);
        Assert.Equal("SongTitle.mp3", fileItem.FileName);
        Assert.Equal(3_500_000, fileItem.FileSize);
        Assert.Equal("audio/mpeg", fileItem.MimeType);
    }

    [Fact]
    public void Normalize_Voice_ReturnsFilePayloadItem()
    {
        var raw = UpdateBuilder.ToRaw(18, UpdateBuilder.Voice(42));
        var item = _normalizer.Normalize(raw, _now);

        var fileItem = Assert.IsType<FilePayloadItem>(item);
        Assert.Equal("voice.ogg", fileItem.FileName);
        Assert.Equal("audio/ogg", fileItem.MimeType);
        Assert.Equal(5_000, fileItem.FileSize);
    }

    [Fact]
    public void Normalize_Animation_ReturnsFilePayloadItem()
    {
        var raw = UpdateBuilder.ToRaw(19, id => new
        {
            update_id = id,
            message = new
            {
                message_id = id,
                date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                from = new { id = 42, is_bot = false, first_name = "T" },
                chat = new { id = 42, type = "private" },
                animation = new
                {
                    file_id = "anim_1",
                    file_name = "cool.gif",
                    file_size = 800_000,
                    mime_type = "video/mp4"
                }
            }
        });

        var item = _normalizer.Normalize(raw, _now);
        var fileItem = Assert.IsType<FilePayloadItem>(item);
        Assert.Equal("cool.gif", fileItem.FileName);
        Assert.Equal(800_000, fileItem.FileSize);
    }

    [Theory]
    [InlineData("sticker", "[Sticker]")]
    [InlineData("location", "[Location]")]
    [InlineData("contact", "[Contact]")]
    [InlineData("poll", "[Poll]")]
    [InlineData("venue", "[Venue]")]
    [InlineData("dice", "[Dice]")]
    [InlineData("game", "[Game]")]
    public void Normalize_UnsupportedTypes_ReturnsTextWithNotice(string propertyName, string expectedNotice)
    {
        var raw = UpdateBuilder.ToRaw(20, id => new
        {
            update_id = id,
            message = new Dictionary<string, object>
            {
                ["message_id"] = id,
                ["date"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["from"] = new { id = 42, is_bot = false, first_name = "T" },
                ["chat"] = new { id = 42, type = "private" },
                [propertyName] = new { id = "sub_object" }
            }
        });

        var item = _normalizer.Normalize(raw, _now);
        var textItem = Assert.IsType<TextPayloadItem>(item);
        Assert.Equal(expectedNotice, textItem.Text);
    }

    [Fact]
    public void Normalize_MalformedOrEmptyJson_NeverThrows_ReturnsUnsupportedUpdate()
    {
        using var emptyDoc = JsonDocument.Parse("{}");
        var rawEmpty = new RawUpdate(99, emptyDoc.RootElement.Clone());

        var item = _normalizer.Normalize(rawEmpty, _now);
        var textItem = Assert.IsType<TextPayloadItem>(item);
        Assert.Equal("[Unsupported update]", textItem.Text);
    }

    [Fact]
    public void Normalize_Photo_WithNegativeWidths_SelectsLargestAvailable()
    {
        var raw = UpdateBuilder.ToRaw(21, id => new
        {
            update_id = id,
            message = new
            {
                message_id = id,
                date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                from = new { id = 42, is_bot = false, first_name = "T" },
                chat = new { id = 42, type = "private" },
                photo = new[]
                {
                    new { file_id = "p_neg50", width = -50, height = 50 },
                    new { file_id = "p_neg10", width = -10, height = 50 }
                }
            }
        });

        var item = _normalizer.Normalize(raw, _now);
        var imageItem = Assert.IsType<ImagePayloadItem>(item);
        Assert.Equal("p_neg10", imageItem.FileId);
    }

    [Fact]
    public void Normalize_Document_WithNonStringProperties_SafeFallback()
    {
        var raw = UpdateBuilder.ToRaw(22, id => new
        {
            update_id = id,
            message = new
            {
                message_id = id,
                date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                from = new { id = 42, is_bot = false, first_name = "T" },
                chat = new { id = 42, type = "private" },
                document = new
                {
                    file_id = 99999, // number instead of string!
                    file_name = true // bool instead of string!
                }
            }
        });

        var item = _normalizer.Normalize(raw, _now);
        var fileItem = Assert.IsType<FilePayloadItem>(item);
        Assert.Null(fileItem.FileId);
        Assert.Equal("document", fileItem.FileName);
    }

    [Fact]
    public void Normalize_VideoNote_ReturnsFilePayloadItem()
    {
        var raw = UpdateBuilder.ToRaw(23, id => new
        {
            update_id = id,
            message = new
            {
                message_id = id,
                date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                from = new { id = 42, is_bot = false, first_name = "T" },
                chat = new { id = 42, type = "private" },
                video_note = new
                {
                    file_id = "vn_123",
                    file_size = 120_000,
                    duration = 10
                }
            }
        });

        var item = _normalizer.Normalize(raw, _now);
        var fileItem = Assert.IsType<FilePayloadItem>(item);
        Assert.Equal("vn_123", fileItem.FileId);
        Assert.Equal("video_note.mp4", fileItem.FileName);
        Assert.Equal(120_000, fileItem.FileSize);
        Assert.Equal("video/mp4", fileItem.MimeType);
    }

    [Fact]
    public void Normalize_MalformedDate_DoesNotThrow_UsesFallbackTimestamp()
    {
        var raw = UpdateBuilder.ToRaw(24, id => new
        {
            update_id = id,
            message = new
            {
                message_id = id,
                date = 999999999999999L, // out of range for DateTimeOffset
                from = new { id = 42, is_bot = false, first_name = "T" },
                chat = new { id = 42, type = "private" },
                text = "future message"
            }
        });

        var item = _normalizer.Normalize(raw, _now);
        var textItem = Assert.IsType<TextPayloadItem>(item);
        Assert.Equal(_now, textItem.Timestamp);
        Assert.Equal("future message", textItem.Text);
    }
}
