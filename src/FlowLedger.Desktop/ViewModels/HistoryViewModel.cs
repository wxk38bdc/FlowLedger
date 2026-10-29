using System.Collections.ObjectModel;
using System.Diagnostics;
using FlowLedger.Application.History;
using FlowLedger.Application.Monitoring;
using FlowLedger.Desktop.Localization;
using FlowLedger.Domain.Accounting;
using FlowLedger.Domain.Proxy;
using FlowLedger.Domain.Ssh;

namespace FlowLedger.Desktop.ViewModels;

public sealed class StreamChoice(HistoryStream stream) : ObservableState
{
    public HistoryStream Stream { get; } = stream;
    public string Label => $"{HistoryViewModel.LayerName(Stream.Layer)}" + (string.IsNullOrEmpty(Stream.Name) ? "" : $" · {Stream.Name} · {Stream.Id[^Math.Min(8, Stream.Id.Length)..]}");
}
public sealed class PeriodChoice(ReportPeriod period, UiText name) : ObservableState { public ReportPeriod Period { get; } = period; public string Label => name; }
public sealed record ReportDisplayRow(string Period, string Entity, string Download, string Upload, int Intervals, string Quality);

public sealed class HistoryViewModel(IHistoryStore store, string databasePath, TimeSpan? saveInterval = null) : ObservableState
{
    private readonly TimeSpan writeInterval = saveInterval ?? TimeSpan.FromSeconds(5);
    private long? lastWrite;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<(string Stream, Guid Epoch), HistoryFrame> pending = [];
    private readonly Dictionary<(string Stream, Guid Epoch), HistoryFrame> pendingMeasurements = [];
    private readonly Dictionary<(string Stream, Guid Epoch), (long Sequence, DateTimeOffset Start)> sequences = [];
    private readonly Guid windowEpoch = Guid.NewGuid();
    private UiText status = L.T("正在打开本地账本…", "Opening the local ledger…");
    private UiText reportStatus = L.T("选择账本与日期范围后查询。", "Select a ledger and date range to query.");
    private bool initialized;
    private bool failed;
    private bool queryBusy;
    private UsageReport? report;
    private StreamChoice? selectedStream;
    private DateTime? fromDate = DateTime.Today.AddDays(1 - DateTime.Today.Day);
    private DateTime? untilDate = DateTime.Today;
    private PeriodChoice? selectedPeriod;
    public ObservableCollection<StreamChoice> Streams { get; } = [];
    public IReadOnlyList<PeriodChoice> Periods { get; } = [new(ReportPeriod.Day, L.T("按日", "Daily")), new(ReportPeriod.Week, L.T("按周", "Weekly")),
        new(ReportPeriod.Month, L.T("按月", "Monthly")), new(ReportPeriod.Custom, L.T("自定义区间合计", "Custom range total"))];
    public IReadOnlyList<TimeZoneInfo> TimeZones { get; } = TimeZoneInfo.GetSystemTimeZones();
    private TimeZoneInfo timeZone = TimeZoneInfo.Local;
    public TimeZoneInfo SelectedTimeZone { get => timeZone; set { timeZone = value; ClearReport(); } }
    public StreamChoice? SelectedStream { get => selectedStream; set { selectedStream = value; ClearReport(); } }
    public PeriodChoice SelectedPeriod { get => selectedPeriod ?? Periods[0]; set { selectedPeriod = value; ClearReport(); } }
    public DateTime? FromDate { get => fromDate; set { fromDate = value; ClearReport(); } }
    public DateTime? UntilDate { get => untilDate; set { untilDate = value; ClearReport(); } }
    public string Status => status;
    public string ReportStatus => reportStatus;
    public string DatabasePath => databasePath;
    public bool CanQuery => initialized && !queryBusy;
    public bool HasUnsaved => pending.Count > 0;
    public bool CanExport => report is not null && !queryBusy;
    public string Total => report?.Total is { } total ? L.T($"接收 {L.Bytes(total.ReceivedBytes)} · 发送 {L.Bytes(total.SentBytes)}", $"Received {L.Bytes(total.ReceivedBytes)} · Sent {L.Bytes(total.SentBytes)}") : L.T("尚无有效测量", "No valid measurements");
    public string ReportDetails => report is null ? L.T("选择一个独立账本查询。", "Select one independent ledger to query.") :
        L.T($"{report.Rows.Count} 行 · 质量提示 {report.QualityEvents} · 时区 {report.TimeZone}", $"{report.Rows.Count} rows · Quality notices {report.QualityEvents} · Time zone {report.TimeZone}");
    public IReadOnlyList<ReportDisplayRow> Rows => report?.Rows.Select(item => new ReportDisplayRow(item.Period,
        DisplayLabel(item.Label), L.Bytes(item.Traffic.ReceivedBytes), L.Bytes(item.Traffic.SentBytes), item.Intervals,
        item.PartialIntervals > 0 ? L.T($"部分 · {item.PartialIntervals}", $"Partial · {item.PartialIntervals}") : L.T("已观察", "Observed"))).ToArray() ?? [];
    private static string DisplayLabel(string label)
    {
        if (label == "$unknown") { return L.T("未归属", "Unknown"); }
        if (label == "$boundary") { return L.T("边界未分配", "Unallocated boundary"); }
        if (label == "$observed") { return L.T("已观察的零增量", "Observed zero increment"); }
        foreach (var suffix in new[] { ("ClientCandidate", L.T("客户端候选", "Client candidate")), ("UserRule", L.T("用户规则", "User rule")) })
        {
            var tail = " · " + suffix.Item1;
            if (label.EndsWith(tail, StringComparison.Ordinal)) { return label[..^tail.Length] + " · " + suffix.Item2; }
        }
        return label.StartsWith("$unknown · ", StringComparison.Ordinal) ? L.T("未归属", "Unknown") + label[8..] : label;
    }

    public async Task InitializeAsync()
    {
        await gate.WaitAsync();
        try { await Task.Run(store.Initialize); initialized = true; status = L.T("本地 SQLite 账本已就绪。", "Local SQLite ledger is ready."); }
        catch (Exception) { failed = true; status = L.T("本地账本无法打开。采集仍可使用，但历史尚未保存。", "Cannot open the local ledger. Capture remains available; history is not saved."); }
        finally { gate.Release(); Notify(null); }
    }

    public async Task<string?> ReadSettingAsync(string key)
    {
        if (!initialized) { return null; }
        try { return await Task.Run(() => store.GetSetting(key)); }
        catch (Exception) { status = L.T("设置读取失败。", "Could not read settings."); Notify(null); return null; }
    }

    public async Task<bool> SetSettingAsync(string key, string value)
    {
        if (!initialized) { return false; }
        try { await Task.Run(() => store.SetSetting(key, value)); return true; }
        catch (Exception) { status = L.T("设置保存失败。", "Could not save settings."); Notify(null); return false; }
    }

    public async Task SaveAsync(IReadOnlyList<HistoryFrame> frames)
    {
        await gate.WaitAsync();
        try
        {
            foreach (var frame in frames)
            {
                var key = (frame.Stream, frame.Epoch);
                var previous = pending.GetValueOrDefault(key);
                if (frame.Observed is null && previous?.Observed is not null) { pendingMeasurements[key] = previous; }
                if (frame.Observed is not null) { pendingMeasurements.Remove(key); }
                var quality = previous is null || previous.Id == frame.Id ? frame.Quality : string.Join(" ",
                    (previous.Quality + " " + frame.Quality).Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal));
                pending[key] = frame with { Quality = quality };
            }
            if (frames.Count > 0 && !failed && lastWrite is { } written && Stopwatch.GetElapsedTime(written) < writeInterval)
            {
                status = L.T("已缓存最新观察 · 每 5 秒自动保存，停止/查询/退出时写入。", "Latest observations buffered · Autosave every 5 seconds; flushed on stop, query and exit.");
                return;
            }
            if (!initialized) { await Task.Run(store.Initialize); initialized = true; }
            var batch = pendingMeasurements.Values.Concat(pending.Values).OrderBy(item => item.Sequence).ToArray();
            if (batch.Length > 0)
            {
                await Task.Run(() => store.Append(batch));
                pending.Clear();
                pendingMeasurements.Clear();
            }
            failed = false;
            lastWrite = Stopwatch.GetTimestamp();
            status = L.T("已保存到本地 SQLite。", "Saved to local SQLite.");
        }
        catch (Exception) { failed = true; status = L.T("历史保存失败：最新累计量暂存内存，下次采样重试；恢复后区间时间可能不完整。", "History save failed. Latest cumulative totals remain in memory for retry; interval timing may be incomplete after recovery."); }
        finally { gate.Release(); Notify(null); }
    }

    private HistoryFrame Frame(string stream, HistoryLayer layer, Guid epoch, TrafficVolume? total, IReadOnlyList<HistoryEntry> entries, string quality, string? label = null)
    {
        var key = (stream, epoch);
        var previous = sequences.GetValueOrDefault(key, (0, DateTimeOffset.UtcNow));
        sequences[key] = (previous.Sequence + 1, previous.Start);
        return new(Guid.NewGuid(), epoch, stream, layer, previous.Sequence, previous.Start, DateTimeOffset.UtcNow, total, entries, quality + (failed ? " StorageGap" : ""), label);
    }

    public Task SaveApplicationsAsync(ApplicationTrafficSnapshot snapshot, bool ssh = false)
    {
        var quality = snapshot.State != ApplicationCaptureState.Running && snapshot.State != ApplicationCaptureState.Stopped
            || !snapshot.LossCounterAvailable || snapshot.Ledger.IdentityUncertain || snapshot.Ledger.CapacityReached
            || snapshot.Ledger.RejectedEvents > 0 ? "PartialCapture" : "";
        if (ssh)
        {
            var usage = snapshot.Ssh;
            var entries = usage?.Connections.Select(item => new HistoryEntry(item.Key,
                $"{item.Alias} · {SshEndpoint.Format(item.Server, item.Port)} · {item.Client} · {item.Evidence}", item.Traffic)).ToList() ?? [];
            if (usage?.Observed is not null) { entries.Add(new("$unknown", "$unknown", usage.Unidentified)); }
            return SaveAsync([Frame("ssh", HistoryLayer.Ssh, snapshot.Epoch, usage?.Observed, entries,
                quality + (usage?.CapacityReached == true ? " Capacity" : ""))]);
        }
        var processes = snapshot.Ledger.Processes.Select(item => new HistoryEntry($"{item.ProcessId}:{item.FirstSeenQpc}", item.Name, item.Traffic.Total)).ToList();
        if (snapshot.Ledger.Observed is not null) { processes.Add(new("$unknown", "$unknown", snapshot.Ledger.Unidentified.Total)); }
        return SaveAsync([Frame("applications", HistoryLayer.Application, snapshot.Epoch, snapshot.Ledger.Observed?.Total, processes, quality)]);
    }

    public Task SaveInterfacesAsync(InterfaceMonitorSnapshot snapshot, IReadOnlyDictionary<string, WifiLedgerSnapshot> wifi)
    {
        var frames = snapshot.Interfaces.Select(item => Frame($"interface:{item.Interface.Id}", HistoryLayer.Interface, windowEpoch,
            item.Usage.Status == TrafficSampleStatus.Measured ? item.Usage.SessionTotal : null,
            item.Usage.Status == TrafficSampleStatus.Measured && item.Usage.SessionTotal is { } total ? [new("interface", item.Interface.Name, total)] : [],
            item.Usage.Status == TrafficSampleStatus.Measured ? "" : item.Usage.Status.ToString(), item.Interface.Name)).ToList();
        foreach (var item in wifi)
        {
            var entries = item.Value.Networks.Select(network => new HistoryEntry(System.Text.Json.JsonSerializer.Serialize(network.Network), network.DisplayName, network.Volume)).ToList();
            if (item.Value.ObservedTotal is not null)
            {
                entries.Add(new("$boundary", "$boundary", item.Value.BoundaryUnallocated));
                entries.Add(new("$unknown", "$unknown", item.Value.Unidentified));
            }
            frames.Add(Frame($"wifi:{item.Key}", HistoryLayer.Wifi, windowEpoch, item.Value.ObservedTotal, entries,
                item.Value.UnidentifiedIntervals > 0 || item.Value.BoundaryIntervals > 0 ? "PartialAttribution" : "",
                snapshot.Interfaces.FirstOrDefault(row => row.Interface.Id == item.Key)?.Interface.Name));
        }
        return SaveAsync(frames);
    }

    public Task SaveProxyAsync(Guid epoch, ProxyUsageSnapshot snapshot)
    {
        var quality = snapshot.Gaps > 0 || snapshot.Resets > 0 || snapshot.CapacityReached ? "PartialCapture" : "";
        var entries = snapshot.ConnectionObserved is null ? [] : snapshot.Rows.Select(item => new HistoryEntry($"{item.Owner?.ProcessId}:{item.Owner?.StartTicks}:{item.Ingress}:{item.Route}",
            $"{item.Owner?.Name ?? "$unknown"} · {item.Ingress} · {item.Route}", item.Traffic)).ToList();
        if (snapshot.ConnectionObserved is not null) { entries.Add(new("$overflow", "$unknown", snapshot.Overflow)); }
        return SaveAsync([
            Frame("proxy-core", HistoryLayer.ProxyCore, epoch, snapshot.CoreObserved,
                snapshot.CoreObserved is { } total ? [new("core", "Clash / Mihomo", total)] : [], quality),
            Frame("proxy-connections", HistoryLayer.ProxyConnection, epoch, snapshot.ConnectionObserved, entries, quality)]);
    }

    public async Task RefreshStreamsAsync()
    {
        if (!initialized) { return; }
        try
        {
            var streams = await Task.Run(store.GetStreams);
            var selected = selectedStream?.Stream.Id;
            Streams.Clear(); foreach (var stream in streams) { Streams.Add(new(stream)); }
            selectedStream = Streams.FirstOrDefault(item => item.Stream.Id == selected) ?? Streams.FirstOrDefault();
            Notify(null);
        }
        catch (Exception) { reportStatus = L.T("无法读取账本列表。", "Cannot read ledger list."); Notify(null); }
    }

    public async Task QueryAsync()
    {
        if (!CanQuery || selectedStream is null) { return; }
        queryBusy = true; ClearReport(); reportStatus = L.T("正在查询报表…", "Querying report…"); Notify(null);
        try
        {
            await SaveAsync([]);
            if (fromDate is not { } from || untilDate is not { } until || until.Date < from.Date || (until.Date - from.Date).TotalDays > 366)
            { reportStatus = L.T("请选择有效日期范围，单次最多 367 天。", "Choose a valid date range of at most 367 days."); return; }
            var query = new HistoryQuery(selectedStream.Stream.Id, UsageReports.LocalBoundary(DateOnly.FromDateTime(from), SelectedTimeZone),
                UsageReports.LocalBoundary(DateOnly.FromDateTime(until).AddDays(1), SelectedTimeZone),
                selectedStream.Stream.Layer is HistoryLayer.Application or HistoryLayer.Ssh or HistoryLayer.ProxyConnection);
            var zone = SelectedTimeZone;
            var period = SelectedPeriod.Period;
            report = await Task.Run(() => UsageReports.Build(store.Read(query), query, period, zone));
            reportStatus = L.T("报表已更新，仅包含已保存的有效观察。", "Report updated. Only saved observations are included.");
        }
        catch (Exception) { reportStatus = L.T("报表查询失败；没有将缺失数据显示为零。", "Report query failed. Missing data has not been shown as zero."); }
        finally { queryBusy = false; Notify(null); }
    }

    public string ExportCsv()
    {
        if (report is null) { throw new InvalidOperationException("No report."); }
        var lines = new List<string> { "Ledger,Period,Entity,ReceivedBytes,SentBytes,Intervals,PartialIntervals,QualityNotices,TimeZone,FromUtc,UntilUtc" };
        foreach (var row in report.Rows)
        {
            lines.Add(string.Join(",", Csv(report.Stream), Csv(row.Period), Csv(DisplayLabel(row.Label)), row.Traffic.ReceivedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                row.Traffic.SentBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), row.Intervals.ToString(System.Globalization.CultureInfo.InvariantCulture),
                row.PartialIntervals.ToString(System.Globalization.CultureInfo.InvariantCulture), report.QualityEvents.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Csv(report.TimeZone), Csv(report.FromUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)), Csv(report.UntilUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture))));
        }
        return string.Join("\r\n", lines) + "\r\n";
    }

    public static string Csv(string value)
    {
        if (value.Length > 0 && "=+-@\t\r\n".Contains(value[0])) { value = "'" + value; }
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
    private void ClearReport() { report = null; reportStatus = L.T("选择账本与日期范围后查询。", "Select a ledger and date range to query."); Notify(null); }
    public static string LayerName(HistoryLayer layer) => layer switch
    {
        HistoryLayer.Interface => L.T("接口", "Interface"),
        HistoryLayer.Wifi => "Wi-Fi",
        HistoryLayer.Application => L.T("应用", "Applications"),
        HistoryLayer.Ssh => "SSH",
        HistoryLayer.ProxyCore => L.T("代理核心", "Proxy core"),
        _ => L.T("代理连接", "Proxy connections"),
    };
}
