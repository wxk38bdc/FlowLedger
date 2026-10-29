using System.Diagnostics;
using System.Text.Json;
using FlowLedger.Application.History;
using FlowLedger.Infrastructure.Windows.History;
using Microsoft.Data.Sqlite;

namespace FlowLedger.Collector.Validation;

internal static class HistoryValidation
{
    internal static string TemporaryPath() => Path.Combine(Path.GetTempPath(), $"FlowLedger-validation-{Guid.NewGuid():N}.sqlite3");
    internal static void DeleteTemporary(string path)
    {
        if (!IsTemporary(path)) { return; }
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) { File.Delete(path + suffix); }
    }
    private static bool IsTemporary(string path) => Path.GetDirectoryName(Path.GetFullPath(path)) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
        && Path.GetFileName(path) is var name && name.StartsWith("FlowLedger-validation-", StringComparison.Ordinal)
        && name.EndsWith(".sqlite3", StringComparison.Ordinal) && Guid.TryParseExact(name[22..^8], "N", out _);

    internal static async Task<int> TransactionWorkerAsync(string path)
    {
        if (!IsTemporary(path) || !File.Exists(path)) { return 2; }
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO settings VALUES('uncommitted','synthetic')";
        command.ExecuteNonQuery(); Console.WriteLine("ready"); Console.Out.Flush();
        await Task.Delay(TimeSpan.FromSeconds(30));
        return 1; // Deliberately never commits; the parent terminates only this helper.
    }

    internal static async Task<int> RunAsync()
    {
        var path = TemporaryPath();
        Process? worker = null;
        try
        {
            var store = new SqliteHistoryStore(path); store.Initialize();
            var now = DateTimeOffset.UtcNow;
            var first = new HistoryFrame(Guid.NewGuid(), Guid.NewGuid(), "synthetic", HistoryLayer.Application, 0, now, now.AddSeconds(1), new(100, 50), [new("app", "Synthetic", new(100, 50))], "");
            store.Append([first]);
            worker = Process.Start(WorkerStartInfo("--history-transaction-worker", path))!;
            var ready = await worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (ready != "ready") { throw new InvalidOperationException("Worker not ready."); }
            worker.Kill(); await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            store = new(path); store.Initialize(); store.Append([first]);
            var readout = store.Read(new("synthetic", now.AddSeconds(-1), now.AddMinutes(1)));
            var passed = readout.Facts.Count == 1 && readout.Facts[0].Traffic == new FlowLedger.Domain.Accounting.TrafficVolume(100, 50) && store.GetSetting("uncommitted") is null;
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = "PRAGMA integrity_check";
            var integrity = (string?)command.ExecuteScalar() == "ok";
            command.CommandText = "SELECT sqlite_version()"; var version = command.ExecuteScalar();
            Console.WriteLine(JsonSerializer.Serialize(new { Passed = passed && integrity, ReplayExactlyOnce = readout.Facts.Count == 1, UncommittedRolledBack = store.GetSetting("uncommitted") is null, Integrity = integrity, SqliteVersion = version }));
            return passed && integrity ? 0 : 1;
        }
        finally
        {
            if (worker is not null) { if (!worker.HasExited) { worker.Kill(); await worker.WaitForExitAsync(); } worker.Dispose(); }
            DeleteTemporary(path);
        }
    }

    internal static ProcessStartInfo WorkerStartInfo(params string[] args)
    {
        var executable = Environment.ProcessPath!;
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) { info.ArgumentList.Add(typeof(HistoryValidation).Assembly.Location); }
        foreach (var arg in args) { info.ArgumentList.Add(arg); }
        return info;
    }
}
