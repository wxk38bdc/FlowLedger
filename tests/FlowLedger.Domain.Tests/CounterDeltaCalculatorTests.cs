using FlowLedger.Domain.Accounting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowLedger.Domain.Tests;

[TestClass]
public sealed class CounterDeltaCalculatorTests
{
    private static readonly Guid Epoch = Guid.Parse("6f61bac6-9ba0-48bc-b8d8-1c7815be269c");
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void FirstReadingDoesNotCountTrafficFromBeforeStartup()
    {
        var result = CounterDeltaCalculator.Calculate(null, Reading(1_000_000, 500_000));

        AssertUnmeasured(result, CounterDeltaStatus.BaselineEstablished);
    }

    [TestMethod]
    public void ConsecutiveReadingsProduceOnlyNewBytes()
    {
        var result = CounterDeltaCalculator.Calculate(Reading(100, 50), Reading(150, 70, 2));

        Assert.AreEqual(CounterDeltaStatus.Measured, result.Status);
        Assert.AreEqual<TrafficVolume?>(new TrafficVolume(50, 20), result.Volume);
        Assert.AreEqual<TimeSpan?>(TimeSpan.FromSeconds(2), result.Duration);
    }

    [TestMethod]
    public void MeasuredZeroIsDifferentFromMissingMeasurement()
    {
        var result = CounterDeltaCalculator.Calculate(Reading(100, 50), Reading(100, 50, 1));

        Assert.AreEqual(CounterDeltaStatus.Measured, result.Status);
        Assert.AreEqual<TrafficVolume?>(new TrafficVolume(0, 0), result.Volume);
    }

    [TestMethod]
    public void SwitchingInterfacesDoesNotMixTheirCounters()
    {
        var current = new CounterReading("other-interface", Epoch, Start.AddSeconds(1), new(300, 100));

        AssertUnmeasured(
            CounterDeltaCalculator.Calculate(Reading(100, 50), current),
            CounterDeltaStatus.InterfaceChanged);
    }

    [TestMethod]
    public void NewEpochResetsBaselineEvenWhenCountersAreLarger()
    {
        var current = new CounterReading("interface-a", Guid.NewGuid(), Start.AddSeconds(1), new(300, 100));

        AssertUnmeasured(
            CounterDeltaCalculator.Calculate(Reading(100, 50), current),
            CounterDeltaStatus.EpochChanged);
    }

    [TestMethod]
    [DataRow(90, 60)]
    [DataRow(110, 40)]
    public void CounterResetDoesNotUnderflow(int received, int sent)
    {
        AssertUnmeasured(
            CounterDeltaCalculator.Calculate(Reading(100, 50), Reading((ulong)received, (ulong)sent, 1)),
            CounterDeltaStatus.CounterDecreased);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void DuplicateOrOutOfOrderTimestampIsNotMeasured(int seconds)
    {
        AssertUnmeasured(
            CounterDeltaCalculator.Calculate(Reading(100, 50), Reading(150, 70, seconds)),
            CounterDeltaStatus.TimeNotIncreasing);
    }

    [TestMethod]
    public void LargeCountersKeepBytePrecision()
    {
        var result = CounterDeltaCalculator.Calculate(
            Reading(ulong.MaxValue - 10, ulong.MaxValue - 8),
            Reading(ulong.MaxValue, ulong.MaxValue, 1));

        Assert.AreEqual<TrafficVolume?>(new TrafficVolume(10, 8), result.Volume);
    }

    [TestMethod]
    public void ReadingNormalizesTimezoneToUtc()
    {
        var reading = new CounterReading("interface-a", Epoch, Start.ToOffset(TimeSpan.FromHours(8)), new(1, 2));

        Assert.AreEqual(TimeSpan.Zero, reading.CapturedAt.Offset);
        Assert.AreEqual(Start, reading.CapturedAt);
    }

    [TestMethod]
    public void ByteAggregationCannotSilentlyOverflow()
    {
        Assert.ThrowsExactly<OverflowException>(() => new TrafficVolume(ulong.MaxValue, 0).Add(new(1, 0)));
        Assert.ThrowsExactly<OverflowException>(() => new TrafficVolume(0, ulong.MaxValue).Add(new(0, 1)));
    }

    [TestMethod]
    public void MissingInterfaceIdentityIsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new CounterReading(" ", Epoch, Start, new(0, 0)));
    }

    [TestMethod]
    public void MissingCounterEpochIsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new CounterReading("interface-a", Guid.Empty, Start, new(0, 0)));
    }

    private static CounterReading Reading(ulong received, ulong sent, int seconds = 0) =>
        new("interface-a", Epoch, Start.AddSeconds(seconds), new(received, sent));

    private static void AssertUnmeasured(CounterDelta result, CounterDeltaStatus expected)
    {
        Assert.AreEqual(expected, result.Status);
        Assert.IsNull(result.Volume);
        Assert.IsNull(result.Duration);
    }
}
