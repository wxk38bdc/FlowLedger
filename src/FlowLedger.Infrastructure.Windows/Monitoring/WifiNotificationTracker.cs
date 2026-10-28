using FlowLedger.Application.Monitoring;

namespace FlowLedger.Infrastructure.Windows.Monitoring;

/// <summary>Only header fields are consumed. Native payloads are never copied or retained.</summary>
internal sealed class WifiNotificationTracker
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, WifiChangeStamp> stamps = [];
    private long globalSequence;
    private bool globalTransitioning;

    public WifiChangeStamp Read(Guid id)
    {
        lock (gate)
        {
            var local = stamps.GetValueOrDefault(id, new(0, false));
            return local with { Sequence = Math.Max(local.Sequence, globalSequence), Transitioning = local.Transitioning || globalTransitioning };
        }
    }

    public void Accept(uint source, uint code, Guid id)
    {
        var start = source == 8 && code is 9 or 20 || source == 16 && code is 1 or 2 or 3 or 5 or 7 or 9;
        var end = source == 8 && code is 10 or 11 or 14 or 21 || source == 16 && code is 4 or 6 or 10 or 13;
        var changed = start || end || source == 8 && code is 1 or 2 or 5 or 13 or 15 or 16 or 27 || source == 16 && code == 14;
        if (!changed)
        {
            return;
        }

        lock (gate)
        {
            // Empty interface IDs invalidate all observations conservatively.
            var sequence = checked(Math.Max(globalSequence, stamps.Values.Select(value => value.Sequence).DefaultIfEmpty().Max()) + 1);
            if (id == Guid.Empty)
            {
                globalSequence = sequence;
                globalTransitioning = start || !end && globalTransitioning;
                foreach (var key in stamps.Keys.ToArray())
                {
                    stamps[key] = new(sequence, start || !end && stamps[key].Transitioning);
                }
            }
            else
            {
                var previous = stamps.GetValueOrDefault(id, new(0, false));
                stamps[id] = new(sequence, start || !end && previous.Transitioning);
            }
        }
    }
}
