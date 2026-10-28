using FlowLedger.Application.Monitoring;
using FlowLedger.Desktop.ViewModels;
using FlowLedger.Domain.Accounting;

namespace FlowLedger.Windows.Tests;

[TestClass]
public sealed class ApplicationDesktopLifecycleTests
{
    [TestMethod]
    public async Task PermissionFailureShowsMissingDataAndAllowsRetry()
    {
        var source = new SyntheticSource(ApplicationCaptureState.AccessDenied);
        var model = new ApplicationMonitorViewModel(() => source);
        await model.StartAsync();
        Assert.IsTrue(source.Disposed);
        Assert.IsTrue(model.CanStart);
        Assert.IsFalse(model.CanStop);
        StringAssert.Contains(model.Status, "权限不足");
        Assert.HasCount(0, model.Rows);
        await model.ShutdownAsync();
        Assert.IsFalse(model.CanStart);
    }

    [TestMethod]
    public async Task StopRetainsFinalSnapshotAndRestartStartsNewEpoch()
    {
        var sources = new Queue<SyntheticSource>([new(name: "first.exe"), new(name: "second.exe")]);
        var model = new ApplicationMonitorViewModel(() => sources.Dequeue());
        await model.StartAsync();
        Assert.IsTrue(model.CanStop);
        await model.StopAsync();
        StringAssert.Contains(model.Rows[0].Name, "first.exe");
        Assert.AreEqual("100 B", model.Rows[0].Download);
        await model.StartAsync();
        StringAssert.Contains(model.Rows[0].Name, "second.exe");
        Assert.AreEqual("100 B", model.Rows[0].Download);
        await model.ShutdownAsync();
    }

    [TestMethod]
    public async Task ShutdownWaitsForActiveReadBeforeDisposing()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new SyntheticSource();
        var model = new ApplicationMonitorViewModel(() => source);
        await model.StartAsync();
        source.ReadEntered = entered;
        source.ReleaseRead = release;
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
        Assert.IsFalse(model.CanStart);
    }

    [TestMethod]
    public async Task ShutdownDuringStartDisposesNewSourceAndDoesNotRestart()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new SyntheticSource();
        var model = new ApplicationMonitorViewModel(() =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new InvalidOperationException("Synthetic factory timed out.");
            }

            return source;
        });
        var start = model.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var shutdown = model.ShutdownAsync();
        release.Set();
        await Task.WhenAll(start, shutdown).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(source.Disposed);
        Assert.IsFalse(model.CanStart);
        await model.StartAsync();
        Assert.IsFalse(model.CanStop);
    }

    [TestMethod]
    public async Task PowerBoundaryStopsCaptureAndRequiresExplicitNewSession()
    {
        var source = new SyntheticSource();
        var model = new ApplicationMonitorViewModel(() => source);
        await model.StartAsync();
        await model.StopAsync(powerBoundary: true);
        Assert.IsTrue(source.Disposed);
        Assert.IsTrue(model.CanStart);
        StringAssert.Contains(model.Status, "电源状态");
        await model.RefreshAsync();
        Assert.IsTrue(model.CanStart);
        await model.ShutdownAsync();
    }

    [TestMethod]
    public async Task ReaderFailureDisposesCaptureAndShowsPartialStatus()
    {
        var source = new SyntheticSource();
        var model = new ApplicationMonitorViewModel(() => source);
        await model.StartAsync();
        source.FailRead = true;
        await model.RefreshAsync();
        Assert.IsTrue(source.Disposed);
        StringAssert.Contains(model.Status, "读取失败");
        Assert.IsTrue(model.CanStart);
        await model.ShutdownAsync();
    }

    private sealed class SyntheticSource(ApplicationCaptureState state = ApplicationCaptureState.Running, string name = "synthetic.exe")
        : IApplicationTrafficSource
    {
        private readonly Guid epoch = Guid.NewGuid();
        private bool stopped;
        public bool Disposed { get; private set; }
        public bool FailRead { get; set; }
        public TaskCompletionSource? ReadEntered { get; set; }
        public ManualResetEventSlim? ReleaseRead { get; set; }

        public ApplicationTrafficSnapshot Read()
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            ReadEntered?.TrySetResult();
            if (ReleaseRead is not null && !ReleaseRead.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new InvalidOperationException("Synthetic read timed out.");
            }

            if (FailRead)
            {
                throw new InvalidOperationException("Synthetic read failure.");
            }

            var ledger = new ApplicationTrafficLedger();
            if (state == ApplicationCaptureState.Running)
            {
                ledger.ProcessStarted(7, 1, name, 100);
                ledger.Observe(7, 2, NetworkTransport.Tcp4, true, 100);
            }

            return new(epoch, stopped ? ApplicationCaptureState.Stopped : state, ledger.Snapshot(), true, 0);
        }

        public Task StopAsync()
        {
            stopped = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
