namespace FlowLedger.Application.Monitoring;

public enum WifiConnectionState
{
    Available,
    Disconnected,
    AccessDenied,
    Unavailable,
}

/// <summary>Current local display only. Not a permanent network identity or historical attribution.</summary>
public sealed record WifiConnection(WifiConnectionState State, string? DisplayName = null, uint ErrorCode = 0);

public interface IWifiConnectionReader
{
    WifiConnection Read(Guid interfaceId);
}
