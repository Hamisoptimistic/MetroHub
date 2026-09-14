using System;
using System.Runtime.InteropServices;

namespace MetroHub.Core.Network.Interop;

public static class WlanNative
{
    private const string WlanApi = "wlanapi.dll";

    public const int ERROR_SUCCESS = 0;
    public const int ERROR_SERVICE_NOT_ACTIVE = 1062;
    public const int ERROR_NOT_SUPPORTED = 50;
    public const int ERROR_INVALID_PARAMETER = 87;

    public const uint WLAN_API_VERSION_2_0 = 2;
    public const uint WLAN_AVAILABLE_NETWORK_INCLUDE_ALL_ADHOC_PROFILES = 0x00000001;
    public const uint WLAN_AVAILABLE_NETWORK_INCLUDE_ALL_MANUAL_HIDDEN_PROFILES = 0x00000002;

    public enum WLAN_INTERFACE_STATE
    {
        wlan_interface_state_not_ready = 0,
        wlan_interface_state_connected = 1,
        wlan_interface_state_ad_hoc_network_formed = 2,
        wlan_interface_state_disconnecting = 3,
        wlan_interface_state_disconnected = 4,
        wlan_interface_state_associating = 5,
        wlan_interface_state_discovering = 6,
        wlan_interface_state_authenticating = 7
    }

    public enum WLAN_INTF_OPCODE
    {
        wlan_intf_opcode_autoconf_start = 0x00000000,
        wlan_intf_opcode_autoconf_enabled,
        wlan_intf_opcode_background_scan_enabled,
        wlan_intf_opcode_media_streaming_mode,
        wlan_intf_opcode_radio_state,
        wlan_intf_opcode_bss_type,
        wlan_intf_opcode_interface_state,
        wlan_intf_opcode_current_connection,
        wlan_intf_opcode_channel_number,
        wlan_intf_opcode_supported_infrastructure_auth_cipher_pairs,
        wlan_intf_opcode_supported_adhoc_auth_cipher_pairs,
        wlan_intf_opcode_supported_country_or_region_string_list,
        wlan_intf_opcode_current_operation_mode,
        wlan_intf_opcode_supported_safe_mode,
        wlan_intf_opcode_certified_safe_mode,
        wlan_intf_opcode_hosted_network_capable,
        wlan_intf_opcode_management_frame_protection_capable,
        wlan_intf_opcode_autoconf_end = 0x0fffffff
    }

    public enum WLAN_CONNECTION_MODE
    {
        wlan_connection_mode_profile = 0,
        wlan_connection_mode_temporary_profile,
        wlan_connection_mode_discovery_profile,
        wlan_connection_mode_discovery_secure,
        wlan_connection_mode_auto,
        wlan_connection_mode_invalid
    }

    public enum DOT11_PHY_TYPE : uint
    {
        dot11_phy_type_unknown = 0,
        dot11_phy_type_any = 0,
        dot11_phy_type_fhss = 1,
        dot11_phy_type_dsss = 2,
        dot11_phy_type_irbaseband = 3,
        dot11_phy_type_ofdm = 4,
        dot11_phy_type_hrdsss = 5,
        dot11_phy_type_erp = 6,
        dot11_phy_type_ht = 7,  // 802.11n (Wi-Fi 4)
        dot11_phy_type_vht = 8, // 802.11ac (Wi-Fi 5)
        dot11_phy_type_he = 9,  // 802.11ax (Wi-Fi 6 / 6E)
        dot11_phy_type_eht = 10 // 802.11be (Wi-Fi 7)
    }

    public enum DOT11_AUTH_ALGORITHM : uint
    {
        DOT11_AUTH_ALGO_80211_OPEN = 1,
        DOT11_AUTH_ALGO_80211_SHARED_KEY = 2,
        DOT11_AUTH_ALGO_WPA = 3,
        DOT11_AUTH_ALGO_WPA_PSK = 4,
        DOT11_AUTH_ALGO_WPA_NONE = 5,
        DOT11_AUTH_ALGO_RSNA = 6,
        DOT11_AUTH_ALGO_RSNA_PSK = 7, // WPA2-Personal
        DOT11_AUTH_ALGO_WPA3 = 8,
        DOT11_AUTH_ALGO_WPA3_ENT_192 = 9,
        DOT11_AUTH_ALGO_WPA3_SAE = 10 // WPA3-Personal
    }

    public enum DOT11_CIPHER_ALGORITHM : uint
    {
        DOT11_CIPHER_ALGO_NONE = 0x00,
        DOT11_CIPHER_ALGO_WEP40 = 0x01,
        DOT11_CIPHER_ALGO_TKIP = 0x02,
        DOT11_CIPHER_ALGO_CCMP = 0x04,
        DOT11_CIPHER_ALGO_WEP104 = 0x05,
        DOT11_CIPHER_ALGO_BIP = 0x06,
        DOT11_CIPHER_ALGO_GCMP = 0x08,
        DOT11_CIPHER_ALGO_GCMP_256 = 0x09,
        DOT11_CIPHER_ALGO_CCMP_256 = 0x0a,
        DOT11_CIPHER_ALGO_BIP_GMAC_128 = 0x0b,
        DOT11_CIPHER_ALGO_BIP_GMAC_256 = 0x0c,
        DOT11_CIPHER_ALGO_BIP_CMAC_256 = 0x0d
    }

    public enum DOT11_RADIO_STATE
    {
        dot11_radio_state_unknown = 0,
        dot11_radio_state_on,
        dot11_radio_state_off
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WLAN_PHY_RADIO_STATE
    {
        public uint dwPhyIndex;
        public DOT11_RADIO_STATE dot11SoftwareRadioState;
        public DOT11_RADIO_STATE dot11HardwareRadioState;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WLAN_INTERFACE_INFO
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strInterfaceDescription;
        public WLAN_INTERFACE_STATE isState;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DOT11_SSID
    {
        public uint uSSIDLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] ucSSID;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WLAN_AVAILABLE_NETWORK
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strProfileName;
        public DOT11_SSID dot11Ssid;
        public uint dot11BssType;
        public uint uNumberOfBssids;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bNetworkConnectable;
        public uint wlanNotConnectableReason;
        public uint uNumberOfPhyTypes;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public uint[] dot11PhyTypes;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bMorePhyTypes;
        public uint wlanSignalQuality;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bSecurityEnabled;
        public DOT11_AUTH_ALGORITHM dot11DefaultAuthAlgorithm;
        public DOT11_CIPHER_ALGORITHM dot11DefaultCipherAlgorithm;
        public uint dwFlags;
        public uint dwReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WLAN_ASSOCIATION_ATTRIBUTES
    {
        public DOT11_SSID dot11Ssid;
        public uint dot11BssType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] dot11Bssid;
        public DOT11_PHY_TYPE dot11PhyType;
        public uint uDot11PhyIndex;
        public uint wlanSignalQuality;
        public uint ulRxRate; // in Kbps
        public uint ulTxRate; // in Kbps
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WLAN_SECURITY_ATTRIBUTES
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool bSecurityEnabled;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bOneXEnabled;
        public DOT11_AUTH_ALGORITHM dot11AuthAlgorithm;
        public DOT11_CIPHER_ALGORITHM dot11CipherAlgorithm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WLAN_CONNECTION_ATTRIBUTES
    {
        public WLAN_INTERFACE_STATE isState;
        public WLAN_CONNECTION_MODE wlanConnectionMode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strProfileName;
        public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
        public WLAN_SECURITY_ATTRIBUTES wlanSecurityAttributes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WLAN_CONNECTION_PARAMETERS
    {
        public WLAN_CONNECTION_MODE wlanConnectionMode;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string strProfile;
        public IntPtr pDot11Ssid;
        public IntPtr pDesiredBssidList;
        public uint dot11BssType;
        public uint dwFlags;
    }

    [DllImport(WlanApi, SetLastError = true)]
    public static extern int WlanOpenHandle(
        uint dwClientVersion,
        IntPtr pReserved,
        out uint pdwNegotiatedVersion,
        out IntPtr phClientHandle);

    [DllImport(WlanApi, SetLastError = true)]
    public static extern int WlanCloseHandle(
        IntPtr hClientHandle,
        IntPtr pReserved);

    [DllImport(WlanApi, SetLastError = true)]
    public static extern int WlanEnumInterfaces(
        IntPtr hClientHandle,
        IntPtr pReserved,
        out IntPtr ppInterfaceList);

    [DllImport(WlanApi, SetLastError = true)]
    public static extern int WlanScan(
        IntPtr hClientHandle,
        ref Guid pInterfaceGuid,
        IntPtr pDot11Ssid,
        IntPtr pIeData,
        IntPtr pReserved);

    [DllImport(WlanApi, SetLastError = true)]
    public static extern int WlanGetAvailableNetworkList(
        IntPtr hClientHandle,
        ref Guid pInterfaceGuid,
        uint dwFlags,
        IntPtr pReserved,
        out IntPtr ppAvailableNetworkList);

    [DllImport(WlanApi, SetLastError = true)]
    public static extern int WlanQueryInterface(
        IntPtr hClientHandle,
        ref Guid pInterfaceGuid,
        WLAN_INTF_OPCODE OpCode,
        IntPtr pReserved,
        out uint pdwDataSize,
        out IntPtr ppData,
        out int pWlanOpcodeValueType);

    [DllImport(WlanApi, SetLastError = true)]
    public static extern int WlanDisconnect(
        IntPtr hClientHandle,
        ref Guid pInterfaceGuid,
        IntPtr pReserved);

    [DllImport(WlanApi, SetLastError = true)]
    public static extern int WlanConnect(
        IntPtr hClientHandle,
        ref Guid pInterfaceGuid,
        ref WLAN_CONNECTION_PARAMETERS pConnectionParameters,
        IntPtr pReserved);

    [DllImport(WlanApi, SetLastError = true)]
    public static extern int WlanSetInterface(
        IntPtr hClientHandle,
        ref Guid pInterfaceGuid,
        WLAN_INTF_OPCODE OpCode,
        uint dwDataSize,
        IntPtr pData,
        IntPtr pReserved);

    public const uint WLAN_PROFILE_USER = 0x00000001;

    [DllImport(WlanApi, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int WlanSetProfile(
        IntPtr hClientHandle,
        ref Guid pInterfaceGuid,
        uint dwFlags,
        string strProfileXml,
        [MarshalAs(UnmanagedType.LPWStr)] string? strAllUserProfileSecurity,
        bool bOverwrite,
        IntPtr pReserved,
        out uint pdwReasonCode);

    [DllImport(WlanApi)]
    public static extern void WlanFreeMemory(IntPtr pMemory);
}
