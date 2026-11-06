using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MetroHub.Tests;
using MetroHub.Widgets.Catalog.MetroSync.Services;
using Xunit;

namespace MetroHub.Tests.Widgets.MetroSync;

[Collection("MetroSyncTests")]
public sealed class MetroSyncThumbnailsTests
{
    private static MemoryStream CreateTestPngStream(int width, int height)
    {
        int stride = width * 4;
        byte[] pixels = new byte[height * stride];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 200;     // B
            pixels[i + 1] = 100; // G
            pixels[i + 2] = 50;  // R
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
            pixels[i] = 10;
            pixels[i + 1] = 200;
            pixels[i + 2] = 10;
            pixels[i + 3] = 255;
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    [Fact]
    public void CreateFrozenThumbnail_Landscape_DownscalesTo200WidthAndFreezes()
    {
        WpfTestHost.RunSta(() =>
        {
            using var stream = CreateTestPngStream(800, 400);

            var bmp = MetroSyncThumbnails.CreateFrozenThumbnail(stream);

            Assert.True(bmp.IsFrozen);
            Assert.Equal(200, bmp.PixelWidth);
            Assert.Equal(100, bmp.PixelHeight);
        });
    }

    [Fact]
    public void CreateFrozenThumbnail_Portrait_DownscalesTo200HeightAndFreezes()
    {
        WpfTestHost.RunSta(() =>
        {
            using var stream = CreateTestPngStream(400, 800);

            var bmp = MetroSyncThumbnails.CreateFrozenThumbnail(stream);

            Assert.True(bmp.IsFrozen);
            Assert.Equal(100, bmp.PixelWidth);
            Assert.Equal(200, bmp.PixelHeight);
        });
    }

    [Fact]
    public void CreateFrozenThumbnail_Square_DownscalesTo200()
    {
        WpfTestHost.RunSta(() =>
        {
            using var stream = CreateTestPngStream(600, 600);

            var bmp = MetroSyncThumbnails.CreateFrozenThumbnail(stream);

            Assert.True(bmp.IsFrozen);
            Assert.Equal(200, bmp.PixelWidth);
            Assert.Equal(200, bmp.PixelHeight);
        });
    }

    [Fact]
    public void CreateFrozenThumbnail_SmallImage_DoesNotUpscale()
    {
        WpfTestHost.RunSta(() =>
        {
            using var stream = CreateTestPngStream(120, 80);

            var bmp = MetroSyncThumbnails.CreateFrozenThumbnail(stream);

            Assert.True(bmp.IsFrozen);
            Assert.Equal(120, bmp.PixelWidth);
            Assert.Equal(80, bmp.PixelHeight);
        });
    }

    [Fact]
    public void SaveAndLoadThumbnail_FromStream_RoundTripSucceeds()
    {
        WpfTestHost.RunSta(() =>
        {
            using var sandbox = new WidgetTestSandbox();
            var thumbnails = new MetroSyncThumbnails(sandbox.Root);

            using var stream = CreateTestPngStream(800, 600);
            const long messageId = 778899;

            string? savedPath = thumbnails.SaveThumbnailAsync(messageId, stream).GetAwaiter().GetResult();

            Assert.NotNull(savedPath);
            Assert.True(File.Exists(savedPath));
            Assert.Equal(thumbnails.GetThumbnailPath(messageId), savedPath);

            var loaded = thumbnails.LoadThumbnail(messageId);
            Assert.NotNull(loaded);
            Assert.True(loaded.IsFrozen);
            Assert.Equal(200, loaded.PixelWidth);
            Assert.Equal(150, loaded.PixelHeight);
        });
    }

    [Fact]
    public void SaveAndLoadThumbnail_FromBitmapSource_RoundTripSucceeds()
    {
        WpfTestHost.RunSta(() =>
        {
            using var sandbox = new WidgetTestSandbox();
            var thumbnails = new MetroSyncThumbnails(sandbox.Root);

            var source = CreateTestBitmapSource(600, 300);
            const long messageId = 112233;

            string? savedPath = thumbnails.SaveThumbnail(messageId, source);

            Assert.NotNull(savedPath);
            Assert.True(File.Exists(savedPath));

            var loaded = thumbnails.LoadThumbnail(messageId);
            Assert.NotNull(loaded);
            Assert.True(loaded.IsFrozen);
            Assert.Equal(200, loaded.PixelWidth);
            Assert.Equal(100, loaded.PixelHeight);
        });
    }

    [Fact]
    public void LoadThumbnail_MissingFile_ReturnsNull()
    {
        WpfTestHost.RunSta(() =>
        {
            using var sandbox = new WidgetTestSandbox();
            var thumbnails = new MetroSyncThumbnails(sandbox.Root);

            var loaded = thumbnails.LoadThumbnail(99999999);
            Assert.Null(loaded);
        });
    }

    [Fact]
    public void LoadThumbnail_CorruptFile_ReturnsNullWithoutThrowing()
    {
        WpfTestHost.RunSta(() =>
        {
            using var sandbox = new WidgetTestSandbox();
            var thumbnails = new MetroSyncThumbnails(sandbox.Root);

            Directory.CreateDirectory(thumbnails.ThumbnailsDirectory);
            string path = thumbnails.GetThumbnailPath(55555);
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

            var loaded = thumbnails.LoadThumbnail(55555);
            Assert.Null(loaded);
        });
    }

    [Fact]
    public void SaveThumbnailAsync_UnfrozenBitmapSource_SafelyMarshalsAndSaves()
    {
        WpfTestHost.RunSta(() =>
        {
            using var sandbox = new WidgetTestSandbox();
            var thumbnails = new MetroSyncThumbnails(sandbox.Root);

            int width = 400;
            int height = 200;
            int stride = width * 4;
            byte[] pixels = new byte[height * stride];
            var unfrozenSource = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            // Intentionally not frozen

            const long messageId = 665544;
            string? saved = thumbnails.SaveThumbnailAsync(messageId, unfrozenSource).GetAwaiter().GetResult();

            Assert.NotNull(saved);
            Assert.True(File.Exists(saved));

            var loaded = thumbnails.LoadThumbnail(messageId);
            Assert.NotNull(loaded);
            Assert.True(loaded.IsFrozen);
            Assert.Equal(200, loaded.PixelWidth);
            Assert.Equal(100, loaded.PixelHeight);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void CreateFrozenThumbnail_NonPositiveDimension_ThrowsArgumentOutOfRangeException(int dimension)
    {
        using var stream = CreateTestPngStream(100, 100);
        Assert.Throws<ArgumentOutOfRangeException>(() => MetroSyncThumbnails.CreateFrozenThumbnail(stream, dimension));
    }

    [Fact]
    public void LoadThumbnail_ZeroByteFile_ReturnsNull()
    {
        WpfTestHost.RunSta(() =>
        {
            using var sandbox = new WidgetTestSandbox();
            var thumbnails = new MetroSyncThumbnails(sandbox.Root);

            Directory.CreateDirectory(thumbnails.ThumbnailsDirectory);
            string path = thumbnails.GetThumbnailPath(1234);
            File.WriteAllBytes(path, Array.Empty<byte>());

            var loaded = thumbnails.LoadThumbnail(1234);
            Assert.Null(loaded);
        });
    }
}
