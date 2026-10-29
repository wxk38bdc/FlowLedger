using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlowLedger.Application.History;
using FlowLedger.Domain.Accounting;
using Microsoft.Data.Sqlite;

namespace FlowLedger.Infrastructure.Windows.History;

/// <summary>Local-only, transactional cumulative checkpoints and exact interval deltas.
/// Unsigned byte counts are decimal TEXT: SQLite INTEGER and SUM cannot represent UInt64.</summary>
public sealed class SqliteHistoryStore(string path) : IHistoryStore
{
    private readonly object gate = new();
    private readonly string connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(path),
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false,
        DefaultTimeout = 5,
        ForeignKeys = true,
    }.ToString();
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlowLedger", "history.sqlite3");

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    public void Initialize()
    {
        lock (gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var connection = Open();
            var version = Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version"), CultureInfo.InvariantCulture);
            if (version > 1) { throw new InvalidOperationException("Newer database schema; no downgrade attempted."); }
            Execute(connection, null, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;");
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, """
                CREATE TABLE IF NOT EXISTS streams(id TEXT PRIMARY KEY, layer INTEGER NOT NULL, label TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS frames(id TEXT PRIMARY KEY, hash TEXT NOT NULL,
                  stream TEXT NOT NULL, epoch TEXT NOT NULL, sequence INTEGER NOT NULL, ended INTEGER NOT NULL, quality TEXT NOT NULL,
                  UNIQUE(stream,epoch,sequence));
                CREATE TABLE IF NOT EXISTS checkpoints(stream TEXT NOT NULL, epoch TEXT NOT NULL, payload TEXT NOT NULL,
                  PRIMARY KEY(stream,epoch));
                CREATE TABLE IF NOT EXISTS facts(frame TEXT NOT NULL REFERENCES frames(id), stream TEXT NOT NULL, epoch TEXT NOT NULL,
                  started INTEGER NOT NULL, ended INTEGER NOT NULL, entity TEXT NOT NULL, label TEXT NOT NULL,
                  received TEXT NOT NULL, sent TEXT NOT NULL, quality TEXT NOT NULL, PRIMARY KEY(frame,entity));
                CREATE INDEX IF NOT EXISTS facts_range ON facts(stream,ended);
                CREATE INDEX IF NOT EXISTS frames_range ON frames(stream,ended);
                CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                PRAGMA user_version=1;
                """);
            transaction.Commit();
        }
    }

    public void Append(IReadOnlyList<HistoryFrame> frames)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            foreach (var frame in frames) { AppendFrame(connection, transaction, frame); }
            transaction.Commit();
        }
    }

    private static void AppendFrame(SqliteConnection connection, SqliteTransaction transaction, HistoryFrame frame)
    {
        if (frame.Id == Guid.Empty || frame.Epoch == Guid.Empty || frame.Sequence < 0 || !Enum.IsDefined(frame.Layer) ||
            string.IsNullOrWhiteSpace(frame.Stream) || frame.Stream.Length > 4096 || frame.Entries.Count > 8192 || frame.Quality.Length > 2048 ||
            (frame.Observed is null && frame.Entries.Count > 0) ||
            frame.Entries.Any(item => string.IsNullOrEmpty(item.Key) || item.Key.Length > 4096 || item.Label.Length > 2048) ||
            frame.Entries.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != frame.Entries.Count)
        { throw new ArgumentException("Invalid history frame."); }
        if (frame.Observed is { } total && (frame.Entries.Count == 0 || frame.Entries.Aggregate(default(TrafficVolume), (sum, item) => sum.Add(item.Total)) != total))
        { throw new ArgumentException("History partitions must conserve observed bytes."); }
        var payload = JsonSerializer.Serialize(frame);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        var existing = Scalar(connection, transaction, "SELECT hash FROM frames WHERE id=$id", ("$id", frame.Id.ToString("N"))) as string;
        if (existing is not null)
        {
            if (existing != hash) { throw new InvalidOperationException("Conflicting batch identity."); }
            return;
        }
        var streamLayer = Scalar(connection, transaction, "SELECT layer FROM streams WHERE id=$stream", ("$stream", frame.Stream));
        if (streamLayer is not null && Convert.ToInt32(streamLayer, CultureInfo.InvariantCulture) != (int)frame.Layer)
        { throw new InvalidOperationException("Cannot mix measurement layers."); }
        Execute(connection, transaction, "INSERT INTO streams VALUES($stream,$layer,$label) ON CONFLICT(id) DO UPDATE SET label=excluded.label",
            ("$stream", frame.Stream), ("$layer", (int)frame.Layer), ("$label", frame.StreamLabel ?? ""));
        var previousJson = Scalar(connection, transaction, "SELECT payload FROM checkpoints WHERE stream=$stream AND epoch=$epoch",
            ("$stream", frame.Stream), ("$epoch", frame.Epoch.ToString("N"))) as string;
        var previous = previousJson is null ? null : JsonSerializer.Deserialize<HistoryFrame>(previousJson)!;
        if (previous is not null && frame.Sequence <= previous.Sequence) { throw new InvalidOperationException("Stale or conflicting sequence."); }
        var effectiveQuality = frame.Quality + (previous is not null && frame.CapturedAt < previous.CapturedAt ? " ClockChanged" : "");
        Execute(connection, transaction, "INSERT INTO frames VALUES($id,$hash,$stream,$epoch,$sequence,$ended,$quality)",
            ("$id", frame.Id.ToString("N")), ("$hash", hash), ("$stream", frame.Stream), ("$epoch", frame.Epoch.ToString("N")),
            ("$sequence", frame.Sequence), ("$ended", frame.CapturedAt.UtcTicks), ("$quality", effectiveQuality));
        if (frame.Observed is { } observed)
        {
            var priorTotal = previous?.Observed ?? default;
            if (!TrySubtract(observed, priorTotal, out var delta)) { throw new InvalidOperationException("Counter decreased without a new epoch."); }
            var before = previous?.Entries.ToDictionary(item => item.Key, StringComparer.Ordinal) ?? [];
            var after = frame.Entries.ToDictionary(item => item.Key, StringComparer.Ordinal);
            var revoked = before.Any(item => !after.ContainsKey(item.Key) || !TrySubtract(after[item.Key].Total, item.Value.Total, out _));
            var start = previous?.CapturedAt ?? frame.StartedAt;
            if (start > frame.CapturedAt) { start = frame.CapturedAt; }
            if (revoked)
            {
                Revoke(connection, transaction, frame.Stream, frame.Epoch);
                InsertFact(connection, transaction, frame, start, "$unknown", "$unknown", delta, "AttributionRevoked");
            }
            else
            {
                if (delta == default && frame.Entries.Count > 1)
                {
                    InsertFact(connection, transaction, frame, start, "$observed", "$observed", default, effectiveQuality);
                }
                foreach (var item in frame.Entries)
                {
                    _ = TrySubtract(item.Total, before.GetValueOrDefault(item.Key)?.Total ?? default, out var change);
                    // Keep a valid zero observation distinguishable from no observation.
                    if (change != default || (delta == default && frame.Entries.Count == 1)) { InsertFact(connection, transaction, frame, start, item.Key, item.Label, change, effectiveQuality); }
                }
            }
        }
        var checkpoint = frame.Observed is null && previous?.Observed is not null
            ? previous with { Sequence = frame.Sequence, CapturedAt = frame.CapturedAt } : frame;
        Execute(connection, transaction, "INSERT INTO checkpoints VALUES($stream,$epoch,$payload) ON CONFLICT(stream,epoch) DO UPDATE SET payload=excluded.payload",
            ("$stream", frame.Stream), ("$epoch", frame.Epoch.ToString("N")), ("$payload", JsonSerializer.Serialize(checkpoint)));
    }

    private static void Revoke(SqliteConnection connection, SqliteTransaction transaction, string stream, Guid epoch)
    {
        var retained = new Dictionary<string, (long Start, long End, TrafficVolume Bytes)>();
        using (var command = Command(connection, transaction, "SELECT frame,started,ended,received,sent FROM facts WHERE stream=$stream AND epoch=$epoch",
            ("$stream", stream), ("$epoch", epoch.ToString("N"))))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var bytes = new TrafficVolume(Parse(reader.GetString(3)), Parse(reader.GetString(4)));
                var previous = retained.GetValueOrDefault(id);
                retained[id] = (reader.GetInt64(1), reader.GetInt64(2), previous.Bytes.Add(bytes));
            }
        }
        Execute(connection, transaction, "DELETE FROM facts WHERE stream=$stream AND epoch=$epoch", ("$stream", stream), ("$epoch", epoch.ToString("N")));
        foreach (var item in retained)
        {
            Execute(connection, transaction, "INSERT INTO facts VALUES($frame,$stream,$epoch,$start,$end,'$unknown','$unknown',$rx,$tx,'AttributionRevoked')",
                ("$frame", item.Key), ("$stream", stream), ("$epoch", epoch.ToString("N")), ("$start", item.Value.Start), ("$end", item.Value.End),
                ("$rx", Text(item.Value.Bytes.ReceivedBytes)), ("$tx", Text(item.Value.Bytes.SentBytes)));
        }
    }

    private static void InsertFact(SqliteConnection connection, SqliteTransaction transaction, HistoryFrame frame, DateTimeOffset start,
        string key, string label, TrafficVolume bytes, string quality) =>
        Execute(connection, transaction, "INSERT INTO facts VALUES($id,$stream,$epoch,$start,$end,$key,$label,$rx,$tx,$quality)",
            ("$id", frame.Id.ToString("N")), ("$stream", frame.Stream), ("$epoch", frame.Epoch.ToString("N")), ("$start", start.UtcTicks),
            ("$end", frame.CapturedAt.UtcTicks), ("$key", key), ("$label", label), ("$rx", Text(bytes.ReceivedBytes)), ("$tx", Text(bytes.SentBytes)), ("$quality", quality));

    public IReadOnlyList<HistoryStream> GetStreams()
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null, "SELECT id,layer,label FROM streams ORDER BY layer,id");
            using var reader = command.ExecuteReader();
            var result = new List<HistoryStream>();
            while (reader.Read()) { result.Add(new(reader.GetString(0), (HistoryLayer)reader.GetInt32(1), reader.GetString(2))); }
            return result;
        }
    }

    public HistoryReadout Read(HistoryQuery query)
    {
        if (query.UntilUtc <= query.FromUtc) { throw new ArgumentException("Invalid range."); }
        // WAL readers get one consistent snapshot without acquiring the writer gate or
        // an immediate write transaction. A report must not stall one-second capture.
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var facts = new List<HistoryFact>();
        using (var command = Command(connection, transaction,
            "SELECT started,ended,entity,label,received,sent,quality FROM facts WHERE stream=$stream AND ended >= $from AND ended < $until ORDER BY ended,entity",
            ("$stream", query.Stream), ("$from", query.FromUtc.UtcTicks), ("$until", query.UntilUtc.UtcTicks)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                facts.Add(new(new(reader.GetInt64(0), TimeSpan.Zero), new(reader.GetInt64(1), TimeSpan.Zero),
                reader.GetString(2), reader.GetString(3), new(Parse(reader.GetString(4)), Parse(reader.GetString(5))), reader.GetString(6)));
            }
        }
        var issues = Convert.ToInt32(Scalar(connection, transaction,
            "SELECT COUNT(*) FROM frames WHERE stream=$stream AND ended >= $from AND ended < $until AND quality <> ''",
            ("$stream", query.Stream), ("$from", query.FromUtc.UtcTicks), ("$until", query.UntilUtc.UtcTicks)), CultureInfo.InvariantCulture);
        transaction.Commit();
        return new(facts, issues + facts.Count(item => item.Quality == "AttributionRevoked"));
    }

    public string? GetSetting(string key)
    {
        lock (gate) { using var connection = Open(); return Scalar(connection, null, "SELECT value FROM settings WHERE key=$key", ("$key", key)) as string; }
    }

    public void SetSetting(string key, string value)
    {
        if (value.Length > 65536) { throw new ArgumentException("Setting is too large."); }
        lock (gate)
        {
            using var connection = Open(); Execute(connection, null,
            "INSERT INTO settings VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$key", key), ("$value", value));
        }
    }

    private static string Text(ulong value) => value.ToString(CultureInfo.InvariantCulture);
    private static ulong Parse(string value) => ulong.Parse(value, CultureInfo.InvariantCulture);
    private static bool TrySubtract(TrafficVolume current, TrafficVolume previous, out TrafficVolume result)
    {
        result = default;
        if (current.ReceivedBytes < previous.ReceivedBytes || current.SentBytes < previous.SentBytes) { return false; }
        result = new(current.ReceivedBytes - previous.ReceivedBytes, current.SentBytes - previous.SentBytes); return true;
    }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] values)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var value in values) { command.Parameters.AddWithValue(value.Name, value.Value); }
        return command;
    }
    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] values)
    { using var command = Command(connection, transaction, sql, values); return command.ExecuteScalar(); }
    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] values)
    { using var command = Command(connection, transaction, sql, values); command.ExecuteNonQuery(); }
}
