using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YoutubeExplode;
using YoutubeExplode.Playlists;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;

namespace MetroHub.Core.Radio;

/// <summary>
/// YouTube search, playlist retrieval, and unthrottled audio stream resolution over YoutubeExplode.
/// One shared client for app lifetime (HttpClient reuse); bounded result sets; Opus-preferred with AAC fallback.
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

        string trimmed = query.Trim();

        // 1. If user pasted an actual playlist URL or standard playlist ID (PL..., RD..., etc.), load playlist videos
        if (IsLikelyPlaylist(trimmed, out string playlistId))
        {
            JukeboxLog.Info($"[YoutubeAudioResolver] Recognized playlist ID '{playlistId}'.");
            return await GetPlaylistVideosAsync(playlistId, Math.Max(topN, 100), ct).ConfigureAwait(false);
        }

        // 2. If user pasted a direct video URL, fetch that single video directly
        if (IsLikelyVideoUrl(trimmed, out string videoId))
        {
            try
            {
                JukeboxLog.Info($"[YoutubeAudioResolver] Recognized direct video ID '{videoId}'.");
                var v = await _client.Videos.GetAsync(videoId, ct).ConfigureAwait(false);
                return new[]
                {
                    new YoutubeSearchHit(
                        v.Id.Value,
                        v.Title,
                        v.Author.ChannelTitle,
                        v.Thumbnails.OrderByDescending(t => t.Resolution.Width * t.Resolution.Height).FirstOrDefault()?.Url,
                        v.Duration)
                };
            }
            catch (OperationCanceledException)
            {
                return hits;
            }
            catch (Exception ex)
            {
                JukeboxLog.Error($"[YoutubeAudioResolver] Video lookup failed for '{videoId}': {ex.Message}");
                return hits;
            }
        }

        // 3. Otherwise standard search query
        try
        {
            await foreach (var v in _client.Search.GetVideosAsync(trimmed).WithCancellation(ct).ConfigureAwait(false))
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
            JukeboxLog.Error($"[YoutubeAudioResolver] Search failed for '{query}': {ex.Message}");
        }

        return hits;
    }

    private static bool IsLikelyPlaylist(string input, out string playlistId)
    {
        playlistId = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;

        string trimmed = input.Trim();

        // 1. Full URL containing playlist or list= parameter
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            if (trimmed.Contains("list=", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("/playlist", StringComparison.OrdinalIgnoreCase))
            {
                if (PlaylistId.TryParse(trimmed) is PlaylistId parsed)
                {
                    playlistId = parsed.Value;
                    return true;
                }
            }
            return false;
        }

        // 2. Explicit YouTube playlist ID format: starts with PL, RD, UU, FL, OLAK5uy_, etc.
        // YouTube playlist IDs are at least 13 characters, typically 34 characters (PL + 32 chars).
        if (!trimmed.Contains(' ') && trimmed.Length >= 13)
        {
            if (trimmed.StartsWith("PL", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("RD", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("UU", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("FL", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("OLAK5uy_", StringComparison.OrdinalIgnoreCase))
            {
                if (PlaylistId.TryParse(trimmed) is PlaylistId parsed)
                {
                    playlistId = parsed.Value;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsLikelyVideoUrl(string input, out string videoId)
    {
        videoId = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;

        string trimmed = input.Trim();

        // Only treat as direct video link if it's an explicit URL
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            if (VideoId.TryParse(trimmed) is VideoId parsed)
            {
                videoId = parsed.Value;
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<YoutubeSearchHit>> GetPlaylistVideosAsync(string playlistUrlOrId, int maxItems = 1000, CancellationToken ct = default)
    {
        var hits = new List<YoutubeSearchHit>(Math.Min(maxItems, 100));
        if (string.IsNullOrWhiteSpace(playlistUrlOrId))
        {
            return hits;
        }

        try
        {
            var playlistId = PlaylistId.Parse(playlistUrlOrId.Trim());
            await foreach (var v in _client.Playlists.GetVideosAsync(playlistId).WithCancellation(ct).ConfigureAwait(false))
            {
                hits.Add(new YoutubeSearchHit(
                    v.Id.Value,
                    v.Title,
                    v.Author.ChannelTitle,
                    v.Thumbnails.OrderByDescending(t => t.Resolution.Width * t.Resolution.Height).FirstOrDefault()?.Url,
                    v.Duration));

                if (hits.Count >= maxItems)
                {
                    break;
                }
            }
            JukeboxLog.Info($"[YoutubeAudioResolver] Loaded {hits.Count} tracks from playlist '{playlistId}'.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            JukeboxLog.Error($"[YoutubeAudioResolver] Playlist fetch failed for '{playlistUrlOrId}': {ex.Message}");
        }

        return hits;
    }

    /// <inheritdoc/>
    public async Task<JukeboxStreamSet?> ResolveStreamsAsync(string videoId, CancellationToken ct = default)
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

            var opus = audioOnly
                .Where(s => s.AudioCodec.StartsWith("opus", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.Bitrate)
                .ToList();
            var aac = audioOnly
                .Where(s => s.Container == Container.Mp4)
                .OrderByDescending(s => s.Bitrate)
                .ToList();

            string? lowUrl = opus.Count > 0
                ? opus[^1].Url // Cheapest Opus first: ~48 kbps fits throttled pipes.
                : aac.Count > 0 ? aac[^1].Url : null;

            return new JukeboxStreamSet(
                lowUrl,
                opus.Count > 0 ? opus[0].Url : null,
                aac.Count > 0 ? aac[0].Url : null);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            JukeboxLog.Error($"[YoutubeAudioResolver] Resolve failed for '{videoId}': {ex.Message}");
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<Stream?> OpenAudioStreamAsync(string videoId, CancellationToken ct = default)
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
                JukeboxLog.Warn($"[YoutubeAudioResolver] No audio streams found in manifest for '{videoId}'.");
                return null;
            }

            // Prefer Opus (decoded natively by bassopus), fallback to AAC/MP4
            var candidates = audioOnly
                .OrderByDescending(s => s.AudioCodec.StartsWith("opus", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenByDescending(s => s.Bitrate)
                .ToList();

            foreach (var candidate in candidates)
            {
                try
                {
                    JukeboxLog.Info($"[YoutubeAudioResolver] Opening unthrottled stream: '{videoId}' ({candidate.Container} {candidate.AudioCodec} {candidate.Bitrate.KiloBitsPerSecond:F0} kbps).");
                    var stream = await _client.Videos.Streams.GetAsync(candidate, ct).ConfigureAwait(false);
                    return stream;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    JukeboxLog.Warn($"[YoutubeAudioResolver] Stream candidate failed ({candidate.Container} {candidate.AudioCodec} {candidate.Bitrate.KiloBitsPerSecond:F0} kbps) for '{videoId}': {ex.Message}. Trying fallback candidate...");
                }
            }

            JukeboxLog.Error($"[YoutubeAudioResolver] All audio stream candidates failed for '{videoId}'.");
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            JukeboxLog.Error($"[YoutubeAudioResolver] OpenAudioStream failed for '{videoId}': {ex.Message}");
            return null;
        }
    }
}
