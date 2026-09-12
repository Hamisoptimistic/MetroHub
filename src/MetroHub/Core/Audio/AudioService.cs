using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MetroHub.Core.Audio.Interop;

namespace MetroHub.Core.Audio;

public class AudioService : IDisposable
{
    private static AudioService? _instance;
    private static readonly object _instanceLock = new();

    public static AudioService Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_instanceLock)
                {
                    _instance ??= new AudioService();
                }
            }
            return _instance;
        }
    }

    private IMMDeviceEnumerator? _enumerator;
    private IMMDevice? _defaultRenderDevice;
    private IAudioEndpointVolume? _endpointVolume;
    private IAudioSessionManager2? _sessionManager;

    private readonly EndpointNotificationCallback _endpointListener;
    private readonly EndpointVolumeCallback _volumeListener;
    private readonly AudioSessionNotificationListener _sessionListener;

    private Guid _emptyGuid = Guid.Empty;
    private readonly ConcurrentDictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    public event Action? DefaultDeviceChanged;
    public event Action? DeviceListChanged;
    public event Action<float, bool>? MasterVolumeChanged;
    public event Action? SessionsChanged;

    public AudioService()
    {
        _endpointListener = new EndpointNotificationCallback();
        _volumeListener = new EndpointVolumeCallback();
        _sessionListener = new AudioSessionNotificationListener();

        _endpointListener.DefaultDeviceChanged += OnDefaultDeviceChanged;
        _endpointListener.DeviceListChanged += () => DeviceListChanged?.Invoke();
        _volumeListener.VolumeChanged += (vol, mute) => MasterVolumeChanged?.Invoke(vol, mute);
        _sessionListener.SessionCreated += () => SessionsChanged?.Invoke();

        InitializeCoreAudio();
    }

    private void InitializeCoreAudio()
    {
        try
        {
            var comObj = new MMDeviceEnumeratorComObject();
            _enumerator = (IMMDeviceEnumerator)comObj;
            _enumerator.RegisterEndpointNotificationCallback(_endpointListener);

            BindToDefaultDevice();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioService] Initialization failed: {ex.Message}");
        }
    }

    private readonly object _deviceLock = new();

    private void BindToDefaultDevice()
    {
        lock (_deviceLock)
        {
            if (_enumerator == null) return;

            // Clean up previous endpoint-specific objects
            if (_endpointVolume != null)
            {
                try { _endpointVolume.UnregisterControlChangeNotify(_volumeListener); } catch { }
                SafeRelease(ref _endpointVolume);
            }

            if (_sessionManager != null)
            {
                try { _sessionManager.UnregisterSessionNotification(_sessionListener); } catch { }
                SafeRelease(ref _sessionManager);
            }

            SafeRelease(ref _defaultRenderDevice);

            int hr = _enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out _defaultRenderDevice);
            if (hr != 0 || _defaultRenderDevice == null)
            {
                // Fallback to console role
                hr = _enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out _defaultRenderDevice);
            }

            if (hr == 0 && _defaultRenderDevice != null)
            {
                // 1. Activate IAudioEndpointVolume
                var iidVol = typeof(IAudioEndpointVolume).GUID;
                hr = _defaultRenderDevice.Activate(ref iidVol, CLSCTX.INPROC_SERVER, IntPtr.Zero, out var volObj);
                if (hr == 0 && volObj is IAudioEndpointVolume endpointVol)
                {
                    _endpointVolume = endpointVol;
                    _endpointVolume.RegisterControlChangeNotify(_volumeListener);
                }

                // 2. Activate IAudioSessionManager2
                var iidSession = typeof(IAudioSessionManager2).GUID;
                hr = _defaultRenderDevice.Activate(ref iidSession, CLSCTX.INPROC_SERVER, IntPtr.Zero, out var sessionObj);
                if (hr == 0 && sessionObj is IAudioSessionManager2 sessionMgr)
                {
                    _sessionManager = sessionMgr;
                    _sessionManager.RegisterSessionNotification(_sessionListener);
                }
            }
        }
    }

    private void OnDefaultDeviceChanged()
    {
        BindToDefaultDevice();
        DefaultDeviceChanged?.Invoke();
        SessionsChanged?.Invoke();
    }

    #region Master Volume Control

    public float GetMasterVolume()
    {
        if (_endpointVolume == null) return 0f;
        try
        {
            int hr = _endpointVolume.GetMasterVolumeLevelScalar(out float level);
            return hr == 0 ? level : 0f;
        }
        catch
        {
            return 0f;
        }
    }

    public void SetMasterVolume(float level)
    {
        if (_endpointVolume == null) return;
        try
        {
            float clamped = Math.Clamp(level, 0f, 1f);
            _endpointVolume.SetMasterVolumeLevelScalar(clamped, ref _emptyGuid);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioService] SetMasterVolume failed: {ex.Message}");
        }
    }

    public bool GetMasterMute()
    {
        if (_endpointVolume == null) return false;
        try
        {
            int hr = _endpointVolume.GetMute(out bool isMuted);
            return hr == 0 && isMuted;
        }
        catch
        {
            return false;
        }
    }

    public void SetMasterMute(bool mute)
    {
        if (_endpointVolume == null) return;
        try
        {
            _endpointVolume.SetMute(mute, ref _emptyGuid);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioService] SetMasterMute failed: {ex.Message}");
        }
    }

    #endregion

    #region Device Enumeration & Switching

    public List<AudioDeviceModel> GetPlaybackDevices()
    {
        var devices = new List<AudioDeviceModel>();
        if (_enumerator == null) return devices;

        string currentDefaultId = string.Empty;
        IMMDevice? defaultDev = null;
        try
        {
            if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out defaultDev) == 0 && defaultDev != null)
            {
                defaultDev.GetId(out currentDefaultId);
            }
        }
        catch { }
        finally
        {
            SafeRelease(ref defaultDev);
        }

        IMMDeviceCollection? collection = null;
        try
        {
            int hr = _enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.Active, out collection);
            if (hr != 0 || collection == null) return devices;

            collection.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                IMMDevice? device = null;
                IPropertyStore? propStore = null;
                try
                {
                    hr = collection.Item(i, out device);
                    if (hr != 0 || device == null) continue;

                    device.GetId(out string id);

                    string friendlyName = "Audio Endpoint";
                    hr = device.OpenPropertyStore(StorageAccessMode.Read, out propStore);
                    if (hr == 0 && propStore != null)
                    {
                        var key = CoreAudioConstants.PKEY_Device_FriendlyName;
                        if (propStore.GetValue(ref key, out var pv) == 0 && pv.pwszVal != IntPtr.Zero)
                        {
                            friendlyName = Marshal.PtrToStringUni(pv.pwszVal) ?? friendlyName;
                        }
                    }

                    string iconGlyph = ResolveDeviceIconGlyph(friendlyName);
                    bool isDefault = !string.IsNullOrEmpty(currentDefaultId) &&
                                     string.Equals(id, currentDefaultId, StringComparison.OrdinalIgnoreCase);

                    devices.Add(new AudioDeviceModel
                    {
                        Id = id,
                        Name = friendlyName,
                        IconGlyph = iconGlyph,
                        IsDefault = isDefault
                    });
                }
                finally
                {
                    SafeRelease(ref propStore);
                    SafeRelease(ref device);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioService] GetPlaybackDevices failed: {ex.Message}");
        }
        finally
        {
            SafeRelease(ref collection);
        }

        return devices;
    }

    private static string ResolveDeviceIconGlyph(string deviceName)
    {
        string lower = deviceName.ToLowerInvariant();
        if (lower.Contains("headphone") || lower.Contains("earphone") || lower.Contains("headset"))
        {
            return "\uE7F6"; // Headphones glyph
        }
        if (lower.Contains("tv") || lower.Contains("monitor") || lower.Contains("display") || lower.Contains("hdmi"))
        {
            return "\uE7F4"; // Screen/TV glyph
        }
        return "\uE7F5"; // Default Speaker glyph
    }

    public bool SetDefaultPlaybackDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;

        try
        {
            var client = new PolicyConfigClient();
            if (client is IPolicyConfigVista policyConfig)
            {
                int hrConsole = policyConfig.SetDefaultEndpoint(deviceId, ERole.eConsole);
                int hrMulti = policyConfig.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
                int hrComm = policyConfig.SetDefaultEndpoint(deviceId, ERole.eCommunications);
                Marshal.ReleaseComObject(policyConfig);
                return hrConsole == 0 || hrMulti == 0 || hrComm == 0;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioService] SetDefaultPlaybackDevice failed: {ex.Message}");
        }

        return false;
    }

    #endregion

    #region Per-App Volume Mixer

    public List<AppAudioSessionModel> GetAppSessions()
    {
        var sessions = new List<AppAudioSessionModel>();
        if (_sessionManager == null) return sessions;

        IAudioSessionEnumerator? sessionEnum = null;
        try
        {
            int hr = _sessionManager.GetSessionEnumerator(out sessionEnum);
            if (hr != 0 || sessionEnum == null) return sessions;

            sessionEnum.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl? control = null;
                try
                {
                    hr = sessionEnum.GetSession(i, out control);
                    if (hr != 0 || control == null) continue;

                    if (control is not IAudioSessionControl2 control2) continue;

                    control2.GetState(out var state);
                    if (state == AudioSessionState.Expired) continue;

                    bool isSystemSounds = control2.IsSystemSoundsSession() == 0;
                    control2.GetProcessId(out uint pid);

                    if (!isSystemSounds && pid == 0) continue;

                    string processName = "System Sounds";
                    string displayName = "System Sounds";
                    ImageSource? iconSource = null;

                    if (!isSystemSounds)
                    {
                        try
                        {
                            using var proc = Process.GetProcessById((int)pid);
                            processName = proc.ProcessName;
                            displayName = !string.IsNullOrWhiteSpace(proc.MainWindowTitle)
                                ? proc.MainWindowTitle
                                : processName;

                            string? exePath = proc.MainModule?.FileName;
                            if (!string.IsNullOrEmpty(exePath))
                            {
                                iconSource = GetOrLoadProcessIcon(exePath);
                            }
                        }
                        catch
                        {
                            // Process might have terminated or access denied
                            if (string.IsNullOrEmpty(processName) || processName == "System Sounds")
                            {
                                processName = $"App (PID: {pid})";
                                displayName = processName;
                            }
                        }
                    }

                    float volume = 1.0f;
                    bool isMuted = false;

                    if (control is ISimpleAudioVolume simpleVolume)
                    {
                        simpleVolume.GetMasterVolume(out volume);
                        simpleVolume.GetMute(out isMuted);
                    }

                    sessions.Add(new AppAudioSessionModel
                    {
                        ProcessId = pid,
                        ProcessName = processName,
                        DisplayName = displayName,
                        IconSource = iconSource,
                        Volume = volume,
                        IsMuted = isMuted,
                        IsSystemSounds = isSystemSounds
                    });
                }
                finally
                {
                    SafeRelease(ref control);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioService] GetAppSessions failed: {ex.Message}");
        }
        finally
        {
            SafeRelease(ref sessionEnum);
        }

        return sessions;
    }

    public void SetAppVolume(uint pid, float volume)
    {
        if (_sessionManager == null) return;

        IAudioSessionEnumerator? sessionEnum = null;
        try
        {
            int hr = _sessionManager.GetSessionEnumerator(out sessionEnum);
            if (hr != 0 || sessionEnum == null) return;

            sessionEnum.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl? control = null;
                try
                {
                    hr = sessionEnum.GetSession(i, out control);
                    if (hr != 0 || control == null) continue;

                    if (control is IAudioSessionControl2 control2)
                    {
                        control2.GetProcessId(out uint curPid);
                        bool isSys = control2.IsSystemSoundsSession() == 0;

                        if ((isSys && pid == 0) || curPid == pid)
                        {
                            if (control is ISimpleAudioVolume simpleVolume)
                            {
                                float clamped = Math.Clamp(volume, 0f, 1f);
                                simpleVolume.SetMasterVolume(clamped, ref _emptyGuid);
                                return;
                            }
                        }
                    }
                }
                finally
                {
                    SafeRelease(ref control);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioService] SetAppVolume failed: {ex.Message}");
        }
        finally
        {
            SafeRelease(ref sessionEnum);
        }
    }

    public void SetAppMute(uint pid, bool mute)
    {
        if (_sessionManager == null) return;

        IAudioSessionEnumerator? sessionEnum = null;
        try
        {
            int hr = _sessionManager.GetSessionEnumerator(out sessionEnum);
            if (hr != 0 || sessionEnum == null) return;

            sessionEnum.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl? control = null;
                try
                {
                    hr = sessionEnum.GetSession(i, out control);
                    if (hr != 0 || control == null) continue;

                    if (control is IAudioSessionControl2 control2)
                    {
                        control2.GetProcessId(out uint curPid);
                        bool isSys = control2.IsSystemSoundsSession() == 0;

                        if ((isSys && pid == 0) || curPid == pid)
                        {
                            if (control is ISimpleAudioVolume simpleVolume)
                            {
                                simpleVolume.SetMute(mute, ref _emptyGuid);
                                return;
                            }
                        }
                    }
                }
                finally
                {
                    SafeRelease(ref control);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioService] SetAppMute failed: {ex.Message}");
        }
        finally
        {
            SafeRelease(ref sessionEnum);
        }
    }

    private ImageSource? GetOrLoadProcessIcon(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return null;

        if (_iconCache.TryGetValue(exePath, out var cached))
        {
            return cached;
        }

        try
        {
            using var icon = Icon.ExtractAssociatedIcon(exePath);
            if (icon != null)
            {
                var bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());

                bitmapSource.Freeze(); // Crucial: Free-threaded and immune to memory leaks
                _iconCache[exePath] = bitmapSource;
                return bitmapSource;
            }
        }
        catch { }

        return null;
    }

    #endregion

    private static void SafeRelease<T>(ref T? comObject) where T : class
    {
        if (comObject != null)
        {
            try
            {
                Marshal.ReleaseComObject(comObject);
            }
            catch { }
            comObject = null;
        }
    }

    public void Dispose()
    {
        if (_enumerator != null)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(_endpointListener); } catch { }
        }

        if (_endpointVolume != null)
        {
            try { _endpointVolume.UnregisterControlChangeNotify(_volumeListener); } catch { }
        }

        if (_sessionManager != null)
        {
            try { _sessionManager.UnregisterSessionNotification(_sessionListener); } catch { }
        }

        SafeRelease(ref _endpointVolume);
        SafeRelease(ref _sessionManager);
        SafeRelease(ref _defaultRenderDevice);
        SafeRelease(ref _enumerator);

        _iconCache.Clear();
    }
}
