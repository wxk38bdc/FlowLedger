using System.Windows;
using System.Windows.Controls;
using FlowLedger.Application.Diagnostics;
using FlowLedger.Infrastructure.Windows.Diagnostics;

namespace FlowLedger.Desktop;

public partial class MainWindow : Window
{
    private readonly IHostDiagnosticsReader diagnosticsReader = new WindowsHostDiagnosticsReader();

    private static readonly (string Title, string Description, string EmptyMessage)[] Sections =
    [
        ("总览", "把每一笔流量，记在清楚的账上。", "采集功能完成后，这里将显示上传、下载、趋势和记录完整性。目前没有生成示例用量。"),
        ("网络账本", "分清家庭网络、办公网络与手机热点的用量。", "网络识别与切换记账功能正在规划中；获得可靠的网络信息后才会开始分账。"),
        ("应用统计", "了解每个软件使用了多少流量。", "应用排行将在采集验证完成后开放。无法确认归属的流量会单独标记。"),
        ("远程连接", "查看 SSH 与代理连接的本地流量消耗。", "这里将按服务器和连接汇总本机用量。远程服务器自身的下载不会被视作本地下载。"),
        ("运行环境", "查看这台电脑的基础运行条件。", ""),
    ];

    public MainWindow()
    {
        InitializeComponent();
        NavigationList.SelectedIndex = 0;
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
        EmptyState.Visibility = index == 4 ? Visibility.Collapsed : Visibility.Visible;
        EnvironmentPanel.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCheckEnvironment(object sender, RoutedEventArgs e)
    {
        var snapshot = diagnosticsReader.Read();
        var inventoryText = snapshot.NetworkInventoryState == NetworkInventoryState.Available
            ? $"可读取的网络接口条目：{snapshot.InterfaceGroups.Sum(group => group.Count)}（含虚拟和过滤接口，不代表物理网卡数量）"
            : "网络接口信息暂时不可读取。";

        EnvironmentResult.Text = $"系统：{snapshot.OperatingSystem}\n架构：{snapshot.Architecture}\n运行环境：{snapshot.RuntimeVersion}\n{inventoryText}\n本次检查没有启动流量采集。";
    }
}
