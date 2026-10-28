using FlowLedger.Application.Monitoring;
using FlowLedger.Desktop.ViewModels;
using FlowLedger.Domain.Accounting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowLedger.Windows.Tests;

[TestClass]
public sealed class DesktopLifecycleTests
{
    [TestMethod]
    public async Task PauseReleasesSubscriptionAndResumeExcludesThePausedPeriod()
    {
        var reader = new SyntheticReader();
        var sources = new List<SyntheticWifiSource>();
        var model = Model(reader, () =>
        {
            var source = new SyntheticWifiSource(reader);
            sources.Add(source);
            return source;
        });
        try
        {
            await model.RefreshAsync();
            await model.SetWifiEnabledAsync(true);
            await model.RefreshAsync();
            await model.RefreshAsync();
            Assert.AreEqual("100 B", model.WifiRows[0].Download);
            await model.SetWifiEnabledAsync(false);
            Assert.IsTrue(sources[0].Disposed);
            await model.RefreshAsync();
            await model.RefreshAsync();
            Assert.AreEqual("100 B", model.WifiRows[0].Download);
            await model.SetWifiEnabledAsync(true);
            await model.RefreshAsync();
            Assert.AreEqual("100 B", model.WifiRows[0].Download);
            await model.RefreshAsync();
            Assert.AreEqual("200 B", model.WifiRows[0].Download);
        }
        finally
        {
            await model.ShutdownAsync();
        }

        Assert.IsTrue(sources.All(source => source.Disposed));
    }

    [TestMethod]
    public async Task ShutdownWaitsForTheInFlightReadBeforeReleasingTheListener()
    {
        var reader = new SyntheticReader();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new SyntheticWifiSource(reader, entered, release);
        var model = Model(reader, () => source);
        await model.SetWifiEnabledAsync(true);
        var refresh = model.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var shutdown = model.ShutdownAsync();
        try
        {
            Assert.IsFalse(shutdown.IsCompleted);
            Assert.IsFalse(source.Disposed);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(refresh, shutdown).WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.IsTrue(source.Disposed);
        Assert.IsFalse(model.CanEnableWifi);
    }

    [TestMethod]
    public async Task PowerNotificationDiscardsAnOlderInFlightWifiResult()
    {
        var reader = new SyntheticReader();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new SyntheticWifiSource(reader, entered, release);
        var model = Model(reader, () => source);
        await model.SetWifiEnabledAsync(true);
        var refresh = model.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            model.BreakContinuity();
        }
        finally
        {
            release.Set();
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.HasCount(0, model.Interfaces);
        await model.RefreshAsync();
        Assert.HasCount(0, model.WifiRows);
        await model.ShutdownAsync();
    }

    private static InterfaceMonitorViewModel Model(SyntheticReader reader, Func<IWifiTrafficSource> factory) =>
        new(reader, new SyntheticNameReader(), factory, new SyntheticRouteReader());

    private sealed class SyntheticReader : IInterfaceCounterReader
    {
        internal const string Id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        internal static readonly Guid Context = Guid.Parse("11111111-1111-1111-1111-111111111111");
        private int sequence;

        public InterfaceCounterSnapshot Read()
        {
            var index = Interlocked.Increment(ref sequence);
            var time = DateTimeOffset.UnixEpoch.AddSeconds(index);
            return new(time, TimeSpan.FromSeconds(index),
                [new(Id, "Synthetic adapter", InterfaceKind.Hardware, 71, true, true, true, "device",
                    new CounterReading(Id, Context, time, new((ulong)index * 100, (ulong)index * 50)), Context)]);
        }
    }

    private sealed class SyntheticWifiSource(
        SyntheticReader reader, TaskCompletionSource? entered = null, ManualResetEventSlim? release = null) : IWifiTrafficSource
    {
        public bool Disposed { get; private set; }

        public WifiTrafficSample Read()
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            entered?.TrySetResult();
            if (release is not null && !release.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new InvalidOperationException("Synthetic read timed out.");
            }

            var network = new WifiNetworkIdentity("41", "synthetic", SyntheticReader.Context, 1, 7, 4);
            return new(reader.Read(), new Dictionary<string, WifiTrafficObservation>
            {
                [SyntheticReader.Id] = new(new(network, "Synthetic WLAN", "AP", 0, true, true), WifiConnectionState.Available, 0),
            }, WifiNotificationCoverage.Full, 0);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class SyntheticNameReader : IWifiConnectionReader
    {
        public WifiConnection Read(Guid interfaceId) => throw new InvalidOperationException("A manual query was not requested.");
    }

    private sealed class SyntheticRouteReader : IRouteEvidenceReader
    {
        public RouteEvidence Read() => new([]);
    }
}
