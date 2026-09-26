using System;
using System.Collections.Generic;

namespace MetroHub.Widgets.Catalog.Jukebox;

/// <summary>
/// One pinned song in jukebox history. Stream URLs are NEVER stored (they expire within
/// hours) — only the VideoId, re-resolved on every tap.
/// </summary>
public sealed class JukeboxTrack
{
    public string VideoId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }
    public long DurationTicks { get; set; }
    public DateTime LastPlayedAt { get; set; }
    public int PlayCount { get; set; }

    /// <summary>"3:24" style duration, empty when unknown (bindable without a converter).</summary>
    public string DurationText => DurationTicks > 0
        ? $"{TimeSpan.FromTicks(DurationTicks).TotalMinutes:0}:{TimeSpan.FromTicks(DurationTicks).Seconds:D2}"
        : string.Empty;
}

/// <summary>
/// Persisted configuration payload for the YouTube Jukebox widget.
/// History is uncapped by design; each entry is ~200 bytes.
/// </summary>
public class JukeboxWidgetSettings
{
    public List<JukeboxTrack> History { get; set; } = new();
    public string? LastVideoId { get; set; }
}
