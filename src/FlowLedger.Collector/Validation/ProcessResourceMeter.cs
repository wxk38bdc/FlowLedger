using System.Diagnostics;

namespace FlowLedger.Collector.Validation;

internal sealed record ProcessResourceUsage(
    double ElapsedSeconds, double CpuMilliseconds, double NormalizedCpuPercent,
    long MaximumSampledWorkingSetBytes, long MaximumSampledPrivateBytes, int LogicalProcessors,
    string Scope);

internal sealed class ProcessResourceMeter : IDisposable
{
    private readonly Process process = Process.GetCurrentProcess();
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly TimeSpan startedCpu;
    private long workingSet;
    private long privateBytes;

    public ProcessResourceMeter()
    {
        startedCpu = process.TotalProcessorTime;
        Sample();
    }

    public void Sample()
    {
        process.Refresh();
        workingSet = Math.Max(workingSet, process.WorkingSet64);
        privateBytes = Math.Max(privateBytes, process.PrivateMemorySize64);
    }

    public ProcessResourceUsage Complete(string scope)
    {
        Sample();
        var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var cpu = (process.TotalProcessorTime - startedCpu).TotalMilliseconds;
        return new(elapsed, cpu, cpu / (elapsed * 1000 * Environment.ProcessorCount) * 100,
            workingSet, privateBytes, Environment.ProcessorCount, scope);
    }

    public void Dispose() => process.Dispose();
}
