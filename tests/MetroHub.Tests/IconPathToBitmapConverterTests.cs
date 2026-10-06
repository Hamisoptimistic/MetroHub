using System;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MetroHub.Presentation.Converters;
using Xunit;

namespace MetroHub.Tests;

public class IconPathToBitmapConverterTests
{
    private static string CreateTempPng(int width, int height)
    {
        string path = Path.Combine(Path.GetTempPath(), $"metrohub_test_{Guid.NewGuid():N}.png");
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.DodgerBlue, null, new System.Windows.Rect(0, 0, width, height));
        }
        rtb.Render(dv);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    [Fact]
    public void Convert_NullOrWhitespace_ReturnsNull()
    {
        var converter = new IconPathToBitmapConverter();
        Assert.Null(converter.Convert(null, typeof(ImageSource), null, CultureInfo.InvariantCulture));
        Assert.Null(converter.Convert("", typeof(ImageSource), null, CultureInfo.InvariantCulture));
        Assert.Null(converter.Convert("   ", typeof(ImageSource), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Convert_NonExistentFile_ReturnsNullSafely()
    {
        var converter = new IconPathToBitmapConverter();
        var result = converter.Convert("C:\\non_existent_file_12345.png", typeof(ImageSource), null, CultureInfo.InvariantCulture);
        Assert.Null(result);
    }

    [Fact]
    public void Convert_ValidImage_ReturnsFrozenBitmapWithClampedWidth()
    {
        WpfTestHost.RunSta(() =>
        {
            // Create a large 256x256 image that would otherwise land on LOH
            string tempImage = CreateTempPng(256, 256);
            try
            {
                var converter = new IconPathToBitmapConverter();
                var result = converter.Convert(tempImage, typeof(ImageSource), 96, CultureInfo.InvariantCulture) as BitmapImage;

                Assert.NotNull(result);
                Assert.True(result.IsFrozen);
                Assert.Equal(96, result.DecodePixelWidth);
            }
            finally
            {
                if (File.Exists(tempImage))
                {
                    try { File.Delete(tempImage); } catch { }
                }
            }
        });
    }

    [Fact]
    public void Convert_IdenticalPath_ReturnsSameCachedInstance()
    {
        WpfTestHost.RunSta(() =>
        {
            string tempImage = CreateTempPng(128, 128);
            try
            {
                var converter = new IconPathToBitmapConverter();
                var first = converter.Convert(tempImage, typeof(ImageSource), 96, CultureInfo.InvariantCulture);
                var second = converter.Convert(tempImage, typeof(ImageSource), 96, CultureInfo.InvariantCulture);

                Assert.NotNull(first);
                Assert.NotNull(second);
                Assert.Same(first, second);
            }
            finally
            {
                if (File.Exists(tempImage))
                {
                    try { File.Delete(tempImage); } catch { }
                }
            }
        });
    }
}
