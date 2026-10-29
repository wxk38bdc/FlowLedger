using FlowLedger.Application.History;
using FlowLedger.Domain.Accounting;

namespace FlowLedger.Application.Tests;

[TestClass]
public sealed class UsageReportTests
{
    [TestMethod]
    public void WeekStartsMondayAndMonthBoundariesFollowSelectedZone()
    {
        var start = new DateTimeOffset(2026, 1, 31, 23, 30, 0, TimeSpan.Zero);
        var zone = TimeZoneInfo.CreateCustomTimeZone("Synthetic UTC+2", TimeSpan.FromHours(2), "Synthetic", "Synthetic");
        var query = new HistoryQuery("applications", start.AddDays(-1), start.AddDays(1));
        var readout = new HistoryReadout([new(start.AddSeconds(-1), start, "a", "App", new(100, 5), "")], 0);
        Assert.AreEqual("2026-02-01", UsageReports.Build(readout, query, ReportPeriod.Month, zone).Rows.Single().Period);
        Assert.AreEqual("2026-01-26", UsageReports.Build(readout, query, ReportPeriod.Week, zone).Rows.Single().Period);
        Assert.AreEqual(new TrafficVolume(100, 5), UsageReports.Build(readout, query, ReportPeriod.Day, zone).Total);
    }

    [TestMethod]
    public void EmptyPeriodsStayMissingAndCustomRangeDoesNotInventZeros()
    {
        var start = DateTimeOffset.UnixEpoch;
        var query = new HistoryQuery("applications", start, start.AddDays(3));
        var report = UsageReports.Build(new([], 2), query, ReportPeriod.Day, TimeZoneInfo.Utc);
        Assert.IsNull(report.Total); Assert.IsEmpty(report.Rows); Assert.AreEqual(2, report.QualityEvents);
        var zero = UsageReports.Build(new([new(start, start, "a", "App", default, "")], 0), query, ReportPeriod.Custom, TimeZoneInfo.Utc);
        Assert.AreEqual(default(TrafficVolume), zero.Total); Assert.HasCount(1, zero.Rows);
    }

    [TestMethod]
    public void OptionalNameGroupingCombinesInstancesWhileDefaultKeepsDistinctIdentities()
    {
        var start = DateTimeOffset.UnixEpoch;
        var readout = new HistoryReadout([new(start, start, "one", "App", new(10, 1), ""), new(start, start, "two", "App", new(20, 2), "")], 0);
        var query = new HistoryQuery("applications", start, start.AddDays(1));
        Assert.HasCount(2, UsageReports.Build(readout, query, ReportPeriod.Day, TimeZoneInfo.Utc).Rows);
        var grouped = UsageReports.Build(readout, query with { GroupByLabel = true }, ReportPeriod.Day, TimeZoneInfo.Utc);
        Assert.HasCount(1, grouped.Rows); Assert.AreEqual(new TrafficVolume(30, 3), grouped.Total);
    }

    [TestMethod]
    public void DstDayRangeUsesActualUtcBoundaries()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");
        var start = UsageReports.LocalBoundary(new(2026, 3, 8), zone);
        var end = UsageReports.LocalBoundary(new(2026, 3, 9), zone);
        Assert.AreEqual(TimeSpan.FromHours(23), end - start);
        var fallStart = UsageReports.LocalBoundary(new(2026, 11, 1), zone);
        var fallEnd = UsageReports.LocalBoundary(new(2026, 11, 2), zone);
        Assert.AreEqual(TimeSpan.FromHours(25), fallEnd - fallStart);
    }

    [TestMethod]
    public void CrossPeriodObservationIsBookedOnlyAtEndAndPartialQualityIsVisible()
    {
        var end = new DateTimeOffset(2026, 2, 1, 0, 0, 1, TimeSpan.Zero);
        var fact = new HistoryFact(end.AddSeconds(-3), end, "one", "Synthetic", new(100, 10), "StorageGap");
        var readout = new HistoryReadout([fact], 1);
        var query = new HistoryQuery("ssh", end.AddDays(-1), end.AddDays(1));
        var report = UsageReports.Build(readout, query, ReportPeriod.Month, TimeZoneInfo.Utc);
        Assert.HasCount(1, report.Rows); Assert.AreEqual("2026-02-01", report.Rows[0].Period);
        Assert.AreEqual(1, report.Rows[0].PartialIntervals); Assert.AreEqual("ssh", report.Stream);
        Assert.AreEqual(new TrafficVolume(100, 10), report.Total);
    }
}
