using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using FlowLedger.Application.Diagnostics;

namespace FlowLedger.Infrastructure.Windows.Diagnostics;

public sealed class WindowsHostDiagnosticsReader : IHostDiagnosticsReader
{
    public HostEnvironmentSnapshot Read()
    {
        IReadOnlyList<NetworkInterfaceGroup> groups;
        var inventoryState = NetworkInventoryState.Available;

        try
        {
            groups = Array.AsReadOnly(NetworkInterface.GetAllNetworkInterfaces()
                .GroupBy(adapter => new
                {
                    Type = adapter.NetworkInterfaceType.ToString(),
                    IsUp = adapter.OperationalStatus == OperationalStatus.Up,
                })
                .Select(group => new NetworkInterfaceGroup(group.Key.Type, group.Key.IsUp, group.Count()))
                .OrderBy(group => group.InterfaceType, StringComparer.Ordinal)
                .ThenByDescending(group => group.IsUp)
                .ToArray());
        }
        catch (Exception exception) when (exception is NetworkInformationException or UnauthorizedAccessException)
        {
            inventoryState = NetworkInventoryState.Unavailable;
            groups = Array.Empty<NetworkInterfaceGroup>();
        }

        return new HostEnvironmentSnapshot(
            DateTimeOffset.UtcNow,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            Environment.ProcessorCount,
            inventoryState,
            groups);
    }
}
