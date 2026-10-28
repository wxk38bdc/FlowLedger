namespace FlowLedger.Domain.Accounting;

public sealed record CounterDelta
{
    internal CounterDelta(CounterDeltaStatus status, TrafficVolume? volume, TimeSpan? duration)
    {
        Status = status;
        Volume = volume;
        Duration = duration;
    }

    public CounterDeltaStatus Status { get; }

    /// <summary>Null means unmeasured; a measured zero is a valid observation.</summary>
    public TrafficVolume? Volume { get; }

    /// <summary>Wall-clock interval for ordering, not a monotonic rate timer.</summary>
    public TimeSpan? Duration { get; }
}
