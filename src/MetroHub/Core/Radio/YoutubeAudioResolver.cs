using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace MetroHub.Core.Radio;

/// <summary>
/// YouTube search + audio URL resolution over YoutubeExplode. One shared client for app
/// lifetime (HttpClient reuse); bounded result sets; Opus-preferred with AAC fallback.
/// All failures surface as empty results / null — the widget shows status text instead.
/// </summary>
public sealed class YoutubeAudioResolver : IYoutubeAudioResolver
{
    public static YoutubeAudioResolver Instance { get; } = new();

    // Single client for app lifetime: reuses the underlying HttpClient (no socket churn).
    private static readonly YoutubeClient _client = new();

    /// <inheritdoc/>
    public async Task<IReadOnlyList<YoutubeSearchHit>> SearchAsync(string query, int topN = 8, CancellationToken ct = default)
    {
        var hits = new List<YoutubeSearchHit>(Math.Max(1, topN));
        if (string.IsNullOrWhiteSpace(query))
        {
            return hits;
        }

        try
        {
            await foreach (var v in _client.Search.GetVideosAsync(query.Trim()).WithCancellation(ct).ConfigureAwait(false))
            {
                hits.Add(new YoutubeSearchHit(
                    v.Id.Value,
                    v.Title,
                    v.Author.ChannelTitle,
                    v.Thumbnails.OrderByDescending(t => t.Resolution.Width * t.Resolution.Height).FirstOrDefault()?.Url,
                    v.Duration));

                if (hits.Count >= topN)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded keystroke burst or shutdown; caller treats as empty.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YoutubeAudioResolver] Search failed for '{query}': {ex.Message}");
        }

        return hits;
    }

    /// <inheritdoc/>
    public Task<YoutubeAudioStream?> ResolveAudioUrlAsync(string videoId, CancellationToken ct = default)
        => ResolveInternalAsync(videoId, allowOpus: true, ct);

    /// <inheritdoc/>
    public Task<YoutubeAudioStream?> ResolveAacFallbackAsync(string videoId, CancellationToken ct = default)
        => ResolveInternalAsync(videoId, allowOpus: false, ct);

    private async Task<YoutubeAudioStream?> ResolveInternalAsync(string videoId, bool allowOpus, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        try
        {
            var manifest = await _client.Videos.Streams.GetManifestAsync(videoId.Trim(), ct).ConfigureAwait(false);
            var audioOnly = manifest.GetAudioOnlyStreams().ToList();
            if (audioOnly.Count == 0)
            {
                return null;
            }

            // Prefer Opus (highest quality; decoded by bassopus), fall back to AAC/MP4
            // (decoded by bass_aac) so playback works even if the Opus plugin is missing.
            YoutubeExplode.Videos.Streams.AudioOnlyStreamInfo? best = null;
            if (allowOpus)
            {
                best = audioOnly
                    .Where(s => s.AudioCodec.StartsWith("opus", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(s => s.Bitrate)
                    .FirstOrDefault();
            }
            best ??= audioOnly
                .Where(s => s.Container == Container.Mp4)
                .OrderByDescending(s => s.Bitrate)
                .FirstOrDefault();
            best ??= audioOnly
                .OrderByDescending(s => s.Bitrate)
                .First();

            if (string.IsNullOrWhiteSpace(best.Url))
            {
                return null;
            }

            return new YoutubeAudioStream(
                best.Url,
                best.AudioCodec,
                (int)(best.Bitrate.BitsPerSecond / 1000));
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            // Cipher breakage, deleted/blocked video, offline: report null, widget shows status.
            Debug.WriteLine($"[YoutubeAudioResolver] Resolve failed for '{videoId}': {ex.Message}");
            return null;
        }
    }
}
