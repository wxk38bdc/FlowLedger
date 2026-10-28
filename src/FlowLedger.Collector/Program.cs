using System.Text.Json;
using System.Text.Json.Serialization;
using FlowLedger.Application.Diagnostics;
using FlowLedger.Infrastructure.Windows.Diagnostics;

if (args.Length == 0 || args is ["--help"] or ["-h"])
{
    Console.WriteLine("""
        FlowLedger collector — foundation build

        Usage:
          FlowLedger.Collector --diagnose   Print read-only environment metadata as JSON
          FlowLedger.Collector --help       Show this help

        This build does not collect traffic, install a service, or change network settings.
        Interface counts include virtual/filter entries and are not physical adapter counts.
        """);
    return 0;
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
