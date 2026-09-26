using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace MetroHub.Core.Radio;

/// <summary>
/// High-performance singleton implementation of <see cref="IRadioAudioService"/>
/// using Windows 10/11's modern hardware-accelerated <see cref="Windows.Media.Playback.MediaPlayer"/>.
/// </summary>
public sealed class RadioAudioService : IRadioAudioService
{
    private static readonly Lazy<RadioAudioService> _lazyInstance = new(() => new RadioAudioService());
    public static RadioAudioService Instance => _lazyInstance.Value;

    private readonly object _gate = new();
    private readonly Windows.Media.Playback.MediaPlayer _player;
    private MediaSource? _currentMediaSource;
    private CancellationTokenSource? _switchCts;

    private RadioStation? _currentStation;
    private bool _isPlaying;
    private bool _isBuffering;
    private double _volume = 0.5;
    private bool _isMuted;
    private string? _lastErrorMessage;
    private bool _isDisposed;

    public RadioStation? CurrentStation
    {
        get { lock (_gate) return _currentStation; }
        private set
        {
            lock (_gate) _currentStation = value;
            CurrentStationChanged?.Invoke(this, value);
        }
    }

    public bool IsPlaying
    {
        get { lock (_gate) return _isPlaying; }
        private set
        {
            lock (_gate)
            {
                if (_isPlaying == value) return;
                _isPlaying = value;
            }
            PlaybackStateChanged?.Invoke(this, value);
        }
    }

    public bool IsBuffering
    {
        get { lock (_gate) return _isBuffering; }
        private set
        {
            lock (_gate)
            {
                if (_isBuffering == value) return;
                _isBuffering = value;
            }
            BufferingStateChanged?.Invoke(this, value);
        }
    }

    public double Volume
    {
        get { lock (_gate) return _volume; }
        set
        {
            SetVolume(value);
        }
    }

    public bool IsMuted
    {
        get { lock (_gate) return _isMuted; }
        set
        {
            SetMuted(value);
        }
    }

    public string? LastErrorMessage
    {
        get { lock (_gate) return _lastErrorMessage; }
        private set { lock (_gate) _lastErrorMessage = value; }
    }

    public event EventHandler<RadioStation?>? CurrentStationChanged;
    public event EventHandler<bool>? PlaybackStateChanged;
    public event EventHandler<bool>? BufferingStateChanged;
    public event EventHandler<double>? VolumeChanged;
    public event EventHandler<bool>? MuteStateChanged;
    public event EventHandler<string>? ErrorOccurred;

    public RadioAudioService()
    {
        _player = new Windows.Media.Playback.MediaPlayer
        {
            AutoPlay = false,
            Volume = _volume,
            IsMuted = _isMuted
        };

        _player.MediaOpened += OnMediaOpened;
        _player.MediaFailed += OnMediaFailed;
        _player.BufferingStarted += OnBufferingStarted;
        _player.BufferingEnded += OnBufferingEnded;
    }

    public async Task PlayStationAsync(RadioStation station, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(station);

        CancellationTokenSource linkedCts;
        lock (_gate)
        {
            if (_isDisposed) return;

            // Cancel any pending switch
            _switchCts?.Cancel();
            _switchCts?.Dispose();
            _switchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts = _switchCts;
        }

        try
        {
            // Debounce rapid clicking by 180ms
            await Task.Delay(180, linkedCts.Token).ConfigureAwait(false);

            // Execute switch on player
            lock (_gate)
            {
                if (linkedCts.Token.IsCancellationRequested || _isDisposed) return;

                CurrentStation = station;
                IsBuffering = true;
                LastErrorMessage = null;

                // Dispose old source cleanly
                if (_currentMediaSource != null)
                {
                    _currentMediaSource.Dispose();
                    _currentMediaSource = null;
                }

                try
                {
                    _currentMediaSource = MediaSource.CreateFromUri(new Uri(station.StreamUrl));
                    _player.Source = _currentMediaSource;
                    _player.Volume = _volume;
                    _player.IsMuted = _isMuted;
                    _player.Play();
                    IsPlaying = true;
                }
                catch (Exception ex)
                {
                    HandlePlaybackError($"Failed to initialize stream {station.Name}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Debounced by a newer station click; expected
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_isDisposed || !IsPlaying) return;

            // Quick fade-out to prevent pops
            try
            {
                _player.Pause();

                // Cleanly disconnect socket on pause to save user bandwidth
                if (_currentMediaSource != null)
                {
                    _player.Source = null;
                    _currentMediaSource.Dispose();
                    _currentMediaSource = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RadioAudioService] Pause exception: {ex.Message}");
            }

            IsPlaying = false;
            IsBuffering = false;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_isDisposed || IsPlaying || _currentStation == null) return;

            var station = _currentStation;
            _ = PlayStationAsync(station);
        }
    }

    public void TogglePlayPause()
    {
        lock (_gate)
        {
            if (IsPlaying)
            {
                Pause();
            }
            else
            {
                Resume();
            }
        }
    }

    public void Stop()
    {
        Pause();
        lock (_gate)
        {
            CurrentStation = null;
        }
    }

    public void SetVolume(double volume)
    {
        double clamped = Math.Clamp(volume, 0.0, 1.0);
        lock (_gate)
        {
            if (Math.Abs(_volume - clamped) < 0.001) return;
            _volume = clamped;

            try
            {
                _player.Volume = _volume;
            }
            catch { }
        }

        VolumeChanged?.Invoke(this, clamped);
    }

    public void SetMuted(bool isMuted)
    {
        lock (_gate)
        {
            if (_isMuted == isMuted) return;
            _isMuted = isMuted;

            try
            {
                _player.IsMuted = _isMuted;
            }
            catch { }
        }

        MuteStateChanged?.Invoke(this, isMuted);
    }

    private void OnMediaOpened(Windows.Media.Playback.MediaPlayer sender, object args)
    {
        IsBuffering = false;
        IsPlaying = true;
    }

    private void OnMediaFailed(Windows.Media.Playback.MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        string message = $"{args.Error}: {args.ErrorMessage} (0x{args.ExtendedErrorCode?.HResult:X8})";
        HandlePlaybackError(message);
    }

    private void OnBufferingStarted(Windows.Media.Playback.MediaPlayer sender, object args)
    {
        IsBuffering = true;
    }

    private void OnBufferingEnded(Windows.Media.Playback.MediaPlayer sender, object args)
    {
        IsBuffering = false;
    }

    private void HandlePlaybackError(string message)
    {
        lock (_gate)
        {
            LastErrorMessage = message;
            IsPlaying = false;
            IsBuffering = false;
        }

        Debug.WriteLine($"[RadioAudioService] Error: {message}");
        ErrorOccurred?.Invoke(this, message);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _switchCts?.Cancel();
            _switchCts?.Dispose();
            _switchCts = null;

            try
            {
                _player.MediaOpened -= OnMediaOpened;
                _player.MediaFailed -= OnMediaFailed;
                _player.BufferingStarted -= OnBufferingStarted;
                _player.BufferingEnded -= OnBufferingEnded;

                _player.Pause();
                _player.Source = null;

                _currentMediaSource?.Dispose();
                _currentMediaSource = null;

                _player.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RadioAudioService] Disposal error: {ex.Message}");
            }
        }
    }
}
