using FlowLedger.Domain.Accounting;

namespace FlowLedger.Application.Monitoring;

/// <summary>Serialized polling coordinator. No aggregation across interface measurement layers.</summary>
public sealed class InterfaceMonitor
{
    private readonly Dictionary<string, InterfaceTrafficSession> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MonitoredInterface> latest = new(StringComparer.Ordinal);

    public InterfaceMonitorSnapshot Apply(InterfaceCounterSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (snapshot.ErrorCode == 0)
        {
            foreach (var item in snapshot.Interfaces)
            {
                if (!seen.Add(item.Id))
                {
                    throw new ArgumentException("Duplicate interface identities in one snapshot.", nameof(snapshot));
                }

                if (!sessions.TryGetValue(item.Id, out var session))
                {
                    session = new InterfaceTrafficSession(item.Id);
                    sessions.Add(item.Id, session);
                }

                latest[item.Id] = new(item, session.Observe(
                    item.Reading, snapshot.MonotonicTime, item.IsUp, item.ContinuityKey), true);
            }
        }

        foreach (var id in latest.Keys.ToArray())
        {
            if (!seen.Contains(id))
            {
                latest[id] = latest[id] with { Usage = sessions[id].Invalidate(), IsPresent = false };
            }
        }

        return new(snapshot.CapturedAt, latest.Values.ToArray(), snapshot.ErrorCode);
    }

    public void BreakContinuity()
    {
        foreach (var session in sessions.Values)
        {
            session.Invalidate(TrafficSampleStatus.SamplingGap);
        }
    }
}
