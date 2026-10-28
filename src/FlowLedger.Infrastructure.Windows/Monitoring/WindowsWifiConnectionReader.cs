using System.Runtime.InteropServices;
using System.Text;
using FlowLedger.Application.Monitoring;

namespace FlowLedger.Infrastructure.Windows.Monitoring;

/// <summary>Explicit, on-demand query; never scans or changes location permissions.</summary>
public sealed class WindowsWifiConnectionReader : IWifiConnectionReader
{
    public WifiConnection Read(Guid interfaceId)
    {
        var error = NativeNetworkMethods.WlanOpenHandle(2, IntPtr.Zero, out _, out var handle);
        if (error != 0)
        {
            return Failure(error);
        }

        var data = IntPtr.Zero;
        try
        {
            // wlan_intf_opcode_current_connection = 7 (wlanapi.h).
            error = NativeNetworkMethods.WlanQueryInterface(handle, in interfaceId, 7,
                IntPtr.Zero, out var size, out data, out _);
            if (error != 0)
            {
                return Failure(error);
            }

            if (size < Marshal.SizeOf<NativeWifiConnection>())
            {
                return new(WifiConnectionState.Unavailable, ErrorCode: 13);
            }

            var connection = Marshal.PtrToStructure<NativeWifiConnection>(data);
            if (connection.State != 1)
            {
                return new(WifiConnectionState.Disconnected);
            }

            if (connection.Association.SsidLength > 32)
            {
                return new(WifiConnectionState.Unavailable, ErrorCode: 13);
            }

            var ssid = connection.Association.Ssid.AsSpan(0, (int)connection.Association.SsidLength);
            return new(WifiConnectionState.Available, DisplaySsid(ssid),
                SsidHex: Convert.ToHexString(ssid), ProfileName: connection.Profile,
                AccessPoint: Convert.ToHexString(connection.Association.Bssid),
                BssType: connection.Association.BssType,
                Authentication: connection.Security[2], Cipher: connection.Security[3]);
        }
        finally
        {
            if (data != IntPtr.Zero)
            {
                NativeNetworkMethods.WlanFreeMemory(data);
            }

            NativeNetworkMethods.WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    internal static WifiConnection Failure(uint error) => new(
        error == 5 ? WifiConnectionState.AccessDenied
        : error == 5023 ? WifiConnectionState.Disconnected
        : WifiConnectionState.Unavailable, ErrorCode: error);

    internal static string DisplaySsid(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return "（空 SSID）";
        }

        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            // Non-UTF8 or control characters are shown losslessly as hex, not guessed.
            return text.Any(char.IsControl) ? $"0x{Convert.ToHexString(bytes)}" : text;
        }
        catch (DecoderFallbackException)
        {
            return $"0x{Convert.ToHexString(bytes)}";
        }
    }
}
