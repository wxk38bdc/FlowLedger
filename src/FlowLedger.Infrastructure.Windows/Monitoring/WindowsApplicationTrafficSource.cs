using System.Runtime.InteropServices;
using System.Security.Principal;
using FlowLedger.Application.Monitoring;
using FlowLedger.Domain.Accounting;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace FlowLedger.Infrastructure.Windows.Monitoring;

/// <summary>Explicitly started, memory-only ETW session. Does not attach to or replace another session.</summary>
public sealed class WindowsApplicationTrafficSource : IApplicationTrafficSource
{
    private readonly object lifecycleGate = new();
    private readonly object ledgerGate = new();
    private readonly ApplicationTrafficLedger ledger = new();
    private readonly Guid epoch = Guid.NewGuid();
    private readonly IEtwApplicationSession? session;
    private readonly Task? consumer;
    private Task? stopTask;
    private ApplicationCaptureState state;
    private bool stopping;
    private bool lossCounterAvailable;
    private int errorCode;
    private EtwRecoverySummary? recovery;
    internal string? SessionName { get; }

    public WindowsApplicationTrafficSource() : this(Guid.NewGuid()) { }

    internal WindowsApplicationTrafficSource(Guid captureEpoch)
    {
        epoch = captureEpoch;
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            state = ApplicationCaptureState.AccessDenied;
            errorCode = 5;
            return;
        }

        TraceEventSession? created = null;
        try
        {
            recovery = EtwSessionOwnership.Recover();
            SessionName = EtwSessionOwnership.CreateName("App", epoch);
            created = new TraceEventSession(SessionName, TraceEventSessionOptions.NoRestartOnCreate)
            {
                BufferSizeMB = 64,
                StopOnDispose = true,
            };
            created.EnableKernelProvider(KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.NetworkTCPIP);
            var source = created.Source;
            var parser = new KernelTraceEventParser(source, KernelTraceEventParser.ParserTrackingOptions.None);
            parser.ProcessStart += data => OnProcessStart(data, false);
            parser.ProcessDCStart += data => OnProcessStart(data, true);
            parser.ProcessStop += data => WithLedger(() => ledger.ProcessStopped(data.ProcessID, Qpc(data), data.UniqueProcessKey));
            parser.TcpIpSend += data => OnNetwork(data, NetworkTransport.Tcp4, false);
            parser.TcpIpRecv += data => OnNetwork(data, NetworkTransport.Tcp4, true);
            parser.TcpIpSendIPV6 += data => OnNetwork(data, NetworkTransport.Tcp6, false);
            parser.TcpIpRecvIPV6 += data => OnNetwork(data, NetworkTransport.Tcp6, true);
            parser.UdpIpSend += data => OnNetwork(data, NetworkTransport.Udp4, false);
            parser.UdpIpRecv += data => OnNetwork(data, NetworkTransport.Udp4, true);
            parser.UdpIpSendIPV6 += data => OnNetwork(data, NetworkTransport.Udp6, false);
            parser.UdpIpRecvIPV6 += data => OnNetwork(data, NetworkTransport.Udp6, true);
            session = new EtwApplicationSession(created, source);
            state = ApplicationCaptureState.Running;
            consumer = StartConsumer(session);
        }
        catch (Exception exception)
        {
            created?.Dispose();
            state = exception is UnauthorizedAccessException || (exception.HResult & 0xFFFF) == 5
                ? ApplicationCaptureState.AccessDenied : ApplicationCaptureState.Unavailable;
            errorCode = exception.HResult;
        }
    }

    internal WindowsApplicationTrafficSource(IEtwApplicationSession session)
    {
        this.session = session;
        state = ApplicationCaptureState.Running;
        consumer = StartConsumer(session);
    }

    private Task StartConsumer(IEtwApplicationSession capture) => Task.Factory.StartNew(() =>
    {
        try
        {
            capture.Process();
            lock (ledgerGate)
            {
                if (!Volatile.Read(ref stopping))
                {
                    state = ApplicationCaptureState.Faulted;
                    ledger.InvalidateIdentities();
                }
            }
        }
        catch (Exception exception)
        {
            lock (ledgerGate)
            {
                state = ApplicationCaptureState.Faulted;
                errorCode = exception.HResult;
                ledger.InvalidateIdentities();
            }
        }
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public ApplicationTrafficSnapshot Read()
    {
        lock (lifecycleGate)
        {
            if (!stopping)
            {
                ReadLossCounter();
            }

            lock (ledgerGate)
            {
                return new(epoch, state, ledger.Snapshot(), lossCounterAvailable, errorCode, recovery);
            }
        }
    }

    public Task StopAsync()
    {
        lock (lifecycleGate)
        {
            Volatile.Write(ref stopping, true);
            return stopTask ??= Task.Run(StopCoreAsync);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task StopCoreAsync()
    {
        if (session is null)
        {
            return;
        }

        try
        {
            lock (lifecycleGate)
            {
                ReadLossCounter();
                // Stop only our unique session. Drain the consumer before disposing its source.
                if (!session.Stop())
                {
                    throw new InvalidOperationException("ETW stop failed.");
                }
            }

            if (consumer is not null)
            {
                await consumer.ConfigureAwait(false);
            }

            lock (ledgerGate)
            {
                ledger.ReportLostEvents(unchecked((uint)session.FinalEventsLost));
                if (state == ApplicationCaptureState.Running)
                {
                    state = ApplicationCaptureState.Stopped;
                }
            }
        }
        catch (Exception exception)
        {
            lock (ledgerGate)
            {
                state = ApplicationCaptureState.Faulted;
                errorCode = exception.HResult;
                lossCounterAvailable = false;
                ledger.InvalidateIdentities();
            }

            session.InterruptConsumer();
            if (consumer is not null)
            {
                await consumer.ConfigureAwait(false);
            }
        }
        finally
        {
            session.Dispose();
        }
    }

    private void ReadLossCounter()
    {
        if (session is null)
        {
            return;
        }

        try
        {
            var lost = unchecked((uint)session.EventsLost);
            lock (ledgerGate)
            {
                lossCounterAvailable = true;
                ledger.ReportLostEvents(lost);
            }
        }
        catch (Exception exception)
        {
            lock (ledgerGate)
            {
                lossCounterAvailable = false;
                errorCode = exception.HResult;
                ledger.InvalidateIdentities();
            }
        }
    }

    private void OnProcessStart(ProcessTraceData data, bool rundown) => WithLedger(() =>
    {
        // Never request command lines, SIDs, full user paths, destinations or packet payloads.
        var name = data.KernelImageFileName.Replace('/', '\\').Split('\\').Last();
        ledger.ProcessStarted(data.ProcessID, Qpc(data), name, data.UniqueProcessKey, rundown);
    });

    private void OnNetwork(TraceEvent data, NetworkTransport transport, bool received) => WithLedger(() =>
    {
        if (!EtwNetworkDecoder.TryRead(data.Version, data.DataStart, data.EventDataLength, out var pid, out var size))
        {
            ledger.RejectEvent();
            return;
        }

        // No retransmit/copy/connect handlers: only one send/receive family for each transport.
        ledger.Observe(pid, Qpc(data), transport, received, size);
    });

    private void WithLedger(Action action)
    {
        lock (ledgerGate)
        {
            try
            {
                action();
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
            {
                ledger.RejectEvent();
                ledger.InvalidateIdentities();
            }
        }
    }

    // Integer QPC is deliberate: process boundaries must not be rounded through double milliseconds.
#pragma warning disable CS0618
    private static long Qpc(TraceEvent data) => data.TimeStampQPC;
#pragma warning restore CS0618
}
