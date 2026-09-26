using System;
using System.Threading;
using System.Threading.Tasks;

namespace MetroHub.Core.Radio;

/// <summary>
/// Singleton contract for managing application-wide radio stream audio playback.
/// </summary>
public interface IRadioAudioService : IDisposable
{
    /// <summary>Currently active or selected station.</summary>
    RadioStation? CurrentStation { get; }

    /// <summary>True if actively streaming audio.</summary>
    bool IsPlaying { get; }

    /// <summary>True if the stream is currently connecting or buffering.</summary>
    bool IsBuffering { get; }

    /// <summary>Volume level normalized from 0.0 to 1.0.</summary>
    double Volume { get; set; }

    /// <summary>True if playback is silenced.</summary>
    bool IsMuted { get; set; }

    /// <summary>Last encountered playback error, if any.</summary>
    string? LastErrorMessage { get; }

    /// <summary>Plays the specified radio station with debounce and cancellation support.</summary>
    Task PlayStationAsync(RadioStation station, CancellationToken ct = default);

    /// <summary>Pauses the live stream, gracefully disconnecting the network socket.</summary>
    void Pause();

    /// <summary>Resumes the current station live stream.</summary>
    void Resume();

    /// <summary>Toggles between Play and Pause.</summary>
    void TogglePlayPause();

    /// <summary>Stops playback and clears current station.</summary>
    void Stop();

    /// <summary>Sets the audio volume level (0.0 to 1.0).</summary>
    void SetVolume(double volume);

    /// <summary>Toggles or sets the audio mute state.</summary>
    void SetMuted(bool isMuted);

    /// <summary>Fired when the current station changes.</summary>
    event EventHandler<RadioStation?>? CurrentStationChanged;

    /// <summary>Fired when playback starts or pauses.</summary>
    event EventHandler<bool>? PlaybackStateChanged;

    /// <summary>Fired when buffering starts or completes.</summary>
    event EventHandler<bool>? BufferingStateChanged;

    /// <summary>Fired when the volume level changes.</summary>
    event EventHandler<double>? VolumeChanged;

    /// <summary>Fired when the mute state changes.</summary>
    event EventHandler<bool>? MuteStateChanged;

    /// <summary>Fired when an error occurs during stream playback.</summary>
    event EventHandler<string>? ErrorOccurred;
}
