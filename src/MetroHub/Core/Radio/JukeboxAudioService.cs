using System;
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
public sealed class JukeboxAudioService : IJukeboxAudioService
{
    public static JukeboxAudioService Instance { get; } = new();

    private readonly object _gate = new();
    private int _stream;
    private CancellationTokenSource? _switchCts;
    private Task _currentPlayTask = Task.CompletedTask;
    private object? _activePush; // Roots push-stream delegates while their stream lives.
    private readonly SyncProcedure _endSyncProc;
    private readonly SyncProcedure _stallSyncProc;
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

        Bass.NetAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36 MetroHub/1.0";
        Bass.Configure(Configuration.NetBufferLength, 5000);
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
    }

    /// <summary>
    /// Resolves the full absolute path for a BASS plugin DLL and loads it.
    /// Bass.PluginLoad uses native LoadLibrary which only searches the process working directory,
    /// NOT AppContext.BaseDirectory. This helper tries all known candidate paths.
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
                        JukeboxLog.Info($"Loaded {fileName} from: {path} (handle={handle}).");
                        return handle;
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

    public async Task PlayUrlAsync(JukeboxAudioUrls urls, string label, CancellationToken ct = default)
    {
        if (urls is null || (string.IsNullOrWhiteSpace(urls.OpusUrl) && string.IsNullOrWhiteSpace(urls.AacUrl)))
        {
            Fail($"No playable URL resolved for: {label}");
            return;
        }

        CancellationTokenSource linkedCts;
        Task previousTask;
        CancellationTokenSource? previousCts;
        Task mine;
        lock (_gate)
        {
            if (_isDisposed) return;
            // Never dispose a CTS another task may still be touching: cancel the previous
            // attempt, wait for it below, and only then dispose it.
            previousCts = _switchCts;
            previousTask = _currentPlayTask;
            _switchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts = _switchCts;
            mine = _currentPlayTask = RunPlayAsync(urls, label, linkedCts);
        }

        try
        {
            previousCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        try
        {
            await previousTask.ConfigureAwait(false);
        }
        catch
        {
        }
        try
        {
            previousCts?.Dispose();
        }
        catch
        {
        }

        await mine.ConfigureAwait(false);
    }

    private async Task RunPlayAsync(JukeboxAudioUrls urls, string label, CancellationTokenSource linkedCts)
    {
        try
        {
            await Task.Delay(120, linkedCts.Token).ConfigureAwait(false);

            int previous;
            lock (_gate)
            {
                if (linkedCts.Token.IsCancellationRequested || _isDisposed) return;
                previous = _stream;
                _stream = 0;
                IsPlaying = false;
                IsBuffering = true;
                LastErrorMessage = null;
                CurrentLabel = label;
            }
            FreeStream(previous);
            lock (_gate) { _activePush = null; } // Previous stream (and its delegates) gone.

            int handle = 0;
            object? root = null;
            CancellationToken token = linkedCts.Token;

            // 1. Push AAC first: our client downloads instantly and MP4 demux lives
            //    inside bass_aac — highest-confidence path.
            if (!string.IsNullOrWhiteSpace(urls.AacUrl) && !token.IsCancellationRequested && !_isDisposed)
            {
                JukeboxLog.Info($"Attempt push-aac: {label}");
                (handle, root) = await TryPushPlayAsync(urls.AacUrl, label, token).ConfigureAwait(false);
                if (token.IsCancellationRequested || _isDisposed)
                {
                    if (handle != 0) Bass.StreamFree(handle);
                    return;
                }
            }

            // 2. Push Opus: needs the webm demux chain (basswebm + bassopus).
            if (handle == 0 && !string.IsNullOrWhiteSpace(urls.OpusUrl) && !token.IsCancellationRequested && !_isDisposed)
            {
                JukeboxLog.Info($"Attempt push-opus: {label}");
                (handle, root) = await TryPushPlayAsync(urls.OpusUrl, label, token).ConfigureAwait(false);
                if (token.IsCancellationRequested || _isDisposed)
                {
                    if (handle != 0) Bass.StreamFree(handle);
                    return;
                }
            }

            // 3-4. Direct URL opens last: BASS's own downloader stalls 30-90 s on
            // throttled googlevideo responses before failing.
            if (handle == 0 && !string.IsNullOrWhiteSpace(urls.OpusUrl) && !token.IsCancellationRequested && !_isDisposed)
            {
                JukeboxLog.Info($"Attempt direct-opus: {label} | {urls.OpusUrl[..Math.Min(100, urls.OpusUrl.Length)]}…");
                handle = await OpenUrlAsync(urls.OpusUrl, token).ConfigureAwait(false);
            }
            if (handle == 0 && !string.IsNullOrWhiteSpace(urls.AacUrl) && !token.IsCancellationRequested && !_isDisposed)
            {
                JukeboxLog.Info($"Attempt direct-aac: {label} | {urls.AacUrl[..Math.Min(100, urls.AacUrl.Length)]}…");
                handle = await OpenUrlAsync(urls.AacUrl, token).ConfigureAwait(false);
            }

            if (token.IsCancellationRequested || _isDisposed)
            {
                if (handle != 0) Bass.StreamFree(handle);
                return;
            }
            if (handle == 0)
            {
                Fail($"BASS could not play this stream ({Bass.LastError}) for: {label}. See aacHandle/opusHandle/webmHandle at startup.");
                return;
            }

            lock (_gate)
            {
                if (linkedCts.Token.IsCancellationRequested || _isDisposed)
                {
                    Bass.StreamFree(handle);
                    return;
                }

                _stream = handle;
                _activePush = root; // Roots push delegates; null on the direct path.
                float targetVol = _isMuted ? 0f : (float)_volume;
                Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, targetVol);
                Bass.ChannelSetSync(_stream, SyncFlags.End, 0, _endSyncProc, IntPtr.Zero);
                Bass.ChannelSetSync(_stream, SyncFlags.Stalled, 0, _stallSyncProc, IntPtr.Zero);

                if (Bass.ChannelPlay(_stream))
                {
                    ChannelInfo info = Bass.ChannelGetInfo(_stream);
                    JukeboxLog.Info($"Playing: {label} ({info.Frequency} Hz, {info.Channels} ch, vol={targetVol:F2} muted={_isMuted})");
                    IsBuffering = false;
                    IsPlaying = true;
                }
                else
                {
                    Fail($"BASS play failed ({Bass.LastError}) for: {label}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer tap; expected.
        }
        catch (ObjectDisposedException)
        {
            // Defensive: a disposed CTS means superseded — same as cancelled.
        }
        catch (Exception ex)
        {
            Fail($"Playback exception for '{label}': {ex.Message}");
        }
    }

    private static Task<int> OpenUrlAsync(string url, CancellationToken ct)
    {
        return Task.Run(() =>
            Bass.CreateStream(url, 0, BassFlags.StreamDownloadBlocks | BassFlags.AutoFree, null, IntPtr.Zero), ct);
    }

    /// <summary>Roots push-stream delegates + network resources for one pump lifetime.</summary>
    private sealed class PushState : IDisposable
    {
        public byte[] Initial = Array.Empty<byte>();
        public int InitialLength;
        public int InitialOffset;
        public readonly object Gate = new();
        public System.Net.Http.HttpResponseMessage? Response;
        public System.IO.Stream? NetStream;

        public int ReadInitial(IntPtr buffer, int length)
        {
            lock (Gate)
            {
                int available = Math.Max(0, InitialLength - InitialOffset);
                int take = Math.Min(available, length);
                if (take > 0)
                {
                    System.Runtime.InteropServices.Marshal.Copy(Initial, InitialOffset, buffer, take);
                    InitialOffset += take;
                }
                return take;
            }
        }

        public void Dispose()
        {
            try { NetStream?.Dispose(); } catch { }
            try { Response?.Dispose(); } catch { }
        }
    }

    private static readonly System.Net.Http.HttpClient _dlClient =
        new(new System.Net.Http.HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };

    /// <summary>
    /// Last-resort open: download through our own HttpClient (proven instant by Probe lines)
    /// and push the bytes into BASS. Returns (0, null) on failure.
    /// </summary>
    private async Task<(int Handle, object? Root)> TryPushPlayAsync(string url, string label, CancellationToken ct)
    {
        PushState? push = null;
        try
        {
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
            var response = await _dlClient.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                JukeboxLog.Warn($"Push download rejected: {(int)response.StatusCode} {response.StatusCode} for: {label}");
                response.Dispose();
                return (0, null);
            }

            var netStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            byte[] initial = new byte[64 * 1024];
            int filled = 0;
            while (filled < initial.Length)
            {
                int read = await netStream.ReadAsync(initial.AsMemory(filled, initial.Length - filled), ct).ConfigureAwait(false);
                if (read == 0) break;
                filled += read;
            }
            if (filled == 0)
            {
                JukeboxLog.Warn($"Push download empty for: {label}");
                netStream.Dispose();
                response.Dispose();
                return (0, null);
            }

            push = new PushState { Initial = initial, InitialLength = filled, NetStream = netStream, Response = response };
            var procs = new FileProcedures
            {
                Close = _ => { },
                Length = _ => 0L,
                Read = (buffer, length, _) => push.ReadInitial(buffer, length),
                Seek = (_, __) => false,
            };

            int handle = Bass.CreateStream(StreamSystem.BufferPush, BassFlags.Default, procs, IntPtr.Zero);
            if (handle == 0)
            {
                JukeboxLog.Error($"Push stream create failed ({Bass.LastError}) for: {label}");
                push.Dispose();
                return (0, null);
            }

            JukeboxLog.Info($"Push stream created, pumping: {label}");
            _ = PumpPushAsync(handle, push, procs, ct);
            return (handle, procs);
        }
        catch (Exception ex)
        {
            JukeboxLog.Warn($"Push download failed for '{label}': {ex.Message}");
            push?.Dispose();
            return (0, null);
        }
    }

    private static async Task PumpPushAsync(int handle, PushState push, object root, CancellationToken ct)
    {
        GC.KeepAlive(root); // Root delegates at creation; kept alive via _activePush — see assignment.
        byte[] tmp = new byte[16 * 1024];
        try
        {
            while (true)
            {
                int read = await push.NetStream!.ReadAsync(tmp.AsMemory(0, tmp.Length), ct).ConfigureAwait(false);
                if (read == 0) break;
                int offset = 0;
                while (offset < read)
                {
                    ct.ThrowIfCancellationRequested();
                    int chunk = Math.Min(16 * 1024, read - offset);
                    byte[] slice = new byte[chunk];
                    Buffer.BlockCopy(tmp, offset, slice, 0, chunk);
                    int put = Bass.StreamPutFileData(handle, slice, chunk);
                    if (put < 0)
                    {
                        await Task.Delay(150, ct).ConfigureAwait(false);
                        continue;
                    }
                    if (put == 0)
                    {
                        await Task.Delay(50, ct).ConfigureAwait(false);
                        continue;
                    }
                    offset += put;
                }
            }
            Bass.StreamPutFileData(handle, Array.Empty<byte>(), (int)StreamProcedureType.End);
            JukeboxLog.Info("Push download complete (EOF signalled).");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            JukeboxLog.Warn($"Push pump ended: {ex.Message}");
        }
        finally
        {
            push.Dispose();
        }
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
            Bass.ChannelPause(handle); // Position kept — Resume continues the song.
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
        lock (_gate)
        {
            if (_isDisposed) return;
            handle = _stream;
            _stream = 0;
            _currentLabel = string.Empty;
            IsPlaying = false;
            IsBuffering = false;
        }
        FreeStream(handle);
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
        // data == 0: playback stalled (pump slower than playback — silence);
        // data == 1: buffered enough, resumed.
        if (data == 0)
        {
            JukeboxLog.Warn($"STALL: buffer underrun on '{CurrentLabel}' (download slower than playback).");
        }
        else
        {
            JukeboxLog.Info($"Stall cleared, resumed: '{CurrentLabel}'.");
        }
    }

    private void OnEndSync(int handle, int channel, int data, IntPtr user)    {
        bool ended;
        lock (_gate)
        {
            // Pause() keeps _stream, Stop() zeroes it: a match here is a natural end.
            ended = _stream != 0 && channel == _stream;
            if (ended)
            {
                _stream = 0;
                _isPlaying = false;
                _isBuffering = false;
            }
        }

        if (ended)
        {
            JukeboxLog.Info($"Track ended: {CurrentLabel}");
            PlaybackStateChanged?.Invoke(this, false);
            EndOfStreamReached?.Invoke(this, EventArgs.Empty);
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
            // Cancel only: disposing here would race in-flight attempts still holding
            // the token (ObjectDisposedException). The CTS is GC-collected.
            _switchCts?.Cancel();
            _switchCts = null;
        }
        // NOTE: no Bass.Free() — the output device is shared with the radio service.
    }
}
