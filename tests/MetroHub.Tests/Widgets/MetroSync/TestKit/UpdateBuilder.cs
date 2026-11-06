using System.Text.Json;
using MetroHub.Widgets.Catalog.MetroSync.Services;

namespace MetroHub.Tests.Widgets.MetroSync.TestKit;

/// <summary>
/// Builders for Telegram-shaped update JSON. Each returns a function of update_id so the
/// fake server can assign ids. Property names intentionally use Telegram's snake_case.
/// </summary>
public static class UpdateBuilder
{
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static Func<long, object> Text(long fromId, string text, string chatType = "private", long? chatId = null) =>
        id => new
        {
            update_id = id,
            message = new
            {
                message_id = id,
                date = Now(),
                from = new { id = fromId, is_bot = false, first_name = "T" },
                chat = new { id = chatId ?? fromId, type = chatType },
                text,
            },
        };

    public static Func<long, object> UrlMessage(long fromId, string url) =>
        id => new
        {
            update_id = id,
            message = new
            {
                message_id = id,
                date = Now(),
                from = new { id = fromId, is_bot = false, first_name = "T" },
                chat = new { id = fromId, type = "private" },
                text = url,
                entities = new[] { new { type = "url", offset = 0, length = url.Length } },
            },
        };

    public static Func<long, object> Photo(long fromId, string? caption = null) =>
        id => new
        {
            update_id = id,
            message = new
            {
                message_id = id,
                date = Now(),
                from = new { id = fromId, is_bot = false, first_name = "T" },
                chat = new { id = fromId, type = "private" },
                caption,
                photo = new[]
                {
                    new { file_id = "p51", file_unique_id = "u51", width = 51, height = 51, file_size = 1_000 },
                    new { file_id = "p180", file_unique_id = "u180", width = 180, height = 180, file_size = 12_000 },
                    new { file_id = "p450", file_unique_id = "u450", width = 450, height = 450, file_size = 60_000 },
                    new { file_id = "p1280", file_unique_id = "u1280", width = 1280, height = 1280, file_size = 400_000 },
                },
            },
        };

    public static Func<long, object> Document(long fromId, string fileName, long size, string? caption = null) =>
        id => new
        {
            update_id = id,
            message = new
            {
                message_id = id,
                date = Now(),
                from = new { id = fromId, is_bot = false, first_name = "T" },
                chat = new { id = fromId, type = "private" },
                caption,
                document = new
                {
                    file_id = "d" + id,
                    file_unique_id = "ud" + id,
                    file_name = fileName,
                    mime_type = "application/octet-stream",
                    file_size = size,
                },
            },
        };

    public static Func<long, object> Video(long fromId, string fileName, long size, string? caption = null) =>
        id => new
        {
            update_id = id,
            message = new
            {
                message_id = id,
                date = Now(),
                from = new { id = fromId, is_bot = false, first_name = "T" },
                chat = new { id = fromId, type = "private" },
                caption,
                video = new
                {
                    file_id = "v" + id,
                    file_unique_id = "uv" + id,
                    file_name = fileName,
                    mime_type = "video/mp4",
                    file_size = size,
                },
            },
        };

    public static Func<long, object> Audio(long fromId, string? title, long size, string? caption = null) =>
        id => new
        {
            update_id = id,
            message = new
            {
                message_id = id,
                date = Now(),
                from = new { id = fromId, is_bot = false, first_name = "T" },
                chat = new { id = fromId, type = "private" },
                caption,
                audio = new
                {
                    file_id = "a" + id,
                    file_unique_id = "ua" + id,
                    title,
                    mime_type = "audio/mpeg",
                    file_size = size,
                },
            },
        };

    public static Func<long, object> Voice(long fromId, string? caption = null) =>
        id => new
        {
            update_id = id,
            message = new
            {
                message_id = id,
                date = Now(),
                from = new { id = fromId, is_bot = false, first_name = "T" },
                chat = new { id = fromId, type = "private" },
                caption,
                voice = new { file_id = "v" + id, file_unique_id = "uv" + id, duration = 3, mime_type = "audio/ogg", file_size = 5_000 },
            },
        };

    public static Func<long, object> ChannelPost(long channelId) =>
        id => new
        {
            update_id = id,
            channel_post = new
            {
                message_id = id,
                date = Now(),
                chat = new { id = channelId, type = "channel" },
                text = "channel post",
            },
        };

    public static Func<long, object> EditedText(long fromId, string text) =>
        id => new
        {
            update_id = id,
            edited_message = new
            {
                message_id = id,
                date = Now(),
                from = new { id = fromId, is_bot = false, first_name = "T" },
                chat = new { id = fromId, type = "private" },
                text,
            },
        };

    /// <summary>
    /// Everything the Iron Shield must drop: other users, right user in the wrong chat type,
    /// spoof-shaped ids, other update types, and malformed shapes. None may reach the index.
    /// </summary>
    public static IReadOnlyList<Func<long, object>> HostileCorpus(long allowedId, long strangerId) =>
    [
        Text(strangerId, "hello"),
        Text(strangerId, "/start 12345678"),
        Text(allowedId, "right user, group chat", "group", -100123),
        Text(allowedId, "right user, supergroup", "supergroup", -100456),
        Text(strangerId, "stranger posing via chat id", "private", allowedId),
        Text(allowedId, "from matches, chat differs", "private", strangerId),
        ChannelPost(-100789),
        EditedText(allowedId, "edited message is not a message"),
        Photo(strangerId),
        Document(strangerId, "payload.exe", 10),
        Voice(strangerId),
        id => new { update_id = id, message = "not-an-object" },
        id => new { update_id = id, message = new { chat = new { id = "string-id", type = "private" }, from = new { id = allowedId } } },
        id => new { update_id = id, message = new { chat = new { id = allowedId, type = "private" } } }, // no "from"
        id => new { update_id = id },
    ];

    public static RawUpdate ToRaw(long updateId, Func<long, object> build)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(build(updateId)));
        return new RawUpdate(updateId, doc.RootElement.Clone());
    }
}
