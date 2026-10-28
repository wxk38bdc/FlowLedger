namespace FlowLedger.Application.Diagnostics;

public enum NetworkInventoryState
{
    Available,
    Unavailable,
}

public sealed record NetworkInterfaceGroup(string InterfaceType, bool IsUp, int Count);

/// <summary>Read-only metadata without interface names, addresses, SSIDs, or traffic.</summary>
public sealed record HostEnvironmentSnapshot(
    DateTimeOffset CapturedAtUtc,
    string OperatingSystem,
    string Architecture,
    string RuntimeVersion,
    int LogicalProcessorCount,
    NetworkInventoryState NetworkInventoryState,
    IReadOnlyList<NetworkInterfaceGroup> InterfaceGroups);
