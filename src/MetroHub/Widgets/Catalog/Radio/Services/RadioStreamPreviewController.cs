using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ManagedBass;
using MetroHub.Core.Services;

namespace MetroHub.Widgets.Catalog.Radio.Services;

/// <summary>
/// Controls fast, lightweight audio streaming preview for radio search results.
/// Operates isolated from the main radio player but synchronizes volume and mute states.
/// </summary>
public sealed class RadioStreamPreviewController : IDisposable
{
    private readonly Lock _gate = new();
    private readonly SyncProcedure _endSyncProc;

    private int _currentStream;
    private RadioSearchResultItem? _activeItem;
    private long _version;
    private CancellationTokenSource? _cts;
    private bool _isDisposed;

    /// <summary>
    /// Raised when preview stream connection or playback fails.
    /// </summary>
    public event EventHandler<string>? PreviewError;

    public RadioSearchResultItem? ActiveItem
    {
        get
        {
            lock (_gate) return _activeItem;
        }
    }

    public bool IsPlaying
    {
        get
        {
            lock (_gate) return _activeItem is { IsPlaying: true };
        }
    }

    public bool IsBuffering
    {
        get
        {
            lock (_gate) return _activeItem is { IsBuffering: true };
        }
    }

    public RadioStreamPreviewController()
    {
        // Retain delegate instance in private readonly field to prevent native GC collection
        _endSyncProc = OnStreamEnd;

        // Ensure RadioAudioService instance and underlying BASS subsystem are loaded
        RadioAudioService.Instance.VolumeChanged += OnVolumeChanged;
        RadioAudioService.Instance.MuteStateChanged += OnMuteStateChanged;
    }

    /// <summary>
    /// Toggles preview playback for the specified search result item.
    /// If the item is already playing or buffering, stops preview.
    /// Otherwise, stops any active stream and begins buffering and streaming the new item.
    /// </summary>
    public void TogglePlayPause(RadioSearchResultItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        bool shouldStop;
        lock (_gate)
        {
            shouldStop = IsSameStation(_activeItem, item) && (_currentStream != 0 || _cts != null);
        }

        if (shouldStop)
        {
            Stop();
            SetItemState(item, isPlaying: false, isBuffering: false);
            return;
        }

        Play(item);
    }

    /// <summary>
    /// Plays stream preview for the given search item.
    /// </summary>
    public void Play(RadioSearchResultItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (string.IsNullOrWhiteSpace(item.ResolvedStreamUrl))
        {
            PreviewError?.Invoke(this, "Preview unavailable (stream URL is empty)");
            return;
        }

        // Guarantee Bass.Init and codec plugins are initialized, and pause any main playback
        try
        {
            RadioAudioService.Instance.Pause();
        }
        catch (Exception ex)
        {
            Safe.Ignore(ex, "RadioStreamPreviewController.PauseMainRadio");
        }

        RadioSearchResultItem? previousItem;
        int oldStream;
        long myVersion;
        CancellationTokenSource cts;

        lock (_gate)
        {
            if (_isDisposed) return;

            _version++;
            myVersion = _version;

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            cts = _cts;

            previousItem = _activeItem;
            _activeItem = item;
            oldStream = _currentStream;
            _currentStream = 0;
        }

        if (previousItem != null && !ReferenceEquals(previousItem, item))
        {
            SetItemState(previousItem, isPlaying: false, isBuffering: false);
        }

        if (oldStream != 0)
        {
            FadeAndFreeStream(oldStream);
        }

        SetItemState(item, isPlaying: false, isBuffering: true);

        string url = item.ResolvedStreamUrl;
        CancellationToken token = cts.Token;

        Task.Run(async () =>
        {
            int stream = 0;
            try
            {
                var connectTask = Task.Run(() =>
                {
                    if (token.IsCancellationRequested) return 0;

                    int s = Bass.CreateStream(
                        url,
                        0,
                        BassFlags.StreamDownloadBlocks,
                        null,
                        IntPtr.Zero);

                    bool isStale;
                    lock (_gate)
                    {
                        isStale = _version != myVersion || token.IsCancellationRequested || _isDisposed;
                    }

                    if (isStale && s != 0)
                    {
                        try { Bass.StreamFree(s); } catch (Exception ex) { Safe.Ignore(ex, "RadioStreamPreviewController.StaleStreamFree"); }
                        return 0;
                    }

                    return s;
                }, token);

                stream = await connectTask.WaitAsync(TimeSpan.FromSeconds(7), token).ConfigureAwait(false);

                if (stream == 0)
                {
                    bool isCancelled;
                    lock (_gate)
                    {
                        isCancelled = _version != myVersion || token.IsCancellationRequested || _isDisposed;
                        if (!isCancelled && ReferenceEquals(_activeItem, item))
                        {
                            _activeItem = null;
                        }
                    }

                    if (!isCancelled)
                    {
                        SetItemState(item, isPlaying: false, isBuffering: false);
                        PreviewError?.Invoke(this, "Preview unavailable (connection failed)");
                    }
                    return;
                }

                lock (_gate)
                {
                    if (_version != myVersion || token.IsCancellationRequested || _isDisposed)
                    {
                        try { Bass.StreamFree(stream); } catch (Exception ex) { Safe.Ignore(ex, "RadioStreamPreviewController.DiscardStreamFree"); }
                        return;
                    }

                    _currentStream = stream;
                }

                // Register SyncFlags.End to reset playing state if stream drops
                Bass.ChannelSetSync(stream, SyncFlags.End, 0, _endSyncProc, IntPtr.Zero);

                // Smooth micro-fade in
                float targetVol = RadioAudioService.Instance.IsMuted ? 0f : (float)RadioAudioService.Instance.Volume;
                Bass.ChannelSetAttribute(stream, ChannelAttribute.Volume, 0f);
                Bass.ChannelPlay(stream);
                Bass.ChannelSlideAttribute(stream, ChannelAttribute.Volume, targetVol, 180);

                SetItemState(item, isPlaying: true, isBuffering: false);
            }
            catch (OperationCanceledException)
            {
                if (stream != 0)
                {
                    try { Bass.StreamFree(stream); } catch (Exception ex) { Safe.Ignore(ex, "RadioStreamPreviewController.CancelStreamFree"); }
                }
            }
            catch (TimeoutException)
            {
                lock (_gate)
                {
                    if (_version == myVersion)
                    {
                        cts.Cancel();
                        if (ReferenceEquals(_activeItem, item))
                        {
                            _activeItem = null;
                        }
                    }
                }

                if (stream != 0)
                {
                    try { Bass.StreamFree(stream); } catch (Exception ex) { Safe.Ignore(ex, "RadioStreamPreviewController.TimeoutStreamFree"); }
                }

                SetItemState(item, isPlaying: false, isBuffering: false);
                PreviewError?.Invoke(this, "Preview unavailable (connection timed out)");
            }
            catch (Exception ex)
            {
                Safe.LogThrottled(ex, "RadioStreamPreviewController.PlayException");
                if (stream != 0)
                {
                    try { Bass.StreamFree(stream); } catch (Exception sEx) { Safe.Ignore(sEx, "RadioStreamPreviewController.ExceptionStreamFree"); }
                }

                bool isCancelled;
                lock (_gate)
                {
                    isCancelled = _version != myVersion || token.IsCancellationRequested || _isDisposed;
                    if (!isCancelled && ReferenceEquals(_activeItem, item))
                    {
                        _activeItem = null;
                    }
                }

                if (!isCancelled)
                {
                    SetItemState(item, isPlaying: false, isBuffering: false);
                    PreviewError?.Invoke(this, "Preview unavailable (connection failed)");
                }
            }
        }).FireAndForget("RadioStreamPreviewController.Play");
    }

    /// <summary>
    /// Stops any active stream preview immediately and resets state.
    /// </summary>
    public void Stop()
    {
        int streamToStop;
        RadioSearchResultItem? itemToReset;

        lock (_gate)
        {
            _version++;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            streamToStop = _currentStream;
            _currentStream = 0;

            itemToReset = _activeItem;
            _activeItem = null;
        }

        if (itemToReset != null)
        {
            SetItemState(itemToReset, isPlaying: false, isBuffering: false);
        }

        if (streamToStop != 0)
        {
            FadeAndFreeStream(streamToStop);
        }
    }

    private void OnStreamEnd(int handle, int channel, int data, IntPtr user)
    {
        RadioSearchResultItem? item;
        lock (_gate)
        {
            if (_currentStream == channel)
            {
                _currentStream = 0;
                item = _activeItem;
                _activeItem = null;
            }
            else
            {
                item = null;
            }
        }

        if (item != null)
        {
            SetItemState(item, isPlaying: false, isBuffering: false);
        }
    }

    private void OnVolumeChanged(object? sender, double volume)
    {
        int stream;
        lock (_gate)
        {
            stream = _currentStream;
        }

        if (stream != 0)
        {
            float targetVol = RadioAudioService.Instance.IsMuted ? 0f : (float)volume;
            Bass.ChannelSetAttribute(stream, ChannelAttribute.Volume, targetVol);
        }
    }

    private void OnMuteStateChanged(object? sender, bool isMuted)
    {
        int stream;
        lock (_gate)
        {
            stream = _currentStream;
        }

        if (stream != 0)
        {
            float targetVol = isMuted ? 0f : (float)RadioAudioService.Instance.Volume;
            Bass.ChannelSetAttribute(stream, ChannelAttribute.Volume, targetVol);
        }
    }

    private static void FadeAndFreeStream(int streamHandle)
    {
        if (streamHandle == 0) return;
        Task.Run(async () =>
        {
            try
            {
                Bass.ChannelSlideAttribute(streamHandle, ChannelAttribute.Volume, 0f, 120);
                await Task.Delay(150).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Safe.Ignore(ex, "RadioStreamPreviewController.ChannelSlide");
            }
            finally
            {
                try
                {
                    Bass.ChannelStop(streamHandle);
                    Bass.StreamFree(streamHandle);
                }
                catch (Exception ex)
                {
                    Safe.Ignore(ex, "RadioStreamPreviewController.StreamFree");
                }
            }
        }).FireAndForget("RadioStreamPreviewController.FadeAndFreeStream");
    }

    private static void SetItemState(RadioSearchResultItem? item, bool isPlaying, bool isBuffering)
    {
        if (item == null) return;
        var app = Application.Current;
        if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.InvokeAsync(() =>
            {
                item.IsPlaying = isPlaying;
                item.IsBuffering = isBuffering;
            }).FireAndForget("RadioStreamPreviewController.SetItemState");
        }
        else
        {
            item.IsPlaying = isPlaying;
            item.IsBuffering = isBuffering;
        }
    }

    private static bool IsSameStation(RadioSearchResultItem? a, RadioSearchResultItem? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        if (!string.IsNullOrEmpty(a.StationUuid) && string.Equals(a.StationUuid, b.StationUuid, StringComparison.OrdinalIgnoreCase)) return true;
        return !string.IsNullOrEmpty(a.ResolvedStreamUrl) && string.Equals(a.ResolvedStreamUrl, b.ResolvedStreamUrl, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_isDisposed) return;
            _isDisposed = true;
        }

        RadioAudioService.Instance.VolumeChanged -= OnVolumeChanged;
        RadioAudioService.Instance.MuteStateChanged -= OnMuteStateChanged;

        Stop();
    }
}
