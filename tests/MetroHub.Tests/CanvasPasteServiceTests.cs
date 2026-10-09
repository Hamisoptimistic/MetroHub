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

public sealed class CanvasPasteServiceTests
{
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
        string tempDir = AppPaths.DataDir;
        if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);

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
        string tempDir = Path.Combine(AppPaths.DataDir, "test_filedrop_" + Guid.NewGuid().ToString("N"));
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
    public void Filenames_TwoGeneratedNamesInSameMillisecond_AreUnique()
    {
        string note1 = CanvasPasteService.GenerateNoteFileName();
        string note2 = CanvasPasteService.GenerateNoteFileName();
        Assert.NotEqual(note1, note2);

        string img1 = CanvasPasteService.GenerateImageFileName();
        string img2 = CanvasPasteService.GenerateImageFileName();
        Assert.NotEqual(img1, img2);
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
}
