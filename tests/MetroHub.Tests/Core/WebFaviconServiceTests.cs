using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using MetroHub.Core.Services;
using Xunit;

namespace MetroHub.Tests;

public class WebFaviconServiceTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void ParseHtmlHead_WithInlineSvgDataUri_ExtractsDataUriCandidateAndScoresHigh()
    {
        string html = """
            <!DOCTYPE html>
            <html>
            <head>
                <title>Kydo Flow</title>
                <link rel="icon" href="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 32 32'%3E%3Crect width='32' height='32' fill='%234f8a1e'/%3E%3C/svg%3E">
            </head>
            <body></body>
            </html>
            """;

        var info = WebFaviconService.ParseHtmlHead(html, new Uri("https://flow.kydo.sh/"));

        Assert.NotNull(info);
        Assert.Equal("Kydo Flow", info.Title);
        Assert.NotNull(info.IconUrl);
        Assert.StartsWith("data:image/svg+xml,", info.IconUrl, StringComparison.OrdinalIgnoreCase);
        Assert.True(info.IconScore >= 1100, $"Score was {info.IconScore}, expected >= 1100");
    }

    [Fact]
    public void ParseHtmlHead_WithBaseHref_ResolvesRelativeIconsAgainstBaseTag()
    {
        string html = """
            <html>
            <head>
                <base href="https://cdn.example.com/assets/">
                <link rel="icon" href="icons/app-logo.svg">
            </head>
            </html>
            """;

        var info = WebFaviconService.ParseHtmlHead(html, new Uri("https://example.com/page"));

        Assert.NotNull(info);
        Assert.Equal("https://cdn.example.com/assets/icons/app-logo.svg", info.IconUrl);
        Assert.True(info.IconScore >= 1100);
    }

    [Fact]
    public void ParseHtmlHead_WithPwaManifest_ExtractsManifestUrl()
    {
        string html = """
            <html>
            <head>
                <link rel="manifest" href="/site.webmanifest">
                <link rel="icon" href="/favicon.ico" sizes="16x16">
            </head>
            </html>
            """;

        var info = WebFaviconService.ParseHtmlHead(html, new Uri("https://example.com/"));

        Assert.NotNull(info);
        Assert.Equal("https://example.com/site.webmanifest", info.ManifestUrl);
        Assert.Equal("https://example.com/favicon.ico", info.IconUrl);
        Assert.True(info.IconScore < 800); // Low-res icon score
    }

    [Fact]
    public void ParseHtmlHead_QuoteAware_HandlesDoubleSingleAndUnquotedAttributes()
    {
        string html = """
            <html>
            <head>
                <link rel='apple-touch-icon' href='https://example.com/apple.png' sizes='180x180'>
            </head>
            </html>
            """;

        var info = WebFaviconService.ParseHtmlHead(html, new Uri("https://example.com/"));

        Assert.NotNull(info);
        Assert.Equal("https://example.com/apple.png", info.IconUrl);
        Assert.Equal(1180, info.IconScore);
    }

    [Fact]
    public async Task WebIconRasterizer_RasterizeSvgToPngFileAsync_CreatesValid256x256PngHeadless()
    {
        string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
                <circle cx="50" cy="50" r="45" fill="#2563eb" />
                <path d="M30 50 L45 65 L70 35" stroke="#ffffff" stroke-width="8" fill="none" stroke-linecap="round"/>
            </svg>
            """;

        string tempPath = Path.Combine(Path.GetTempPath(), $"metrohub_test_{Guid.NewGuid():N}.png");
        try
        {
            bool success = await WebIconRasterizer.RasterizeSvgToPngFileAsync(svg, tempPath, 256);
            Assert.True(success);
            Assert.True(File.Exists(tempPath));

            byte[] bytes = await File.ReadAllBytesAsync(tempPath);
            Assert.True(bytes.Length > 100);

            // Verify PNG magic header
            for (int i = 0; i < PngSignature.Length; i++)
            {
                Assert.Equal(PngSignature[i], bytes[i]);
            }

            // Verify PNG dimensions using BitmapDecoder
            using var ms = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            Assert.Equal(256, frame.PixelWidth);
            Assert.Equal(256, frame.PixelHeight);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    [Fact]
    public async Task WebIconRasterizer_RasterizeSvgToPngBytesAsync_ReturnsValidPngBytes()
    {
        string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 48 48">
                <rect width="48" height="48" rx="8" fill="#10b981"/>
            </svg>
            """;

        byte[]? pngBytes = await WebIconRasterizer.RasterizeSvgToPngBytesAsync(svg, 256);

        Assert.NotNull(pngBytes);
        Assert.True(pngBytes.Length > 100);
        for (int i = 0; i < PngSignature.Length; i++)
        {
            Assert.Equal(PngSignature[i], pngBytes[i]);
        }
    }

    [Fact]
    public async Task WebIconRasterizer_DisguisedIcoSniffing_DetectsAndRasterizesInPlace()
    {
        string tempIcoPath = Path.Combine(Path.GetTempPath(), $"disguised_{Guid.NewGuid():N}.ico");
        try
        {
            // Write raw SVG XML into a file with a .ico extension
            string fakeIcoSvg = """
                <?xml version="1.0" encoding="utf-8"?>
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32">
                    <circle cx="16" cy="16" r="14" fill="#f59e0b"/>
                </svg>
                """;
            await File.WriteAllTextAsync(tempIcoPath, fakeIcoSvg, Encoding.UTF8);

            bool converted = await WebIconRasterizer.TryRasterizeFileIfSvgAsync(tempIcoPath, 256);
            Assert.True(converted);

            byte[] resultBytes = await File.ReadAllBytesAsync(tempIcoPath);
            Assert.True(resultBytes.Length > 100);

            // Must now be a genuine PNG file
            for (int i = 0; i < PngSignature.Length; i++)
            {
                Assert.Equal(PngSignature[i], resultBytes[i]);
            }
        }
        finally
        {
            if (File.Exists(tempIcoPath))
            {
                try { File.Delete(tempIcoPath); } catch { }
            }
        }
    }

    [Fact]
    public async Task WebIconRasterizer_DisguisedIcoSniffing_LeavesGenuineBinaryIcoUntouched()
    {
        string tempIcoPath = Path.Combine(Path.GetTempPath(), $"genuine_{Guid.NewGuid():N}.ico");
        try
        {
            // Standard ICO header: 0x00, 0x00, 0x01, 0x00, 0x01, 0x00 ...
            byte[] genuineIcoBytes = new byte[300];
            genuineIcoBytes[2] = 0x01; // image type 1 = icon
            genuineIcoBytes[4] = 0x01; // 1 image
            for (int i = 6; i < genuineIcoBytes.Length; i++) genuineIcoBytes[i] = 0xAA;

            await File.WriteAllBytesAsync(tempIcoPath, genuineIcoBytes);

            bool converted = await WebIconRasterizer.TryRasterizeFileIfSvgAsync(tempIcoPath, 256);
            Assert.False(converted);

            byte[] afterBytes = await File.ReadAllBytesAsync(tempIcoPath);
            Assert.Equal(genuineIcoBytes.Length, afterBytes.Length);
            Assert.Equal(0xAA, afterBytes[10]);
        }
        finally
        {
            if (File.Exists(tempIcoPath))
            {
                try { File.Delete(tempIcoPath); } catch { }
            }
        }
    }

    [Fact]
    public async Task WebIconRasterizer_TryProcessDataUriToFileAsync_WithUrlEncodedSvg_RasterizesSuccessfully()
    {
        // Exact real-world payload format from flow.kydo.sh
        string dataUri = "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 32 32'%3E%3Crect x='2' y='4' width='5' height='24' rx='1' fill='%234f8a1e'/%3E%3Crect x='25' y='4' width='5' height='24' rx='1' fill='%232f68c9'/%3E%3Cpath d='M7 9C16 9 16 20 25 20' stroke='%234f8a1e' stroke-width='5' fill='none' opacity='.6'/%3E%3C/svg%3E";

        string tempPng = Path.Combine(Path.GetTempPath(), $"flow_kydo_{Guid.NewGuid():N}.png");
        try
        {
            bool success = await WebIconRasterizer.TryProcessDataUriToFileAsync(dataUri, tempPng, 256);
            Assert.True(success);
            Assert.True(File.Exists(tempPng));

            byte[] bytes = await File.ReadAllBytesAsync(tempPng);
            Assert.True(bytes.Length > 100);
            for (int i = 0; i < PngSignature.Length; i++)
            {
                Assert.Equal(PngSignature[i], bytes[i]);
            }
        }
        finally
        {
            if (File.Exists(tempPng))
            {
                try { File.Delete(tempPng); } catch { }
            }
        }
    }

    [Fact]
    public async Task WebIconRasterizer_TryProcessDataUriToFileAsync_WithBase64Svg_RasterizesSuccessfully()
    {
        string rawSvg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 20 20'><circle cx='10' cy='10' r='8' fill='purple'/></svg>";
        string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(rawSvg));
        string dataUri = $"data:image/svg+xml;base64,{base64}";

        string tempPng = Path.Combine(Path.GetTempPath(), $"b64_svg_{Guid.NewGuid():N}.png");
        try
        {
            bool success = await WebIconRasterizer.TryProcessDataUriToFileAsync(dataUri, tempPng, 256);
            Assert.True(success);
            Assert.True(File.Exists(tempPng));

            byte[] bytes = await File.ReadAllBytesAsync(tempPng);
            for (int i = 0; i < PngSignature.Length; i++)
            {
                Assert.Equal(PngSignature[i], bytes[i]);
            }
        }
        finally
        {
            if (File.Exists(tempPng))
            {
                try { File.Delete(tempPng); } catch { }
            }
        }
    }

    [Fact]
    public async Task DownloadIconBytesAsync_WithDataUri_ReturnsPngBytesDirectly()
    {
        string dataUri = "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 32 32'%3E%3Crect width='32' height='32' fill='red'/%3E%3C/svg%3E";

        byte[]? bytes = await WebFaviconService.DownloadIconBytesAsync(dataUri, 200);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length >= 200);
        for (int i = 0; i < PngSignature.Length; i++)
        {
            Assert.Equal(PngSignature[i], bytes[i]);
        }
    }

    [Theory]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'>", true)]
    [InlineData("  <SVG viewBox='0 0 10 10'>", true)]
    [InlineData("<?xml version='1.0'?><svg>", true)]
    [InlineData("<!DOCTYPE svg PUBLIC '-//W3C//DTD SVG 1.1//EN'><svg>", true)]
    [InlineData("<html><body>Hello</body></html>", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void WebIconRasterizer_IsSvgHeader_DetectsSvgSignaturesCorrectly(string? input, bool expected)
    {
        bool result = WebIconRasterizer.IsSvgHeader(input!);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ParseHtmlHead_WithSvgAndAppleTouchIcon_PrefersSvgVectorOverAppleTouchIcon()
    {
        string html = """
            <html>
            <head>
                <link rel="apple-touch-icon" href="/apple-touch-icon.png">
                <link rel="icon" type="image/svg+xml" href="/favicon.svg">
            </head>
            </html>
            """;

        var info = WebFaviconService.ParseHtmlHead(html, new Uri("https://example.com/"));

        Assert.NotNull(info);
        Assert.Equal("https://example.com/favicon.svg", info.IconUrl);
        Assert.True(info.IconScore > 1180, $"Expected SVG score > 1180 to beat apple-touch-icon, was {info.IconScore}");
    }

    [Fact]
    public void ParseHtmlHead_WithMultipleBaseTags_PicksFirstBaseWithHref()
    {
        string html = """
            <html>
            <head>
                <base target="_top">
                <base href="https://cdn.example.com/subfolder/">
                <link rel="icon" href="icon.svg">
            </head>
            </html>
            """;

        var info = WebFaviconService.ParseHtmlHead(html, new Uri("https://example.com/page"));

        Assert.NotNull(info);
        Assert.Equal("https://cdn.example.com/subfolder/icon.svg", info.IconUrl);
    }

    [Fact]
    public void ParseHtmlHead_WithHtmlEntitiesInHref_DecodesEntitiesCorrectly()
    {
        string html = """
            <html>
            <head>
                <link rel="icon" type="image/svg+xml" href="/icon.svg?v=1&amp;theme=dark">
            </head>
            </html>
            """;

        var info = WebFaviconService.ParseHtmlHead(html, new Uri("https://example.com/"));

        Assert.NotNull(info);
        Assert.Equal("https://example.com/icon.svg?v=1&theme=dark", info.IconUrl);
    }

    [Fact]
    public async Task WebIconRasterizer_DataUriWithUnescapedPercent_DoesNotThrowAndRasterizesSuccessfully()
    {
        string dataUri = "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'%3E%3Crect width='100%' height='100%' fill='%234f8a1e'/%3E%3C/svg%3E";

        string tempPng = Path.Combine(Path.GetTempPath(), $"percent_svg_{Guid.NewGuid():N}.png");
        try
        {
            bool success = await WebIconRasterizer.TryProcessDataUriToFileAsync(dataUri, tempPng, 256);
            Assert.True(success);
            Assert.True(File.Exists(tempPng));
        }
        finally
        {
            if (File.Exists(tempPng))
            {
                try { File.Delete(tempPng); } catch { }
            }
        }
    }

    [Fact]
    public void WebIconRasterizer_IsSvgBytes_DetectsSvgWithLongPreambleExceeding1KB()
    {
        string preamble = "<!-- " + new string('A', 2000) + " -->\n<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10'><rect width='10' height='10'/></svg>";
        byte[] bytes = Encoding.UTF8.GetBytes(preamble);

        bool isSvg = WebIconRasterizer.IsSvgBytes(bytes);
        Assert.True(isSvg, "Should detect SVG even when license comments exceed 1KB");
    }

    [Fact]
    public async Task WebIconRasterizer_RasterizeSvgWithoutViewBox_UsesSvgDimensionsSuccessfully()
    {
        string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="64" height="64">
                <circle cx="32" cy="32" r="30" fill="#3b82f6"/>
            </svg>
            """;

        byte[]? pngBytes = await WebIconRasterizer.RasterizeSvgToPngBytesAsync(svg, 256);
        Assert.NotNull(pngBytes);
        Assert.True(pngBytes.Length > 100);
    }

    [Fact]
    public async Task WebIconRasterizer_DisguisedIcoSniffing_WithSmallSvgUnder200Bytes_DetectsAndRasterizes()
    {
        string tempIcoPath = Path.Combine(Path.GetTempPath(), $"tiny_svg_{Guid.NewGuid():N}.ico");
        try
        {
            // Tiny 96-byte SVG
            string tinySvg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'><circle cx='8' cy='8' r='8' fill='#ff0000'/></svg>";
            byte[] rawBytes = Encoding.UTF8.GetBytes(tinySvg);
            Assert.True(rawBytes.Length < 200, $"Expected byte length < 200, was {rawBytes.Length}");

            await File.WriteAllBytesAsync(tempIcoPath, rawBytes);

            bool converted = await WebIconRasterizer.TryRasterizeFileIfSvgAsync(tempIcoPath, 256);
            Assert.True(converted);

            byte[] resultBytes = await File.ReadAllBytesAsync(tempIcoPath);
            Assert.True(resultBytes.Length > 100);
            for (int i = 0; i < PngSignature.Length; i++)
            {
                Assert.Equal(PngSignature[i], resultBytes[i]);
            }
        }
        finally
        {
            if (File.Exists(tempIcoPath))
            {
                try { File.Delete(tempIcoPath); } catch { }
            }
        }
    }

    [Fact]
    public void ParseHtmlHead_WithSvgSizesAny_ScoresHigherThanStandardSvg()
    {
        string html = """
            <html>
            <head>
                <link rel="icon" type="image/svg+xml" sizes="any" href="/vector.svg">
            </head>
            </html>
            """;

        var info = WebFaviconService.ParseHtmlHead(html, new Uri("https://example.com/"));

        Assert.NotNull(info);
        Assert.Equal("https://example.com/vector.svg", info.IconUrl);
        Assert.Equal(2500, info.IconScore);
    }

    [Fact]
    public async Task WebIconRasterizer_TryProcessDataUriToFileAsync_WithUrlEncodedBase64_DecodesSuccessfully()
    {
        string rawSvg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10'><rect width='10' height='10' fill='green'/></svg>";
        string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(rawSvg));
        // URL-encode padding '=' as '%3D'
        string urlEncodedBase64 = base64.Replace("=", "%3D");
        string dataUri = $"data:image/svg+xml;base64,{urlEncodedBase64}";

        string tempPng = Path.Combine(Path.GetTempPath(), $"b64_urlencoded_{Guid.NewGuid():N}.png");
        try
        {
            bool success = await WebIconRasterizer.TryProcessDataUriToFileAsync(dataUri, tempPng, 256);
            Assert.True(success);
            Assert.True(File.Exists(tempPng));

            byte[] bytes = await File.ReadAllBytesAsync(tempPng);
            for (int i = 0; i < PngSignature.Length; i++)
            {
                Assert.Equal(PngSignature[i], bytes[i]);
            }
        }
        finally
        {
            if (File.Exists(tempPng))
            {
                try { File.Delete(tempPng); } catch { }
            }
        }
    }

    [Fact]
    public async Task WebIconRasterizer_TryRasterizeFileIfSvgAsync_WithMalformedSvg_DeletesFileAndReturnsFalse()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"malformed_svg_{Guid.NewGuid():N}.svg");
        try
        {
            await File.WriteAllTextAsync(tempPath, "<svg xmlns='http://www.w3.org/2000/svg'><bad-unclosed-xml");
            bool result = await WebIconRasterizer.TryRasterizeFileIfSvgAsync(tempPath, 256);

            Assert.False(result);
            Assert.False(File.Exists(tempPath), "Malformed SVG file must be deleted so unrenderable XML is not left on disk.");
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html><head><title>SPA 404</title></head><body>Not Found</body></html>")]
    [InlineData("<html><head><title>SPA</title></head></html>")]
    [InlineData("<head><meta charset='utf-8'></head>")]
    public async Task WebFaviconService_ValidateAndNormalizeDownloadedIconAsync_WithHtmlResponses_DeletesFileAndReturnsFalse(string htmlContent)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"html_response_{Guid.NewGuid():N}.ico");
        try
        {
            await File.WriteAllTextAsync(tempPath, htmlContent);
            bool result = await WebFaviconService.ValidateAndNormalizeDownloadedIconAsync(tempPath);

            Assert.False(result);
            Assert.False(File.Exists(tempPath), "HTML response disguised as icon must be deleted.");
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    [Fact]
    public async Task WebFaviconService_ValidateAndNormalizeDownloadedIconAsync_WithValidSvg_RasterizesInPlace()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"valid_svg_{Guid.NewGuid():N}.svg");
        try
        {
            string validSvg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'><circle cx='8' cy='8' r='7' fill='blue'/></svg>";
            await File.WriteAllTextAsync(tempPath, validSvg);

            bool result = await WebFaviconService.ValidateAndNormalizeDownloadedIconAsync(tempPath);

            Assert.True(result);
            Assert.True(File.Exists(tempPath));

            byte[] bytes = await File.ReadAllBytesAsync(tempPath);
            Assert.True(bytes.Length > 100);
            for (int i = 0; i < PngSignature.Length; i++)
            {
                Assert.Equal(PngSignature[i], bytes[i]);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    [Fact]
    public void WebFaviconService_SaveIconToCache_WithMalformedSvg_ReturnsNullAndDeletesCacheFile()
    {
        string url = $"https://example.com/bad_{Guid.NewGuid():N}";
        byte[] malformedSvg = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><unclosed");

        string? result = WebFaviconService.SaveIconToCache(url, malformedSvg);

        Assert.Null(result);
        string expectedPath = WebFaviconService.GetCachedIconPath(url);
        Assert.False(File.Exists(expectedPath), "Unrenderable SVG bytes must not be committed to disk cache.");
    }

    [Fact]
    public void ParseHtmlHead_WithMultiSizeDimensions_SelectsMaximumWidth()
    {
        string html = """
            <html>
            <head>
                <link rel="icon" sizes="16x16 32x32 180x180" href="/icons/app-icon.png">
            </head>
            </html>
            """;

        var info = WebFaviconService.ParseHtmlHead(html, new Uri("https://example.com/"));

        Assert.NotNull(info);
        Assert.Equal("https://example.com/icons/app-icon.png", info.IconUrl);
        // maxDim is 180 >= 128, so score is 1000 + 180 = 1180 (previously only parsed first match 16x16 -> score 26)
        Assert.Equal(1180, info.IconScore);
    }

    [Fact]
    public void ParseHtmlHead_WithCurrentColorSvg_PrefersCrispAppleTouchIcon()
    {
        string html = """
            <html>
            <head>
                <link rel="icon" type="image/svg+xml" href="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Cpath fill='currentColor' d='M0 0h16v16H0z'/%3E%3C/svg%3E">
                <link rel="apple-touch-icon" sizes="180x180" href="/apple-touch-icon.png">
            </head>
            </html>
            """;

        var info = WebFaviconService.ParseHtmlHead(html, new Uri("https://example.com/"));

        Assert.NotNull(info);
        Assert.Equal("https://example.com/apple-touch-icon.png", info.IconUrl);
        Assert.Equal(1180, info.IconScore);
    }

    [Fact]
    public void ParseHtmlHead_WithPrefersColorSchemeSvg_PrefersCrispAppleTouchIcon()
    {
        string html = """
            <html>
            <head>
                <link rel="icon" type="image/svg+xml" href="/logo-dark.svg" media="(prefers-color-scheme: dark)">
                <link rel="apple-touch-icon" sizes="180x180" href="/apple-touch-icon.png">
            </head>
            </html>
            """;

        var info = WebFaviconService.ParseHtmlHead(html, new Uri("https://example.com/"));

        Assert.NotNull(info);
        Assert.Equal("https://example.com/apple-touch-icon.png", info.IconUrl);
        Assert.Equal(1180, info.IconScore);
    }

    [Fact]
    public void ParseHtmlHead_WithCurrentColorSvg_WhenNoAppleTouchIcon_RetainsScore900()
    {
        string html = """
            <html>
            <head>
                <link rel="icon" type="image/svg+xml" href="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' fill='currentColor'%3E%3C/svg%3E">
            </head>
            </html>
            """;

        var info = WebFaviconService.ParseHtmlHead(html, new Uri("https://example.com/"));

        Assert.NotNull(info);
        Assert.Equal(900, info.IconScore);
    }

    [Fact]
    public void WebFaviconService_SaveIconToCache_WithHtmlResponse_ReturnsNullAndDeletesCacheFile()
    {
        string url = $"https://example.com/html_{Guid.NewGuid():N}";
        byte[] htmlBytes = Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>Error</body></html>");

        string? result = WebFaviconService.SaveIconToCache(url, htmlBytes);

        Assert.Null(result);
        string expectedPath = WebFaviconService.GetCachedIconPath(url);
        Assert.False(File.Exists(expectedPath), "HTML bytes must not be committed to disk cache.");
    }

    [Fact]
    public void WebFaviconService_SaveIconToCache_WithNonSvgXml_ReturnsNullAndDeletesCacheFile()
    {
        string url = $"https://example.com/xml_{Guid.NewGuid():N}";
        byte[] xmlBytes = Encoding.UTF8.GetBytes("<?xml version='1.0'?><Error><Code>AccessDenied</Code></Error>");

        string? result = WebFaviconService.SaveIconToCache(url, xmlBytes);

        Assert.Null(result);
        string expectedPath = WebFaviconService.GetCachedIconPath(url);
        Assert.False(File.Exists(expectedPath), "Non-SVG XML bytes must not be committed to disk cache.");
    }

    [Fact]
    public async Task WebFaviconService_ValidateAndNormalizeDownloadedIconAsync_WithNonSvgXml_DeletesFileAndReturnsFalse()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"xml_error_{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(tempPath, "<?xml version='1.0'?><Error><Code>AccessDenied</Code></Error>");
            bool result = await WebFaviconService.ValidateAndNormalizeDownloadedIconAsync(tempPath);

            Assert.False(result);
            Assert.False(File.Exists(tempPath), "Non-SVG XML response must be deleted.");
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    [Fact]
    public void WebIconRasterizer_IsSvgBytes_WithHtmlContainingInlineSvg_ReturnsFalse()
    {
        byte[] htmlBytes = Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body><svg viewBox='0 0 16 16'><circle cx='8' cy='8' r='7'/></svg></body></html>");
        bool isSvg = WebIconRasterizer.IsSvgBytes(htmlBytes);

        Assert.False(isSvg, "HTML document with inline SVG must not be classified as standalone SVG.");
    }

    [Fact]
    public async Task WebFaviconService_ValidateAndNormalizeDownloadedIconAsync_WithHtmlContainingInlineSvg_DeletesFileAndReturnsFalse()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"html_with_svg_{Guid.NewGuid():N}.ico");
        try
        {
            string content = "<!DOCTYPE html><html><body><svg viewBox='0 0 16 16'><rect width='16' height='16'/></svg>404 Not Found</body></html>";
            await File.WriteAllTextAsync(tempPath, content);
            bool result = await WebFaviconService.ValidateAndNormalizeDownloadedIconAsync(tempPath);

            Assert.False(result);
            Assert.False(File.Exists(tempPath), "HTML file containing inline SVG must be deleted.");
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }
}
