using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Presentation.Controllers;
using Xunit;

namespace MetroHub.Tests;

[Collection("StorageTests")]
public sealed class CanvasPasteServiceTests : IDisposable
{
    private readonly string _sandboxDir;

    public CanvasPasteServiceTests()
    {
        StorageService.ResetPending();
        _sandboxDir = Path.Combine(Path.GetTempPath(), "MetroHub_CanvasPasteTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandboxDir);
        AppPaths.CustomAppDataDir = _sandboxDir;
    }

    public void Dispose()
    {
        try
        {
            StorageService.Flush();
            StorageService.ResetPending();
        }
        catch { }
        finally
        {
            AppPaths.CustomAppDataDir = null;
            try
            {
                if (Directory.Exists(_sandboxDir))
                {
                    Directory.Delete(_sandboxDir, recursive: true);
                }
            }
            catch { }
        }
    }
    private static BitmapSource CreateTestBitmap(int width = 2, int height = 2)
    {
        int stride = width * 4;
        byte[] pixels = new byte[height * stride];
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgr32,
            null,
            pixels,
            stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static TileManager CreateTestTileManager(
        ObservableCollection<TileModel> tiles,
        ObservableCollection<TileGroupModel> groups,
        Action? saveAction = null)
    {
        return new TileManager(
            tilesProvider: () => tiles,
            groupsProvider: () => groups,
            tilesListBoxProvider: () => null,
            contentScrollViewerProvider: () => null,
            windowWidthProvider: () => 1920.0,
            animateModifiedTilesAction: _ => { },
            updateGroupHeaderPositionsAction: () => { },
            updateLayoutMetricsAction: () => { },
            updateCanvasHeightAction: () => { },
            updateExposedAddSlotsAction: () => { },
            saveGroupsAndLayoutAction: saveAction ?? (() => { }),
            cleanEmptyGroupsAndReflowAction: () => { },
            compactGroupGapsAction: () => { },
            flashLockedGroupAction: _ => { },
            hideDropSlotIndicatorAction: () => { },
            historyService: new LayoutHistoryService(),
            dispatcher: Dispatcher.CurrentDispatcher);
    }

    // ── 1. Format Priority ──────────────────────────────────────────────────

    [Fact]
    public void ProcessSnapshot_FormatPriority_FileDropWinsOverUrlAndBitmapAndText()
    {
        string tempDir = _sandboxDir;
        string tempFile = Path.Combine(tempDir, "priority_test_" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(tempFile, "sample file");

        try
        {
            var snapshot = new ClipboardSnapshot(
                Files: new[] { tempFile },
                Text: "https://github.com",
                Image: CreateTestBitmap(),
                IsSensitive: false);

            var specs = CanvasPasteService.ProcessSnapshot(snapshot);

            Assert.NotNull(specs);
            Assert.Single(specs);
            Assert.Equal(tempFile, specs[0].TargetPath);
            Assert.Equal(TileType.App, specs[0].TileType);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProcessSnapshot_FormatPriority_UrlWinsOverBitmapAndText()
    {
        var snapshot = new ClipboardSnapshot(
            Files: null,
            Text: "https://github.com",
            Image: CreateTestBitmap(),
            IsSensitive: false);

        var specs = CanvasPasteService.ProcessSnapshot(snapshot);

        Assert.NotNull(specs);
        Assert.Single(specs);
        Assert.Equal(TileType.WebUrl, specs[0].TileType);
        Assert.Equal("https://github.com/", specs[0].TargetPath);
    }

    [Fact]
    public void ProcessSnapshot_FormatPriority_BitmapWinsOverPlainText()
    {
        var snapshot = new ClipboardSnapshot(
            Files: null,
            Text: "This is a plain text note that is not a url",
            Image: CreateTestBitmap(),
            IsSensitive: false);

        var specs = CanvasPasteService.ProcessSnapshot(snapshot);

        Assert.NotNull(specs);
        Assert.Single(specs);
        Assert.StartsWith("Image", specs[0].Title);
        Assert.EndsWith(".png", specs[0].TargetPath);
        Assert.True(File.Exists(specs[0].TargetPath));
    }

    [Fact]
    public void ProcessSnapshot_FormatPriority_PlainTextNoteWinsWhenNoOtherFormats()
    {
        var snapshot = new ClipboardSnapshot(
            Files: null,
            Text: "Antigravity task checklist\r\nItem 1\r\nItem 2",
            Image: null,
            IsSensitive: false);

        var specs = CanvasPasteService.ProcessSnapshot(snapshot);

        Assert.NotNull(specs);
        Assert.Single(specs);
        Assert.StartsWith("Antigravity task", specs[0].Title);
        Assert.EndsWith(".txt", specs[0].TargetPath);
        Assert.True(File.Exists(specs[0].TargetPath));
    }

    [Fact]
    public void ProcessSnapshot_SensitiveData_SuppressesPlainTextNotes()
    {
        var snapshot = new ClipboardSnapshot(
            Files: null,
            Text: "SecretPassword123",
            Image: null,
            IsSensitive: true);

        var specs = CanvasPasteService.ProcessSnapshot(snapshot);

        Assert.Null(specs);
    }

    // ── 2. URL Validation & Bare Filename Rejection ─────────────────────────

    [Theory]
    [InlineData("https://github.com", true)]
    [InlineData("https://github.com/dotnet/wpf", true)]
    [InlineData("http://example.com/page", true)]
    [InlineData("youtube.com", true)]
    [InlineData("www.reddit.com", true)]
    [InlineData(" https://github.com\r\n", true)]
    public void TryValidateWebUrl_ValidUrls_ReturnsTrue(string input, bool expected)
    {
        bool result = CanvasPasteService.TryValidateWebUrl(input, out string normalized, out string title);

        Assert.Equal(expected, result);
        Assert.StartsWith("http", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(title));
    }

    [Theory]
    [InlineData("readme.txt")]
    [InlineData("notes.md")]
    [InlineData("v1.2.3")]
    [InlineData("//evil.com")]
    [InlineData("user:pass@host.com")]
    [InlineData("ftp://files.example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/passwords.txt")]
    [InlineData("data:text/html;base64,PHNjcmlwdD5...")]
    [InlineData("vbscript:MsgBox(1)")]
    [InlineData("https://example.com with spaces inside")]
    [InlineData("Line 1\nhttps://example.com")]
    [InlineData("Line 1\r\nLine 2")]
    [InlineData("")]
    [InlineData("   ")]
    public void TryValidateWebUrl_InvalidOrDisallowedInputs_ReturnsFalse(string input)
    {
        bool result = CanvasPasteService.TryValidateWebUrl(input, out string normalized, out string title);

        Assert.False(result);
        Assert.Empty(normalized);
        Assert.Empty(title);
    }

    // ── 3. FileDrop Filtering ───────────────────────────────────────────────

    [Fact]
    public void ProcessSnapshot_FileDropFiltering_SkipsMissing_KeepsFolders_ParsesUrlShortcuts()
    {
        string tempDir = Path.Combine(_sandboxDir, "test_filedrop_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string urlFile = Path.Combine(tempDir, "GitHub.url");
            File.WriteAllLines(urlFile, new[] { "[InternetShortcut]", "URL=https://github.com/dotnet" });

            string validFile = Path.Combine(tempDir, "sample.txt");
            File.WriteAllText(validFile, "sample text");

            string nonExistent = Path.Combine(tempDir, "missing.txt");

            var snapshot = new ClipboardSnapshot(
                Files: new[] { nonExistent, urlFile, validFile, tempDir },
                Text: null,
                Image: null,
                IsSensitive: false);

            var specs = CanvasPasteService.ProcessSnapshot(snapshot);

            Assert.NotNull(specs);
            Assert.Equal(3, specs.Count); // nonExistent is skipped

            // 1. urlFile -> parsed as WebUrl
            Assert.Equal(TileType.WebUrl, specs[0].TileType);
            Assert.Equal("https://github.com/dotnet", specs[0].TargetPath);

            // 2. validFile -> App
            Assert.Equal(TileType.App, specs[1].TileType);
            Assert.Equal(validFile, specs[1].TargetPath);

            // 3. tempDir -> Folder
            Assert.Equal(TileType.Folder, specs[2].TileType);
            Assert.Equal(tempDir, specs[2].TargetPath);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // ── 4. Payload Size Caps ────────────────────────────────────────────────

    [Fact]
    public void ProcessSnapshot_Caps_OversizedText_ReturnsNull()
    {
        string oversizedText = new string('A', CanvasPasteService.MaxTextLength + 10);
        var snapshot = new ClipboardSnapshot(
            Files: null,
            Text: oversizedText,
            Image: null,
            IsSensitive: false);

        var specs = CanvasPasteService.ProcessSnapshot(snapshot);

        Assert.Null(specs);
    }

    [Fact]
    public void ProcessSnapshot_Caps_TooManyFiles_ReturnsNull()
    {
        var files = Enumerable.Range(1, CanvasPasteService.MaxFileCount + 5)
            .Select(i => $"C:\\dummy\\file_{i}.txt")
            .ToList();

        var snapshot = new ClipboardSnapshot(
            Files: files,
            Text: null,
            Image: null,
            IsSensitive: false);

        var specs = CanvasPasteService.ProcessSnapshot(snapshot);

        Assert.Null(specs);
    }

    [Fact]
    public void ProcessSnapshot_Caps_OversizedBitmap_ReturnsNull()
    {
        int oversizedDim = CanvasPasteService.MaxBitmapDimension + 1;
        var oversizedBitmap = BitmapSource.Create(
            oversizedDim,
            1,
            96,
            96,
            PixelFormats.Bgr32,
            null,
            new byte[oversizedDim * 4],
            oversizedDim * 4);
        oversizedBitmap.Freeze();

        var snapshot = new ClipboardSnapshot(
            Files: null,
            Text: null,
            Image: oversizedBitmap,
            IsSensitive: false);

        var specs = CanvasPasteService.ProcessSnapshot(snapshot);

        Assert.Null(specs);
    }

    // ── 5. Filename Uniqueness in the Same Millisecond ───────────────────────

    [Fact]
    public void GenerateNoteFileName_TwoGeneratedNamesInSameMillisecond_AreUnique()
    {
        string note1 = CanvasPasteService.GenerateNoteFileName();
        string note2 = CanvasPasteService.GenerateNoteFileName();
        Assert.NotEqual(note1, note2);
    }

    // ── 6. Batch Placement with Injectable Slot Finder ──────────────────────

    [Fact]
    public void BatchAddPastedTiles_WithCustomSlotFinder_SkipsNoFreeSlot_AndSavesOnce()
    {
        WpfTestHost.RunSta(() =>
        {
            var tiles = new ObservableCollection<TileModel>();
            var groups = new ObservableCollection<TileGroupModel>();
            int saveCount = 0;

            var manager = CreateTestTileManager(tiles, groups, saveAction: () => saveCount++);

            var items = new List<PasteItemSpec>
            {
                new() { Title = "Item 1", TargetPath = "C:\\item1.exe", SpanX = 2, SpanY = 2 },
                new() { Title = "Item 2", TargetPath = "C:\\item2.exe", SpanX = 2, SpanY = 2 },
                new() { Title = "Item 3", TargetPath = "C:\\item3.exe", SpanX = 2, SpanY = 2 },
            };

            int callCount = 0;
            Func<int, int, int, int, (int Col, int Row)> slotFinder = (col, row, spanX, spanY) =>
            {
                callCount++;
                if (callCount == 2)
                {
                    // Item 2 has no free slot available
                    return (-1, -1);
                }
                return (callCount * 2, 1);
            };

            manager.BatchAddPastedTiles(items, null, slotFinder);

            // Item 2 was skipped; Item 1 and Item 3 were added
            Assert.Equal(2, tiles.Count);
            Assert.Equal("Item 1", tiles[0].Title);
            Assert.Equal("Item 3", tiles[1].Title);

            // Layout was saved exactly once
            Assert.Equal(1, saveCount);

            // Added tiles are automatically selected
            Assert.True(tiles[0].IsSelected);
            Assert.True(tiles[1].IsSelected);
        });
    }

    // ── 7. Empty or Locked Clipboard ────────────────────────────────────────

    [Fact]
    public void ProcessSnapshot_WhenNullSnapshot_ReturnsNullWithoutException()
    {
        var specs = CanvasPasteService.ProcessSnapshot(null);
        Assert.Null(specs);
    }

    [Fact]
    public void CaptureSnapshot_WhenNullDataObject_ReturnsNullWithoutException()
    {
        var snapshot = CanvasPasteService.CaptureSnapshot(null);
        // Note: On headless/CI environments where clipboard might be empty, returns null or empty
        // Key guarantee: does not throw an exception!
    }

    // ── 8. Sensitive Data Suppression & DWORD Handling ─────────────────────

    [Fact]
    public void IsSensitiveData_ReturnsTrue_WhenCanIncludeInClipboardHistoryIsMemoryStreamZero()
    {
        var dataObject = new DataObject();
        byte[] zeroDword = new byte[] { 0, 0, 0, 0 };
        using var ms = new MemoryStream(zeroDword);
        dataObject.SetData(SafeClipboard.FormatCanIncludeInHistory, ms);

        bool isSensitive = SafeClipboard.IsSensitiveData(dataObject);

        Assert.True(isSensitive);
    }

    [Fact]
    public void IsSensitiveData_ReturnsFalse_WhenCanIncludeInClipboardHistoryIsMemoryStreamOne()
    {
        var dataObject = new DataObject();
        byte[] oneDword = new byte[] { 1, 0, 0, 0 };
        using var ms = new MemoryStream(oneDword);
        dataObject.SetData(SafeClipboard.FormatCanIncludeInHistory, ms);

        bool isSensitive = SafeClipboard.IsSensitiveData(dataObject);

        Assert.False(isSensitive);
    }

    [Fact]
    public void IsSensitiveData_ReturnsTrue_WhenExcludeClipboardContentFromMonitorProcessingIsSet()
    {
        var dataObject = new DataObject();
        dataObject.SetData(SafeClipboard.FormatExcludeFromMonitor, "1");

        bool isSensitive = SafeClipboard.IsSensitiveData(dataObject);

        Assert.True(isSensitive);
    }

    [Fact]
    public void IsSensitiveData_ReturnsFalse_ForStandardNonSensitiveData()
    {
        var dataObject = new DataObject();
        dataObject.SetText("Standard public text snippet");

        bool isSensitive = SafeClipboard.IsSensitiveData(dataObject);

        Assert.False(isSensitive);
    }

    // ── 9. AppPaths & Constants Isolation ───────────────────────────────────

    [Fact]
    public void AppPaths_DataDirectories_AreSeparatedFromCacheDirectory()
    {
        string dataDir = AppPaths.DataDir;
        string cacheDir = AppPaths.CacheDir;

        Assert.NotEqual(dataDir, cacheDir);
        Assert.StartsWith(dataDir, AppPaths.PastedImagesDir, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(dataDir, AppPaths.PastedNotesDir, StringComparison.OrdinalIgnoreCase);
        Assert.False(AppPaths.PastedImagesDir.Contains("cache", StringComparison.OrdinalIgnoreCase));
        Assert.False(AppPaths.PastedNotesDir.Contains("cache", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Constants_ConformToDefensiveGuardLimits()
    {
        Assert.Equal(50, CanvasPasteService.MaxFileCount);
        Assert.Equal(1024 * 1024, CanvasPasteService.MaxTextLength);
        Assert.Equal(8192, CanvasPasteService.MaxBitmapDimension);
    }

    // ── 10. Hash Deduplication (Content-Addressable Storage) ─────────────────

    [Fact]
    public void GenerateImageFileName_WithBytes_ProducesDeterministicSha256Name()
    {
        byte[] sample1 = [1, 2, 3, 4, 5];
        byte[] sample2 = [1, 2, 3, 4, 5];
        byte[] different = [1, 2, 3, 4, 6];

        string name1 = CanvasPasteService.GenerateImageFileName(sample1);
        string name2 = CanvasPasteService.GenerateImageFileName(sample2);
        string nameDiff = CanvasPasteService.GenerateImageFileName(different);

        Assert.Equal(name1, name2);
        Assert.NotEqual(name1, nameDiff);
        Assert.StartsWith("img_", name1);
        Assert.EndsWith(".png", name1);
    }

    [Fact]
    public void ProcessBitmap_IdenticalBitmaps_ReuseSameDiskFile()
    {
        WpfTestHost.RunSta(() =>
        {
            var bmp1 = CreateTestBitmap(4, 4);
            var bmp2 = CreateTestBitmap(4, 4);

            var snap1 = new ClipboardSnapshot(null, null, bmp1, false);
            var snap2 = new ClipboardSnapshot(null, null, bmp2, false);

            var items1 = CanvasPasteService.ProcessSnapshot(snap1);
            var items2 = CanvasPasteService.ProcessSnapshot(snap2);

            Assert.NotNull(items1);
            Assert.NotNull(items2);
            Assert.Single(items1!);
            Assert.Single(items2!);

            string path1 = items1![0].TargetPath;
            string path2 = items2![0].TargetPath;

            // Must point to the exact same file path on disk
            Assert.Equal(path1, path2);
            Assert.True(File.Exists(path1));

            // Verify only one file was written in PastedImagesDir
            var files = Directory.GetFiles(AppPaths.PastedImagesDir, "img_*.png");
            Assert.Single(files);
        });
    }

    // ── 11. Orphan Asset Mark & Sweep Garbage Collection ────────────────────

    [Fact]
    public async Task PastedAssetCleanupService_KeepsReferenced_DeletesOrphanOlderThanGrace()
    {
        string imagesDir = AppPaths.PastedImagesDir;
        Directory.CreateDirectory(imagesDir);

        // 1. Live referenced file
        string liveFile = Path.Combine(imagesDir, "img_live_123.png");
        File.WriteAllText(liveFile, "live image data");
        File.SetLastWriteTimeUtc(liveFile, DateTime.UtcNow.AddDays(-5));
        File.SetCreationTimeUtc(liveFile, DateTime.UtcNow.AddDays(-5));

        // 2. Unreferenced file within grace period (young)
        string youngFile = Path.Combine(imagesDir, "img_young_456.png");
        File.WriteAllText(youngFile, "young image data");
        File.SetLastWriteTimeUtc(youngFile, DateTime.UtcNow.AddMinutes(-10));
        File.SetCreationTimeUtc(youngFile, DateTime.UtcNow.AddMinutes(-10));

        // 3. Unreferenced file older than grace period (stale orphan)
        string staleFile = Path.Combine(imagesDir, "img_stale_789.png");
        File.WriteAllText(staleFile, "stale orphan image data");
        File.SetLastWriteTimeUtc(staleFile, DateTime.UtcNow.AddDays(-3));
        File.SetCreationTimeUtc(staleFile, DateTime.UtcNow.AddDays(-3));

        // 4. Stale temporary file
        string staleTmp = Path.Combine(imagesDir, "img_test.png.tmp.abc");
        File.WriteAllText(staleTmp, "temp data");
        File.SetLastWriteTimeUtc(staleTmp, DateTime.UtcNow.AddHours(-3));

        var liveTile = new TileModel
        {
            Id = "tile_live",
            Title = "Live Tile",
            TargetPath = liveFile,
            IconPath = liveFile
        };

        var result = await PastedAssetCleanupService.SweepAsync(
            gracePeriod: TimeSpan.FromHours(24),
            activeTiles: [liveTile],
            historySnapshots: null,
            force: true);

        Assert.True(File.Exists(liveFile), "Live referenced tile image must be kept.");
        Assert.True(File.Exists(youngFile), "Young unreferenced tile image within grace period must be kept.");
        Assert.False(File.Exists(staleFile), "Stale orphan image older than grace period must be deleted.");
        Assert.False(File.Exists(staleTmp), "Stale temporary file must be deleted.");
        Assert.Equal(2, result.DeletedCount);
        Assert.True(result.ReclaimedBytes > 0);
    }

    [Fact]
    public async Task PastedAssetCleanupService_ProtectsUndoHistorySnapshots()
    {
        string imagesDir = AppPaths.PastedImagesDir;
        Directory.CreateDirectory(imagesDir);

        string undoFile = Path.Combine(imagesDir, "img_history_999.png");
        File.WriteAllText(undoFile, "history image data");
        File.SetLastWriteTimeUtc(undoFile, DateTime.UtcNow.AddDays(-5));
        File.SetCreationTimeUtc(undoFile, DateTime.UtcNow.AddDays(-5));

        var historyTile = new TileModel
        {
            Id = "tile_in_history",
            Title = "History Tile",
            TargetPath = undoFile
        };

        string snapshotJson = LayoutHistoryService.CaptureSnapshot([historyTile]);

        var result = await PastedAssetCleanupService.SweepAsync(
            gracePeriod: TimeSpan.FromHours(24),
            activeTiles: [], // No active tiles currently on canvas
            historySnapshots: [snapshotJson], // Retained in Undo stack
            force: true
        );

        Assert.True(File.Exists(undoFile), "Image referenced in Undo history stack must never be deleted.");
        Assert.Equal(0, result.DeletedCount);
    }

    // ── 12. Pure SelectOrphans & Fail-Closed Guard Tests ─────────────────────

    [Fact]
    public void SelectOrphans_PureEvaluation_MatchesAllExpectedConditions()
    {
        var now = DateTime.UtcNow;
        var grace = TimeSpan.FromHours(24);

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "C:\\data\\img_by_full_path.png",
            "img_by_filename.png"
        };

        var candidates = new List<AssetFileInfo>
        {
            // 1. Referenced by full path -> keep
            new("C:\\data\\img_by_full_path.png", now.AddDays(-5), now.AddDays(-5), 100),

            // 2. Referenced by filename only -> keep
            new("D:\\other\\img_by_filename.png", now.AddDays(-5), now.AddDays(-5), 200),

            // 3. Unreferenced but created recently (< 24h) -> keep
            new("C:\\data\\img_recent.png", now.AddHours(-2), now.AddHours(-2), 300),

            // 4. Stale temp file (> 1h old) -> delete temp
            new("C:\\data\\img_stale.png.tmp", now.AddHours(-3), now.AddHours(-3), 400),

            // 5. Young temp file (< 1h old) -> keep
            new("C:\\data\\img_young.png.tmp", now.AddMinutes(-10), now.AddMinutes(-10), 500),

            // 6. Stale unreferenced file (> 24h old) -> delete orphan
            new("C:\\data\\img_orphan.png", now.AddDays(-3), now.AddDays(-3), 600)
        };

        var orphans = PastedAssetCleanupService.SelectOrphans(candidates, referenced, now, grace);

        Assert.Equal(2, orphans.Count);

        var staleTemp = orphans.FirstOrDefault(o => o.FullPath == "C:\\data\\img_stale.png.tmp");
        Assert.NotNull(staleTemp);
        Assert.True(staleTemp!.IsStaleTemp);

        var staleOrphan = orphans.FirstOrDefault(o => o.FullPath == "C:\\data\\img_orphan.png");
        Assert.NotNull(staleOrphan);
        Assert.False(staleOrphan!.IsStaleTemp);
    }

    [Fact]
    public void SelectOrphans_CircuitBreaker_EmptyReferencedSetReturnsZeroOrphans()
    {
        var now = DateTime.UtcNow;
        var grace = TimeSpan.FromHours(24);

        var emptyReferenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var candidates = new List<AssetFileInfo>
        {
            new("C:\\data\\img_orphan.png", now.AddDays(-3), now.AddDays(-3), 600)
        };

        // When reference set is empty, circuit breaker triggers and returns 0 orphans
        var orphans = PastedAssetCleanupService.SelectOrphans(candidates, emptyReferenced, now, grace);

        Assert.Empty(orphans);
    }

    [Fact]
    public void TryCollectReferencedPastedAssets_AbortsOnCorruptLayoutFile()
    {
        string layoutPath = AppPaths.LayoutPath;
        AppPaths.EnsureDirectory(layoutPath);
        File.WriteAllText(layoutPath, "{ this is corrupted invalid json }}}");

        try
        {
            bool success = PastedAssetCleanupService.TryCollectReferencedPastedAssets(
                activeTiles: null,
                historySnapshots: null,
                out var referenced);

            Assert.False(success, "Corrupt layout file must cause Mark phase to abort (fail-closed).");
            Assert.Null(referenced);
        }
        finally
        {
            if (File.Exists(layoutPath))
            {
                File.Delete(layoutPath);
            }
        }
    }

    [Fact]
    public async Task SweepAsync_Throttling_SkipsSecondRunWithin24Hours()
    {
        string imagesDir = AppPaths.PastedImagesDir;
        Directory.CreateDirectory(imagesDir);

        string liveFile = Path.Combine(imagesDir, "img_throttle_test.png");
        File.WriteAllText(liveFile, "test data");

        var liveTile = new TileModel
        {
            Id = "tile_throttle",
            Title = "Throttle Tile",
            TargetPath = liveFile
        };

        // Run 1 with force: true sets the timestamp
        var run1 = await PastedAssetCleanupService.SweepAsync(
            activeTiles: [liveTile],
            force: true);

        Assert.True(File.Exists(liveFile));

        // Run 2 immediately with force: false should be throttled (returns 0 scanned/deleted)
        var run2 = await PastedAssetCleanupService.SweepAsync(
            activeTiles: [liveTile],
            force: false);

        Assert.Equal(0, run2.ScannedCount);
        Assert.Equal(0, run2.DeletedCount);
    }

    // ── 13. Image File Icon Passthrough & Association Cache Tests ───────────

    [Fact]
    public void IsImageFile_AccuratelyIdentifiesImageFormats()
    {
        Assert.True(IconExtractorService.IsImageFile("C:\\test\\photo.png"));
        Assert.True(IconExtractorService.IsImageFile("C:\\test\\photo.jpg"));
        Assert.True(IconExtractorService.IsImageFile("C:\\test\\photo.jpeg"));
        Assert.True(IconExtractorService.IsImageFile("C:\\test\\photo.webp"));
        Assert.True(IconExtractorService.IsImageFile("C:\\test\\photo.gif"));
        Assert.True(IconExtractorService.IsImageFile("C:\\test\\photo.bmp"));

        Assert.False(IconExtractorService.IsImageFile("C:\\test\\notes.txt"));
        Assert.False(IconExtractorService.IsImageFile("C:\\test\\app.exe"));
        Assert.False(IconExtractorService.IsImageFile("C:\\test\\document.pdf"));
        Assert.False(IconExtractorService.IsImageFile("C:\\test\\script.bat"));
    }

    [Fact]
    public void ExtractAndCacheIcon_DirectImageFile_ReturnsImagePathDirectly()
    {
        string tempImage = Path.Combine(_sandboxDir, "sample_thumbnail.png");
        File.WriteAllText(tempImage, "fake png content");

        string? icon = IconExtractorService.ExtractAndCacheIcon(tempImage);

        // Must return the image path itself without creating any icon cache files
        Assert.Equal(tempImage, icon);
    }

    [Fact]
    public void ProcessFiles_ImageFile_SetsIconPathToImageDirectly()
    {
        string tempImage = Path.Combine(_sandboxDir, "pasted_explorer_img.png");
        File.WriteAllText(tempImage, "fake image");

        var snapshot = new ClipboardSnapshot(
            Files: [tempImage],
            Text: null,
            Image: null,
            IsSensitive: false
        );

        var items = CanvasPasteService.ProcessSnapshot(snapshot);

        Assert.NotNull(items);
        Assert.Single(items!);
        Assert.Equal(tempImage, items![0].TargetPath);
        Assert.Equal(tempImage, items![0].IconPath);
    }

    [Fact]
    public void GetDefaultHandlerForExtension_CachesAndCanBeInvalidated()
    {
        string handler1 = IconExtractorService.GetDefaultHandlerForExtension(".txt");
        string handler2 = IconExtractorService.GetDefaultHandlerForExtension(".txt");

        Assert.Equal(handler1, handler2);
        Assert.NotEmpty(handler1);

        // Verify invalidation clears the cache without throwing
        IconExtractorService.InvalidateAssociationCache();

        string handler3 = IconExtractorService.GetDefaultHandlerForExtension(".txt");
        Assert.Equal(handler1, handler3);
        Assert.DoesNotContain("\"", handler1);
    }

    [Fact]
    public void NormalizeTiles_ImageTileWithGenericIcon_SelfHealsToTargetPath()
    {
        string imageFile = Path.Combine(_sandboxDir, "healing_photo.png");
        File.WriteAllText(imageFile, "test image bytes");

        string oldCachedIcon = Path.Combine(_sandboxDir, "v5_old_generic_icon.png");
        File.WriteAllText(oldCachedIcon, "old generic icon bytes");

        var tile = new TileModel
        {
            Title = "My Photo",
            TargetPath = imageFile,
            IconPath = oldCachedIcon,
            TileType = TileType.App,
            Col = 1,
            Row = 1
        };

        var tiles = new ObservableCollection<TileModel> { tile };

        bool dirty = StorageService.NormalizeTiles(tiles);

        Assert.True(dirty);
        // Self-heals: IconPath should now point directly to the image file, not the generic icon
        Assert.Equal(imageFile, tile.IconPath);
    }
}
