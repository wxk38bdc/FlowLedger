namespace FlowLedger.Desktop;

public partial class App : System.Windows.Application
{
    private Mutex? instance;
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        instance = new Mutex(true, "Local\\FlowLedger.Desktop", out var created);
        if (!created)
        {
            System.Windows.MessageBox.Show("FlowLedger 已在运行 / FlowLedger is already running.", "FlowLedger");
            Shutdown(); return;
        }
        base.OnStartup(e);
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        instance?.Dispose(); base.OnExit(e);
    }
}
