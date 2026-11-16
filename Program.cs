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

        // Only allow one copy to run at a time. Right after an update, the new copy waits for the old one to exit.
        bool justUpdated = args.Contains("--updated");
        using var mutex = new Mutex(true, @"Local\TaskbarMonitor.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            if (!justUpdated) return;
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(30))) return;
            }
            catch (AbandonedMutexException) { /* the old copy exited without releasing it: we own it now */ }
        }
        Updater.CleanUpAfterUpdate(); // removes TaskbarMonitor.exe.old left by an update, if any

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayContext());
    }
}
