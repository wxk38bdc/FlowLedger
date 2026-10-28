using System.Text.Json;
using System.Text.Json.Serialization;
using FlowLedger.Application.Diagnostics;
using FlowLedger.Collector;
using FlowLedger.Infrastructure.Windows.Diagnostics;

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
          FlowLedger.Collector --help       Show this help

        Samples stay in memory. No service installation, automatic elevation or network settings changes.
        Names, process IDs, stable IDs, addresses, SSIDs, and payloads are never printed by these commands.
        --verify-apps generates bounded local loopback traffic only; no external server is contacted.
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
