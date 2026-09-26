using System;
using System.Threading;
using System.Threading.Tasks;

namespace MetroHub.Core.Radio;

/// <summary>
/// Resolved playback URLs for one track. Either may be null when that container
/// is absent from the manifest.
/// </summary>
public sealed record JukeboxAudioUrls(string? OpusUrl, string? AacUrl);

/// <summary>
/// Independent song player for the Jukebox widget. Own BASS channel, own volume/mute,
/// true pause/resume (songs have timelines — unlike live radio, pause keeps the stream).
/// Never calls <c>Bass.Free()</c>: the output device is shared with the radio service.
/// </summary>
public interface IJukeboxAudioService : IDisposable
{
    /// <summary>True while a track is audibly playing.</summary>
    bool IsPlaying { get; }

    /// <summary>True while connecting/buffering a track.</summary>
    bool IsBuffering { get; }

    /// <summary>Label of the loaded track (title), empty when none.</summary>
    string CurrentLabel { get; }

    /// <summary>True when a track is loaded (playable/paused), even if not playing.</summary>
    bool HasTrack { get; }

    /// <summary>Volume 0.0–1.0, independent of the radio service.</summary>
    double Volume { get; set; }

    /// <summary>Mute, independent of the radio service.</summary>
    bool IsMuted { get; set; }

    /// <summary>Last error message, if any.</summary>
    string? LastErrorMessage { get; }

    /// <summary>
    /// Stops any current track and plays the given URLs, fastest reliable path first:
    /// push-download AAC → push-download Opus → direct URL opens. Push paths stream through
    /// our own HttpClient because BASS's downloader stalls on throttled googlevideo responses.
    /// </summary>
    Task PlayUrlAsync(JukeboxAudioUrls urls, string label, CancellationToken ct = default);

    /// <summary>Pauses, keeping position for resume.</summary>
    void Pause();

    /// <summary>Resumes the paused track.</summary>
    void Resume();

    /// <summary>Stops and unloads the track.</summary>
    void Stop();

    /// <summary>Fired when playback starts or pauses.</summary>
    event EventHandler<bool>? PlaybackStateChanged;

    /// <summary>Fired when a track reaches its natural end (not pause/stop).</summary>
    event EventHandler? EndOfStreamReached;

    /// <summary>Fired with a human-readable message when playback fails.</summary>
    event EventHandler<string>? ErrorOccurred;
}
