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
    IInterfaceCounterReader reader, IWifiConnectionReader wifiReader) : ObservableState
{
    private readonly InterfaceMonitor monitor = new();
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
        var generation = continuityGeneration;
        try
        {
            var raw = await Task.Run(reader.Read);
            if (generation == continuityGeneration)
            {
                Apply(monitor.Apply(raw));
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or OverflowException)
        {
            Apply(monitor.Apply(new(DateTimeOffset.UtcNow, TimeSpan.Zero, [], 13)));
            collectionStatus = "采集异常；当前速率不可用，恢复后重新建立基线。";
            Notify(null);
        }
        finally
        {
            refreshing = false;
        }
    }

    public void BreakContinuity()
    {
        continuityGeneration++;
        monitor.BreakContinuity();
        ClearWifi();
        Apply(monitor.Apply(new(DateTimeOffset.UtcNow, TimeSpan.Zero, [], 21)));
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
            (!selected.State.IsPresent || !selected.State.Interface.IsUp || selected.State.Interface.ContinuityKey != oldKey))
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
