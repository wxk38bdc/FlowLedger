using FlowLedger.Domain.Accounting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowLedger.Domain.Tests;

[TestClass]
public sealed class InterfaceTrafficSessionTests
{
    private static readonly Guid Epoch = Guid.Parse("98b04e15-fd8d-420a-ade5-63d41422f561");
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void StartupHasNoMeasuredTotalOrRate()
    {
        var session = new InterfaceTrafficSession("a");
        var result = Observe(session, 0, 1_000_000);
        Assert.AreEqual(TrafficSampleStatus.Baseline, result.Status);
        Assert.IsNull(result.SessionTotal);
        Assert.IsNull(result.ReceivedBytesPerSecond);
    }

    [TestMethod]
    public void RateUsesMonotonicTimeInsteadOfWallClockInterval()
    {
        var session = new InterfaceTrafficSession("a");
        Observe(session, 0, 100);
        var result = session.Observe(Reading(3, 300), TimeSpan.FromSeconds(2), true, "connection-a");
        Assert.AreEqual<double?>(100, result.ReceivedBytesPerSecond);
        Assert.AreEqual<TrafficVolume?>(new(200, 100), result.SessionTotal);
    }

    [TestMethod]
    public void IdleIsMeasuredZero()
    {
        var session = new InterfaceTrafficSession("a");
        Observe(session, 0, 100);
        var result = Observe(session, 1, 100);
        Assert.AreEqual<TrafficVolume?>(new(0, 0), result.SessionTotal);
        Assert.AreEqual<double?>(0, result.ReceivedBytesPerSecond);
    }

    [TestMethod]
    public void FailedPollDoesNotBridgeUnobservedBytes()
    {
        var session = new InterfaceTrafficSession("a");
        Observe(session, 0, 100);
        Observe(session, 1, 200);
        session.Invalidate();
        var baseline = Observe(session, 3, 1000);
        var result = Observe(session, 4, 1100);
        Assert.AreEqual(TrafficSampleStatus.Baseline, baseline.Status);
        Assert.AreEqual<TrafficVolume?>(new(200, 100), result.SessionTotal);
        Assert.AreEqual(1, result.Discontinuities);
    }

    [TestMethod]
    public void RepeatedFailuresCountOneBrokenContinuity()
    {
        var session = new InterfaceTrafficSession("a");
        Observe(session, 0, 100);
        session.Invalidate();
        var result = session.Invalidate();
        Assert.AreEqual(1, result.Discontinuities);
        Assert.IsNull(result.SessionTotal);
    }

    [TestMethod]
    public void OfflineAndReconnectStartFreshBaseline()
    {
        var session = new InterfaceTrafficSession("a");
        Observe(session, 0, 100);
        var offline = session.Observe(Reading(1, 500), TimeSpan.FromSeconds(1), false, "connection-a");
        var connected = Observe(session, 2, 1000);
        Assert.AreEqual(TrafficSampleStatus.Offline, offline.Status);
        Assert.IsNull(offline.ReceivedBytesPerSecond);
        Assert.AreEqual(TrafficSampleStatus.Baseline, connected.Status);
        Assert.IsNull(connected.SessionTotal);
    }

    [TestMethod]
    public void LongGapDoesNotCreateResumeSpike()
    {
        var session = new InterfaceTrafficSession("a");
        Observe(session, 0, 100);
        var gap = Observe(session, 60, 100_000);
        var result = Observe(session, 61, 100_100);
        Assert.AreEqual(TrafficSampleStatus.SamplingGap, gap.Status);
        Assert.IsNull(gap.Interval);
        Assert.AreEqual<TrafficVolume?>(new(100, 50), result.SessionTotal);
    }

    [TestMethod]
    public void ConnectionIdentityChangeDoesNotBridgeBoundary()
    {
        var session = new InterfaceTrafficSession("a");
        Observe(session, 0, 100);
        var boundary = session.Observe(Reading(1, 1000), TimeSpan.FromSeconds(1), true, "connection-b");
        var result = session.Observe(Reading(2, 1100), TimeSpan.FromSeconds(2), true, "connection-b");
        Assert.AreEqual(TrafficSampleStatus.IdentityChanged, boundary.Status);
        Assert.AreEqual<TrafficVolume?>(new(100, 50), result.SessionTotal);
    }

    [TestMethod]
    public void CounterResetStartsNewBaselineAndPreservesMeasuredTotals()
    {
        var session = new InterfaceTrafficSession("a");
        Observe(session, 0, 100);
        Observe(session, 1, 200);
        var reset = Observe(session, 2, 10);
        var result = Observe(session, 3, 30);
        Assert.AreEqual(TrafficSampleStatus.CounterReset, reset.Status);
        Assert.IsNull(reset.Interval);
        Assert.AreEqual<TrafficVolume?>(new(120, 60), result.SessionTotal);
    }

    [TestMethod]
    public void EpochChangeWithLargerCountersIsNotTraffic()
    {
        var session = new InterfaceTrafficSession("a");
        Observe(session, 0, 100);
        var changed = session.Observe(new("a", Guid.NewGuid(), Start.AddSeconds(1), new(10_000, 20_000)),
            TimeSpan.FromSeconds(1), true, "connection-a");
        Assert.AreEqual(TrafficSampleStatus.EpochChanged, changed.Status);
        Assert.IsNull(changed.Interval);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-10)]
    public void BackwardsWallClockRebaselinesWithoutStalling(int seconds)
    {
        var session = new InterfaceTrafficSession("a");
        Observe(session, 0, 100);
        var changed = session.Observe(Reading(seconds, 200), TimeSpan.FromSeconds(1), true, "connection-a");
        var recovered = session.Observe(Reading(seconds + 1, 220), TimeSpan.FromSeconds(2), true, "connection-a");
        Assert.AreEqual(TrafficSampleStatus.ClockChanged, changed.Status);
        Assert.AreEqual<TrafficVolume?>(new(20, 10), recovered.SessionTotal);
    }

    [TestMethod]
    public void ForwardWallClockJumpRecordsDiscontinuity()
    {
        var session = new InterfaceTrafficSession("a");
        Observe(session, 0, 100);
        var result = session.Observe(Reading(3600, 200), TimeSpan.FromSeconds(1), true, "connection-a");
        Assert.AreEqual(TrafficSampleStatus.ClockChanged, result.Status);
        Assert.IsNull(result.Interval);
    }

    [TestMethod]
    public void NonIncreasingMonotonicTimeIsNotAValidRate()
    {
        var session = new InterfaceTrafficSession("a");
        Observe(session, 0, 100);
        var result = session.Observe(Reading(1, 200), TimeSpan.Zero, true, "connection-a");
        Assert.AreEqual(TrafficSampleStatus.ClockChanged, result.Status);
        Assert.IsNull(result.ReceivedBytesPerSecond);
    }

    [TestMethod]
    public void SessionsRejectDifferentInterfaceIdentity()
    {
        var session = new InterfaceTrafficSession("a");
        Assert.ThrowsExactly<ArgumentException>(() => session.Observe(
            new("b", Epoch, Start, new(100, 50)), TimeSpan.Zero, true, "connection-a"));
    }

    private static TrafficSessionUpdate Observe(InterfaceTrafficSession session, int seconds, ulong bytes) =>
        session.Observe(Reading(seconds, bytes), TimeSpan.FromSeconds(seconds), true, "connection-a");

    private static CounterReading Reading(int seconds, ulong bytes) =>
        new("a", Epoch, Start.AddSeconds(seconds), new(bytes, bytes / 2));
}
