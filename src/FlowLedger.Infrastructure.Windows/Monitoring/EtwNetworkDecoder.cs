using System.Runtime.InteropServices;

namespace FlowLedger.Infrastructure.Windows.Monitoring;

/// <summary>Modern classic TCP/UDP v2 metadata starts with UINT32 PID and UINT32 size.</summary>
internal static class EtwNetworkDecoder
{
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
