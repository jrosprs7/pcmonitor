namespace TaskbarMonitor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--diag")
        {
            Diagnostics.Run(args[1], args.Length >= 3 && int.TryParse(args[2], out int secs) ? secs : 20);
            return;
        }

        // Only allow one copy to run at a time.
        using var mutex = new Mutex(true, @"Local\TaskbarMonitor.SingleInstance", out bool isFirst);
        if (!isFirst) return;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayContext());
    }
}
