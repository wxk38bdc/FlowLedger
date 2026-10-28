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
    private readonly InterfaceMonitorViewModel monitor = new(new WindowsInterfaceCounterReader(), new WindowsWifiConnectionReader());
    private readonly DispatcherTimer sampleTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool closed;

    private static readonly (string Title, string Description, string EmptyMessage)[] Sections =
    [
        ("总览", "实时查看所选接口的流量；各接口独立计量。", ""),
        ("网络账本", "分清家庭网络、办公网络与手机热点的用量。", "网络识别与切换记账功能正在规划中；获得可靠的网络信息后才会开始分账。"),
        ("应用统计", "了解每个软件使用了多少流量。", "应用排行将在采集验证完成后开放。无法确认归属的流量会单独标记。"),
        ("远程连接", "查看 SSH 与代理连接的本地流量消耗。", "这里将按服务器和连接汇总本机用量。远程服务器自身的下载不会被视作本地下载。"),
        ("运行环境", "查看这台电脑的基础运行条件。", ""),
    ];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = monitor;
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
        EmptyState.Visibility = index is 0 or 4 ? Visibility.Collapsed : Visibility.Visible;
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

    private async void OnSample(object? sender, EventArgs e) => await monitor.RefreshAsync();

    private async void OnReadWifi(object sender, RoutedEventArgs e) => await monitor.ReadWifiAsync();

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is PowerModes.Suspend or PowerModes.Resume)
        {
            Dispatcher.BeginInvoke(() => monitor.BreakContinuity());
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        closed = true;
        sampleTimer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}
