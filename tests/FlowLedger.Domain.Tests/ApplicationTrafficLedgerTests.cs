using FlowLedger.Domain.Accounting;

namespace FlowLedger.Domain.Tests;

[TestClass]
public sealed class ApplicationTrafficLedgerTests
{
    [TestMethod]
    public void NoEventsRemainMissingRatherThanZero() => Assert.IsNull(new ApplicationTrafficLedger().Snapshot().Observed);

    [TestMethod]
    public void KnownZeroEventIsMeasured()
    {
        var ledger = new ApplicationTrafficLedger();
        ledger.ProcessStarted(7, 1, "sample.exe", 10);
        ledger.Observe(7, 2, NetworkTransport.Tcp4, true, 0);
        var result = ledger.Snapshot();
        Assert.IsNotNull(result.Observed);
        Assert.AreEqual(default(TrafficVolume), result.Processes.Single().Traffic.Total);
        Assert.AreEqual(1UL, result.NetworkEvents);
    }

    [TestMethod]
    public void AllTransportDirectionsConserveExactIntegerBytes()
    {
        var ledger = new ApplicationTrafficLedger();
        ledger.ProcessStarted(7, 1, "sample.exe", 10);
        long qpc = 1;
        foreach (var transport in Enum.GetValues<NetworkTransport>())
        {
            ledger.Observe(7, ++qpc, transport, true, uint.MaxValue);
            ledger.Observe(7, ++qpc, transport, false, uint.MaxValue);
            ledger.Observe(99, ++qpc, transport, true, 123);
        }

        var result = ledger.Snapshot();
        Assert.AreEqual(4UL * uint.MaxValue + 492, result.Observed!.Value.Total.ReceivedBytes);
        Assert.AreEqual(4UL * uint.MaxValue, result.Observed.Value.Total.SentBytes);
        Conserve(result);
    }

    [TestMethod]
    public void PidReuseKeepsInstancesSeparate()
    {
        var ledger = new ApplicationTrafficLedger();
        ledger.ProcessStarted(7, 10, "first.exe", 1);
        ledger.Observe(7, 11, NetworkTransport.Tcp4, true, 100);
        ledger.ProcessStopped(7, 12, 1);
        ledger.ProcessStarted(7, 20, "second.exe", 2);
        ledger.Observe(7, 21, NetworkTransport.Udp6, false, 200);
        var result = ledger.Snapshot();
        Assert.HasCount(2, result.Processes);
        Assert.AreEqual(100UL, result.Processes[0].Traffic.Total.ReceivedBytes);
        Assert.AreEqual(200UL, result.Processes[1].Traffic.Total.SentBytes);
        Assert.AreNotEqual(result.Processes[0].FirstSeenQpc, result.Processes[1].FirstSeenQpc);
        Conserve(result);
    }

    [TestMethod]
    public void NewStartCanReuseKernelPointerWhenOldStopWasLost()
    {
        var ledger = new ApplicationTrafficLedger();
        ledger.ProcessStarted(7, 1, "first.exe", 10);
        ledger.Observe(7, 2, NetworkTransport.Tcp4, true, 100);
        ledger.ProcessStarted(7, 3, "second.exe", 10);
        ledger.Observe(7, 4, NetworkTransport.Tcp4, true, 200);
        Assert.HasCount(2, ledger.Snapshot().Processes);
        Assert.AreEqual("second.exe", ledger.Snapshot().Processes[1].Name);
        Conserve(ledger.Snapshot());
    }

    [TestMethod]
    public void MissingStopStillClosesOldInstanceAtNewStart()
    {
        var ledger = new ApplicationTrafficLedger();
        ledger.ProcessStarted(7, 10, "first.exe", 1);
        ledger.Observe(7, 11, NetworkTransport.Tcp4, true, 100);
        ledger.ProcessStarted(7, 20, "second.exe", 2);
        ledger.Observe(7, 21, NetworkTransport.Tcp4, true, 200);
        Assert.HasCount(2, ledger.Snapshot().Processes);
    }

    [TestMethod]
    public void EventsBeforeRundownAreNeverBackfilled()
    {
        var ledger = new ApplicationTrafficLedger();
        ledger.Observe(7, 1, NetworkTransport.Tcp4, false, 30);
        ledger.ProcessStarted(7, 2, "existing.exe", 1, true);
        ledger.Observe(7, 3, NetworkTransport.Tcp4, false, 40);
        var result = ledger.Snapshot();
        Assert.AreEqual(30UL, result.Unidentified.Total.SentBytes);
        Assert.AreEqual(40UL, result.Processes.Single().Traffic.Total.SentBytes);
        Assert.IsTrue(result.Processes.Single().IsRundown);
        Conserve(result);
    }

    [TestMethod]
    public void StopBoundaryAndPostExitBytesRemainUnknown()
    {
        var ledger = new ApplicationTrafficLedger();
        ledger.ProcessStarted(7, 1, "sample.exe", 10);
        ledger.ProcessStopped(7, 2, 10);
        ledger.Observe(7, 2, NetworkTransport.Udp4, true, 100);
        ledger.Observe(7, 3, NetworkTransport.Udp4, true, 100);
        Assert.HasCount(0, ledger.Snapshot().Processes);
        Assert.AreEqual(200UL, ledger.Snapshot().Unidentified.Total.ReceivedBytes);
    }

    [TestMethod]
    public void StopWithDifferentKernelKeyCannotCloseNewProcess()
    {
        var ledger = new ApplicationTrafficLedger();
        ledger.ProcessStarted(7, 1, "sample.exe", 10);
        ledger.ProcessStopped(7, 2, 9);
        ledger.Observe(7, 3, NetworkTransport.Udp4, true, 100);
        Assert.AreEqual(100UL, ledger.Snapshot().Processes.Single().Traffic.Total.ReceivedBytes);
    }

    [TestMethod]
    public void RepeatedRundownDoesNotCreateSecondInstance()
    {
        var ledger = new ApplicationTrafficLedger();
        ledger.ProcessStarted(7, 1, "sample.exe", 10);
        ledger.ProcessStarted(7, 2, "sample.exe", 10, true);
        ledger.Observe(7, 3, NetworkTransport.Udp4, true, 100);
        Assert.HasCount(1, ledger.Snapshot().Processes);
        Assert.IsFalse(ledger.Snapshot().Processes.Single().IsRundown);
    }

    [TestMethod]
    public void LossRevokesPotentiallyMisnamedBytesAndRequiresFreshEvidence()
    {
        var ledger = new ApplicationTrafficLedger();
        ledger.ProcessStarted(7, 1, "sample.exe", 10);
        ledger.Observe(7, 2, NetworkTransport.Tcp4, true, 100);
        ledger.ReportLostEvents(3);
        ledger.ReportLostEvents(3);
        ledger.ReportLostEvents(2);
        ledger.Observe(7, 3, NetworkTransport.Tcp4, true, 200);
        ledger.ProcessStarted(8, 4, "fresh.exe", 11);
        ledger.Observe(8, 5, NetworkTransport.Tcp4, true, 400);
        var result = ledger.Snapshot();
        Assert.AreEqual(3UL, result.LostEvents);
        Assert.AreEqual(300UL, result.Unidentified.Total.ReceivedBytes);
        Assert.AreEqual("fresh.exe", result.Processes.Single().Name);
        Assert.IsTrue(result.IdentityUncertain);
        Conserve(result);
    }

    [TestMethod]
    public void LateLifecycleRevokesEarlierAttributionInsteadOfRenamingOldPidBytes()
    {
        var ledger = new ApplicationTrafficLedger();
        ledger.ProcessStarted(7, 1, "old.exe", 10);
        ledger.Observe(7, 10, NetworkTransport.Tcp4, true, 100);
        ledger.ProcessStarted(7, 5, "new.exe", 11);
        ledger.Observe(7, 11, NetworkTransport.Tcp4, true, 200);
        var result = ledger.Snapshot();
        Assert.HasCount(0, result.Processes);
        Assert.IsTrue(result.IdentityUncertain);
        Assert.AreEqual(300UL, result.Unidentified.Total.ReceivedBytes);
        Conserve(result);
    }

    [TestMethod]
    public void LateNetworkEventIsUnknownAndInvalidatesPotentiallyReusedIdentity()
    {
        var ledger = new ApplicationTrafficLedger();
        ledger.ProcessStarted(7, 10, "new.exe", 10);
        ledger.Observe(7, 11, NetworkTransport.Tcp4, true, 100);
        ledger.Observe(7, 5, NetworkTransport.Tcp4, true, 200);
        Assert.HasCount(0, ledger.Snapshot().Processes);
        Assert.AreEqual(300UL, ledger.Snapshot().Unidentified.Total.ReceivedBytes);
        Conserve(ledger.Snapshot());
    }

    [TestMethod]
    public void CapacityDoesNotLeaveReusedPidAttachedToOldProcess()
    {
        var ledger = new ApplicationTrafficLedger(1);
        ledger.ProcessStarted(7, 1, "first.exe", 1);
        ledger.Observe(7, 2, NetworkTransport.Tcp4, true, 100);
        ledger.ProcessStarted(7, 3, "second.exe", 2);
        ledger.Observe(7, 4, NetworkTransport.Tcp4, true, 200);
        var result = ledger.Snapshot();
        Assert.IsTrue(result.CapacityReached);
        Assert.AreEqual(100UL, result.Processes.Single().Traffic.Total.ReceivedBytes);
        Assert.AreEqual(200UL, result.Unidentified.Total.ReceivedBytes);
        Conserve(result);
    }

    [TestMethod]
    public void InvalidNameCannotLeaveOldPidAttributionActive()
    {
        var ledger = new ApplicationTrafficLedger();
        ledger.ProcessStarted(7, 1, "first.exe", 1);
        ledger.ProcessStarted(7, 2, "", 2);
        ledger.Observe(7, 3, NetworkTransport.Tcp4, true, 100);
        Assert.AreEqual(100UL, ledger.Snapshot().Unidentified.Total.ReceivedBytes);
        Assert.AreEqual(1UL, ledger.Snapshot().RejectedEvents);
    }

    [TestMethod]
    public void SeededManyShortLivedProcessesConserveEveryTransport()
    {
        var ledger = new ApplicationTrafficLedger(100);
        var random = new Random(42);
        long time = 0;
        for (var index = 0; index < 250; index++)
        {
            var pid = index % 17 + 1;
            ledger.ProcessStarted(pid, ++time, "synthetic.exe", (ulong)index + 1);
            for (var item = 0; item < 50; item++)
            {
                ledger.Observe(item % 3 == 0 ? 999 : pid, ++time, (NetworkTransport)random.Next(4),
                    item % 2 == 0, (uint)random.Next(1000000));
            }

            ledger.ProcessStopped(pid, ++time, (ulong)index + 1);
        }

        var result = ledger.Snapshot();
        Assert.AreEqual(12500UL, result.NetworkEvents);
        Assert.IsTrue(result.CapacityReached);
        Conserve(result);
    }

    private static void Conserve(ApplicationLedgerSnapshot result)
    {
        var assigned = result.Processes.Aggregate(default(TransportTraffic), (sum, item) => sum.Add(item.Traffic));
        Assert.AreEqual(result.Observed, assigned.Add(result.Unidentified));
    }
}
