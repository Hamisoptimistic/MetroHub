using System;
using System.Runtime.InteropServices;

namespace MetroHub.Core.Network.Interop;

public static class IpHelperInterop
{
    private const string IpHlpApi = "iphlpapi.dll";
    private const string Kernel32 = "kernel32.dll";

    public const int NO_ERROR = 0;
    public const int IF_MAX_STRING_SIZE = 256;
    public const int IF_MAX_PHYS_ADDRESS_SIZE = 32;

    [DllImport(Kernel32, ExactSpelling = true)]
    public static extern ulong GetTickCount64();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MIB_IF_ROW2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public Guid InterfaceGuid;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = IF_MAX_STRING_SIZE + 1)]
        public string Alias;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = IF_MAX_STRING_SIZE + 1)]
        public string Description;

        public uint PhysicalAddressLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = IF_MAX_PHYS_ADDRESS_SIZE)]
        public byte[] PhysicalAddress;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = IF_MAX_PHYS_ADDRESS_SIZE)]
        public byte[] PermanentPhysicalAddress;

        public uint Mtu;
        public uint Type;
        public uint TunnelType;
        public uint MediaType;
        public uint PhysicalMediumType;
        public uint AccessType;
        public uint DirectionType;
        public byte InterfaceAndOperStatusFlags;
        public uint OperStatus;
        public uint AdminStatus;
        public uint MediaConnectState;
        public Guid NetworkGuid;
        public uint ConnectionType;
        public ulong TransmitLinkSpeed;
        public ulong ReceiveLinkSpeed;
        public ulong InOctets;
        public ulong InUcastPkts;
        public ulong InNUcastPkts;
        public ulong InDiscards;
        public ulong InErrors;
        public ulong InUnknownProtos;
        public ulong InUcastOctets;
        public ulong InMulticastOctets;
        public ulong InBroadcastOctets;
        public ulong OutOctets;
        public ulong OutUcastPkts;
        public ulong OutNUcastPkts;
        public ulong OutDiscards;
        public ulong OutErrors;
        public ulong OutUcastOctets;
        public ulong OutMulticastOctets;
        public ulong OutBroadcastOctets;
        public ulong OutQLen;
        public ulong LastChange;
    }

    [DllImport(IpHlpApi, SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetIfEntry2(ref MIB_IF_ROW2 Row);
}
