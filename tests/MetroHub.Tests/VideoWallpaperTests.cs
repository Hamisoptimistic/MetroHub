using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Presentation.Controllers;
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

    [Fact]
    public void VideoWallpaper_MediaElement_ConfiguredForManualControl()
    {
        WpfTestHost.RunSta(() =>
        {
            var element = new MediaElement
            {
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Manual,
                Volume = 0,
                IsMuted = true
            };

            Assert.Equal(MediaState.Manual, element.LoadedBehavior);
            Assert.Equal(MediaState.Manual, element.UnloadedBehavior);
            Assert.Equal(0, element.Volume);
            Assert.True(element.IsMuted);
            element.IsHitTestVisible = false;
            element.Focusable = false;
            Assert.False(element.IsHitTestVisible);
            Assert.False(element.Focusable);
        });
    }

    [Fact]
    public void VideoWallpaper_Teardown_ClearsSourceAndVisibility()
    {
        WpfTestHost.RunSta(() =>
        {
            var window = new Window();
            var video = new MediaElement
            {
                Source = new Uri("http://localhost/dummy.mp4"),
                Visibility = Visibility.Visible
            };

            using var mgr = new BackdropManager(
                window,
                () => new AppSettings(),
                null, null, null,
                video,
                null, null, null, null,
                (_, _) => { });

            mgr.TeardownWallpaperVideo();

            Assert.Null(video.Source);
            Assert.Equal(Visibility.Collapsed, video.Visibility);
        });
    }

    [Theory]
    [InlineData("sample.webm", "WebM codec missing")]
    [InlineData("movie.mkv", "HEVC Video Extensions")]
    [InlineData("video.mov", "QuickTime codec missing")]
    [InlineData("clip.flv", "Flash Video codec missing")]
    [InlineData("capture.ts", "MPEG-2 codec missing")]
    [InlineData("unknown.xyz", "Unsupported video codec")]
    public void VideoWallpaper_MissingCodecMessage_ProvidesActionableGuidance(string file, string expectedSubstring)
    {
        string message = BackdropManager.GetMissingCodecMessage(file);
        Assert.Contains(expectedSubstring, message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VideoWallpaper_OpensLocalVideoSuccessfully_WhenFileExists()
    {
        string videoPath = @"C:\Users\HamB\Pictures\Downloads\Video\neon-harbor-city-pixel-art-moewalls-com.mp4";
        if (!File.Exists(videoPath)) return;

        bool opened = false;
        string? failureReason = null;

        WpfTestHost.RunSta(() =>
        {
            var element = new MediaElement
            {
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Manual,
                Volume = 0,
                IsMuted = true
            };

            element.MediaOpened += (_, _) => opened = true;
            element.MediaFailed += (_, e) => failureReason = e.ErrorException?.ToString() ?? "Unknown MediaFailed";

            element.Source = new Uri(videoPath, UriKind.Absolute);
            element.Play();

            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) => frame.Continue = false;
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);

            element.Stop();
            element.Close();
        });

        Assert.Null(failureReason);
        Assert.True(opened);
    }
}
