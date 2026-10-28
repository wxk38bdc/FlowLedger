using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowLedger.Application.Monitoring;
using FlowLedger.Domain.Accounting;
using FlowLedger.Infrastructure.Windows.Monitoring;

namespace FlowLedger.Collector;

internal static class ApplicationCommands
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static async Task<int> WatchAsync(int seconds)
    {
        await using var source = new WindowsApplicationTrafficSource();
        var snapshot = source.Read();
        if (snapshot.State == ApplicationCaptureState.Running)
        {
            for (var index = 0; index < seconds; index++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                snapshot = source.Read();
                if (snapshot.State != ApplicationCaptureState.Running)
                {
                    break;
                }
            }

            await source.StopAsync();
            snapshot = source.Read();
        }

        PrintSummary(snapshot);
        return snapshot.State == ApplicationCaptureState.Stopped && snapshot.Ledger.Observed is not null &&
            snapshot.LossCounterAvailable && snapshot.Ledger.LostEvents == 0 && snapshot.Ledger.RejectedEvents == 0 &&
            !snapshot.Ledger.IdentityUncertain && !snapshot.Ledger.CapacityReached ? 0 : 1;
    }

    internal static async Task<int> VerifyAsync()
    {
        await using var source = new WindowsApplicationTrafficSource();
        if (source.Read().State != ApplicationCaptureState.Running)
        {
            PrintSummary(source.Read());
            return 1;
        }

        var deliverySucceeded = false;
        try
        {
            // Give initial process rundown and the real-time consumer time to arrive.
            await Task.Delay(TimeSpan.FromSeconds(2));
            source.Read();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
            {
                await ProbeTcpAsync(address, deadline.Token);
                await ProbeUdpAsync(address, deadline.Token);
            }

            deliverySucceeded = true;
            for (var index = 0; index < 3; index++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                source.Read();
            }
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or IOException)
        {
            // Do not print endpoints or exception messages. Failure is inconclusive, never zero usage.
        }

        await source.StopAsync();
        var snapshot = source.Read();
        var own = snapshot.Ledger.Processes.Where(row => row.ProcessId == Environment.ProcessId).ToArray();
        var transports = Enum.GetValues<NetworkTransport>().Select(transport =>
        {
            var traffic = own.Select(row => Volume(row.Traffic, transport)).Aggregate(default(TrafficVolume), (total, next) => total.Add(next));
            return new
            {
                Transport = transport,
                ExpectedSocketBytesPerDirection = transport is NetworkTransport.Tcp4 or NetworkTransport.Tcp6 ? 131072 : 8192,
                ObservedEtwSize = traffic,
                BothDirectionsObserved = traffic.ReceivedBytes > 0 && traffic.SentBytes > 0,
            };
        }).ToArray();
        var coverage = deliverySucceeded && transports.All(item => item.BothDirectionsObserved) &&
            snapshot.State == ApplicationCaptureState.Stopped && snapshot.LossCounterAvailable &&
            snapshot.Ledger.LostEvents == 0 && snapshot.Ledger.RejectedEvents == 0 &&
            !snapshot.Ledger.IdentityUncertain && !snapshot.Ledger.CapacityReached && PartitionsMatch(snapshot.Ledger);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Scope = "Controlled local loopback coverage and PID attribution only; not size accuracy, Internet, QUIC or throughput acceptance",
            DeliverySucceeded = deliverySucceeded,
            CoveragePassed = coverage,
            snapshot.State,
            snapshot.ErrorCode,
            snapshot.LossCounterAvailable,
            snapshot.Ledger.LostEvents,
            snapshot.Ledger.RejectedEvents,
            PartitionsMatch = PartitionsMatch(snapshot.Ledger),
            Transports = transports,
        }, JsonOptions));
        return coverage ? 0 : 1;
    }

    private static void PrintSummary(ApplicationTrafficSnapshot snapshot) => Console.WriteLine(JsonSerializer.Serialize(new
    {
        snapshot.State,
        snapshot.ErrorCode,
        snapshot.LossCounterAvailable,
        snapshot.Ledger.NetworkEvents,
        snapshot.Ledger.RejectedEvents,
        snapshot.Ledger.LostEvents,
        snapshot.Ledger.CapacityReached,
        snapshot.Ledger.IdentityUncertain,
        snapshot.Ledger.Observed,
        snapshot.Ledger.Unidentified,
        PartitionsMatch = PartitionsMatch(snapshot.Ledger),
        Processes = snapshot.Ledger.Processes.Select((row, index) => new { Slot = index + 1, row.IsRundown, row.Traffic }),
    }, JsonOptions));

    internal static bool PartitionsMatch(ApplicationLedgerSnapshot ledger) => ledger.Observed is null
        ? ledger.NetworkEvents == 0
        : Enum.GetValues<NetworkTransport>().All(transport =>
            ledger.Processes.Select(row => Volume(row.Traffic, transport))
                .Aggregate(Volume(ledger.Unidentified, transport), (sum, next) => sum.Add(next)) == Volume(ledger.Observed.Value, transport));

    private static TrafficVolume Volume(TransportTraffic traffic, NetworkTransport transport) => transport switch
    {
        NetworkTransport.Tcp4 => traffic.Tcp4,
        NetworkTransport.Tcp6 => traffic.Tcp6,
        NetworkTransport.Udp4 => traffic.Udp4,
        NetworkTransport.Udp6 => traffic.Udp6,
        _ => throw new ArgumentOutOfRangeException(nameof(transport)),
    };

    private static async Task ProbeTcpAsync(IPAddress address, CancellationToken token)
    {
        var listener = new TcpListener(address, 0);
        listener.Start();
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            var accepting = listener.AcceptTcpClientAsync(token);
            await client.ConnectAsync(address, ((IPEndPoint)listener.LocalEndpoint).Port, token);
            using var peer = await accepting;
            var sent = new byte[131072];
            var received = new byte[sent.Length];
            await Task.WhenAll(client.GetStream().WriteAsync(sent, token).AsTask(), peer.GetStream().ReadExactlyAsync(received, token).AsTask());
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task ProbeUdpAsync(IPAddress address, CancellationToken token)
    {
        using var receiver = new UdpClient(new IPEndPoint(address, 0));
        using var sender = new UdpClient(address.AddressFamily);
        var endpoint = (IPEndPoint)receiver.Client.LocalEndPoint!;
        var buffer = new byte[1024];
        for (var index = 0; index < 8; index++)
        {
            await sender.SendAsync(buffer, endpoint, token);
            var received = await receiver.ReceiveAsync(token);
            if (received.Buffer.Length != buffer.Length)
            {
                throw new IOException("Unexpected local datagram size.");
            }
        }
    }
}
