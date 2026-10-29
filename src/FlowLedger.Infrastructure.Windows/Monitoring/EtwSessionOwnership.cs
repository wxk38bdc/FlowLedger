using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using FlowLedger.Application.Monitoring;
using Microsoft.Diagnostics.Tracing.Session;

namespace FlowLedger.Infrastructure.Windows.Monitoring;

/// <summary>Own per-user namespace only. No adoption of legacy or foreign session names.</summary>
internal static class EtwSessionOwnership
{
    private static readonly object Gate = new();

    internal static Guid ReadOwner()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlowLedger");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "etw-owner.id");
        lock (Gate)
        {
            if (!File.Exists(path))
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    using var writer = new StreamWriter(stream);
                    writer.Write(Guid.NewGuid().ToString("N"));
                }
                catch (IOException) when (File.Exists(path)) { }
            }

            // A concurrently initializing process can hold the file briefly. Fail closed on error;
            // never replace a malformed identifier or broaden recovery to another namespace.
            if (new FileInfo(path).Length != 32 || !Guid.TryParseExact(File.ReadAllText(path), "N", out var owner))
            {
                throw new IOException("Invalid ETW ownership marker.");
            }

            return owner;
        }
    }

    internal static string CreateName(string kind, Guid epoch)
    {
        using var process = Process.GetCurrentProcess();
        return Format(ReadOwner(), kind, process.Id, process.StartTime.ToUniversalTime().Ticks, epoch);
    }

    internal static string Format(Guid owner, string kind, int pid, long startTicks, Guid epoch) =>
        $"FlowLedger.{kind}.{owner:N}.{pid.ToString(CultureInfo.InvariantCulture)}.{startTicks.ToString(CultureInfo.InvariantCulture)}.{epoch:N}";

    internal static bool TryParse(string name, Guid owner, out int pid, out long startTicks)
    {
        pid = 0;
        startTicks = 0;
        var parts = name.Split('.');
        return parts.Length == 6 && parts[0] == "FlowLedger" && parts[1] is "App" or "Reference" &&
            parts[2] == owner.ToString("N") && int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out pid) && pid > 0 &&
            long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out startTicks) &&
            startTicks > 0 && startTicks <= DateTime.MaxValue.Ticks && Guid.TryParseExact(parts[5], "N", out _);
    }

    internal static EtwRecoverySummary Recover() => Recover(TraceEventSession.GetActiveSessionNames(), ReadOwner(), OwnerAlive, name =>
    {
        using var abandoned = new TraceEventSession(name, TraceEventSessionOptions.Attach);
        abandoned.StopOnDispose = false;
        return abandoned.Stop();
    });

    internal static EtwRecoverySummary Recover(IEnumerable<string> names, Guid owner, Func<int, long, bool?> alive, Func<string, bool> stop)
    {
        var recovered = 0;
        var uncertain = 0;
        var failed = 0;
        foreach (var name in names)
        {
            if (!TryParse(name, owner, out var pid, out var start))
            {
                continue;
            }

            var status = alive(pid, start);
            if (status == true)
            {
                continue;
            }

            if (status is null)
            {
                uncertain++;
                continue;
            }

            try
            {
                if (stop(name)) { recovered++; } else { failed++; }
            }
            catch (Exception) { failed++; }
        }

        return new(recovered, uncertain, failed);
    }

    private static bool? OwnerAlive(int pid, long startTicks)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime().Ticks == startTicks;
        }
        catch (ArgumentException) { return false; }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException) { return null; }
    }
}
