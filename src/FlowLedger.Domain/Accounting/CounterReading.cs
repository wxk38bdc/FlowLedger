namespace FlowLedger.Domain.Accounting;

/// <summary>A cumulative interface counter reading within one counter epoch.</summary>
public sealed record CounterReading
{
    public CounterReading(
        string interfaceId,
        Guid counterEpoch,
        DateTimeOffset capturedAt,
        TrafficVolume counters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceId);
        if (counterEpoch == Guid.Empty)
        {
            throw new ArgumentException("A counter epoch is required.", nameof(counterEpoch));
        }

        InterfaceId = interfaceId;
        CounterEpoch = counterEpoch;
        CapturedAt = capturedAt.ToUniversalTime();
        Counters = counters;
    }

    public string InterfaceId { get; }
    public Guid CounterEpoch { get; }
    public DateTimeOffset CapturedAt { get; }
    public TrafficVolume Counters { get; }
}
