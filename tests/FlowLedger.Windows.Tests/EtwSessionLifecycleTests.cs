using System.Runtime.InteropServices;
using FlowLedger.Application.Monitoring;
using FlowLedger.Infrastructure.Windows.Monitoring;

namespace FlowLedger.Windows.Tests;

[TestClass]
public sealed class EtwSessionLifecycleTests
{
    [TestMethod]
    public async Task StopIsIdempotentAndDrainsConsumerBeforeDispose()
    {
        using var session = new SyntheticSession();
        await using var source = new WindowsApplicationTrafficSource(session);
        await session.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(ApplicationCaptureState.Running, source.Read().State);
        await Task.WhenAll(source.StopAsync(), source.StopAsync()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, session.StopCalls);
        Assert.IsTrue(session.ConsumerFinished);
        Assert.IsTrue(session.Disposed);
        Assert.IsFalse(session.DisposedBeforeConsumerFinished);
        Assert.AreEqual(ApplicationCaptureState.Stopped, source.Read().State);
    }

    [TestMethod]
    public async Task LostEventsStayCumulativeAndFinalDrainIsIncluded()
    {
        using var session = new SyntheticSession { Lost = 3, FinalLost = 5 };
        await using var source = new WindowsApplicationTrafficSource(session);
        Assert.AreEqual(3UL, source.Read().Ledger.LostEvents);
        Assert.AreEqual(3UL, source.Read().Ledger.LostEvents);
        await source.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(5UL, source.Read().Ledger.LostEvents);
        Assert.IsTrue(source.Read().Ledger.IdentityUncertain);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedLossQueryIsNotReportedAsHealthyZeroLoss(bool accessDenied)
    {
        using var session = new SyntheticSession { FailQuery = true, DenyQuery = accessDenied };
        await using var source = new WindowsApplicationTrafficSource(session);
        var result = source.Read();
        Assert.IsFalse(result.LossCounterAvailable);
        Assert.IsTrue(result.Ledger.IdentityUncertain);
        await source.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task FailedNativeStopInterruptsAndDrainsBeforeDispose()
    {
        using var session = new SyntheticSession { FailStop = true };
        await using var source = new WindowsApplicationTrafficSource(session);
        await session.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(ApplicationCaptureState.Faulted, source.Read().State);
        Assert.IsTrue(session.Interrupted);
        Assert.IsFalse(session.DisposedBeforeConsumerFinished);
    }

    [TestMethod]
    public async Task ConsumerFailureIsVisibleAndSessionCanStillBeReleased()
    {
        using var session = new SyntheticSession { FailProcess = true };
        await using var source = new WindowsApplicationTrafficSource(session);
        await session.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(ApplicationCaptureState.Faulted, source.Read().State);
        Assert.IsTrue(session.Disposed);
    }

    private sealed class SyntheticSession : IEtwApplicationSession
    {
        private readonly ManualResetEventSlim release = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ConsumerFinished { get; private set; }
        public bool Disposed { get; private set; }
        public bool DisposedBeforeConsumerFinished { get; private set; }
        public bool Interrupted { get; private set; }
        public int StopCalls { get; private set; }
        public int Lost { get; init; }
        public int FinalLost { get; init; }
        public bool FailQuery { get; init; }
        public bool DenyQuery { get; init; }
        public bool FailStop { get; init; }
        public bool FailProcess { get; init; }
        public int EventsLost => DenyQuery ? throw new UnauthorizedAccessException("Synthetic loss query denied.") :
            FailQuery ? throw new COMException("Synthetic loss query failure.") : Lost;
        public int FinalEventsLost => FinalLost;

        public void Process()
        {
            Entered.TrySetResult();
            try
            {
                if (FailProcess)
                {
                    throw new InvalidOperationException("Synthetic consumer failure.");
                }

                if (!release.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new InvalidOperationException("Synthetic consumer timed out.");
                }
            }
            finally
            {
                ConsumerFinished = true;
            }
        }

        public bool Stop()
        {
            StopCalls++;
            if (FailStop)
            {
                return false;
            }

            release.Set();
            return true;
        }

        public void InterruptConsumer()
        {
            Interrupted = true;
            release.Set();
        }

        public void Dispose()
        {
            if (!Disposed)
            {
                DisposedBeforeConsumerFinished = !ConsumerFinished;
                Disposed = true;
                release.Dispose();
            }
        }
    }
}
