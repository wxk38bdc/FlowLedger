namespace FlowLedger.Application.Diagnostics;

public interface IHostDiagnosticsReader
{
    HostEnvironmentSnapshot Read();
}
