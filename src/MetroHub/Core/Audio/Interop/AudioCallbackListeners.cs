using System;
using System.Runtime.InteropServices;

namespace MetroHub.Core.Audio.Interop;

public class EndpointNotificationCallback : IMMNotificationClient
{
    public event Action? DefaultDeviceChanged;
    public event Action? DeviceListChanged;

    public int OnDeviceStateChanged(string pwstrDeviceId, DeviceState dwNewState)
    {
        Task.Run(() => DeviceListChanged?.Invoke());
        return 0;
    }

    public int OnDeviceAdded(string pwstrDeviceId)
    {
        Task.Run(() => DeviceListChanged?.Invoke());
        return 0;
    }

    public int OnDeviceRemoved(string pwstrDeviceId)
    {
        Task.Run(() => DeviceListChanged?.Invoke());
        return 0;
    }

    public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string pwstrDefaultDeviceId)
    {
        if (flow == EDataFlow.eRender && (role == ERole.eMultimedia || role == ERole.eConsole))
        {
            Task.Run(() => DefaultDeviceChanged?.Invoke());
        }
        return 0;
    }

    public int OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
        return 0;
    }
}

public class EndpointVolumeCallback : IAudioEndpointVolumeCallback
{
    public event Action<float, bool>? VolumeChanged;

    public int OnNotify(IntPtr pNotifyData)
    {
        if (pNotifyData == IntPtr.Zero) return 0;

        try
        {
            var data = Marshal.PtrToStructure<AUDIO_VOLUME_NOTIFICATION_DATA>(pNotifyData);
            VolumeChanged?.Invoke(data.fMasterVolume, data.bMuted);
        }
        catch { }

        return 0;
    }
}

public class AudioSessionNotificationListener : IAudioSessionNotification
{
    public event Action? SessionCreated;

    public int OnSessionCreated(IAudioSessionControl newSession)
    {
        SessionCreated?.Invoke();
        return 0;
    }
}

public class AudioSessionEventsListener : IAudioSessionEvents
{
    public uint ProcessId { get; }
    public event Action<uint, float, bool>? VolumeChanged;
    public event Action<uint>? SessionEnded;

    public AudioSessionEventsListener(uint processId)
    {
        ProcessId = processId;
    }

    public int OnSimpleVolumeChanged(float NewVolume, bool NewMute, ref Guid EventContext)
    {
        VolumeChanged?.Invoke(ProcessId, NewVolume, NewMute);
        return 0;
    }

    public int OnDisplayNameChanged(string NewDisplayName, ref Guid EventContext) => 0;
    public int OnIconPathChanged(string NewIconPath, ref Guid EventContext) => 0;
    public int OnChannelVolumeChanged(uint ChannelCount, IntPtr NewChannelVolumeArray, uint ChangedChannel, ref Guid EventContext) => 0;
    public int OnGroupingParamChanged(ref Guid NewGroupingParam, ref Guid EventContext) => 0;

    public int OnStateChanged(AudioSessionState NewState)
    {
        if (NewState == AudioSessionState.Expired)
        {
            SessionEnded?.Invoke(ProcessId);
        }
        return 0;
    }

    public int OnSessionDisconnected(AudioSessionDisconnectReason DisconnectReason)
    {
        SessionEnded?.Invoke(ProcessId);
        return 0;
    }
}
