using System;
using System.Diagnostics;
using System.IO;
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
    private readonly AudioSpectrumProcessor _spectrumProcessor = new();
    private int _currentStream;
    private long _switchVersion;
    private readonly System.Collections.Generic.HashSet<int> _retiringStreams = new();
    private CancellationTokenSource? _switchCts;

    // Hard delegate references to prevent native Garbage Collection
    private readonly SyncProcedure _stallSyncProc;
    private readonly SyncProcedure _endSyncProc;

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
            bool changed;
            lock (_gate)
            {
                changed = !ReferenceEquals(_currentStation, value) && _currentStation?.Id != value?.Id;
                _currentStation = value;
            }
            if (changed)
            {
                CurrentStationChanged?.Invoke(this, value);
            }
        }
    }

    public bool IsPlaying
    {
        get => Volatile.Read(ref _isPlaying);
        private set
        {
            bool changed = false;
            lock (_gate)
            {
                if (_isPlaying != value)
                {
                    _isPlaying = value;
                    changed = true;
                }
            }
            if (changed)
            {
                PlaybackStateChanged?.Invoke(this, value);
            }
        }
    }

    public bool IsBuffering
    {
        get => Volatile.Read(ref _isBuffering);
        private set
        {
            bool changed = false;
            lock (_gate)
            {
                if (_isBuffering != value)
                {
                    _isBuffering = value;
                    changed = true;
                }
            }
            if (changed)
            {
                BufferingStateChanged?.Invoke(this, value);
            }
        }
    }

    public double Volume
    {
        get { lock (_gate) return _volume; }
        set => SetVolume(value);
    }

    public bool IsMuted
    {
        get => Volatile.Read(ref _isMuted);
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
    public event EventHandler? EndOfStreamReached;

    public RadioAudioService()
    {
        // 1. Ensure native library resolver is active
        BassLoader.Register();

        // 2. Retain persistent delegate references for native callbacks
        _stallSyncProc = OnStallSync;
        _endSyncProc = OnEndSync;

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
            Bass.Configure(Configuration.NetTimeOut, 6000);             // 6s connection timeout
            Bass.Configure(Configuration.IncludeDefaultDevice, true);   // Dynamically follow default endpoint changes

            // 5. Load decoder plugins using full absolute paths.
            // Bass.PluginLoad uses native LoadLibrary which searches the process CWD,
            // NOT AppContext.BaseDirectory. Desktop shortcuts set CWD to System32.
            string baseDir = AppContext.BaseDirectory;
            LoadBassPlugin(baseDir, "bass_aac.dll");
            LoadBassPlugin(baseDir, "basshls.dll");
            LoadBassPlugin(baseDir, "bassopus.dll");
        }
        else
        {
            Debug.WriteLine($"[RadioAudioService] BASS initialization failed: {Bass.LastError}");
        }
    }

    /// <summary>
    /// Resolves the full absolute path for a BASS plugin DLL and loads it.
    /// Tries AppContext.BaseDirectory, runtimes/win-x64/native/, and solution lib fallback.
    /// </summary>
    private static int LoadBassPlugin(string baseDir, string fileName)
    {
        string[] candidates =
        [
            Path.Combine(baseDir, fileName),
            Path.Combine(baseDir, "runtimes", "win-x64", "native", fileName),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "lib", "native", "win-x64", fileName))
        ];

        foreach (string path in candidates)
        {
            if (File.Exists(path))
            {
                try
                {
                    int handle = Bass.PluginLoad(path);
                    if (handle != 0)
                    {
                        Debug.WriteLine($"[RadioAudioService] Loaded {fileName} from: {path} (handle={handle}).");
                        return handle;
                    }
                    Debug.WriteLine($"[RadioAudioService] {fileName} found at {path} but PluginLoad returned 0 (error={Bass.LastError}).");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[RadioAudioService] {fileName} load failed from {path}: {ex.Message}");
                }
            }
        }

        Debug.WriteLine($"[RadioAudioService] {fileName} not found in any candidate path.");
        return 0;
    }

    public async Task PlayStationAsync(RadioStation station, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(station);

        CancellationTokenSource linkedCts;
        long myVersion;
        lock (_gate)
        {
            if (_isDisposed) return;

            // Cancel any pending switch immediately
            _switchCts?.Cancel();
            _switchCts?.Dispose();
            _switchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts = _switchCts;
            myVersion = ++_switchVersion;
        }

        try
        {
            // Debounce rapid clicking by 180ms
            await Task.Delay(180, linkedCts.Token).ConfigureAwait(false);

            int oldStream;
            lock (_gate)
            {
                if (linkedCts.Token.IsCancellationRequested || _isDisposed || _switchVersion != myVersion) return;

                _currentStation = station;
                _isBuffering = true;
                _lastErrorMessage = null;
                oldStream = _currentStream;
            }

            // Fire state events OUTSIDE _gate to prevent AB-BA deadlocks with Dispatcher
            CurrentStationChanged?.Invoke(this, station);
            BufferingStateChanged?.Invoke(this, true);

            // Connect to URL on background thread. If cancelled or timed out while Bass.CreateStream
            // is blocked in native socket code, WaitAsync returns immediately and the background task
            // frees the orphaned stream handle as soon as Bass.CreateStream completes.
            var createStreamTask = Task.Run(() =>
            {
                if (linkedCts.Token.IsCancellationRequested) return (Stream: 0, Error: Errors.OK);

                int stream = Bass.CreateStream(
                    station.StreamUrl,
                    0,
                    BassFlags.StreamDownloadBlocks,
                    null,
                    IntPtr.Zero);

                Errors err = stream == 0 ? Bass.LastError : Errors.OK;

                bool shouldDiscard;
                lock (_gate)
                {
                    shouldDiscard = linkedCts.Token.IsCancellationRequested || _isDisposed || _switchVersion != myVersion;
                }

                if (shouldDiscard && stream != 0)
                {
                    try { Bass.StreamFree(stream); } catch { }
                    return (Stream: 0, Error: Errors.OK);
                }

                return (Stream: stream, Error: err);
            });

            var (newStream, createError) = await createStreamTask
                .WaitAsync(TimeSpan.FromSeconds(7), linkedCts.Token)
                .ConfigureAwait(false);

            bool isCancelled;
            double volumeSnapshot;
            bool mutedSnapshot;
            lock (_gate)
            {
                isCancelled = linkedCts.Token.IsCancellationRequested || _isDisposed || _switchVersion != myVersion;
                volumeSnapshot = _volume;
                mutedSnapshot = _isMuted;
            }

            if (isCancelled)
            {
                if (newStream != 0)
                {
                    FreeStreamInBackground(newStream);
                }
                return;
            }

            if (newStream == 0)
            {
                // Silent Self-Healing: If stream failed to connect and station has an API UUID or Name,
                // query Radio-Browser community CDN mirrors for a fresh working URL.
                string? healedUrl = null;
                if (!string.IsNullOrWhiteSpace(station.ApiStationUuid) || !string.IsNullOrWhiteSpace(station.Name))
                {
                    try
                    {
                        Debug.WriteLine($"[RadioAudioService] Stream failed ({createError}). Attempting self-healing for '{station.Name}'...");
                        healedUrl = await RadioBrowserClient.Instance.ResolveWorkingUrlAsync(
                            station.ApiStationUuid, station.Name, linkedCts.Token).ConfigureAwait(false);
                    }
                    catch (Exception healEx)
                    {
                        Debug.WriteLine($"[RadioAudioService] Self-healing resolution exception: {healEx.Message}");
                    }
                }

                if (!string.IsNullOrWhiteSpace(healedUrl) && !string.Equals(healedUrl, station.StreamUrl, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine($"[RadioAudioService] Self-healing found new URL for '{station.Name}': {healedUrl}. Retrying playback...");

                    // Persist healed URL into local catalog atomically so subsequent launches use the fresh link
                    _ = RadioCatalogService.Instance.UpdateStationUrlAsync(station.Id, healedUrl);

                    var healedStation = station with { StreamUrl = healedUrl };
                    lock (_gate)
                    {
                        if (_switchVersion == myVersion)
                        {
                            _currentStation = healedStation;
                        }
                    }

                    // Retry connecting with healed URL
                    var retryTask = Task.Run(() =>
                    {
                        if (linkedCts.Token.IsCancellationRequested) return (Stream: 0, Error: Errors.OK);

                        int stream = Bass.CreateStream(
                            healedUrl,
                            0,
                            BassFlags.StreamDownloadBlocks,
                            null,
                            IntPtr.Zero);

                        Errors err = stream == 0 ? Bass.LastError : Errors.OK;

                        bool shouldDiscard;
                        lock (_gate)
                        {
                            shouldDiscard = linkedCts.Token.IsCancellationRequested || _isDisposed || _switchVersion != myVersion;
                        }

                        if (shouldDiscard && stream != 0)
                        {
                            try { Bass.StreamFree(stream); } catch { }
                            return (Stream: 0, Error: Errors.OK);
                        }

                        return (Stream: stream, Error: err);
                    });

                    var (healedStream, retryError) = await retryTask
                        .WaitAsync(TimeSpan.FromSeconds(7), linkedCts.Token)
                        .ConfigureAwait(false);

                    newStream = healedStream;
                    createError = retryError;
                }

                if (newStream == 0)
                {
                    HandlePlaybackError($"Failed to stream {station.Name} ({createError})", myVersion);
                    return;
                }
            }

            // Configure and start BASS channel OUTSIDE _gate so native audio mutexes never nest with _gate
            Bass.ChannelSetAttribute(newStream, ChannelAttribute.Volume, 0f);
            Bass.ChannelSetSync(newStream, SyncFlags.Stalled, 0, _stallSyncProc, IntPtr.Zero);
            Bass.ChannelSetSync(newStream, SyncFlags.End, 0, _endSyncProc, IntPtr.Zero);

            bool started = Bass.ChannelPlay(newStream);
            if (!started)
            {
                var error = Bass.LastError;
                FreeStreamInBackground(newStream);
                HandlePlaybackError($"Failed to play audio for {station.Name} ({error})", myVersion);
                return;
            }

            int streamToFade = 0;
            float targetVol;
            lock (_gate)
            {
                if (linkedCts.Token.IsCancellationRequested || _isDisposed || _switchVersion != myVersion)
                {
                    // Another station switch or Pause() occurred while ChannelPlay was starting
                    FreeStreamInBackground(newStream);
                    return;
                }

                streamToFade = _currentStream;
                _currentStream = newStream;
                _isPlaying = true;
                _isBuffering = false;
                targetVol = _isMuted ? 0f : (float)_volume;
            }

            const int CrossfadeMs = 1500;
            Bass.ChannelSlideAttribute(newStream, ChannelAttribute.Volume, targetVol, CrossfadeMs);

            if (streamToFade != 0 && streamToFade != newStream)
            {
                FadeAndFreeStream(streamToFade, CrossfadeMs);
            }

            // Fire UI state notifications OUTSIDE _gate
            PlaybackStateChanged?.Invoke(this, true);
            BufferingStateChanged?.Invoke(this, false);
        }
        catch (OperationCanceledException)
        {
            // Debounced or superseded by a newer station click or Pause()
        }
        catch (TimeoutException)
        {
            HandlePlaybackError($"Connection timed out for {station.Name}", myVersion);
        }
        catch (Exception ex)
        {
            HandlePlaybackError($"Playback exception on {station.Name}: {ex.Message}", myVersion);
        }
    }

    public void Pause()
    {
        int streamToStop;
        int[] retiringSnapshot;
        bool wasPlaying;
        bool wasBuffering;

        lock (_gate)
        {
            if (_isDisposed) return;

            _switchVersion++;
            _switchCts?.Cancel();

            streamToStop = _currentStream;
            _currentStream = 0;
            wasPlaying = _isPlaying;
            wasBuffering = _isBuffering;
            _isPlaying = false;
            _isBuffering = false;

            retiringSnapshot = new int[_retiringStreams.Count];
            _retiringStreams.CopyTo(retiringSnapshot);
            _retiringStreams.Clear();
        }

        _spectrumProcessor.Reset();

        // Fire state change events OUTSIDE _gate
        if (wasPlaying) PlaybackStateChanged?.Invoke(this, false);
        if (wasBuffering) BufferingStateChanged?.Invoke(this, false);

        if (streamToStop != 0)
        {
            FadeAndFreeStream(streamToStop, 200);
        }

        if (retiringSnapshot.Length > 0)
        {
            _ = Task.Run(() =>
            {
                foreach (int handle in retiringSnapshot)
                {
                    if (handle != 0 && handle != streamToStop)
                    {
                        try
                        {
                            Bass.ChannelStop(handle);
                            Bass.StreamFree(handle);
                        }
                        catch { }
                    }
                }
            });
        }
    }

    private static void FreeStreamInBackground(int streamHandle)
    {
        if (streamHandle == 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                Bass.ChannelStop(streamHandle);
                Bass.StreamFree(streamHandle);
            }
            catch { }
        });
    }

    private void FadeAndFreeStream(int streamHandle, int durationMs)
    {
        if (streamHandle == 0) return;

        lock (_gate)
        {
            _retiringStreams.Add(streamHandle);
        }

        _ = Task.Run(async () =>
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
        RadioStation? station;
        lock (_gate)
        {
            if (_isDisposed || _isPlaying || _currentStation == null) return;
            station = _currentStation;
        }

        _ = PlayStationAsync(station);
    }

    public void TogglePlayPause()
    {
        bool currentlyPlaying;
        lock (_gate)
        {
            currentlyPlaying = _isPlaying || _isBuffering;
        }

        if (currentlyPlaying)
        {
            Pause();
        }
        else
        {
            Resume();
        }
    }

    public void Stop()
    {
        Pause();
        CurrentStation = null;
        _spectrumProcessor.Reset();
    }

    public void SetVolume(double volume)
    {
        double clamped = Math.Clamp(volume, 0.0, 1.0);
        int streamToUpdate = 0;
        lock (_gate)
        {
            if (Math.Abs(_volume - clamped) < 0.001) return;
            _volume = clamped;

            if (_currentStream != 0 && !_isMuted)
            {
                streamToUpdate = _currentStream;
            }
        }

        if (streamToUpdate != 0)
        {
            try { Bass.ChannelSetAttribute(streamToUpdate, ChannelAttribute.Volume, (float)clamped); } catch { }
        }

        VolumeChanged?.Invoke(this, clamped);
    }

    public void SetMuted(bool isMuted)
    {
        int streamToUpdate = 0;
        float targetVol = 0f;
        lock (_gate)
        {
            if (_isMuted == isMuted) return;
            _isMuted = isMuted;

            if (_currentStream != 0)
            {
                streamToUpdate = _currentStream;
                targetVol = _isMuted ? 0f : (float)_volume;
            }
        }

        if (streamToUpdate != 0)
        {
            try { Bass.ChannelSetAttribute(streamToUpdate, ChannelAttribute.Volume, targetVol); } catch { }
        }

        MuteStateChanged?.Invoke(this, isMuted);
    }

    public bool GetSpectrumLevels(out float kick, out float bass, out float mid, out float treble)
    {
        int stream = Volatile.Read(ref _currentStream);
        bool isPlaying = Volatile.Read(ref _isPlaying);
        bool isMuted = Volatile.Read(ref _isMuted);

        if (!isPlaying || isMuted || stream == 0)
        {
            _spectrumProcessor.DecayToZero(out kick, out bass, out mid, out treble);
            return false;
        }

        return _spectrumProcessor.ProcessChannel(stream, out kick, out bass, out mid, out treble);
    }

    public bool GetSpectrumLevels(out float bass, out float mid, out float treble)
    {
        return GetSpectrumLevels(out _, out bass, out mid, out treble);
    }

    private void OnStallSync(int handle, int channel, int data, IntPtr user)
    {
        if (channel != Volatile.Read(ref _currentStream)) return;

        // data == 0: playback stalled (network buffer underrun)
        // data == 1: playback resumed
        bool isBuffering = (data == 0);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (channel == Volatile.Read(ref _currentStream))
            {
                IsBuffering = isBuffering;
            }
        });
    }

    private void OnEndSync(int handle, int channel, int data, IntPtr user)
    {
        if (channel != Volatile.Read(ref _currentStream)) return;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            bool changed = false;
            lock (_gate)
            {
                if (channel == _currentStream)
                {
                    _currentStream = 0;
                    _isPlaying = false;
                    _isBuffering = false;
                    changed = true;
                }
            }
            if (changed)
            {
                PlaybackStateChanged?.Invoke(this, false);
                BufferingStateChanged?.Invoke(this, false);
                // Natural end (Pause/Stop zero _currentStream first, so a match here
                // means the stream finished).
                EndOfStreamReached?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    private void HandlePlaybackError(string message, long expectedVersion)
    {
        bool shouldNotify = false;
        lock (_gate)
        {
            if (_switchVersion == expectedVersion)
            {
                _lastErrorMessage = message;
                _isPlaying = false;
                _isBuffering = false;
                shouldNotify = true;
            }
        }

        if (shouldNotify)
        {
            Debug.WriteLine($"[RadioAudioService] Error: {message}");
            PlaybackStateChanged?.Invoke(this, false);
            BufferingStateChanged?.Invoke(this, false);
            ErrorOccurred?.Invoke(this, message);
        }
    }

    public void Dispose()
    {
        int streamToFree;
        int[] retiringSnapshot;
        lock (_gate)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _switchVersion++;

            _switchCts?.Cancel();
            _switchCts?.Dispose();
            _switchCts = null;

            streamToFree = _currentStream;
            _currentStream = 0;

            retiringSnapshot = new int[_retiringStreams.Count];
            _retiringStreams.CopyTo(retiringSnapshot);
            _retiringStreams.Clear();
        }

        _ = Task.Run(() =>
        {
            if (streamToFree != 0)
            {
                try
                {
                    Bass.ChannelStop(streamToFree);
                    Bass.StreamFree(streamToFree);
                }
                catch { }
            }

            foreach (int handle in retiringSnapshot)
            {
                if (handle != 0)
                {
                    try
                    {
                        Bass.ChannelStop(handle);
                        Bass.StreamFree(handle);
                    }
                    catch { }
                }
            }

            try
            {
                Bass.Free();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RadioAudioService] BASS disposal error: {ex.Message}");
            }
        });
    }
}
