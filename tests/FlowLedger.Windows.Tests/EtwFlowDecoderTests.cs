using System.Net;
using System.Runtime.InteropServices;
using FlowLedger.Domain.Ssh;
using FlowLedger.Infrastructure.Windows.Monitoring;

namespace FlowLedger.Windows.Tests;

[TestClass]
public sealed class EtwFlowDecoderTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TcpMetadataUsesConnectionOrientationAndNetworkByteOrder(bool ipv6)
    {
        var local = IPAddress.Parse(ipv6 ? "2001:db8::1" : "192.0.2.1");
        var remote = IPAddress.Parse(ipv6 ? "2001:db8::2" : "192.0.2.2");
        var count = ipv6 ? 16 : 4; var length = 8 + count * 2 + 4; var bytes = new byte[length];
        remote.GetAddressBytes().CopyTo(bytes, 8); local.GetAddressBytes().CopyTo(bytes, 8 + count);
        var offset = 8 + count * 2; bytes[offset] = 0x08; bytes[offset + 1] = 0xAE; bytes[offset + 2] = 0xC3; bytes[offset + 3] = 0x50;
        var pointer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, length);
            Assert.IsTrue(EtwNetworkDecoder.TryReadFlow(2, pointer, length, ipv6, out var flow));
            Assert.AreEqual(new TcpFlow(local.ToString(), 50000, remote.ToString(), 2222), flow);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    [TestMethod]
    public void TruncatedOrUnknownLayoutsNeverReadMemory()
    {
        Assert.IsFalse(EtwNetworkDecoder.TryReadFlow(1, new(1), 200, false, out _));
        Assert.IsFalse(EtwNetworkDecoder.TryReadFlow(2, new(1), 19, false, out _));
        Assert.IsFalse(EtwNetworkDecoder.TryReadFlow(2, new(1), 43, true, out _));
        Assert.IsFalse(EtwNetworkDecoder.TryReadFlow(2, IntPtr.Zero, 200, true, out _));
    }
}
