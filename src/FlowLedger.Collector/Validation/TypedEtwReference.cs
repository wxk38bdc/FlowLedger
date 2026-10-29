using FlowLedger.Domain.Accounting;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace FlowLedger.Collector.Validation;

/// <summary>Validation only: a second session and the library's typed PID/size accessors.
/// Does not reuse the production decoder or lifetime ledger; never reads endpoints or retains names.</summary>
internal sealed class TypedEtwReference : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly TraceEventSession session;
    private readonly ETWTraceEventSource source;
    private readonly Task consumer;
    private readonly Dictionary<int, TransportTraffic> traffic = [];
    private readonly Dictionary<int, int> starts = [];
    private readonly Dictionary<int, int> stops = [];
    private Task? completion;
    private bool stopping;
    private volatile bool failed;
    private uint lost;
    private int idleRundowns;
    private int unnamedNonIdleStarts;

    public TypedEtwReference()
    {
        session = new TraceEventSession($"FlowLedger.Reference.{Guid.NewGuid():N}", TraceEventSessionOptions.NoRestartOnCreate)
        {
            BufferSizeMB = 64,
            StopOnDispose = true,
        };
        try
        {
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.NetworkTCPIP);
            source = session.Source;
            var parser = new KernelTraceEventParser(source, KernelTraceEventParser.ParserTrackingOptions.None);
            parser.ProcessStart += data => Lifecycle(starts, data.ProcessID);
            parser.ProcessStop += data => Lifecycle(stops, data.ProcessID);
            parser.ProcessDCStart += data =>
            {
                lock (gate)
                {
                    if (data.ProcessID == 0)
                    {
                        idleRundowns++;
                    }
                    else if (string.IsNullOrWhiteSpace(data.KernelImageFileName))
                    {
                        unnamedNonIdleStarts++;
                    }
                }
            };
            // TraceEvent fixes ProcessID from the payload before these typed callbacks.
            parser.TcpIpSend += data => Add(data.ProcessID, NetworkTransport.Tcp4, false, data.size);
            parser.TcpIpRecv += data => Add(data.ProcessID, NetworkTransport.Tcp4, true, data.size);
            parser.TcpIpSendIPV6 += data => Add(data.ProcessID, NetworkTransport.Tcp6, false, data.size);
            parser.TcpIpRecvIPV6 += data => Add(data.ProcessID, NetworkTransport.Tcp6, true, data.size);
            parser.UdpIpSend += data => Add(data.ProcessID, NetworkTransport.Udp4, false, data.size);
            parser.UdpIpRecv += data => Add(data.ProcessID, NetworkTransport.Udp4, true, data.size);
            parser.UdpIpSendIPV6 += data => Add(data.ProcessID, NetworkTransport.Udp6, false, data.size);
            parser.UdpIpRecvIPV6 += data => Add(data.ProcessID, NetworkTransport.Udp6, true, data.size);
            consumer = Task.Factory.StartNew(() =>
            {
                try
                {
                    source.Process();
                    if (!Volatile.Read(ref stopping))
                    {
                        failed = true;
                    }
                }
                catch (Exception)
                {
                    failed = true;
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public void Register(int pid)
    {
        lock (gate)
        {
            traffic.Add(pid, default);
        }
    }

    public (TransportTraffic Traffic, int Starts, int Stops) Read(int pid)
    {
        lock (gate)
        {
            return (traffic.GetValueOrDefault(pid), starts.GetValueOrDefault(pid), stops.GetValueOrDefault(pid));
        }
    }

    public bool Healthy => !failed && lost == 0;
    public uint LostEvents => lost;
    public int IdleRundowns => idleRundowns;
    public int UnnamedNonIdleStarts => unnamedNonIdleStarts;
    public Task CompleteAsync() => completion ??= CompleteCoreAsync();
    public async ValueTask DisposeAsync() => await CompleteAsync();

    private async Task CompleteCoreAsync()
    {
        Volatile.Write(ref stopping, true);
        try
        {
            lost = unchecked((uint)session.EventsLost);
            if (!session.Stop())
            {
                failed = true;
                source.StopProcessing();
            }

            await consumer;
            lost = Math.Max(lost, unchecked((uint)source.EventsLost));
        }
        catch (Exception)
        {
            failed = true;
            source.StopProcessing();
            await consumer;
        }
        finally
        {
            session.Dispose();
        }
    }

    private void Lifecycle(Dictionary<int, int> counts, int pid)
    {
        lock (gate)
        {
            // Bounded even on a busy machine; overflow is an inconclusive reference.
            if (counts.Count >= 8192 && !counts.ContainsKey(pid))
            {
                failed = true;
                return;
            }

            counts[pid] = counts.GetValueOrDefault(pid) + 1;
        }
    }

    private void Add(int pid, NetworkTransport transport, bool received, int size)
    {
        lock (gate)
        {
            if (traffic.TryGetValue(pid, out var current))
            {
                var bytes = unchecked((uint)size);
                traffic[pid] = current.Add(transport, received ? new(bytes, 0) : new(0, bytes));
            }
        }
    }
}
