using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowLedger.Application.Monitoring;
using FlowLedger.Domain.Accounting;
using FlowLedger.Infrastructure.Windows.Monitoring;

namespace FlowLedger.Collector;

internal static class InterfaceCommands
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<int> SampleAsync(int seconds, bool verify)
    {
        var reader = new WindowsInterfaceCounterReader();
        var monitor = new InterfaceMonitor();
        var first = ReadBracket(reader, verify);
        var result = monitor.Apply(first.Native);
        for (var i = 0; i < seconds; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            var next = ReadBracket(reader, verify);
            result = monitor.Apply(next.Native);
            if (i == seconds - 1)
            {
                if (verify)
                {
                    return WriteVerification(first, next);
                }
            }
        }

        // No aliases, GUIDs, addresses, SSIDs, or pre-start cumulative counters in CLI output.
        Write(new
        {
            DurationSeconds = seconds,
            result.ErrorCode,
            Interfaces = result.Interfaces.Select((item, index) => new
            {
                Slot = index + 1,
                item.Interface.Kind,
                item.Interface.NativeType,
                item.Interface.IsUp,
                item.IsPresent,
                item.Usage,
            }),
        });
        return result.ErrorCode == 0 && result.Interfaces.Any(item => item.Usage.Interval is not null) ? 0 : 1;
    }

    public static int ProbeWifi()
    {
        var snapshot = new WindowsInterfaceCounterReader().Read();
        var wifi = new WindowsWifiConnectionReader();
        var states = snapshot.Interfaces
            .Where(item => item.NativeType == 71 && item.Kind == InterfaceKind.Hardware && item.IsUp)
            .Select(item => Guid.TryParse(item.Id, out var id)
                ? wifi.Read(id) : new WifiConnection(WifiConnectionState.Unavailable))
            .Select(result => new { result.State, result.ErrorCode }).ToArray();
        Write(new { snapshot.ErrorCode, Results = states, Note = "Names are never printed. This query may trigger a Windows location prompt." });
        return snapshot.ErrorCode == 0 && states.Length > 0 ? 0 : 1;
    }

    private static Bracket ReadBracket(WindowsInterfaceCounterReader reader, bool verify)
    {
        var before = verify ? ReadReference() : [];
        var native = reader.Read();
        var after = verify ? ReadReference() : [];
        return new(before, native, after);
    }

    private static Dictionary<string, TrafficVolume> ReadReference()
    {
        var values = new Dictionary<string, TrafficVolume>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!Guid.TryParse(adapter.Id, out var id))
            {
                continue;
            }

            try
            {
                var stats = adapter.GetIPStatistics();
                if (stats.BytesReceived >= 0 && stats.BytesSent >= 0)
                {
                    values[id.ToString("D")] = new((ulong)stats.BytesReceived, (ulong)stats.BytesSent);
                }
            }
            catch (NetworkInformationException)
            {
                // A removed interface is not an invented zero reference.
            }
        }

        return values;
    }

    private static int WriteVerification(Bracket first, Bracket last)
    {
        var comparisons = 0;
        var failures = 0;
        var positive = 0;
        var nativeStart = first.Native.Interfaces.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var end in last.Native.Interfaces.Where(item => item.Kind == InterfaceKind.Hardware && item.IsUp))
        {
            if (!nativeStart.TryGetValue(end.Id, out var start) || !start.IsUp || start.ContinuityKey != end.ContinuityKey ||
                !first.Before.TryGetValue(end.Id, out var startBefore) || !first.After.TryGetValue(end.Id, out var startAfter) ||
                !last.Before.TryGetValue(end.Id, out var endBefore) || !last.After.TryGetValue(end.Id, out var endAfter))
            {
                continue;
            }

            comparisons++;
            var nativeDelta = CounterDeltaCalculator.Calculate(start.Reading, end.Reading);
            var valid = nativeDelta.Volume is { } volume &&
                InBounds(startBefore.ReceivedBytes, startAfter.ReceivedBytes, endBefore.ReceivedBytes, endAfter.ReceivedBytes, volume.ReceivedBytes) &&
                InBounds(startBefore.SentBytes, startAfter.SentBytes, endBefore.SentBytes, endAfter.SentBytes, volume.SentBytes) &&
                Inside(startBefore, start.Reading.Counters, startAfter) && Inside(endBefore, end.Reading.Counters, endAfter);
            if (!valid)
            {
                failures++;
            }
            else if (nativeDelta.Volume is { } measured && (measured.ReceivedBytes > 0 || measured.SentBytes > 0))
            {
                positive++;
            }
        }

        Write(new
        {
            FirstError = first.Native.ErrorCode,
            LastError = last.Native.ErrorCode,
            ComparedHardwareInterfaces = comparisons,
            OutsideReferenceBounds = failures,
            InterfacesWithPositiveTraffic = positive,
            Note = "GetIfTable2 native samples are bounded by .NET GetIPStatistics reads before and after each endpoint. Same OS counter layer; no ISP or application accuracy claim. No comparable interface is inconclusive.",
        });
        return first.Native.ErrorCode == 0 && last.Native.ErrorCode == 0 && comparisons > 0 && failures == 0 ? 0 : 1;
    }

    private static bool InBounds(ulong startBefore, ulong startAfter, ulong endBefore, ulong endAfter, ulong actual) =>
        startAfter >= startBefore && endBefore >= startAfter && endAfter >= endBefore &&
        actual >= endBefore - startAfter && actual <= endAfter - startBefore;

    private static bool Inside(TrafficVolume lower, TrafficVolume actual, TrafficVolume upper) =>
        lower.ReceivedBytes <= actual.ReceivedBytes && actual.ReceivedBytes <= upper.ReceivedBytes &&
        lower.SentBytes <= actual.SentBytes && actual.SentBytes <= upper.SentBytes;

    private static void Write<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private sealed record Bracket(
        Dictionary<string, TrafficVolume> Before,
        InterfaceCounterSnapshot Native,
        Dictionary<string, TrafficVolume> After);
}
