using FlowLedger.Domain.Accounting;
using FlowLedger.Domain.Ssh;

namespace FlowLedger.Domain.Tests;

[TestClass]
public sealed class SshUsageLedgerTests
{
    private static readonly ProcessTraffic Client = new(17, 10, "ssh.exe", false, default);
    private static readonly TcpFlow Flow = new("127.0.0.1", 50000, "127.0.0.1", 2222);

    [TestMethod]
    public void NewOutboundCustomPortIsCandidateAndDirectionsStaySeparate()
    {
        var ledger = new SshUsageLedger([]);
        ledger.Connected(Client, Flow);
        ledger.Observe(Client, 20, Flow, false, 100);
        ledger.Observe(Client, 21, Flow, true, 200);
        var row = ledger.Snapshot().Connections.Single();
        Assert.AreEqual(SshEvidence.ClientCandidate, row.Evidence);
        Assert.AreEqual(new TrafficVolume(200, 100), row.Traffic);
        Assert.AreEqual(row.Traffic, ledger.Snapshot().Observed);
    }

    [TestMethod]
    public void ExistingCustomPortNeedsRuleAndPort22AloneDoesNotIdentifyOtherApps()
    {
        var ledger = new SshUsageLedger([]);
        ledger.Observe(Client, 20, Flow, true, 100);
        ledger.Observe(Client with { Name = "browser.exe" }, 21, Flow with { RemotePort = 22 }, true, 100);
        Assert.IsNull(ledger.Snapshot().Observed);
        ledger.Observe(Client, 22, Flow with { RemotePort = 22 }, true, 100);
        Assert.HasCount(1, ledger.Snapshot().Connections);
    }

    [TestMethod]
    public void UserRuleMatchesExactAddressAndPortAndRetainsAlias()
    {
        var ledger = new SshUsageLedger([new("127.0.0.1", 2222, "Synthetic server")]);
        ledger.Observe(Client, 20, Flow, true, 100);
        ledger.Observe(Client, 21, Flow with { RemoteAddress = "::1" }, true, 200);
        var row = ledger.Snapshot().Connections.Single();
        Assert.AreEqual("Synthetic server", row.Alias);
        Assert.AreEqual(SshEvidence.UserRule, row.Evidence);
        Assert.AreEqual(100UL, row.Traffic.ReceivedBytes);
    }

    [TestMethod]
    public void ReusedPidAndReconnectedTupleRemainSeparateInstances()
    {
        var ledger = new SshUsageLedger([]);
        ledger.Connected(Client, Flow); ledger.Observe(Client, 20, Flow, true, 100);
        ledger.Close(Client.ProcessId, Flow);
        ledger.Connected(Client, Flow); ledger.Observe(Client, 22, Flow, true, 200);
        var reused = Client with { FirstSeenQpc = 30 };
        ledger.Connected(reused, Flow); ledger.Observe(reused, 31, Flow, true, 300);
        Assert.HasCount(3, ledger.Snapshot().Connections);
        Assert.HasCount(3, ledger.Snapshot().Connections.Select(item => item.Key).Distinct().ToArray());
        Assert.AreEqual(600UL, ledger.Snapshot().Observed!.Value.ReceivedBytes);
        Assert.AreEqual(2, ledger.Snapshot().Connections.Count(item => item.Closed));
    }

    [TestMethod]
    public void IdentityRevocationConservesAlreadyObservedTraffic()
    {
        var ledger = new SshUsageLedger([]);
        ledger.Connected(Client, Flow); ledger.Observe(Client, 20, Flow, true, 100);
        ledger.InvalidateIdentities(); ledger.InvalidateIdentities();
        ledger.Observe(null, 21, Flow, true, 200);
        Assert.IsEmpty(ledger.Snapshot().Connections);
        Assert.AreEqual(new TrafficVolume(100, 0), ledger.Snapshot().Unidentified);
        Assert.AreEqual(ledger.Snapshot().Observed, ledger.Snapshot().Unidentified);
        Assert.IsTrue(ledger.Snapshot().IdentityUncertain);
    }

    [TestMethod]
    public void CapacityOverflowIsUnknownAndDoesNotLoseBytes()
    {
        var ledger = new SshUsageLedger([new("127.0.0.1", 2222, "Synthetic")], 1);
        ledger.Observe(Client, 20, Flow, false, 100);
        ledger.Observe(Client, 21, Flow with { LocalPort = 50001 }, true, 200);
        var snapshot = ledger.Snapshot();
        Assert.IsTrue(snapshot.CapacityReached);
        Assert.AreEqual(snapshot.Observed, snapshot.Connections.Single().Traffic.Add(snapshot.Unidentified));
    }
}
