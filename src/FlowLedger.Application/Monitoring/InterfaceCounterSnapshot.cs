using FlowLedger.Domain.Accounting;

namespace FlowLedger.Application.Monitoring;

public enum InterfaceKind
{
    Hardware,
    Tunnel,
    Loopback,
    VirtualOrOther,
    Filter,
}

public sealed record InterfaceCounter(
    string Id,
    string Name,
    InterfaceKind Kind,
    uint NativeType,
    bool IsUp,
    bool HardwareFlag,
    bool ConnectorPresent,
    string ContinuityKey,
    CounterReading Reading);

public sealed record InterfaceCounterSnapshot(
    DateTimeOffset CapturedAt,
    TimeSpan MonotonicTime,
    IReadOnlyList<InterfaceCounter> Interfaces,
    uint ErrorCode = 0);

public interface IInterfaceCounterReader
{
    InterfaceCounterSnapshot Read();
}

public sealed record MonitoredInterface(InterfaceCounter Interface, TrafficSessionUpdate Usage, bool IsPresent);

public sealed record InterfaceMonitorSnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyList<MonitoredInterface> Interfaces,
    uint ErrorCode);
