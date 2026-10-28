namespace FlowLedger.Application.Monitoring;

public enum WifiConnectionState
{
    Available,
    Disconnected,
    AccessDenied,
    Unavailable,
}

/// <summary>Current local connection evidence; never read profile credentials.</summary>
public sealed record WifiConnection(
    WifiConnectionState State, string? DisplayName = null, uint ErrorCode = 0,
    string? SsidHex = null, string? ProfileName = null, string? AccessPoint = null,
    uint BssType = 0, uint Authentication = 0, uint Cipher = 0);

public interface IWifiConnectionReader
{
    WifiConnection Read(Guid interfaceId);
}
