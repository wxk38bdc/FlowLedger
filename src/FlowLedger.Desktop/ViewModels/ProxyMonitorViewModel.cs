using FlowLedger.Application.Proxy;
using FlowLedger.Desktop.Localization;
using FlowLedger.Domain.Accounting;
using FlowLedger.Domain.Proxy;

namespace FlowLedger.Desktop.ViewModels;

public sealed class ProxyMonitorViewModel(Func<string, string, int, IProxyTrafficSource> factory,
    Func<Guid, ProxyUsageSnapshot, Task>? persist = null) : ObservableState
{
    private Guid historyEpoch = Guid.NewGuid();
    private readonly SemaphoreSlim gate = new(1, 1);
    private IProxyTrafficSource? source;
    private ProxyUsageLedger ledger = new();
    private bool changing;
    private bool refreshing;
    private bool closed;
    private CancellationTokenSource? verification;
    private string endpoint = "http://127.0.0.1:9090";
    private string proxyPort = "7890";
    private UiText status = L.T("尚未启用代理账本。填写本机控制器地址和密钥；代理入口端口不等于控制器端口。", "Proxy observation is off. Enter the controller address and local token; the controller and proxy ports are different.");
    private string version = "";

    public string Endpoint { get => endpoint; set { endpoint = value; Notify(nameof(Endpoint)); } }
    public string ProxyPort { get => proxyPort; set { proxyPort = value; Notify(nameof(ProxyPort)); } }
    public bool CanStart => !closed && !changing && source is null;
    public bool CanStop => !changing && source is not null;
    public bool CanVerify => !closed && !changing && source is not null;
    public string Status => status;
    public string Version => version.Length == 0 ? L.T("等待核心版本", "Waiting for core version") : L.T($"核心报告版本：{version}", $"Core version: {version}");
    public string CoreTotal => L.T($"核心逻辑增量 · {Format(ledger.Snapshot().CoreObserved)}", $"Core logical delta · {Format(ledger.Snapshot().CoreObserved)}");
    public string ConnectionTotal => L.T($"连接观察增量 · {Format(ledger.Snapshot().ConnectionObserved)}", $"Observed connections · {Format(ledger.Snapshot().ConnectionObserved)}");
    public string Quality
    {
        get
        {
            var snapshot = ledger.Snapshot();
            return L.T($"活动 {snapshot.ActiveConnections?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—"} · 断档 {snapshot.Gaps} · 重置 {snapshot.Resets} · 新基线 {snapshot.NewBaselines} · 已消失 {snapshot.ClosedConnections}",
                $"Active {snapshot.ActiveConnections?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—"} · Gaps {snapshot.Gaps} · Resets {snapshot.Resets} · Baselines {snapshot.NewBaselines} · Disappeared {snapshot.ClosedConnections}") +
                (snapshot.CapacityReached ? L.T(" · 容量溢出保留未知。", " · Capacity overflow remains unknown.") : "");
        }
    }

    public IReadOnlyList<ProxyTrafficRow> Rows
    {
        get
        {
            var snapshot = ledger.Snapshot();
            var rows = snapshot.Rows.OrderByDescending(row => (decimal)row.Traffic.ReceivedBytes + row.Traffic.SentBytes)
                .Select(row => new ProxyTrafficRow(row.Owner is { } owner ? $"{owner.Name} · PID {owner.ProcessId}" : L.T("未确认原应用", "Unknown original app"),
                    row.Ingress switch { ProxyIngress.ExplicitProxy => L.T("HTTP / SOCKS 入站", "HTTP / SOCKS"), ProxyIngress.Tun => L.T("TUN 入站", "TUN"), _ => L.T("入站未确认", "Unknown ingress") },
                    row.Route switch { ProxyRoute.CoreDirect => L.T("核心报告 DIRECT", "Core reports DIRECT"), ProxyRoute.CoreProxy => L.T("核心报告代理链", "Core reports proxy"), _ => L.T("路径未确认", "Unknown route") },
                    Bytes(row.Traffic.ReceivedBytes), Bytes(row.Traffic.SentBytes))).ToList();
            if (snapshot.CapacityReached) { rows.Add(new(L.T("容量溢出 · 未归属", "Capacity overflow"), L.T("未知", "Unknown"), L.T("未知", "Unknown"), Bytes(snapshot.Overflow.ReceivedBytes), Bytes(snapshot.Overflow.SentBytes))); }
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
            if (!int.TryParse(proxyPort, out var port) || port is < 1 or > 65535) { status = L.T("代理入口端口无效。", "Invalid proxy entry port."); return; }
            ledger = new();
            historyEpoch = Guid.NewGuid();
            source = factory(endpoint, secret, port);
            await ReadCoreAsync();
        }
        catch (ArgumentException) { status = L.T("设置无效：控制器必须使用本机回环 IP，密钥不能包含控制字符。", "Invalid settings: use a literal loopback controller IP and a token without control characters."); await ReleaseAsync(); }
        catch (Exception) { status = L.T("代理观察启动失败，请检查本机控制器。", "Could not start proxy observation. Check the local controller."); await ReleaseAsync(); }
        finally { changing = false; gate.Release(); Notify(null); }
    }

    public async Task RefreshAsync()
    {
        if (closed || changing || refreshing || source is null) { return; }
        refreshing = true;
        await gate.WaitAsync();
        try { if (!closed && source is not null) { await ReadCoreAsync(); } }
        catch (Exception) { ledger.BreakContinuity(); status = L.T("代理读取失败，记录已有断档。", "Proxy read failed. Records have a gap."); }
        finally { refreshing = false; gate.Release(); Notify(null); }
    }

    public async Task StopAsync(bool powerBoundary = false)
    {
        if (verification is { } pending) { try { await pending.CancelAsync(); } catch (ObjectDisposedException) { } }
        changing = true; Notify(null);
        await gate.WaitAsync();
        try
        {
            try { if (source is not null) { await ReadCoreAsync(); } }
            finally { await ReleaseAsync(); }
            ledger.BreakContinuity();
            status = powerBoundary ? L.T("电源状态变化，代理观察已停止；需要手动重新启用。", "Proxy observation stopped at a power boundary. Enable it again manually.") : L.T("代理观察已停止，记录保留；再次开始会清空本页记录。", "Proxy observation stopped. Records are retained; restarting creates a new capture.");
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
            if (!int.TryParse(proxyPort, out var port) || port is < 1 or > 65535) { status = L.T("请填写有效的代理入口端口。", "Enter a valid proxy entry port."); return; }
            var candidate = await discover(port);
            if (closed) { return; }
            if (candidate is null) { status = L.T("没有找到唯一的控制器候选；请从客户端手动确认地址。未读取配置或密钥。", "No unique controller found. Check its address in your proxy client. No configurations or tokens were read."); }
            else { Endpoint = candidate; status = L.T("已找到本机核心的控制器候选。请在本地输入密钥后启用；未读取配置或密钥。", "Local controller candidate found. Enter the token locally to enable observation. No configurations or tokens were read."); }
        }
        catch (Exception) { status = L.T("本机控制器查找未完成；可以手动填写地址。", "Controller discovery did not finish. You can enter its address manually."); }
        finally { changing = false; gate.Release(); Notify(null); }
    }

    public async Task<string> VerifyAsync(Func<IProxyTrafficSource, int, CancellationToken, Task<string>> verify)
    {
        if (!CanVerify) { return L.T("请先连接本机控制器。", "Connect to the local controller first."); }
        changing = true; Notify(null);
        await gate.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        verification = cancellation;
        try { return source is null ? L.T("控制器未连接。", "Controller is not connected.") : await verify(source, int.Parse(proxyPort, System.Globalization.CultureInfo.InvariantCulture), cancellation.Token); }
        catch (Exception) { return L.T("本机验证未完成；没有修改代理配置。", "Local validation did not finish. Proxy settings were not changed."); }
        finally { verification = null; changing = false; gate.Release(); Notify(null); }
    }

    private async Task ReadCoreAsync()
    {
        var result = await source!.ReadAsync();
        version = result.Version;
        if (result.State == ProxyReadState.Available && result.Observation is { } observation)
        {
            ledger.Observe(observation);
            if (persist is not null) { await persist(historyEpoch, ledger.Snapshot()); }
            status = L.T("正在只读观察本机核心。每个连接只按核心 API 增量计一次；不叠加 ETW 原应用、核心进程或网卡字节。", "Observing the local core. Each API connection delta is counted once; ETW and interface bytes stay separate.");
            return;
        }

        ledger.BreakContinuity();
        if (persist is not null) { await persist(historyEpoch, ledger.Snapshot() with { CoreObserved = null, ConnectionObserved = null }); }
        status = result.State switch
        {
            ProxyReadState.AccessDenied => L.T("控制器认证失败。请停止后重新填写本地密钥，不要把密钥发送到聊天或提交到仓库。", "Controller authentication failed. Stop and re-enter the token locally; never share it in chat or source control."),
            ProxyReadState.InvalidResponse => L.T("控制器返回格式不受支持或超出上限；没有使用旧数据继续记账。", "Unsupported or oversized controller response. Old data is not reused."),
            ProxyReadState.CoreUnconfirmed => L.T("无法确认控制器与代理入口同属一个稳定核心；暂不记账。请停止后重新查找控制器，或检查管理员权限。", "Cannot confirm that controller and proxy entry belong to the same stable core. Stop and rediscover the controller or check permissions."),
            _ => L.T("控制器暂时不可用，已中断基线，恢复后重新观察。", "Controller unavailable. Baseline interrupted; observation will restart after recovery."),
        };
        if (result.State is ProxyReadState.AccessDenied or ProxyReadState.InvalidResponse) { await ReleaseAsync(); }
    }

    private async Task ReleaseAsync()
    {
        var current = source; source = null;
        if (current is not null) { await current.DisposeAsync(); }
    }

    private static string Format(TrafficVolume? volume) => volume is { } measured ? L.T($"下载 {Bytes(measured.ReceivedBytes)} / 上传 {Bytes(measured.SentBytes)}", $"Received {Bytes(measured.ReceivedBytes)} / Sent {Bytes(measured.SentBytes)}") : L.T("等待有效区间", "Waiting for a valid interval");
    private static string Bytes(ulong value) => value >= 1048576 ? $"{value / 1048576d:F2} MiB" : value >= 1024 ? $"{value / 1024d:F2} KiB" : $"{value} B";
}

public sealed record ProxyTrafficRow(string Application, string Ingress, string Route, string Download, string Upload);
