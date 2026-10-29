using System.Text.Json;
using System.Text.Json.Serialization;
using FlowLedger.Application.Diagnostics;
using FlowLedger.Collector;
using FlowLedger.Collector.Validation;
using FlowLedger.Infrastructure.Windows.Diagnostics;
using FlowLedger.Infrastructure.Windows.Proxy;

if (args.Length == 0 || args is ["--help"] or ["-h"])
{
    Console.WriteLine("""
        FlowLedger collector — interface and application validation build

        Usage:
          FlowLedger.Collector --diagnose   Print read-only environment metadata as JSON
          FlowLedger.Collector --sample <1..60>           Sample interface traffic for N seconds
          FlowLedger.Collector --verify-counters <1..60>  Compare hardware counters with bounded reference reads
          FlowLedger.Collector --probe-wifi               Query Wi-Fi capability; print status only (may prompt)
          FlowLedger.Collector --watch-wifi <1..60>       Observe Wi-Fi notifications and usage (may prompt)
          FlowLedger.Collector --route-summary           Print anonymous per-interface route evidence
          FlowLedger.Collector --watch-apps <1..60>       Observe anonymous per-process ETW usage (administrator)
          FlowLedger.Collector --verify-apps              Controlled loopback TCP/UDP v4/v6 coverage probe (administrator)
          FlowLedger.Collector --validate-apps            Three rounds: child attribution, typed ETW reference, short/concurrent loopback probes
          FlowLedger.Collector --verify-reliability <15..300>  Three rounds of QUIC, sustained TCP, owned-session fault/crash recovery
          FlowLedger.Collector --verify-etw-recovery      Interrupt own test session, terminate own helper, verify recovery (administrator)
          FlowLedger.Collector --watch-proxy <1..60> <controller-url> <proxy-port>  Anonymous read-only core usage
          FlowLedger.Collector --verify-proxy <controller-url> <proxy-port>       Local explicit-proxy attribution probe
          FlowLedger.Collector --verify-tcp-owners        Verify native IPv4/IPv6 socket ownership using controlled loopback
          FlowLedger.Collector --help       Show this help

        Samples stay in memory. No service installation, automatic elevation or network settings changes.
        Names, process IDs, stable IDs, addresses, SSIDs, and payloads are never printed by these commands.
        --verify-apps / --validate-apps generate bounded local loopback traffic only; no external server is contacted.
        --validate-apps needs administrator rights and starts finite helper processes; no payloads are captured.
        Proxy commands accept a loopback controller IP only. Optional token comes from FLOWLEDGER_CONTROLLER_SECRET, never CLI arguments.
        --verify-proxy transfers a synthetic 1 MiB loopback response; it does not test TUN or Internet paths.
        Interface counts include virtual/filter entries and are not physical adapter counts.
        """);
    return 0;
}

if (args is ["--probe-wifi"])
{
    return InterfaceCommands.ProbeWifi();
}

if (args is ["--route-summary"])
{
    return InterfaceCommands.RouteSummary();
}

if (args is ["--verify-apps"])
{
    return await ApplicationCommands.VerifyAsync();
}

if (args is ["--validate-apps"])
{
    return await ApplicationValidation.RunAsync();
}

if (args is ["--verify-reliability", var reliabilityDuration] && int.TryParse(reliabilityDuration, out var reliabilitySeconds) && reliabilitySeconds is >= 15 and <= 300)
{
    return await ReliabilityValidation.RunAsync(reliabilitySeconds);
}

if (args is ["--verify-etw-recovery"]) { return await RunSafeAsync(ReliabilityValidation.RecoveryAsync); }
if (args is ["--verify-tcp-owners"]) { return await RunSafeAsync(ProxyCommands.VerifyOwnersAsync); }

if (args is ["--watch-proxy", var proxyDuration, var proxyEndpoint, var proxyPort] &&
    int.TryParse(proxyDuration, out var proxySeconds) && proxySeconds is >= 1 and <= 60 &&
    int.TryParse(proxyPort, out var proxyPortNumber) && proxyPortNumber is >= 1 and <= 65535 && ClashProxySource.TryEndpoint(proxyEndpoint, out _))
{
    return await ProxyCommands.RunAsync(proxyEndpoint, proxyPortNumber, proxySeconds);
}

if (args is ["--verify-proxy", var controllerEndpoint, var inboundPort] &&
    int.TryParse(inboundPort, out var inboundPortNumber) && inboundPortNumber is >= 1 and <= 65535 && ClashProxySource.TryEndpoint(controllerEndpoint, out _))
{
    return await ProxyCommands.RunAsync(controllerEndpoint, inboundPortNumber, null);
}

try
{
    if (args is ["--quic-probe", "4" or "6"]) { return await ReliabilityWorkers.QuicAsync(args[1] == "6"); }
    if (args is ["--sustained-probe", var workerDuration] && int.TryParse(workerDuration, out var workerSeconds) && workerSeconds is >= 15 and <= 300) { return await ReliabilityWorkers.SustainedAsync(workerSeconds); }
    if (args is ["--capture-probe", var captureId] && Guid.TryParseExact(captureId, "N", out var captureEpoch)) { return await ReliabilityWorkers.CaptureAsync(captureEpoch); }
}
catch (Exception exception)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        ErrorType = exception.GetType().Name,
        exception.HResult,
        InnerType = exception.InnerException?.GetType().Name,
        InnerCode = exception.InnerException?.HResult,
        QuicError = (exception as System.Net.Quic.QuicException)?.QuicError.ToString()
    }));
    return 1;
}

// Internal validation worker: no ETW, hardcoded loopback, strict byte/count limits and deadline.
if (ProbeParameters.TryParse(args, out var probe))
{
    return await LoopbackProbe.WorkerAsync(probe!);
}

if (args is ["--watch-apps", var applicationDuration] &&
    int.TryParse(applicationDuration, out var applicationSeconds) && applicationSeconds is >= 1 and <= 60)
{
    return await ApplicationCommands.WatchAsync(applicationSeconds);
}

if (args is ["--watch-wifi", var wifiDuration] && int.TryParse(wifiDuration, out var wifiSeconds) && wifiSeconds is >= 1 and <= 60)
{
    return await InterfaceCommands.WatchWifiAsync(wifiSeconds);
}

if (args is ["--sample" or "--verify-counters", var duration] &&
    int.TryParse(duration, out var seconds) && seconds is >= 1 and <= 60)
{
    return await InterfaceCommands.SampleAsync(seconds, args[0] == "--verify-counters");
}

if (args is not ["--diagnose"])
{
    Console.Error.WriteLine("Unknown arguments. Run with --help for available commands.");
    return 2;
}

IHostDiagnosticsReader reader = new WindowsHostDiagnosticsReader();
var snapshot = reader.Read();
var options = new JsonSerializerOptions
{
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() },
};

Console.WriteLine(JsonSerializer.Serialize(snapshot, options));
return snapshot.NetworkInventoryState == NetworkInventoryState.Available ? 0 : 1;

static async Task<int> RunSafeAsync(Func<Task<int>> operation)
{
    try { return await operation(); }
    catch (Exception exception)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { State = "Unavailable", ErrorType = exception.GetType().Name, exception.HResult }));
        return 1;
    }
}
