using FlowLedger.Application.Monitoring;
using FlowLedger.Desktop.Localization;
using FlowLedger.Domain.Ssh;

namespace FlowLedger.Desktop.ViewModels;

public sealed class ApplicationMonitorViewModel(Func<IApplicationTrafficSource> factory, bool sshMode = false,
    Func<ApplicationTrafficSnapshot, Task>? persist = null) : ObservableState
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IApplicationTrafficSource? source;
    private ApplicationTrafficSnapshot? snapshot;
    private bool changing;
    private bool refreshing;
    private bool closed;
    private UiText status = L.T("尚未启用。流量统计需要管理员权限。", "Not started. Traffic capture requires administrator rights.");

    public bool CanStart => !changing && !closed && source is null;
    public bool CanStop => !changing && source is not null;
    public string Status => status;
    public string Quality => snapshot is null ? L.T("等待采集；没有数据不表示用量为零。", "Waiting for capture. Missing data is not zero usage.") :
        L.T($"网络事件 {snapshot.Ledger.NetworkEvents} · 已知丢失 {snapshot.Ledger.LostEvents} · 异常事件 {snapshot.Ledger.RejectedEvents}",
            $"Network events {snapshot.Ledger.NetworkEvents} · Known lost {snapshot.Ledger.LostEvents} · Rejected {snapshot.Ledger.RejectedEvents}") +
        L.T(snapshot.LossCounterAvailable ? " · 丢失计数可读" : " · 丢失计数不可确认", snapshot.LossCounterAvailable ? " · Loss counter available" : " · Loss counter unconfirmed") +
        (snapshot.Ledger.IdentityUncertain || snapshot.Ledger.CapacityReached || snapshot.Ssh?.CapacityReached == true
            ? L.T("。记录不完整，身份降级或达到容量上限。", ". Partial data: attribution degraded or capacity reached.") : "") +
        (snapshot.Recovery is { } recovery ? L.T($" · 回收残留 {recovery.Recovered} / 不明 {recovery.Unconfirmed} / 失败 {recovery.Failed}",
            $" · Recovery {recovery.Recovered} / unconfirmed {recovery.Unconfirmed} / failed {recovery.Failed}") : "");

    public IReadOnlyList<ApplicationTrafficRow> Rows
    {
        get
        {
            if (snapshot?.Ledger.Observed is null) { return []; }
            var rows = snapshot.Ledger.Processes.OrderByDescending(item => (decimal)item.Traffic.Total.ReceivedBytes + item.Traffic.Total.SentBytes)
                .Select(item => new ApplicationTrafficRow($"{item.Name} · PID {item.ProcessId}",
                    L.Bytes(item.Traffic.Total.ReceivedBytes), L.Bytes(item.Traffic.Total.SentBytes),
                    item.IsRundown ? L.T("启动时发现的实例", "Present at capture start") : L.T("已观察到启动的实例", "Process start observed"))).ToList();
            rows.Add(new(L.T("未识别进程", "Unknown process"), L.Bytes(snapshot.Ledger.Unidentified.Total.ReceivedBytes),
                L.Bytes(snapshot.Ledger.Unidentified.Total.SentBytes), L.T("身份缺失 / 边界 / 降级", "Missing identity / boundary / degraded")));
            return rows;
        }
    }

    public IReadOnlyList<SshTrafficRow> SshRows => snapshot?.Ssh is not { Observed: not null } ssh ? [] :
        ssh.Connections.OrderByDescending(item => (decimal)item.Traffic.ReceivedBytes + item.Traffic.SentBytes)
            .Select(item => new SshTrafficRow(item.Alias, SshEndpoint.Format(item.Server, item.Port), $"{item.Client} · {item.ProcessId} / :{item.LocalPort}",
                item.Closed ? L.T("已结束", "Closed") : L.T("已观察，结束待确认", "Observed; closure unconfirmed"),
                item.Evidence == SshEvidence.UserRule ? L.T("用户规则", "User rule") : L.T("客户端候选", "Client candidate"),
                L.Bytes(item.Traffic.ReceivedBytes), L.Bytes(item.Traffic.SentBytes)))
            .Append(new(L.T("未归属 / 溢出", "Unknown / overflow"), "—", "—", "—", "—", L.Bytes(ssh.Unidentified.ReceivedBytes), L.Bytes(ssh.Unidentified.SentBytes))).ToArray();

    public async Task StartAsync()
    {
        if (!CanStart) { return; }
        changing = true; Notify(null);
        await gate.WaitAsync();
        try
        {
            if (closed) { return; }
            source = await Task.Run(factory);
            await ReadAsync();
            if (snapshot!.State != ApplicationCaptureState.Running || closed) { await ReleaseAsync(); }
        }
        catch (Exception) { status = L.T("采集启动失败；请检查系统支持及权限。", "Capture could not start. Check system support and permissions."); await ReleaseAsync(); }
        finally { changing = false; gate.Release(); Notify(null); }
    }

    public async Task RefreshAsync()
    {
        if (refreshing || changing || closed || source is null) { return; }
        refreshing = true;
        await gate.WaitAsync();
        try
        {
            if (source is not null && !closed)
            {
                await ReadAsync();
                if (snapshot!.State != ApplicationCaptureState.Running) { await ReleaseAsync(); }
            }
        }
        catch (Exception) { status = L.T("采集读取失败，已停止；记录可能不完整。", "Capture read failed and stopped. Records may be incomplete."); await ReleaseAsync(); }
        finally { refreshing = false; gate.Release(); Notify(null); }
    }

    public async Task StopAsync(bool powerBoundary = false)
    {
        changing = true; Notify(null);
        await gate.WaitAsync();
        try
        {
            if (source is not null)
            {
                await source.StopAsync();
                await ReadAsync();
                await ReleaseAsync();
                if (powerBoundary) { status = L.T("电源状态发生变化，采集已停止；请手动开始新的记录。", "Capture stopped at a power boundary. Start a new capture manually."); }
            }
        }
        catch (Exception) { status = L.T("采集停止时发生错误，记录可能不完整。", "An error occurred while stopping. Records may be incomplete."); await ReleaseAsync(); }
        finally { changing = false; gate.Release(); Notify(null); }
    }

    public async Task ShutdownAsync() { closed = true; await StopAsync(); }

    private async Task ReadAsync()
    {
        snapshot = await Task.Run(source!.Read);
        status = snapshot.State switch
        {
            ApplicationCaptureState.Running => sshMode ? L.T("正在统计 SSH 客户端连接。", "Capturing SSH client connections.") : L.T("正在采集应用网络事件。", "Capturing application network events."),
            ApplicationCaptureState.AccessDenied => L.T("权限不足：请以管理员身份打开软件后启用。", "Access denied: reopen FlowLedger as administrator to enable capture."),
            ApplicationCaptureState.Stopped => L.T("采集已停止，保留本次记录；历史记录可在报表中查看。", "Capture stopped. Current records are retained; saved history is available in Reports."),
            ApplicationCaptureState.Faulted => L.T("采集意外中断，记录不完整；可重新开始。", "Capture interrupted. Records are partial; you can start again."),
            _ => L.T("当前系统无法启动采集。", "Capture is unavailable on this system."),
        };
        if (persist is not null) { await persist(snapshot); }
    }

    private async Task ReleaseAsync()
    {
        var current = source; source = null;
        if (current is not null) { await current.DisposeAsync(); }
    }
}

public sealed record ApplicationTrafficRow(string Name, string Download, string Upload, string Identity);
public sealed record SshTrafficRow(string Alias, string Server, string Client, string State, string Evidence, string Download, string Upload);
