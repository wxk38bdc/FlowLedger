using System.Runtime.InteropServices;
using System.Security.Principal;
using FlowLedger.Application.Monitoring;
using FlowLedger.Infrastructure.Windows.Monitoring;

namespace FlowLedger.Windows.Tests;

[TestClass]
public sealed class EtwNetworkDecoderTests
{
    [TestMethod]
    public void ReadsUnsignedSizeAndPayloadPidFromMetadata()
    {
        var memory = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt32(memory, 12345);
            Marshal.WriteInt32(memory, 4, -1);
            Assert.IsTrue(EtwNetworkDecoder.TryRead(2, memory, 8, out var pid, out var size));
            Assert.AreEqual(12345, pid);
            Assert.AreEqual(uint.MaxValue, size);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    [TestMethod]
    public void RejectsUnknownVersionsAndTruncatedMetadataWithoutReadingPointer()
    {
        Assert.IsFalse(EtwNetworkDecoder.TryRead(0, new IntPtr(1), 100, out _, out _));
        Assert.IsFalse(EtwNetworkDecoder.TryRead(3, new IntPtr(1), 100, out _, out _));
        Assert.IsFalse(EtwNetworkDecoder.TryRead(2, new IntPtr(1), 7, out _, out _));
        Assert.IsFalse(EtwNetworkDecoder.TryRead(2, IntPtr.Zero, 100, out _, out _));
    }

    [TestMethod]
    public async Task OrdinaryUserDoesNotCreateOrElevateAnEtwSession()
    {
        if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Assert.Inconclusive("This test only exercises the ordinary-user path; it must not start a privileged session in CI.");
        }

        await using var source = new WindowsApplicationTrafficSource();
        var snapshot = source.Read();
        Assert.AreEqual(ApplicationCaptureState.AccessDenied, snapshot.State);
        Assert.AreEqual(5, snapshot.ErrorCode);
        Assert.IsNull(snapshot.Ledger.Observed);
        await Task.WhenAll(source.StopAsync(), source.StopAsync()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(ApplicationCaptureState.AccessDenied, source.Read().State);
    }
}
