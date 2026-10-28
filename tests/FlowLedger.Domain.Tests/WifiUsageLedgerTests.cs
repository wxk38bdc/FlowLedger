using FlowLedger.Domain.Accounting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowLedger.Domain.Tests;

[TestClass]
public sealed class WifiUsageLedgerTests
{
    private static readonly Guid ContextA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ContextB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly WifiNetworkIdentity A = new("41", "profile", ContextA, 1, 7, 4);
    private static readonly WifiNetworkIdentity B = new("42", "profile-b", ContextB, 1, 7, 4);

    [TestMethod]
    public void EnablingStartsAtTheFirstWifiCounterNotThePreviousInterfaceSample()
    {
        var ledger = new WifiUsageLedger();
        var result = ledger.Observe(new(10000, 5000), Endpoint(A));
        Assert.IsNull(result.ObservedTotal);
        Assert.HasCount(0, result.Networks);
    }

    [TestMethod]
    public void StableNetworkGetsExactBytesAndMeasuredIdleIsNotMissing()
    {
        var ledger = Start();
        var idle = ledger.Observe(new(0, 0), Endpoint(A));
        Assert.AreEqual<TrafficVolume?>(new(0, 0), idle.ObservedTotal);
        var result = ledger.Observe(new(123, 45), Endpoint(A));
        Assert.AreEqual(new TrafficVolume(123, 45), result.Networks[0].Volume);
        AssertConserved(result);
    }

    [TestMethod]
    public void SwitchingNetworksKeepsBoundaryBytesInsteadOfSplittingProportionally()
    {
        var ledger = Start();
        ledger.Observe(new(100, 50), Endpoint(A));
        ledger.Observe(new(20, 10), Endpoint(B, sequence: 1));
        var result = ledger.Observe(new(40, 20), Endpoint(B, sequence: 1));
        Assert.HasCount(2, result.Networks);
        Assert.AreEqual(new TrafficVolume(20, 10), result.BoundaryUnallocated);
        Assert.AreEqual<TrafficVolume?>(new(160, 80), result.ObservedTotal);
        AssertConserved(result);
    }

    [TestMethod]
    public void ReturnToAnExistingCandidateReusesOnlyItsOwnRecord()
    {
        var ledger = Start();
        ledger.Observe(new(10, 5), Endpoint(A));
        ledger.Observe(new(10, 5), Endpoint(B, sequence: 1));
        ledger.Observe(new(20, 10), Endpoint(B, sequence: 1));
        ledger.Observe(new(10, 5), Endpoint(A, sequence: 2));
        var result = ledger.Observe(new(30, 15), Endpoint(A, sequence: 2));
        Assert.HasCount(2, result.Networks);
        Assert.AreEqual(new TrafficVolume(40, 20), result.Networks.Single(row => row.Network == A).Volume);
        AssertConserved(result);
    }

    [TestMethod]
    public void SameSsidWithDifferentWindowsContextDoesNotMerge()
    {
        var ledger = Start();
        var other = A with { NetworkContext = ContextB };
        ledger.Observe(new(10, 5), Endpoint(A));
        ledger.Observe(new(20, 10), Endpoint(other));
        var result = ledger.Observe(new(30, 15), Endpoint(other));
        Assert.HasCount(2, result.Networks);
        Assert.AreEqual(new TrafficVolume(20, 10), result.BoundaryUnallocated);
        AssertConserved(result);
    }

    [TestMethod]
    public void RoamingDoesNotCreateANewCandidateButItsBoundaryIsUnallocated()
    {
        var ledger = Start();
        ledger.Observe(new(10, 5), Endpoint(A));
        ledger.Observe(new(20, 10), Endpoint(A, ap: "AP-B", sequence: 1));
        var result = ledger.Observe(new(30, 15), Endpoint(A, ap: "AP-B", sequence: 1));
        Assert.HasCount(1, result.Networks);
        Assert.AreEqual(new TrafficVolume(40, 20), result.Networks[0].Volume);
        Assert.AreEqual(new TrafficVolume(20, 10), result.BoundaryUnallocated);
        AssertConserved(result);
    }

    [TestMethod]
    public void UnnotifiedAccessPointChangeIsStillABoundary()
    {
        var ledger = Start();
        var result = ledger.Observe(new(20, 10), Endpoint(A, ap: "AP-B"));
        Assert.AreEqual(new TrafficVolume(20, 10), result.BoundaryUnallocated);
        AssertConserved(result);
    }

    [TestMethod]
    public void DisconnectAndReconnectBetweenPollsToTheSameNetworkIsNotMissed()
    {
        var ledger = Start();
        var result = ledger.Observe(new(20, 10), Endpoint(A, sequence: 2));
        Assert.AreEqual(new TrafficVolume(20, 10), result.BoundaryUnallocated);
        Assert.HasCount(0, result.Networks);
    }

    [TestMethod]
    public void TransitionDuringTheCounterBracketProtectsBothAdjacentIntervals()
    {
        var ledger = Start();
        ledger.Observe(new(10, 5), Endpoint(A, sequence: 1, stable: false));
        ledger.Observe(new(20, 10), Endpoint(B, sequence: 1));
        var result = ledger.Observe(new(30, 15), Endpoint(B, sequence: 1));
        Assert.AreEqual(new TrafficVolume(30, 15), result.BoundaryUnallocated);
        Assert.AreEqual(new TrafficVolume(30, 15), result.Networks[0].Volume);
        AssertConserved(result);
    }

    [TestMethod]
    public void PermissionFailureNeverReusesThePreviousNetwork()
    {
        var ledger = Start();
        var result = ledger.Observe(new(20, 10), Endpoint(null));
        Assert.AreEqual(new TrafficVolume(20, 10), result.Unidentified);
        Assert.HasCount(0, result.Networks);
        AssertConserved(result);
    }

    [TestMethod]
    public void PartialOrMissingNotificationsPreventNetworkAttribution()
    {
        var ledger = Start();
        var result = ledger.Observe(new(20, 10), Endpoint(A, complete: false));
        Assert.AreEqual(new TrafficVolume(20, 10), result.Unidentified);
        Assert.HasCount(0, result.Networks);
        AssertConserved(result);
    }

    [TestMethod]
    public void MissingCounterIntervalDoesNotBecomeZeroOrBoundaryBytes()
    {
        var ledger = Start();
        var missing = ledger.Observe(null, Endpoint(B, sequence: 1));
        Assert.IsNull(missing.ObservedTotal);
        var result = ledger.Observe(new(20, 10), Endpoint(B, sequence: 1));
        Assert.AreEqual<TrafficVolume?>(new(20, 10), result.ObservedTotal);
        Assert.AreEqual(new TrafficVolume(0, 0), result.BoundaryUnallocated);
    }

    [TestMethod]
    public void PauseAndResumeDoNotChargeBytesFromBeforeTheNewBaseline()
    {
        var ledger = Start();
        ledger.Observe(new(10, 5), Endpoint(A));
        ledger.BreakContinuity();
        ledger.Observe(new(10000, 5000), Endpoint(B));
        var result = ledger.Observe(new(20, 10), Endpoint(B));
        Assert.AreEqual<TrafficVolume?>(new(30, 15), result.ObservedTotal);
        AssertConserved(result);
    }

    [TestMethod]
    public void RawSsidProfileAndSecurityArePartOfCandidateIdentity()
    {
        var ledger = Start();
        var candidates = new[] { A, A with { SsidHex = "FF" }, A with { ProfileName = "other" }, A with { Authentication = 9 } };
        foreach (var candidate in candidates)
        {
            ledger.Observe(new(1, 1), Endpoint(candidate));
            ledger.Observe(new(1, 1), Endpoint(candidate));
        }

        var result = ledger.Snapshot();
        Assert.HasCount(4, result.Networks);
        AssertConserved(result);
    }

    private static WifiUsageLedger Start()
    {
        var ledger = new WifiUsageLedger();
        ledger.Observe(null, Endpoint(A));
        return ledger;
    }

    private static WifiEndpoint Endpoint(WifiNetworkIdentity? network, string ap = "AP-A", long sequence = 0, bool complete = true, bool stable = true) =>
        new(network, "Synthetic WLAN", ap, sequence, complete, stable);

    private static void AssertConserved(WifiLedgerSnapshot snapshot) =>
        Assert.AreEqual(snapshot.ObservedTotal,
            snapshot.Networks.Aggregate(default(TrafficVolume), (sum, row) => sum.Add(row.Volume))
                .Add(snapshot.BoundaryUnallocated).Add(snapshot.Unidentified));
}
