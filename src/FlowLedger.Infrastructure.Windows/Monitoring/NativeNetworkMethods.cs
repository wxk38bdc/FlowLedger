using System.Runtime.InteropServices;

namespace FlowLedger.Infrastructure.Windows.Monitoring;

internal static class NativeNetworkMethods
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern uint GetIfTable2(out IntPtr table);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern void FreeMibTable(IntPtr table);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    internal static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    internal static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    internal static extern uint WlanQueryInterface(
        IntPtr handle, in Guid interfaceId, uint opcode, IntPtr reserved,
        out uint dataSize, out IntPtr data, out uint valueType);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    internal static extern void WlanFreeMemory(IntPtr memory);
}

// Windows SDK netioapi.h MIB_IF_ROW2. All scalar widths and default 8-byte packing
// are intentional; tests verify size and offsets against the Windows SDK ABI.
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NativeInterfaceRow
{
    public ulong Luid;
    public uint Index;
    public Guid Guid;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)]
    public string Alias;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)]
    public string Description;
    public uint PhysicalAddressLength;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public byte[] PhysicalAddress;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public byte[] PermanentPhysicalAddress;
    public uint Mtu;
    public uint Type;
    public uint TunnelType;
    public uint MediaType;
    public uint PhysicalMediumType;
    public uint AccessType;
    public uint DirectionType;
    public byte Flags;
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
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeInterfaceTable
{
    public uint Count;
    public NativeInterfaceRow First;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NativeWifiConnection
{
    public uint State;
    public uint Mode;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string Profile;
    public NativeWifiAssociation Association;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public uint[] Security;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeWifiAssociation
{
    public uint SsidLength;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public byte[] Ssid;
    public uint BssType;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
    public byte[] Bssid;
    public uint PhyType;
    public uint PhyIndex;
    public uint SignalQuality;
    public uint RxRate;
    public uint TxRate;
}
