using FlowLedger.Domain.Accounting;

namespace FlowLedger.Domain.Proxy;

public enum ProxyIngress { Unknown, ExplicitProxy, Tun }
public enum ProxyRoute { Unknown, CoreDirect, CoreProxy }
public sealed record ProxyProcess(int ProcessId, long StartTicks, string Name);
public sealed record ProxyConnectionSample(Guid Id, DateTimeOffset Started, TrafficVolume Total,
    ProxyProcess? Owner, ProxyIngress Ingress, ProxyRoute Route);
public sealed record ProxyObservation(Guid CoreEpoch, DateTimeOffset Timestamp, TimeSpan MonotonicTime,
    TrafficVolume CoreTotal, IReadOnlyList<ProxyConnectionSample> Connections);
public sealed record ProxyUsageRow(ProxyProcess? Owner, ProxyIngress Ingress, ProxyRoute Route, TrafficVolume Traffic);
public sealed record ProxyUsageSnapshot(TrafficVolume? CoreObserved, TrafficVolume? ConnectionObserved,
    IReadOnlyList<ProxyUsageRow> Rows, TrafficVolume Overflow, int? ActiveConnections,
    ulong Gaps, ulong Resets, ulong NewBaselines, ulong ClosedConnections, bool CapacityReached);

/// <summary>One API source, once per connection ID/lifetime. API bytes and ETW bytes are never added.
/// Only consecutive snapshots produce deltas; active-connection coverage is necessarily partial.</summary>
public sealed class ProxyUsageLedger(int maximumRows = 4096)
{
    private readonly record struct RowKey(ProxyProcess? Owner, ProxyIngress Ingress, ProxyRoute Route);
    private readonly Dictionary<RowKey, TrafficVolume> rows = [];
    private Dictionary<Guid, ProxyConnectionSample> previousConnections = [];
    private ProxyObservation? previous;
    private TrafficVolume? coreObserved;
    private TrafficVolume? connectionObserved;
    private TrafficVolume overflow;
    private int? active;
    private ulong gaps;
    private ulong resets;
    private ulong baselines;
    private ulong closed;
    private bool capacity;

    public void BreakContinuity()
    {
        previous = null;
        previousConnections.Clear();
        active = null;
        gaps = checked(gaps + 1);
    }

    public void Observe(ProxyObservation observation)
    {
        if (observation.Connections.Count > 4096 || observation.Connections.Select(item => item.Id).Distinct().Count() != observation.Connections.Count)
        {
            BreakContinuity();
            return;
        }

        active = observation.Connections.Count;
        var continuous = previous is not null;
        if (previous is { } priorObservation)
        {
            var monotonic = observation.MonotonicTime - priorObservation.MonotonicTime;
            if (priorObservation.CoreEpoch != observation.CoreEpoch || !TryDelta(observation.CoreTotal, priorObservation.CoreTotal, out _))
            {
                resets = checked(resets + 1);
                continuous = false;
            }
            else if (monotonic <= TimeSpan.Zero || monotonic > TimeSpan.FromSeconds(5) ||
                ((observation.Timestamp - priorObservation.Timestamp) - monotonic).Duration() > TimeSpan.FromSeconds(2))
            {
                gaps = checked(gaps + 1);
                continuous = false;
            }

            if (continuous && TryDelta(observation.CoreTotal, priorObservation.CoreTotal, out var delta))
            {
                coreObserved = (coreObserved ?? default).Add(delta);
            }
        }

        if (!continuous) { previousConnections.Clear(); }
        var next = new Dictionary<Guid, ProxyConnectionSample>();
        foreach (var connection in observation.Connections)
        {
            next.Add(connection.Id, connection);
            if (!previousConnections.TryGetValue(connection.Id, out var prior) || prior.Started != connection.Started)
            {
                baselines = checked(baselines + 1);
                continue;
            }

            if (!TryDelta(connection.Total, prior.Total, out var delta))
            {
                resets = checked(resets + 1);
                continue;
            }

            connectionObserved = (connectionObserved ?? default).Add(delta);
            var owner = prior.Owner is not null && prior.Owner == connection.Owner ? connection.Owner : null;
            var key = new RowKey(owner, prior.Ingress == connection.Ingress ? connection.Ingress : ProxyIngress.Unknown,
                prior.Route == connection.Route ? connection.Route : ProxyRoute.Unknown);
            if (!rows.ContainsKey(key) && rows.Count >= maximumRows)
            {
                capacity = true;
                overflow = overflow.Add(delta);
                continue;
            }

            rows[key] = rows.GetValueOrDefault(key).Add(delta);
        }

        closed = checked(closed + (ulong)previousConnections.Keys.Count(id => !next.ContainsKey(id)));
        previousConnections = next;
        previous = observation;
    }

    public ProxyUsageSnapshot Snapshot() => new(coreObserved, connectionObserved,
        rows.Select(item => new ProxyUsageRow(item.Key.Owner, item.Key.Ingress, item.Key.Route, item.Value)).ToArray(),
        overflow, active, gaps, resets, baselines, closed, capacity);

    private static bool TryDelta(TrafficVolume current, TrafficVolume prior, out TrafficVolume delta)
    {
        delta = default;
        if (current.ReceivedBytes < prior.ReceivedBytes || current.SentBytes < prior.SentBytes) { return false; }
        delta = new(current.ReceivedBytes - prior.ReceivedBytes, current.SentBytes - prior.SentBytes);
        return true;
    }
}
