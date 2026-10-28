using System.Runtime.InteropServices;
using FlowLedger.Application.Monitoring;

namespace FlowLedger.Infrastructure.Windows.Monitoring;

public sealed class WindowsRouteEvidenceReader : IRouteEvidenceReader
{
    public RouteEvidence Read()
    {
        var error = NativeNetworkMethods.GetIpForwardTable2(0, out var table);
        try
        {
            if (error != 0)
            {
                return new([], error);
            }

            var count = checked((uint)Marshal.ReadInt32(table));
            var offset = checked((int)Marshal.OffsetOf<NativeRouteTable>(nameof(NativeRouteTable.First)));
            var stride = Marshal.SizeOf<NativeRouteRow>();
            var rows = new List<NativeRouteRow>();
            for (uint i = 0; i < count; i++)
            {
                rows.Add(Marshal.PtrToStructure<NativeRouteRow>(IntPtr.Add(table, checked(offset + (int)i * stride))));
            }

            return new(rows.GroupBy(row => row.Luid).Select(group => new InterfaceRouteEvidence(
                group.Key, group.Count(row => row.DestinationFamily == 2 && row.PrefixLength == 0),
                group.Count(row => row.DestinationFamily == 23 && row.PrefixLength == 0),
                group.Count(row => row.PrefixLength != 0))).ToArray());
        }
        finally
        {
            if (table != IntPtr.Zero)
            {
                NativeNetworkMethods.FreeMibTable(table);
            }
        }
    }
}

// MIB_IPFORWARD_ROW2 from the Windows SDK. Only identity/family/prefix-length
// fields are exposed; no gateway or destination address fields are parsed or returned.
[StructLayout(LayoutKind.Explicit, Size = 104)]
internal struct NativeRouteRow
{
    [FieldOffset(0)] public ulong Luid;
    [FieldOffset(8)] public uint Index;
    [FieldOffset(12)] public ushort DestinationFamily;
    [FieldOffset(40)] public byte PrefixLength;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRouteTable
{
    public uint Count;
    public NativeRouteRow First;
}
