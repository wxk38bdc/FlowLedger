using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlowLedger.Desktop;
using FlowLedger.Desktop.Localization;
using FlowLedger.Desktop.ViewModels;

namespace FlowLedger.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class UiLayoutTests
{
    [TestMethod]
    public void AllPagesLoadInBothLanguagesAndRenderAtMinimumWidth()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App(); app.InitializeComponent();
                var window = new MainWindow(Path.Combine(Path.GetTempPath(), "FlowLedger-ui-unused.sqlite3"), passive: true);
                var root = (FrameworkElement)window.Content;
                var language = (ComboBox)window.FindName("LanguagePicker");
                var navigation = (ListBox)window.FindName("NavigationList");
                foreach (var english in new[] { false, true })
                {
                    language.SelectedIndex = english ? 1 : 0;
                    Assert.AreEqual(english ? "Reports" : "历史报表", app.Resources["Reports"]);
                    foreach (var item in new[] { ("ApplicationPanel", "Rows"), ("SshPanel", "SshRows") })
                    {
                        var panel = (FrameworkElement)window.FindName(item.Item1);
                        panel.DataContext = item.Item2 == "Rows" ? new
                        {
                            Status = english ? "Capture active" : "正在采集",
                            Quality = "TCP / UDP · IPv4 / IPv6",
                            Rows = new[] {
                            new ApplicationTrafficRow("ssh.exe · PID 1024", "12.34 MiB", "5.67 MiB", english ? "Process start observed" : "已观察到启动的实例") }
                        }
                            : (object)new
                            {
                                Status = english ? "Capturing SSH client connections" : "正在统计 SSH 客户端连接",
                                Quality = "TCP · IPv4 / IPv6",
                                SshRows = new[] {
                                new SshTrafficRow("Development", "192.0.2.8:2222", "ssh.exe · 1024 / :50000", english ? "Closed" : "已结束", english ? "Client candidate" : "客户端候选", "12.34 MiB", "5.67 MiB") }
                            };
                    }
                    for (var index = 0; index < 7; index++)
                    {
                        navigation.SelectedIndex = index;
                        root.Measure(new Size(1040, 800)); root.Arrange(new Rect(0, 0, 1040, 800)); root.UpdateLayout();
                        var title = (TextBlock)window.FindName("PageTitle"); Assert.IsFalse(string.IsNullOrWhiteSpace(title.Text));
                        var panel = (FrameworkElement)window.FindName(new[] { "TrafficPanel", "WifiLedgerPanel", "ApplicationPanel", "ProxyPanel", "SshPanel", "ReportsPanel", "EnvironmentPanel" }[index]);
                        Assert.AreEqual(Visibility.Visible, panel.Visibility); Assert.IsTrue(panel.ActualWidth > 600);
                        foreach (var grid in Descendants(panel).OfType<DataGrid>())
                        {
                            foreach (var column in grid.Columns)
                            { Assert.IsFalse(english && column.Header?.ToString()?.Any(c => c is >= '\u4e00' and <= '\u9fff') == true, "English column header retained Chinese text."); }
                        }
                        var output = Environment.GetEnvironmentVariable("FLOWLEDGER_UI_TEST_OUTPUT");
                        if (!string.IsNullOrWhiteSpace(output))
                        {
                            Directory.CreateDirectory(output);
                            var image = new RenderTargetBitmap(1040, 800, 96, 96, PixelFormats.Pbgra32); image.Render(root);
                            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                            using var file = File.Create(Path.Combine(output, $"{(english ? "en" : "zh")}-{index}.png")); encoder.Save(file);
                        }
                    }
                }
            }
            catch (Exception exception) { error = exception; }
            finally { L.UseEnglish(false); }
        });
        thread.IsBackground = true; thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "UI layout did not complete.");
        if (error is not null) { ExceptionDispatchInfo.Capture(error).Throw(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); yield return child;
            foreach (var descendant in Descendants(child)) { yield return descendant; }
        }
    }
}
