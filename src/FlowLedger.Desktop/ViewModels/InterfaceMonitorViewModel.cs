using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FlowLedger.Application.Monitoring;
using FlowLedger.Domain.Accounting;

namespace FlowLedger.Desktop.ViewModels;

public abstract class ObservableState : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class InterfaceChoice(MonitoredInterface state) : ObservableState
{
    public MonitoredInterface State { get; private set; } = state;
    public string Id => State.Interface.Id;
    public string Label => $"{State.Interface.Name} · {KindName(State.Interface.Kind)} · {(State.IsPresent ? State.Interface.IsUp ? "在线" : "离线" : "不可用")}";

    public override string ToString() => Label;

    public void Update(MonitoredInterface value)
    {
        State = value;
        Notify(nameof(Label));
    }

    public static string KindName(InterfaceKind kind) => kind switch
    {
        InterfaceKind.Hardware => "物理候选",
        InterfaceKind.Tunnel => "隧道",
        InterfaceKind.Loopback => "回环",
        InterfaceKind.Filter => "过滤层",
        _ => "虚拟 / 其他",
    };
}

public sealed class InterfaceMonitorViewModel(
    IInterfaceCounterReader reader, IWifiConnectionReader wifiReader,
    Func<IWifiTrafficSource> wifiSourceFactory, IRouteEvidenceReader routeReader) : ObservableState
{
    private readonly InterfaceMonitor monitor = new();
    private readonly WifiLedgerCoordinator wifiLedger = new();
    private readonly SemaphoreSlim samplingGate = new(1, 1);
    private IWifiTrafficSource? wifiSource;
    private IReadOnlyDictionary<string, WifiLedgerSnapshot> wifiLedgers = new Dictionary<string, WifiLedgerSnapshot>();
    private IReadOnlyDictionary<string, WifiTrafficObservation> wifiObservations = new Dictionary<string, WifiTrafficObservation>();
    private bool changingWifiMode;
    private bool shuttingDown;
    private string wifiModeStatus = "尚未启用 Wi-Fi 分账；启用时 Windows 可能提示位置权限。";
    private string routeInfo = "尚未检查出口关系。此检查不修改路由。";
    private bool routeBusy;
    private readonly Dictionary<string, InterfaceChoice> choices = new(StringComparer.Ordinal);
    private InterfaceChoice? selectedInterface;
    private bool refreshing;
    private bool wifiBusy;
    private long wifiGeneration;
    private long continuityGeneration;
    private string wifiInfo = "尚未查询；Wi-Fi 名称不参与本次用量分账。";
    private string collectionStatus = "正在读取接口计数…";
    private string inventorySummary = "等待接口列表";

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
            routeInfo = "所选接口已变更，请重新检查出口关系。";
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
        ? "选择物理 Wi-Fi 接口并启用分账后，等待建立基线。"
        : $"分账启用期间的有效区间：下载 {Volume(ledger.ObservedTotal?.ReceivedBytes)} · 上传 {Volume(ledger.ObservedTotal?.SentBytes)}\n边界区间 {ledger.BoundaryIntervals} 次 · 未识别区间 {ledger.UnidentifiedIntervals} 次；计数断档没有可分配字节。";

    public IReadOnlyList<WifiNetworkRow> WifiRows
    {
        get
        {
            if (selectedInterface is null || !wifiLedgers.TryGetValue(selectedInterface.Id, out var ledger) || ledger.ObservedTotal is null)
            {
                return [];
            }

            return ledger.Networks.Select((network, index) => new WifiNetworkRow(
                $"候选 {index + 1} · {network.DisplayName}", Volume(network.Volume.ReceivedBytes), Volume(network.Volume.SentBytes)))
                .Concat([
                    new WifiNetworkRow("边界未分配", Volume(ledger.BoundaryUnallocated.ReceivedBytes), Volume(ledger.BoundaryUnallocated.SentBytes)),
                    new WifiNetworkRow("未识别网络", Volume(ledger.Unidentified.ReceivedBytes), Volume(ledger.Unidentified.SentBytes)),
                ]).ToArray();
        }
    }
    public bool CanReadWifi => !wifiBusy && selectedInterface is { State.IsPresent: true, State.Interface.IsUp: true, State.Interface.NativeType: 71 }
        && selectedInterface.State.Interface.Kind == InterfaceKind.Hardware
        && Guid.TryParse(selectedInterface.Id, out _);

    public string SelectionDetails => selectedInterface is null ? "没有可供展示的接口。" :
        $"{InterfaceChoice.KindName(selectedInterface.State.Interface.Kind)} · 接口类型 {selectedInterface.State.Interface.NativeType} · {(selectedInterface.State.Interface.ConnectorPresent ? "有连接器标志" : "无连接器标志")}\n只展示所选接口，不与代理、回环或其他网卡相加。硬件标志不证明这是当前互联网出口。";

    public string QualityStatus => selectedInterface is null ? "尚未测量" :
        $"{StatusName(selectedInterface.State.Usage.Status)} · 断档 {selectedInterface.State.Usage.Discontinuities} 次\n用量仅含本次进程运行期间的有效采样区间；断档期间未计入。关闭软件后停止记录，不保存历史。";

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
                        ? "Wi-Fi 分账运行中 · 连接变化通知正常 · 仅保留本次运行记录"
                        : $"Wi-Fi 通知受限（错误 {sample.NotificationError}）；有效字节计入未识别网络。";
                    if (selectedInterface is not null && wifiObservations.TryGetValue(selectedInterface.Id, out var current) &&
                        (oldSequence != current.Endpoint.ChangeSequence || current.State != WifiConnectionState.Available))
                    {
                        ClearWifi();
                    }
                }

                Apply(monitored);
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or OverflowException)
        {
            wifiLedger.BreakContinuity();
            Apply(monitor.Apply(new(DateTimeOffset.UtcNow, TimeSpan.Zero, [], 13)));
            collectionStatus = "采集异常；当前速率不可用，恢复后重新建立基线。";
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
                wifiModeStatus = "Wi-Fi 分账已启用，等待首次连接观察；系统可能提示位置权限。";
            }
            else if (!enabled && wifiSource is { } source)
            {
                wifiSource = null;
                await Task.Run(source.Dispose);
                wifiModeStatus = "Wi-Fi 分账已暂停，保留本次已有记录；接口采样继续运行。";
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
            routeInfo = routes.ErrorCode != 0 ? $"路由信息不可用（错误 {routes.ErrorCode}）。"
                : $"全机存在默认路由的接口：{routes.Interfaces.Count(item => item.Ipv4DefaultRoutes + item.Ipv6DefaultRoutes > 0)} 个\n"
                  + (row is null ? "所选接口没有可见路由条目。" : $"所选接口：IPv4 默认路由 {row.Ipv4DefaultRoutes} 条 · IPv6 默认路由 {row.Ipv6DefaultRoutes} 条 · 其他前缀路由 {row.OtherPrefixRoutes} 条")
                  + "\n这是本次路由表证据，不能据此判定每个应用的实际出口；VPN 分流也可能使用更具体的前缀。";
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
        wifiInfo = "正在查询当前 Wi-Fi；系统可能提示位置权限。";
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
                WifiConnectionState.Available => $"查询结果：{result.DisplayName}\n查询于 {DateTime.Now:HH:mm:ss}，仅供查看；不是持续网络身份，也不用于分账。",
                WifiConnectionState.AccessDenied => "系统拒绝读取 Wi-Fi 名称；继续按网卡统计，不沿用旧名称。",
                WifiConnectionState.Disconnected => "Wi-Fi 当前未连接；继续保留接口级记录。",
                _ => $"Wi-Fi 信息暂不可用（错误 {result.ErrorCode}）；接口计数仍独立运行。",
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
        inventorySummary = $"当前 {present.Length} 个条目：物理候选 {present.Count(item => item.Interface.Kind == InterfaceKind.Hardware)} · 过滤层 {present.Count(item => item.Interface.Kind == InterfaceKind.Filter)} · 其他 {present.Count(item => item.Interface.Kind is not InterfaceKind.Hardware and not InterfaceKind.Filter)}";
        collectionStatus = snapshot.ErrorCode == 0
            ? $"接口采集运行中 · 每秒采样 · 更新于 {snapshot.CapturedAt.ToLocalTime():HH:mm:ss}"
            : $"接口读取失败（错误 {snapshot.ErrorCode}）· 当前速率不可用；等待恢复";
        Notify(null);
    }

    private void ClearWifi()
    {
        wifiGeneration++;
        wifiInfo = "尚未查询；Wi-Fi 名称不参与本次用量分账。";
    }

    private static string Volume(ulong? bytes) => bytes is null ? "尚未测量" : FormatBytes(bytes.Value);
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
        TrafficSampleStatus.Measured => "有效测量",
        TrafficSampleStatus.Baseline => "已建立基线，等待下一次采样",
        TrafficSampleStatus.Offline => "接口离线，当前无测量",
        TrafficSampleStatus.Unavailable => "接口不可用，当前无测量",
        TrafficSampleStatus.CounterReset => "计数回退，已重建基线",
        TrafficSampleStatus.IdentityChanged => "连接或接口身份变化，已重建基线",
        TrafficSampleStatus.ClockChanged => "采样时钟异常，已重建基线",
        TrafficSampleStatus.EpochChanged => "计数纪元变化，已重建基线",
        _ => "采样间隔过长，已重建基线",
    };
}

public sealed record WifiNetworkRow(string Name, string Download, string Upload);
