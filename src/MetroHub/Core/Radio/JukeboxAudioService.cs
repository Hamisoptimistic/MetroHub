using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ManagedBass;

namespace MetroHub.Core.Radio;

/// <summary>
/// Independent BASS song player for the Jukebox widget. Shares the output <i>device</i>
/// with the radio service (one <c>Bass.Init</c> wins, the other gets <c>Already</c>) but owns
/// its own channel, volume, and mute — stopping radio never touches songs and vice versa.
/// Pause keeps the stream (songs have timelines); only Stop frees it.
/// </summary>
/// <remarks>
/// <para>
/// Playback uses BASS's <b>buffered user-file system</b> (<see cref="StreamSystem.Buffer"/>):
/// a <see cref="YoutubeChunkedDownloader"/> fills a <see cref="ChunkedAudioBuffer"/> with short
/// HTTP range requests, and BASS pulls from it in its own download thread. BASS then buffers,
/// stalls, and resumes on its own, so a slow moment no longer ends the track.
/// </para>
/// <para>
/// We never call <c>Bass.Free()</c>: the output device is shared with the radio service.
/// </para>
/// </remarks>
public sealed class JukeboxAudioService : IJukeboxAudioService
{
    public static JukeboxAudioService Instance { get; } = new();

    private const int PrebufferPreferredBytes = 128 * 1024;
    private const int PrebufferMinimumBytes = 32 * 1024;
    private const int PrebufferTimeoutMs = 15000;
    private const int SwitchDelayMs = 120;

    private readonly object _gate = new();
    private readonly List<DateTime> _stallTimes = new();
    private readonly SyncProcedure _endSyncProc;
    private readonly SyncProcedure _stallSyncProc;

    private int _stream;
    private CancellationTokenSource? _switchCts;
    private Task _currentPlayTask = Task.CompletedTask;
    private ActiveSource? _activeSource;
    private string _currentLabel = string.Empty;
    private bool _isPlaying;
    private bool _isBuffering;
    private double _volume = 0.5;
    private bool _isMuted;
    private string? _lastErrorMessage;
    private bool _isDisposed;

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
        }
    }

    public string CurrentLabel
    {
        get { lock (_gate) return _currentLabel; }
        private set { lock (_gate) _currentLabel = value; }
    }

    public bool HasTrack
    {
        get { lock (_gate) return _stream != 0; }
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

    public event EventHandler<bool>? PlaybackStateChanged;
    public event EventHandler? EndOfStreamReached;
    public event EventHandler? StallStormDetected;
    public event EventHandler<string>? ErrorOccurred;

    public JukeboxAudioService()
    {
        BassLoader.Register();
        _endSyncProc = OnEndSync;
        _stallSyncProc = OnStallSync;
        EnsureDevice();
    }

    private static void EnsureDevice()
    {
        // Device init is process-wide: whoever (radio or jukebox) gets here second sees Already.
        bool initialized = Bass.Init(-1, 44100, DeviceInitFlags.Default, IntPtr.Zero);
        if (!initialized && Bass.LastError == Errors.Already)
        {
            initialized = true;
        }
        else if (!initialized)
        {
            initialized = Bass.Init(0, 44100, DeviceInitFlags.Default, IntPtr.Zero);
            if (!initialized && Bass.LastError == Errors.Already)
            {
                initialized = true;
            }
        }

        if (!initialized)
        {
            JukeboxLog.Error($"BASS init failed: {Bass.LastError}");
            return;
        }

        Bass.NetAgent = YoutubeChunkedDownloader.UserAgent + " MetroHub/1.0";
        Bass.Configure(Configuration.NetPreBuffer, 75);
        Bass.Configure(Configuration.NetTimeOut, 10000);

        string baseDir = AppContext.BaseDirectory;

        // Bass.PluginLoad uses native LoadLibrary which searches the process working directory,
        // NOT AppContext.BaseDirectory. When launched from a desktop shortcut the CWD can be
        // C:\Windows\System32 — so we must resolve full absolute paths for each plugin DLL.
        int aacPlugin = LoadBassPlugin(baseDir, "bass_aac.dll");
        int opusPlugin = LoadBassPlugin(baseDir, "bassopus.dll");
        int webmPlugin = LoadBassPlugin(baseDir, "basswebm.dll");
        JukeboxLog.Info($"Decoder plugins: aacHandle={aacPlugin} opusHandle={opusPlugin} webmHandle={webmPlugin} (0 = failed to load).");

        JukeboxLog.Info($"BASS device ready (default={Bass.LastError != Errors.Already}).");
        try
        {
            if (Bass.GetDeviceInfo(Bass.CurrentDevice, out DeviceInfo deviceInfo))
            {
                JukeboxLog.Info($"Output device: '{deviceInfo.Name}' driver='{deviceInfo.Driver}'.");
            }
        }
        catch (Exception ex)
        {
            JukeboxLog.Warn($"Device query failed: {ex.Message}");
        }
    }

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
                        JukeboxLog.Info($"Loaded {fileName} from: {path} (handle={handle}).");
                        return handle;
                    }
                    if (Bass.LastError == Errors.Already)
                    {
                        JukeboxLog.Info($"{fileName} already loaded in process: {path}.");
                        return -1;
                    }
                    JukeboxLog.Warn($"{fileName} found at {path} but PluginLoad returned 0 (error={Bass.LastError}).");
                }
                catch (Exception ex)
                {
                    JukeboxLog.Warn($"{fileName} load failed from {path}: {ex.Message}");
                }
            }
        }

        JukeboxLog.Warn($"{fileName} not found in any candidate path.");
        return 0;
    }

    /// <inheritdoc/>
    public Task PlayStreamAsync(Stream audioStream, string label, CancellationToken ct = default)
        => PlayStreamAsync(_ => Task.FromResult<Stream?>(audioStream), label, ct);

    /// <inheritdoc/>
    public Task PlayStreamAsync(Func<CancellationToken, Task<Stream?>> streamFactory, string label, CancellationToken ct = default)
    {
        if (streamFactory is null)
        {
            Fail($"No stream factory provided for: {label}");
            return Task.CompletedTask;
        }

        int previousStream;
        ActiveSource? previousSource;
        CancellationTokenSource? previousCts;

        lock (_gate)
        {
            if (_isDisposed) return Task.CompletedTask;
            (previousStream, previousSource, previousCts) = ResetForNewTrack(label, ct);
            _currentPlayTask = RunPlayStreamAsync(streamFactory, label, _switchCts!);
        }

        // These must run outside the lock (they can block/free native handles).
        CutAndCleanup(previousStream, previousSource, previousCts);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task PlayUrlAsync(JukeboxAudioUrls urls, string label, CancellationToken ct = default)
    {
        if (urls is null ||
            (string.IsNullOrWhiteSpace(urls.LowUrl) && string.IsNullOrWhiteSpace(urls.OpusUrl) && string.IsNullOrWhiteSpace(urls.AacUrl)))
        {
            Fail($"No playable URL resolved for: {label}");
            return Task.CompletedTask;
        }

        int previousStream;
        ActiveSource? previousSource;
        CancellationTokenSource? previousCts;

        lock (_gate)
        {
            if (_isDisposed) return Task.CompletedTask;
            (previousStream, previousSource, previousCts) = ResetForNewTrack(label, ct);
            _currentPlayTask = RunPlayAsync(urls, label, _switchCts!);
        }

        CutAndCleanup(previousStream, previousSource, previousCts);
        return Task.CompletedTask;
    }

    /// <summary>Under <c>_gate</c>: tears down the current track's bookkeeping and starts a fresh token.</summary>
    private (int PreviousStream, ActiveSource? PreviousSource, CancellationTokenSource? PreviousCts) ResetForNewTrack(string label, CancellationToken ct)
    {
        int previousStream = _stream;
        ActiveSource? previousSource = _activeSource;
        CancellationTokenSource? previousCts = _switchCts;

        _stream = 0;
        _activeSource = null;
        IsPlaying = false;
        IsBuffering = true;
        LastErrorMessage = null;
        CurrentLabel = label;
        _stallTimes.Clear();

        _switchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        return (previousStream, previousSource, previousCts);
    }

    private static void CutAndCleanup(int previousStream, ActiveSource? previousSource, CancellationTokenSource? previousCts)
    {
        // 1. Cut audio on the previous channel immediately (instant silence).
        if (previousStream != 0)
        {
            try { Bass.ChannelStop(previousStream); } catch { }
        }

        // 2. Cancel the previous producer (fire-and-forget: never block the UI thread).
        try { previousCts?.Cancel(); } catch { }

        // 3. Detached cleanup of previous native/memory resources.
        if (previousStream != 0 || previousSource != null || previousCts != null)
        {
            _ = Task.Run(() => DetachedCleanup(previousStream, previousSource, previousCts));
        }
    }

    private static void DetachedCleanup(int streamHandle, ActiveSource? source, CancellationTokenSource? cts)
    {
        try
        {
            if (streamHandle != 0)
            {
                Bass.ChannelStop(streamHandle);
                Bass.StreamFree(streamHandle);
            }
        }
        catch { }

        try { source?.Dispose(); } catch { }
        try { cts?.Dispose(); } catch { }
    }

    private async Task RunPlayAsync(JukeboxAudioUrls urls, string label, CancellationTokenSource linkedCts)
    {
        CancellationToken token = linkedCts.Token;
        try
        {
            await Task.Delay(SwitchDelayMs, token).ConfigureAwait(false);

            (string? Url, string Tier)[] candidates =
            [
                (urls.LowUrl, "low"),
                (urls.OpusUrl, "opus"),
                (urls.AacUrl, "aac"),
            ];

            foreach ((string? url, string tier) in candidates)
            {
                if (string.IsNullOrWhiteSpace(url)) continue;
                if (token.IsCancellationRequested || _isDisposed) return;

                JukeboxLog.Info($"Attempt chunked-{tier}: {label}");

                var buffer = new ChunkedAudioBuffer();
                var downloader = new YoutubeChunkedDownloader(buffer, url!, label);
                Task producer = downloader.RunAsync(token);

                bool started = await StartFromBufferAsync(buffer, producer, label, token).ConfigureAwait(false);
                if (started) return;

                try { downloader.Dispose(); } catch { }
                try { buffer.Dispose(); } catch { }
            }

            if (!token.IsCancellationRequested && !_isDisposed)
            {
                Fail($"Could not start playback for: {label}. All audio tiers failed.");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Fail($"Playback exception for '{label}': {ex.Message}");
        }
    }

    private async Task RunPlayStreamAsync(Func<CancellationToken, Task<Stream?>> streamFactory, string label, CancellationTokenSource linkedCts)
    {
        CancellationToken token = linkedCts.Token;
        try
        {
            await Task.Delay(SwitchDelayMs, token).ConfigureAwait(false);

            Stream? netStream = await streamFactory(token).ConfigureAwait(false);
            if (netStream is null)
            {
                Fail($"No audio stream returned for: {label}");
                return;
            }

            var buffer = new ChunkedAudioBuffer();
            Task producer = Task.Run(async () =>
            {
                try
                {
                    byte[] tmp = new byte[64 * 1024];
                    int n;
                    while ((n = await netStream.ReadAsync(tmp.AsMemory(0, tmp.Length), token).ConfigureAwait(false)) > 0)
                    {
                        buffer.Append(tmp, 0, n);
                    }
                    buffer.Complete();
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    buffer.CompleteFaulted(ex);
                }
                finally
                {
                    try { netStream.Dispose(); } catch { }
                }
            }, token);

            bool started = await StartFromBufferAsync(buffer, producer, label, token).ConfigureAwait(false);
            if (!started)
            {
                try { buffer.Dispose(); } catch { }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Fail($"Stream playback error for '{label}': {ex.Message}");
        }
    }

    /// <summary>
    /// Creates a BASS buffered user-file stream over <paramref name="buffer"/>, waits for a real
    /// prebuffer, then starts playback. Returns false so the caller can try the next tier.
    /// </summary>
    private async Task<bool> StartFromBufferAsync(ChunkedAudioBuffer buffer, Task producer, string label, CancellationToken token)
    {
        var procs = new FileProcedures
        {
            Close = _ => { },
            Length = _ => 0L,
            Read = (ptr, length, _) => buffer.FileRead(ptr, length),
            Seek = (offset, _) => buffer.FileSeek(offset),
        };

        // BASS pulls the header from the buffer here; it returns 0 if the format is unsupported.
        // Run without the token so a cancelled create cannot leak an orphaned native handle.
        int handle = await Task.Run(
            () => Bass.CreateStream(StreamSystem.Buffer, BassFlags.Default, procs, IntPtr.Zero))
            .ConfigureAwait(false);

        if (token.IsCancellationRequested || _isDisposed)
        {
            if (handle != 0) FreeStream(handle);
            return false;
        }

        if (handle == 0)
        {
            JukeboxLog.Warn($"BASS stream create failed ({Bass.LastError}) for: {label} — trying next tier.");
            return false;
        }

        await WaitForPrebufferAsync(buffer, label, token).ConfigureAwait(false);

        if (buffer.Fault != null || (buffer.BufferedBytes < PrebufferMinimumBytes && !buffer.IsCompleted))
        {
            JukeboxLog.Warn($"Not enough audio buffered for '{label}' ({buffer.BufferedBytes / 1024} KB) — trying next tier.");
            FreeStream(handle);
            return false;
        }

        lock (_gate)
        {
            if (token.IsCancellationRequested || _isDisposed)
            {
                FreeStream(handle);
                return false;
            }

            _stream = handle;
            _activeSource = new ActiveSource(buffer, producer, procs);

            float targetVol = _isMuted ? 0f : (float)_volume;
            Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, targetVol);
            Bass.ChannelSetSync(_stream, SyncFlags.End, 0, _endSyncProc, IntPtr.Zero);
            Bass.ChannelSetSync(_stream, SyncFlags.Stalled, 0, _stallSyncProc, IntPtr.Zero);

            if (!Bass.ChannelPlay(_stream))
            {
                Fail($"BASS playback start failed ({Bass.LastError}) for: {label}");
                FreeStream(_stream);
                _stream = 0;
                _activeSource = null;
                return false;
            }

            ChannelInfo info = Bass.ChannelGetInfo(_stream);
            JukeboxLog.Info(
                $"Playing: {label} ({info.Frequency} Hz, {info.Channels} ch, buffered={buffer.BufferedBytes / 1024} KB, vol={targetVol:F2} muted={_isMuted})");
            IsBuffering = false;
            IsPlaying = true;
        }

        return true;
    }

    /// <summary>
    /// Waits until enough audio is buffered before starting playback. Never starts on an empty
    /// buffer (the old fixed 6 s timeout was the direct cause of "plays half a second then stops"),
    /// but still proceeds after <see cref="PrebufferTimeoutMs"/> if at least the minimum arrived.
    /// </summary>
    private static async Task WaitForPrebufferAsync(ChunkedAudioBuffer buffer, string label, CancellationToken token)
    {
        JukeboxLog.Info($"Prebuffering '{label}'…");
        long deadline = Environment.TickCount64 + PrebufferTimeoutMs;

        while (!buffer.IsCompleted && buffer.Fault is null && buffer.BufferedBytes < PrebufferPreferredBytes)
        {
            if (Environment.TickCount64 >= deadline) break;
            await Task.Delay(50, token).ConfigureAwait(false);
        }

        JukeboxLog.Info($"Prebuffered {buffer.BufferedBytes / 1024} KB for '{label}' (completed={buffer.IsCompleted}).");
    }

    public void Pause()
    {
        int handle;
        lock (_gate)
        {
            if (_isDisposed || !IsPlaying || _stream == 0) return;
            handle = _stream;
            IsPlaying = false;
        }

        try
        {
            Bass.ChannelPause(handle);
            JukeboxLog.Info($"Paused: {CurrentLabel}");
        }
        catch (Exception ex)
        {
            JukeboxLog.Warn($"Pause failed: {ex.Message}");
        }
    }

    public void Resume()
    {
        int handle;
        lock (_gate)
        {
            if (_isDisposed || IsPlaying || _stream == 0) return;
            handle = _stream;
        }

        try
        {
            if (Bass.ChannelPlay(handle))
            {
                JukeboxLog.Info($"Resumed: {CurrentLabel}");
                IsPlaying = true;
            }
            else
            {
                Fail($"BASS resume failed ({Bass.LastError}) for: {CurrentLabel}");
            }
        }
        catch (Exception ex)
        {
            Fail($"Resume exception for '{CurrentLabel}': {ex.Message}");
        }
    }

    public void Stop()
    {
        int handle;
        ActiveSource? source;
        lock (_gate)
        {
            if (_isDisposed) return;
            handle = _stream;
            source = _activeSource;
            _stream = 0;
            _activeSource = null;
            _currentLabel = string.Empty;
            IsPlaying = false;
            IsBuffering = false;
        }

        FreeStream(handle);
        try { source?.Dispose(); } catch { }
        JukeboxLog.Info("Stopped.");
    }

    public void SetVolume(double volume)
    {
        double clamped = Math.Clamp(volume, 0.0, 1.0);
        int handle;
        lock (_gate)
        {
            if (Math.Abs(_volume - clamped) < 0.001) return;
            _volume = clamped;
            handle = _stream;
        }

        if (handle != 0 && !_isMuted)
        {
            Bass.ChannelSetAttribute(handle, ChannelAttribute.Volume, (float)clamped);
        }
    }

    public void SetMuted(bool isMuted)
    {
        int handle;
        lock (_gate)
        {
            if (_isMuted == isMuted) return;
            _isMuted = isMuted;
            handle = _stream;
        }

        if (handle != 0)
        {
            float targetVol = _isMuted ? 0f : (float)_volume;
            Bass.ChannelSetAttribute(handle, ChannelAttribute.Volume, targetVol);
        }
    }

    private void OnStallSync(int handle, int channel, int data, IntPtr user)
    {
        if (data != 0)
        {
            JukeboxLog.Info($"Stall cleared, resumed: '{CurrentLabel}'.");
            return;
        }

        bool storm = false;
        lock (_gate)
        {
            DateTime cutoff = DateTime.UtcNow.AddSeconds(-30);
            _stallTimes.RemoveAll(t => t < cutoff);
            _stallTimes.Add(DateTime.UtcNow);
            storm = _stallTimes.Count >= 4;
            if (storm)
            {
                _stallTimes.Clear();
            }
        }

        if (storm)
        {
            JukeboxLog.Warn($"STALL STORM on '{CurrentLabel}' (4+ underruns in 30 s) — pipe narrower than bitrate.");
            StallStormDetected?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            JukeboxLog.Warn($"STALL: buffer underrun on '{CurrentLabel}' (download slower than playback).");
        }
    }

    private void OnEndSync(int handle, int channel, int data, IntPtr user)
    {
        bool ended;
        bool natural;
        string label;

        lock (_gate)
        {
            ended = _stream != 0 && channel == _stream;
            label = _currentLabel;
            natural = ended && _activeSource?.Buffer.IsCompleted == true && _activeSource.Buffer.Fault is null;

            if (ended)
            {
                _stream = 0;
                _activeSource = null;
                _isPlaying = false;
                _isBuffering = false;
            }
        }

        if (!ended) return;

        // Free the native stream off the sync-callback thread (never free from within a callback).
        int endedHandle = channel;
        _ = Task.Run(() => FreeStream(endedHandle));

        if (natural)
        {
            JukeboxLog.Info($"Track ended: {label}");
            PlaybackStateChanged?.Invoke(this, false);
            EndOfStreamReached?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            // The stream ended before the download finished (stall gave up, format issue, etc.).
            // Do NOT report a natural end, otherwise the widget would autoplay on every failure.
            JukeboxLog.Warn($"Track interrupted (stream ended before download completed): {label}");
            PlaybackStateChanged?.Invoke(this, false);
        }
    }

    private void Fail(string message)
    {
        JukeboxLog.Error(message);
        lock (_gate)
        {
            LastErrorMessage = message;
            IsPlaying = false;
            IsBuffering = false;
        }
        ErrorOccurred?.Invoke(this, message);
    }

    private static void FreeStream(int handle)
    {
        if (handle == 0) return;
        try
        {
            Bass.ChannelStop(handle);
            Bass.StreamFree(handle);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[JukeboxAudioService] Free failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _switchCts?.Cancel();
            _switchCts = null;
        }
    }

    /// <summary>Roots the BASS file callbacks and the backing buffer for one stream's lifetime.</summary>
    private sealed class ActiveSource
    {
        public ChunkedAudioBuffer Buffer { get; }
        public Task Producer { get; }
        public FileProcedures Procs { get; }

        public ActiveSource(ChunkedAudioBuffer buffer, Task producer, FileProcedures procs)
        {
            Buffer = buffer;
            Producer = producer;
            Procs = procs;
        }

        public void Dispose()
        {
            try { Buffer.Dispose(); } catch { }
        }
    }
}
