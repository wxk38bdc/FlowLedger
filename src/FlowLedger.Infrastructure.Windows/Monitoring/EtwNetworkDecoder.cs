using System.Net;
using System.Runtime.InteropServices;
using FlowLedger.Domain.Ssh;

namespace FlowLedger.Infrastructure.Windows.Monitoring;

/// <summary>Modern classic TCP/UDP v2 metadata starts with UINT32 PID and UINT32 size.</summary>
internal static class EtwNetworkDecoder
{
    internal static bool TryReadFlow(int version, IntPtr metadata, int length, bool ipv6, out TcpFlow? flow)
    {
        flow = null;
        var addressLength = ipv6 ? 16 : 4;
        var minimumLength = 8 + addressLength * 2 + 4;
        if (version != 2 || metadata == IntPtr.Zero || length < minimumLength) { return false; }
        var destination = new byte[addressLength];
        var source = new byte[addressLength];
        Marshal.Copy(IntPtr.Add(metadata, 8), destination, 0, addressLength);
        Marshal.Copy(IntPtr.Add(metadata, 8 + addressLength), source, 0, addressLength);
        var offset = 8 + addressLength * 2;
        var destinationPort = (Marshal.ReadByte(metadata, offset) << 8) | Marshal.ReadByte(metadata, offset + 1);
        var sourcePort = (Marshal.ReadByte(metadata, offset + 2) << 8) | Marshal.ReadByte(metadata, offset + 3);
        if (sourcePort == 0 || destinationPort == 0) { return false; }
        var sourceAddress = new IPAddress(source).ToString();
        var destinationAddress = new IPAddress(destination).ToString();
        // Classic TCP v2 describes the owning connection: saddr/sport are local for both send
        // and receive. Confirmed against controlled OpenSSH IPv4/IPv6 socket delivery, not
        // inferred from packet direction or from a remote-port heuristic.
        flow = new(sourceAddress, sourcePort, destinationAddress, destinationPort);
        return true;
    }

    internal static bool TryRead(int version, IntPtr metadata, int length, out int pid, out uint size)
    {
        pid = 0;
        size = 0;
        if (version != 2 || metadata == IntPtr.Zero || length < 8)
        {
            return false;
        }

        // Read the payload PID explicitly, regardless of the event header's execution context.
        pid = Marshal.ReadInt32(metadata);
        size = unchecked((uint)Marshal.ReadInt32(metadata, 4));
        return true;
    }
}
