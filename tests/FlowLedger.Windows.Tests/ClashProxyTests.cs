using System.Net;
using System.Text;
using FlowLedger.Application.Proxy;
using FlowLedger.Domain.Proxy;
using FlowLedger.Infrastructure.Windows.Proxy;

namespace FlowLedger.Windows.Tests;

[TestClass]
public sealed class ClashProxyTests
{
    private static readonly ProxyProcess Core = new(99, 1, "core");
    private static readonly ProxyProcess App = new(7, 2, "app");
    private const string Fixture = """
        {"downloadTotal":9007199254740993,"uploadTotal":10,"connections":[{
          "id":"00000000-0000-0000-0000-000000000001","start":"2026-01-01T00:00:00Z","download":100,"upload":20,
          "metadata":{"network":"tcp","type":"HTTP","sourceIP":"127.0.0.1","sourcePort":"50000",
            "destinationIP":"192.0.2.1","destinationPort":"443","process":"untrusted-name"},
          "chains":["synthetic-node","synthetic-group","synthetic-chain"]}]}
        """;

    [TestMethod]
    public void OldClashAndMihomoStringOrNumericPortsKeepIntegerBytes()
    {
        foreach (var json in new[] { Fixture, Fixture.Replace("\"50000\"", "50000", StringComparison.Ordinal) })
        {
            var parsed = ClashApiParser.Parse(Encoding.UTF8.GetBytes(json));
            Assert.AreEqual(9007199254740993UL, parsed.CoreTotal.ReceivedBytes);
            Assert.HasCount(1, parsed.Connections);
            Assert.AreEqual(ProxyIngress.ExplicitProxy, parsed.Connections.Single().Ingress);
            Assert.AreEqual(ProxyRoute.CoreProxy, parsed.Connections.Single().Route);
        }
    }

    [TestMethod]
    [DataRow("http://example.com:9090")]
    [DataRow("http://127.0.0.1:9090/path")]
    [DataRow("http://user:password@127.0.0.1:9090")]
    [DataRow("http://127.0.0.1:9090/?token=test")]
    [DataRow("file:///tmp/controller")]
    public void ControllerRejectsRemotePathsCredentialsAndQueries(string endpoint) => Assert.IsFalse(ClashProxySource.TryEndpoint(endpoint, out _));

    [TestMethod]
    public void NullEmptyConnectionsAreValidButMissingCountersAreNot()
    {
        Assert.IsEmpty(ClashApiParser.Parse(Encoding.UTF8.GetBytes("{\"downloadTotal\":0,\"uploadTotal\":0,\"connections\":null}")).Connections);
        Assert.Throws<KeyNotFoundException>(() => ClashApiParser.Parse(Encoding.UTF8.GetBytes("{\"connections\":[]}")));
        Assert.Throws<FormatException>(() => ClashApiParser.Parse(Encoding.UTF8.GetBytes(Fixture.Replace("\"download\":100", "\"download\":-1", StringComparison.Ordinal))));
    }

    [TestMethod]
    public void ExplicitProxyNeedsExactLocalPeerAndCoreListener()
    {
        var connection = ClashApiParser.Parse(Encoding.UTF8.GetBytes(Fixture)).Connections.Single();
        Assert.AreEqual(App, ClashProxySource.MatchOwner(connection, Snapshot(Core, App), Core, 7890));
        Assert.IsNull(ClashProxySource.MatchOwner(connection, Snapshot(Core with { ProcessId = 100 }, App), Core, 7890));
        Assert.IsNull(ClashProxySource.MatchOwner(connection with { SourcePort = 50001 }, Snapshot(Core, App), Core, 7890));
        Assert.IsNull(ClashProxySource.MatchOwner(connection with { Network = "udp" }, Snapshot(Core, App), Core, 7890));
    }

    [TestMethod]
    public void AmbiguousOwnerAndProcessCreatedAfterConnectionRemainUnknown()
    {
        var connection = ClashApiParser.Parse(Encoding.UTF8.GetBytes(Fixture)).Connections.Single();
        var snapshot = Snapshot(Core, App);
        Assert.IsNull(ClashProxySource.MatchOwner(connection, snapshot with { Rows = [.. snapshot.Rows, snapshot.Rows.Last() with { Process = App with { ProcessId = 8 } }] }, Core, 7890));
        Assert.IsNull(ClashProxySource.MatchOwner(connection, Snapshot(Core, App with { StartTicks = DateTime.MaxValue.Ticks }), Core, 7890));
    }

    [TestMethod]
    public void TunRequiresDestinationTupleAndNeverUsesReportedProcessName()
    {
        var connection = ClashApiParser.Parse(Encoding.UTF8.GetBytes(Fixture)).Connections.Single() with { Ingress = ProxyIngress.Tun };
        var snapshot = new TcpOwnerSnapshot(true, [new(connection.SourceAddress, connection.SourcePort, connection.DestinationAddress, connection.DestinationPort, 5, App)]);
        Assert.AreEqual(App, ClashProxySource.MatchOwner(connection, snapshot, Core, 7890));
        Assert.IsNull(ClashProxySource.MatchOwner(connection with { DestinationAddress = "198.51.100.1" }, snapshot, Core, 7890));
    }

    [TestMethod]
    [DataRow(401, ProxyReadState.AccessDenied)]
    [DataRow(403, ProxyReadState.AccessDenied)]
    [DataRow(302, ProxyReadState.Unavailable)]
    public async Task AuthenticationOrRedirectFailureNeverUsesData(int status, ProxyReadState expected)
    {
        var calls = 0;
        using var handler = new Handler(request => { calls++; Assert.AreEqual("Bearer synthetic-test-token", request.Headers.Authorization!.ToString()); return new((HttpStatusCode)status); });
        await using var source = new ClashProxySource("http://127.0.0.1:9090", "synthetic-test-token", 7890, handler, new Owners(Snapshot(Core, App)));
        var result = await source.ReadAsync();
        Assert.AreEqual(expected, result.State);
        Assert.IsNull(result.Observation);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task OnlyFixedGetEndpointsAndStableCoreCanProduceObservation()
    {
        var paths = new List<string>();
        using var handler = new Handler(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            paths.Add(request.RequestUri!.AbsolutePath);
            return Json(request.RequestUri.AbsolutePath == "/version" ? "{\"version\":\"synthetic-core\"}" : Fixture);
        });
        await using var source = new ClashProxySource("http://127.0.0.1:9090", "", 7890, handler, new Owners(Snapshot(Core, App)));
        var result = await source.ReadAsync();
        Assert.AreEqual(ProxyReadState.Available, result.State);
        Assert.AreEqual(App, result.Observation!.Connections.Single().Owner);
        CollectionAssert.AreEqual(new[] { "/version", "/connections" }, paths);
    }

    [TestMethod]
    public async Task CoreOrOwnerChangeDuringReadIsNotTrusted()
    {
        using var handler = new Handler(request => Json(request.RequestUri!.AbsolutePath == "/version" ? "{\"version\":\"synthetic\"}" : Fixture));
        await using var source = new ClashProxySource("http://127.0.0.1:9090", "", 7890, handler,
            new SequenceOwners(Snapshot(Core, App), Snapshot(Core with { StartTicks = 3 }, App)));
        Assert.AreEqual(ProxyReadState.CoreUnconfirmed, (await source.ReadAsync()).State);
    }

    [TestMethod]
    public async Task StableCoreButReusedApplicationPidBecomesUnknown()
    {
        using var handler = new Handler(request => Json(request.RequestUri!.AbsolutePath == "/version" ? "{\"version\":\"synthetic\"}" : Fixture));
        await using var source = new ClashProxySource("http://127.0.0.1:9090", "", 7890, handler,
            new SequenceOwners(Snapshot(Core, App), Snapshot(Core, App with { StartTicks = 3 })));
        var result = await source.ReadAsync();
        Assert.AreEqual(ProxyReadState.Available, result.State);
        Assert.IsNull(result.Observation!.Connections.Single().Owner);
    }

    [TestMethod]
    public async Task MissingOrDifferentControllerOwnerNeverReceivesToken()
    {
        var sent = false;
        using var handler = new Handler(_ => { sent = true; return Json("{}"); });
        await using var source = new ClashProxySource("http://127.0.0.1:9090", "synthetic-test-token", 7890, handler,
            new Owners(new(true, [new("127.0.0.1", 9090, "0.0.0.0", 0, 2, Core)])));
        Assert.AreEqual(ProxyReadState.CoreUnconfirmed, (await source.ReadAsync()).State);
        Assert.IsFalse(sent);
    }

    [TestMethod]
    public async Task OversizedReplyIsRejectedBeforeParsing()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2 * 1024 * 1024 + 1]) });
        await using var source = new ClashProxySource("http://127.0.0.1:9090", "", 7890, handler, new Owners(Snapshot(Core, App)));
        Assert.AreEqual(ProxyReadState.InvalidResponse, (await source.ReadAsync()).State);
    }

    [TestMethod]
    public async Task SameVersionWithNewCoreInstanceHasNewEpoch()
    {
        using var handler = new Handler(request => Json(request.RequestUri!.AbsolutePath == "/version" ? "{\"version\":\"synthetic\"}" : Fixture));
        var nextCore = Core with { StartTicks = 5 };
        await using var source = new ClashProxySource("http://127.0.0.1:9090", "", 7890, handler,
            new SequenceOwners(Snapshot(Core, App), Snapshot(Core, App), Snapshot(nextCore, App), Snapshot(nextCore, App)));
        var first = await source.ReadAsync();
        var second = await source.ReadAsync();
        Assert.AreNotEqual(first.Observation!.CoreEpoch, second.Observation!.CoreEpoch);
    }

    [TestMethod]
    public async Task DisposalIsIdempotentAndFurtherReadsSendNothing()
    {
        var sent = false;
        using var handler = new Handler(_ => { sent = true; return Json("{}"); });
        var source = new ClashProxySource("http://127.0.0.1:9090", "synthetic-token", 7890, handler, new Owners(Snapshot(Core, App)));
        await Task.WhenAll(source.DisposeAsync().AsTask(), source.DisposeAsync().AsTask());
        Assert.AreEqual(ProxyReadState.Unavailable, (await source.ReadAsync()).State);
        Assert.IsFalse(sent);
    }

    [TestMethod]
    public void DuplicateConnectionIdsAreRejectedEvenIfCountersMatch()
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(Fixture)!;
        var connections = root["connections"]!.AsArray();
        connections.Add(connections[0]!.DeepClone());
        Assert.Throws<System.Text.Json.JsonException>(() => ClashApiParser.Parse(Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    [TestMethod]
    public async Task StalledBodyHasWholeReadDeadlineAfterHeaders()
    {
        using var stream = new StalledStream();
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        await using var source = new ClashProxySource("http://127.0.0.1:9090", "", 7890, handler, new Owners(Snapshot(Core, App)));
        var reading = source.ReadAsync();
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(ProxyReadState.Unavailable, (await reading.WaitAsync(TimeSpan.FromSeconds(6))).State);
    }

    [TestMethod]
    public async Task DisposeCancelsStalledBodyAndWaitsForReadBeforeRelease()
    {
        using var stream = new StalledStream();
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        var source = new ClashProxySource("http://127.0.0.1:9090", "", 7890, handler, new Owners(Snapshot(Core, App)));
        var reading = source.ReadAsync();
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await source.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(ProxyReadState.Unavailable, (await reading).State);
    }

    [TestMethod]
    public void NativeOwnerReaderCanReadWithoutPublishingAddresses()
    {
        var result = new WindowsTcpOwners().Read();
        Assert.IsTrue(result.Available);
    }

    private static TcpOwnerSnapshot Snapshot(ProxyProcess core, ProxyProcess app) => new(true,
        [new("127.0.0.1", 9090, "0.0.0.0", 0, 2, core), new("127.0.0.1", 7890, "0.0.0.0", 0, 2, core),
         new("127.0.0.1", 50000, "127.0.0.1", 7890, 5, app)]);
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
    private sealed class Owners(TcpOwnerSnapshot snapshot) : ITcpOwnerReader { public TcpOwnerSnapshot Read() => snapshot; }
    private sealed class SequenceOwners(params TcpOwnerSnapshot[] snapshots) : ITcpOwnerReader
    {
        private int index;
        public TcpOwnerSnapshot Read() => snapshots[Math.Min(index++, snapshots.Length - 1)];
    }

    private sealed class StalledStream : Stream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
