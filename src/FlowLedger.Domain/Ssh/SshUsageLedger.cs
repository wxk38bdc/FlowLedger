using FlowLedger.Domain.Accounting;

namespace FlowLedger.Domain.Ssh;

public sealed record SshServerRule(string Address, int Port, string Alias);
public sealed record TcpFlow(string LocalAddress, int LocalPort, string RemoteAddress, int RemotePort);
public enum SshEvidence { ClientCandidate, UserRule }
public static class SshEndpoint
{
    public static string Format(string address, int port) => address.Contains(':') ? $"[{address}]:{port}" : $"{address}:{port}";
}
public sealed record SshConnectionUsage(string Key, string Server, int Port, string Alias, string Client,
    int ProcessId, long ProcessStartQpc, int LocalPort, long FirstSeenQpc, bool Closed, SshEvidence Evidence, TrafficVolume Traffic);
public sealed record SshLedgerSnapshot(IReadOnlyList<SshConnectionUsage> Connections, TrafficVolume? Observed,
    TrafficVolume Unidentified, bool IdentityUncertain, bool CapacityReached);

/// <summary>Classifies metadata, never SSH payloads. Client names indicate candidates, not protocol proof.
/// SSH is a subset of application ETW bytes; it must never be added to that layer.</summary>
public sealed class SshUsageLedger(IReadOnlyList<SshServerRule> rules, int maximumConnections = 4096)
{
    private readonly Dictionary<(int Pid, long Start, TcpFlow Flow), string> active = [];
    private readonly Dictionary<string, SshConnectionUsage> rows = [];
    private readonly HashSet<(int Pid, long Start, TcpFlow Flow)> outbound = [];
    private TrafficVolume? observed;
    private TrafficVolume unknown;
    private bool uncertain;
    private bool capacity;
    private long sequence;

    public static bool IsClient(string name) => name.ToLowerInvariant() is
        "ssh" or "ssh.exe" or "scp" or "scp.exe" or "sftp" or "sftp.exe" or
        "xshell" or "xshell.exe" or "xshellcore" or "xshellcore.exe" or
        "putty" or "putty.exe" or "plink" or "plink.exe";

    public void Observe(ProcessTraffic? process, long qpc, TcpFlow flow, bool received, uint bytes)
    {
        if (process is null || !IsClient(process.Name)) { return; }
        var rule = rules.SingleOrDefault(item => item.Address == flow.RemoteAddress && item.Port == flow.RemotePort);
        var identity = (process.ProcessId, process.FirstSeenQpc, flow);
        if (rule is null && flow.RemotePort != 22 && !outbound.Contains(identity)) { return; }
        var volume = received ? new TrafficVolume(bytes, 0) : new TrafficVolume(0, bytes);
        observed = (observed ?? default).Add(volume);
        if (!active.TryGetValue(identity, out var key))
        {
            if (rows.Count >= maximumConnections) { capacity = true; unknown = unknown.Add(volume); return; }
            key = $"{process.ProcessId}:{process.FirstSeenQpc}:{++sequence}";
            active.Add(identity, key);
            rows.Add(key, new(key, flow.RemoteAddress, flow.RemotePort, rule?.Alias ?? flow.RemoteAddress,
                process.Name, process.ProcessId, process.FirstSeenQpc, flow.LocalPort, qpc, false,
                rule is null ? SshEvidence.ClientCandidate : SshEvidence.UserRule, default));
        }
        rows[key] = rows[key] with { Traffic = rows[key].Traffic.Add(volume) };
    }

    public void Connected(ProcessTraffic? process, TcpFlow flow)
    {
        if (process is null || !IsClient(process.Name)) { return; }
        Close(process.ProcessId, flow);
        if (outbound.Count < maximumConnections) { outbound.Add((process.ProcessId, process.FirstSeenQpc, flow)); }
        else { capacity = true; }
    }

    public void Close(int pid, TcpFlow? flow = null)
    {
        outbound.RemoveWhere(item => item.Pid == pid && (flow is null || item.Flow == flow));
        foreach (var item in active.Keys.Where(item => item.Pid == pid && (flow is null || item.Flow == flow)).ToArray())
        {
            var key = active[item];
            rows[key] = rows[key] with { Closed = true };
            active.Remove(item);
        }
    }

    public void InvalidateIdentities()
    {
        foreach (var row in rows.Values) { unknown = unknown.Add(row.Traffic); }
        rows.Clear(); active.Clear(); outbound.Clear(); uncertain = true;
    }

    public SshLedgerSnapshot Snapshot() => new(rows.Values.ToArray(), observed, unknown, uncertain, capacity);
}
