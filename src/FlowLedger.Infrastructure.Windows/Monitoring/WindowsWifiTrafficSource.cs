using FlowLedger.Application.Monitoring;
using FlowLedger.Domain.Accounting;

namespace FlowLedger.Infrastructure.Windows.Monitoring;

/// <summary>Explicit opt-in. WLAN reads bracket the counter sample and notification sequence.</summary>
public sealed class WindowsWifiTrafficSource(IInterfaceCounterReader counterReader) : IWifiTrafficSource
{
    private readonly WindowsWifiConnectionReader wifiReader = new();
    private readonly WindowsWifiNotificationListener notifications = new();

    public WifiTrafficSample Read()
    {
        var inventory = counterReader.Read();
        var before = new Dictionary<string, WifiProbe>();
        foreach (var item in inventory.Interfaces.Where(IsWifi))
        {
            var id = Guid.Parse(item.Id);
            var stamp = notifications.Read(id);
            before.Add(item.Id, new(item, wifiReader.Read(id), stamp));
        }

        var counters = counterReader.Read();
        var observations = new Dictionary<string, WifiTrafficObservation>();
        foreach (var item in counters.Interfaces.Where(IsWifi))
        {
            var id = Guid.Parse(item.Id);
            var connection = wifiReader.Read(id);
            var stamp = notifications.Read(id);
            var complete = notifications.Coverage == WifiNotificationCoverage.Full && !notifications.CallbackFailed;
            observations.Add(item.Id, CreateObservation(before.GetValueOrDefault(item.Id), new(item, connection, stamp), complete));
        }

        return new(counters, observations,
            notifications.CallbackFailed ? WifiNotificationCoverage.Unavailable : notifications.Coverage,
            notifications.CallbackFailed ? 13u : notifications.ErrorCode);
    }

    internal static WifiTrafficObservation CreateObservation(WifiProbe? before, WifiProbe after, bool complete)
    {
        var network = Identity(after.Interface, after.Connection);
        var stable = before is not null && before.Stamp.Sequence == after.Stamp.Sequence &&
            !before.Stamp.Transitioning && !after.Stamp.Transitioning &&
            before.Interface.IsUp && after.Interface.IsUp &&
            before.Interface.ContinuityKey == after.Interface.ContinuityKey &&
            Identity(before.Interface, before.Connection) == network &&
            before.Connection.State == after.Connection.State && before.Connection.AccessPoint == after.Connection.AccessPoint;
        return new(new(network, after.Connection.DisplayName, after.Connection.AccessPoint,
            after.Stamp.Sequence, complete, stable), after.Connection.State, after.Connection.ErrorCode);
    }

    internal static WifiNetworkIdentity? Identity(InterfaceCounter item, WifiConnection connection) =>
        connection.State == WifiConnectionState.Available && connection.SsidHex is not null &&
        connection.ProfileName is not null && item.NetworkContext != Guid.Empty && item.IsUp
            ? new(connection.SsidHex, connection.ProfileName, item.NetworkContext,
                connection.BssType, connection.Authentication, connection.Cipher) : null;

    private static bool IsWifi(InterfaceCounter item) =>
        item.Kind == InterfaceKind.Hardware && item.NativeType == 71 && Guid.TryParse(item.Id, out _);

    public void Dispose() => notifications.Dispose();
}

internal sealed record WifiProbe(InterfaceCounter Interface, WifiConnection Connection, WifiChangeStamp Stamp);
