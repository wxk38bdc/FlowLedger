using System.Net;
using System.Net.Sockets;
using FlowLedger.Domain.Accounting;

namespace FlowLedger.Collector.Validation;

internal sealed record ProbeParameters(NetworkTransport Transport, int Port, int SendBytes, int ReceiveBytes, int Exchanges)
{
    public IPAddress Address => Transport is NetworkTransport.Tcp4 or NetworkTransport.Udp4 ? IPAddress.Loopback : IPAddress.IPv6Loopback;
    public bool IsTcp => Transport is NetworkTransport.Tcp4 or NetworkTransport.Tcp6;
    public TrafficVolume Expected => new(checked((ulong)ReceiveBytes * (ulong)Exchanges), checked((ulong)SendBytes * (ulong)Exchanges));

    public static bool TryParse(string[] args, out ProbeParameters? parameters)
    {
        parameters = null;
        if (args is not ["--app-probe", var kind, var port, var send, var receive, var count] ||
            !Enum.GetNames<NetworkTransport>().Contains(kind, StringComparer.Ordinal) ||
            !int.TryParse(port, out var portNumber) || portNumber is < 1 or > 65535 ||
            !int.TryParse(send, out var sendBytes) || sendBytes < 1 ||
            !int.TryParse(receive, out var receiveBytes) || receiveBytes < 1 ||
            !int.TryParse(count, out var exchanges) || exchanges is < 1 or > 32)
        {
            return false;
        }

        var transport = Enum.Parse<NetworkTransport>(kind);
        var limit = transport is NetworkTransport.Tcp4 or NetworkTransport.Tcp6 ? 8 * 1024 * 1024 : 1400;
        if (sendBytes > limit || receiveBytes > limit || ((long)sendBytes + receiveBytes) * exchanges > 16 * 1024 * 1024)
        {
            return false;
        }

        parameters = new(transport, portNumber, sendBytes, receiveBytes, exchanges);
        return true;
    }
}

/// <summary>Only loopback, no elevated APIs, no remote address argument, finite bytes and deadline.</summary>
internal static class LoopbackProbe
{
    public static async Task<int> WorkerAsync(ProbeParameters parameters)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            if (await Console.In.ReadLineAsync(deadline.Token) != "go")
            {
                return 1;
            }

            for (var index = 0; index < parameters.Exchanges; index++)
            {
                if (parameters.IsTcp)
                {
                    using var client = new TcpClient(parameters.Address.AddressFamily);
                    await client.ConnectAsync(parameters.Address, parameters.Port, deadline.Token);
                    await WriteAsync(client.GetStream(), parameters.SendBytes, deadline.Token);
                    await ReadAsync(client.GetStream(), parameters.ReceiveBytes, deadline.Token);
                }
                else
                {
                    using var client = new UdpClient(parameters.Address.AddressFamily);
                    client.Connect(parameters.Address, parameters.Port);
                    await client.SendAsync(new byte[parameters.SendBytes], deadline.Token);
                    var received = await client.ReceiveAsync(deadline.Token);
                    if (received.Buffer.Length != parameters.ReceiveBytes)
                    {
                        return 1;
                    }
                }
            }

            Console.WriteLine("delivered");
            return 0;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or IOException)
        {
            return 1;
        }
    }

    public static async Task ServeTcpAsync(TcpListener listener, ProbeParameters parameters, CancellationToken token)
    {
        for (var index = 0; index < parameters.Exchanges; index++)
        {
            using var peer = await listener.AcceptTcpClientAsync(token);
            await ReadAsync(peer.GetStream(), parameters.SendBytes, token);
            await WriteAsync(peer.GetStream(), parameters.ReceiveBytes, token);
        }
    }

    public static async Task ServeUdpAsync(UdpClient listener, ProbeParameters parameters, CancellationToken token)
    {
        for (var index = 0; index < parameters.Exchanges; index++)
        {
            var received = await listener.ReceiveAsync(token);
            if (received.Buffer.Length != parameters.SendBytes)
            {
                throw new IOException("Unexpected controlled datagram size.");
            }

            await listener.SendAsync(new byte[parameters.ReceiveBytes], received.RemoteEndPoint, token);
        }
    }

    private static async Task WriteAsync(Stream stream, int count, CancellationToken token)
    {
        var buffer = new byte[Math.Min(count, 65536)];
        while (count > 0)
        {
            var next = Math.Min(count, buffer.Length);
            await stream.WriteAsync(buffer.AsMemory(0, next), token);
            count -= next;
        }
    }

    private static async Task ReadAsync(Stream stream, int count, CancellationToken token)
    {
        var buffer = new byte[Math.Min(count, 65536)];
        while (count > 0)
        {
            var next = Math.Min(count, buffer.Length);
            await stream.ReadExactlyAsync(buffer.AsMemory(0, next), token);
            count -= next;
        }
    }
}
