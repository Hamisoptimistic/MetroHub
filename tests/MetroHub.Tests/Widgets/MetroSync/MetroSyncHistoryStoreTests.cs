using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MetroHub.Tests;
using MetroHub.Widgets.Catalog.MetroSync.Models;
using MetroHub.Widgets.Catalog.MetroSync.Services;
using Xunit;

namespace MetroHub.Tests.Widgets.MetroSync;

[Collection("MetroSyncTests")]
public sealed class MetroSyncHistoryStoreTests
{
    [Fact]
    public async Task AppendAndLoad_100SequentialItems_ReturnsLatest50InOrder()
    {
        using var sandbox = new WidgetTestSandbox();
        using var store = new MetroSyncHistoryStore(sandbox.Root);

        var baselineTime = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

        for (int i = 1; i <= 100; i++)
        {
            MetroSyncMessageItem item = (i % 3) switch
            {
                0 => new TextPayloadItem
                {
                    UpdateId = i,
                    MessageId = i * 10,
                    Timestamp = baselineTime.AddMinutes(i),
                    IsOutgoing = (i % 2 == 0),
                    Text = $"Message {i}"
                },
                1 => new ImagePayloadItem
                {
                    UpdateId = i,
                    MessageId = i * 10,
                    Timestamp = baselineTime.AddMinutes(i),
                    IsOutgoing = false,
                    Caption = $"Photo {i}",
                    FileId = $"file_{i}",
                    ThumbnailFilePath = $"thumb_{i}.jpg"
                },
                _ => new FilePayloadItem
                {
                    UpdateId = i,
                    MessageId = i * 10,
                    Timestamp = baselineTime.AddMinutes(i),
                    IsOutgoing = true,
                    FileName = $"Doc{i}.pdf",
                    FileSize = 1024 * i,
                    MimeType = "application/pdf",
                    Caption = $"Caption {i}"
                }
            };

            bool appended = await store.AppendAsync(item);
            Assert.True(appended);
        }

        var latest50 = await store.LoadLatestAsync(50);

        Assert.Equal(50, latest50.Count);

        // Verify chronological order: item 51 (UpdateId 51) to item 100 (UpdateId 100)
        for (int idx = 0; idx < 50; idx++)
        {
            long expectedUpdateId = 51 + idx;
            Assert.Equal(expectedUpdateId, latest50[idx].UpdateId);
            Assert.Equal(expectedUpdateId * 10, latest50[idx].MessageId);
        }

        // Verify polymorphic restoration
        Assert.IsType<TextPayloadItem>(latest50.First(x => x.UpdateId == 60));
        Assert.IsType<ImagePayloadItem>(latest50.First(x => x.UpdateId == 61));
        Assert.IsType<FilePayloadItem>(latest50.First(x => x.UpdateId == 62));
    }

    [Fact]
    public async Task AppendAsync_DeduplicatesByUpdateIdAndMessageId()
    {
        using var sandbox = new WidgetTestSandbox();
        using var store = new MetroSyncHistoryStore(sandbox.Root);

        var item1 = new TextPayloadItem { UpdateId = 100, MessageId = 500, Text = "First" };
        var duplicateUpdate = new TextPayloadItem { UpdateId = 100, MessageId = 501, Text = "Duplicate UpdateId" };
        var duplicateMessage = new TextPayloadItem { UpdateId = 101, MessageId = 500, Text = "Duplicate MessageId" };
        var validNew = new TextPayloadItem { UpdateId = 102, MessageId = 502, Text = "Valid New" };

        Assert.True(await store.AppendAsync(item1));
        Assert.False(await store.AppendAsync(duplicateUpdate));
        Assert.False(await store.AppendAsync(duplicateMessage));
        Assert.True(await store.AppendAsync(validNew));

        var items = await store.LoadLatestAsync(50);
        Assert.Equal(2, items.Count);
        Assert.Equal(100, items[0].UpdateId);
        Assert.Equal(102, items[1].UpdateId);
    }

    [Fact]
    public async Task LoadLatestAsync_DeduplicatesPreExistingLinesInFile()
    {
        using var sandbox = new WidgetTestSandbox();

        Directory.CreateDirectory(sandbox.Root);
        string historyPath = Path.Combine(sandbox.Root, "history.jsonl");

        // Write lines directly where line 2 and line 3 duplicate line 1's UpdateId or MessageId
        string lines =
            "{\"kind\":\"text\",\"updateId\":1,\"messageId\":10,\"text\":\"Item 1\"}\n" +
            "{\"kind\":\"text\",\"updateId\":1,\"messageId\":11,\"text\":\"Dup by update\"}\n" +
            "{\"kind\":\"text\",\"updateId\":2,\"messageId\":10,\"text\":\"Dup by message\"}\n" +
            "{\"kind\":\"text\",\"updateId\":3,\"messageId\":30,\"text\":\"Item 3\"}\n";

        await File.WriteAllTextAsync(historyPath, lines);

        using var store = new MetroSyncHistoryStore(sandbox.Root);
        var loaded = await store.LoadLatestAsync(50);

        Assert.Equal(2, loaded.Count);
        Assert.Equal(1, loaded[0].UpdateId);
        Assert.Equal(3, loaded[1].UpdateId);
    }

    [Fact]
    public async Task LoadLatestAsync_TruncatedFinalLine_RecoversValidPrecedingItems()
    {
        using var sandbox = new WidgetTestSandbox();
        using var store = new MetroSyncHistoryStore(sandbox.Root);

        var item1 = new TextPayloadItem { UpdateId = 1, MessageId = 10, Text = "Good 1" };
        var item2 = new TextPayloadItem { UpdateId = 2, MessageId = 20, Text = "Good 2" };

        await store.AppendAsync(item1);
        await store.AppendAsync(item2);

        // Simulate crash during append: append a broken, half-written JSON line
        await File.AppendAllTextAsync(store.HistoryPath, "{\"kind\":\"text\",\"updateId\":3,\"messageId\":30,\"text\":\"unfini");

        var loaded = await store.LoadLatestAsync(50);

        Assert.Equal(2, loaded.Count);
        Assert.Equal(1, loaded[0].UpdateId);
        Assert.Equal(2, loaded[1].UpdateId);
    }

    [Fact]
    public async Task AtomicStateSaveAndLoad_RoundTrip_Succeeds()
    {
        using var sandbox = new WidgetTestSandbox();
        using var store = new MetroSyncHistoryStore(sandbox.Root);

        Assert.Equal(0, await store.LoadLastAcknowledgedUpdateIdAsync());

        await store.SaveLastAcknowledgedUpdateIdAsync(987654321);
        long loaded = await store.LoadLastAcknowledgedUpdateIdAsync();

        Assert.Equal(987654321, loaded);
        Assert.True(File.Exists(store.StatePath));

        // Ensure no temporary files remained
        string[] tmpFiles = Directory.GetFiles(sandbox.Root, "*.tmp");
        Assert.Empty(tmpFiles);
    }

    [Fact]
    public async Task LoadLastAcknowledgedUpdateIdAsync_CorruptFile_ReturnsZero()
    {
        using var sandbox = new WidgetTestSandbox();
        using var store = new MetroSyncHistoryStore(sandbox.Root);

        Directory.CreateDirectory(sandbox.Root);
        await File.WriteAllTextAsync(store.StatePath, "{ corrupted json !@#$$% ");

        long loaded = await store.LoadLastAcknowledgedUpdateIdAsync();
        Assert.Equal(0, loaded);
    }

    [Fact]
    public async Task DeleteState_RemovesStateFile()
    {
        using var sandbox = new WidgetTestSandbox();
        using var store = new MetroSyncHistoryStore(sandbox.Root);

        await store.SaveLastAcknowledgedUpdateIdAsync(12345);
        Assert.True(File.Exists(store.StatePath));

        store.DeleteState();

        Assert.False(File.Exists(store.StatePath));
        Assert.Equal(0, await store.LoadLastAcknowledgedUpdateIdAsync());
    }

    [Fact]
    public async Task ConcurrentAccess_NoIOExceptionUnderStress()
    {
        using var sandbox = new WidgetTestSandbox();
        using var store = new MetroSyncHistoryStore(sandbox.Root);

        var tasks = new List<Task>();

        for (int i = 1; i <= 20; i++)
        {
            int id = i;
            tasks.Add(Task.Run(async () =>
            {
                var item = new TextPayloadItem
                {
                    UpdateId = id,
                    MessageId = id * 100,
                    Text = $"Concurrent {id}"
                };
                await store.AppendAsync(item);
                await store.LoadLatestAsync(10);
                await store.SaveLastAcknowledgedUpdateIdAsync(id);
            }));
        }

        await Task.WhenAll(tasks);

        var finalItems = await store.LoadLatestAsync(50);
        Assert.Equal(20, finalItems.Count);
    }

    [Fact]
    public async Task AppendAsync_AfterTruncatedLineWithoutNewline_DoesNotCorruptNewItem()
    {
        using var sandbox = new WidgetTestSandbox();
        using var store = new MetroSyncHistoryStore(sandbox.Root);

        await store.AppendAsync(new TextPayloadItem { UpdateId = 1, MessageId = 10, Text = "First valid" });

        // Simulate a power cut / unexpected crash while writing line 2 (no newline character)
        await File.AppendAllTextAsync(store.HistoryPath, "{\"kind\":\"text\",\"updateId\":2,\"messageId\":20,\"text\":\"truncated-data");

        // Now append a valid third item
        var item3 = new TextPayloadItem { UpdateId = 3, MessageId = 30, Text = "Third valid" };
        bool appended = await store.AppendAsync(item3);
        Assert.True(appended);

        var loaded = await store.LoadLatestAsync(50);

        Assert.Equal(2, loaded.Count);
        Assert.Equal(1, loaded[0].UpdateId);
        Assert.Equal("First valid", ((TextPayloadItem)loaded[0]).Text);
        Assert.Equal(3, loaded[1].UpdateId);
        Assert.Equal("Third valid", ((TextPayloadItem)loaded[1]).Text);
    }

    [Fact]
    public async Task IsRecorded_OnColdStartBeforeLoad_IdentifiesPersistedIds()
    {
        using var sandbox = new WidgetTestSandbox();

        using (var store1 = new MetroSyncHistoryStore(sandbox.Root))
        {
            await store1.AppendAsync(new TextPayloadItem { UpdateId = 42, MessageId = 420, Text = "Saved" });
        }

        // New cold instance on the same directory; no LoadLatestAsync or AppendAsync called yet
        using var store2 = new MetroSyncHistoryStore(sandbox.Root);

        Assert.True(store2.IsRecorded(42, 0));
        Assert.True(store2.IsRecorded(0, 420));
        Assert.True(store2.IsRecorded(42, 420));
        Assert.False(store2.IsRecorded(99, 990));
    }

    [Fact]
    public async Task CrashRecovery_CrashBetweenHistoryAppendAndOffsetWrite_PreventsDuplicates()
    {
        using var sandbox = new WidgetTestSandbox();

        // 1. App receives update 1, appends to history, but crashes before saving offset (offset remains 0)
        using (var store1 = new MetroSyncHistoryStore(sandbox.Root))
        {
            var item = new TextPayloadItem { UpdateId = 1, MessageId = 10, Text = "Item 1" };
            Assert.True(await store1.AppendAsync(item));
            // Offset NOT saved
        }

        // 2. On restart, Telegram redelivers update 1 because offset was not acknowledged
        using (var store2 = new MetroSyncHistoryStore(sandbox.Root))
        {
            Assert.Equal(0, await store2.LoadLastAcknowledgedUpdateIdAsync());

            var redeliveredItem = new TextPayloadItem { UpdateId = 1, MessageId = 10, Text = "Item 1" };
            bool isNew = await store2.UpsertAsync(redeliveredItem, default);

            // Must detect redelivery and refuse to append duplicate
            Assert.False(isNew);

            // Engine advances and saves offset
            await store2.SaveLastAcknowledgedUpdateIdAsync(1);

            var items = await store2.LoadLatestAsync(50);
            Assert.Single(items);
            Assert.Equal(1, items[0].UpdateId);
            Assert.Equal(1, await store2.LoadLastAcknowledgedUpdateIdAsync());
        }
    }

    [Fact]
    public async Task FilePayloadItem_LocalPathAndDetails_RoundTripSuccessfully()
    {
        using var sandbox = new WidgetTestSandbox();
        using var store = new MetroSyncHistoryStore(sandbox.Root);

        var fileItem = new FilePayloadItem
        {
            UpdateId = 1,
            MessageId = 100,
            FileName = "sample.pdf",
            FileSize = 54321,
            MimeType = "application/pdf",
            Caption = "Test document",
            LocalPath = @"C:\Users\User\Downloads\sample.pdf"
        };

        Assert.True(await store.AppendAsync(fileItem));

        var loaded = await store.LoadLatestAsync(10);
        Assert.Single(loaded);

        var loadedFile = Assert.IsType<FilePayloadItem>(loaded[0]);
        Assert.Equal("sample.pdf", loadedFile.FileName);
        Assert.Equal(54321, loadedFile.FileSize);
        Assert.Equal("application/pdf", loadedFile.MimeType);
        Assert.Equal("Test document", loadedFile.Caption);
        Assert.Equal(@"C:\Users\User\Downloads\sample.pdf", loadedFile.LocalPath);
    }

    [Fact]
    public async Task DeleteState_CleansUpOrphanedTmpFiles()
    {
        using var sandbox = new WidgetTestSandbox();
        using var store = new MetroSyncHistoryStore(sandbox.Root);

        await store.SaveLastAcknowledgedUpdateIdAsync(123);
        string dummyTmp = Path.Combine(sandbox.Root, "state.json.test.tmp");
        await File.WriteAllTextAsync(dummyTmp, "junk");

        store.DeleteState();

        Assert.False(File.Exists(store.StatePath));
        Assert.False(File.Exists(dummyTmp));
    }
}
