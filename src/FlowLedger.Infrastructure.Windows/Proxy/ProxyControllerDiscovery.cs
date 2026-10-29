using System.Net;

namespace FlowLedger.Infrastructure.Windows.Proxy;

public static class ProxyControllerDiscovery
{
    public static async Task<string?> FindAsync(int proxyPort, CancellationToken token = default)
    {
        var snapshot = await Task.Run(new WindowsTcpOwners().Read, token).ConfigureAwait(false);
        var core = snapshot.Controller(proxyPort);
        if (core is null || (!core.Name.Contains("clash", StringComparison.OrdinalIgnoreCase) && !core.Name.Contains("mihomo", StringComparison.OrdinalIgnoreCase))) { return null; }
        var candidates = snapshot.Rows.Where(row => row.State == 2 && row.Process == core && row.LocalPort != proxyPort &&
            IPAddress.TryParse(row.LocalAddress, out var address) && (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)))
            .Select(row => new { row.LocalPort, Address = row.LocalAddress.Contains(':', StringComparison.Ordinal) ? "[::1]" : "127.0.0.1" }).Distinct().Take(17).ToArray();
        if (candidates.Length > 16) { return null; }
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(1) };
        var found = new HashSet<string>();
        foreach (var candidate in candidates)
        {
            var endpoint = $"http://{candidate.Address}:{candidate.LocalPort}";
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(1));
                // No token, config, subscriptions or connection contents during discovery.
                using var response = await client.GetAsync($"{endpoint}/version", HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized) { found.Add(endpoint); }
                else if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength is >= 1 and <= 8192)
                {
                    var body = new byte[(int)response.Content.Headers.ContentLength.Value];
                    await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                    await stream.ReadExactlyAsync(body, deadline.Token).ConfigureAwait(false);
                    ClashApiParser.Version(body);
                    found.Add(endpoint);
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException or IOException) { }
        }

        var ports = found.Select(value => new Uri(value).Port).Distinct().ToArray();
        return ports.Length == 1 ? found.OrderBy(value => value.Contains("127.0.0.1", StringComparison.Ordinal) ? 0 : 1).First() : null;
    }
}
