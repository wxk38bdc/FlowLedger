using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace FlowLedger.Infrastructure.Windows.Monitoring;

/// <summary>Separates session ownership/drain tests from privileged native ETW calls.</summary>
internal interface IEtwApplicationSession : IDisposable
{
    int EventsLost { get; }
    int FinalEventsLost { get; }
    void Process();
    bool Stop();
    void InterruptConsumer();
}

internal sealed class EtwApplicationSession(TraceEventSession session, ETWTraceEventSource source) : IEtwApplicationSession
{
    public int EventsLost => session.EventsLost;
    public int FinalEventsLost => source.EventsLost;
    public void Process() => source.Process();
    public bool Stop() => session.Stop();
    public void InterruptConsumer() => source.StopProcessing();
    public void Dispose() => session.Dispose();
}
