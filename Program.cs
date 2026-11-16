using System.Diagnostics;

namespace TaskbarMonitor;

internal static class Program
{
    private const string MutexName = @"Local\TaskbarMonitor.SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--diag")
        {
            Diagnostics.Run(args[1], args.Length >= 3 && int.TryParse(args[2], out int secs) ? secs : 20);
            return;
        }

        // Log unexpected errors instead of showing a crash dialog (or silently hanging).
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write("Unhandled UI error: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Fatal error: " + e.ExceptionObject);

        // "--updated [oldPid]": started by the previous copy right after it installed this version.
        int idx = Array.IndexOf(args, "--updated");
        bool justUpdated = idx >= 0;
        int? oldPid = justUpdated && idx + 1 < args.Length && int.TryParse(args[idx + 1], out int pid) ? pid : null;

        // Only allow one copy to run at a time. After an update, wait for the old copy to exit.
        using var mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            if (!justUpdated) return;
            Log.Write($"Started after update; waiting for the previous copy{(oldPid is null ? "" : $" (pid {oldPid})")} to exit");
            if (!Acquire(mutex, TimeSpan.FromSeconds(15)))
            {
                EndPreviousCopies(oldPid);
                if (!Acquire(mutex, TimeSpan.FromSeconds(15)))
                {
                    Log.Write("The previous copy is still running; giving up");
                    return;
                }
            }
            Log.Write("Previous copy has exited; starting");
        }
        else if (justUpdated) Log.Write("Started after update");

        Updater.CleanUpAfterUpdate(); // removes TaskbarMonitor.exe.old left by an update, if any

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayContext());
        Log.Write("Exited");
    }

    private static bool Acquire(Mutex mutex, TimeSpan timeout)
    {
        try { return mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; } // the old copy ended without releasing it: we own it now
    }

    /// <summary>
    /// Ends an old copy that didn't exit after updating: the given process id, or any other TaskbarMonitor
    /// process in this user session started from this same file. (A copy that was running when its file was
    /// renamed to .old still reports the original path.)
    /// </summary>
    private static void EndPreviousCopies(int? oldPid)
    {
        string? exe = Environment.ProcessPath;
        var me = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(me.ProcessName))
        {
            using (p)
            {
                try
                {
                    if (p.Id == me.Id || p.SessionId != me.SessionId) continue;
                    string? path = p.MainModule?.FileName;
                    bool sameFile = exe is not null && path is not null &&
                        (path.Equals(exe, StringComparison.OrdinalIgnoreCase) ||
                         path.Equals(exe + ".old", StringComparison.OrdinalIgnoreCase));
                    if (p.Id != oldPid && !sameFile) continue;
                    Log.Write($"Ending previous copy (pid {p.Id}) that didn't exit after the update");
                    p.Kill();
                    p.WaitForExit(5000);
                }
                catch (Exception ex) { Log.Write($"Couldn't end pid {p.Id}: {ex.Message}"); }
            }
        }
    }
}
