using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FlowLedger.Domain.Accounting;
using FlowLedger.Infrastructure.Windows.Monitoring;

namespace FlowLedger.Collector.Validation;

internal static class ReliabilityWorkers
{
    internal static async Task<int> CaptureAsync(Guid epoch)
    {
        await using var source = new WindowsApplicationTrafficSource(epoch);
        if (source.Read().State != Application.Monitoring.ApplicationCaptureState.Running) { return 1; }
        Console.WriteLine("ready");
        await Task.Delay(TimeSpan.FromSeconds(60));
        return 0;
    }

    internal static async Task<int> SustainedAsync(int seconds)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 20));
        if (await Console.In.ReadLineAsync(deadline.Token) != "go") { return 1; }
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(index => PumpAsync(
            index % 2 == 0 ? IPAddress.Loopback : IPAddress.IPv6Loopback, seconds, deadline.Token)));
        var tcp4 = checked((ulong)(results[0] + results[2]));
        var tcp6 = checked((ulong)(results[1] + results[3]));
        var expected = new TransportTraffic(new(tcp4, tcp4), new(tcp6, tcp6), default, default);
        Console.WriteLine(JsonSerializer.Serialize(expected));
        return 0;
    }

    private static async Task<long> PumpAsync(IPAddress address, int seconds, CancellationToken token)
    {
        var listener = new TcpListener(address, 0);
        listener.Start();
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            var accept = listener.AcceptTcpClientAsync(token);
            await client.ConnectAsync(address, ((IPEndPoint)listener.LocalEndpoint).Port, token);
            using var peer = await accept;
            var upload = new byte[65536];
            var download = new byte[32768];
            var service = Task.Run(async () =>
            {
                var request = new byte[upload.Length];
                while (await peer.GetStream().ReadAsync(request.AsMemory(0, 1), token) != 0)
                {
                    await peer.GetStream().ReadExactlyAsync(request.AsMemory(1), token);
                    await peer.GetStream().WriteAsync(download, token);
                }
            }, token);
            var watch = Stopwatch.StartNew();
            long bytes = 0;
            do
            {
                await client.GetStream().WriteAsync(upload, token);
                await client.GetStream().ReadExactlyAsync(download, token);
                bytes = checked(bytes + upload.Length + download.Length);
                await Task.Delay(2, token);
            } while (watch.Elapsed < TimeSpan.FromSeconds(seconds));
            client.Client.Shutdown(SocketShutdown.Send);
            await service;
            return bytes;
        }
        finally { listener.Stop(); }
    }

    internal static async Task<int> QuicAsync(bool ipv6)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348) || !QuicListener.IsSupported || !QuicConnection.IsSupported) { return 3; }
        return await RunQuicAsync(ipv6);
    }

    [SupportedOSPlatform("windows10.0.20348")]
    private static async Task<int> RunQuicAsync(bool ipv6)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        if (await Console.In.ReadLineAsync(deadline.Token) != "go") { return 1; }
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=flowledger.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("flowledger.test");
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
        // SChannel QUIC cannot use ephemeral CNG keys. Import this synthetic PFX in memory
        // without PersistKeySet; Windows' temporary key container is removed on disposal.
        // No certificate store/trust changes or PFX file, and never use a user's certificate.
        var pfx = generated.Export(X509ContentType.Pfx);
        using var certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet);
        CryptographicOperations.ZeroMemory(pfx);
        var pin = certificate.RawData;
        var protocol = new SslApplicationProtocol("flowledger-validation");
        await using var listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = new(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, 0),
            ApplicationProtocols = [protocol],
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultCloseErrorCode = 0,
                DefaultStreamErrorCode = 1,
                MaxInboundBidirectionalStreams = 1,
                ServerAuthenticationOptions = new() { ApplicationProtocols = [protocol], ServerCertificate = certificate },
            }),
        }, deadline.Token);
        var responseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Task.Run(async () =>
        {
            await using var peer = await listener.AcceptConnectionAsync(deadline.Token);
            await using var stream = await peer.AcceptInboundStreamAsync(deadline.Token);
            await stream.ReadExactlyAsync(new byte[262144], deadline.Token);
            await stream.WriteAsync(new byte[131072], completeWrites: true, deadline.Token);
            await stream.WritesClosed.WaitAsync(deadline.Token);
            // Keep the connection alive until the initiator has read all response bytes.
            await responseRead.Task.WaitAsync(deadline.Token);
        }, deadline.Token);
        await using var client = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
        {
            RemoteEndPoint = listener.LocalEndPoint,
            DefaultCloseErrorCode = 0,
            DefaultStreamErrorCode = 1,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "flowledger.test",
                ApplicationProtocols = [protocol],
                // Only this synthetic loopback certificate, never a global TLS bypass.
                RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null && cert.GetRawCertData().AsSpan().SequenceEqual(pin),
            },
        }, deadline.Token);
        await using (var stream = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, deadline.Token))
        {
            await stream.WriteAsync(new byte[262144], completeWrites: true, deadline.Token);
            await stream.ReadExactlyAsync(new byte[131072], deadline.Token);
            responseRead.TrySetResult();
        }

        await service;
        Console.WriteLine("delivered");
        return 0;
    }
}
