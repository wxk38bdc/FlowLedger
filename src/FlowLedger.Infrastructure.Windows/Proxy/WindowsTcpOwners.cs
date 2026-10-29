using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using FlowLedger.Domain.Proxy;

namespace FlowLedger.Infrastructure.Windows.Proxy;

internal sealed record TcpOwnerRow(string LocalAddress, int LocalPort, string RemoteAddress, int RemotePort, uint State, ProxyProcess? Process);
internal sealed record TcpOwnerSnapshot(bool Available, IReadOnlyList<TcpOwnerRow> Rows)
{
    internal ProxyProcess? Controller(int port)
    {
        var owners = Rows.Where(row => row.State == 2 && row.LocalPort == port &&
            (IPAddress.TryParse(row.LocalAddress, out var ip) && (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))))
            .Select(row => row.Process).Distinct().ToArray();
        return Available && owners.Length == 1 ? owners[0] : null;
    }
}

internal interface ITcpOwnerReader { TcpOwnerSnapshot Read(); }

internal sealed class WindowsTcpOwners : ITcpOwnerReader
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool sorted,
        uint family, uint tableClass, uint reserved);

    public TcpOwnerSnapshot Read()
    {
        var rows = new List<TcpOwnerRow>();
        var processes = new Dictionary<int, ProxyProcess?>();
        try
        {
            foreach (var family in new uint[] { 2, 23 })
            {
                uint size = 0;
                var status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 5, 0);
                if (status != 122 || size is < 4 or > 16777216) { return new(false, []); }
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var capacity = size;
                    var table = Marshal.AllocHGlobal(checked((int)capacity));
                    try
                    {
                        status = GetExtendedTcpTable(table, ref size, false, family, 5, 0);
                        if (status == 122 && size <= 16777216) { continue; }
                        if (status != 0 || size > capacity) { return new(false, []); }
                        var count = Marshal.ReadInt32(table);
                        var rowSize = family == 2 ? 24 : 56;
                        if (count < 0 || 4L + (long)count * rowSize > capacity) { return new(false, []); }
                        for (var index = 0; index < count; index++)
                        {
                            var pointer = IntPtr.Add(table, 4 + index * rowSize);
                            var pid = Marshal.ReadInt32(pointer, family == 2 ? 20 : 52);
                            if (!processes.TryGetValue(pid, out var process)) { process = ProcessIdentity(pid); processes[pid] = process; }
                            var local = Address(pointer, family == 2 ? 4 : 0, family);
                            var remote = Address(pointer, family == 2 ? 12 : 24, family);
                            rows.Add(new(local, Port(pointer, family == 2 ? 8 : 20), remote, Port(pointer, family == 2 ? 16 : 44),
                                unchecked((uint)Marshal.ReadInt32(pointer, family == 2 ? 0 : 48)), process));
                        }

                        break;
                    }
                    finally { Marshal.FreeHGlobal(table); }
                }

                if (status != 0) { return new(false, []); }
            }

            return new(true, rows);
        }
        catch (Exception exception) when (exception is Win32Exception or OverflowException or ArgumentException) { return new(false, []); }
    }

    private static ProxyProcess? ProcessIdentity(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return new(pid, process.StartTime.ToUniversalTime().Ticks, process.ProcessName);
        }
        catch (Exception exception) when (exception is Win32Exception or ArgumentException or InvalidOperationException or NotSupportedException) { return null; }
    }

    private static string Address(IntPtr row, int offset, uint family)
    {
        var bytes = new byte[family == 2 ? 4 : 16];
        Marshal.Copy(IntPtr.Add(row, offset), bytes, 0, bytes.Length);
        return (family == 2 ? new IPAddress(bytes) : new IPAddress(bytes, unchecked((uint)Marshal.ReadInt32(row, offset + 16)))).ToString();
    }

    private static int Port(IntPtr row, int offset) => (Marshal.ReadByte(row, offset) << 8) | Marshal.ReadByte(row, offset + 1);
}
