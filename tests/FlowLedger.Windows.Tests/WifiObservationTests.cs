using System.Runtime.InteropServices;
using FlowLedger.Application.Monitoring;
using FlowLedger.Domain.Accounting;
using FlowLedger.Infrastructure.Windows.Monitoring;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowLedger.Windows.Tests;

[TestClass]
public sealed class WifiObservationTests
{
    private static readonly Guid Adapter = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherAdapter = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Context = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [TestMethod]
    public void NotificationAndRouteProjectionLayoutsMatchWindowsSdk()
    {
        Assert.AreEqual(40, Marshal.SizeOf<NativeWifiNotification>());
        Assert.AreEqual(new IntPtr(32), Marshal.OffsetOf<NativeWifiNotification>(nameof(NativeWifiNotification.Data)));
        Assert.AreEqual(104, Marshal.SizeOf<NativeRouteRow>());
        Assert.AreEqual(new IntPtr(8), Marshal.OffsetOf<NativeRouteTable>(nameof(NativeRouteTable.First)));
        Assert.AreEqual(new IntPtr(40), Marshal.OffsetOf<NativeRouteRow>(nameof(NativeRouteRow.PrefixLength)));
    }

    [TestMethod]
    public void ConnectDisconnectAndReconnectKeepIncreasingTheBoundaryVersion()
    {
        var tracker = new WifiNotificationTracker();
        tracker.Accept(8, 9, Adapter);
        Assert.IsTrue(tracker.Read(Adapter).Transitioning);
        tracker.Accept(8, 10, Adapter);
        var connected = tracker.Read(Adapter);
        Assert.IsFalse(connected.Transitioning);
        tracker.Accept(8, 20, Adapter);
        tracker.Accept(8, 21, Adapter);
        tracker.Accept(8, 9, Adapter);
        tracker.Accept(8, 10, Adapter);
        Assert.IsTrue(tracker.Read(Adapter).Sequence > connected.Sequence);
        Assert.IsFalse(tracker.Read(Adapter).Transitioning);
    }

    [TestMethod]
    public void RoamingIsATransitionButSignalQualityAndScansAreNot()
    {
        var tracker = new WifiNotificationTracker();
        tracker.Accept(16, 8, Adapter);
        tracker.Accept(8, 7, Adapter);
        Assert.AreEqual(0L, tracker.Read(Adapter).Sequence);
        tracker.Accept(16, 5, Adapter);
        Assert.IsTrue(tracker.Read(Adapter).Transitioning);
        tracker.Accept(16, 6, Adapter);
        Assert.IsFalse(tracker.Read(Adapter).Transitioning);
        Assert.AreEqual(2L, tracker.Read(Adapter).Sequence);
    }

    [TestMethod]
    public void EventsForAnotherAdapterDoNotInvalidateThisAdapter()
    {
        var tracker = new WifiNotificationTracker();
        tracker.Accept(8, 9, OtherAdapter);
        Assert.AreEqual(0L, tracker.Read(Adapter).Sequence);
        Assert.IsFalse(tracker.Read(Adapter).Transitioning);
    }

    [TestMethod]
    public void UnknownAdapterNotificationsInvalidateEvenUnseenAdapters()
    {
        var tracker = new WifiNotificationTracker();
        tracker.Accept(8, 9, Guid.Empty);
        Assert.IsTrue(tracker.Read(Adapter).Transitioning);
        tracker.Accept(8, 10, Guid.Empty);
        Assert.IsFalse(tracker.Read(Adapter).Transitioning);
        Assert.AreEqual(2L, tracker.Read(Adapter).Sequence);
    }

    [TestMethod]
    public void StableBracketingProducesACandidateWithoutUsingApAsIdentity()
    {
        var probe = Probe();
        var result = WindowsWifiTrafficSource.CreateObservation(probe, probe, true);
        Assert.IsTrue(result.Endpoint.StableDuringRead);
        Assert.IsNotNull(result.Endpoint.Network);
        var otherAp = probe with { Connection = probe.Connection with { AccessPoint = "020202020202" } };
        Assert.AreEqual(WindowsWifiTrafficSource.Identity(probe.Interface, probe.Connection),
            WindowsWifiTrafficSource.Identity(otherAp.Interface, otherAp.Connection));
    }

    [TestMethod]
    public void NotificationDuringQueryMakesTheCounterEndpointUnstable()
    {
        var before = Probe();
        var after = before with { Stamp = new(1, false) };
        var result = WindowsWifiTrafficSource.CreateObservation(before, after, true);
        Assert.IsFalse(result.Endpoint.StableDuringRead);
    }

    [TestMethod]
    public void QueryOrApChangeDuringBracketMakesEndpointUnstable()
    {
        var before = Probe();
        var changedAp = before with { Connection = before.Connection with { AccessPoint = "020202020202" } };
        var denied = before with { Connection = new(WifiConnectionState.AccessDenied, ErrorCode: 5) };
        Assert.IsFalse(WindowsWifiTrafficSource.CreateObservation(before, changedAp, true).Endpoint.StableDuringRead);
        Assert.IsFalse(WindowsWifiTrafficSource.CreateObservation(before, denied, true).Endpoint.StableDuringRead);
    }

    [TestMethod]
    public void MissingWindowsContextDoesNotInventAStableNetworkIdentity()
    {
        var probe = Probe();
        Assert.IsNull(WindowsWifiTrafficSource.Identity(probe.Interface with { NetworkContext = Guid.Empty }, probe.Connection));
    }

    [TestMethod]
    public void ChangedContextDuringQueryIsNotAssignedToTheOldNetwork()
    {
        var before = Probe();
        var after = before with { Interface = before.Interface with { NetworkContext = OtherAdapter } };
        Assert.IsFalse(WindowsWifiTrafficSource.CreateObservation(before, after, true).Endpoint.StableDuringRead);
    }

    [TestMethod]
    public void TransitionsAndMissingBeforeObservationFailClosed()
    {
        var probe = Probe();
        var transitioning = probe with { Stamp = new(0, true) };
        Assert.IsFalse(WindowsWifiTrafficSource.CreateObservation(transitioning, transitioning, true).Endpoint.StableDuringRead);
        Assert.IsFalse(WindowsWifiTrafficSource.CreateObservation(null, probe, true).Endpoint.StableDuringRead);
        Assert.IsFalse(WindowsWifiTrafficSource.CreateObservation(probe, probe, false).Endpoint.NotificationsComplete);
    }

    [TestMethod]
    public void RouteEvidenceReadsWithoutChangingNetworkSettings()
    {
        var result = new WindowsRouteEvidenceReader().Read();
        Assert.AreEqual(0u, result.ErrorCode);
        Assert.IsNotEmpty(result.Interfaces);
        Assert.IsTrue(result.Interfaces.All(row => row.Ipv4DefaultRoutes >= 0 && row.Ipv6DefaultRoutes >= 0 && row.OtherPrefixRoutes >= 0));
    }

    private static WifiProbe Probe()
    {
        var counter = new InterfaceCounter(Adapter.ToString("D"), "Synthetic adapter", InterfaceKind.Hardware, 71,
            true, true, true, "counter-identity", new CounterReading(Adapter.ToString("D"), Context,
                DateTimeOffset.UnixEpoch, new(100, 50)), Context, 1);
        var connection = new WifiConnection(WifiConnectionState.Available, "Synthetic WLAN", SsidHex: "41",
            ProfileName: "Synthetic profile", AccessPoint: "010101010101", BssType: 1, Authentication: 7, Cipher: 4);
        return new(counter, connection, new(0, false));
    }
}
