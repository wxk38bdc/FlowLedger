using FlowLedger.Domain.Accounting;

namespace FlowLedger.Application.History;

public enum HistoryLayer { Interface, Wifi, Application, ProxyCore, ProxyConnection, Ssh }
public sealed record HistoryEntry(string Key, string Label, TrafficVolume Total);
public sealed record HistoryFrame(Guid Id, Guid Epoch, string Stream, HistoryLayer Layer, long Sequence,
    DateTimeOffset StartedAt, DateTimeOffset CapturedAt, TrafficVolume? Observed,
    IReadOnlyList<HistoryEntry> Entries, string Quality, string? StreamLabel = null);
public sealed record HistoryFact(DateTimeOffset StartedAt, DateTimeOffset CapturedAt,
    string Key, string Label, TrafficVolume Traffic, string Quality);
public sealed record HistoryStream(string Id, HistoryLayer Layer, string? Name = null);
public sealed record HistoryQuery(string Stream, DateTimeOffset FromUtc, DateTimeOffset UntilUtc, bool GroupByLabel = false);
public sealed record HistoryReadout(IReadOnlyList<HistoryFact> Facts, int QualityEvents);

public interface IHistoryStore
{
    void Initialize();
    void Append(IReadOnlyList<HistoryFrame> frames);
    IReadOnlyList<HistoryStream> GetStreams();
    HistoryReadout Read(HistoryQuery query);
    string? GetSetting(string key);
    void SetSetting(string key, string value);
}

public enum ReportPeriod { Day, Week, Month, Custom }
public sealed record ReportRow(string Period, string Key, string Label, TrafficVolume Traffic, int Intervals, int PartialIntervals);
public sealed record UsageReport(IReadOnlyList<ReportRow> Rows, TrafficVolume? Total, int QualityEvents,
    DateTimeOffset FromUtc, DateTimeOffset UntilUtc, string TimeZone, string Stream);

/// <summary>Reports use observation-end timestamps. Weeks start Monday, ranges are [from, until).</summary>
public static class UsageReports
{
    public static UsageReport Build(HistoryReadout readout, HistoryQuery query, ReportPeriod period, TimeZoneInfo zone)
    {
        if (query.UntilUtc <= query.FromUtc) { throw new ArgumentException("Invalid report range."); }
        var rows = new Dictionary<(DateOnly Period, string Key, string Label), (TrafficVolume Traffic, int Count, int Partial)>();
        TrafficVolume? total = null;
        foreach (var fact in readout.Facts.Where(item => item.CapturedAt >= query.FromUtc && item.CapturedAt < query.UntilUtc))
        {
            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(fact.CapturedAt, zone).DateTime);
            var start = period switch
            {
                ReportPeriod.Week => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
                ReportPeriod.Month => new DateOnly(date.Year, date.Month, 1),
                ReportPeriod.Custom => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(query.FromUtc, zone).DateTime),
                _ => date,
            };
            var key = (start, query.GroupByLabel ? fact.Label : fact.Key, fact.Label);
            var previous = rows.GetValueOrDefault(key);
            rows[key] = (previous.Traffic.Add(fact.Traffic), checked(previous.Count + 1), checked(previous.Partial + (fact.Quality.Length > 0 ? 1 : 0)));
            total = (total ?? default).Add(fact.Traffic);
        }
        return new(rows.OrderBy(item => item.Key.Period).ThenBy(item => item.Key.Label, StringComparer.Ordinal)
            .Select(item => new ReportRow(item.Key.Period.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                item.Key.Key, item.Key.Label, item.Value.Traffic, item.Value.Count, item.Value.Partial)).ToArray(),
            total, readout.QualityEvents, query.FromUtc, query.UntilUtc, zone.Id, query.Stream);
    }

    public static DateTimeOffset LocalBoundary(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // Some historical zones skip midnight. Advance only across the invalid boundary.
        for (var i = 0; zone.IsInvalidTime(local) && i < 1440; i++) { local = local.AddMinutes(1); }
        if (zone.IsInvalidTime(local)) { throw new ArgumentException("Unsupported local boundary."); }
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
