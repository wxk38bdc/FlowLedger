using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FlowLedger.Application.Proxy;
using FlowLedger.Domain.Proxy;

namespace FlowLedger.Infrastructure.Windows.Proxy;

/// <summary>Explicitly enabled, loopback-only GET adapter. Token lives in memory; no config discovery.</summary>
public sealed class ClashProxySource : IProxyTrafficSource
{
    private readonly HttpClient client;
    private readonly ITcpOwnerReader owners;
    private readonly Uri endpoint;
    private readonly int proxyPort;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object disposalGate = new();
    private Task? disposal;
    private string secret;
    private string version = "";
    private ProxyProcess? core;
    private Guid epoch;
    private bool closed;

    public ClashProxySource(string endpoint, string secret, int proxyPort = 7890)
        : this(endpoint, secret, proxyPort, new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            MaxConnectionsPerServer = 1,
        }, new WindowsTcpOwners())
    { }

    internal ClashProxySource(string endpoint, string secret, int proxyPort, HttpMessageHandler handler, ITcpOwnerReader owners)
    {
        if (!TryEndpoint(endpoint, out var uri) || proxyPort is < 1 or > 65535 || secret.Length > 4096 || secret.Any(char.IsControl))
        {
            handler.Dispose();
            throw new ArgumentException("Invalid local controller settings.");
        }

        this.endpoint = uri!;
        this.secret = secret;
        this.proxyPort = proxyPort;
        this.owners = owners;
        client = new(handler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(3) };
    }

    public static bool TryEndpoint(string value, out Uri? endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" || uri.Port < 1)
        {
            return false;
        }

        endpoint = uri;
        return true;
    }

    public async Task<ProxyReadout> ReadAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (closed) { return Failure(ProxyReadState.Unavailable, 0); }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            // HttpClient.Timeout ends at headers with ResponseHeadersRead. Bound body reads too.
            cancellation.CancelAfter(TimeSpan.FromSeconds(4));
            var before = await Task.Run(owners.Read, cancellation.Token).ConfigureAwait(false);
            var beforeCore = before.Controller(endpoint.Port);
            // Do not send a token to an absent/reassigned local port or a different process.
            if (beforeCore is null || before.Controller(proxyPort) != beforeCore) { return Failure(ProxyReadState.CoreUnconfirmed, 0); }
            var reportedVersion = ClashApiParser.Version(await GetAsync("version", cancellation.Token).ConfigureAwait(false));
            var parsed = ClashApiParser.Parse(await GetAsync("connections", cancellation.Token).ConfigureAwait(false));
            var after = await Task.Run(owners.Read, cancellation.Token).ConfigureAwait(false);
            var afterCore = after.Controller(endpoint.Port);
            version = reportedVersion;
            if (beforeCore is null || beforeCore != afterCore)
            {
                return Failure(ProxyReadState.CoreUnconfirmed, 0);
            }

            if (core != beforeCore) { core = beforeCore; epoch = Guid.NewGuid(); }
            var connections = parsed.Connections.Select(connection =>
            {
                var first = MatchOwner(connection, before, beforeCore, proxyPort);
                var last = MatchOwner(connection, after, beforeCore, proxyPort);
                return new ProxyConnectionSample(connection.Id, connection.Started, connection.Total,
                    first is not null && first == last ? first : null, connection.Ingress, connection.Route);
            }).ToArray();
            return new(ProxyReadState.Available, version, new(epoch, DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency), parsed.CoreTotal, connections), 0);
        }
        catch (HttpRequestException exception)
        {
            return Failure(exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? ProxyReadState.AccessDenied : ProxyReadState.Unavailable,
                exception.StatusCode is { } code ? (int)code : exception.HResult);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or InvalidDataException)
        {
            return Failure(ProxyReadState.InvalidResponse, exception.HResult);
        }
        catch (OperationCanceledException) { return Failure(ProxyReadState.Unavailable, 0); }
        catch (IOException exception) { return Failure(ProxyReadState.Unavailable, exception.HResult); }
        finally { gate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalGate) { return new(disposal ??= DisposeCoreAsync()); }
    }

    private async Task DisposeCoreAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!closed) { closed = true; secret = ""; client.Dispose(); lifetime.Dispose(); }
        }
        finally { gate.Release(); }
    }

    internal static ProxyProcess? MatchOwner(ClashConnection connection, TcpOwnerSnapshot snapshot, ProxyProcess core, int proxyPort)
    {
        if (!snapshot.Available || connection.Network != "tcp" || connection.SourceAddress.Length == 0 || connection.SourcePort == 0) { return null; }
        var explicitProxy = connection.Ingress == ProxyIngress.ExplicitProxy;
        if (explicitProxy && snapshot.Controller(proxyPort) != core) { return null; }
        if (!explicitProxy && connection.Ingress != ProxyIngress.Tun) { return null; }
        var matches = snapshot.Rows.Where(row => row.State == 5 && row.LocalAddress == connection.SourceAddress && row.LocalPort == connection.SourcePort &&
            (explicitProxy ? row.RemotePort == proxyPort && IPAddress.TryParse(row.RemoteAddress, out var address) && IPAddress.IsLoopback(address) :
                row.RemoteAddress == connection.DestinationAddress && row.RemotePort == connection.DestinationPort))
            .Select(row => row.Process).Distinct().ToArray();
        return matches.Length == 1 && matches[0] is { } process && process.ProcessId != core.ProcessId && process.StartTicks <= connection.Started.UtcTicks ? process : null;
    }

    private ProxyReadout Failure(ProxyReadState state, int error) { core = null; return new(state, version, null, error); }

    private async Task<byte[]> GetAsync(string path, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (secret.Length != 0) { request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret); }
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        const int limit = 2 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) { throw new InvalidDataException(); }
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (memory.Length + count > limit) { throw new InvalidDataException(); }
            memory.Write(buffer, 0, count);
        }

        return memory.ToArray();
    }
}
