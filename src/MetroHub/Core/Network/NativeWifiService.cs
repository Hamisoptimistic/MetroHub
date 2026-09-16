using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using MetroHub.Core.Network.Interop;

namespace MetroHub.Core.Network;

public sealed class NativeWifiService : IDisposable
{
    private static readonly Lazy<NativeWifiService> _instance = new(() => new NativeWifiService());
    public static NativeWifiService Instance => _instance.Value;

    private IntPtr _clientHandle = IntPtr.Zero;
    private Guid _primaryInterfaceGuid = Guid.Empty;
    private string _primaryInterfaceDescription = string.Empty;
    private bool _hasWifiAdapter;
    private string _wifiStatusMessage = "Initializing Wi-Fi...";

    private readonly object _lock = new();
    private DateTime _lastEnumTime = DateTime.MinValue;
    private static readonly TimeSpan EnumThrottle = TimeSpan.FromMilliseconds(500);

    public bool HasWifiAdapter
    {
        get
        {
            lock (_lock)
            {
                if (DateTime.UtcNow - _lastEnumTime > EnumThrottle)
                {
                    _lastEnumTime = DateTime.UtcNow;
                    RefreshInterfaces();
                }
                return _hasWifiAdapter;
            }
        }
    }

    public string WifiStatusMessage => _wifiStatusMessage;
    public Guid PrimaryInterfaceGuid => _primaryInterfaceGuid;
    public string PrimaryInterfaceDescription => _primaryInterfaceDescription;

    public bool IsRadioOn
    {
        get
        {
            if (!_hasWifiAdapter || _primaryInterfaceGuid == Guid.Empty || _clientHandle == IntPtr.Zero)
            {
                return false;
            }

            IntPtr ppData = IntPtr.Zero;
            try
            {
                int result = WlanNative.WlanQueryInterface(
                    _clientHandle,
                    ref _primaryInterfaceGuid,
                    WlanNative.WLAN_INTF_OPCODE.wlan_intf_opcode_radio_state,
                    IntPtr.Zero,
                    out _,
                    out ppData,
                    out _);

                if (result != WlanNative.ERROR_SUCCESS || ppData == IntPtr.Zero)
                {
                    return false;
                }

                uint numPhys = (uint)Marshal.ReadInt32(ppData);
                if (numPhys == 0) return true;

                int phyStructSize = Marshal.SizeOf<WlanNative.WLAN_PHY_RADIO_STATE>();
                IntPtr cur = IntPtr.Add(ppData, 4);

                for (int i = 0; i < numPhys; i++)
                {
                    var phy = Marshal.PtrToStructure<WlanNative.WLAN_PHY_RADIO_STATE>(cur);
                    cur = IntPtr.Add(cur, phyStructSize);

                    if (phy.dot11SoftwareRadioState == WlanNative.DOT11_RADIO_STATE.dot11_radio_state_off)
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (ppData != IntPtr.Zero)
                {
                    WlanNative.WlanFreeMemory(ppData);
                }
            }
        }
    }

    public bool SetRadioState(bool turnOn)
    {
        if (!_hasWifiAdapter || _primaryInterfaceGuid == Guid.Empty || _clientHandle == IntPtr.Zero)
        {
            return false;
        }

        var targetState = turnOn ? WlanNative.DOT11_RADIO_STATE.dot11_radio_state_on : WlanNative.DOT11_RADIO_STATE.dot11_radio_state_off;
        uint size = (uint)Marshal.SizeOf<WlanNative.WLAN_PHY_RADIO_STATE>();

        List<uint> phyIndices = new() { 0 };
        IntPtr ppData = IntPtr.Zero;
        try
        {
            int queryRes = WlanNative.WlanQueryInterface(
                _clientHandle,
                ref _primaryInterfaceGuid,
                WlanNative.WLAN_INTF_OPCODE.wlan_intf_opcode_radio_state,
                IntPtr.Zero,
                out _,
                out ppData,
                out _);

            if (queryRes == WlanNative.ERROR_SUCCESS && ppData != IntPtr.Zero)
            {
                uint numPhys = (uint)Marshal.ReadInt32(ppData);
                if (numPhys > 0)
                {
                    phyIndices.Clear();
                    int phyStructSize = Marshal.SizeOf<WlanNative.WLAN_PHY_RADIO_STATE>();
                    IntPtr cur = IntPtr.Add(ppData, 4);
                    for (int i = 0; i < numPhys; i++)
                    {
                        var phy = Marshal.PtrToStructure<WlanNative.WLAN_PHY_RADIO_STATE>(cur);
                        phyIndices.Add(phy.dwPhyIndex);
                        cur = IntPtr.Add(cur, phyStructSize);
                    }
                }
            }
        }
        catch { }
        finally
        {
            if (ppData != IntPtr.Zero)
            {
                WlanNative.WlanFreeMemory(ppData);
            }
        }

        bool allSuccess = true;
        foreach (var phyIdx in phyIndices)
        {
            var phyState = new WlanNative.WLAN_PHY_RADIO_STATE
            {
                dwPhyIndex = phyIdx,
                dot11SoftwareRadioState = targetState,
                dot11HardwareRadioState = WlanNative.DOT11_RADIO_STATE.dot11_radio_state_unknown
            };

            IntPtr pData = Marshal.AllocHGlobal((int)size);
            try
            {
                Marshal.StructureToPtr(phyState, pData, false);
                int res = WlanNative.WlanSetInterface(
                    _clientHandle,
                    ref _primaryInterfaceGuid,
                    WlanNative.WLAN_INTF_OPCODE.wlan_intf_opcode_radio_state,
                    size,
                    pData,
                    IntPtr.Zero);

                if (res != WlanNative.ERROR_SUCCESS)
                {
                    allSuccess = false;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pData);
            }
        }

        return allSuccess;
    }

    public NativeWifiService()
    {
        EnsureClientHandle();
    }

    public bool EnsureClientHandle()
    {
        if (_clientHandle != IntPtr.Zero)
        {
            return true;
        }

        int result = WlanNative.WlanOpenHandle(
            WlanNative.WLAN_API_VERSION_2_0,
            IntPtr.Zero,
            out _,
            out _clientHandle);

        if (result != WlanNative.ERROR_SUCCESS || _clientHandle == IntPtr.Zero)
        {
            _hasWifiAdapter = false;
            _wifiStatusMessage = result == WlanNative.ERROR_SERVICE_NOT_ACTIVE
                ? "WLAN AutoConfig service is stopped (No Wi-Fi hardware detected)."
                : $"Wi-Fi subsystem unavailable (Error code {result}).";
            return false;
        }

        return RefreshInterfaces();
    }

    public bool RefreshInterfaces()
    {
        lock (_lock)
        {
            _lastEnumTime = DateTime.UtcNow;

            if (_clientHandle == IntPtr.Zero && !EnsureClientHandle())
            {
                _hasWifiAdapter = false;
                _primaryInterfaceGuid = Guid.Empty;
                _primaryInterfaceDescription = string.Empty;
                return false;
            }

            IntPtr pInterfaceList = IntPtr.Zero;
            try
            {
                int result = WlanNative.WlanEnumInterfaces(_clientHandle, IntPtr.Zero, out pInterfaceList);
                if (result == 6) // ERROR_INVALID_HANDLE
                {
                    _clientHandle = IntPtr.Zero;
                    if (EnsureClientHandle())
                    {
                        result = WlanNative.WlanEnumInterfaces(_clientHandle, IntPtr.Zero, out pInterfaceList);
                    }
                }

                if (result != WlanNative.ERROR_SUCCESS || pInterfaceList == IntPtr.Zero)
                {
                    _hasWifiAdapter = false;
                    _wifiStatusMessage = "No Wi-Fi interfaces found.";
                    _primaryInterfaceGuid = Guid.Empty;
                    _primaryInterfaceDescription = string.Empty;
                    return false;
                }

                uint count = (uint)Marshal.ReadInt32(pInterfaceList);
                if (count == 0)
                {
                    _hasWifiAdapter = false;
                    _wifiStatusMessage = "No Wi-Fi adapter detected.";
                    _primaryInterfaceGuid = Guid.Empty;
                    _primaryInterfaceDescription = string.Empty;
                    return false;
                }

                // Read the first interface
                IntPtr firstInterfacePtr = IntPtr.Add(pInterfaceList, 8); // Skip dwNumberOfItems (4) + dwIndex (4)
                var info = Marshal.PtrToStructure<WlanNative.WLAN_INTERFACE_INFO>(firstInterfacePtr);

                _primaryInterfaceGuid = info.InterfaceGuid;
                _primaryInterfaceDescription = info.strInterfaceDescription ?? string.Empty;
                _hasWifiAdapter = true;
                _wifiStatusMessage = $"Wi-Fi Adapter Ready: {_primaryInterfaceDescription}";
                return true;
            }
            catch (Exception ex)
            {
                _hasWifiAdapter = false;
                _wifiStatusMessage = $"Wi-Fi enumeration error: {ex.Message}";
                _primaryInterfaceGuid = Guid.Empty;
                _primaryInterfaceDescription = string.Empty;
                return false;
            }
            finally
            {
                if (pInterfaceList != IntPtr.Zero)
                {
                    try { WlanNative.WlanFreeMemory(pInterfaceList); } catch { }
                }
            }
        }
    }

    public WifiConnectionDetails GetCurrentConnectionDetails()
    {
        var details = new WifiConnectionDetails();

        if (!_hasWifiAdapter || _primaryInterfaceGuid == Guid.Empty)
        {
            details.IsConnected = false;
            return details;
        }

        IntPtr ppData = IntPtr.Zero;
        try
        {
            int result = WlanNative.WlanQueryInterface(
                _clientHandle,
                ref _primaryInterfaceGuid,
                WlanNative.WLAN_INTF_OPCODE.wlan_intf_opcode_current_connection,
                IntPtr.Zero,
                out uint dataSize,
                out ppData,
                out _);

            if (result != WlanNative.ERROR_SUCCESS || ppData == IntPtr.Zero)
            {
                details.IsConnected = false;
                return details;
            }

            var conn = Marshal.PtrToStructure<WlanNative.WLAN_CONNECTION_ATTRIBUTES>(ppData);
            details.IsConnected = conn.isState == WlanNative.WLAN_INTERFACE_STATE.wlan_interface_state_connected;

            if (details.IsConnected)
            {
                details.Ssid = FormatSsid(conn.wlanAssociationAttributes.dot11Ssid);
                details.AdapterDescription = _primaryInterfaceDescription;
                details.SignalQuality = (int)conn.wlanAssociationAttributes.wlanSignalQuality;
                details.Standard = MapPhyTypeToStandard(conn.wlanAssociationAttributes.dot11PhyType);
                details.SecurityType = MapAuthAlgorithm(conn.wlanSecurityAttributes.dot11AuthAlgorithm);

                details.ReceiveLinkSpeedBps = (long)conn.wlanAssociationAttributes.ulRxRate * 1000;
                details.TransmitLinkSpeedBps = (long)conn.wlanAssociationAttributes.ulTxRate * 1000;
                details.LinkSpeedString = FormatSpeed(details.ReceiveLinkSpeedBps);

                // Query associated IP properties from NetworkInterface
                EnrichWithIpProperties(details);
            }

            return details;
        }
        catch
        {
            details.IsConnected = false;
            return details;
        }
        finally
        {
            if (ppData != IntPtr.Zero)
            {
                WlanNative.WlanFreeMemory(ppData);
            }
        }
    }

    public string? GetConnectedSsidFast()
    {
        if (!_hasWifiAdapter || _primaryInterfaceGuid == Guid.Empty || _clientHandle == IntPtr.Zero)
        {
            return null;
        }

        IntPtr ppData = IntPtr.Zero;
        try
        {
            int result = WlanNative.WlanQueryInterface(
                _clientHandle,
                ref _primaryInterfaceGuid,
                WlanNative.WLAN_INTF_OPCODE.wlan_intf_opcode_current_connection,
                IntPtr.Zero,
                out _,
                out ppData,
                out _);

            if (result != WlanNative.ERROR_SUCCESS || ppData == IntPtr.Zero)
            {
                return null;
            }

            var conn = Marshal.PtrToStructure<WlanNative.WLAN_CONNECTION_ATTRIBUTES>(ppData);
            if (conn.isState == WlanNative.WLAN_INTERFACE_STATE.wlan_interface_state_connected)
            {
                return FormatSsid(conn.wlanAssociationAttributes.dot11Ssid);
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (ppData != IntPtr.Zero)
            {
                WlanNative.WlanFreeMemory(ppData);
            }
        }
    }

    public IReadOnlyList<WifiNetworkItem> ScanAndGetAvailableNetworks(bool triggerScan = false)
    {
        var items = new List<WifiNetworkItem>();

        if (!_hasWifiAdapter || _primaryInterfaceGuid == Guid.Empty)
        {
            return items;
        }

        // Only trigger active radio frequency probe scan if explicitly requested (e.g. user clicked Refresh)
        if (triggerScan)
        {
            try
            {
                WlanNative.WlanScan(_clientHandle, ref _primaryInterfaceGuid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        IntPtr ppList = IntPtr.Zero;
        try
        {
            int result = WlanNative.WlanGetAvailableNetworkList(
                _clientHandle,
                ref _primaryInterfaceGuid,
                WlanNative.WLAN_AVAILABLE_NETWORK_INCLUDE_ALL_MANUAL_HIDDEN_PROFILES,
                IntPtr.Zero,
                out ppList);

            if (result != WlanNative.ERROR_SUCCESS || ppList == IntPtr.Zero)
            {
                return items;
            }

            uint count = (uint)Marshal.ReadInt32(ppList);
            if (count > 200) count = 200;
            int networkStructSize = Marshal.SizeOf<WlanNative.WLAN_AVAILABLE_NETWORK>();
            IntPtr currentPtr = IntPtr.Add(ppList, 8); // Skip dwNumberOfItems + dwIndex

            string? connectedSsid = GetConnectedSsidFast();
            var dict = new Dictionary<string, WifiNetworkItem>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < count; i++)
            {
                var net = Marshal.PtrToStructure<WlanNative.WLAN_AVAILABLE_NETWORK>(currentPtr);
                currentPtr = IntPtr.Add(currentPtr, networkStructSize);

                string ssid = FormatSsid(net.dot11Ssid);
                if (string.IsNullOrWhiteSpace(ssid))
                {
                    continue; // Skip hidden/unnamed SSIDs
                }

                int quality = (int)net.wlanSignalQuality;
                bool isConnected = !string.IsNullOrEmpty(connectedSsid) && string.Equals(connectedSsid, ssid, StringComparison.OrdinalIgnoreCase);
                bool hasProfile = !string.IsNullOrWhiteSpace(net.strProfileName);

                WifiStandard standard = WifiStandard.Unknown;
                if (net.uNumberOfPhyTypes > 0 && net.dot11PhyTypes != null)
                {
                    uint maxPhy = 0;
                    for (int p = 0; p < net.uNumberOfPhyTypes && p < net.dot11PhyTypes.Length; p++)
                    {
                        if (net.dot11PhyTypes[p] > maxPhy)
                        {
                            maxPhy = net.dot11PhyTypes[p];
                        }
                    }
                    standard = MapPhyTypeToStandard((WlanNative.DOT11_PHY_TYPE)maxPhy);
                }

                if (!dict.TryGetValue(ssid, out var existing) || quality > existing.SignalQuality || isConnected)
                {
                    dict[ssid] = new WifiNetworkItem
                    {
                        Ssid = ssid,
                        SignalQuality = quality,
                        IsConnected = isConnected,
                        IsProfileKnown = hasProfile,
                        Standard = standard,
                        SecurityType = MapAuthAlgorithm(net.dot11DefaultAuthAlgorithm),
                        AuthAlgorithm = net.dot11DefaultAuthAlgorithm,
                        CipherAlgorithm = net.dot11DefaultCipherAlgorithm
                    };
                }
            }

            return dict.Values
                .OrderByDescending(n => n.IsConnected)
                .ThenByDescending(n => n.SignalQuality)
                .ToList();
        }
        catch
        {
            return items;
        }
        finally
        {
            if (ppList != IntPtr.Zero)
            {
                WlanNative.WlanFreeMemory(ppList);
            }
        }
    }

    public bool QuickConnect(string profileName)
    {
        if (!_hasWifiAdapter || _primaryInterfaceGuid == Guid.Empty || string.IsNullOrWhiteSpace(profileName))
        {
            return false;
        }

        var param = new WlanNative.WLAN_CONNECTION_PARAMETERS
        {
            wlanConnectionMode = WlanNative.WLAN_CONNECTION_MODE.wlan_connection_mode_profile,
            strProfile = profileName,
            pDot11Ssid = IntPtr.Zero,
            pDesiredBssidList = IntPtr.Zero,
            dot11BssType = 1, // dot11_BSS_type_infrastructure
            dwFlags = 0
        };

        int result = WlanNative.WlanConnect(_clientHandle, ref _primaryInterfaceGuid, ref param, IntPtr.Zero);
        return result == WlanNative.ERROR_SUCCESS;
    }

    public bool Disconnect()
    {
        if (!_hasWifiAdapter || _primaryInterfaceGuid == Guid.Empty)
        {
            return false;
        }

        int result = WlanNative.WlanDisconnect(_clientHandle, ref _primaryInterfaceGuid, IntPtr.Zero);
        return result == WlanNative.ERROR_SUCCESS;
    }

    public bool ForgetProfile(string profileName)
    {
        if (!_hasWifiAdapter || _primaryInterfaceGuid == Guid.Empty || string.IsNullOrWhiteSpace(profileName))
        {
            return false;
        }

        int result = WlanNative.WlanDeleteProfile(_clientHandle, ref _primaryInterfaceGuid, profileName, IntPtr.Zero);
        return result == WlanNative.ERROR_SUCCESS;
    }

    public async Task<(bool Success, string Message)> ConnectAsync(
        string ssid,
        SecureString? securePassword,
        WlanNative.DOT11_AUTH_ALGORITHM authAlgo,
        WlanNative.DOT11_CIPHER_ALGORITHM cipherAlgo,
        bool isKnownProfile)
    {
        if (!_hasWifiAdapter || _primaryInterfaceGuid == Guid.Empty || string.IsNullOrWhiteSpace(ssid))
        {
            return (false, "No Wi-Fi adapter available.");
        }

        return await Task.Run(async () =>
        {
            // 1. If known profile, quick connect directly
            if (isKnownProfile)
            {
                if (QuickConnect(ssid))
                {
                    return await WaitForConnectionOutcomeAsync(ssid);
                }
                return (false, $"Failed to initiate connection to saved profile '{ssid}'.");
            }

            // 2. If Enterprise network, return informative message directly
            bool isEnterprise = authAlgo switch
            {
                WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_RSNA or
                WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_WPA or
                WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_WPA3 or
                WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_WPA3_ENT or
                WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_WPA3_ENT_192 => true,
                _ => false
            };

            if (isEnterprise)
            {
                return (false, "This network requires domain credentials (802.1X).");
            }

            // 3. Construct profile XML based on security type
            bool isOpen = authAlgo == WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_80211_OPEN;
            string profileXml;

            if (isOpen)
            {
                profileXml = BuildWifiProfileXml(ssid, null, "open", "none");
            }
            else
            {
                if (securePassword == null || securePassword.Length == 0)
                {
                    return (false, "Password cannot be empty.");
                }

                // Map auth algorithm to XML schema value
                string authString = authAlgo switch
                {
                    WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_WPA3_SAE => "WPA3SAE",
                    WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_WPA_PSK => "WPAPSK",
                    _ => "WPA2PSK"
                };

                // Map cipher algorithm to XML schema value
                string cipherString = cipherAlgo switch
                {
                    WlanNative.DOT11_CIPHER_ALGORITHM.DOT11_CIPHER_ALGO_TKIP => "TKIP",
                    _ => "AES"
                };

                // Marshal SecureString to unmanaged memory and zero out immediately
                IntPtr pPassword = Marshal.SecureStringToGlobalAllocUnicode(securePassword);
                char[]? rawChars = null;
                string escapedPassword;
                try
                {
                    int len = securePassword.Length;
                    rawChars = new char[len];
                    Marshal.Copy(pPassword, rawChars, 0, len);

                    var sb = new StringBuilder(len + 8);
                    for (int i = 0; i < len; i++)
                    {
                        char c = rawChars[i];
                        switch (c)
                        {
                            case '&': sb.Append("&amp;"); break;
                            case '<': sb.Append("&lt;"); break;
                            case '>': sb.Append("&gt;"); break;
                            case '"': sb.Append("&quot;"); break;
                            case '\'': sb.Append("&apos;"); break;
                            default: sb.Append(c); break;
                        }
                    }
                    escapedPassword = sb.ToString();
                }
                finally
                {
                    if (rawChars != null)
                    {
                        Array.Clear(rawChars, 0, rawChars.Length);
                    }
                    if (pPassword != IntPtr.Zero)
                    {
                        Marshal.ZeroFreeGlobalAllocUnicode(pPassword);
                    }
                }

                profileXml = BuildWifiProfileXml(ssid, escapedPassword, authString, cipherString);
                escapedPassword = null!;
            }

            // 4. Set Profile via WlanSetProfile (Try dwFlags=0, fallback to WLAN_PROFILE_USER if access denied)
            int setProfileRes = WlanNative.WlanSetProfile(
                _clientHandle,
                ref _primaryInterfaceGuid,
                0,
                profileXml,
                null,
                true,
                IntPtr.Zero,
                out uint reasonCode);

            if (setProfileRes == 5) // ERROR_ACCESS_DENIED
            {
                setProfileRes = WlanNative.WlanSetProfile(
                    _clientHandle,
                    ref _primaryInterfaceGuid,
                    WlanNative.WLAN_PROFILE_USER,
                    profileXml,
                    null,
                    true,
                    IntPtr.Zero,
                    out reasonCode);
            }

            if (setProfileRes != WlanNative.ERROR_SUCCESS)
            {
                return (false, $"Failed to create Wi-Fi profile (Code {setProfileRes}, reason {reasonCode}).");
            }

            // 5. Connect to newly created profile
            if (!QuickConnect(ssid))
            {
                return (false, $"Profile created, but failed to initiate connection to '{ssid}'.");
            }

            return await WaitForConnectionOutcomeAsync(ssid);
        });
    }

    private async Task<(bool Success, string Message)> WaitForConnectionOutcomeAsync(string ssid)
    {
        for (int i = 0; i < 12; i++)
        {
            await Task.Delay(500);
            var details = GetCurrentConnectionDetails();
            if (details.IsConnected && string.Equals(details.Ssid, ssid, StringComparison.OrdinalIgnoreCase))
            {
                return (true, $"Connected to {ssid}.");
            }
        }

        var finalDetails = GetCurrentConnectionDetails();
        if (finalDetails.IsConnected && string.Equals(finalDetails.Ssid, ssid, StringComparison.OrdinalIgnoreCase))
        {
            return (true, $"Connected to {ssid}.");
        }

        return (false, $"Connection to '{ssid}' timed out or could not be established.");
    }

    private static string BuildWifiProfileXml(string ssid, string? passwordXmlEscaped, string authAlgo, string cipherAlgo)
    {
        string hexSsid = Convert.ToHexString(Encoding.UTF8.GetBytes(ssid));
        string escapedSsid = EscapeXml(ssid);

        if (string.IsNullOrEmpty(passwordXmlEscaped))
        {
            return $"""
            <?xml version="1.0"?>
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
                <name>{escapedSsid}</name>
                <SSIDConfig>
                    <SSID>
                        <hex>{hexSsid}</hex>
                        <name>{escapedSsid}</name>
                    </SSID>
                </SSIDConfig>
                <connectionType>ESS</connectionType>
                <connectionMode>auto</connectionMode>
                <MSM>
                    <security>
                        <authEncryption>
                            <authentication>open</authentication>
                            <encryption>none</encryption>
                            <useOneX>false</useOneX>
                        </authEncryption>
                    </security>
                </MSM>
            </WLANProfile>
            """;
        }

        return $"""
        <?xml version="1.0"?>
        <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
            <name>{escapedSsid}</name>
            <SSIDConfig>
                <SSID>
                    <hex>{hexSsid}</hex>
                    <name>{escapedSsid}</name>
                </SSID>
            </SSIDConfig>
            <connectionType>ESS</connectionType>
            <connectionMode>auto</connectionMode>
            <MSM>
                <security>
                    <authEncryption>
                        <authentication>{authAlgo}</authentication>
                        <encryption>{cipherAlgo}</encryption>
                        <useOneX>false</useOneX>
                    </authEncryption>
                    <sharedKey>
                        <keyType>passPhrase</keyType>
                        <protected>false</protected>
                        <keyMaterial>{passwordXmlEscaped}</keyMaterial>
                    </sharedKey>
                </security>
            </MSM>
        </WLANProfile>
        """;
    }

    private static string EscapeXml(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        return input
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    private void EnrichWithIpProperties(WifiConnectionDetails details)
    {
        try
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces();
            string primaryGuidB = _primaryInterfaceGuid != Guid.Empty ? _primaryInterfaceGuid.ToString("B") : string.Empty;
            string primaryGuidD = _primaryInterfaceGuid != Guid.Empty ? _primaryInterfaceGuid.ToString("D") : string.Empty;

            // 1. Primary: Match exact tracked interface GUID
            var wifiNic = nics.FirstOrDefault(nic =>
                !string.IsNullOrEmpty(primaryGuidB) &&
                (string.Equals(nic.Id, primaryGuidB, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(nic.Id, primaryGuidD, StringComparison.OrdinalIgnoreCase)));

            // 2. Fallback: Active Wireless80211 NIC that is Up
            wifiNic ??= nics.FirstOrDefault(nic =>
                nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
                nic.OperationalStatus == OperationalStatus.Up);

            if (wifiNic != null)
            {
                var ipProps = wifiNic.GetIPProperties();
                var ipv4 = ipProps.UnicastAddresses
                    .FirstOrDefault(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork);
                if (ipv4 != null)
                {
                    details.IpAddress = ipv4.Address.ToString();
                    details.SubnetMask = ipv4.IPv4Mask?.ToString() ?? "--";
                }

                var gw = ipProps.GatewayAddresses
                    .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                if (gw != null)
                {
                    details.Gateway = gw.Address.ToString();
                }

                foreach (var d in ipProps.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork))
                {
                    details.DnsServers.Add(d.ToString());
                }

                // Query uptime
                var ipv4Props = ipProps.GetIPv4Properties();
                if (ipv4Props != null)
                {
                    var row = new IpHelperInterop.MIB_IF_ROW2 { InterfaceIndex = (uint)ipv4Props.Index };
                    if (IpHelperInterop.GetIfEntry2(ref row) == IpHelperInterop.NO_ERROR)
                    {
                        ulong currentBootMs = IpHelperInterop.GetTickCount64();
                        if (row.LastChange == 0)
                        {
                            details.LinkDuration = TimeSpan.FromMilliseconds(currentBootMs);
                        }
                        else
                        {
                            ulong current100Ns = currentBootMs * 10_000UL;
                            if (current100Ns >= row.LastChange)
                            {
                                details.LinkDuration = TimeSpan.FromTicks((long)(current100Ns - row.LastChange));
                            }
                        }
                    }
                }
            }
        }
        catch { }
    }

    private static string FormatSsid(WlanNative.DOT11_SSID ssid)
    {
        if (ssid.uSSIDLength == 0 || ssid.ucSSID == null) return string.Empty;
        int len = (int)Math.Min(ssid.uSSIDLength, 32);
        return Encoding.UTF8.GetString(ssid.ucSSID, 0, len).Trim('\0');
    }

    private static WifiStandard MapPhyTypeToStandard(WlanNative.DOT11_PHY_TYPE phy)
    {
        return phy switch
        {
            WlanNative.DOT11_PHY_TYPE.dot11_phy_type_eht => WifiStandard.Wifi7,
            WlanNative.DOT11_PHY_TYPE.dot11_phy_type_he => WifiStandard.Wifi6,
            WlanNative.DOT11_PHY_TYPE.dot11_phy_type_vht => WifiStandard.Wifi5,
            WlanNative.DOT11_PHY_TYPE.dot11_phy_type_ht => WifiStandard.Wifi4,
            WlanNative.DOT11_PHY_TYPE.dot11_phy_type_erp or
            WlanNative.DOT11_PHY_TYPE.dot11_phy_type_hrdsss or
            WlanNative.DOT11_PHY_TYPE.dot11_phy_type_dsss => WifiStandard.Legacy,
            _ => WifiStandard.Unknown
        };
    }

    private static string MapAuthAlgorithm(WlanNative.DOT11_AUTH_ALGORITHM auth)
    {
        return auth switch
        {
            WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_WPA3_SAE => "WPA3-Personal",
            WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_OWE => "OWE",
            WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_WPA3 or
            WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_WPA3_ENT_192 or
            WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_WPA3_ENT => "WPA3-Enterprise",
            WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_RSNA_PSK => "WPA2-Personal",
            WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_RSNA => "WPA2-Enterprise",
            WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_WPA_PSK => "WPA-Personal",
            WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_WPA => "WPA-Enterprise",
            WlanNative.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_80211_OPEN => "Open",
            _ => "Secured"
        };
    }

    private static string FormatSpeed(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0) return "--";
        if (bitsPerSecond >= 1_000_000_000)
        {
            return $"{(double)bitsPerSecond / 1_000_000_000.0:0.#} Gbps";
        }
        if (bitsPerSecond >= 1_000_000)
        {
            return $"{(double)bitsPerSecond / 1_000_000.0:0.#} Mbps";
        }
        return $"{bitsPerSecond / 1_000} Kbps";
    }

    public void Dispose()
    {
        if (_clientHandle != IntPtr.Zero)
        {
            WlanNative.WlanCloseHandle(_clientHandle, IntPtr.Zero);
            _clientHandle = IntPtr.Zero;
        }
    }
}
