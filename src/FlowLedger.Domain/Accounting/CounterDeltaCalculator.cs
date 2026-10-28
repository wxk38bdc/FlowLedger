namespace FlowLedger.Domain.Accounting;

public static class CounterDeltaCalculator
{
    public static CounterDelta Calculate(CounterReading? previous, CounterReading current)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (previous is null)
        {
            return Unmeasured(CounterDeltaStatus.BaselineEstablished);
        }

        if (!StringComparer.Ordinal.Equals(previous.InterfaceId, current.InterfaceId))
        {
            return Unmeasured(CounterDeltaStatus.InterfaceChanged);
        }

        if (previous.CounterEpoch != current.CounterEpoch)
        {
            return Unmeasured(CounterDeltaStatus.EpochChanged);
        }

        if (current.CapturedAt <= previous.CapturedAt)
        {
            return Unmeasured(CounterDeltaStatus.TimeNotIncreasing);
        }

        if (current.Counters.ReceivedBytes < previous.Counters.ReceivedBytes ||
            current.Counters.SentBytes < previous.Counters.SentBytes)
        {
            return Unmeasured(CounterDeltaStatus.CounterDecreased);
        }

        return new CounterDelta(
            CounterDeltaStatus.Measured,
            new TrafficVolume(
                current.Counters.ReceivedBytes - previous.Counters.ReceivedBytes,
                current.Counters.SentBytes - previous.Counters.SentBytes),
            current.CapturedAt - previous.CapturedAt);
    }

    private static CounterDelta Unmeasured(CounterDeltaStatus status) => new(status, null, null);
}
