using FlowLedger.Application.Proxy;
using FlowLedger.Domain.Accounting;
using FlowLedger.Domain.Proxy;

namespace FlowLedger.Desktop.ViewModels;

public sealed class ProxyMonitorViewModel(Func<string, string, int, IProxyTrafficSource> factory) : ObservableState
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IProxyTrafficSource? source;
    private ProxyUsageLedger ledger = new();
    private bool changing;
    private bool refreshing;
    private bool closed;
    private CancellationTokenSource? verification;
    private string endpoint = "http://127.0.0.1:9090";
    private string proxyPort = "7890";
    private string status = "尚未启用代理账本。填写本机控制器地址和密钥；代理入口端口不等于控制器端口。";
    private string version = "";

    public string Endpoint { get => endpoint; set { endpoint = value; Notify(nameof(Endpoint)); } }
    public string ProxyPort { get => proxyPort; set { proxyPort = value; Notify(nameof(ProxyPort)); } }
    public bool CanStart => !closed && !changing && source is null;
    public bool CanStop => !changing && source is not null;
    public bool CanVerify => !closed && !changing && source is not null;
    public string Status => status;
    public string Version => version.Length == 0 ? "等待核心版本" : $"核心报告版本：{version}";
    public string CoreTotal => $"核心逻辑增量 · {Format(ledger.Snapshot().CoreObserved)}";
    public string ConnectionTotal => $"已连续观察的连接增量 · {Format(ledger.Snapshot().ConnectionObserved)}";
    public string Quality
    {
        get
        {
            var snapshot = ledger.Snapshot();
            return $"活动连接：{snapshot.ActiveConnections?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "不可用"}；断档 {snapshot.Gaps}；重置 {snapshot.Resets}；新基线 {snapshot.NewBaselines}；已消失连接 {snapshot.ClosedConnections}。" +
                (snapshot.CapacityReached ? "已达到记录上限，溢出单列未知。" : "") +
                "活动列表会漏掉轮询之间结束的连接和关闭尾部；未确认原应用的流量保留未知。核心总量与连接明细的采样时刻可能不同，分别展示，不相加。";
        }
    }

    public IReadOnlyList<ProxyTrafficRow> Rows
    {
        get
        {
            var snapshot = ledger.Snapshot();
            var rows = snapshot.Rows.OrderByDescending(row => (decimal)row.Traffic.ReceivedBytes + row.Traffic.SentBytes)
                .Select(row => new ProxyTrafficRow(row.Owner is { } owner ? $"{owner.Name} · PID {owner.ProcessId}" : "未确认原应用",
                    row.Ingress switch { ProxyIngress.ExplicitProxy => "HTTP / SOCKS 入站", ProxyIngress.Tun => "TUN 入站", _ => "入站未确认" },
                    row.Route switch { ProxyRoute.CoreDirect => "核心报告 DIRECT", ProxyRoute.CoreProxy => "核心报告代理链", _ => "路径未确认" },
                    Bytes(row.Traffic.ReceivedBytes), Bytes(row.Traffic.SentBytes))).ToList();
            if (snapshot.CapacityReached) { rows.Add(new("容量溢出 · 未归属", "未知", "未知", Bytes(snapshot.Overflow.ReceivedBytes), Bytes(snapshot.Overflow.SentBytes))); }
            return rows;
        }
    }

    public async Task StartAsync(string secret)
    {
        if (!CanStart) { return; }
        changing = true; Notify(null);
        await gate.WaitAsync();
        try
        {
            if (closed) { return; }
            if (!int.TryParse(proxyPort, out var port) || port is < 1 or > 65535) { status = "代理入口端口无效。"; return; }
            ledger = new();
            source = factory(endpoint, secret, port);
            await ReadCoreAsync();
        }
        catch (ArgumentException) { status = "设置无效：控制器必须使用本机回环 IP，密钥不能包含控制字符。"; await ReleaseAsync(); }
        catch (Exception) { status = "代理观察启动失败，请检查本机控制器。"; await ReleaseAsync(); }
        finally { changing = false; gate.Release(); Notify(null); }
    }

    public async Task RefreshAsync()
    {
        if (closed || changing || refreshing || source is null) { return; }
        refreshing = true;
        await gate.WaitAsync();
        try { if (!closed && source is not null) { await ReadCoreAsync(); } }
        catch (Exception) { ledger.BreakContinuity(); status = "代理读取失败，记录已有断档。"; }
        finally { refreshing = false; gate.Release(); Notify(null); }
    }

    public async Task StopAsync(bool powerBoundary = false)
    {
        if (verification is { } pending) { try { await pending.CancelAsync(); } catch (ObjectDisposedException) { } }
        changing = true; Notify(null);
        await gate.WaitAsync();
        try
        {
            await ReleaseAsync();
            ledger.BreakContinuity();
            status = powerBoundary ? "电源状态变化，代理观察已停止；需要手动重新启用。" : "代理观察已停止，记录保留；再次开始会清空本页记录。";
        }
        finally { changing = false; gate.Release(); Notify(null); }
    }

    public async Task ShutdownAsync() { closed = true; await StopAsync(); }

    public async Task DiscoverAsync(Func<int, Task<string?>> discover)
    {
        if (!CanStart) { return; }
        changing = true; Notify(null);
        await gate.WaitAsync();
        try
        {
            if (!int.TryParse(proxyPort, out var port) || port is < 1 or > 65535) { status = "请填写有效的代理入口端口。"; return; }
            var candidate = await discover(port);
            if (closed) { return; }
            if (candidate is null) { status = "没有找到唯一的控制器候选；请从客户端手动确认地址。未读取配置或密钥。"; }
            else { Endpoint = candidate; status = "已找到本机核心的控制器候选。请在本地输入密钥后启用；未读取配置或密钥。"; }
        }
        catch (Exception) { status = "本机控制器查找未完成；可以手动填写地址。"; }
        finally { changing = false; gate.Release(); Notify(null); }
    }

    public async Task<string> VerifyAsync(Func<IProxyTrafficSource, int, CancellationToken, Task<string>> verify)
    {
        if (!CanVerify) { return "请先连接本机控制器。"; }
        changing = true; Notify(null);
        await gate.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        verification = cancellation;
        try { return source is null ? "控制器未连接。" : await verify(source, int.Parse(proxyPort, System.Globalization.CultureInfo.InvariantCulture), cancellation.Token); }
        catch (Exception) { return "本机验证未完成；没有修改代理配置。"; }
        finally { verification = null; changing = false; gate.Release(); Notify(null); }
    }

    private async Task ReadCoreAsync()
    {
        var result = await source!.ReadAsync();
        version = result.Version;
        if (result.State == ProxyReadState.Available && result.Observation is { } observation)
        {
            ledger.Observe(observation);
            status = "正在只读观察本机核心。每个连接只按核心 API 增量计一次；不叠加 ETW 原应用、核心进程或网卡字节。";
            return;
        }

        ledger.BreakContinuity();
        status = result.State switch
        {
            ProxyReadState.AccessDenied => "控制器认证失败。请停止后重新填写本地密钥，不要把密钥发送到聊天或提交到仓库。",
            ProxyReadState.InvalidResponse => "控制器返回格式不受支持或超出上限；没有使用旧数据继续记账。",
            ProxyReadState.CoreUnconfirmed => "无法确认控制器与代理入口同属一个稳定核心；暂不记账。请停止后重新查找控制器，或检查管理员权限。",
            _ => "控制器暂时不可用，已中断基线，恢复后重新观察。",
        };
        if (result.State is ProxyReadState.AccessDenied or ProxyReadState.InvalidResponse) { await ReleaseAsync(); }
    }

    private async Task ReleaseAsync()
    {
        var current = source; source = null;
        if (current is not null) { await current.DisposeAsync(); }
    }

    private static string Format(TrafficVolume? volume) => volume is { } measured ? $"下载 {Bytes(measured.ReceivedBytes)} / 上传 {Bytes(measured.SentBytes)}" : "等待有效区间";
    private static string Bytes(ulong value) => value >= 1048576 ? $"{value / 1048576d:F2} MiB" : value >= 1024 ? $"{value / 1024d:F2} KiB" : $"{value} B";
}

public sealed record ProxyTrafficRow(string Application, string Ingress, string Route, string Download, string Upload);
