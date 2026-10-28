namespace FlowLedger.Domain.Accounting;

public readonly record struct TrafficVolume(ulong ReceivedBytes, ulong SentBytes)
{
    public TrafficVolume Add(TrafficVolume other) => new(
        checked(ReceivedBytes + other.ReceivedBytes),
        checked(SentBytes + other.SentBytes));
}
