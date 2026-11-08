using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MetroHub.Core.Models;
using MetroHub.Tests;
using MetroHub.Tests.Widgets.MetroSync.TestKit;
using MetroHub.Widgets;
using MetroHub.Widgets.Catalog.MetroSync;
using MetroHub.Widgets.Catalog.MetroSync.Models;
using MetroHub.Widgets.Catalog.MetroSync.Services;
using Xunit;

namespace MetroHub.Tests.Widgets.MetroSync;

[Collection("MetroSyncTests")]
public sealed class MetroSyncWidgetViewModelTests
{
    private const string BotToken = "123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11";
    private const long AllowedUserId = 987654321;

    private static TelegramHttpTransport Http(FakeTelegramServer server, HttpClient client) =>
        new(client, server.BaseAddress, FakeTelegramServer.Token, allowInsecureLoopbackForTests: true);

    private static TileModel CreateTestTile() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Title = "MetroSync Test",
        TileType = TileType.Widget,
        SpanX = 8,
        SpanY = 6
    };

    private static BitmapSource CreateFrozenBitmap(int width = 100, int height = 100)
    {
        int stride = width * 4;
        byte[] pixels = new byte[height * stride];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 200;
            pixels[i + 1] = 100;
            pixels[i + 2] = 50;
            pixels[i + 3] = 255;
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    [Fact]
    public async Task Initialization_WithExistingCredentials_LoadsLatest50Items_AndStartsEngine()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);
        using var historyStore = new MetroSyncHistoryStore(sandbox.Root);
        var thumbnails = new MetroSyncThumbnails(sandbox.Root);

        security.SaveCredentials(BotToken, AllowedUserId);

        // Prepopulate history with 60 items
        for (int i = 1; i <= 60; i++)
        {
            await historyStore.AppendAsync(new TextPayloadItem
            {
                UpdateId = i,
                MessageId = 1000 + i,
                Text = $"Message #{i}",
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(i)
            });
        }

        var transport = new ScriptedTransport();
        var tile = CreateTestTile();

        using var vm = new MetroSyncWidgetViewModel(
            tile,
            security: security,
            historyStore: historyStore,
            thumbnails: thumbnails,
            transport: transport,
            baseDirectory: sandbox.Root);

        await vm.InitializationTask;

        Assert.True(vm.IsPaired);
        Assert.Equal(50, vm.Items.Count);
        Assert.Equal("Message #11", ((TextPayloadItem)vm.Items[0]).Text);
        Assert.Equal("Message #60", ((TextPayloadItem)vm.Items[^1]).Text);
        Assert.True(vm.IsConnected);
    }

    [Fact]
    public async Task Initialization_WithoutCredentials_TransitionsToUnpaired_WithoutAutoSetupRequest()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);
        using var historyStore = new MetroSyncHistoryStore(sandbox.Root);
        var thumbnails = new MetroSyncThumbnails(sandbox.Root);

        bool setupRequested = false;
        var tile = CreateTestTile();

        using var vm = new MetroSyncWidgetViewModel(
            tile,
            security: security,
            historyStore: historyStore,
            thumbnails: thumbnails,
            baseDirectory: sandbox.Root,
            requestSetupDialog: () => setupRequested = true);

        await vm.InitializationTask;

        Assert.False(vm.IsPaired);
        Assert.True(vm.CanConnect);
        Assert.False(vm.IsConnected);
        Assert.Equal(EngineState.Stopped, vm.EngineState);
        Assert.Empty(vm.Items);
        Assert.False(setupRequested);

        // Explicit user action opens setup
        vm.OpenSetupCommand.Execute(null);
        Assert.True(setupRequested);
    }

    [Fact]
    public async Task SendCommand_ValidText_SendsOutbound_ClearsInput_AddsToFeed()
    {
        using var sandbox = new WidgetTestSandbox();
        await using var server = await FakeTelegramServer.StartAsync();
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);

        var security = new MetroSyncSecurity(sandbox.Root);
        using var historyStore = new MetroSyncHistoryStore(sandbox.Root);
        var thumbnails = new MetroSyncThumbnails(sandbox.Root);

        security.SaveCredentials(FakeTelegramServer.Token, AllowedUserId);

        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            security: security,
            historyStore: historyStore,
            thumbnails: thumbnails,
            transport: transport,
            baseDirectory: sandbox.Root);

        await vm.InitializationTask;

        vm.InputText = "Hello from MetroSync test!";
        Assert.True(vm.CanSend);

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Empty(vm.InputText);
        Assert.Null(vm.ErrorMessage);
        Assert.NotEmpty(vm.Items);

        var lastItem = Assert.IsType<TextPayloadItem>(vm.Items[^1]);
        Assert.True(lastItem.IsOutgoing);
        Assert.Equal("Hello from MetroSync test!", lastItem.Text);
        Assert.Equal(1, server.Requests("sendMessage"));
    }

    [Fact]
    public async Task SendCommand_EmptyOrWhitespace_DoesNotExecute()
    {
        using var sandbox = new WidgetTestSandbox();
        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(tile, baseDirectory: sandbox.Root);
        await vm.InitializationTask;

        vm.InputText = "    ";
        Assert.False(vm.CanSend);

        await vm.SendCommand.ExecuteAsync(null);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task CanSend_RequiresPairedAndNonEmptyInput()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);
        var tile = CreateTestTile();

        // 1. Unpaired: CanSend must be false regardless of text
        using var vmUnpaired = new MetroSyncWidgetViewModel(tile, security: security, baseDirectory: sandbox.Root);
        await vmUnpaired.InitializationTask;
        Assert.False(vmUnpaired.IsPaired);

        vmUnpaired.InputText = "Valid text";
        Assert.False(vmUnpaired.CanSend);

        // 2. Paired: CanSend is true only when text is non-empty
        security.SaveCredentials(BotToken, AllowedUserId);
        using var vmPaired = new MetroSyncWidgetViewModel(
            tile,
            security: security,
            transport: new ScriptedTransport(),
            baseDirectory: sandbox.Root);
        await vmPaired.InitializationTask;
        Assert.True(vmPaired.IsPaired);

        vmPaired.InputText = string.Empty;
        Assert.False(vmPaired.CanSend);

        vmPaired.InputText = "Hello!";
        Assert.True(vmPaired.CanSend);
    }

    [Fact]
    public async Task SendCommand_Exceeds4096Chars_SetsErrorMessage_PreservesInput()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);
        security.SaveCredentials(BotToken, AllowedUserId);

        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            security: security,
            transport: new ScriptedTransport(),
            baseDirectory: sandbox.Root);

        await vm.InitializationTask;

        string longText = new('A', 4097);
        vm.InputText = longText;
        Assert.True(vm.CanSend);

        await vm.SendCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("4096", vm.ErrorMessage);
        Assert.Equal(longText, vm.InputText);
        Assert.Empty(vm.Items);
    }

    private sealed class FailingOutboundTransport : ITelegramTransport
    {
        public Task<TelegramBotUser> GetMeAsync(CancellationToken ct) =>
            Task.FromResult(new TelegramBotUser(1, true, "bot", "bot"));

        public Task<IReadOnlyList<RawUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct) =>
            Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => (IReadOnlyList<RawUpdate>)Array.Empty<RawUpdate>(), ct);

        public Task<TelegramFileInfo> GetFileAsync(string fileId, CancellationToken ct) => throw new NotSupportedException();
        public Task DownloadAsync(string filePath, Stream destination, long maxBytes, CancellationToken ct) => throw new NotSupportedException();

        public Task<JsonDocument> SendMessageAsync(long chatId, string text, CancellationToken ct) =>
            throw new TelegramApiException(500, $"Internal server error for {BotToken}");

        public Task<JsonDocument> SendPhotoAsync(long chatId, Stream imageStream, string? caption, CancellationToken ct) =>
            throw new TelegramApiException(500, $"Internal server error for {BotToken}");

        public Task<JsonDocument> SendDocumentAsync(long chatId, Stream documentStream, string fileName, string? caption, CancellationToken ct) =>
            throw new TelegramApiException(500, $"Internal server error for {BotToken}");
    }

    [Fact]
    public async Task SendCommand_OnTransportError_PreservesInput_SetsErrorMessage_RedactsToken()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);
        security.SaveCredentials(BotToken, AllowedUserId);

        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            security: security,
            transport: new FailingOutboundTransport(),
            baseDirectory: sandbox.Root);

        await vm.InitializationTask;

        vm.InputText = "Failed message";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.DoesNotContain(BotToken, vm.ErrorMessage);
        Assert.Equal("Failed message", vm.InputText);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task SendClipboardImageCommand_WithImage_SendsPhoto_AddsToFeed()
    {
        using var sandbox = new WidgetTestSandbox();
        await using var server = await FakeTelegramServer.StartAsync();
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);

        var security = new MetroSyncSecurity(sandbox.Root);
        using var historyStore = new MetroSyncHistoryStore(sandbox.Root);
        var thumbnails = new MetroSyncThumbnails(sandbox.Root);

        security.SaveCredentials(FakeTelegramServer.Token, AllowedUserId);

        var testBmp = CreateFrozenBitmap(120, 120);

        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            security: security,
            historyStore: historyStore,
            thumbnails: thumbnails,
            transport: transport,
            getClipboardImageAction: () => testBmp,
            baseDirectory: sandbox.Root);

        await vm.InitializationTask;

        Assert.True(vm.CanSendClipboardImage);

        await vm.SendClipboardImageCommand.ExecuteAsync(null);

        Assert.Null(vm.ErrorMessage);
        Assert.NotEmpty(vm.Items);
        var lastItem = Assert.IsType<ImagePayloadItem>(vm.Items[^1]);
        Assert.True(lastItem.IsOutgoing);
        Assert.NotNull(lastItem.ThumbnailFilePath);
        Assert.True(File.Exists(lastItem.ThumbnailFilePath));
        Assert.Equal(1, server.Requests("sendPhoto"));
    }

    [Fact]
    public async Task SendClipboardImageCommand_NoImageOnClipboard_SetsErrorMessage()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);
        security.SaveCredentials(BotToken, AllowedUserId);

        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            security: security,
            transport: new ScriptedTransport(),
            getClipboardImageAction: () => null,
            baseDirectory: sandbox.Root);

        await vm.InitializationTask;

        await vm.SendClipboardImageCommand.ExecuteAsync(null);

        Assert.Equal("No image found on clipboard.", vm.ErrorMessage);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task AttachFilesCommand_MultipleFiles_SendsSequentiallyAndAddsToFeed()
    {
        using var sandbox = new WidgetTestSandbox();
        await using var server = await FakeTelegramServer.StartAsync();
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);

        var security = new MetroSyncSecurity(sandbox.Root);
        using var historyStore = new MetroSyncHistoryStore(sandbox.Root);
        var thumbnails = new MetroSyncThumbnails(sandbox.Root);

        security.SaveCredentials(FakeTelegramServer.Token, AllowedUserId);

        var docPath = Path.Combine(sandbox.Root, "report.pdf");
        await File.WriteAllTextAsync(docPath, "%PDF-1.4 report data");

        var txtPath = Path.Combine(sandbox.Root, "notes.txt");
        await File.WriteAllTextAsync(txtPath, "Hello from attached file");

        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            security: security,
            historyStore: historyStore,
            thumbnails: thumbnails,
            transport: transport,
            pickFilesAction: () => [docPath, txtPath],
            baseDirectory: sandbox.Root);

        await vm.InitializationTask;

        Assert.True(vm.CanAttachFiles);

        await vm.AttachFilesCommand.ExecuteAsync(null);

        Assert.Null(vm.ErrorMessage);
        Assert.Equal(2, vm.Items.Count);
        Assert.Equal(2, server.Requests("sendDocument"));

        var first = Assert.IsType<FilePayloadItem>(vm.Items[0]);
        Assert.Equal("report.pdf", first.FileName);
        Assert.Equal(docPath, first.LocalPath);
        Assert.True(first.IsDownloaded);

        var second = Assert.IsType<FilePayloadItem>(vm.Items[1]);
        Assert.Equal("notes.txt", second.FileName);
        Assert.Equal(txtPath, second.LocalPath);
        Assert.True(second.IsDownloaded);
    }

    [Fact]
    public async Task AttachFilesCommand_CancelledPicker_DoesNothing()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);
        security.SaveCredentials(BotToken, AllowedUserId);

        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            security: security,
            transport: new ScriptedTransport(),
            pickFilesAction: () => null,
            baseDirectory: sandbox.Root);

        await vm.InitializationTask;

        await vm.AttachFilesCommand.ExecuteAsync(null);

        Assert.Null(vm.ErrorMessage);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task AttachFilesCommand_OneFileExceedsLimit_SetsErrorMessageAndContinues()
    {
        using var sandbox = new WidgetTestSandbox();
        await using var server = await FakeTelegramServer.StartAsync();
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);

        var security = new MetroSyncSecurity(sandbox.Root);
        using var historyStore = new MetroSyncHistoryStore(sandbox.Root);
        var thumbnails = new MetroSyncThumbnails(sandbox.Root);

        security.SaveCredentials(FakeTelegramServer.Token, AllowedUserId);

        var largePath = Path.Combine(sandbox.Root, "oversized.iso");
        using (var fs = File.Create(largePath))
        {
            fs.SetLength(55 * 1024 * 1024);
        }

        var validPath = Path.Combine(sandbox.Root, "valid.txt");
        await File.WriteAllTextAsync(validPath, "Valid text");

        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            security: security,
            historyStore: historyStore,
            thumbnails: thumbnails,
            transport: transport,
            pickFilesAction: () => [largePath, validPath],
            baseDirectory: sandbox.Root);

        await vm.InitializationTask;

        await vm.AttachFilesCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Single(vm.Items);
        var item = Assert.IsType<FilePayloadItem>(vm.Items[0]);
        Assert.Equal("valid.txt", item.FileName);
    }

    [Fact]
    public void CopyTextCommand_ExtractsCorrectTextFromVariousTypes()
    {
        using var sandbox = new WidgetTestSandbox();
        string? copied = null;

        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            copyTextAction: text => copied = text,
            baseDirectory: sandbox.Root);

        // 1. Direct string
        vm.CopyTextCommand.Execute("Hello direct");
        Assert.Equal("Hello direct", copied);

        // 2. TextPayloadItem
        vm.CopyTextCommand.Execute(new TextPayloadItem { Text = "Payload text" });
        Assert.Equal("Payload text", copied);

        // 3. ImagePayloadItem with caption
        vm.CopyTextCommand.Execute(new ImagePayloadItem { Caption = "Cool photo" });
        Assert.Equal("Cool photo", copied);

        // 4. FilePayloadItem with caption
        vm.CopyTextCommand.Execute(new FilePayloadItem { FileName = "doc.pdf", Caption = "Annual report" });
        Assert.Equal("Annual report", copied);

        // 5. FilePayloadItem without caption -> uses FileName
        vm.CopyTextCommand.Execute(new FilePayloadItem { FileName = "archive.zip" });
        Assert.Equal("archive.zip", copied);
    }

    [Fact]
    public void OpenLinkCommand_ValidHttpAndHttps_LaunchesUrl()
    {
        using var sandbox = new WidgetTestSandbox();
        string? openedUrl = null;

        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            openUrlAction: url => openedUrl = url,
            baseDirectory: sandbox.Root);

        // Direct HTTPS
        vm.OpenLinkCommand.Execute("https://metrohub.app/docs");
        Assert.Equal("https://metrohub.app/docs", openedUrl);

        // Direct HTTP
        vm.OpenLinkCommand.Execute("http://example.com");
        Assert.Equal("http://example.com/", openedUrl);

        // Extracted from TextPayloadItem
        vm.OpenLinkCommand.Execute(new TextPayloadItem { Text = "See https://github.com/dotnet for details." });
        Assert.Equal("https://github.com/dotnet", openedUrl);
    }

    [Fact]
    public void OpenLinkCommand_FromImageAndFileCaptions_ExtractsUrl()
    {
        using var sandbox = new WidgetTestSandbox();
        string? openedUrl = null;

        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            openUrlAction: url => openedUrl = url,
            baseDirectory: sandbox.Root);

        // Extracted from ImagePayloadItem caption
        vm.OpenLinkCommand.Execute(new ImagePayloadItem { Caption = "Screenshot of https://x.com/post/999" });
        Assert.Equal("https://x.com/post/999", openedUrl);

        // Extracted from FilePayloadItem caption
        openedUrl = null;
        vm.OpenLinkCommand.Execute(new FilePayloadItem { FileName = "spec.pdf", Caption = "Read https://specs.io" });
        Assert.Equal("https://specs.io/", openedUrl);
    }

    [Fact]
    public void OpenLinkCommand_RejectsDangerousSchemes()
    {
        using var sandbox = new WidgetTestSandbox();
        string? openedUrl = null;

        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            openUrlAction: url => openedUrl = url,
            baseDirectory: sandbox.Root);

        // File scheme
        vm.OpenLinkCommand.Execute("file:///C:/Windows/System32/cmd.exe");
        Assert.Null(openedUrl);

        // Javascript scheme
        vm.OpenLinkCommand.Execute("javascript:alert(1)");
        Assert.Null(openedUrl);

        // Non-URL plain text
        vm.OpenLinkCommand.Execute("Just a plain text row without links");
        Assert.Null(openedUrl);
    }

    [Fact]
    public async Task DownloadFileCommand_ValidFile_Enforces20MBLimit_AndUpdatesItem()
    {
        using var sandbox = new WidgetTestSandbox();
        await using var server = await FakeTelegramServer.StartAsync();
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);

        var security = new MetroSyncSecurity(sandbox.Root);
        security.SaveCredentials(FakeTelegramServer.Token, AllowedUserId);

        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            security: security,
            transport: transport,
            baseDirectory: sandbox.Root);

        await vm.InitializationTask;

        // 1. Valid download
        server.AddFile("doc_valid", Encoding.UTF8.GetBytes("sample file content"));

        var validFile = new FilePayloadItem
        {
            FileId = "doc_valid",
            FileName = "notes.txt",
            FileSize = 1024
        };

        await vm.DownloadFileCommand.ExecuteAsync(validFile);

        Assert.Null(validFile.ErrorMessage);
        Assert.False(validFile.IsDownloading);
        Assert.NotNull(validFile.LocalPath);
        Assert.True(validFile.IsDownloaded);
        Assert.True(File.Exists(validFile.LocalPath));

        // 2. Oversized file (> 20 MB)
        var oversizedFile = new FilePayloadItem
        {
            FileId = "doc_oversized",
            FileName = "big_video.mp4",
            FileSize = 25 * 1024 * 1024 // 25 MB
        };

        await vm.DownloadFileCommand.ExecuteAsync(oversizedFile);

        Assert.Equal("Too large for bot download (20 MB limit)", oversizedFile.ErrorMessage);
        Assert.False(oversizedFile.IsDownloading);
        Assert.False(oversizedFile.IsDownloaded);
        Assert.Null(oversizedFile.LocalPath);
    }

    [Fact]
    public void OpenFileCommand_And_ShowInFolderCommand_VerifyFileContainment()
    {
        using var sandbox = new WidgetTestSandbox();
        string downloadsDir = Path.Combine(sandbox.Root, "Downloads");
        Directory.CreateDirectory(downloadsDir);

        string validDownloadedFile = Path.Combine(downloadsDir, "downloaded.txt");
        File.WriteAllText(validDownloadedFile, "sample file content");

        string outsideFile = Path.Combine(sandbox.Root, "outside.txt");
        File.WriteAllText(outsideFile, "outside content");

        string? openedPath = null;
        string? shownPath = null;

        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            openFileAction: path => openedPath = path,
            showInFolderAction: path => shownPath = path,
            baseDirectory: sandbox.Root);

        var validItem = new FilePayloadItem { LocalPath = validDownloadedFile };
        var outsideItem = new FilePayloadItem { LocalPath = outsideFile };

        // Valid inside Downloads
        vm.OpenFileCommand.Execute(validItem);
        Assert.Equal(Path.GetFullPath(validDownloadedFile), openedPath);

        vm.ShowInFolderCommand.Execute(validItem);
        Assert.Equal(Path.GetFullPath(validDownloadedFile), shownPath);

        // Security check: outside file is rejected
        openedPath = null;
        shownPath = null;
        vm.OpenFileCommand.Execute(outsideItem);
        Assert.Null(openedPath);

        vm.ShowInFolderCommand.Execute(outsideItem);
        Assert.Null(shownPath);
    }

    [Fact]
    public async Task DisconnectCommand_ClearsCredentials_DeletesState_ResetsFeed()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);
        using var historyStore = new MetroSyncHistoryStore(sandbox.Root);

        security.SaveCredentials(BotToken, AllowedUserId);
        await historyStore.SaveLastAcknowledgedUpdateIdAsync(55);

        bool setupRequested = false;
        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            security: security,
            historyStore: historyStore,
            transport: new ScriptedTransport(),
            baseDirectory: sandbox.Root);

        vm.RequestSetupDialog = () => setupRequested = true;
        await vm.InitializationTask;

        // Add an item to feed
        vm.Items.Add(new TextPayloadItem { Text = "Temporary item" });
        Assert.NotEmpty(vm.Items);

        await vm.DisconnectCommand.ExecuteAsync(null);

        Assert.False(security.TryLoadCredentials(out _));
        Assert.Equal(0, await historyStore.LoadLastAcknowledgedUpdateIdAsync());
        Assert.Empty(vm.Items);
        Assert.False(vm.IsPaired);
        Assert.True(vm.CanConnect);
        Assert.False(vm.IsConnected);
        Assert.Equal(EngineState.Stopped, vm.EngineState);
        Assert.False(setupRequested);

        // Explicit user action opens setup
        vm.OpenSetupCommand.Execute(null);
        Assert.True(setupRequested);
    }

    [Fact]
    public void ContextMenuItems_ReflectsPairedAndUnpairedStates()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile();
            using var vm = new MetroSyncWidgetViewModel(tile);

            // Unpaired: offers Connect...
            var unpairedItems = new List<Control>(vm.GetContextMenuItems());
            Assert.Single(unpairedItems);
            var connectItem = Assert.IsAssignableFrom<MenuItem>(unpairedItems[0]);
            Assert.Equal("Connect...", connectItem.Header);

            // Simulate paired state
            typeof(MetroSyncWidgetViewModel)
                .GetProperty(nameof(MetroSyncWidgetViewModel.IsPaired))!
                .SetValue(vm, true);

            // Paired: offers Disconnect
            var pairedItems = new List<Control>(vm.GetContextMenuItems());
            Assert.Single(pairedItems);
            var disconnectItem = Assert.IsAssignableFrom<MenuItem>(pairedItems[0]);
            Assert.Equal("Disconnect", disconnectItem.Header);
        });
    }

    [Fact]
    public async Task Disconnect_WhileLongPollingInFlight_CancelsCleanlyAndResetsState()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);
        security.SaveCredentials(BotToken, AllowedUserId);

        var transport = new ScriptedTransport(); // empty script = infinite wait (long-poll)
        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            security: security,
            transport: transport,
            baseDirectory: sandbox.Root);

        await vm.InitializationTask;
        Assert.True(vm.IsPaired);

        // Disconnect while long polling is actively blocked
        await vm.DisconnectCommand.ExecuteAsync(null);

        Assert.False(vm.IsPaired);
        Assert.False(vm.IsConnected);
        Assert.Equal(EngineState.Stopped, vm.EngineState);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task Disconnect_RapidConcurrentCalls_AreSafeAndIdempotent()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);
        security.SaveCredentials(BotToken, AllowedUserId);

        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            security: security,
            transport: new ScriptedTransport(),
            baseDirectory: sandbox.Root);

        await vm.InitializationTask;
        Assert.True(vm.IsPaired);

        // Rapid concurrent disconnects
        await Task.WhenAll(
            vm.DisconnectAsync(),
            vm.DisconnectAsync(),
            vm.DisconnectAsync());

        Assert.False(vm.IsPaired);
        Assert.False(vm.IsConnected);
        Assert.Equal(EngineState.Stopped, vm.EngineState);
    }

    [Fact]
    public async Task ConnectAsync_AfterDisconnect_ReConnectsAndReloadsHistory()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);
        using var historyStore = new MetroSyncHistoryStore(sandbox.Root);

        security.SaveCredentials(BotToken, AllowedUserId);
        await historyStore.AppendAsync(new TextPayloadItem
        {
            UpdateId = 1,
            MessageId = 100,
            Text = "Durable message across re-pair",
            Timestamp = DateTimeOffset.UtcNow
        });

        var transport = new ScriptedTransport();
        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            security: security,
            historyStore: historyStore,
            transport: transport,
            baseDirectory: sandbox.Root);

        await vm.InitializationTask;
        Assert.Single(vm.Items);

        // 1. Disconnect
        await vm.DisconnectCommand.ExecuteAsync(null);
        Assert.Empty(vm.Items);
        Assert.False(vm.IsPaired);

        // 2. Re-connect with credentials
        var newCredentials = new MetroSyncCredentials("654321:XYZ-DEF1234ghIkl-zyx57W2v1u123ew99", 555444333);
        await vm.ConnectAsync(newCredentials);

        Assert.True(vm.IsPaired);
        Assert.True(vm.IsConnected);
        Assert.Single(vm.Items);
        Assert.Equal("Durable message across re-pair", ((TextPayloadItem)vm.Items[0]).Text);
    }

    [Fact]
    public void FeedCap_200Items_EnforcesTrimmingOfOldestItems()
    {
        using var sandbox = new WidgetTestSandbox();
        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(tile, baseDirectory: sandbox.Root);

        // Add 250 items to feed via production AddToFeed method
        for (int i = 1; i <= 250; i++)
        {
            vm.AddToFeed(new TextPayloadItem { UpdateId = i, Text = $"Item {i}" });
        }

        Assert.Equal(200, vm.Items.Count);
        Assert.Equal("Item 51", ((TextPayloadItem)vm.Items[0]).Text);
        Assert.Equal("Item 250", ((TextPayloadItem)vm.Items[^1]).Text);
    }

    [Fact]
    public async Task HydrateThumbnail_RecoversCachedThumbnailFromDisk_WhenThumbnailFilePathWasNotPersisted()
    {
        using var sandbox = new WidgetTestSandbox();
        var thumbnails = new MetroSyncThumbnails(sandbox.Root);
        var testBmp = CreateFrozenBitmap(60, 60);

        // Save a thumbnail to disk for message ID 42
        using (var ms = new MemoryStream())
        {
            var encoder = new JpegBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(testBmp));
            encoder.Save(ms);
            ms.Position = 0;
            await thumbnails.SaveThumbnailAsync(42, ms, CancellationToken.None);
        }

        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            thumbnails: thumbnails,
            baseDirectory: sandbox.Root);

        // Item with empty ThumbnailFilePath
        var item = new ImagePayloadItem
        {
            MessageId = 42,
            ThumbnailFilePath = null
        };

        vm.AddToFeed(item);

        Assert.NotNull(item.ThumbnailFilePath);
        Assert.True(File.Exists(item.ThumbnailFilePath));
        Assert.NotNull(item.ThumbnailImage);
    }

    [Fact]
    public void RedactBotToken_RedactsTokenInApiUrl_EvenWhenCredentialsAreNull()
    {
        using var sandbox = new WidgetTestSandbox();
        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(tile, baseDirectory: sandbox.Root);

        string rawUrlError = "Request failed: https://api.telegram.org/bot987654:ABCdefGHI_jkl123-xyz/sendMessage?chat_id=123";
        string redacted = vm.RedactBotToken(rawUrlError);

        Assert.DoesNotContain("987654:ABCdefGHI_jkl123-xyz", redacted);
        Assert.Contains("/bot[REDACTED]", redacted);
    }

    [Fact]
    public async Task Lifecycle_CreateAndDisposeCycles_LeavesNoLeakedTasksOrResources()
    {
        using var guard = new UnobservedTaskGuard();
        using var sandbox = new WidgetTestSandbox();
        await using var server = await FakeTelegramServer.StartAsync();

        var security = new MetroSyncSecurity(sandbox.Root);
        security.SaveCredentials(FakeTelegramServer.Token, AllowedUserId);

        // Warm-up: JIT, thread pool, socket pools
        for (int i = 0; i < 5; i++)
        {
            _ = await RunLifecycleCycleAsync(server, sandbox.Root);
        }

        var baseline = ResourceProbe.Capture(server.Port);
        var references = new List<WeakReference>();

        for (int i = 0; i < 30; i++)
        {
            references.Add(await RunLifecycleCycleAsync(server, sandbox.Root));
        }

        await LeakAssert.AssertCollectedAsync(references);
        var after = ResourceProbe.Capture(server.Port);

        Assert.True(after.Threads - baseline.Threads <= 6, $"Threads {baseline.Threads} -> {after.Threads}");
        Assert.Equal(0, after.CloseWaitSockets);
        guard.AssertNone();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> RunLifecycleCycleAsync(FakeTelegramServer server, string baseDirectory)
    {
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);
        var tile = CreateTestTile();

        var vm = new MetroSyncWidgetViewModel(
            tile,
            transport: transport,
            baseDirectory: baseDirectory);

        await vm.InitializationTask;
        await Task.Delay(20);

        vm.Dispose();
        return new WeakReference(vm);
    }

    [Fact]
    public void ExtractFirstUrl_HandlesComplexStrings()
    {
        Assert.Equal("https://x.com/post/123", MetroSyncWidgetViewModel.ExtractFirstUrl("Check this https://x.com/post/123 now"));
        Assert.Equal("http://foo.bar/baz", MetroSyncWidgetViewModel.ExtractFirstUrl("Link: (http://foo.bar/baz)"));
        Assert.Equal("https://example.com", MetroSyncWidgetViewModel.ExtractFirstUrl("Visit https://example.com."));
        Assert.Equal("https://en.wikipedia.org/wiki/WPF_(disambiguation)", MetroSyncWidgetViewModel.ExtractFirstUrl("Read https://en.wikipedia.org/wiki/WPF_(disambiguation)."));
        Assert.Equal("https://en.wikipedia.org/wiki/WPF_(disambiguation)", MetroSyncWidgetViewModel.ExtractFirstUrl("(https://en.wikipedia.org/wiki/WPF_(disambiguation))"));
        Assert.Equal("https://example.com/item", MetroSyncWidgetViewModel.ExtractFirstUrl("[https://example.com/item]"));
        Assert.Equal("https://example.com/raw", MetroSyncWidgetViewModel.ExtractFirstUrl("<https://example.com/raw>"));
        Assert.Null(MetroSyncWidgetViewModel.ExtractFirstUrl("Plain text with no URL"));
        Assert.Null(MetroSyncWidgetViewModel.ExtractFirstUrl("ftp://file.com"));
        Assert.Null(MetroSyncWidgetViewModel.ExtractFirstUrl(null));
    }

    [Fact]
    public async Task ContextMenu_ProvidesDisconnectWhenPaired()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);
        security.SaveCredentials(BotToken, AllowedUserId);

        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            security: security,
            transport: new ScriptedTransport(),
            baseDirectory: sandbox.Root);

        await vm.InitializationTask;
        Assert.True(vm.IsPaired);

        WpfTestHost.RunSta(() =>
        {
            var items = vm.GetContextMenuItems().ToList();
            Assert.Single(items);
            var menuItem = Assert.IsType<System.Windows.Controls.MenuItem>(items[0]);
            Assert.Equal("Disconnect", menuItem.Header);
        });
    }

    [Fact]
    public async Task SaveFileAsCommand_DownloadedFile_CopiesToSelectedDestination()
    {
        using var sandbox = new WidgetTestSandbox();
        var downloadsDir = Path.Combine(sandbox.Root, "Downloads");
        Directory.CreateDirectory(downloadsDir);
        var sourceFile = Path.Combine(downloadsDir, "report.pdf");
        await File.WriteAllTextAsync(sourceFile, "Sample PDF Content");

        var destinationFile = Path.Combine(sandbox.Root, "saved_report.pdf");

        var fileItem = new FilePayloadItem
        {
            FileName = "report.pdf",
            LocalPath = sourceFile,
            FileSize = 18
        };

        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            baseDirectory: sandbox.Root,
            pickSaveFileAction: (name, filter) => destinationFile);

        await vm.SaveFileAsCommand.ExecuteAsync(fileItem);

        Assert.True(File.Exists(destinationFile));
        Assert.Equal("Sample PDF Content", await File.ReadAllTextAsync(destinationFile));
    }

    [Fact]
    public async Task SaveFileAsCommand_WhenUserCancels_DoesNotCopy()
    {
        using var sandbox = new WidgetTestSandbox();
        var downloadsDir = Path.Combine(sandbox.Root, "Downloads");
        Directory.CreateDirectory(downloadsDir);
        var sourceFile = Path.Combine(downloadsDir, "doc.txt");
        await File.WriteAllTextAsync(sourceFile, "Doc Content");

        var fileItem = new FilePayloadItem
        {
            FileName = "doc.txt",
            LocalPath = sourceFile,
            FileSize = 11
        };

        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            baseDirectory: sandbox.Root,
            pickSaveFileAction: (name, filter) => null);

        await vm.SaveFileAsCommand.ExecuteAsync(fileItem);

        Assert.True(File.Exists(sourceFile));
    }

    [Fact]
    public async Task SaveFileAsCommand_UndownloadedFile_DownloadsAndCopiesToDestination()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);

        using var sandbox = new WidgetTestSandbox();
        byte[] expectedBytes = "Network content from telegram"u8.ToArray();
        server.AddFile("telegram_file_123", expectedBytes);

        var destinationFile = Path.Combine(sandbox.Root, "exported.txt");

        var fileItem = new FilePayloadItem
        {
            FileId = "telegram_file_123",
            FileName = "notes.txt",
            FileSize = expectedBytes.Length
        };

        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            transport: transport,
            baseDirectory: sandbox.Root,
            pickSaveFileAction: (name, filter) => destinationFile);

        await vm.SaveFileAsCommand.ExecuteAsync(fileItem);

        Assert.True(File.Exists(destinationFile));
        Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(destinationFile));
        Assert.True(fileItem.IsDownloaded);
    }

    [Fact]
    public void CopyImageCommand_WithImagePayloadItem_InvokesCopyImageAction()
    {
        using var sandbox = new WidgetTestSandbox();
        BitmapSource? copiedSource = null;

        var bmp = CreateFrozenBitmap(50, 50);
        var imageItem = new ImagePayloadItem
        {
            MessageId = 42,
            ThumbnailImage = bmp
        };

        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            baseDirectory: sandbox.Root,
            copyImageAction: b => copiedSource = b);

        vm.CopyImageCommand.Execute(imageItem);

        Assert.NotNull(copiedSource);
        Assert.Equal(50, copiedSource.PixelWidth);
        Assert.Equal(50, copiedSource.PixelHeight);
    }

    [Fact]
    public async Task OpenOrDownloadFileCommand_TogglesCorrectActionBasedOnDownloadedState()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);

        using var sandbox = new WidgetTestSandbox();
        byte[] content = "Hello file"u8.ToArray();
        server.AddFile("file_state_test", content);

        string? openedPath = null;
        var fileItem = new FilePayloadItem
        {
            FileId = "file_state_test",
            FileName = "state.txt",
            FileSize = content.Length
        };

        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            transport: transport,
            baseDirectory: sandbox.Root,
            openFileAction: path => openedPath = path);

        // First click: not downloaded -> downloads
        Assert.False(fileItem.IsDownloaded);
        await vm.OpenOrDownloadFileCommand.ExecuteAsync(fileItem);
        Assert.True(fileItem.IsDownloaded);
        Assert.Null(openedPath);

        // Second click: downloaded -> opens
        await vm.OpenOrDownloadFileCommand.ExecuteAsync(fileItem);
        Assert.NotNull(openedPath);
        Assert.Equal(fileItem.LocalPath, openedPath);
    }

    [Fact]
    public async Task SaveFileAsCommand_WhenDestinationMatchesSource_DoesNotThrow()
    {
        using var sandbox = new WidgetTestSandbox();
        var downloadsDir = Path.Combine(sandbox.Root, "Downloads");
        Directory.CreateDirectory(downloadsDir);
        var sourceFile = Path.Combine(downloadsDir, "same.txt");
        await File.WriteAllTextAsync(sourceFile, "Identical content");

        var fileItem = new FilePayloadItem
        {
            FileName = "same.txt",
            LocalPath = sourceFile,
            FileSize = 17
        };

        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            baseDirectory: sandbox.Root,
            pickSaveFileAction: (name, filter) => sourceFile);

        await vm.SaveFileAsCommand.ExecuteAsync(fileItem);

        Assert.Null(fileItem.ErrorMessage);
        Assert.True(File.Exists(sourceFile));
        Assert.Equal("Identical content", await File.ReadAllTextAsync(sourceFile));
    }

    [Fact]
    public async Task SaveFileAsCommand_OversizedUndownloadedFile_SetsLimitErrorMessage()
    {
        using var sandbox = new WidgetTestSandbox();
        var destinationFile = Path.Combine(sandbox.Root, "big_export.zip");

        var fileItem = new FilePayloadItem
        {
            FileId = "big_file_id",
            FileName = "huge.zip",
            FileSize = 25 * 1024 * 1024 // 25 MB > 20 MB limit
        };

        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            baseDirectory: sandbox.Root,
            pickSaveFileAction: (name, filter) => destinationFile);

        await vm.SaveFileAsCommand.ExecuteAsync(fileItem);

        Assert.Equal("Too large for bot download (20 MB limit)", fileItem.ErrorMessage);
        Assert.False(File.Exists(destinationFile));
    }

    [Fact]
    public async Task SaveFileAsCommand_WithImagePayloadItem_SavesImageFile()
    {
        using var sandbox = new WidgetTestSandbox();
        var destinationFile = Path.Combine(sandbox.Root, "exported_image.jpg");
        var bmp = CreateFrozenBitmap(64, 64);

        var imageItem = new ImagePayloadItem
        {
            MessageId = 88,
            ThumbnailImage = bmp
        };

        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            baseDirectory: sandbox.Root,
            pickSaveFileAction: (name, filter) => destinationFile);

        await vm.SaveFileAsCommand.ExecuteAsync(imageItem);

        Assert.True(File.Exists(destinationFile));
        Assert.True(new FileInfo(destinationFile).Length > 0);
    }

    [Fact]
    public void CopyLinkCommand_WithTextPayloadItem_InvokesCopyTextActionWithUrl()
    {
        using var sandbox = new WidgetTestSandbox();
        string? copiedText = null;

        var textItem = new TextPayloadItem
        {
            Text = "Check this site https://example.com/portal today"
        };

        var tile = CreateTestTile();
        using var vm = new MetroSyncWidgetViewModel(
            tile,
            baseDirectory: sandbox.Root,
            copyTextAction: t => copiedText = t);

        vm.CopyLinkCommand.Execute(textItem);

        Assert.Equal("https://example.com/portal", copiedText);
    }
}