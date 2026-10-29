using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using FlowLedger.Application.Diagnostics;
using FlowLedger.Desktop.Localization;
using FlowLedger.Desktop.ViewModels;
using FlowLedger.Infrastructure.Windows.Diagnostics;
using FlowLedger.Infrastructure.Windows.History;
using FlowLedger.Infrastructure.Windows.Monitoring;
using FlowLedger.Infrastructure.Windows.Proxy;
using Microsoft.Win32;

namespace FlowLedger.Desktop;

public partial class MainWindow : Window
{
    private readonly IHostDiagnosticsReader diagnosticsReader = new WindowsHostDiagnosticsReader();
    private readonly InterfaceMonitorViewModel monitor;
    private readonly ApplicationMonitorViewModel applications;
    private readonly ApplicationMonitorViewModel ssh;
    private readonly ProxyMonitorViewModel proxy;
    private readonly HistoryViewModel history;
    private readonly SshRulesViewModel rules;
    private readonly DispatcherTimer sampleTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool closed;
    private bool closing;
    private bool ready;
    private bool shutdownComplete;
    private UiText environmentText = L.T("尚未检查", "Not checked");
    private UiText validationText = "";
    private static readonly string[] SectionKeys = ["Overview", "Networks", "Applications", "Proxy", "Ssh", "Reports", "Environment"];

    public MainWindow() : this(SqliteHistoryStore.DefaultPath) { }

    internal MainWindow(string databasePath, bool passive = false)
    {
        var store = new SqliteHistoryStore(databasePath);
        history = new(store, databasePath);
        rules = new(history);
        var reader = new WindowsInterfaceCounterReader();
        monitor = new(reader, new WindowsWifiConnectionReader(), () => new WindowsWifiTrafficSource(reader), new WindowsRouteEvidenceReader(), history.SaveInterfacesAsync);
        applications = new(() => new WindowsApplicationTrafficSource(), persist: snapshot => history.SaveApplicationsAsync(snapshot));
        ssh = new(() => new WindowsApplicationTrafficSource(rules.CaptureRules), sshMode: true, persist: snapshot => history.SaveApplicationsAsync(snapshot, ssh: true));
        proxy = new((endpoint, secret, port) => new ClashProxySource(endpoint, secret, port), history.SaveProxyAsync);
        UiCatalog.Apply(System.Windows.Application.Current.Resources);
        InitializeComponent();
        DataContext = monitor;
        ApplicationPanel.DataContext = applications; ProxyPanel.DataContext = proxy;
        SshPanel.DataContext = ssh; SshRulesPanel.DataContext = rules;
        ReportsPanel.DataContext = history; EnvironmentPanel.DataContext = history; StorageStatus.DataContext = history;
        NavigationList.SelectedIndex = 0; LanguagePicker.SelectedIndex = L.English ? 1 : 0;
        if (!passive)
        {
            Loaded += OnLoaded; Closing += OnClosing;
            Closed += (_, _) => System.Windows.Application.Current.Shutdown();
            sampleTimer.Tick += OnSample;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }
    }

    private async void OnNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) { return; }
        ShowSection();
        if (ready && NavigationList.SelectedIndex == 5) { await history.RefreshStreamsAsync(); }
    }

    private void ShowSection()
    {
        var index = NavigationList.SelectedIndex;
        if (index < 0 || index >= SectionKeys.Length) { return; }
        var key = SectionKeys[index];
        PageTitle.Text = (string)FindResource(key);
        PageDescription.Text = (string)FindResource(key + "Description");
        FrameworkElement[] panels = [TrafficPanel, WifiLedgerPanel, ApplicationPanel, ProxyPanel, SshPanel, ReportsPanel, EnvironmentPanel];
        for (var i = 0; i < panels.Length; i++) { panels[i].Visibility = index == i ? Visibility.Visible : Visibility.Collapsed; }
    }

    private async void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || LanguagePicker.SelectedIndex < 0) { return; }
        ApplyLanguage(LanguagePicker.SelectedIndex == 1);
        if (ready) { await history.SetSettingAsync("language", L.English ? "en" : "zh-CN"); }
    }

    private void ApplyLanguage(bool english)
    {
        L.UseEnglish(english);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(english ? "en-US" : "zh-CN");
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
        Language = XmlLanguage.GetLanguage(english ? "en-US" : "zh-CN");
        Title = english ? "FlowLedger · Local ledger" : "FlowLedger · 流量簿";
        UiCatalog.Apply(System.Windows.Application.Current.Resources);
        foreach (var choice in monitor.Interfaces) { choice.RefreshText(); }
        monitor.RefreshText(); applications.RefreshText(); proxy.RefreshText(); ssh.RefreshText(); rules.RefreshText(); history.RefreshText();
        foreach (var item in history.Streams) { item.RefreshText(); }
        foreach (var item in history.Periods) { item.RefreshText(); }
        EnvironmentResult.Text = environmentText; ProxyValidationResult.Text = validationText;
        ShowSection();
    }

    private void OnCheckEnvironment(object sender, RoutedEventArgs e)
    {
        var snapshot = diagnosticsReader.Read();
        environmentText = L.T($"系统：{snapshot.OperatingSystem}\n架构：{snapshot.Architecture}\n运行环境：{snapshot.RuntimeVersion}\n接口清单状态：{snapshot.NetworkInventoryState}",
            $"System: {snapshot.OperatingSystem}\nArchitecture: {snapshot.Architecture}\nRuntime: {snapshot.RuntimeVersion}\nInterface inventory: {snapshot.NetworkInventoryState}");
        EnvironmentResult.Text = environmentText;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await history.InitializeAsync();
        var language = await history.ReadSettingAsync("language");
        await rules.LoadAsync();
        if (closed) { return; }
        LanguagePicker.SelectedIndex = language == "en" ? 1 : 0;
        ApplyLanguage(language == "en");
        ready = true;
        await monitor.RefreshAsync();
        await history.RefreshStreamsAsync();
        if (!closed) { sampleTimer.Start(); }
    }

    private async void OnSample(object? sender, EventArgs e) => await Task.WhenAll(monitor.RefreshAsync(), applications.RefreshAsync(), proxy.RefreshAsync(), ssh.RefreshAsync());
    private async void OnStartProxy(object sender, RoutedEventArgs e) { var secret = ProxySecret.Password; ProxySecret.Clear(); await proxy.StartAsync(secret); }
    private async void OnStopProxy(object sender, RoutedEventArgs e) { await proxy.StopAsync(); await history.SaveAsync([]); }
    private async void OnFindProxy(object sender, RoutedEventArgs e) => await proxy.DiscoverAsync(port => ProxyControllerDiscovery.FindAsync(port));
    private async void OnVerifyProxy(object sender, RoutedEventArgs e)
    {
        validationText = L.T("正在验证本机代理…", "Validating the local proxy…"); ProxyValidationResult.Text = validationText;
        await proxy.VerifyAsync(async (source, port, token) =>
        {
            var result = await ProxyLocalValidation.RunAsync(source, port, token);
            var directory = System.IO.Path.GetDirectoryName(SqliteHistoryStore.DefaultPath)!;
            System.IO.Directory.CreateDirectory(directory);
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(directory, "proxy-validation.json"),
                JsonSerializer.Serialize(new { CompletedAtUtc = DateTimeOffset.UtcNow, Result = result }), token);
            validationText = result.Passed ? L.T("本机代理验证通过。此测试不验证 TUN 或公网。", "Local proxy validation passed. TUN and Internet paths are not covered.")
                : L.T("本机代理验证未通过，请检查认证及规则。", "Local proxy validation failed. Check authentication and rules.");
            return validationText;
        });
        if (validationText.Chinese == "正在验证本机代理…") { validationText = L.T("本机验证未完成；请确认控制器已连接。", "Validation did not finish. Check that the controller is connected."); }
        ProxyValidationResult.Text = validationText;
    }

    private async void OnStartApplications(object sender, RoutedEventArgs e) => await applications.StartAsync();
    private async void OnStopApplications(object sender, RoutedEventArgs e) { await applications.StopAsync(); await history.SaveAsync([]); }
    private async void OnStartSsh(object sender, RoutedEventArgs e) => await ssh.StartAsync();
    private async void OnStopSsh(object sender, RoutedEventArgs e) { await ssh.StopAsync(); await history.SaveAsync([]); }
    private async void OnAddSshRule(object sender, RoutedEventArgs e) { if (ssh.CanStart) { await rules.AddAsync(); } }
    private async void OnRemoveSshRule(object sender, RoutedEventArgs e) { if (ssh.CanStart) { await rules.RemoveAsync(); } }
    private async void OnReadWifi(object sender, RoutedEventArgs e) => await monitor.ReadWifiAsync();
    private async void OnEnableWifi(object sender, RoutedEventArgs e) => await monitor.SetWifiEnabledAsync(true);
    private async void OnDisableWifi(object sender, RoutedEventArgs e) { await monitor.SetWifiEnabledAsync(false); await history.SaveAsync([]); }
    private async void OnReadRoutes(object sender, RoutedEventArgs e) => await monitor.ReadRoutesAsync();
    private async void OnQueryReport(object sender, RoutedEventArgs e) => await history.QueryAsync();
    private async void OnRefreshLedgers(object sender, RoutedEventArgs e) => await history.RefreshStreamsAsync();

    private async void OnExportCsv(object sender, RoutedEventArgs e)
    {
        if (!history.CanExport) { return; }
        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"FlowLedger-{DateTime.Now:yyyy-MM-dd}.csv", AddExtension = true, DefaultExt = ".csv" };
        if (dialog.ShowDialog(this) != true) { return; }
        try { var csv = history.ExportCsv(); await System.IO.File.WriteAllTextAsync(dialog.FileName, csv, new UTF8Encoding(true)); }
        catch (Exception) { MessageBox.Show(this, L.T("CSV 保存失败，请检查目标位置。", "Could not save CSV. Check the destination."), "FlowLedger", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is PowerModes.Suspend or PowerModes.Resume)
        {
            Dispatcher.BeginInvoke(async () =>
            {
                if (closed) { return; }
                monitor.BreakContinuity();
                await Task.WhenAll(applications.StopAsync(powerBoundary: true), proxy.StopAsync(powerBoundary: true), ssh.StopAsync(powerBoundary: true));
                await history.SaveAsync([]);
            });
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (shutdownComplete) { return; }
        e.Cancel = true;
        if (closing) { return; }
        closing = true; closed = true; sampleTimer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        try
        {
            await Task.WhenAll(monitor.ShutdownAsync(), applications.ShutdownAsync(), proxy.ShutdownAsync(), ssh.ShutdownAsync());
            await history.SaveAsync([]);
            if (history.HasUnsaved && MessageBox.Show(this,
                L.T("部分历史尚未保存。选择“否”保留窗口，稍后再次关闭以重试；选择“是”丢弃未保存部分并退出。", "Some history is unsaved. Choose No to keep this window and retry closing later, or Yes to discard unsaved data and exit."),
                "FlowLedger", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) { return; }
            shutdownComplete = true; Close();
        }
        finally { closing = false; }
    }
}
