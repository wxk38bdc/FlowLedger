namespace FlowLedger.Domain.Accounting;

/// <summary>A candidate identity within one interface, not proof of a globally unique WLAN.</summary>
public sealed record WifiNetworkIdentity(
    string SsidHex, string ProfileName, Guid NetworkContext, uint BssType, uint Authentication, uint Cipher);

public sealed record WifiEndpoint(
    WifiNetworkIdentity? Network,
    string? DisplayName,
    string? AccessPoint,
    long ChangeSequence,
    bool NotificationsComplete,
    bool StableDuringRead);

public sealed record WifiNetworkUsage(WifiNetworkIdentity Network, string DisplayName, TrafficVolume Volume);

public sealed record WifiLedgerSnapshot(
    IReadOnlyList<WifiNetworkUsage> Networks,
    TrafficVolume? ObservedTotal,
    TrafficVolume BoundaryUnallocated,
    TrafficVolume Unidentified,
    int BoundaryIntervals,
    int UnidentifiedIntervals);

/// <summary>Partitions valid interface intervals without inventing bytes across counter gaps.</summary>
public sealed class WifiUsageLedger
{
    private readonly Dictionary<WifiNetworkIdentity, WifiNetworkUsage> networks = [];
    private WifiEndpoint? previous;
    private TrafficVolume? observedTotal;
    private TrafficVolume boundary;
    private TrafficVolume unidentified;
    private int boundaryIntervals;
    private int unidentifiedIntervals;

    public WifiLedgerSnapshot Observe(TrafficVolume? interval, WifiEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (previous is not null && interval is { } bytes)
        {
            // Compute checked values before changing state, so overflow is atomic.
            var total = (observedTotal ?? default).Add(bytes);
            if (!previous.NotificationsComplete || !endpoint.NotificationsComplete)
            {
                unidentified = unidentified.Add(bytes);
                unidentifiedIntervals = checked(unidentifiedIntervals + 1);
            }
            else if (!previous.StableDuringRead || !endpoint.StableDuringRead ||
                previous.ChangeSequence != endpoint.ChangeSequence ||
                (previous.Network is not null && endpoint.Network is not null &&
                 (previous.Network != endpoint.Network || previous.AccessPoint != endpoint.AccessPoint)))
            {
                boundary = boundary.Add(bytes);
                boundaryIntervals = checked(boundaryIntervals + 1);
            }
            else if (previous.Network is null || endpoint.Network is null)
            {
                unidentified = unidentified.Add(bytes);
                unidentifiedIntervals = checked(unidentifiedIntervals + 1);
            }
            else
            {
                var key = endpoint.Network;
                var existing = networks.TryGetValue(key, out var usage) ? usage.Volume : default;
                networks[key] = new(key, endpoint.DisplayName ?? "（未知名称）", existing.Add(bytes));
            }

            observedTotal = total;
        }

        previous = endpoint;
        return Snapshot();
    }

    public void BreakContinuity() => previous = null;

    public WifiLedgerSnapshot Snapshot() =>
        new(networks.Values.ToArray(), observedTotal, boundary, unidentified, boundaryIntervals, unidentifiedIntervals);
}
