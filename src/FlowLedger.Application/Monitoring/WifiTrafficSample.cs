using FlowLedger.Domain.Accounting;

namespace FlowLedger.Application.Monitoring;

public enum WifiNotificationCoverage
{
    Unavailable,
    AcmOnly,
    Full,
}

public sealed record WifiChangeStamp(long Sequence, bool Transitioning);

public sealed record WifiTrafficObservation(WifiEndpoint Endpoint, WifiConnectionState State, uint ErrorCode);

public sealed record WifiTrafficSample(
    InterfaceCounterSnapshot Counters,
    IReadOnlyDictionary<string, WifiTrafficObservation> Observations,
    WifiNotificationCoverage Coverage,
    uint NotificationError);

public interface IWifiTrafficSource : IDisposable
{
    WifiTrafficSample Read();
}

public sealed class WifiLedgerCoordinator
{
    private readonly Dictionary<string, WifiUsageLedger> ledgers = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, WifiLedgerSnapshot> Apply(WifiTrafficSample sample, InterfaceMonitorSnapshot monitor)
    {
        foreach (var item in monitor.Interfaces.Where(item => item.Interface is { Kind: InterfaceKind.Hardware, NativeType: 71 }))
        {
            if (!ledgers.TryGetValue(item.Interface.Id, out var ledger))
            {
                ledger = new();
                ledgers.Add(item.Interface.Id, ledger);
            }

            var endpoint = sample.Observations.TryGetValue(item.Interface.Id, out var observation) && item.IsPresent
                ? observation.Endpoint : new WifiEndpoint(null, null, null, 0, false, false);
            ledger.Observe(item.Usage.Interval, endpoint);
        }

        return ledgers.ToDictionary(pair => pair.Key, pair => pair.Value.Snapshot(), StringComparer.Ordinal);
    }

    public void BreakContinuity()
    {
        foreach (var ledger in ledgers.Values)
        {
            ledger.BreakContinuity();
        }
    }
}
