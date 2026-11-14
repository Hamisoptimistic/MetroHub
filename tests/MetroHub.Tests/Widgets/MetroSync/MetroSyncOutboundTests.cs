using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MetroHub.Tests;
using MetroHub.Tests.Widgets.MetroSync.TestKit;
using MetroHub.Widgets.Catalog.MetroSync.Models;
using MetroHub.Widgets.Catalog.MetroSync.Services;
using Xunit;

namespace MetroHub.Tests.Widgets.MetroSync;

[Collection("MetroSyncTests")]
public sealed class MetroSyncOutboundTests
{
    private static TelegramHttpTransport Http(FakeTelegramServer server, HttpClient client) =>
        new(client, server.BaseAddress, FakeTelegramServer.Token, allowInsecureLoopbackForTests: true);

    private static MemoryStream CreateTestPngStream(int width, int height)
    {
        int stride = width * 4;
        byte[] pixels = new byte[height * stride];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 180;     // B
            pixels[i + 1] = 120; // G
            pixels[i + 2] = 40;  // R
            pixels[i + 3] = 255; // A
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));

        var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;
        return ms;
    }

    private static BitmapSource CreateTestBitmapSource(int width, int height)
    {
        int stride = width * 4;
        byte[] pixels = new byte[height * stride];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 25;
            pixels[i + 1] = 200;
            pixels[i + 2] = 120;
            pixels[i + 3] = 255;
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    [Fact]
    public async Task SendTextAsync_ValidText_AppendsToHistoryAndReturnsOutgoingItem()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var client = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, client);

        using var sandbox = new WidgetTestSandbox();
        using var historyStore = new MetroSyncHistoryStore(sandbox.Root);
        var normalizer = new MetroSyncUpdateNormalizer();
        var thumbnails = new MetroSyncThumbnails(sandbox.Root);

        var outbound = new MetroSyncOutboundService(transport, normalizer, historyStore, thumbnails);

        var sent = await outbound.SendTextAsync(123456789, "Hello from MetroHub outbound!");

        Assert.NotNull(sent);
        Assert.True(sent.IsOutgoing);
        Assert.Equal("Hello from MetroHub outbound!", sent.Text);
        Assert.True(sent.MessageId > 0);

        var history = await historyStore.LoadLatestAsync(50);
        Assert.Single(history);
        var item = Assert.IsType<TextPayloadItem>(history[0]);
        Assert.True(item.IsOutgoing);
        Assert.Equal("Hello from MetroHub outbound!", item.Text);
        Assert.Equal(sent.MessageId, item.MessageId);
    }

    [Fact]
    public async Task SendTextAsync_LengthExceeds4096_ThrowsBeforeNetworkAndHistoryUntouched()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var client = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, client);
        var recording = new RecordingTransport(transport);

        using var sandbox = new WidgetTestSandbox();
        using var historyStore = new MetroSyncHistoryStore(sandbox.Root);
        var normalizer = new MetroSyncUpdateNormalizer();
        var thumbnails = new MetroSyncThumbnails(sandbox.Root);

        var outbound = new MetroSyncOutboundService(recording, normalizer, historyStore, thumbnails);

        var longText = new string('A', 4097);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            outbound.SendTextAsync(123456789, longText));

        Assert.Equal(0, recording.SendMessageCalls);
        var history = await historyStore.LoadLatestAsync(50);
        Assert.Empty(history);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SendTextAsync_EmptyOrWhitespace_ThrowsAndLeavesHistoryUntouched(string text)
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var client = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, client);

        using var sandbox = new WidgetTestSandbox();
        using var historyStore = new MetroSyncHistoryStore(sandbox.Root);
        var normalizer = new MetroSyncUpdateNormalizer();
        var thumbnails = new MetroSyncThumbnails(sandbox.Root);

        var outbound = new MetroSyncOutboundService(transport, normalizer, historyStore, thumbnails);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            outbound.SendTextAsync(123456789, text));

        var history = await historyStore.LoadLatestAsync(50);
        Assert.Empty(history);
    }

    [Fact]
    public async Task SendTextAsync_ServerFails_ThrowsAndLeavesHistoryUntouched()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        server.Chaos.Script("sendMessage", new RespondStatus(401, "Unauthorized"));

        using var client = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, client);

        using var sandbox = new WidgetTestSandbox();
        using var historyStore = new MetroSyncHistoryStore(sandbox.Root);
        var normalizer = new MetroSyncUpdateNormalizer();
        var thumbnails = new MetroSyncThumbnails(sandbox.Root);

        var outbound = new MetroSyncOutboundService(transport, normalizer, historyStore, thumbnails);

        await Assert.ThrowsAsync<TelegramApiException>(() =>
            outbound.SendTextAsync(123456789, "Test message that will fail"));

        var history = await historyStore.LoadLatestAsync(50);
        Assert.Empty(history);
    }

    [Fact]
    public async Task SendPhotoAsync_ValidStream_SendsPhotoGeneratesThumbnailAndAppendsToHistory()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var client = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, client);

        using var sandbox = new WidgetTestSandbox();
        using var historyStore = new MetroSyncHistoryStore(sandbox.Root);
        var normalizer = new MetroSyncUpdateNormalizer();
        var thumbnails = new MetroSyncThumbnails(sandbox.Root);

        var outbound = new MetroSyncOutboundService(transport, normalizer, historyStore, thumbnails);

        using var photoStream = CreateTestPngStream(500, 300);

        var sent = await outbound.SendPhotoAsync(123456789, photoStream, caption: "Sunset photo");

        Assert.NotNull(sent);
        Assert.True(sent.IsOutgoing);
        Assert.Equal("Sunset photo", sent.Caption);
        Assert.True(sent.MessageId > 0);
        Assert.NotNull(sent.ThumbnailFilePath);
        Assert.True(File.Exists(sent.ThumbnailFilePath));

        // Check thumbnail properties
        var loadedThumb = MetroSyncThumbnails.LoadThumbnail(sent.ThumbnailFilePath);
        Assert.NotNull(loadedThumb);
        Assert.True(loadedThumb.IsFrozen);
        Assert.True(loadedThumb.PixelWidth <= 200);

        // Check history
        var history = await historyStore.LoadLatestAsync(50);
        Assert.Single(history);
        var item = Assert.IsType<ImagePayloadItem>(history[0]);
        Assert.True(item.IsOutgoing);
        Assert.Equal("Sunset photo", item.Caption);
        Assert.Equal(sent.ThumbnailFilePath, item.ThumbnailFilePath);
    }

    [Fact]
    public async Task SendPhotoAsync_StreamOver10MB_ThrowsBeforeNetworkAndHistoryUntouched()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var client = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, client);
        var recording = new RecordingTransport(transport);

        using var sandbox = new WidgetTestSandbox();
        using var historyStore = new MetroSyncHistoryStore(sandbox.Root);
        var normalizer = new MetroSyncUpdateNormalizer();
        var thumbnails = new MetroSyncThumbnails(sandbox.Root);

        var outbound = new MetroSyncOutboundService(recording, normalizer, historyStore, thumbnails);

        const int overLimit = 10 * 1024 * 1024 + 1; // 10 MB + 1 byte
        using var bigStream = new MemoryStream(new byte[overLimit]);

        await Assert.ThrowsAsync<FileTooLargeException>(() =>
            outbound.SendPhotoAsync(123456789, bigStream, "Too big"));

        Assert.Equal(0, recording.SendPhotoCalls);
        var history = await historyStore.LoadLatestAsync(50);
        Assert.Empty(history);
    }

    [Fact]
    public async Task SendPhotoAsync_ServerFails_ThrowsAndLeavesHistoryEmpty()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        server.Chaos.Script("sendPhoto", new RespondStatus(500, "Internal Server Error"));

        using var client = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, client);

        using var sandbox = new WidgetTestSandbox();
        using var historyStore = new MetroSyncHistoryStore(sandbox.Root);
        var normalizer = new MetroSyncUpdateNormalizer();
        var thumbnails = new MetroSyncThumbnails(sandbox.Root);

        var outbound = new MetroSyncOutboundService(transport, normalizer, historyStore, thumbnails);

        using var photoStream = CreateTestPngStream(100, 100);

        await Assert.ThrowsAsync<TelegramApiException>(() =>
            outbound.SendPhotoAsync(123456789, photoStream, "Will fail"));

        var history = await historyStore.LoadLatestAsync(50);
        Assert.Empty(history);
    }

    [Fact]
    public void SendPhotoAsync_BitmapSource_EncodesAndAppendsToHistory()
    {
        WpfTestHost.RunSta(() =>
        {
            Task.Run(async () =>
            {
                await using var server = await FakeTelegramServer.StartAsync();
                using var client = TelegramHttpTransport.CreateDedicatedClient();
                var transport = Http(server, client);

                using var sandbox = new WidgetTestSandbox();
                using var historyStore = new MetroSyncHistoryStore(sandbox.Root);
                var normalizer = new MetroSyncUpdateNormalizer();
                var thumbnails = new MetroSyncThumbnails(sandbox.Root);

                var outbound = new MetroSyncOutboundService(transport, normalizer, historyStore, thumbnails);

                var bitmap = CreateTestBitmapSource(400, 300);

                var sent = await outbound.SendPhotoAsync(123456789, bitmap, "Clipboard test");

                Assert.NotNull(sent);
                Assert.True(sent.IsOutgoing);
                Assert.Equal("Clipboard test", sent.Caption);
                Assert.True(sent.MessageId > 0);
                Assert.NotNull(sent.ThumbnailFilePath);
                Assert.True(File.Exists(sent.ThumbnailFilePath));

                var history = await historyStore.LoadLatestAsync(50);
                Assert.Single(history);
                var item = Assert.IsType<ImagePayloadItem>(history[0]);
                Assert.True(item.IsOutgoing);
                Assert.Equal("Clipboard test", item.Caption);
            }).GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void NormalizeOutbound_TextStartCommand_DoesNotSuppressOutgoingStart()
    {
        var normalizer = new MetroSyncUpdateNormalizer();

        var json = "{\"message_id\":1234,\"date\":1730000000,\"text\":\"/start\"}";
        using var doc = JsonDocument.Parse(json);

        var item = normalizer.NormalizeOutbound(doc.RootElement, DateTimeOffset.UtcNow);

        Assert.NotNull(item);
        var textItem = Assert.IsType<TextPayloadItem>(item);
        Assert.True(textItem.IsOutgoing);
        Assert.Equal("/start", textItem.Text);
        Assert.Equal(1234, textItem.MessageId);
    }

    [Fact]
    public void NormalizeOutbound_VariousTypes_SetsIsOutgoingTrue()
    {
        var normalizer = new MetroSyncUpdateNormalizer();

        // 1. Text
        var textJson = "{\"message_id\":10,\"date\":1730000000,\"text\":\"hello\"}";
        using var doc1 = JsonDocument.Parse(textJson);
        var item1 = normalizer.NormalizeOutbound(doc1.RootElement, DateTimeOffset.UtcNow);
        Assert.NotNull(item1);
        Assert.True(item1.IsOutgoing);
        Assert.IsType<TextPayloadItem>(item1);

        // 2. Photo
        var photoJson = "{\"message_id\":20,\"date\":1730000000,\"photo\":[{\"file_id\":\"f1\",\"width\":300,\"height\":200}],\"caption\":\"cap\"}";
        using var doc2 = JsonDocument.Parse(photoJson);
        var item2 = normalizer.NormalizeOutbound(doc2.RootElement, DateTimeOffset.UtcNow);
        Assert.NotNull(item2);
        Assert.True(item2.IsOutgoing);
        var photoItem = Assert.IsType<ImagePayloadItem>(item2);
        Assert.Equal("cap", photoItem.Caption);
        Assert.Equal("f1", photoItem.FileId);

        // 3. Document
        var docJson = "{\"message_id\":30,\"date\":1730000000,\"document\":{\"file_id\":\"d1\",\"file_name\":\"report.pdf\",\"file_size\":1024}}";
        using var doc3 = JsonDocument.Parse(docJson);
        var item3 = normalizer.NormalizeOutbound(doc3.RootElement, DateTimeOffset.UtcNow);
        Assert.NotNull(item3);
        Assert.True(item3.IsOutgoing);
        var fileItem = Assert.IsType<FilePayloadItem>(item3);
        Assert.Equal("report.pdf", fileItem.FileName);

        // 4. Sticker
        var stickerJson = "{\"message_id\":40,\"date\":1730000000,\"sticker\":{\"file_id\":\"s1\"}}";
        using var doc4 = JsonDocument.Parse(stickerJson);
        var item4 = normalizer.NormalizeOutbound(doc4.RootElement, DateTimeOffset.UtcNow);
        Assert.NotNull(item4);
        Assert.True(item4.IsOutgoing);
        var stickerItem = Assert.IsType<ImagePayloadItem>(item4);
        Assert.True(stickerItem.IsSticker);
        Assert.Equal("s1", stickerItem.FileId);

        // 5. Unsupported
        var locationJson = "{\"message_id\":50,\"date\":1730000000,\"location\":{\"latitude\":10.0,\"longitude\":20.0}}";
        using var doc5 = JsonDocument.Parse(locationJson);
        var item5 = normalizer.NormalizeOutbound(doc5.RootElement, DateTimeOffset.UtcNow);
        Assert.NotNull(item5);
        Assert.True(item5.IsOutgoing);
        var unsupportedItem = Assert.IsType<TextPayloadItem>(item5);
        Assert.Equal("[Location]", unsupportedItem.Text);
    }

    [Fact]
    public async Task SendPhotoAsync_NonSeekableStreamOver10MB_ThrowsFileTooLargeException()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var client = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, client);

        using var sandbox = new WidgetTestSandbox();
        using var historyStore = new MetroSyncHistoryStore(sandbox.Root);
        var normalizer = new MetroSyncUpdateNormalizer();
        var thumbnails = new MetroSyncThumbnails(sandbox.Root);

        var outbound = new MetroSyncOutboundService(transport, normalizer, historyStore, thumbnails);

        // 10 MB + 100 bytes non-seekable stream
        var bigData = new byte[10 * 1024 * 1024 + 100];
        using var nonSeekable = new NonSeekableReadStream(bigData);

        await Assert.ThrowsAsync<FileTooLargeException>(() =>
            outbound.SendPhotoAsync(123456789, nonSeekable));

        var history = await historyStore.LoadLatestAsync(50);
        Assert.Empty(history);
    }

    [Fact]
    public async Task TelegramHttpTransport_SendMessageAsync_AllowsNegativeGroupChatId()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var client = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, client);

        // Telegram group and supergroup chats have negative IDs (e.g. -100123456789)
        var doc = await transport.SendMessageAsync(-100123456789, "Group broadcast", CancellationToken.None);
        using (doc)
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        }
    }

    [Fact]
    public async Task TelegramHttpTransport_SendPhotoAsync_AllowsNegativeGroupChatId()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var client = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, client);

        using var stream = CreateTestPngStream(100, 100);
        var doc = await transport.SendPhotoAsync(-100123456789, stream, "Group photo", CancellationToken.None);
        using (doc)
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        }
    }

    [Fact]
    public void EnsureThreadSafeBitmap_UnfrozenBitmapOnWorkerThread_MarshalsViaDispatcher()
    {
        WpfTestHost.RunSta(() =>
        {
            // Create unfrozen bitmap on STA thread with Dispatcher
            var unfrozen = new RenderTargetBitmap(50, 50, 96, 96, PixelFormats.Pbgra32);
            Assert.False(unfrozen.IsFrozen);
            Assert.NotNull(unfrozen.Dispatcher);

            var frame = new System.Windows.Threading.DispatcherFrame();
            BitmapSource? safe = null;
            Exception? error = null;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    safe = MetroSyncThumbnails.EnsureThreadSafeBitmap(unfrozen);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    frame.Continue = false;
                }
            });

            System.Windows.Threading.Dispatcher.PushFrame(frame);

            if (error is not null)
            {
                throw error;
            }

            Assert.NotNull(safe);
            Assert.True(safe.IsFrozen);
        });
    }

    [Fact]
    public async Task SendDocumentAsync_ValidFile_UploadsAndAppendsFilePayloadItemWithLocalPath()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var client = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, client);

        using var sandbox = new WidgetTestSandbox();
        using var historyStore = new MetroSyncHistoryStore(sandbox.Root);
        var normalizer = new MetroSyncUpdateNormalizer();
        var thumbnails = new MetroSyncThumbnails(sandbox.Root);

        var outbound = new MetroSyncOutboundService(transport, normalizer, historyStore, thumbnails);

        var testFile = Path.Combine(sandbox.Root, "test_doc.pdf");
        await File.WriteAllTextAsync(testFile, "%PDF-1.4 test document content");

        var sent = await outbound.SendDocumentAsync(123456789, testFile, caption: "My PDF Report");

        Assert.NotNull(sent);
        Assert.True(sent.IsOutgoing);
        Assert.Equal("test_doc.pdf", sent.FileName);
        Assert.Equal(testFile, sent.LocalPath);
        Assert.True(sent.IsDownloaded);
        Assert.Equal("My PDF Report", sent.Caption);
        Assert.True(sent.MessageId > 0);
        Assert.Equal(1, server.Requests("sendDocument"));

        var history = await historyStore.LoadLatestAsync(10);
        Assert.Single(history);
        var storedItem = Assert.IsType<FilePayloadItem>(history[0]);
        Assert.Equal("test_doc.pdf", storedItem.FileName);
        Assert.Equal(testFile, storedItem.LocalPath);
        Assert.True(storedItem.IsDownloaded);
    }

    [Fact]
    public async Task SendDocumentAsync_EmptyFile_ThrowsBeforeNetworkAndHistoryUntouched()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var client = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, client);
        var recording = new RecordingTransport(transport);

        using var sandbox = new WidgetTestSandbox();
        using var historyStore = new MetroSyncHistoryStore(sandbox.Root);
        var normalizer = new MetroSyncUpdateNormalizer();
        var thumbnails = new MetroSyncThumbnails(sandbox.Root);

        var outbound = new MetroSyncOutboundService(recording, normalizer, historyStore, thumbnails);

        var emptyFile = Path.Combine(sandbox.Root, "empty.txt");
        File.WriteAllBytes(emptyFile, Array.Empty<byte>());

        await Assert.ThrowsAsync<ArgumentException>(() => outbound.SendDocumentAsync(123456789, emptyFile));

        Assert.Equal(0, recording.SendDocumentCalls);
        var history = await historyStore.LoadLatestAsync(10);
        Assert.Empty(history);
    }

    [Fact]
    public async Task SendDocumentAsync_Exceeds50MB_ThrowsFileTooLargeBeforeNetwork()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var client = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, client);
        var recording = new RecordingTransport(transport);

        using var sandbox = new WidgetTestSandbox();
        using var historyStore = new MetroSyncHistoryStore(sandbox.Root);
        var normalizer = new MetroSyncUpdateNormalizer();
        var thumbnails = new MetroSyncThumbnails(sandbox.Root);

        var outbound = new MetroSyncOutboundService(recording, normalizer, historyStore, thumbnails);

        var largeFile = Path.Combine(sandbox.Root, "large.dat");
        using (var fs = File.Create(largeFile))
        {
            fs.SetLength(51 * 1024 * 1024);
        }

        await Assert.ThrowsAsync<FileTooLargeException>(() => outbound.SendDocumentAsync(123456789, largeFile));

        Assert.Equal(0, recording.SendDocumentCalls);
        var history = await historyStore.LoadLatestAsync(10);
        Assert.Empty(history);
    }

    [Fact]
    public async Task SendFileAsync_ImageUnder10MB_RoutesToSendPhoto()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var client = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, client);

        using var sandbox = new WidgetTestSandbox();
        using var historyStore = new MetroSyncHistoryStore(sandbox.Root);
        var normalizer = new MetroSyncUpdateNormalizer();
        var thumbnails = new MetroSyncThumbnails(sandbox.Root);

        var outbound = new MetroSyncOutboundService(transport, normalizer, historyStore, thumbnails);

        var imagePath = Path.Combine(sandbox.Root, "photo.png");
        using (var pngStream = CreateTestPngStream(50, 50))
        using (var fs = File.Create(imagePath))
        {
            pngStream.CopyTo(fs);
        }

        var sent = await outbound.SendFileAsync(123456789, imagePath);

        Assert.IsType<ImagePayloadItem>(sent);
        Assert.Equal(1, server.Requests("sendPhoto"));
        Assert.Equal(0, server.Requests("sendDocument"));
    }

    [Fact]
    public async Task SendFileAsync_NonImage_RoutesToSendDocument()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var client = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, client);

        using var sandbox = new WidgetTestSandbox();
        using var historyStore = new MetroSyncHistoryStore(sandbox.Root);
        var normalizer = new MetroSyncUpdateNormalizer();
        var thumbnails = new MetroSyncThumbnails(sandbox.Root);

        var outbound = new MetroSyncOutboundService(transport, normalizer, historyStore, thumbnails);

        var docPath = Path.Combine(sandbox.Root, "archive.zip");
        await File.WriteAllBytesAsync(docPath, [1, 2, 3, 4]);

        var sent = await outbound.SendFileAsync(123456789, docPath);

        Assert.IsType<FilePayloadItem>(sent);
        Assert.Equal(0, server.Requests("sendPhoto"));
        Assert.Equal(1, server.Requests("sendDocument"));
    }

    private sealed class NonSeekableReadStream(byte[] data) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int available = Math.Min(count, data.Length - _position);
            if (available <= 0) return 0;
            Array.Copy(data, _position, buffer, offset, available);
            _position += available;
            return available;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
