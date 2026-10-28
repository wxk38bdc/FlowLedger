using FlowLedger.Application.Monitoring;
using FlowLedger.Domain.Accounting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace FlowLedger.Application.Tests;

[TestClass]
public sealed class InterfaceMonitorTests
{
    private static readonly Guid Epoch = Guid.Parse("98b04e15-fd8d-420a-ade5-63d41422f561");
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void PhysicalTunnelAndFilterAreNeverAddedTogether()
    {
        var monitor = new InterfaceMonitor();
        monitor.Apply(Snapshot(0, Item("a", 100, 0), Item("t", 100, 0, InterfaceKind.Tunnel), Item("f", 100, 0, InterfaceKind.Filter)));
        var result = monitor.Apply(Snapshot(1, Item("a", 200, 1), Item("t", 200, 1, InterfaceKind.Tunnel), Item("f", 200, 1, InterfaceKind.Filter)));
        Assert.HasCount(3, result.Interfaces);
        Assert.IsTrue(result.Interfaces.All(item => item.Usage.SessionTotal == new TrafficVolume(100, 50)));
    }

    [TestMethod]
    public void RemovedAndReappearingInterfaceDoesNotBridgeGap()
    {
        var monitor = new InterfaceMonitor();
        monitor.Apply(Snapshot(0, Item("a", 100, 0)));
        var removed = monitor.Apply(Snapshot(1));
        var restored = monitor.Apply(Snapshot(2, Item("a", 1000, 2)));
        Assert.IsFalse(removed.Interfaces[0].IsPresent);
        Assert.AreEqual(TrafficSampleStatus.Unavailable, removed.Interfaces[0].Usage.Status);
        Assert.AreEqual(TrafficSampleStatus.Baseline, restored.Interfaces[0].Usage.Status);
        Assert.IsNull(restored.Interfaces[0].Usage.SessionTotal);
    }

    [TestMethod]
    public void FailedEnumerationInvalidatesEveryInterface()
    {
        var monitor = new InterfaceMonitor();
        monitor.Apply(Snapshot(0, Item("a", 100, 0), Item("b", 100, 0)));
        var result = monitor.Apply(new(Start.AddSeconds(1), TimeSpan.FromSeconds(1), [], 5));
        Assert.AreEqual(5u, result.ErrorCode);
        Assert.IsTrue(result.Interfaces.All(item => !item.IsPresent && item.Usage.Interval is null));
    }

    [TestMethod]
    public void SuspendNotificationInvalidatesAllBaselines()
    {
        var monitor = new InterfaceMonitor();
        monitor.Apply(Snapshot(0, Item("a", 100, 0)));
        monitor.BreakContinuity();
        var result = monitor.Apply(Snapshot(1, Item("a", 1000, 1)));
        Assert.AreEqual(TrafficSampleStatus.Baseline, result.Interfaces[0].Usage.Status);
        Assert.IsNull(result.Interfaces[0].Usage.SessionTotal);
        Assert.AreEqual(1, result.Interfaces[0].Usage.Discontinuities);
    }

    [TestMethod]
    public void DifferentInterfacesKeepSeparateBaselinesAndTotals()
    {
        var monitor = new InterfaceMonitor();
        monitor.Apply(Snapshot(0, Item("a", 100, 0), Item("b", 10000, 0)));
        var result = monitor.Apply(Snapshot(1, Item("a", 150, 1), Item("b", 10500, 1)));
        Assert.AreEqual<TrafficVolume?>(new(50, 25), result.Interfaces.Single(item => item.Interface.Id == "a").Usage.SessionTotal);
        Assert.AreEqual<TrafficVolume?>(new(500, 250), result.Interfaces.Single(item => item.Interface.Id == "b").Usage.SessionTotal);
    }

    private static InterfaceCounterSnapshot Snapshot(int second, params InterfaceCounter[] items) =>
        new(Start.AddSeconds(second), TimeSpan.FromSeconds(second), items);

    private static InterfaceCounter Item(string id, ulong bytes, int second, InterfaceKind kind = InterfaceKind.Hardware) =>
        new(id, "Synthetic adapter", kind, 6, true, true, true, "connection-a",
            new CounterReading(id, Epoch, Start.AddSeconds(second), new(bytes, bytes / 2)));
}
