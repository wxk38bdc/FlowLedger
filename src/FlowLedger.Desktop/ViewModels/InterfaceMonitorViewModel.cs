using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FlowLedger.Application.Monitoring;
using FlowLedger.Desktop.Localization;
using FlowLedger.Domain.Accounting;

namespace FlowLedger.Desktop.ViewModels;

public abstract class ObservableState : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    public void RefreshText() => Notify(null);
}

public sealed class InterfaceChoice(MonitoredInterface state) : ObservableState
{
    public MonitoredInterface State { get; private set; } = state;
    public string Id => State.Interface.Id;
    public string Label => $"{State.Interface.Name} · {KindName(State.Interface.Kind)} · {(State.IsPresent ? State.Interface.IsUp ? L.T("在线", "Online") : L.T("离线", "Offline") : L.T("不可用", "Unavailable"))}";

    public override string ToString() => Label;

    public void Update(MonitoredInterface value)
    {
        State = value;
        Notify(nameof(Label));
    }

    public static string KindName(InterfaceKind kind) => kind switch
    {
        InterfaceKind.Hardware => L.T("物理候选", "Hardware candidate"),
        InterfaceKind.Tunnel => L.T("隧道", "Tunnel"),
        InterfaceKind.Loopback => L.T("回环", "Loopback"),
        InterfaceKind.Filter => L.T("过滤层", "Filter"),
        _ => L.T("虚拟 / 其他", "Virtual / other"),
    };
}

public sealed class InterfaceMonitorViewModel(
    IInterfaceCounterReader reader, IWifiConnectionReader wifiReader,
    Func<IWifiTrafficSource> wifiSourceFactory, IRouteEvidenceReader routeReader,
    Func<InterfaceMonitorSnapshot, IReadOnlyDictionary<string, WifiLedgerSnapshot>, Task>? persist = null) : ObservableState
{
    private readonly InterfaceMonitor monitor = new();
    private readonly WifiLedgerCoordinator wifiLedger = new();
    private readonly SemaphoreSlim samplingGate = new(1, 1);
    private IWifiTrafficSource? wifiSource;
    private IReadOnlyDictionary<string, WifiLedgerSnapshot> wifiLedgers = new Dictionary<string, WifiLedgerSnapshot>();
    private IReadOnlyDictionary<string, WifiTrafficObservation> wifiObservations = new Dictionary<string, WifiTrafficObservation>();
    private bool changingWifiMode;
    private bool shuttingDown;
    private UiText wifiModeStatus = L.T("尚未启用 Wi-Fi 分账；启用时 Windows 可能提示位置权限。", "Wi-Fi allocation is off. Windows may request location access when enabled.");
    private UiText routeInfo = L.T("尚未检查出口关系。此检查不修改路由。", "Route evidence has not been read. This does not change routes.");
    private bool routeBusy;
    private readonly Dictionary<string, InterfaceChoice> choices = new(StringComparer.Ordinal);
    private InterfaceChoice? selectedInterface;
    private bool refreshing;
    private bool wifiBusy;
    private long wifiGeneration;
    private long continuityGeneration;
    private UiText wifiInfo = L.T("尚未查询；Wi-Fi 名称不参与本次用量分账。", "Not queried. This name lookup is separate from allocation.");
    private UiText collectionStatus = L.T("正在读取接口计数…", "Reading interface counters…");
    private UiText inventorySummary = L.T("等待接口列表", "Waiting for interfaces");

    public ObservableCollection<InterfaceChoice> Interfaces { get; } = [];

    public InterfaceChoice? SelectedInterface
    {
        get => selectedInterface;
        set
        {
            if (ReferenceEquals(selectedInterface, value))
            {
                return;
            }

            selectedInterface = value;
            routeInfo = L.T("所选接口已变更，请重新检查出口关系。", "Interface changed. Read route evidence again.");
            ClearWifi();
            Notify(null);
        }
    }

    public string CollectionStatus => collectionStatus;
    public string InventorySummary => inventorySummary;
    public string DownloadRate => Rate(selectedInterface?.State.Usage.ReceivedBytesPerSecond);
    public string UploadRate => Rate(selectedInterface?.State.Usage.SentBytesPerSecond);
    public string DownloadTotal => Volume(selectedInterface?.State.Usage.SessionTotal?.ReceivedBytes);
    public string UploadTotal => Volume(selectedInterface?.State.Usage.SessionTotal?.SentBytes);
    public string WifiInfo => wifiInfo;
    public string WifiModeStatus => wifiModeStatus;
    public bool CanEnableWifi => !changingWifiMode && wifiSource is null && !shuttingDown;
    public bool CanDisableWifi => !changingWifiMode && wifiSource is not null;
    public string RouteInfo => routeInfo;
    public bool CanReadRoutes => !routeBusy;

    public string WifiAllocationSummary => selectedInterface is null || !wifiLedgers.TryGetValue(selectedInterface.Id, out var ledger)
        ? L.T("选择物理 Wi-Fi 接口并启用分账后，等待建立基线。", "Select a hardware Wi-Fi interface and enable allocation to establish a baseline.")
        : L.T($"有效区间：下载 {Volume(ledger.ObservedTotal?.ReceivedBytes)} · 上传 {Volume(ledger.ObservedTotal?.SentBytes)} · 边界 {ledger.BoundaryIntervals} · 未识别 {ledger.UnidentifiedIntervals}",
            $"Valid intervals: received {Volume(ledger.ObservedTotal?.ReceivedBytes)} · sent {Volume(ledger.ObservedTotal?.SentBytes)} · Boundaries {ledger.BoundaryIntervals} · Unknown {ledger.UnidentifiedIntervals}");

    public IReadOnlyList<WifiNetworkRow> WifiRows
    {
        get
        {
            if (selectedInterface is null || !wifiLedgers.TryGetValue(selectedInterface.Id, out var ledger) || ledger.ObservedTotal is null)
            {
                return [];
            }

            return ledger.Networks.Select((network, index) => new WifiNetworkRow(
                L.T($"候选 {index + 1} · {network.DisplayName}", $"Candidate {index + 1} · {network.DisplayName}"), Volume(network.Volume.ReceivedBytes), Volume(network.Volume.SentBytes)))
                .Concat([
                    new WifiNetworkRow(L.T("边界未分配", "Unallocated boundary"), Volume(ledger.BoundaryUnallocated.ReceivedBytes), Volume(ledger.BoundaryUnallocated.SentBytes)),
                    new WifiNetworkRow(L.T("未识别网络", "Unknown network"), Volume(ledger.Unidentified.ReceivedBytes), Volume(ledger.Unidentified.SentBytes)),
                ]).ToArray();
        }
    }
    public bool CanReadWifi => !wifiBusy && selectedInterface is { State.IsPresent: true, State.Interface.IsUp: true, State.Interface.NativeType: 71 }
        && selectedInterface.State.Interface.Kind == InterfaceKind.Hardware
        && Guid.TryParse(selectedInterface.Id, out _);

    public string SelectionDetails => selectedInterface is null ? L.T("没有可供展示的接口。", "No interface available.") :
        L.T($"{InterfaceChoice.KindName(selectedInterface.State.Interface.Kind)} · 接口类型 {selectedInterface.State.Interface.NativeType} · 连接器标志 {selectedInterface.State.Interface.ConnectorPresent}。各接口独立计量，硬件标志不证明互联网出口。",
            $"{InterfaceChoice.KindName(selectedInterface.State.Interface.Kind)} · Native type {selectedInterface.State.Interface.NativeType} · Connector flag {selectedInterface.State.Interface.ConnectorPresent}. Interfaces are measured separately; hardware flags do not prove an Internet exit.");

    public string QualityStatus => selectedInterface is null ? L.T("尚未测量", "Not measured") :
        L.T($"{StatusName(selectedInterface.State.Usage.Status)} · 断档 {selectedInterface.State.Usage.Discontinuities} 次。关闭软件后停止采集；已保存的区间可在报表查看。",
            $"{StatusName(selectedInterface.State.Usage.Status)} · Gaps {selectedInterface.State.Usage.Discontinuities}. Capture stops when the app closes. Saved intervals remain in Reports.");

    public async Task RefreshAsync()
    {
        if (refreshing)
        {
            return;
        }

        refreshing = true;
        await samplingGate.WaitAsync();
        try
        {
            if (shuttingDown)
            {
                return;
            }

            var generation = continuityGeneration;
            var sample = wifiSource is { } source ? await Task.Run(source.Read) : null;
            var raw = sample?.Counters ?? await Task.Run(reader.Read);
            if (generation == continuityGeneration)
            {
                var monitored = monitor.Apply(raw);
                if (sample is not null)
                {
                    var oldSequence = selectedInterface is not null && wifiObservations.TryGetValue(selectedInterface.Id, out var old)
                        ? old.Endpoint.ChangeSequence : (long?)null;
                    wifiObservations = sample.Observations;
                    wifiLedgers = wifiLedger.Apply(sample, monitored);
                    wifiModeStatus = sample.Coverage == WifiNotificationCoverage.Full
                        ? L.T("Wi-Fi 分账运行中 · 连接变化通知正常 · 仅保留本次运行记录", "Wi-Fi allocation active \u00b7 Connection notifications available")
                        : L.T($"Wi-Fi 通知受限（错误 {sample.NotificationError}）；有效字节计入未识别网络。", $"Wi-Fi notifications limited (error {sample.NotificationError}). Valid bytes remain unallocated.");
                    if (selectedInterface is not null && wifiObservations.TryGetValue(selectedInterface.Id, out var current) &&
                        (oldSequence != current.Endpoint.ChangeSequence || current.State != WifiConnectionState.Available))
                    {
                        ClearWifi();
                    }
                }

                Apply(monitored);
                if (persist is not null) { await persist(monitored, sample is null ? new Dictionary<string, WifiLedgerSnapshot>() : wifiLedgers); }
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or OverflowException)
        {
            wifiLedger.BreakContinuity();
            Apply(monitor.Apply(new(DateTimeOffset.UtcNow, TimeSpan.Zero, [], 13)));
            collectionStatus = L.T("采集异常；当前速率不可用，恢复后重新建立基线。", "Capture error. Rates are unavailable; a new baseline will be established after recovery.");
            Notify(null);
        }
        finally
        {
            samplingGate.Release();
            refreshing = false;
        }
    }

    public void BreakContinuity()
    {
        continuityGeneration++;
        monitor.BreakContinuity();
        wifiLedger.BreakContinuity();
        ClearWifi();
        Apply(monitor.Apply(new(DateTimeOffset.UtcNow, TimeSpan.Zero, [], 21)));
    }

    public async Task SetWifiEnabledAsync(bool enabled)
    {
        if (changingWifiMode || shuttingDown)
        {
            return;
        }

        changingWifiMode = true;
        Notify(null);
        await samplingGate.WaitAsync();
        try
        {
            if (enabled && wifiSource is null)
            {
                wifiSource = await Task.Run(wifiSourceFactory);
                wifiModeStatus = L.T("Wi-Fi 分账已启用，等待首次连接观察；系统可能提示位置权限。", "Wi-Fi allocation enabled. Waiting for connection evidence; Windows may request location access.");
            }
            else if (!enabled && wifiSource is { } source)
            {
                wifiSource = null;
                await Task.Run(source.Dispose);
                wifiModeStatus = L.T("Wi-Fi 分账已暂停，保留本次已有记录；接口采样继续运行。", "Wi-Fi allocation paused. Existing records are retained; interface sampling continues.");
            }

            wifiLedger.BreakContinuity();
        }
        finally
        {
            samplingGate.Release();
            changingWifiMode = false;
            Notify(null);
        }
    }

    public async Task ShutdownAsync()
    {
        shuttingDown = true;
        await samplingGate.WaitAsync();
        try
        {
            if (wifiSource is { } source)
            {
                wifiSource = null;
                await Task.Run(source.Dispose);
            }
        }
        finally
        {
            samplingGate.Release();
        }
    }

    public async Task ReadRoutesAsync()
    {
        if (routeBusy)
        {
            return;
        }

        routeBusy = true;
        Notify(null);
        try
        {
            var routes = await Task.Run(routeReader.Read);
            var row = routes.Interfaces.FirstOrDefault(item => item.Luid == selectedInterface?.State.Interface.Luid);
            routeInfo = routes.ErrorCode != 0 ? L.T($"路由信息不可用（错误 {routes.ErrorCode}）。", $"Route evidence unavailable (error {routes.ErrorCode}).")
                : L.T($"有默认路由的接口 {routes.Interfaces.Count(item => item.Ipv4DefaultRoutes + item.Ipv6DefaultRoutes > 0)} 个\n", $"Interfaces with default routes: {routes.Interfaces.Count(item => item.Ipv4DefaultRoutes + item.Ipv6DefaultRoutes > 0)}\n")
                  + (row is null ? L.T("所选接口没有可见路由条目。", "No visible routes for this interface.") : L.T($"IPv4 默认 {row.Ipv4DefaultRoutes} · IPv6 默认 {row.Ipv6DefaultRoutes} · 其他前缀 {row.OtherPrefixRoutes}", $"IPv4 default {row.Ipv4DefaultRoutes} · IPv6 default {row.Ipv6DefaultRoutes} · Other prefixes {row.OtherPrefixRoutes}"))
                  + L.T("\n路由条目不证明各应用的实际出口。", "\nRoute entries do not prove each application's exit path.");
        }
        finally
        {
            routeBusy = false;
            Notify(null);
        }
    }

    public async Task ReadWifiAsync()
    {
        if (!CanReadWifi || selectedInterface is null)
        {
            return;
        }

        var id = Guid.Parse(selectedInterface.Id);
        var generation = ++wifiGeneration;
        wifiBusy = true;
        wifiInfo = L.T("正在查询当前 Wi-Fi；系统可能提示位置权限。", "Reading current Wi-Fi. Windows may request location access.");
        Notify(null);
        try
        {
            var result = await Task.Run(() => wifiReader.Read(id));
            if (generation != wifiGeneration)
            {
                return;
            }

            wifiInfo = result.State switch
            {
                WifiConnectionState.Available => L.T($"查询结果：{result.DisplayName}\n{DateTime.Now:HH:mm:ss} · 本次名称查询不用于分账。", $"Network: {result.DisplayName}\n{DateTime.Now:HH:mm:ss} · This lookup is not used for allocation."),
                WifiConnectionState.AccessDenied => L.T("系统拒绝读取 Wi-Fi 名称；继续按网卡统计，不沿用旧名称。", "Wi-Fi name access denied. Interface counters continue; old names are not reused."),
                WifiConnectionState.Disconnected => L.T("Wi-Fi 当前未连接；继续保留接口级记录。", "Wi-Fi is disconnected. Existing interface records are retained."),
                _ => L.T($"Wi-Fi 信息暂不可用（错误 {result.ErrorCode}）；接口计数仍独立运行。", $"Wi-Fi unavailable (error {result.ErrorCode}). Interface counters continue independently."),
            };
        }
        finally
        {
            wifiBusy = false;
            Notify(null);
        }
    }

    private void Apply(InterfaceMonitorSnapshot snapshot)
    {
        var oldKey = selectedInterface?.State.Interface.ContinuityKey;
        var oldContext = selectedInterface?.State.Interface.NetworkContext;
        foreach (var item in snapshot.Interfaces.OrderBy(item => item.Interface.Kind).ThenByDescending(item => item.Interface.IsUp))
        {
            if (!choices.TryGetValue(item.Interface.Id, out var choice))
            {
                choice = new(item);
                choices.Add(choice.Id, choice);
                Interfaces.Add(choice);
            }
            else
            {
                choice.Update(item);
            }
        }

        if (selectedInterface is null)
        {
            SelectedInterface = Interfaces.FirstOrDefault(item => item.State is { IsPresent: true, Interface.Kind: InterfaceKind.Hardware, Interface.IsUp: true })
                ?? Interfaces.FirstOrDefault(item => item.State.IsPresent && item.State.Interface.Kind != InterfaceKind.Filter);
        }

        if (selectedInterface is { } selected &&
            (!selected.State.IsPresent || !selected.State.Interface.IsUp || selected.State.Interface.ContinuityKey != oldKey || selected.State.Interface.NetworkContext != oldContext))
        {
            ClearWifi();
        }

        var present = snapshot.Interfaces.Where(item => item.IsPresent).ToArray();
        inventorySummary = L.T($"共 {present.Length} 个条目 · 物理候选 {present.Count(item => item.Interface.Kind == InterfaceKind.Hardware)} · 过滤 {present.Count(item => item.Interface.Kind == InterfaceKind.Filter)}", $"{present.Length} entries · Hardware candidates {present.Count(item => item.Interface.Kind == InterfaceKind.Hardware)} · Filters {present.Count(item => item.Interface.Kind == InterfaceKind.Filter)}");
        collectionStatus = snapshot.ErrorCode == 0
            ? L.T($"接口采集运行中 · 更新于 {snapshot.CapturedAt.ToLocalTime():HH:mm:ss}", $"Interface capture active · Updated {snapshot.CapturedAt.ToLocalTime():HH:mm:ss}")
            : L.T($"接口读取失败（错误 {snapshot.ErrorCode}）· 等待恢复", $"Interface read failed (error {snapshot.ErrorCode}) · Waiting for recovery");
        Notify(null);
    }

    private void ClearWifi()
    {
        wifiGeneration++;
        wifiInfo = L.T("尚未查询；Wi-Fi 名称不参与本次用量分账。", "Not queried. This name lookup is separate from allocation.");
    }

    private static string Volume(ulong? bytes) => bytes is null ? L.T("尚未测量", "Not measured") : FormatBytes(bytes.Value);
    private static string Rate(double? bytes) => bytes is null ? "—" : $"{FormatBytes(bytes.Value)}/s";
    private static string FormatBytes(double bytes) => bytes switch
    {
        >= 1_099_511_627_776 => $"{bytes / 1_099_511_627_776:F2} TiB",
        >= 1_073_741_824 => $"{bytes / 1_073_741_824:F2} GiB",
        >= 1_048_576 => $"{bytes / 1_048_576:F2} MiB",
        >= 1024 => $"{bytes / 1024:F2} KiB",
        _ => $"{bytes:F0} B",
    };

    private static string StatusName(TrafficSampleStatus status) => status switch
    {
        TrafficSampleStatus.Measured => L.T("有效测量", "Measured"),
        TrafficSampleStatus.Baseline => L.T("已建立基线，等待下一次采样", "Baseline established; waiting for next sample"),
        TrafficSampleStatus.Offline => L.T("接口离线，当前无测量", "Interface offline; no current measurement"),
        TrafficSampleStatus.Unavailable => L.T("接口不可用，当前无测量", "Interface unavailable; no current measurement"),
        TrafficSampleStatus.CounterReset => L.T("计数回退，已重建基线", "Counter reset; baseline renewed"),
        TrafficSampleStatus.IdentityChanged => L.T("连接或接口身份变化，已重建基线", "Identity changed; baseline renewed"),
        TrafficSampleStatus.ClockChanged => L.T("采样时钟异常，已重建基线", "Clock changed; baseline renewed"),
        TrafficSampleStatus.EpochChanged => L.T("计数纪元变化，已重建基线", "Counter epoch changed; baseline renewed"),
        _ => L.T("采样间隔过长，已重建基线", "Sampling gap; baseline renewed"),
    };
}

public sealed record WifiNetworkRow(string Name, string Download, string Upload);
