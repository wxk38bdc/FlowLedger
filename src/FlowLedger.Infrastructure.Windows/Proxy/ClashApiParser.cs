using System.Globalization;
using System.Net;
using System.Text.Json;
using FlowLedger.Domain.Accounting;
using FlowLedger.Domain.Proxy;

namespace FlowLedger.Infrastructure.Windows.Proxy;

internal sealed record ClashConnection(Guid Id, DateTimeOffset Started, TrafficVolume Total, string Network,
    string SourceAddress, int SourcePort, string DestinationAddress, int DestinationPort, ProxyIngress Ingress, ProxyRoute Route);
internal sealed record ClashConnections(TrafficVolume CoreTotal, IReadOnlyList<ClashConnection> Connections);

internal static class ClashApiParser
{
    internal static ClashConnections Parse(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        var total = new TrafficVolume(root.GetProperty("downloadTotal").GetUInt64(), root.GetProperty("uploadTotal").GetUInt64());
        var array = root.GetProperty("connections");
        if (array.ValueKind == JsonValueKind.Null) { return new(total, []); }
        if (array.GetArrayLength() > 4096) { throw new JsonException(); }
        var rows = new List<ClashConnection>();
        var ids = new HashSet<Guid>();
        foreach (var item in array.EnumerateArray())
        {
            var id = item.GetProperty("id").GetGuid();
            if (!ids.Add(id)) { throw new JsonException(); }
            var started = item.GetProperty("start").GetDateTimeOffset();
            var volume = new TrafficVolume(item.GetProperty("download").GetUInt64(), item.GetProperty("upload").GetUInt64());
            var metadata = item.GetProperty("metadata");
            var type = Text(metadata, "type");
            var ingress = type.ToUpperInvariant() switch
            {
                "HTTP" or "HTTPS" or "SOCKS4" or "SOCKS5" => ProxyIngress.ExplicitProxy,
                "TUN" => ProxyIngress.Tun,
                _ => ProxyIngress.Unknown,
            };
            var route = ProxyRoute.Unknown;
            if (item.TryGetProperty("chains", out var chains) && chains.ValueKind == JsonValueKind.Array && chains.GetArrayLength() > 0)
            {
                route = chains[0].GetString() == "DIRECT" ? ProxyRoute.CoreDirect : ProxyRoute.CoreProxy;
            }

            rows.Add(new(id, started.ToUniversalTime(), volume, Text(metadata, "network").ToLowerInvariant(),
                Address(metadata, "sourceIP"), Port(metadata, "sourcePort"), Address(metadata, "destinationIP"), Port(metadata, "destinationPort"), ingress, route));
        }

        return new(total, rows);
    }

    internal static string Version(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        var version = Text(document.RootElement, "version");
        if (version.Length is < 1 or > 128 || version.Any(char.IsControl)) { throw new JsonException(); }
        return version;
    }

    private static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string Address(JsonElement metadata, string name) => IPAddress.TryParse(Text(metadata, name), out var address) ? address.ToString() : "";
    private static int Port(JsonElement metadata, string name)
    {
        if (!metadata.TryGetProperty(name, out var value)) { return 0; }
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535 ? port : 0;
    }
}
