using FlowLedger.Domain.Accounting;

namespace FlowLedger.Application.Monitoring;

public enum ApplicationCaptureState
{
    Running,
    AccessDenied,
    Unavailable,
    Faulted,
    Stopped,
}

public sealed record ApplicationTrafficSnapshot(
    Guid Epoch, ApplicationCaptureState State, ApplicationLedgerSnapshot Ledger,
    bool LossCounterAvailable, int ErrorCode, EtwRecoverySummary? Recovery = null);

public sealed record EtwRecoverySummary(int Recovered, int Unconfirmed, int Failed);

public interface IApplicationTrafficSource : IAsyncDisposable
{
    ApplicationTrafficSnapshot Read();
    Task StopAsync();
}
