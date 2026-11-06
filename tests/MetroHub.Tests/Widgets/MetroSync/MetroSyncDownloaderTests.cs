using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Tests;
using MetroHub.Tests.Widgets.MetroSync.TestKit;
using MetroHub.Widgets.Catalog.MetroSync.Models;
using MetroHub.Widgets.Catalog.MetroSync.Services;
using Xunit;

namespace MetroHub.Tests.Widgets.MetroSync;

[Collection("MetroSyncTests")]
public sealed class MetroSyncDownloaderTests
{
    private static TelegramHttpTransport Http(FakeTelegramServer server, HttpClient client) =>
        new(client, server.BaseAddress, FakeTelegramServer.Token, allowInsecureLoopbackForTests: true);

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\secret.txt", "secret.txt")]
    [InlineData("/var/log/app.log", "app.log")]
    [InlineData("C:\\Windows\\System32\\cmd.exe", "cmd.exe")]
    [InlineData("foo/bar/baz.pdf", "baz.pdf")]
    [InlineData("foo\\bar\\baz.pdf", "baz.pdf")]
    public void SanitizeFileName_StripsDirectoryTraversal(string rawName, string expected)
    {
        var sanitized = MetroSyncDownloader.SanitizeFileName(rawName);
        Assert.Equal(expected, sanitized);
    }

    [Fact]
    public void SanitizeFileName_ReplacesInvalidCharacters()
    {
        var raw = "file*name?with:invalid<chars>and|pipes\"test.txt";
        var sanitized = MetroSyncDownloader.SanitizeFileName(raw);

        Assert.DoesNotContain('*', sanitized);
        Assert.DoesNotContain('?', sanitized);
        Assert.DoesNotContain(':', sanitized);
        Assert.DoesNotContain('<', sanitized);
        Assert.DoesNotContain('>', sanitized);
        Assert.DoesNotContain('|', sanitized);
        Assert.DoesNotContain('"', sanitized);
        Assert.EndsWith(".txt", sanitized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("prn.pdf", "_prn.pdf")]
    [InlineData("aux", "_aux")]
    [InlineData("NUL.dat", "_NUL.dat")]
    [InlineData("com1.txt", "_com1.txt")]
    [InlineData("LPT2.log", "_LPT2.log")]
    public void SanitizeFileName_ReplacesReservedDeviceNames(string rawName, string expected)
    {
        var sanitized = MetroSyncDownloader.SanitizeFileName(rawName);
        Assert.Equal(expected, sanitized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData(null)]
    public void SanitizeFileName_EmptyOrDots_ReturnsFallback(string? rawName)
    {
        var sanitized = MetroSyncDownloader.SanitizeFileName(rawName);
        Assert.Equal("file", sanitized);
    }

    [Fact]
    public void SanitizeFileName_TrimsTrailingDotsAndSpaces()
    {
        var sanitized = MetroSyncDownloader.SanitizeFileName("document.pdf.  ");
        Assert.Equal("document.pdf", sanitized);
    }

    [Fact]
    public void SanitizeFileName_CapsLength_PreservingExtension()
    {
        var longBase = new string('a', 200);
        var raw = longBase + ".pdf";

        var sanitized = MetroSyncDownloader.SanitizeFileName(raw);
        Assert.True(sanitized.Length <= 120);
        Assert.EndsWith(".pdf", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void GetUniqueDestinationPath_DeDuplicatesCollisions()
    {
        using var sandbox = new WidgetTestSandbox();
        using var client = TelegramHttpTransport.CreateDedicatedClient();
        var transport = new ScriptedTransport();
        var downloader = new MetroSyncDownloader(transport, sandbox.Root);

        Directory.CreateDirectory(downloader.DownloadsDirectory);

        var first = downloader.GetUniqueDestinationPath("document.pdf");
        Assert.Equal(Path.Combine(downloader.DownloadsDirectory, "document.pdf"), first);
        File.WriteAllText(first, "content 1");

        var second = downloader.GetUniqueDestinationPath("document.pdf");
        Assert.Equal(Path.Combine(downloader.DownloadsDirectory, "document (1).pdf"), second);
        File.WriteAllText(second, "content 2");

        var third = downloader.GetUniqueDestinationPath("document.pdf");
        Assert.Equal(Path.Combine(downloader.DownloadsDirectory, "document (2).pdf"), third);
    }

    [Fact]
    public async Task DownloadFileAsync_DeclaredSizeExceeds20MB_ThrowsBeforeNetwork()
    {
        using var sandbox = new WidgetTestSandbox();
        var recording = new RecordingTransport(new ScriptedTransport());
        var downloader = new MetroSyncDownloader(recording, sandbox.Root);

        const long overLimit = 20 * 1024 * 1024 + 1; // 20 MB + 1 byte
        var ex = await Assert.ThrowsAsync<FileTooLargeException>(() =>
            downloader.DownloadFileAsync("file_123", "big.zip", declaredFileSize: overLimit));

        Assert.Equal(0, recording.GetFileCalls);
        Assert.Equal(0, recording.DownloadCalls);
    }

    [Fact]
    public async Task DownloadFileAsync_GetFileReportsOver20MB_ThrowsBeforeDownload()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var client = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, client);
        var recording = new RecordingTransport(transport);

        using var sandbox = new WidgetTestSandbox();
        var downloader = new MetroSyncDownloader(recording, sandbox.Root);

        const long overLimit = 20 * 1024 * 1024 + 500;
        server.AddFile("too_big_id", [1, 2, 3], declaredSize: overLimit);

        await Assert.ThrowsAsync<FileTooLargeException>(() =>
            downloader.DownloadFileAsync("too_big_id", "oversized.bin"));

        Assert.Equal(1, recording.GetFileCalls);
        Assert.Equal(0, recording.DownloadCalls);
    }

    [Fact]
    public async Task DownloadFileAsync_ServerStreamsOver20MB_ThrowsAndCleansUp()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var client = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, client);

        using var sandbox = new WidgetTestSandbox();
        var downloader = new MetroSyncDownloader(transport, sandbox.Root);

        // 20 MB + 100 KB actual bytes, but declared size under-reported
        var content = new byte[20 * 1024 * 1024 + 100 * 1024];
        server.AddFile("stream_over_limit", content, declaredSize: 1024);

        await Assert.ThrowsAsync<FileTooLargeException>(() =>
            downloader.DownloadFileAsync("stream_over_limit", "stream.bin"));

        // Verify no leftover .tmp or downloaded files in Downloads directory
        var files = Directory.GetFiles(downloader.DownloadsDirectory);
        Assert.Empty(files);
    }

    [Fact]
    public async Task DownloadFileAsync_Success_DownloadsFileAtomicallyWithProgress()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var client = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, client);

        using var sandbox = new WidgetTestSandbox();
        var downloader = new MetroSyncDownloader(transport, sandbox.Root);

        var expectedData = Encoding.UTF8.GetBytes("Hello, MetroSync file downloader test content!");
        server.AddFile("file_abc_123", expectedData);

        double lastProgress = 0.0;
        var progress = new Progress<double>(p => lastProgress = p);

        var destination = await downloader.DownloadFileAsync(
            "file_abc_123",
            "hello.txt",
            declaredFileSize: expectedData.Length,
            progress: progress);

        Assert.True(File.Exists(destination));
        Assert.Equal(expectedData, await File.ReadAllBytesAsync(destination));

        // Verify atomic move - no leftover .tmp files
        var allFiles = Directory.GetFiles(downloader.DownloadsDirectory);
        Assert.Single(allFiles);
        Assert.Equal(destination, allFiles[0]);
    }

    [Fact]
    public async Task DownloadFileAsync_NetworkFault_CleansUpTmpFile()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var client = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, client);

        using var sandbox = new WidgetTestSandbox();
        var downloader = new MetroSyncDownloader(transport, sandbox.Root);

        server.AddFile("file_fault", [1, 2, 3, 4, 5]);
        server.Chaos.Script("download", new ResetConnection());

        await Assert.ThrowsAnyAsync<Exception>(() =>
            downloader.DownloadFileAsync("file_fault", "fault.bin"));

        var files = Directory.GetFiles(downloader.DownloadsDirectory);
        Assert.Empty(files);
    }

    [Fact]
    public async Task DownloadFileAsync_Cancellation_CleansUpTmpFile()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var client = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, client);

        using var sandbox = new WidgetTestSandbox();
        var downloader = new MetroSyncDownloader(transport, sandbox.Root);

        server.AddFile("file_slow", new byte[100 * 1024]);
        server.Chaos.Script("download", new DelayThenContinue(TimeSpan.FromSeconds(5)));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            downloader.DownloadFileAsync("file_slow", "slow.bin", ct: cts.Token));

        var files = Directory.GetFiles(downloader.DownloadsDirectory);
        Assert.Empty(files);
    }

    [Fact]
    public async Task DownloadFileAsync_WithFilePayloadItem_UpdatesItemProperties()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var client = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, client);

        using var sandbox = new WidgetTestSandbox();
        var downloader = new MetroSyncDownloader(transport, sandbox.Root);

        var data = Encoding.UTF8.GetBytes("FilePayloadItem test contents");
        server.AddFile("doc_item_id", data);

        var item = new FilePayloadItem
        {
            FileId = "doc_item_id",
            FileName = "sample.pdf",
            FileSize = data.Length,
            IsDownloading = false,
            LocalPath = null
        };

        var path = await downloader.DownloadFileAsync(item);

        Assert.Equal(path, item.LocalPath);
        Assert.False(item.IsDownloading);
        Assert.Equal(1.0, item.DownloadProgress);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void EnsureContainedInDownloads_TrailingSlashInBaseDir_DoesNotThrow()
    {
        using var sandbox = new WidgetTestSandbox();
        var transport = new ScriptedTransport();
        var baseWithSlash = sandbox.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var downloader = new MetroSyncDownloader(transport, baseWithSlash);

        var path = downloader.GetUniqueDestinationPath("doc.pdf");
        Assert.NotNull(path);
        Assert.StartsWith(downloader.DownloadsDirectory, path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizeFileName_BidiOverrideAndHiddenControlChars_ReplacedWithUnderscore()
    {
        // \u202E is Right-To-Left Override used in extension-spoofing attacks (e.g. report\u202Efdp.exe)
        // \u200B is zero-width space
        var spoofedName = "report\u202Efdp.exe";
        var sanitized = MetroSyncDownloader.SanitizeFileName(spoofedName);

        Assert.DoesNotContain('\u202E', sanitized);
        Assert.Contains('_', sanitized);

        var zeroWidthName = "test\u200Bfile.txt";
        var sanitizedZero = MetroSyncDownloader.SanitizeFileName(zeroWidthName);
        Assert.DoesNotContain('\u200B', sanitizedZero);
    }

    [Fact]
    public void SanitizeFileName_ExtLengthExceedsMaxTotalLength_ClampedTo120()
    {
        var raw = "file." + new string('x', 150);
        var sanitized = MetroSyncDownloader.SanitizeFileName(raw);

        Assert.True(sanitized.Length <= 120);
    }

    [Fact]
    public async Task DownloadFileAsync_NegativeDeclaredSize_ThrowsArgumentOutOfRangeException()
    {
        using var sandbox = new WidgetTestSandbox();
        var transport = new ScriptedTransport();
        var downloader = new MetroSyncDownloader(transport, sandbox.Root);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            downloader.DownloadFileAsync("file_123", "test.txt", declaredFileSize: -10));
    }
}
