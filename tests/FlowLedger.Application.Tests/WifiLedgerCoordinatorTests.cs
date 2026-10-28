using FlowLedger.Application.Monitoring;
using FlowLedger.Domain.Accounting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowLedger.Application.Tests;

[TestClass]
public sealed class WifiLedgerCoordinatorTests
{
    private static readonly Guid Context = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly WifiNetworkIdentity Network = new("41", "synthetic", Context, 1, 7, 4);

    [TestMethod]
    public void MultipleWifiAdaptersKeepIndependentPartitions()
    {
        var monitor = new InterfaceMonitor();
        var coordinator = new WifiLedgerCoordinator();
        var first = Sample(0, 100, 1000);
        coordinator.Apply(first, monitor.Apply(first.Counters));
        var second = Sample(1, 120, 1100);
        var result = coordinator.Apply(second, monitor.Apply(second.Counters));
        Assert.AreEqual<TrafficVolume?>(new(20, 10), result["a"].ObservedTotal);
        Assert.AreEqual<TrafficVolume?>(new(100, 50), result["b"].ObservedTotal);
    }

    [TestMethod]
    public void SuspendAndCounterBaselineDoNotCreateUnallocatedPhantomBytes()
    {
        var monitor = new InterfaceMonitor();
        var coordinator = new WifiLedgerCoordinator();
        var first = Sample(0, 100, 1000);
        coordinator.Apply(first, monitor.Apply(first.Counters));
        monitor.BreakContinuity();
        coordinator.BreakContinuity();
        var after = Sample(60, 10000, 20000);
        var result = coordinator.Apply(after, monitor.Apply(after.Counters));
        Assert.IsNull(result["a"].ObservedTotal);
        Assert.AreEqual(default(TrafficVolume), result["a"].BoundaryUnallocated);
    }

    [TestMethod]
    public void FailedEnumerationCannotReuseAFormerNetworkName()
    {
        var monitor = new InterfaceMonitor();
        var coordinator = new WifiLedgerCoordinator();
        var first = Sample(0, 100, 1000);
        coordinator.Apply(first, monitor.Apply(first.Counters));
        var failed = new WifiTrafficSample(new(DateTimeOffset.UnixEpoch.AddSeconds(1), TimeSpan.FromSeconds(1), [], 5),
            new Dictionary<string, WifiTrafficObservation>(), WifiNotificationCoverage.Full, 0);
        var result = coordinator.Apply(failed, monitor.Apply(failed.Counters));
        Assert.IsNull(result["a"].ObservedTotal);
        Assert.HasCount(0, result["a"].Networks);
    }

    [TestMethod]
    public void NetworkContextChangeStillPreservesInterfaceBytesForBoundaryAllocation()
    {
        var monitor = new InterfaceMonitor();
        var coordinator = new WifiLedgerCoordinator();
        var first = Sample(0, 100, 1000);
        coordinator.Apply(first, monitor.Apply(first.Counters));
        var next = Sample(1, 120, 1100);
        var changed = next with
        {
            Observations = new Dictionary<string, WifiTrafficObservation>(next.Observations)
            {
                ["a"] = new(new(Network with { NetworkContext = Guid.Parse("22222222-2222-2222-2222-222222222222") },
                    "Synthetic WLAN", "AP", 1, true, true), WifiConnectionState.Available, 0),
            },
        };
        var monitored = monitor.Apply(changed.Counters);
        var result = coordinator.Apply(changed, monitored);
        Assert.AreEqual<TrafficVolume?>(new(20, 10), monitored.Interfaces.Single(row => row.Interface.Id == "a").Usage.Interval);
        Assert.AreEqual(new TrafficVolume(20, 10), result["a"].BoundaryUnallocated);
    }

    private static WifiTrafficSample Sample(int seconds, ulong aBytes, ulong bBytes)
    {
        var time = DateTimeOffset.UnixEpoch.AddSeconds(seconds);
        InterfaceCounter Item(string id, ulong bytes) => new(id, "Synthetic adapter", InterfaceKind.Hardware, 71,
            true, true, true, "counter-identity", new CounterReading(id, Context, time, new(bytes, bytes / 2)), Context);
        WifiTrafficObservation Observation() => new(new(Network, "Synthetic WLAN", "AP", 0, true, true), WifiConnectionState.Available, 0);
        return new(new(time, TimeSpan.FromSeconds(seconds), [Item("a", aBytes), Item("b", bBytes)]),
            new Dictionary<string, WifiTrafficObservation> { ["a"] = Observation(), ["b"] = Observation() }, WifiNotificationCoverage.Full, 0);
    }
}
