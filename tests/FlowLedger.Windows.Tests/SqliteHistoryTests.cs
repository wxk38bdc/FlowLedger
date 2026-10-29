using FlowLedger.Application.History;
using FlowLedger.Domain.Accounting;
using FlowLedger.Infrastructure.Windows.History;
using Microsoft.Data.Sqlite;

namespace FlowLedger.Windows.Tests;

[TestClass]
public sealed class SqliteHistoryTests
{
    private string path = "";
    private readonly Guid epoch = Guid.NewGuid();
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private SqliteHistoryStore store = null!;
    [TestInitialize]
    public void Initialize() { path = Path.Combine(Path.GetTempPath(), $"FlowLedger-test-{Guid.NewGuid():N}.sqlite3"); store = new(path); store.Initialize(); }
    [TestCleanup]
    public void Cleanup() { foreach (var suffix in new[] { "", "-wal", "-shm" }) { File.Delete(path + suffix); } }
    private HistoryFrame Frame(long sequence, ulong received, string label = "Synthetic", string key = "one") =>
        new(Guid.NewGuid(), epoch, "applications", HistoryLayer.Application, sequence, Start, Start.AddSeconds(sequence + 1), new(received, 0), [new(key, label, new(received, 0))], "");
    private HistoryReadout Read() => store.Read(new("applications", Start, Start.AddDays(1)));

    [TestMethod]
    public void ReopenAndReplayDoNotDuplicateDeltas()
    {
        var first = Frame(0, 100); var second = Frame(1, 140);
        store.Append([first]); store = new(path); store.Initialize(); store.Append([first, second, second]);
        Assert.AreEqual(140UL, Read().Facts.Aggregate(0UL, (sum, row) => checked(sum + row.Traffic.ReceivedBytes)));
        Assert.HasCount(2, Read().Facts);
    }

    [TestMethod]
    public void WholeBatchRollsBackWhenOneFrameViolatesConservation()
    {
        Assert.Throws<ArgumentException>(() => store.Append([Frame(0, 100), Frame(1, 200) with { Observed = new(201, 0) }]));
        Assert.IsEmpty(Read().Facts);
        store.Append([Frame(0, 100)]); Assert.HasCount(1, Read().Facts);
    }

    [TestMethod]
    public void ConflictingReplayAndStaleSequenceAreRejectedAtomically()
    {
        var first = Frame(0, 100); store.Append([first]);
        Assert.Throws<InvalidOperationException>(() => store.Append([first with { Quality = "Changed" }]));
        Assert.Throws<InvalidOperationException>(() => store.Append([Frame(0, 100)]));
        Assert.HasCount(1, Read().Facts);
    }

    [TestMethod]
    public void FullUnsignedRangeStaysExactAcrossDiskAndReport()
    {
        store.Append([Frame(0, ulong.MaxValue)]);
        var report = UsageReports.Build(Read(), new("applications", Start, Start.AddDays(1)), ReportPeriod.Day, TimeZoneInfo.Utc);
        Assert.AreEqual(ulong.MaxValue, report.Total!.Value.ReceivedBytes);
        Assert.AreEqual(ulong.MaxValue, report.Rows.Single().Traffic.ReceivedBytes);
    }

    [TestMethod]
    public void MissingObservationDoesNotCreateAZeroMeasurement()
    {
        store.Append([Frame(0, 0) with { Observed = null, Entries = [], Quality = "Offline" }]);
        Assert.IsEmpty(Read().Facts); Assert.AreEqual(1, Read().QualityEvents);
        store.Append([Frame(1, 0)]);
        Assert.HasCount(1, Read().Facts); Assert.AreEqual(default(TrafficVolume), Read().Facts[0].Traffic);
    }

    [TestMethod]
    public void HistoricalAttributionIsRevokedWithoutChangingTimesOrTotals()
    {
        store.Append([Frame(0, 100), Frame(1, 150)]);
        store.Append([Frame(2, 170, "$unknown", "$unknown")]);
        var facts = Read().Facts;
        Assert.HasCount(3, facts);
        Assert.IsTrue(facts.All(row => row.Key == "$unknown"));
        Assert.AreEqual(Start.AddSeconds(1), facts[0].CapturedAt);
        Assert.AreEqual(170UL, facts.Aggregate(0UL, (sum, row) => checked(sum + row.Traffic.ReceivedBytes)));
        Assert.IsTrue(Read().QualityEvents > 0);
    }

    [TestMethod]
    public void CounterDecreaseRequiresNewEpochAndLayersCannotMix()
    {
        store.Append([Frame(0, 100)]);
        Assert.Throws<InvalidOperationException>(() => store.Append([Frame(1, 50)]));
        Assert.Throws<InvalidOperationException>(() => store.Append([Frame(1, 120) with { Layer = HistoryLayer.Ssh }]));
        store.Append([Frame(0, 20) with { Epoch = Guid.NewGuid() }]);
        Assert.AreEqual(120UL, Read().Facts.Aggregate(0UL, (sum, row) => checked(sum + row.Traffic.ReceivedBytes)));
    }

    [TestMethod]
    public void LabelsAndSettingsAreParameterizedAndSurviveReopen()
    {
        const string label = "'; DROP TABLE facts; -- 中文";
        store.Append([Frame(0, 3, label)]); store.SetSetting("language", "en");
        store = new(path); store.Initialize();
        Assert.AreEqual(label, Read().Facts.Single().Label);
        Assert.AreEqual("en", store.GetSetting("language"));
    }

    [TestMethod]
    public void ReadRangeIsExclusiveAtUpperBoundary()
    {
        store.Append([Frame(0, 1), Frame(1, 2)]);
        var read = store.Read(new("applications", Start.AddSeconds(1), Start.AddSeconds(2)));
        Assert.HasCount(1, read.Facts);
        Assert.AreEqual(1UL, read.Facts[0].Traffic.ReceivedBytes);
    }

    [TestMethod]
    public void NewerSchemaIsNotSilentlyDowngraded()
    {
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version=99"; command.ExecuteNonQuery(); }
        Assert.Throws<InvalidOperationException>(store.Initialize);
    }

    [TestMethod]
    public async Task ConcurrentStoreInstancesReplayTheSameBatchOnce()
    {
        var frame = Frame(0, 100); var other = new SqliteHistoryStore(path);
        await Task.WhenAll(Task.Run(() => store.Append([frame])), Task.Run(() => other.Append([frame])));
        Assert.HasCount(1, Read().Facts);
    }

    [TestMethod]
    public void ClockReversalIsFlaggedAndCannotCreateNegativeIntervals()
    {
        store.Append([Frame(0, 10)]);
        store.Append([Frame(1, 20) with { CapturedAt = Start }]);
        var facts = Read().Facts;
        Assert.AreEqual(20UL, facts.Aggregate(0UL, (sum, row) => checked(sum + row.Traffic.ReceivedBytes)));
        Assert.IsTrue(facts.All(row => row.StartedAt <= row.CapturedAt));
        Assert.IsTrue(facts.Any(row => row.Quality.Contains("ClockChanged", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void WalReaderSnapshotDoesNotBlockHistoryWriter()
    {
        store.Append([Frame(0, 10)]);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open(); using var transaction = connection.BeginTransaction(deferred: true);
        using var read = connection.CreateCommand(); read.Transaction = transaction; read.CommandText = "SELECT COUNT(*) FROM facts";
        Assert.AreEqual(1L, read.ExecuteScalar());
        store.Append([Frame(1, 20)]);
        Assert.AreEqual(1L, read.ExecuteScalar());
        transaction.Commit(); Assert.HasCount(2, Read().Facts);
    }

    [TestMethod]
    public void IdleSnapshotDoesNotWriteOneZeroFactForEveryRetainedProcess()
    {
        var entries = Enumerable.Range(0, 100).Select(index => new HistoryEntry(index.ToString(), "Synthetic", new(1, 0))).ToArray();
        var first = Frame(0, 100) with { Entries = entries };
        store.Append([first]); store.Append([first with { Id = Guid.NewGuid(), Sequence = 1, CapturedAt = Start.AddSeconds(2) }]);
        var idle = Read().Facts.Where(item => item.CapturedAt == Start.AddSeconds(2)).ToArray();
        Assert.HasCount(1, idle); Assert.AreEqual(default(TrafficVolume), idle[0].Traffic);
    }
}
