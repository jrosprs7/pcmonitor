using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace TaskbarMonitor;

/// <summary>
/// Hidden troubleshooting mode: <c>TaskbarMonitor.exe --diag out.csv 20</c> logs the app's CPU reading
/// next to other ways of measuring CPU usage, once a second, so they can be compared with Task Manager.
/// </summary>
internal static class Diagnostics
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint Low, High; public readonly ulong Value => ((ulong)High << 32) | Low; }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    public static void Run(string path, int seconds)
    {
        var inv = CultureInfo.InvariantCulture;
        using var w = new StreamWriter(path);
        using var app = new CpuMonitor();
        using var util = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total", true);
        using var time = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);

        w.WriteLine($"# cores={Environment.ProcessorCount} utilityCounterType={util.CounterType} os={Environment.OSVersion}");
        w.WriteLine("t,app,processor_utility,utility_raw_value,utility_base_value,processor_time,system_times");

        util.NextValue();
        time.NextValue();
        GetSystemTimes(out var i0, out var k0, out var u0);
        var start = Stopwatch.StartNew();

        for (int s = 1; s <= seconds; s++)
        {
            Thread.Sleep(1000);
            float a = app.Read() ?? -1;
            var raw = util.NextSample();
            float u = util.NextValue();
            float t = time.NextValue();

            GetSystemTimes(out var i1, out var k1, out var u1);
            ulong idle = i1.Value - i0.Value, total = (k1.Value - k0.Value) + (u1.Value - u0.Value); // kernel includes idle
            double st = total == 0 ? 0 : 100.0 * (1 - (double)idle / total);
            (i0, k0, u0) = (i1, k1, u1);

            w.WriteLine(string.Join(",",
                (start.ElapsedMilliseconds / 1000.0).ToString("0.0", inv),
                a.ToString("0.0", inv), u.ToString("0.0", inv),
                raw.RawValue.ToString(inv), raw.BaseValue.ToString(inv),
                t.ToString("0.0", inv), st.ToString("0.0", inv)));
            w.Flush();
        }
    }
}
