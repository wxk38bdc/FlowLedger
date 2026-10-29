using FlowLedger.Application.Proxy;
using FlowLedger.Desktop.ViewModels;
using FlowLedger.Domain.Proxy;

namespace FlowLedger.Windows.Tests;

[TestClass]
public sealed class ProxyDesktopLifecycleTests
{
    [TestMethod]
    public async Task AccessDeniedReleasesTokenHoldingSourceAndAllowsRetry()
    {
        var source = new FakeSource(new ProxyReadout(ProxyReadState.AccessDenied, "", null, 401));
        var model = new ProxyMonitorViewModel((_, _, _) => source);
        await model.StartAsync("synthetic-token");
        Assert.IsTrue(source.Disposed);
        Assert.IsTrue(model.CanStart);
        Assert.IsFalse(model.CanStop);
        StringAssert.Contains(model.Status, "认证失败");
        await model.ShutdownAsync();
        Assert.IsFalse(model.CanStart);
    }

    [TestMethod]
    public async Task StopRetainsUsageAndNewStartReplacesIt()
    {
        var id = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        ProxyReadout Read(int index) => new(ProxyReadState.Available, "synthetic", new(epoch,
            DateTimeOffset.UnixEpoch.AddSeconds(index), TimeSpan.FromSeconds(index), new((ulong)index * 100, 0),
            [new(id, DateTimeOffset.UnixEpoch, new((ulong)index * 100, 0), new(7, 1, "synthetic"), ProxyIngress.ExplicitProxy, ProxyRoute.CoreDirect)]), 0);
        var first = new FakeSource(Read(1), Read(2));
        var second = new FakeSource(Read(1));
        var calls = 0;
        var model = new ProxyMonitorViewModel((_, _, _) => calls++ == 0 ? first : second);
        await model.StartAsync("");
        await model.RefreshAsync();
        Assert.HasCount(1, model.Rows);
        await model.StopAsync();
        Assert.HasCount(1, model.Rows);
        Assert.IsTrue(first.Disposed);
        await model.StartAsync("");
        Assert.IsEmpty(model.Rows);
        await model.ShutdownAsync();
        Assert.IsTrue(second.Disposed);
    }

    [TestMethod]
    public async Task ClosingCancelsOwnedVerificationBeforeDisposingSource()
    {
        var source = new FakeSource(new ProxyReadout(ProxyReadState.Available, "synthetic", null, 0));
        var model = new ProxyMonitorViewModel((_, _, _) => source);
        await model.StartAsync("");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifying = model.VerifyAsync(async (_, _, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return "unreachable";
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await model.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await verifying;
        Assert.IsTrue(source.Disposed);
        Assert.IsFalse(model.CanStart);
    }

    [TestMethod]
    public async Task DiscoveryDoesNotCreateASecretHoldingSource()
    {
        var created = false;
        var model = new ProxyMonitorViewModel((_, _, _) => { created = true; throw new InvalidOperationException(); });
        await model.DiscoverAsync(_ => Task.FromResult<string?>("http://127.0.0.1:19090"));
        Assert.AreEqual("http://127.0.0.1:19090", model.Endpoint);
        Assert.IsFalse(created);
        Assert.IsTrue(model.CanStart);
        await model.ShutdownAsync();
    }

    private sealed class FakeSource(params ProxyReadout[] readings) : IProxyTrafficSource
    {
        private int index;
        public bool Disposed { get; private set; }
        public Task<ProxyReadout> ReadAsync(CancellationToken token = default) => Task.FromResult(readings[Math.Min(index++, readings.Length - 1)]);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
