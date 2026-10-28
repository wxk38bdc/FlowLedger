using System.Diagnostics;
using System.Runtime.InteropServices;
using FlowLedger.Application.Monitoring;
using FlowLedger.Domain.Accounting;

namespace FlowLedger.Infrastructure.Windows.Monitoring;

public sealed class WindowsInterfaceCounterReader : IInterfaceCounterReader
{
    private readonly Guid epoch = Guid.NewGuid();
    private readonly long started = Stopwatch.GetTimestamp();

    public InterfaceCounterSnapshot Read()
    {
        var error = NativeNetworkMethods.GetIfTable2(out var table);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var elapsed = Stopwatch.GetElapsedTime(started);
            if (error != 0)
            {
                return new(now, elapsed, [], error);
            }

            var count = checked((uint)Marshal.ReadInt32(table));
            var offset = checked((int)Marshal.OffsetOf<NativeInterfaceTable>(nameof(NativeInterfaceTable.First)));
            var stride = Marshal.SizeOf<NativeInterfaceRow>();
            var interfaces = new List<InterfaceCounter>();
            for (uint i = 0; i < count; i++)
            {
                var address = IntPtr.Add(table, checked(offset + (int)i * stride));
                var row = Marshal.PtrToStructure<NativeInterfaceRow>(address);
                var id = row.Guid != Guid.Empty ? row.Guid.ToString("D") : $"luid-{row.Luid:x16}";
                interfaces.Add(new(id, row.Alias, Classify(row.Type, row.Flags), row.Type,
                    row.OperStatus == 1 && row.MediaConnectState != 2 && (row.Flags & 0x78) == 0,
                    (row.Flags & 1) != 0, (row.Flags & 4) != 0,
                    $"{row.Luid:x16}/{row.NetworkGuid:D}/{row.Index}",
                    new CounterReading(id, epoch, now, new TrafficVolume(row.InOctets, row.OutOctets))));
            }

            return new(now, elapsed, interfaces);
        }
        finally
        {
            if (table != IntPtr.Zero)
            {
                NativeNetworkMethods.FreeMibTable(table);
            }
        }
    }

    internal static InterfaceKind Classify(uint type, byte flags) =>
        (flags & 2) != 0 ? InterfaceKind.Filter
        : type == 24 ? InterfaceKind.Loopback
        : type == 131 ? InterfaceKind.Tunnel
        : (flags & 1) != 0 && (flags & 128) == 0 ? InterfaceKind.Hardware
        : InterfaceKind.VirtualOrOther;
}
