using MetroHub.Core.Services;
using Xunit;

namespace MetroHub.Tests;

public class VideoWallpaperTests
{
    [Theory]
    [InlineData("wallpaper.mp4", true)]
    [InlineData("clip.WEBM", true)]
    [InlineData("movie.mkv", true)]
    [InlineData("video.mov", true)]
    [InlineData("trailer.qt", true)]
    [InlineData("old.avi", true)]
    [InlineData("stream.wmv", true)]
    [InlineData("file.asf", true)]
    [InlineData("flash.flv", true)]
    [InlineData("flash2.f4v", true)]
    [InlineData("capture.ts", true)]
    [InlineData("dvd.vob", true)]
    [InlineData("sample.mpg", true)]
    [InlineData("sample.mpeg", true)]
    [InlineData("mobile.3gp", true)]
    [InlineData("divx_video.divx", true)]
    [InlineData("xvid_video.xvid", true)]
    [InlineData("photo.png", false)]
    [InlineData("photo.jpg", false)]
    [InlineData("photo.jpeg", false)]
    [InlineData("photo.webp", false)]
    [InlineData("document.pdf", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsVideoWallpaper_ValidatesSupportedFormats(string? path, bool expected)
    {
        bool actual = DailyWallpaperService.IsVideoWallpaper(path);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void GetWallpaperFileDialogFilter_IncludesVideoAndImageFormats()
    {
        string filter = DailyWallpaperService.GetWallpaperFileDialogFilter();
        Assert.Contains("*.mp4", filter);
        Assert.Contains("*.webm", filter);
        Assert.Contains("*.mkv", filter);
        Assert.Contains("*.mov", filter);
        Assert.Contains("*.avi", filter);
        Assert.Contains("*.png", filter);
        Assert.Contains("*.jpg", filter);
    }
}
