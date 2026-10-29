using FlowLedger.Domain.Accounting;
using FlowLedger.Domain.Proxy;

namespace FlowLedger.Domain.Tests;

[TestClass]
public sealed class ProxyUsageLedgerTests
{
    private static readonly Guid Epoch = Guid.NewGuid();
    private static readonly Guid Id = Guid.NewGuid();
    private static readonly ProxyProcess Owner = new(7, 1, "synthetic");
    private static ProxyConnectionSample Connection(ulong received, ulong sent, ProxyProcess? owner = null) =>
        new(Id, DateTimeOffset.UnixEpoch, new(received, sent), owner, ProxyIngress.ExplicitProxy, ProxyRoute.CoreProxy);
    private static ProxyObservation Sample(int second, ulong received, ulong sent, params ProxyConnectionSample[] connections) =>
        new(Epoch, DateTimeOffset.UnixEpoch.AddSeconds(second), TimeSpan.FromSeconds(second), new(received, sent), connections);

    [TestMethod]
    public void FirstSnapshotDoesNotCountPreEnableHistory()
    {
        var ledger = new ProxyUsageLedger();
        ledger.Observe(Sample(1, 10000, 20000, Connection(1000, 2000, Owner)));
        Assert.IsNull(ledger.Snapshot().CoreObserved);
        Assert.IsNull(ledger.Snapshot().ConnectionObserved);
        Assert.IsEmpty(ledger.Snapshot().Rows);
    }

    [TestMethod]
    public void OneConnectionIsCountedOnceWithoutAddingCoreOrOtherLayers()
    {
        var ledger = new ProxyUsageLedger();
        ledger.Observe(Sample(1, 10000, 20000, Connection(100, 200, Owner)));
        ledger.Observe(Sample(2, 10060, 20080, Connection(130, 240, Owner)));
        ledger.Observe(Sample(3, 10060, 20080, Connection(130, 240, Owner)));
        var result = ledger.Snapshot();
        Assert.AreEqual(new TrafficVolume(60, 80), result.CoreObserved);
        Assert.AreEqual(new TrafficVolume(30, 40), result.ConnectionObserved);
        Assert.AreEqual(new TrafficVolume(30, 40), result.Rows.Single().Traffic);
        Conserve(result);
    }

    [TestMethod]
    public void ChangedOwnerAndPidReuseNeverRenamePriorBytes()
    {
        var ledger = new ProxyUsageLedger();
        ledger.Observe(Sample(1, 100, 100, Connection(100, 100, Owner)));
        ledger.Observe(Sample(2, 200, 200, Connection(200, 200, Owner)));
        ledger.Observe(Sample(3, 300, 300, Connection(300, 300, Owner with { StartTicks = 2 })));
        var result = ledger.Snapshot();
        Assert.AreEqual(new TrafficVolume(100, 100), result.Rows.Single(row => row.Owner == Owner).Traffic);
        Assert.AreEqual(new TrafficVolume(100, 100), result.Rows.Single(row => row.Owner is null).Traffic);
        Conserve(result);
    }

    [TestMethod]
    public void FailureAndLongGapRequireNewBaselines()
    {
        var ledger = new ProxyUsageLedger();
        ledger.Observe(Sample(1, 100, 100, Connection(100, 100, Owner)));
        ledger.Observe(Sample(2, 200, 200, Connection(200, 200, Owner)));
        ledger.BreakContinuity();
        ledger.Observe(Sample(3, 500, 500, Connection(500, 500, Owner)));
        ledger.Observe(Sample(10, 1000, 1000, Connection(1000, 1000, Owner)));
        Assert.AreEqual(new TrafficVolume(100, 100), ledger.Snapshot().ConnectionObserved);
        Assert.AreEqual(2UL, ledger.Snapshot().Gaps);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void CoreEpochOrCounterResetNeverInventsDelta(bool epochChanged)
    {
        var ledger = new ProxyUsageLedger();
        ledger.Observe(Sample(1, 100, 100, Connection(100, 100)));
        var next = epochChanged ? Sample(2, 200, 200, Connection(200, 200)) with { CoreEpoch = Guid.NewGuid() } : Sample(2, 1, 1, Connection(200, 200));
        ledger.Observe(next);
        Assert.IsNull(ledger.Snapshot().CoreObserved);
        Assert.IsNull(ledger.Snapshot().ConnectionObserved);
        Assert.AreEqual(1UL, ledger.Snapshot().Resets);
    }

    [TestMethod]
    public void ConnectionResetAndIdReuseCreateBaselines()
    {
        var ledger = new ProxyUsageLedger();
        ledger.Observe(Sample(1, 100, 100, Connection(100, 100)));
        ledger.Observe(Sample(2, 200, 200, Connection(1, 1)));
        ledger.Observe(Sample(3, 300, 300, Connection(50, 50) with { Started = DateTimeOffset.UnixEpoch.AddSeconds(2) }));
        Assert.IsNull(ledger.Snapshot().ConnectionObserved);
        Assert.AreEqual(1UL, ledger.Snapshot().Resets);
    }

    [TestMethod]
    public void RemovedAndReappearingConnectionDoesNotFillMissingTail()
    {
        var ledger = new ProxyUsageLedger();
        ledger.Observe(Sample(1, 100, 100, Connection(100, 100)));
        ledger.Observe(Sample(2, 300, 300));
        ledger.Observe(Sample(3, 500, 500, Connection(500, 500)));
        Assert.AreEqual(1UL, ledger.Snapshot().ClosedConnections);
        Assert.IsNull(ledger.Snapshot().ConnectionObserved);
    }

    [TestMethod]
    public void DuplicateIdsInvalidateEntireSnapshotInsteadOfDoubleCounting()
    {
        var ledger = new ProxyUsageLedger();
        ledger.Observe(Sample(1, 100, 100, Connection(100, 100)));
        ledger.Observe(Sample(2, 300, 300, Connection(200, 200), Connection(200, 200)));
        Assert.IsNull(ledger.Snapshot().ConnectionObserved);
        Assert.AreEqual(1UL, ledger.Snapshot().Gaps);
    }

    [TestMethod]
    public void CapacityKeepsExactUnknownOverflowConservation()
    {
        var ledger = new ProxyUsageLedger(1);
        var second = Connection(100, 100, Owner with { ProcessId = 8 }) with { Id = Guid.NewGuid() };
        ledger.Observe(Sample(1, 1000, 1000, Connection(100, 100, Owner), second));
        ledger.Observe(Sample(2, 2000, 2000, Connection(200, 200, Owner), second with { Total = new(300, 300) }));
        var result = ledger.Snapshot();
        Assert.IsTrue(result.CapacityReached);
        Assert.AreEqual(new TrafficVolume(200, 200), result.Overflow);
        Conserve(result);
    }

    [TestMethod]
    public void ClockJumpDoesNotBridgeIntervals()
    {
        var ledger = new ProxyUsageLedger();
        ledger.Observe(Sample(1, 100, 100, Connection(100, 100)));
        ledger.Observe(Sample(2, 200, 200, Connection(200, 200)) with { Timestamp = DateTimeOffset.UnixEpoch.AddHours(1) });
        Assert.IsNull(ledger.Snapshot().ConnectionObserved);
        Assert.AreEqual(1UL, ledger.Snapshot().Gaps);
    }

    private static void Conserve(ProxyUsageSnapshot snapshot) => Assert.AreEqual(snapshot.ConnectionObserved,
        snapshot.Rows.Aggregate(snapshot.Overflow, (sum, row) => sum.Add(row.Traffic)));
}
