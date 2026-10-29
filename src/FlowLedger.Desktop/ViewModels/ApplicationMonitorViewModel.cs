using FlowLedger.Application.Monitoring;

namespace FlowLedger.Desktop.ViewModels;

public sealed class ApplicationMonitorViewModel(Func<IApplicationTrafficSource> factory) : ObservableState
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IApplicationTrafficSource? source;
    private ApplicationTrafficSnapshot? snapshot;
    private bool changing;
    private bool refreshing;
    private bool closed;
    private string status = "尚未启用应用统计。此实验功能需要以管理员身份运行软件。";

    public bool CanStart => !changing && !closed && source is null;
    public bool CanStop => !changing && source is not null;
    public string Status => status;
    public string Quality => snapshot is null ? "等待采集；没有数据不表示用量为零。" :
        $"已接收网络事件 {snapshot.Ledger.NetworkEvents}；已知丢失 {snapshot.Ledger.LostEvents}；不支持/异常事件 {snapshot.Ledger.RejectedEvents}。" +
        $"丢失计数{(snapshot.LossCounterAvailable ? "可读（不保证绝无遗漏）" : "不可确认")}。" +
        (snapshot.Ledger.IdentityUncertain || snapshot.Ledger.CapacityReached ? "记录不完整，部分进程身份已失效或达到容量上限。" : "") +
        (snapshot.Recovery is { } recovery ? $"已回收异常残留会话 {recovery.Recovered}；无法确认 {recovery.Unconfirmed}；回收失败 {recovery.Failed}。" : "") +
        "这是应用网络事件的 size 合计，包含本机回环及局域网；未与网卡或代理用量去重。";

    public IReadOnlyList<ApplicationTrafficRow> Rows
    {
        get
        {
            if (snapshot?.Ledger.Observed is null)
            {
                return [];
            }

            var rows = snapshot.Ledger.Processes
                .OrderByDescending(item => (decimal)item.Traffic.Total.ReceivedBytes + item.Traffic.Total.SentBytes)
                .Select(item => new ApplicationTrafficRow($"{item.Name} · PID {item.ProcessId}",
                    Format(item.Traffic.Total.ReceivedBytes), Format(item.Traffic.Total.SentBytes),
                    item.IsRundown ? "启动时发现的实例" : "已观察到启动的实例")).ToList();
            rows.Add(new("未识别进程", Format(snapshot.Ledger.Unidentified.Total.ReceivedBytes),
                Format(snapshot.Ledger.Unidentified.Total.SentBytes), "身份缺失 / 边界 / 降级"));
            return rows;
        }
    }

    public async Task StartAsync()
    {
        if (!CanStart)
        {
            return;
        }

        changing = true;
        Notify(null);
        await gate.WaitAsync();
        try
        {
            if (closed)
            {
                return;
            }

            source = await Task.Run(factory);
            snapshot = await Task.Run(source.Read);
            SetStatus();
            if (snapshot.State != ApplicationCaptureState.Running || closed)
            {
                await ReleaseAsync();
            }
        }
        catch (Exception)
        {
            status = "应用采集启动失败；请检查系统支持及权限。";
            await ReleaseAsync();
        }
        finally
        {
            changing = false;
            gate.Release();
            Notify(null);
        }
    }

    public async Task RefreshAsync()
    {
        if (refreshing || changing || closed || source is null)
        {
            return;
        }

        refreshing = true;
        await gate.WaitAsync();
        try
        {
            if (source is not null && !closed)
            {
                snapshot = await Task.Run(source.Read);
                SetStatus();
                if (snapshot.State != ApplicationCaptureState.Running)
                {
                    await ReleaseAsync();
                }
            }
        }
        catch (Exception)
        {
            status = "应用采集读取失败，已停止；已观察的记录可能不完整。";
            await ReleaseAsync();
        }
        finally
        {
            refreshing = false;
            gate.Release();
            Notify(null);
        }
    }

    public async Task StopAsync(bool powerBoundary = false)
    {
        changing = true;
        Notify(null);
        await gate.WaitAsync();
        try
        {
            if (source is not null)
            {
                await source.StopAsync();
                snapshot = await Task.Run(source.Read);
                await ReleaseAsync();
                SetStatus();
                if (powerBoundary)
                {
                    status = "电源状态发生变化，应用采集已停止；请手动开始新的记录。";
                }
            }
        }
        catch (Exception)
        {
            status = "应用采集停止时发生错误，记录可能不完整。";
            await ReleaseAsync();
        }
        finally
        {
            changing = false;
            gate.Release();
            Notify(null);
        }
    }

    public async Task ShutdownAsync()
    {
        closed = true;
        await StopAsync();
    }

    private async Task ReleaseAsync()
    {
        if (source is not null)
        {
            var current = source;
            source = null;
            await current.DisposeAsync();
        }
    }

    private void SetStatus() => status = snapshot?.State switch
    {
        ApplicationCaptureState.Running => "正在采集应用网络事件 · 实验阶段 · 本次记录仅在内存中。",
        ApplicationCaptureState.AccessDenied => "权限不足：请关闭软件后，以管理员身份重新打开，再启用应用统计。网卡与 Wi-Fi 功能仍可使用。",
        ApplicationCaptureState.Stopped => "应用采集已停止，保留本次记录；再次开始将清空应用记录并创建新会话。",
        ApplicationCaptureState.Faulted => "应用采集意外中断，记录不完整；可重新开始。",
        _ => "当前系统无法启动应用采集；请检查 ETW 支持及可用资源。",
    };

    private static string Format(ulong bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824d:F2} GiB",
        >= 1_048_576 => $"{bytes / 1_048_576d:F2} MiB",
        >= 1024 => $"{bytes / 1024d:F2} KiB",
        _ => $"{bytes} B",
    };
}

public sealed record ApplicationTrafficRow(string Name, string Download, string Upload, string Identity);
