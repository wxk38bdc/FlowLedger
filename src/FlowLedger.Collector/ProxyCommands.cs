using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowLedger.Application.Proxy;
using FlowLedger.Domain.Proxy;
using FlowLedger.Infrastructure.Windows.Proxy;

namespace FlowLedger.Collector;

internal static class ProxyCommands
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    internal static async Task<int> VerifyOwnersAsync()
    {
        var results = new List<object>();
        var passed = true;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            var listener = new TcpListener(address, 0);
            listener.Start();
            try
            {
                using var client = new TcpClient(address.AddressFamily);
                var accept = listener.AcceptTcpClientAsync(deadline.Token);
                await client.ConnectAsync(address, ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
                using var peer = await accept;
                var local = (IPEndPoint)client.Client.LocalEndPoint!;
                var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
                var snapshot = new WindowsTcpOwners().Read();
                var matched = snapshot.Available && snapshot.Rows.Count(row => row.State == 5 && row.LocalAddress == local.Address.ToString() &&
                    row.LocalPort == local.Port && row.RemoteAddress == remote.Address.ToString() && row.RemotePort == remote.Port &&
                    row.Process?.ProcessId == Environment.ProcessId) == 1;
                results.Add(new { Family = address.AddressFamily == AddressFamily.InterNetwork ? 4 : 6, Matched = matched });
                passed &= matched;
            }
            finally { listener.Stop(); }
        }

        Console.WriteLine(JsonSerializer.Serialize(new { Passed = passed, Cases = results }, JsonOptions));
        return passed ? 0 : 1;
    }

    internal static async Task<int> RunAsync(string endpoint, int proxyPort, int? seconds)
    {
        try { return await RunCoreAsync(endpoint, proxyPort, seconds); }
        catch (ArgumentException)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { State = "InvalidSettings", ErrorCode = 0 }, JsonOptions));
            return 2;
        }
        catch (Exception exception)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { State = "Unavailable", ErrorType = exception.GetType().Name, exception.HResult }, JsonOptions));
            return 1;
        }
    }

    private static async Task<int> RunCoreAsync(string endpoint, int proxyPort, int? seconds)
    {
        // Explicit operator-provided environment value only; never discover a client's credentials.
        var secret = Environment.GetEnvironmentVariable("FLOWLEDGER_CONTROLLER_SECRET") ?? "";
        await using var source = new ClashProxySource(endpoint, secret, proxyPort);
        if (seconds is null)
        {
            var result = await ProxyLocalValidation.RunAsync(source, proxyPort);
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return result.Passed ? 0 : 1;
        }

        var ledger = new ProxyUsageLedger();
        var read = await source.ReadAsync();
        for (var index = 0; ; index++)
        {
            if (read.Observation is { } observation) { ledger.Observe(observation); } else { ledger.BreakContinuity(); }
            if (index >= seconds || read.State == ProxyReadState.AccessDenied) { break; }
            await Task.Delay(1000);
            read = await source.ReadAsync();
        }

        var snapshot = ledger.Snapshot();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            read.State,
            read.ErrorCode,
            snapshot.CoreObserved,
            snapshot.ConnectionObserved,
            snapshot.ActiveConnections,
            snapshot.Gaps,
            snapshot.Resets,
            snapshot.NewBaselines,
            snapshot.ClosedConnections,
            snapshot.CapacityReached,
            snapshot.Overflow,
            Rows = snapshot.Rows.Select((row, index) => new { Slot = index + 1, OwnerConfirmed = row.Owner is not null, row.Ingress, row.Route, row.Traffic }),
            Scope = "One core API accounting layer; partial active-connection deltas, never summed with ETW or physical bytes.",
        }, JsonOptions));
        return read.State == ProxyReadState.Available && snapshot.CoreObserved is not null && snapshot.Gaps == 0 && snapshot.Resets == 0 && !snapshot.CapacityReached ? 0 : 1;
    }
}
