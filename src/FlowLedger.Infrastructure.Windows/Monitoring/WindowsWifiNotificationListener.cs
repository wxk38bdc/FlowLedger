using System.Runtime.InteropServices;
using FlowLedger.Application.Monitoring;

namespace FlowLedger.Infrastructure.Windows.Monitoring;

internal sealed class WindowsWifiNotificationListener : IDisposable
{
    private readonly WifiNotificationTracker tracker = new();
    private readonly NativeNetworkMethods.WifiNotificationCallback callback;
    private IntPtr handle;
    private int disposed;
    private int callbackFailed;

    public WindowsWifiNotificationListener()
    {
        callback = OnNotification;
        ErrorCode = NativeNetworkMethods.WlanOpenHandle(2, IntPtr.Zero, out _, out handle);
        if (ErrorCode != 0)
        {
            return;
        }

        ErrorCode = NativeNetworkMethods.WlanRegisterNotification(handle, 8 | 16, false, callback, IntPtr.Zero, IntPtr.Zero, out _);
        if (ErrorCode == 0)
        {
            Coverage = WifiNotificationCoverage.Full;
        }
        else
        {
            var fallback = NativeNetworkMethods.WlanRegisterNotification(handle, 8, false, callback, IntPtr.Zero, IntPtr.Zero, out _);
            Coverage = fallback == 0 ? WifiNotificationCoverage.AcmOnly : WifiNotificationCoverage.Unavailable;
        }
    }

    public WifiNotificationCoverage Coverage { get; }
    public uint ErrorCode { get; }
    public bool CallbackFailed => Volatile.Read(ref callbackFailed) != 0;
    public WifiChangeStamp Read(Guid id) => tracker.Read(id);

    private void OnNotification(IntPtr address, IntPtr context)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        try
        {
            var data = Marshal.PtrToStructure<NativeWifiNotification>(address);
            tracker.Accept(data.Source, data.Code, data.InterfaceGuid);
        }
        catch (Exception)
        {
            // Exceptions must not escape a reverse P/Invoke callback. Fail closed.
            Interlocked.Exchange(ref callbackFailed, 1);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        if (handle != IntPtr.Zero)
        {
            // Never hold the tracker lock while native unregistration waits for callbacks.
            NativeNetworkMethods.WlanRegisterNotification(handle, 0, false, null, IntPtr.Zero, IntPtr.Zero, out _);
            NativeNetworkMethods.WlanCloseHandle(handle, IntPtr.Zero);
            handle = IntPtr.Zero;
        }

        GC.KeepAlive(callback);
    }
}
