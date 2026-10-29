using System.Net;
using System.Net.Sockets;
using System.Text;
using FlowLedger.Application.Proxy;
using FlowLedger.Domain.Proxy;

namespace FlowLedger.Infrastructure.Windows.Proxy;

public sealed record ProxyLocalValidationResult(bool Passed, bool Delivered, int ConfirmedConnections,
    ulong AttributedDownload, bool CoreObserved, ulong Gaps, ulong Resets, ProxyReadState State,
    int ErrorCode = 0, string CoreVersion = "", int TunConnections = 0, int TunOwnersConfirmed = 0);

public static class ProxyLocalValidation
{
    public static async Task<ProxyLocalValidationResult> RunAsync(IProxyTrafficSource source, int proxyPort, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var ledger = new ProxyUsageLedger();
        var read = await source.ReadAsync(deadline.Token);
        if (read.Observation is null) { return new(false, false, 0, 0, false, 0, 0, read.State, read.ErrorCode, read.Version); }
        ledger.Observe(read.Observation);
        var ids = new HashSet<Guid>();
        var tunIds = new HashSet<Guid>();
        var tunOwners = new HashSet<Guid>();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Task? serving = null;
        Task? transferring = null;
        var delivered = false;
        var started = DateTimeOffset.UtcNow;
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var nonce = Guid.NewGuid().ToString("N");
            serving = ServeAsync(listener, nonce, deadline.Token);
            transferring = TransferAsync(proxyPort, port, nonce, deadline.Token);
            while (!transferring.IsCompleted)
            {
                await Task.Delay(250, deadline.Token);
                read = await source.ReadAsync(deadline.Token);
                if (read.Observation is { } observation)
                {
                    ledger.Observe(observation);
                    foreach (var connection in observation.Connections.Where(connection => connection.Ingress == ProxyIngress.Tun))
                    {
                        tunIds.Add(connection.Id);
                        if (connection.Owner is not null) { tunOwners.Add(connection.Id); }
                    }
                    foreach (var connection in observation.Connections.Where(connection => connection.Started >= started &&
                        connection.Owner?.ProcessId == Environment.ProcessId && connection.Ingress == ProxyIngress.ExplicitProxy)) { ids.Add(connection.Id); }
                }
                else { ledger.BreakContinuity(); }
            }

            await Task.WhenAll(transferring, serving);
            delivered = true;
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or InvalidOperationException) { }
        finally
        {
            await deadline.CancelAsync();
            listener.Stop();
            foreach (var task in new[] { serving, transferring })
            {
                if (task is null) { continue; }
                try { await task; } catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or InvalidOperationException or ObjectDisposedException) { }
            }
        }

        var final = ledger.Snapshot();
        var attributed = final.Rows.Where(row => row.Owner?.ProcessId == Environment.ProcessId && row.Ingress == ProxyIngress.ExplicitProxy)
            .Aggregate(0UL, (sum, row) => checked(sum + row.Traffic.ReceivedBytes));
        var coreObserved = final.CoreObserved is { ReceivedBytes: > 0 };
        var passed = delivered && ids.Count == 1 && attributed is > 0 and <= 1056768 && coreObserved && final.Gaps == 0 && final.Resets == 0 && !final.CapacityReached;
        return new(passed, delivered, ids.Count, attributed, coreObserved, final.Gaps, final.Resets, read.State,
            read.ErrorCode, read.Version, tunIds.Count, tunOwners.Count);
    }

    private static async Task ServeAsync(TcpListener listener, string nonce, CancellationToken token)
    {
        using var peer = await listener.AcceptTcpClientAsync(token);
        var stream = peer.GetStream();
        if (!(await HeaderAsync(stream, token)).Contains(nonce, StringComparison.Ordinal)) { throw new IOException(); }
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 1048576\r\nConnection: close\r\n\r\n"), token);
        var buffer = new byte[16384];
        for (var index = 0; index < 64; index++) { await stream.WriteAsync(buffer, token); await Task.Delay(50, token); }
    }

    private static async Task TransferAsync(int proxyPort, int serverPort, string nonce, CancellationToken token)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxyPort, token);
        var stream = client.GetStream();
        var request = $"GET http://127.0.0.1:{serverPort}/flowledger-{nonce} HTTP/1.1\r\nHost: 127.0.0.1:{serverPort}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), token);
        var header = await HeaderAsync(stream, token);
        if (!header.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) && !header.StartsWith("HTTP/1.0 200", StringComparison.Ordinal)) { throw new IOException(); }
        var buffer = new byte[16384];
        for (var index = 0; index < 64; index++) { await stream.ReadExactlyAsync(buffer, token); }
    }

    private static async Task<string> HeaderAsync(Stream stream, CancellationToken token)
    {
        var buffer = new List<byte>();
        var next = new byte[1];
        while (buffer.Count < 8192)
        {
            await stream.ReadExactlyAsync(next, token);
            buffer.Add(next[0]);
            if (buffer.Count >= 4 && buffer[^4] == 13 && buffer[^3] == 10 && buffer[^2] == 13 && buffer[^1] == 10)
            {
                return Encoding.ASCII.GetString(buffer.ToArray());
            }
        }

        throw new IOException();
    }
}
