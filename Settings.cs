using Microsoft.Win32;

namespace TaskbarMonitor;

internal enum DisplayMode
{
    TaskbarWidget = 0, // one rectangular panel on the taskbar
    CombinedIcon = 1,  // one tray icon, one row per metric
    SeparateIcons = 2, // one tray icon per metric
}

[Flags]
internal enum Metrics
{
    None = 0,
    Cpu = 1,
    Gpu = 2,
    Ram = 4,
    Temp = 8, // GPU temperature
    All = Cpu | Gpu | Ram | Temp,
}

/// <summary>Small settings store in HKCU\Software\TaskbarMonitor.</summary>
internal static class Settings
{
    private const string KeyPath = @"Software\TaskbarMonitor";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "TaskbarMonitor";

    public static int IntervalMs
    {
        get => Get("IntervalMs", 1000);
        set => Set("IntervalMs", value);
    }

    public static DisplayMode Mode
    {
        get => Enum.IsDefined(typeof(DisplayMode), Get("Display", 0)) ? (DisplayMode)Get("Display", 0) : DisplayMode.TaskbarWidget;
        set => Set("Display", (int)value);
    }

    /// <summary>Which metrics to show on the panel / icons (the tooltip always shows everything).</summary>
    public static Metrics Shown
    {
        get
        {
            var m = (Metrics)Get("ShownMetrics", (int)Metrics.All) & Metrics.All;
            return m == Metrics.None ? Metrics.All : m;
        }
        set => Set("ShownMetrics", (int)value);
    }

    /// <summary>Taskbar panel: show the number (23%) instead of the label (CPU) inside each ring.</summary>
    public static bool RingShowsNumber
    {
        get => Get("RingShowsNumber", 0) != 0;
        set => Set("RingShowsNumber", value ? 1 : 0);
    }

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (value) key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    private static int Get(string name, int fallback)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(name) is int i ? i : fallback;
    }

    private static void Set(string name, int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }

    /// <summary>
    /// Windows 11 hides new tray icons in the overflow (^) menu. On first run, ask Explorer to
    /// show ours on the taskbar. Only touches entries the user hasn't already decided on.
    /// (Best effort: uses the per-icon settings Windows 11 keeps under Control Panel\NotifyIconSettings.)
    /// </summary>
    public static void PromoteTrayIcons()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is null) return;
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root is null) return;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub, writable: true);
                if (k?.GetValue("ExecutablePath") is string p &&
                    string.Equals(p, exe, StringComparison.OrdinalIgnoreCase) &&
                    k.GetValue("IsPromoted") is null)
                {
                    k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                }
            }
        }
        catch { /* not Windows 11, or no access — user can pin manually */ }
    }
}
