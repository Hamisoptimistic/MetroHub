using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MetroHub.Core.Radio;

/// <summary>
/// Lightweight YouTube search hit. Decoupled from YoutubeExplode types for testability.
/// </summary>
public sealed record YoutubeSearchHit(
    string VideoId,
    string Title,
    string Channel,
    string? ThumbnailUrl,
    TimeSpan? Duration)
{
    /// <summary>"3:24" style duration, empty when unknown (bindable without a converter).</summary>
    public string DurationText => Duration.HasValue
        ? $"{(int)Duration.Value.TotalMinutes}:{Duration.Value.Seconds:D2}"
        : string.Empty;
}

/// <summary>
/// All usable audio URLs for one video, resolved from a single manifest fetch.
/// Stream URLs expire within hours: resolve-then-play immediately, never persist.
/// Any slot may be null when that tier is absent.
/// </summary>
public sealed record JukeboxStreamSet(string? LowUrl, string? OpusUrl, string? AacUrl);

/// <summary>
/// Contract for YouTube search + unthrottled audio stream resolution.
/// </summary>
public interface IYoutubeAudioResolver
{
    /// <summary>Top-N search hits for a query, single video URL, or playlist URL. Empty list when offline or on failure.</summary>
    Task<IReadOnlyList<YoutubeSearchHit>> SearchAsync(string query, int topN = 8, CancellationToken ct = default);

    /// <summary>Fetches all videos in a playlist (up to maxItems, default 1000).</summary>
    Task<IReadOnlyList<YoutubeSearchHit>> GetPlaylistVideosAsync(string playlistUrlOrId, int maxItems = 1000, CancellationToken ct = default);

    /// <summary>
    /// Best-effort full tier set for a video in ONE manifest fetch: cheapest stream
    /// (low-bandwidth Opus, else cheapest AAC), best Opus, best AAC/MP4.
    /// Null when unresolvable (deleted, blocked, cipher breakage) — never throws for those.
    /// </summary>
    Task<JukeboxStreamSet?> ResolveStreamsAsync(string videoId, CancellationToken ct = default);

    /// <summary>
    /// Opens an unthrottled audio stream directly using YoutubeExplode's internal pipeline.
    /// </summary>
    Task<Stream?> OpenAudioStreamAsync(string videoId, CancellationToken ct = default);
}