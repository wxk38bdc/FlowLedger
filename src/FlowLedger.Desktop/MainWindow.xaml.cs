using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlowLedger.Application.Diagnostics;
using FlowLedger.Desktop.ViewModels;
using FlowLedger.Infrastructure.Windows.Diagnostics;
using FlowLedger.Infrastructure.Windows.Monitoring;
using Microsoft.Win32;

namespace FlowLedger.Desktop;

public partial class MainWindow : Window
{
    private readonly IHostDiagnosticsReader diagnosticsReader = new WindowsHostDiagnosticsReader();
    private readonly InterfaceMonitorViewModel monitor;
    private readonly ApplicationMonitorViewModel applications = new(() => new WindowsApplicationTrafficSource());
    private readonly DispatcherTimer sampleTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool closed;

    private static readonly (string Title, string Description, string EmptyMessage)[] Sections =
    [
        ("总览", "实时查看所选接口的流量；各接口独立计量。", ""),
        ("网络账本", "按 Wi-Fi 网络候选分账；切换边界和未知归属分别保留。", ""),
        ("应用统计", "按进程实例查看应用网络事件用量；实验阶段，需管理员权限。", ""),
        ("远程连接", "查看 SSH 与代理连接的本地流量消耗。", "这里将按服务器和连接汇总本机用量。远程服务器自身的下载不会被视作本地下载。"),
        ("运行环境", "查看这台电脑的基础运行条件。", ""),
    ];

    public MainWindow()
    {
        var reader = new WindowsInterfaceCounterReader();
        monitor = new(reader, new WindowsWifiConnectionReader(),
            () => new WindowsWifiTrafficSource(reader), new WindowsRouteEvidenceReader());
        InitializeComponent();
        DataContext = monitor;
        ApplicationPanel.DataContext = applications;
        NavigationList.SelectedIndex = 0;
        Loaded += OnLoaded;
        Closed += OnClosed;
        sampleTimer.Tick += OnSample;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void OnNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = NavigationList.SelectedIndex;
        if (index < 0 || index >= Sections.Length)
        {
            return;
        }

        var section = Sections[index];
        PageTitle.Text = section.Title;
        PageDescription.Text = section.Description;
        EmptyDescription.Text = section.EmptyMessage;
        TrafficPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        WifiLedgerPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        ApplicationPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = index is 0 or 1 or 2 or 4 ? Visibility.Collapsed : Visibility.Visible;
        EnvironmentPanel.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCheckEnvironment(object sender, RoutedEventArgs e)
    {
        var snapshot = diagnosticsReader.Read();
        var inventoryText = snapshot.NetworkInventoryState == NetworkInventoryState.Available
            ? $"可读取的网络接口条目：{snapshot.InterfaceGroups.Sum(group => group.Count)}（含虚拟和过滤接口，不代表物理网卡数量）"
            : "网络接口信息暂时不可读取。";

        EnvironmentResult.Text = $"系统：{snapshot.OperatingSystem}\n架构：{snapshot.Architecture}\n运行环境：{snapshot.RuntimeVersion}\n{inventoryText}\n此按钮只检查环境；接口采样随窗口独立运行。";
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await monitor.RefreshAsync();
        if (!closed)
        {
            sampleTimer.Start();
        }
    }

    private async void OnSample(object? sender, EventArgs e) =>
        await Task.WhenAll(monitor.RefreshAsync(), applications.RefreshAsync());

    private async void OnStartApplications(object sender, RoutedEventArgs e) => await applications.StartAsync();

    private async void OnStopApplications(object sender, RoutedEventArgs e) => await applications.StopAsync();

    private async void OnReadWifi(object sender, RoutedEventArgs e) => await monitor.ReadWifiAsync();

    private async void OnEnableWifi(object sender, RoutedEventArgs e) => await monitor.SetWifiEnabledAsync(true);

    private async void OnDisableWifi(object sender, RoutedEventArgs e) => await monitor.SetWifiEnabledAsync(false);

    private async void OnReadRoutes(object sender, RoutedEventArgs e) => await monitor.ReadRoutesAsync();

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is PowerModes.Suspend or PowerModes.Resume)
        {
            Dispatcher.BeginInvoke(async () =>
            {
                monitor.BreakContinuity();
                await applications.StopAsync(powerBoundary: true);
            });
        }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        closed = true;
        sampleTimer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        try
        {
            await Task.WhenAll(monitor.ShutdownAsync(), applications.ShutdownAsync());
        }
        finally
        {
            System.Windows.Application.Current.Shutdown();
        }
    }
}
