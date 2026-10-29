using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowLedger.Application.Monitoring;
using FlowLedger.Domain.Accounting;
using FlowLedger.Infrastructure.Windows.Monitoring;

namespace FlowLedger.Collector.Validation;

internal sealed record ProbeComparison(
    int Slot, string Profile, NetworkTransport Transport, int Exchanges,
    TrafficVolume ExpectedSocketBytes, TransportTraffic Production, TransportTraffic Reference,
    bool DeliverySucceeded, int InstanceCount, int StartEvents, int StopEvents,
    bool NewInstanceObserved, bool SameLayerMatch, bool SocketPayloadMatch, bool Passed);

internal sealed record ValidationRound(
    int Round, bool Passed, ApplicationCaptureState State, int ErrorCode, bool LossCounterAvailable,
    ulong LostEvents, ulong RejectedEvents, bool IdentityUncertain, bool CapacityReached,
    bool PartitionsMatch, bool ReferenceHealthy, uint ReferenceLostEvents,
    int IdleRundowns, int UnnamedNonIdleStarts, double LoadSeconds, double LoadPayloadMiBPerSecond,
    ProcessResourceUsage Resources, IReadOnlyList<ProbeComparison> Cases);

/// <summary>Explicit hardware acceptance, never run by ordinary unit tests or CI.</summary>
internal static class ApplicationValidation
{
    private sealed record ProbeRun(int Pid, string Profile, ProbeParameters Parameters, bool Delivered);

    public static async Task<int> RunAsync()
    {
        var rounds = new List<ValidationRound>();
        try
        {
            for (var index = 1; index <= 3; index++)
            {
                await using var source = new WindowsApplicationTrafficSource();
                var initial = source.Read();
                if (initial.State != ApplicationCaptureState.Running)
                {
                    Print(new { Passed = false, initial.State, initial.ErrorCode, Rounds = rounds });
                    return 1;
                }

                rounds.Add(await RunRoundAsync(index, source));
            }

            var passed = rounds.All(round => round.Passed);
            Print(new
            {
                Scope = "Three local loopback rounds: isolated child PIDs, asymmetric bytes, short connections and eight concurrent TCP workers. A second ETW session uses typed TraceEvent PID/size accessors. Shared kernel provider is not an independent event-delivery oracle. No QUIC, real PID reuse, Internet/billing or long-run acceptance.",
                Passed = passed,
                Rounds = rounds,
            });
            return passed ? 0 : 1;
        }
        catch (Exception exception)
        {
            // Keep machine paths, endpoints and exception messages out of public diagnostics.
            Print(new { Passed = false, ErrorType = exception.GetType().Name, exception.HResult, Rounds = rounds });
            return 1;
        }
    }

    private static async Task<ValidationRound> RunRoundAsync(int round, WindowsApplicationTrafficSource source)
    {
        using var resources = new ProcessResourceMeter();
        await using var reference = new TypedEtwReference();
        await Task.Delay(TimeSpan.FromSeconds(2));
        source.Read();
        var runs = new List<ProbeRun>();
        foreach (var transport in Enum.GetValues<NetworkTransport>())
        {
            var tcp = transport is NetworkTransport.Tcp4 or NetworkTransport.Tcp6;
            runs.Add(await RunProbeAsync(reference, "direction", transport, tcp ? 131072 : 1024, tcp ? 65536 : 512, tcp ? 1 : 8));
            runs.Add(await RunProbeAsync(reference, "short", transport, 257, 127, 24));
            resources.Sample();
            source.Read();
        }

        var loadStarted = Stopwatch.GetTimestamp();
        var load = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => RunProbeAsync(reference, "concurrent",
            index % 2 == 0 ? NetworkTransport.Tcp4 : NetworkTransport.Tcp6, 8 * 1024 * 1024, 4 * 1024 * 1024, 1)));
        var loadSeconds = Stopwatch.GetElapsedTime(loadStarted).TotalSeconds;
        runs.AddRange(load);
        resources.Sample();
        for (var index = 0; index < 3; index++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            source.Read();
            resources.Sample();
        }

        // Workers have exited; drain both sessions before comparing the entire child lifetime.
        await Task.WhenAll(source.StopAsync(), reference.CompleteAsync());
        var final = source.Read();
        var cases = runs.Select((run, index) =>
        {
            var rows = final.Ledger.Processes.Where(item => item.ProcessId == run.Pid).ToArray();
            var independent = reference.Read(run.Pid);
            return Compare(index + 1, run.Profile, run.Parameters, run.Delivered, rows,
                independent.Traffic, independent.Starts, independent.Stops);
        }).ToArray();
        var partitionMatch = ApplicationCommands.PartitionsMatch(final.Ledger);
        var passed = cases.All(item => item.Passed) && reference.Healthy && partitionMatch &&
            final.State == ApplicationCaptureState.Stopped && final.LossCounterAvailable &&
            final.Ledger.LostEvents == 0 && final.Ledger.RejectedEvents == 0 &&
            !final.Ledger.IdentityUncertain && !final.Ledger.CapacityReached;
        var loadBytes = load.Where(item => item.Delivered).Sum(item => (double)item.Parameters.Expected.ReceivedBytes + item.Parameters.Expected.SentBytes);
        return new(round, passed, final.State, final.ErrorCode, final.LossCounterAvailable,
            final.Ledger.LostEvents, final.Ledger.RejectedEvents, final.Ledger.IdentityUncertain, final.Ledger.CapacityReached,
            partitionMatch, reference.Healthy, reference.LostEvents, reference.IdleRundowns, reference.UnnamedNonIdleStarts,
            loadSeconds, loadBytes / 1048576 / loadSeconds,
            resources.Complete("Validation parent including two ETW consumers and loopback peers; excludes child CPU and kernel ETW buffers. Memory maxima are sampled, not guaranteed peaks."), cases);
    }

    internal static ProbeComparison Compare(int slot, string profile, ProbeParameters parameters, bool delivered,
        IReadOnlyList<ProcessTraffic> rows, TransportTraffic reference, int starts, int stops)
    {
        var production = rows.Aggregate(default(TransportTraffic), (sum, row) => sum.Add(row.Traffic));
        var expected = default(TransportTraffic).Add(parameters.Transport, parameters.Expected);
        var sameLayer = production == reference;
        var payloadMatch = production == expected && reference == expected;
        var newInstance = rows.Count == 1 && !rows[0].IsRundown;
        var passed = delivered && newInstance && starts == 1 && stops == 1 && sameLayer && payloadMatch;
        return new(slot, profile, parameters.Transport, parameters.Exchanges, parameters.Expected,
            production, reference, delivered, rows.Count, starts, stops, newInstance, sameLayer, payloadMatch, passed);
    }

    private static async Task<ProbeRun> RunProbeAsync(TypedEtwReference reference, string profile,
        NetworkTransport transport, int sendBytes, int receiveBytes, int exchanges)
    {
        var parameters = new ProbeParameters(transport, 0, sendBytes, receiveBytes, exchanges);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        TcpListener? tcp = null;
        UdpClient? udp = null;
        Process? worker = null;
        Task? serving = null;
        var pid = -1;
        try
        {
            if (parameters.IsTcp)
            {
                tcp = new(parameters.Address, 0);
                tcp.Start();
                parameters = parameters with { Port = ((IPEndPoint)tcp.LocalEndpoint).Port };
                serving = LoopbackProbe.ServeTcpAsync(tcp, parameters, deadline.Token);
            }
            else
            {
                udp = new(new IPEndPoint(parameters.Address, 0));
                parameters = parameters with { Port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port };
                serving = LoopbackProbe.ServeUdpAsync(udp, parameters, deadline.Token);
            }

            worker = Process.Start(WorkerStartInfo(parameters)) ?? throw new InvalidOperationException("Worker did not start.");
            pid = worker.Id;
            reference.Register(pid);
            var output = worker.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = worker.StandardError.ReadToEndAsync(deadline.Token);
            await worker.StandardInput.WriteLineAsync("go");
            worker.StandardInput.Close();
            await Task.WhenAll(serving, worker.WaitForExitAsync(deadline.Token));
            var delivered = worker.ExitCode == 0 && (await output).Trim() == "delivered" && (await error).Length == 0;
            return new(pid, profile, parameters, delivered);
        }
        catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException or Win32Exception or InvalidOperationException)
        {
            return new(pid, profile, parameters, false);
        }
        finally
        {
            await deadline.CancelAsync();
            tcp?.Stop();
            udp?.Dispose();
            if (worker is not null)
            {
                if (!worker.HasExited)
                {
                    // Only the exact child created above, never another process or its tree.
                    worker.Kill();
                    await worker.WaitForExitAsync();
                }

                worker.Dispose();
            }

            if (serving is not null)
            {
                try { await serving; }
                catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
    }

    private static ProcessStartInfo WorkerStartInfo(ProbeParameters parameters)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No executable path.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        }

        foreach (var argument in new[] { "--app-probe", parameters.Transport.ToString(), parameters.Port.ToString(CultureInfo.InvariantCulture),
            parameters.SendBytes.ToString(CultureInfo.InvariantCulture), parameters.ReceiveBytes.ToString(CultureInfo.InvariantCulture),
            parameters.Exchanges.ToString(CultureInfo.InvariantCulture) })
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    private static void Print<T>(T result) => Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    }));
}
