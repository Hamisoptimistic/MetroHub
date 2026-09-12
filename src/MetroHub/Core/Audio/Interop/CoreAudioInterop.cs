using System;
using System.Runtime.InteropServices;

namespace MetroHub.Core.Audio.Interop;

#region Enumerations

public enum EDataFlow
{
    eRender = 0,
    eCapture = 1,
    eAll = 2
}

public enum ERole
{
    eConsole = 0,
    eMultimedia = 1,
    eCommunications = 2
}

[Flags]
public enum DeviceState : uint
{
    Active = 0x00000001,
    Disabled = 0x00000002,
    NotPresent = 0x00000004,
    Unplugged = 0x00000008,
    All = 0x0000000F
}

public enum StorageAccessMode
{
    Read = 0,
    Write = 1,
    ReadWrite = 2
}

public enum AudioSessionState
{
    Inactive = 0,
    Active = 1,
    Expired = 2
}

public enum AudioSessionDisconnectReason
{
    DisconnectReasonDeviceRemoval = 0,
    DisconnectReasonServerShutdown = 1,
    DisconnectReasonFormatChanged = 2,
    DisconnectReasonSessionLogoff = 3,
    DisconnectReasonSessionDisconnected = 4,
    DisconnectReasonExclusiveModeOverride = 5
}

[Flags]
public enum CLSCTX : uint
{
    INPROC_SERVER = 0x1,
    INPROC_HANDLER = 0x2,
    LOCAL_SERVER = 0x4,
    INPROC_SERVER16 = 0x8,
    REMOTE_SERVER = 0x10,
    INPROC_HANDLER16 = 0x20,
    NO_CODE_DOWNLOAD = 0x400,
    NO_CUSTOM_MARSHAL = 0x1000,
    ENABLE_CODE_DOWNLOAD = 0x2000,
    NO_FAILURE_LOG = 0x4000,
    DISABLE_AAA = 0x8000,
    ENABLE_AAA = 0x10000,
    FROM_DEFAULT_CONTEXT = 0x20000,
    INPROC = INPROC_SERVER | INPROC_HANDLER,
    SERVER = INPROC_SERVER | LOCAL_SERVER | REMOTE_SERVER,
    ALL = SERVER | INPROC_HANDLER
}

#endregion

#region Structures

[StructLayout(LayoutKind.Sequential)]
public struct PropertyKey
{
    public Guid fmtid;
    public uint pid;

    public PropertyKey(Guid fmtid, uint pid)
    {
        this.fmtid = fmtid;
        this.pid = pid;
    }
}

[StructLayout(LayoutKind.Explicit)]
public struct PropVariant
{
    [FieldOffset(0)] public short vt;
    [FieldOffset(2)] public short wReserved1;
    [FieldOffset(4)] public short wReserved2;
    [FieldOffset(6)] public short wReserved3;
    [FieldOffset(8)] public IntPtr pwszVal;
    [FieldOffset(8)] public uint uintVal;
}

[StructLayout(LayoutKind.Sequential)]
public struct AUDIO_VOLUME_NOTIFICATION_DATA
{
    public Guid guidEventContext;
    [MarshalAs(UnmanagedType.Bool)]
    public bool bMuted;
    public float fMasterVolume;
    public uint nChannels;
    public float fChannelVolume;
}

#endregion

#region COM CoClasses

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
public class MMDeviceEnumeratorComObject { }

[ComImport]
[Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
public class CPolicyConfigVistaClient { }

[ComImport]
[Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
public class PolicyConfigClient { }

#endregion

#region COM Interfaces

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState dwStateMask, out IMMDeviceCollection ppDevices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IMMNotificationClient pClient);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IMMNotificationClient pClient);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint pcDevices);

    [PreserveSig]
    int Item(uint nDevice, out IMMDevice ppDevice);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, CLSCTX dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);

    [PreserveSig]
    int OpenPropertyStore(StorageAccessMode stgmAccess, out IPropertyStore ppProperties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);

    [PreserveSig]
    int GetState(out DeviceState pdwState);
}

[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint cProps);

    [PreserveSig]
    int GetAt(uint iProp, out PropertyKey pkey);

    [PreserveSig]
    int GetValue(ref PropertyKey key, out PropVariant pv);

    [PreserveSig]
    int SetValue(ref PropertyKey key, ref PropVariant propvar);

    [PreserveSig]
    int Commit();
}

[ComImport]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMNotificationClient
{
    [PreserveSig]
    int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, DeviceState dwNewState);

    [PreserveSig]
    int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);

    [PreserveSig]
    int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);

    [PreserveSig]
    int OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string pwstrDefaultDeviceId);

    [PreserveSig]
    int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, PropertyKey key);
}

[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioEndpointVolume
{
    [PreserveSig]
    int RegisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify);

    [PreserveSig]
    int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify);

    [PreserveSig]
    int GetChannelCount(out uint pnChannelCount);

    [PreserveSig]
    int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);

    [PreserveSig]
    int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);

    [PreserveSig]
    int GetMasterVolumeLevel(out float pfLevelDB);

    [PreserveSig]
    int GetMasterVolumeLevelScalar(out float pfLevel);

    [PreserveSig]
    int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);

    [PreserveSig]
    int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);

    [PreserveSig]
    int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);

    [PreserveSig]
    int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);

    [PreserveSig]
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);

    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);

    [PreserveSig]
    int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);

    [PreserveSig]
    int VolumeStepUp(ref Guid pguidEventContext);

    [PreserveSig]
    int VolumeStepDown(ref Guid pguidEventContext);

    [PreserveSig]
    int QueryHardwareSupport(out uint pdwHardwareSupportMask);

    [PreserveSig]
    int GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
}

[ComImport]
[Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioEndpointVolumeCallback
{
    [PreserveSig]
    int OnNotify(IntPtr pNotifyData);
}

[ComImport]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionManager2
{
    [PreserveSig]
    int GetAudioSessionControl(ref Guid AudioSessionGuid, uint StreamFlags, out IAudioSessionControl SessionControl);

    [PreserveSig]
    int GetSimpleAudioVolume(ref Guid AudioSessionGuid, uint StreamFlags, out ISimpleAudioVolume AudioVolume);

    [PreserveSig]
    int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);

    [PreserveSig]
    int RegisterSessionNotification(IAudioSessionNotification NewSessionNotifications);

    [PreserveSig]
    int UnregisterSessionNotification(IAudioSessionNotification NewSessionNotifications);

    [PreserveSig]
    int RegisterDuckNotification(string sessionID, IntPtr duckNotification);

    [PreserveSig]
    int UnregisterDuckNotification(IntPtr duckNotification);
}

[ComImport]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionEnumerator
{
    [PreserveSig]
    int GetCount(out int SessionCount);

    [PreserveSig]
    int GetSession(int SessionIndex, out IAudioSessionControl Session);
}

[ComImport]
[Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionControl
{
    [PreserveSig]
    int GetState(out AudioSessionState pRetVal);

    [PreserveSig]
    int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);

    [PreserveSig]
    int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);

    [PreserveSig]
    int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);

    [PreserveSig]
    int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);

    [PreserveSig]
    int GetGroupingParam(out Guid pRetVal);

    [PreserveSig]
    int SetGroupingParam(ref Guid Override, ref Guid EventContext);

    [PreserveSig]
    int RegisterAudioSessionNotification(IAudioSessionEvents NewNotifications);

    [PreserveSig]
    int UnregisterAudioSessionNotification(IAudioSessionEvents NewNotifications);
}

[ComImport]
[Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionControl2
{
    [PreserveSig]
    int GetState(out AudioSessionState pRetVal);

    [PreserveSig]
    int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);

    [PreserveSig]
    int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, [In] ref Guid EventContext);

    [PreserveSig]
    int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);

    [PreserveSig]
    int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, [In] ref Guid EventContext);

    [PreserveSig]
    int GetGroupingParam(out Guid pRetVal);

    [PreserveSig]
    int SetGroupingParam([In] ref Guid Override, [In] ref Guid EventContext);

    [PreserveSig]
    int RegisterAudioSessionNotification([In] IAudioSessionEvents NewNotifications);

    [PreserveSig]
    int UnregisterAudioSessionNotification([In] IAudioSessionEvents NewNotifications);

    [PreserveSig]
    int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);

    [PreserveSig]
    int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);

    [PreserveSig]
    int GetProcessId(out uint pRetVal);

    [PreserveSig]
    int IsSystemSoundsSession();

    [PreserveSig]
    int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

[ComImport]
[Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ISimpleAudioVolume
{
    [PreserveSig]
    int SetMasterVolume(float fLevel, [In] ref Guid EventContext);

    [PreserveSig]
    int GetMasterVolume(out float pfLevel);

    [PreserveSig]
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, [In] ref Guid EventContext);

    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
}

[ComImport]
[Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionEvents
{
    [PreserveSig]
    int OnDisplayNameChanged([MarshalAs(UnmanagedType.LPWStr)] string NewDisplayName, [In] ref Guid EventContext);

    [PreserveSig]
    int OnIconPathChanged([MarshalAs(UnmanagedType.LPWStr)] string NewIconPath, [In] ref Guid EventContext);

    [PreserveSig]
    int OnSimpleVolumeChanged(float NewVolume, [MarshalAs(UnmanagedType.Bool)] bool NewMute, [In] ref Guid EventContext);

    [PreserveSig]
    int OnChannelVolumeChanged(uint ChannelCount, IntPtr NewChannelVolumeArray, uint ChangedChannel, [In] ref Guid EventContext);

    [PreserveSig]
    int OnGroupingParamChanged([In] ref Guid NewGroupingParam, [In] ref Guid EventContext);

    [PreserveSig]
    int OnStateChanged(AudioSessionState NewState);

    [PreserveSig]
    int OnSessionDisconnected(AudioSessionDisconnectReason DisconnectReason);
}

[ComImport]
[Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionNotification
{
    [PreserveSig]
    int OnSessionCreated(IAudioSessionControl NewSession);
}

[ComImport]
[Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPolicyConfigVista
{
    [PreserveSig]
    int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr ppFormat);

    [PreserveSig]
    int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bDefault, IntPtr ppFormat);

    [PreserveSig]
    int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);

    [PreserveSig]
    int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr pMixFormat);

    [PreserveSig]
    int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);

    [PreserveSig]
    int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pmftPeriod);

    [PreserveSig]
    int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pMode);

    [PreserveSig]
    int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);

    [PreserveSig]
    int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bFxStore, ref PropertyKey key, out PropVariant pv);

    [PreserveSig]
    int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bFxStore, ref PropertyKey key, ref PropVariant pv);

    [PreserveSig]
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceId, ERole role);

    [PreserveSig]
    int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceId, [MarshalAs(UnmanagedType.Bool)] bool bVisible);
}

#endregion

public static class CoreAudioConstants
{
    public static readonly PropertyKey PKEY_Device_FriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    public static readonly PropertyKey PKEY_DeviceInterface_FriendlyName = new(new Guid("026E516E-B814-414B-83CD-856D6FEF4822"), 2);
    public static readonly PropertyKey PKEY_Device_DeviceDesc = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 2);
}
