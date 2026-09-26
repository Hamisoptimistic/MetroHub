using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ManagedBass;

namespace MetroHub.Core.Radio;

/// <summary>
/// High-performance singleton implementation of <see cref="IRadioAudioService"/>
/// powered by the industry-standard Un4seen BASS audio engine (via ManagedBass).
/// Directly streams raw Icecast, Shoutcast, Radio.co, and AAC/MP3 chunked internet streams
/// with decoupled buffer architecture, custom User-Agent, and hardware WASAPI volume fading.
/// </summary>
public sealed class RadioAudioService : IRadioAudioService
{
    private static readonly Lazy<RadioAudioService> _lazyInstance = new(() => new RadioAudioService());
    public static RadioAudioService Instance => _lazyInstance.Value;

    private readonly object _gate = new();
    private int _currentStream;
    private readonly System.Collections.Generic.HashSet<int> _retiringStreams = new();
    private CancellationTokenSource? _switchCts;

    // Hard delegate references to prevent native Garbage Collection
    private readonly SyncProcedure _stallSyncProc;
    private readonly SyncProcedure _endSyncProc;
    private readonly SyncProcedure _metaSyncProc;

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
        set => SetVolume(value);
    }

    public bool IsMuted
    {
        get { lock (_gate) return _isMuted; }
        set => SetMuted(value);
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
        // 1. Ensure native library resolver is active
        BassLoader.Register();

        // 2. Retain persistent delegate references for native callbacks
        _stallSyncProc = OnStallSync;
        _endSyncProc = OnEndSync;
        _metaSyncProc = OnMetaSync;

        // 3. Initialize BASS engine (device -1 is default Windows Core Audio/WASAPI device)
        bool initialized = Bass.Init(-1, 44100, DeviceInitFlags.Default, IntPtr.Zero);
        if (!initialized && Bass.LastError == Errors.Already)
        {
            initialized = true;
        }
        else if (!initialized)
        {
            // Fallback to "No Sound" device (0) for headless CI / unit test environments
            initialized = Bass.Init(0, 44100, DeviceInitFlags.Default, IntPtr.Zero);
            if (!initialized && Bass.LastError == Errors.Already)
            {
                initialized = true;
            }
        }

        if (initialized)
        {
            // 4. Configure stream networking and buffer formula ("Never Stall / Never Buffer Loop")
            Bass.NetAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36 MetroHub/1.0";
            Bass.Configure(Configuration.NetBufferLength, 5000);         // 5-second ring buffer in RAM
            Bass.Configure(Configuration.PlaybackBufferLength, 2000);   // 2-second playback mixing buffer
            Bass.Configure(Configuration.NetPreBuffer, 75);             // Start playback when 75% pre-buffered
            Bass.Configure(Configuration.NetTimeOut, 10000);            // 10s connection timeout
            Bass.Configure(Configuration.IncludeDefaultDevice, true);   // Dynamically follow default endpoint changes

            // 5. Load AAC decoder plugin for streams like Birdsong / SomaFM AAC / DEF CON
            try
            {
                Bass.PluginLoad("bass_aac.dll");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RadioAudioService] Notice: AAC plugin load: {ex.Message}");
            }
        }
        else
        {
            Debug.WriteLine($"[RadioAudioService] BASS initialization failed: {Bass.LastError}");
        }
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

            int oldStream;
            lock (_gate)
            {
                if (linkedCts.Token.IsCancellationRequested || _isDisposed) return;

                CurrentStation = station;
                IsBuffering = true;
                LastErrorMessage = null;

                // DJ Crossfade: Keep previous stream playing while the new station connects!
                oldStream = _currentStream;
            }

            // Connect to URL on background thread while previous station continues playing seamlessly
            int newStream = await Task.Run(() =>
            {
                return Bass.CreateStream(
                    station.StreamUrl,
                    0,
                    BassFlags.StreamDownloadBlocks | BassFlags.AutoFree,
                    null,
                    IntPtr.Zero);
            }, linkedCts.Token).ConfigureAwait(false);

            lock (_gate)
            {
                if (linkedCts.Token.IsCancellationRequested || _isDisposed)
                {
                    if (newStream != 0)
                    {
                        Bass.StreamFree(newStream);
                    }
                    return;
                }

                if (newStream == 0)
                {
                    var error = Bass.LastError;
                    HandlePlaybackError($"Failed to stream {station.Name} ({error})");
                    return;
                }

                _currentStream = newStream;

                // 1. Initialize new stream at 0 volume for seamless crossfade
                Bass.ChannelSetAttribute(newStream, ChannelAttribute.Volume, 0f);

                // 2. Register native synchronization callbacks
                Bass.ChannelSetSync(newStream, SyncFlags.Stalled, 0, _stallSyncProc, IntPtr.Zero);
                Bass.ChannelSetSync(newStream, SyncFlags.End, 0, _endSyncProc, IntPtr.Zero);
                Bass.ChannelSetSync(newStream, SyncFlags.MetadataReceived, 0, _metaSyncProc, IntPtr.Zero);

                // 3. Start playback on new stream
                bool started = Bass.ChannelPlay(newStream);
                if (started)
                {
                    const int CrossfadeMs = 1500;
                    float targetVol = _isMuted ? 0f : (float)_volume;

                    // 4. Smoothly slide new stream volume UP to target volume (1.5s crossfade)
                    Bass.ChannelSlideAttribute(newStream, ChannelAttribute.Volume, targetVol, CrossfadeMs);

                    // 5. Concurrently slide old stream volume DOWN to 0 and disconnect socket cleanly
                    if (oldStream != 0 && oldStream != newStream)
                    {
                        FadeAndFreeStream(oldStream, CrossfadeMs);
                    }

                    IsPlaying = true;
                    IsBuffering = false;
                }
                else
                {
                    var error = Bass.LastError;
                    HandlePlaybackError($"Failed to play audio for {station.Name} ({error})");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Debounced by a newer station click; expected
        }
        catch (Exception ex)
        {
            HandlePlaybackError($"Playback exception on {station.Name}: {ex.Message}");
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_isDisposed || !IsPlaying) return;

            int streamToStop = _currentStream;
            _currentStream = 0;
            IsPlaying = false;
            IsBuffering = false;

            if (streamToStop != 0)
            {
                FadeAndFreeStream(streamToStop, 200);
            }

            // Immediately tear down any remaining retiring streams
            foreach (int handle in _retiringStreams)
            {
                try
                {
                    Bass.ChannelStop(handle);
                    Bass.StreamFree(handle);
                }
                catch { }
            }
            _retiringStreams.Clear();
        }
    }

    private void FadeAndFreeStream(int streamHandle, int durationMs)
    {
        if (streamHandle == 0) return;

        lock (_gate)
        {
            _retiringStreams.Add(streamHandle);
        }

        Task.Run(async () =>
        {
            try
            {
                Bass.ChannelSlideAttribute(streamHandle, ChannelAttribute.Volume, 0f, durationMs);
                await Task.Delay(durationMs + 100).ConfigureAwait(false);
            }
            catch { }
            finally
            {
                lock (_gate)
                {
                    _retiringStreams.Remove(streamHandle);
                }
                try
                {
                    Bass.ChannelStop(streamHandle);
                    Bass.StreamFree(streamHandle);
                }
                catch { }
            }
        });
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

            if (_currentStream != 0 && !_isMuted)
            {
                Bass.ChannelSetAttribute(_currentStream, ChannelAttribute.Volume, (float)_volume);
            }
        }

        VolumeChanged?.Invoke(this, clamped);
    }

    public void SetMuted(bool isMuted)
    {
        lock (_gate)
        {
            if (_isMuted == isMuted) return;
            _isMuted = isMuted;

            if (_currentStream != 0)
            {
                float targetVol = _isMuted ? 0f : (float)_volume;
                Bass.ChannelSetAttribute(_currentStream, ChannelAttribute.Volume, targetVol);
            }
        }

        MuteStateChanged?.Invoke(this, isMuted);
    }

    private void OnStallSync(int handle, int channel, int data, IntPtr user)
    {
        // data == 0: playback stalled (network buffer underrun)
        // data == 1: playback resumed
        bool isBuffering = (data == 0);
        IsBuffering = isBuffering;
        Debug.WriteLine($"[RadioAudioService] Stall sync: isBuffering={isBuffering}");
    }

    private void OnEndSync(int handle, int channel, int data, IntPtr user)
    {
        lock (_gate)
        {
            if (channel == _currentStream)
            {
                _currentStream = 0;
                IsPlaying = false;
                IsBuffering = false;
            }
        }
    }

    private void OnMetaSync(int handle, int channel, int data, IntPtr user)
    {
        try
        {
            IntPtr tagsPtr = Bass.ChannelGetTags(channel, TagType.ICY);
            if (tagsPtr != IntPtr.Zero)
            {
                string? icy = Marshal.PtrToStringAnsi(tagsPtr);
                Debug.WriteLine($"[RadioAudioService] ICY Tag: {icy}");
            }
        }
        catch { }
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

            if (_currentStream != 0)
            {
                try
                {
                    Bass.ChannelStop(_currentStream);
                    Bass.StreamFree(_currentStream);
                }
                catch { }
                _currentStream = 0;
            }

            foreach (int handle in _retiringStreams)
            {
                try
                {
                    Bass.ChannelStop(handle);
                    Bass.StreamFree(handle);
                }
                catch { }
            }
            _retiringStreams.Clear();

            try
            {
                Bass.Free();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RadioAudioService] BASS disposal error: {ex.Message}");
            }
        }
    }
}
