using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using MetroHub.Core.Services;
using MetroHub.Presentation.Dialogs;
using MetroHub.Presentation.Converters;
using Xunit;

namespace MetroHub.Tests;

public class WebLinkDialogTests
{
    [Fact]
    public void GetCandidateSourceUrls_PathUrlWithHtmlHead_ReturnsFiveSourcesInCorrectPriority()
    {
        string url = "https://github.com/torvalds/linux";
        string htmlHeadIcon = "https://github.githubassets.com/favicons/favicon.png";

        var sources = WebFaviconService.GetCandidateSourceUrls(url, htmlHeadIcon);

        Assert.Equal(5, sources.Count);
        // Source 1: HTML head icon
        Assert.Equal(htmlHeadIcon, sources[0]);
        // Source 2: Google S2 path-aware
        Assert.Contains("domain=https%3A%2F%2Fgithub.com%2Ftorvalds%2Flinux", sources[1]);
        Assert.Contains("sz=128", sources[1]);
        // Source 3: Google S2 domain fallback
        Assert.Contains("domain=github.com", sources[2]);
        Assert.Contains("sz=128", sources[2]);
        // Source 4: DuckDuckGo
        Assert.Equal("https://icons.duckduckgo.com/ip3/github.com.ico", sources[3]);
        // Source 5: Direct website root
        Assert.Equal("https://github.com/favicon.ico", sources[4]);
    }

    [Fact]
    public void GetCandidateSourceUrls_RootDomainWithHtmlHead_ReturnsFourSources()
    {
        string url = "https://youtube.com/";
        string htmlHeadIcon = "https://www.youtube.com/s/desktop/abcdef/img/favicon.ico";

        var sources = WebFaviconService.GetCandidateSourceUrls(url, htmlHeadIcon);

        Assert.Equal(4, sources.Count);
        // Source 1: HTML head icon
        Assert.Equal(htmlHeadIcon, sources[0]);
        // Source 2: Google S2
        Assert.Contains("domain=https%3A%2F%2Fyoutube.com%2F", sources[1]);
        // Source 3: DuckDuckGo
        Assert.Equal("https://icons.duckduckgo.com/ip3/youtube.com.ico", sources[2]);
        // Source 4: Direct website root
        Assert.Equal("https://youtube.com/favicon.ico", sources[3]);
    }

    [Fact]
    public void GetCandidateSourceUrls_PathWithoutHtmlHead_ReturnsFourSources()
    {
        string url = "https://github.com/torvalds/linux";

        var sources = WebFaviconService.GetCandidateSourceUrls(url, null);

        Assert.Equal(4, sources.Count);
        // Source 1: Google S2 path
        Assert.Contains("domain=https%3A%2F%2Fgithub.com%2Ftorvalds%2Flinux", sources[0]);
        // Source 2: Google S2 domain
        Assert.Contains("domain=github.com", sources[1]);
        // Source 3: DuckDuckGo
        Assert.Equal("https://icons.duckduckgo.com/ip3/github.com.ico", sources[2]);
        // Source 4: Direct website root
        Assert.Equal("https://github.com/favicon.ico", sources[3]);
    }

    [Fact]
    public void SaveIconToCache_OverwritesDeterministicPathInPlace()
    {
        string url = "https://example.com/myapp";
        string deterministicPath = WebFaviconService.GetCachedIconPath(url);

        try
        {
            byte[] initialBytes = [1, 2, 3, 4, 5, 6, 7, 8];
            string? saved1 = WebFaviconService.SaveIconToCache(url, initialBytes);
            Assert.Equal(deterministicPath, saved1);
            Assert.True(File.Exists(deterministicPath));
            Assert.Equal(initialBytes, File.ReadAllBytes(deterministicPath));

            // Overwrite in place with new bytes (e.g. user selected different cycled icon)
            byte[] newBytes = [9, 8, 7, 6, 5, 4, 3, 2, 1];
            string? saved2 = WebFaviconService.SaveIconToCache(url, newBytes);
            Assert.Equal(deterministicPath, saved2);
            Assert.Equal(newBytes, File.ReadAllBytes(deterministicPath));
        }
        finally
        {
            if (File.Exists(deterministicPath))
            {
                try { File.Delete(deterministicPath); } catch { }
            }
        }
    }

    [Fact]
    public void WebLinkDialog_SetupAndSetupEdit_InitializesButtonsProperly()
    {
        WpfTestHost.RunSta(() =>
        {
            var dialog = new WebLinkDialog();

            // 1. Add mode
            dialog.Setup(initialUrl: "https://example.com", initialTitle: "Example");
            var changeBtn = (Button)dialog.FindName("WebLinkChangeIconButton");
            var actionBtn = (Button)dialog.FindName("WebLinkActionButton");

            Assert.NotNull(changeBtn);
            Assert.NotNull(actionBtn);
            Assert.Equal("Change Icon", changeBtn.Content);
            Assert.Equal("Add Shortcut", actionBtn.Content);
            Assert.True(changeBtn.IsEnabled);

            // 2. Edit mode
            dialog.SetupEdit(initialUrl: "https://example.com", initialTitle: "Example Edit");
            Assert.Equal("Change Icon", changeBtn.Content);
            Assert.Equal("Save Changes", actionBtn.Content);
            Assert.True(changeBtn.IsEnabled);
        });
    }

    [Fact]
    public void GetCandidateSourceUrls_WhenHtmlHeadMatchesRootFavicon_DeduplicatesCandidates()
    {
        string url = "https://example.com/";
        string htmlHeadIcon = "https://example.com/favicon.ico";

        var sources = WebFaviconService.GetCandidateSourceUrls(url, htmlHeadIcon);

        // Should not contain duplicate "https://example.com/favicon.ico"
        int faviconCount = sources.Count(s => string.Equals(s, htmlHeadIcon, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, faviconCount);
    }

    [Fact]
    public void IconPathToBitmapConverter_InvalidateCache_NormalizesPathSeparators()
    {
        WpfTestHost.RunSta(() =>
        {
            string tempImage = Path.Combine(Path.GetTempPath(), $"metrohub_test_{Guid.NewGuid():N}.png");
            try
            {
                // Create a tiny 16x16 PNG
                int stride = 16 * 4;
                byte[] pixels = new byte[16 * stride];
                var bmp = BitmapSource.Create(16, 16, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
                using (var fs = File.Create(tempImage))
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    enc.Save(fs);
                }

                var converter = new IconPathToBitmapConverter();
                var first = converter.Convert(tempImage, typeof(System.Windows.Media.ImageSource), 96, System.Globalization.CultureInfo.InvariantCulture);
                Assert.NotNull(first);

                // Invalidate using forward slash version
                string forwardSlashPath = tempImage.Replace('\\', '/');
                IconPathToBitmapConverter.InvalidateCache(forwardSlashPath);

                var second = converter.Convert(tempImage, typeof(System.Windows.Media.ImageSource), 96, System.Globalization.CultureInfo.InvariantCulture);
                Assert.NotNull(second);
                Assert.NotSame(first, second);
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
