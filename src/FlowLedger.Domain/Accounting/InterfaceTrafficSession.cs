namespace FlowLedger.Domain.Accounting;

public enum TrafficSampleStatus
{
    Baseline,
    Measured,
    Offline,
    Unavailable,
    SamplingGap,
    IdentityChanged,
    CounterReset,
    ClockChanged,
    EpochChanged,
}

public sealed record TrafficSessionUpdate(
    TrafficSampleStatus Status,
    TrafficVolume? Interval,
    TrafficVolume? SessionTotal,
    double? ReceivedBytesPerSecond,
    double? SentBytesPerSecond,
    int Discontinuities);

/// <summary>One interface, one in-memory session. Gaps never bridge counter baselines.</summary>
public sealed class InterfaceTrafficSession(string interfaceId)
{
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromSeconds(5);

    private CounterReading? previous;
    private TimeSpan previousMonotonicTime;
    private string? previousContinuityKey;
    private TrafficVolume total;
    private bool hasMeasurement;
    private int discontinuities;

    public TrafficSessionUpdate Observe(
        CounterReading reading, TimeSpan monotonicTime, bool isUp, string continuityKey)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentException.ThrowIfNullOrWhiteSpace(continuityKey);
        if (!StringComparer.Ordinal.Equals(interfaceId, reading.InterfaceId))
        {
            throw new ArgumentException("An interface session cannot accept another interface.", nameof(reading));
        }

        if (!isUp)
        {
            return Invalidate(TrafficSampleStatus.Offline);
        }

        var elapsed = monotonicTime - previousMonotonicTime;
        var delta = CounterDeltaCalculator.Calculate(previous, reading);
        var status = previous is null ? TrafficSampleStatus.Baseline
            : previousContinuityKey != continuityKey ? TrafficSampleStatus.IdentityChanged
            : elapsed <= TimeSpan.Zero || reading.CapturedAt <= previous.CapturedAt ||
              ((reading.CapturedAt - previous.CapturedAt) - elapsed).Duration() > TimeSpan.FromSeconds(2) ? TrafficSampleStatus.ClockChanged
            : elapsed > MaximumInterval ? TrafficSampleStatus.SamplingGap
            : delta.Status == CounterDeltaStatus.EpochChanged ? TrafficSampleStatus.EpochChanged
            : delta.Status == CounterDeltaStatus.CounterDecreased ? TrafficSampleStatus.CounterReset
            : TrafficSampleStatus.Measured;

        if (status is not TrafficSampleStatus.Measured and not TrafficSampleStatus.Baseline)
        {
            Invalidate(status);
        }

        previous = reading;
        previousMonotonicTime = monotonicTime;
        previousContinuityKey = continuityKey;

        if (status != TrafficSampleStatus.Measured)
        {
            return Update(status);
        }

        var volume = delta.Volume!.Value;
        // Calculate before mutating: overflow must not partially change the session total.
        total = total.Add(volume);
        hasMeasurement = true;
        return new(status, volume, total,
            volume.ReceivedBytes / elapsed.TotalSeconds,
            volume.SentBytes / elapsed.TotalSeconds, discontinuities);
    }

    public TrafficSessionUpdate Invalidate(TrafficSampleStatus status = TrafficSampleStatus.Unavailable)
    {
        if (previous is not null)
        {
            discontinuities = checked(discontinuities + 1);
        }

        previous = null;
        previousContinuityKey = null;
        return Update(status);
    }

    private TrafficSessionUpdate Update(TrafficSampleStatus status) =>
        new(status, null, hasMeasurement ? total : null, null, null, discontinuities);
}
