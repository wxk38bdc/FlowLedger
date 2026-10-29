using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using FlowLedger.Application.Monitoring;
using FlowLedger.Domain.Accounting;
using FlowLedger.Infrastructure.Windows.Monitoring;
using Microsoft.Diagnostics.Tracing.Session;

namespace FlowLedger.Collector.Validation;

internal static class ReliabilityValidation
{
    private sealed record RecoveryResult(bool Passed, bool FaultVisible, bool AbandonedObserved, int Recovered, bool Clean);

    internal static async Task<int> RecoveryAsync()
    {
        await using (var permission = new WindowsApplicationTrafficSource())
        {
            var state = permission.Read();
            if (state.State != ApplicationCaptureState.Running)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { Passed = false, state.State, state.ErrorCode }));
                return 1;
            }
        }

        var result = await VerifyRecoveryAsync();
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result.Passed ? 0 : 1;
    }
    internal static ProcessStartInfo WorkerInfo(params string[] arguments)
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException();
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        }

        foreach (var argument in arguments) { info.ArgumentList.Add(argument); }
        return info;
    }

    internal static async Task<int> RunAsync(int seconds)
    {
        var results = new List<object>();
        var passed = true;
        try
        {
            for (var round = 1; round <= 3; round++)
            {
                await using var source = new WindowsApplicationTrafficSource();
                var initial = source.Read();
                if (initial.State != ApplicationCaptureState.Running)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { Passed = false, initial.State, initial.ErrorCode }));
                    return 1;
                }

                await using var reference = new TypedEtwReference();
                using var resources = new ProcessResourceMeter();
                await Task.Delay(2000);
                var probes = new List<(int Pid, string Kind, int Exit, string Output)>();
                foreach (var kind in new[] { "4", "6", "sustained" })
                {
                    using var worker = Process.Start(kind == "sustained" ? WorkerInfo("--sustained-probe", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)) : WorkerInfo("--quic-probe", kind))!;
                    reference.Register(worker.Id);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 30));
                    var output = worker.StandardOutput.ReadToEndAsync(timeout.Token);
                    var error = worker.StandardError.ReadToEndAsync(timeout.Token);
                    try
                    {
                        await worker.StandardInput.WriteLineAsync("go");
                        worker.StandardInput.Close();
                        while (!worker.HasExited)
                        {
                            await Task.Delay(250, timeout.Token);
                            source.Read();
                            resources.Sample();
                        }

                        await worker.WaitForExitAsync(timeout.Token);
                        probes.Add((worker.Id, kind, (await error).Length == 0 ? worker.ExitCode : 1, await output));
                    }
                    finally
                    {
                        if (!worker.HasExited) { worker.Kill(); await worker.WaitForExitAsync(); }
                    }
                }

                await Task.Delay(3000);
                await Task.WhenAll(source.StopAsync(), reference.CompleteAsync());
                var snapshot = source.Read();
                var cases = probes.Select(probe =>
                {
                    var rows = snapshot.Ledger.Processes.Where(row => row.ProcessId == probe.Pid).ToArray();
                    var observed = rows.Aggregate(default(TransportTraffic), (sum, row) => sum.Add(row.Traffic));
                    var independent = reference.Read(probe.Pid);
                    var sameLayer = observed == independent.Traffic;
                    var expected = probe.Kind == "sustained" && probe.Exit == 0 ? JsonSerializer.Deserialize<TransportTraffic>(probe.Output) : default;
                    var payloadMatch = probe.Kind == "sustained" ? observed == expected && expected.Total.SentBytes > 0 :
                        probe.Output.Trim() == "delivered" && (probe.Kind == "4" ? observed.Udp4 : observed.Udp6) is { ReceivedBytes: > 393216, SentBytes: > 393216 };
                    var ok = probe.Exit == 0 && sameLayer && payloadMatch && rows.Length == 1 && !rows[0].IsRundown && independent.Starts == 1 && independent.Stops == 1;
                    return new { Kind = probe.Kind, Passed = ok, Exit = probe.Exit, SameLayerMatch = sameLayer, PayloadCheck = payloadMatch, Observed = observed };
                }).ToArray();
                var healthy = snapshot.State == ApplicationCaptureState.Stopped && snapshot.LossCounterAvailable &&
                    snapshot.Ledger.LostEvents == 0 && snapshot.Ledger.RejectedEvents == 0 && !snapshot.Ledger.IdentityUncertain &&
                    !snapshot.Ledger.CapacityReached && ApplicationCommands.PartitionsMatch(snapshot.Ledger) && reference.Healthy;
                passed &= healthy && cases.All(item => item.Passed);
                results.Add(new
                {
                    Round = round,
                    Healthy = healthy,
                    snapshot.Ledger.LostEvents,
                    snapshot.Ledger.RejectedEvents,
                    ReferenceLost = reference.LostEvents,
                    Cases = cases,
                    Resources = resources.Complete("Dual ETW validation parent; excludes worker CPU and kernel buffers; sampled memory maxima.")
                });
            }

            var recovery = await VerifyRecoveryAsync();
            passed &= recovery.Passed;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Passed = passed,
                SustainedSecondsPerRound = seconds,
                RecoveryPassed = recovery.Passed,
                Recovery = recovery,
                Scope = "Local QUIC v4/v6, sustained TCP and owned-session interruption/crash recovery. No WAN or 72-hour claim.",
                Rounds = results
            }, new JsonSerializerOptions { WriteIndented = true }));
            return passed ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { Passed = false, ErrorType = exception.GetType().Name, exception.HResult, Rounds = results }));
            return 1;
        }
    }

    private static async Task<RecoveryResult> VerifyRecoveryAsync()
    {
        // Interrupt exactly the live session this method owns, never a discovered foreign name.
        await using (var interrupted = new WindowsApplicationTrafficSource())
        {
            if (interrupted.Read().State != ApplicationCaptureState.Running || interrupted.SessionName is null) { return new(false, false, false, 0, false); }
            using var control = new TraceEventSession(interrupted.SessionName, TraceEventSessionOptions.Attach) { StopOnDispose = false };
            if (!control.Stop()) { return new(false, false, false, 0, false); }
            await Task.Delay(1500);
            if (interrupted.Read().State != ApplicationCaptureState.Faulted) { return new(false, false, false, 0, false); }
            await interrupted.StopAsync();
        }

        var epoch = Guid.NewGuid();
        using var worker = Process.Start(WorkerInfo("--capture-probe", epoch.ToString("N")))!;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (await worker.StandardOutput.ReadLineAsync(timeout.Token) != "ready") { return new(false, true, false, 0, false); }
            worker.Kill();
            await worker.WaitForExitAsync(timeout.Token);
            var abandoned = TraceEventSession.GetActiveSessionNames().Any(name => name.EndsWith($".{epoch:N}", StringComparison.Ordinal));
            await using var restarted = new WindowsApplicationTrafficSource();
            var state = restarted.Read();
            var clean = !TraceEventSession.GetActiveSessionNames().Any(name => name.EndsWith($".{epoch:N}", StringComparison.Ordinal));
            var passed = state.State == ApplicationCaptureState.Running && state.Recovery is { Failed: 0, Unconfirmed: 0 } && clean &&
                (!abandoned || state.Recovery.Recovered > 0);
            return new(passed, true, abandoned, state.Recovery?.Recovered ?? 0, clean);
        }
        finally
        {
            if (!worker.HasExited) { worker.Kill(); await worker.WaitForExitAsync(); }
            // A cleanup attempt is also required when the validation itself failed.
            EtwSessionOwnership.Recover();
        }
    }
}
