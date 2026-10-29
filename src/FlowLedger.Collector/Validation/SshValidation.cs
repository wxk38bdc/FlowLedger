using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FlowLedger.Application.History;
using FlowLedger.Application.Monitoring;
using FlowLedger.Domain.Accounting;
using FlowLedger.Domain.Ssh;
using FlowLedger.Infrastructure.Windows.History;
using FlowLedger.Infrastructure.Windows.Monitoring;

namespace FlowLedger.Collector.Validation;

internal static class SshValidation
{
    internal static async Task<int> RunAsync()
    {
        var executable = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe");
        if (!File.Exists(executable)) { Console.WriteLine("{\"State\":\"OpenSshUnavailable\"}"); return 1; }
        var results = new List<object>(); var allPassed = true;
        foreach (var ipv6 in new[] { false, true })
        {
            await using var capture = new WindowsApplicationTrafficSource(Array.Empty<SshServerRule>());
            if (capture.Read().State != ApplicationCaptureState.Running)
            { Console.WriteLine(JsonSerializer.Serialize(new { State = capture.Read().State.ToString(), capture.Read().ErrorCode })); return 1; }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var ip = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
            using var listener = new TcpListener(ip, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await Task.Delay(1500, deadline.Token);
            var server = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
                var stream = peer.GetStream();
                var banner = "SSH-2.0-FlowLedgerValidation\r\n"u8.ToArray();
                await stream.WriteAsync(banner, deadline.Token);
                using var exchange = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); exchange.CancelAfter(TimeSpan.FromSeconds(2));
                var buffer = new byte[8192]; var bytes = 0;
                try
                {
                    while (bytes < 65536)
                    {
                        var count = await stream.ReadAsync(buffer, exchange.Token);
                        if (count == 0) { break; }
                        bytes += count;
                    }
                }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                return new TrafficVolume((ulong)banner.Length, (ulong)bytes);
            }, deadline.Token);
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-F", "NUL", "-T", "-oBatchMode=yes", "-oProxyCommand=none", "-oProxyJump=none", "-oIdentityAgent=none", "-oIdentityFile=none",
                "-oIdentitiesOnly=yes", "-oUserKnownHostsFile=NUL", "-oGlobalKnownHostsFile=NUL", "-oStrictHostKeyChecking=yes", "-oConnectTimeout=5", "-oConnectionAttempts=1",
                "-oPreferredAuthentications=none", "-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture), $"flowledger-validation@{ip}" }) { info.ArgumentList.Add(arg); }
            using var child = Process.Start(info)!;
            var output = child.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = child.StandardError.ReadToEndAsync(deadline.Token);
            try
            {
                var delivered = await server.WaitAsync(deadline.Token);
                await child.WaitForExitAsync(deadline.Token); await Task.WhenAll(output, error);
                await Task.Delay(1500, deadline.Token);
                await capture.StopAsync();
                var snapshot = capture.Read();
                var rows = snapshot.Ssh!.Connections.Where(row => row.ProcessId == child.Id && row.Server == ip.ToString() && row.Port == port).ToArray();
                var usage = rows.Aggregate(default(TrafficVolume), (sum, row) => sum.Add(row.Traffic));
                var passed = rows.Length == 1 && usage == delivered && delivered.ReceivedBytes > 0 && delivered.SentBytes > 0 &&
                    rows[0].Evidence == SshEvidence.ClientCandidate && snapshot.Ledger.LostEvents == 0 && snapshot.Ledger.RejectedEvents == 0 &&
                    !snapshot.Ledger.IdentityUncertain && snapshot.LossCounterAvailable;
                var path = HistoryValidation.TemporaryPath();
                try
                {
                    var store = new SqliteHistoryStore(path); store.Initialize(); var now = DateTimeOffset.UtcNow;
                    var frame = new HistoryFrame(Guid.NewGuid(), snapshot.Epoch, "ssh", HistoryLayer.Ssh, 0, now.AddSeconds(-8), now, usage,
                        [new("synthetic", "Synthetic SSH endpoint", usage)], "");
                    store.Append([frame]); store = new(path); store.Initialize(); store.Append([frame]);
                    var report = UsageReports.Build(store.Read(new("ssh", now.AddMinutes(-1), now.AddMinutes(1))), new("ssh", now.AddMinutes(-1), now.AddMinutes(1)), ReportPeriod.Day, TimeZoneInfo.Utc);
                    passed &= report.Total == delivered;
                }
                finally { HistoryValidation.DeleteTemporary(path); }
                results.Add(new
                {
                    Family = ipv6 ? "IPv6" : "IPv4",
                    Passed = passed,
                    Connections = rows.Length,
                    Delivered = delivered,
                    Observed = usage,
                    snapshot.Ledger.LostEvents,
                    snapshot.Ledger.RejectedEvents,
                    snapshot.Ledger.IdentityUncertain
                }); allPassed &= passed;
            }
            finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { Passed = allPassed, Results = results, Scope = "Local OpenSSH banner/KEX only; no authentication, remote host, credentials or configuration changes." }));
        return allPassed ? 0 : 1;
    }
}
