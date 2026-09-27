using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Radio;
using Xunit;

namespace MetroHub.Tests;

/// <summary>
/// Offline-safe tests for <see cref="YoutubeChunkedDownloader"/> using a fake range-aware HTTP
/// handler; no YouTube network calls are made.
/// </summary>
public sealed class YoutubeChunkedDownloaderTests
{
    static YoutubeChunkedDownloaderTests() => JukeboxLog.Enabled = false;

    private sealed class FakeRangeHandler : HttpMessageHandler
    {
        private readonly long _total;

        public FakeRangeHandler(long total) => _total = total;

        public List<(long Start, long End)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RangeHeaderValue? range = request.Headers.Range;
            if (range is null || range.Ranges.Count == 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            }

            RangeItemHeaderValue first = range.Ranges.First();
            long start = first.From ?? 0;
            long end = first.To ?? (_total - 1);
            if (start >= _total)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
            }

            end = Math.Min(end, _total - 1);
            Requests.Add((start, end));

            int count = checked((int)(end - start + 1));
            var content = new ByteArrayContent(new byte[count]);
            content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, _total);

            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = content,
                RequestMessage = request,
            };
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task RunAsync_Downloads_Whole_File_In_NonOverlapping_Ranges()
    {
        const long total = 4L * 1024 * 1024;
        var handler = new FakeRangeHandler(total);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var buffer = new ChunkedAudioBuffer();
        var downloader = new YoutubeChunkedDownloader(buffer, "https://example.test/audio", "test", client);

        await downloader.RunAsync(CancellationToken.None);

        Assert.True(buffer.IsCompleted);
        Assert.Null(buffer.Fault);
        Assert.Equal(total, buffer.BufferedBytes);

        Assert.NotEmpty(handler.Requests);
        long expected = 0;
        foreach ((long start, long end) in handler.Requests)
        {
            Assert.Equal(expected, start);   // ranges tile the file with no gaps
            Assert.True(end >= start);
            expected = end + 1;
        }
        Assert.Equal(total, expected);
    }

    [Fact]
    public async Task RunAsync_Completes_A_File_Smaller_Than_One_Chunk()
    {
        const long total = 256 * 1024;
        var handler = new FakeRangeHandler(total);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var buffer = new ChunkedAudioBuffer();
        var downloader = new YoutubeChunkedDownloader(buffer, "https://example.test/audio", "test", client);

        await downloader.RunAsync(CancellationToken.None);

        Assert.True(buffer.IsCompleted);
        Assert.Null(buffer.Fault);
        Assert.Equal(total, buffer.BufferedBytes);
    }
}
