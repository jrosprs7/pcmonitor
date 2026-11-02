namespace TaskbarMonitor;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Only allow one copy to run at a time.
        using var mutex = new Mutex(true, @"Local\TaskbarMonitor.SingleInstance", out bool isFirst);
        if (!isFirst) return;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayContext());
    }
}
