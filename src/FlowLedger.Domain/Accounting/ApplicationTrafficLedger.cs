namespace FlowLedger.Domain.Accounting;

public enum NetworkTransport
{
    Tcp4,
    Tcp6,
    Udp4,
    Udp6,
}

public readonly record struct TransportTraffic(
    TrafficVolume Tcp4, TrafficVolume Tcp6, TrafficVolume Udp4, TrafficVolume Udp6)
{
    public TrafficVolume Total => Tcp4.Add(Tcp6).Add(Udp4).Add(Udp6);

    public TransportTraffic Add(TransportTraffic other) => new(
        Tcp4.Add(other.Tcp4), Tcp6.Add(other.Tcp6), Udp4.Add(other.Udp4), Udp6.Add(other.Udp6));

    public TransportTraffic Add(NetworkTransport transport, TrafficVolume volume) => transport switch
    {
        NetworkTransport.Tcp4 => this with { Tcp4 = Tcp4.Add(volume) },
        NetworkTransport.Tcp6 => this with { Tcp6 = Tcp6.Add(volume) },
        NetworkTransport.Udp4 => this with { Udp4 = Udp4.Add(volume) },
        NetworkTransport.Udp6 => this with { Udp6 = Udp6.Add(volume) },
        _ => throw new ArgumentOutOfRangeException(nameof(transport)),
    };
}

public sealed record ProcessTraffic(
    int ProcessId, long FirstSeenQpc, string Name, bool IsRundown, TransportTraffic Traffic);

public sealed record ApplicationLedgerSnapshot(
    IReadOnlyList<ProcessTraffic> Processes, TransportTraffic Unidentified, TransportTraffic? Observed,
    ulong NetworkEvents, ulong RejectedEvents, ulong LostEvents, bool CapacityReached, bool IdentityUncertain);

/// <summary>One capture epoch. Unknown or ambiguous lifetimes never acquire a process name later.</summary>
public sealed class ApplicationTrafficLedger(int maximumLifetimes = 4096)
{
    private sealed class Lifetime(int pid, long start, string name, ulong key, bool rundown)
    {
        public int Pid { get; } = pid;
        public long Start { get; } = start;
        public long End { get; set; } = long.MaxValue;
        public string Name { get; } = name;
        public ulong Key { get; } = key;
        public bool Rundown { get; } = rundown;
        public bool HasTraffic { get; set; }
        public TransportTraffic Traffic { get; set; }
    }

    private readonly Dictionary<int, List<Lifetime>> lifetimes = [];
    private readonly List<Lifetime> retained = [];
    private TransportTraffic unidentified;
    private TransportTraffic? observed;
    private ulong networkEvents;
    private ulong rejectedEvents;
    private ulong lostEvents;
    private bool capacityReached;
    private bool identityUncertain;
    private long latestQpc = -1;

    public void ProcessStarted(int pid, long qpc, string name, ulong key, bool isRundown = false)
    {
        // Windows rundown includes the idle pseudo-process. It has no application identity;
        // its presence is not a malformed event or a reason to fail capture health.
        if (pid == 0)
        {
            return;
        }

        if (!AcceptTimestamp(qpc))
        {
            return;
        }

        if (pid <= 0 || string.IsNullOrWhiteSpace(name))
        {
            RejectEvent();
            lifetimes.Remove(pid);
            return;
        }

        lifetimes.TryGetValue(pid, out var history);
        if (history?.Any(item => item.Start == qpc || (isRundown && key != 0 && item.Key == key && item.End == long.MaxValue)) == true)
        {
            return;
        }

        // Even when capacity is exhausted, close the old PID before refusing a new identity.
        var previous = history?.LastOrDefault(item => item.Start < qpc);
        if (previous is not null)
        {
            previous.End = Math.Min(previous.End, qpc);
        }

        if (retained.Count >= maximumLifetimes)
        {
            capacityReached = true;
            return;
        }

        var next = history?.FirstOrDefault(item => item.Start > qpc);
        var lifetime = new Lifetime(pid, qpc, name, key, isRundown) { End = next?.Start ?? long.MaxValue };
        history ??= lifetimes[pid] = [];
        history.Add(lifetime);
        history.Sort((left, right) => left.Start.CompareTo(right.Start));
        retained.Add(lifetime);
    }

    public void ProcessStopped(int pid, long qpc, ulong key)
    {
        if (!AcceptTimestamp(qpc))
        {
            return;
        }

        var lifetime = Find(pid, qpc);
        if (lifetime is not null && (key == 0 || lifetime.Key == key))
        {
            lifetime.End = Math.Min(lifetime.End, qpc);
        }
    }

    public void Observe(int payloadPid, long qpc, NetworkTransport transport, bool received, uint size)
    {
        var ordered = AcceptTimestamp(qpc);
        var volume = received ? new TrafficVolume(size, 0) : new TrafficVolume(0, size);
        observed = (observed ?? default).Add(transport, volume);
        networkEvents = checked(networkEvents + 1);
        var lifetime = ordered ? Find(payloadPid, qpc) : null;
        if (lifetime is null)
        {
            unidentified = unidentified.Add(transport, volume);
            return;
        }

        lifetime.Traffic = lifetime.Traffic.Add(transport, volume);
        lifetime.HasTraffic = true;
    }

    public void RejectEvent() => rejectedEvents = checked(rejectedEvents + 1);

    public void ReportLostEvents(ulong cumulative)
    {
        if (cumulative > lostEvents)
        {
            lostEvents = cumulative;
            InvalidateIdentities();
        }
    }

    public void InvalidateIdentities()
    {
        // Loss/late lifecycle evidence has no precise boundary. Revoke existing attribution rather
        // than leave possibly misnamed bytes after a hidden PID reuse. Observed bytes are conserved.
        foreach (var lifetime in retained)
        {
            unidentified = unidentified.Add(lifetime.Traffic);
        }

        lifetimes.Clear();
        retained.Clear();
        identityUncertain = true;
    }

    public ApplicationLedgerSnapshot Snapshot() => new(
        retained.Where(item => item.HasTraffic)
            .Select(item => new ProcessTraffic(item.Pid, item.Start, item.Name, item.Rundown, item.Traffic)).ToArray(),
        unidentified, observed, networkEvents, rejectedEvents, lostEvents, capacityReached, identityUncertain);

    private Lifetime? Find(int pid, long qpc) => lifetimes.TryGetValue(pid, out var history)
        ? history.LastOrDefault(item => item.Start <= qpc && qpc < item.End)
        : null;

    private bool AcceptTimestamp(long qpc)
    {
        if (qpc < 0 || qpc < latestQpc)
        {
            InvalidateIdentities();
            return false;
        }

        latestQpc = qpc;
        return true;
    }
}
