namespace TaskbarMonitor;

/// <summary>Small text log at %LOCALAPPDATA%\TaskbarMonitor\TaskbarMonitor.log (rotated at 512 KB).</summary>
internal static class Log
{
    private static readonly object Gate = new();
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarMonitor", "TaskbarMonitor.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 512 * 1024) File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [pid {Environment.ProcessId}, v{Updater.VersionText}] {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }
}
