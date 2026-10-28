using System.Runtime.InteropServices;
using FlowLedger.Application.Monitoring;
using FlowLedger.Infrastructure.Windows.Monitoring;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace FlowLedger.Windows.Tests;

[TestClass]
public sealed class WindowsInteropTests
{
    [TestMethod]
    public void InterfaceCounterLayoutMatchesWindowsSdkAbi()
    {
        Assert.AreEqual(1352, Marshal.SizeOf<NativeInterfaceRow>());
        Assert.AreEqual(new IntPtr(8), Marshal.OffsetOf<NativeInterfaceTable>(nameof(NativeInterfaceTable.First)));
        Assert.AreEqual(new IntPtr(1152), Marshal.OffsetOf<NativeInterfaceRow>(nameof(NativeInterfaceRow.Flags)));
        Assert.AreEqual(new IntPtr(1208), Marshal.OffsetOf<NativeInterfaceRow>(nameof(NativeInterfaceRow.InOctets)));
        Assert.AreEqual(new IntPtr(1280), Marshal.OffsetOf<NativeInterfaceRow>(nameof(NativeInterfaceRow.OutOctets)));
    }

    [TestMethod]
    public void WifiConnectionLayoutMatchesWindowsSdkAbi()
    {
        Assert.AreEqual(604, Marshal.SizeOf<NativeWifiConnection>());
        Assert.AreEqual(new IntPtr(520), Marshal.OffsetOf<NativeWifiConnection>(nameof(NativeWifiConnection.Association)));
        Assert.AreEqual(68, Marshal.SizeOf<NativeWifiAssociation>());
    }

    [TestMethod]
    [DataRow(71u, (byte)7, InterfaceKind.Filter)]
    [DataRow(24u, (byte)0, InterfaceKind.Loopback)]
    [DataRow(131u, (byte)0, InterfaceKind.Tunnel)]
    [DataRow(71u, (byte)5, InterfaceKind.Hardware)]
    [DataRow(6u, (byte)128, InterfaceKind.VirtualOrOther)]
    [DataRow(53u, (byte)0, InterfaceKind.VirtualOrOther)]
    public void NativeFlagsRatherThanNamesDetermineInterfaceKind(uint type, byte flags, InterfaceKind expected) =>
        Assert.AreEqual(expected, WindowsInterfaceCounterReader.Classify(type, flags));

    [TestMethod]
    [DataRow(5u, WifiConnectionState.AccessDenied)]
    [DataRow(5023u, WifiConnectionState.Disconnected)]
    [DataRow(1062u, WifiConnectionState.Unavailable)]
    public void WifiFailuresAreExplicitAndNeverKeepAnOldName(uint code, WifiConnectionState expected)
    {
        var result = WindowsWifiConnectionReader.Failure(code);
        Assert.AreEqual(expected, result.State);
        Assert.IsNull(result.DisplayName);
    }

    [TestMethod]
    public void SsidDisplayPreservesInvalidEncodingWithoutGuessing()
    {
        Assert.AreEqual("Sample Wi-Fi", WindowsWifiConnectionReader.DisplaySsid("Sample Wi-Fi"u8));
        Assert.AreEqual("0xFF00", WindowsWifiConnectionReader.DisplaySsid([255, 0]));
        Assert.AreEqual("0x410A42", WindowsWifiConnectionReader.DisplaySsid([65, 10, 66]));
    }

    [TestMethod]
    public void NativeEnumerationCanReadWithoutAServiceOrDriver()
    {
        var result = new WindowsInterfaceCounterReader().Read();
        Assert.AreEqual(0u, result.ErrorCode);
        Assert.IsNotEmpty(result.Interfaces);
        Assert.AreEqual(result.Interfaces.Count, result.Interfaces.Select(item => item.Id).Distinct().Count());
        Assert.IsTrue(result.Interfaces.Any(item => item.Kind == InterfaceKind.Loopback));
    }
}
