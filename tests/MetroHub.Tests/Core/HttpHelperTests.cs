using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Radio;
using MetroHub.Core.Services;
using MetroHub.Core.Services.Catalog.Weather;
using Xunit;

namespace MetroHub.Tests;

public sealed record SampleWidgetData(string Name, int Version, bool IsActive);

public sealed record SampleDataDto
{
    public string Name { get; init; } = string.Empty;
    public int Value { get; init; }
}

[JsonSerializable(typeof(SampleWidgetData))]
internal partial class SampleWidgetJsonContext : JsonSerializerContext
{
}

[Collection("HttpHelperTests")]
public class HttpHelperTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }

    [Fact]
    public void Client_HasExpectedTimeoutAndUserAgent()
    {
        Assert.NotNull(HttpHelper.Client);
        Assert.Equal(TimeSpan.FromSeconds(30), HttpHelper.Client.Timeout);

        string userAgent = HttpHelper.Client.DefaultRequestHeaders.UserAgent.ToString();
        Assert.Contains("Mozilla/5.0", userAgent);
        Assert.Contains("Chrome/128.0.0.0", userAgent);
        Assert.Contains("MetroHub/2.0", userAgent);
    }

    [Fact]
    public void WeatherService_SharedHttpClient_ReferencesHttpHelperClient()
    {
        Assert.Same(HttpHelper.Client, WeatherService.SharedHttpClient);
    }

    [Fact]
    public void RadioBrowserClient_Constructors_DefaultToHttpHelperClient()
    {
        var client1 = new RadioBrowserClient();
        Assert.NotNull(client1);

        var client2 = new RadioBrowserClient(httpClient: null);
        Assert.NotNull(client2);
    }

    [Theory]
    [InlineData("", "C:\\temp\\test.png")]
    [InlineData("   ", "C:\\temp\\test.png")]
    [InlineData("https://example.com/test.png", "")]
    [InlineData("https://example.com/test.png", "   ")]
    public async Task DownloadFileAsync_InvalidInputs_ReturnsFalse(string url, string destination)
    {
        bool result = await HttpHelper.DownloadFileAsync(url, destination);
        Assert.False(result);
    }

    [Fact]
    public async Task DownloadFileAsync_SuccessfulDownload_WritesFileAtomicallyAndCleansUpTemp()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "MetroHub_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string destPath = Path.Combine(tempDir, "downloaded.bin");

        byte[] payload = new byte[1024];
        Random.Shared.NextBytes(payload);

        var handler = new MockHttpMessageHandler(req =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
            return res;
        });

        try
        {
            using (HttpHelper.UseTestClient(new HttpClient(handler)))
            {
                bool success = await HttpHelper.DownloadFileAsync("https://example.com/test.bin", destPath, minimumBytes: 500);

                Assert.True(success);
                Assert.True(File.Exists(destPath));
                Assert.Equal(payload.Length, new FileInfo(destPath).Length);

                // Ensure no dangling .tmp files remain
                string[] tmpFiles = Directory.GetFiles(tempDir, "*.tmp");
                Assert.Empty(tmpFiles);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task DownloadFileAsync_HttpError_CleansUpAndReturnsFalse()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "MetroHub_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string destPath = Path.Combine(tempDir, "should_not_exist.bin");

        var handler = new MockHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.NotFound));

        try
        {
            using (HttpHelper.UseTestClient(new HttpClient(handler)))
            {
                bool success = await HttpHelper.DownloadFileAsync("https://example.com/missing.bin", destPath);

                Assert.False(success);
                Assert.False(File.Exists(destPath));

                string[] tmpFiles = Directory.GetFiles(tempDir, "*.tmp");
                Assert.Empty(tmpFiles);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task DownloadFileAsync_BelowMinimumBytes_RejectsFileAndCleansUp()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "MetroHub_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string destPath = Path.Combine(tempDir, "too_small.bin");

        byte[] smallPayload = new byte[50];

        var handler = new MockHttpMessageHandler(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(smallPayload)
            };
        });

        try
        {
            using (HttpHelper.UseTestClient(new HttpClient(handler)))
            {
                // minimumBytes is 200, payload is only 50 bytes
                bool success = await HttpHelper.DownloadFileAsync("https://example.com/small.bin", destPath, minimumBytes: 200);

                Assert.False(success);
                Assert.False(File.Exists(destPath));

                string[] tmpFiles = Directory.GetFiles(tempDir, "*.tmp");
                Assert.Empty(tmpFiles);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task DownloadFileAsync_CancelledToken_CleansUpAndReturnsFalse()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "MetroHub_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string destPath = Path.Combine(tempDir, "cancelled.bin");

        var handler = new MockHttpMessageHandler(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[500])
            };
        });

        try
        {
            using (HttpHelper.UseTestClient(new HttpClient(handler)))
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel(); // Pre-cancelled
                bool success = await HttpHelper.DownloadFileAsync("https://example.com/test.bin", destPath, ct: cts.Token);

                Assert.False(success);
                Assert.False(File.Exists(destPath));

                string[] tmpFiles = Directory.GetFiles(tempDir, "*.tmp");
                Assert.Empty(tmpFiles);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task DownloadFileAsync_CreatesParentDirectoriesIfNeeded()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "MetroHub_Test_" + Guid.NewGuid().ToString("N"));
        string nestedPath = Path.Combine(tempDir, "subfolder1", "subfolder2", "nested.bin");

        var handler = new MockHttpMessageHandler(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[100])
            };
        });

        try
        {
            using (HttpHelper.UseTestClient(new HttpClient(handler)))
            {
                bool success = await HttpHelper.DownloadFileAsync("https://example.com/nested.bin", nestedPath);

                Assert.True(success);
                Assert.True(File.Exists(nestedPath));
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task GetJsonAsync_WithJsonTypeInfo_DeserializesSuccessfully()
    {
        string json = """{"Name":"MetroHub","Version":2,"IsActive":true}""";
        var handler = new MockHttpMessageHandler(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        using (HttpHelper.UseTestClient(new HttpClient(handler)))
        {
            var result = await HttpHelper.GetJsonAsync(
                "https://api.example.com/widget",
                SampleWidgetJsonContext.Default.SampleWidgetData);

            Assert.NotNull(result);
            Assert.Equal("MetroHub", result.Name);
            Assert.Equal(2, result.Version);
            Assert.True(result.IsActive);
        }
    }

    [Fact]
    public async Task GetJsonAsync_WithOptions_DeserializesCaseInsensitive()
    {
        string json = """{"name":"CaseTest","value":999}""";
        var handler = new MockHttpMessageHandler(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        using (HttpHelper.UseTestClient(new HttpClient(handler)))
        {
            var result = await HttpHelper.GetJsonAsync<SampleDataDto>("https://api.example.com/dto");

            Assert.NotNull(result);
            Assert.Equal("CaseTest", result.Name);
            Assert.Equal(999, result.Value);
        }
    }

    [Fact]
    public async Task GetJsonAsync_HttpError_ReturnsDefault()
    {
        var handler = new MockHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        using (HttpHelper.UseTestClient(new HttpClient(handler)))
        {
            var result = await HttpHelper.GetJsonAsync<SampleDataDto>("https://api.example.com/error");
            Assert.Null(result);
        }
    }

    [Fact]
    public async Task GetByteArrayAsync_ReturnsExpectedBytes()
    {
        byte[] expected = [10, 20, 30, 40, 50];
        var handler = new MockHttpMessageHandler(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(expected)
            };
        });

        using (HttpHelper.UseTestClient(new HttpClient(handler)))
        {
            byte[] actual = await HttpHelper.GetByteArrayAsync("https://example.com/bytes");
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public async Task GetStreamAsync_ReturnsReadableStream()
    {
        string content = "Stream Content For Unit Test";
        var handler = new MockHttpMessageHandler(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "text/plain")
            };
        });

        using (HttpHelper.UseTestClient(new HttpClient(handler)))
        {
            await using var stream = await HttpHelper.GetStreamAsync("https://example.com/stream");
            using var reader = new StreamReader(stream);
            string readContent = await reader.ReadToEndAsync();

            Assert.Equal(content, readContent);
        }
    }

    [Fact]
    public void UseTestClient_SupportsNestingAndRestoresPrevious()
    {
        var original = HttpHelper.Client;
        var client1 = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var client2 = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        using (HttpHelper.UseTestClient(client1))
        {
            Assert.Same(client1, HttpHelper.Client);
            using (HttpHelper.UseTestClient(client2))
            {
                Assert.Same(client2, HttpHelper.Client);
            }
            Assert.Same(client1, HttpHelper.Client);
        }

        Assert.Same(original, HttpHelper.Client);
    }

    [Fact]
    public async Task DownloadFileAsync_InvalidPathOrDrive_ReturnsFalseWithoutThrowing()
    {
        string invalidPath = "Z:\\MetroHub_NonExistent_Drive_12345\\sub\\file.bin";
        bool result = await HttpHelper.DownloadFileAsync("https://example.com/file.bin", invalidPath);
        Assert.False(result);
    }

    [Fact]
    public async Task DownloadFileAsync_WhenDownloadFails_PreservesExistingFile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "MetroHub_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string destPath = Path.Combine(tempDir, "existing.bin");
        await File.WriteAllTextAsync(destPath, "ORIGINAL CONTENT");

        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        try
        {
            using (HttpHelper.UseTestClient(new HttpClient(handler)))
            {
                bool success = await HttpHelper.DownloadFileAsync("https://example.com/error.bin", destPath);
                Assert.False(success);
                Assert.True(File.Exists(destPath));
                Assert.Equal("ORIGINAL CONTENT", await File.ReadAllTextAsync(destPath));

                string[] tmpFiles = Directory.GetFiles(tempDir, "*.tmp");
                Assert.Empty(tmpFiles);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task DownloadFileAsync_WhenBelowMinimumBytes_PreservesExistingFile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "MetroHub_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string destPath = Path.Combine(tempDir, "existing_wallpaper.jpg");
        await File.WriteAllTextAsync(destPath, "ORIGINAL WALLPAPER ASSET");

        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[50])
        });

        try
        {
            using (HttpHelper.UseTestClient(new HttpClient(handler)))
            {
                bool success = await HttpHelper.DownloadFileAsync("https://example.com/tiny.jpg", destPath, minimumBytes: 500);
                Assert.False(success);
                Assert.True(File.Exists(destPath));
                Assert.Equal("ORIGINAL WALLPAPER ASSET", await File.ReadAllTextAsync(destPath));

                string[] tmpFiles = Directory.GetFiles(tempDir, "*.tmp");
                Assert.Empty(tmpFiles);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    private sealed class FaultyStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            throw new IOException("Simulated disk full or broken socket mid-stream");
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            throw new IOException("Simulated disk full or broken socket mid-stream");
        }
    }

    [Fact]
    public async Task DownloadFileAsync_StreamThrowsMidway_CleansUpTempAndReturnsFalse()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "MetroHub_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string destPath = Path.Combine(tempDir, "aborted.bin");

        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new FaultyStream())
        });

        try
        {
            using (HttpHelper.UseTestClient(new HttpClient(handler)))
            {
                bool success = await HttpHelper.DownloadFileAsync("https://example.com/broken.bin", destPath);
                Assert.False(success);
                Assert.False(File.Exists(destPath));

                string[] tmpFiles = Directory.GetFiles(tempDir, "*.tmp");
                Assert.Empty(tmpFiles);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task GetJsonAsync_WhenCancellationRequested_ThrowsOperationCanceledException()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"Name":"CancelTest","Version":1,"IsActive":true}""", Encoding.UTF8, "application/json")
        });

        using (HttpHelper.UseTestClient(new HttpClient(handler)))
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel(); // Pre-cancelled
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await HttpHelper.GetJsonAsync("https://example.com/data", SampleWidgetJsonContext.Default.SampleWidgetData, cts.Token);
            });
        }
    }

    [Fact]
    public async Task GetJsonAsync_MalformedJson_ReturnsDefault()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<!DOCTYPE html><html>Broken</html>", Encoding.UTF8, "application/json")
        });

        using (HttpHelper.UseTestClient(new HttpClient(handler)))
        {
            var result = await HttpHelper.GetJsonAsync("https://example.com/malformed", SampleWidgetJsonContext.Default.SampleWidgetData);
            Assert.Null(result);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetJsonAsync_NullOrWhitespaceUrl_ThrowsArgumentException(string invalidUrl)
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await HttpHelper.GetJsonAsync(invalidUrl, SampleWidgetJsonContext.Default.SampleWidgetData);
        });

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await HttpHelper.GetJsonAsync<SampleDataDto>(invalidUrl);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetByteArrayAndStreamAsync_NullOrWhitespaceUrl_ThrowsArgumentException(string invalidUrl)
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await HttpHelper.GetByteArrayAsync(invalidUrl);
        });

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await HttpHelper.GetStreamAsync(invalidUrl);
        });
    }
}
