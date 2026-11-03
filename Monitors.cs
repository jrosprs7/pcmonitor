using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TaskbarMonitor;

/// <summary>Total CPU usage, matching what Task Manager shows.</summary>
internal sealed class CpuMonitor : IDisposable
{
    private readonly PerformanceCounter? _counter;

    public CpuMonitor()
    {
        // "% Processor Utility" is what Task Manager uses on Windows 8+ (accounts for turbo/frequency).
        // Fall back to the classic "% Processor Time" if it is not available.
        _counter = TryCreate("Processor Information", "% Processor Utility", "_Total")
                ?? TryCreate("Processor", "% Processor Time", "_Total");
        _counter?.NextValue(); // first read always returns 0; prime it
    }

    private static PerformanceCounter? TryCreate(string category, string counter, string instance)
    {
        try
        {
            if (!PerformanceCounterCategory.CounterExists(counter, category)) return null;
            var c = new PerformanceCounter(category, counter, instance, readOnly: true);
            c.NextValue();
            return c;
        }
        catch { return null; }
    }

    /// <summary>Returns 0-100, or null if CPU counters are unavailable.</summary>
    public float? Read()
    {
        if (_counter is null) return null;
        try { return Math.Clamp(_counter.NextValue(), 0f, 100f); }
        catch { return null; }
    }

    public void Dispose() => _counter?.Dispose();
}

/// <summary>
/// GPU usage using the Windows "GPU Engine" performance counters (Windows 10 1709+).
/// Works with NVIDIA, AMD and Intel GPUs. Mirrors Task Manager: per-engine utilization is
/// summed across all processes, and the busiest engine is reported as the GPU's usage.
/// The whole category is read in one call per tick (cheap), instead of polling hundreds of
/// per-process counters individually (expensive).
/// </summary>
internal sealed class GpuMonitor
{
    private const string Category = "GPU Engine";
    private const string CounterName = "Utilization Percentage";

    private readonly PerformanceCounterCategory? _category;
    private Dictionary<string, CounterSample> _previous = new();

    public string? BusiestEngine { get; private set; }

    /// <summary>LUID of the adapter that owns the busiest engine (used to pick which GPU's temperature to show).</summary>
    public long? BusiestAdapterLuid { get; private set; }

    public GpuMonitor()
    {
        try
        {
            if (PerformanceCounterCategory.Exists(Category))
                _category = new PerformanceCounterCategory(Category);
        }
        catch { _category = null; }
    }

    /// <summary>Returns 0-100, or null if GPU counters are unavailable.</summary>
    public float? Read()
    {
        if (_category is null) return null;

        InstanceDataCollection samples;
        try
        {
            var all = _category.ReadCategory();
            if (!all.Contains(CounterName)) return null;
            samples = all[CounterName];
        }
        catch { return null; }

        var current = new Dictionary<string, CounterSample>(samples.Count);
        var perEngine = new Dictionary<string, float>();

        foreach (InstanceData d in samples.Values)
        {
            current[d.InstanceName] = d.Sample;
            // A rate needs two samples; new processes show up from the next tick on.
            if (!_previous.TryGetValue(d.InstanceName, out var prev)) continue;

            float value = CounterSample.Calculate(prev, d.Sample);
            if (float.IsNaN(value) || value < 0) continue;

            string engine = EngineKey(d.InstanceName);
            perEngine[engine] = perEngine.GetValueOrDefault(engine) + value;
        }

        _previous = current;

        if (perEngine.Count == 0) return 0f;

        var busiest = perEngine.MaxBy(kv => kv.Value);
        BusiestEngine = busiest.Value > 0.5f ? EngineType(busiest.Key) : null;
        BusiestAdapterLuid = ParseLuid(busiest.Key);
        return Math.Clamp(busiest.Value, 0f, 100f);
    }

    // Instance names look like: pid_1234_luid_0x00000000_0x0000C6D3_phys_0_eng_0_engtype_3D
    // The engine identity is everything from "luid_" onward (i.e. the same engine across processes).
    private static string EngineKey(string instance)
    {
        int i = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? instance[i..] : instance;
    }

    // "luid_0x00000000_0x0000C6D3_..." -> high part, low part
    private static long? ParseLuid(string engineKey)
    {
        var parts = engineKey.Split('_');
        if (parts.Length < 3 || parts[0] != "luid") return null;
        try
        {
            uint high = Convert.ToUInt32(parts[1], 16);
            uint low = Convert.ToUInt32(parts[2], 16);
            return ((long)high << 32) | low;
        }
        catch { return null; }
    }

    private static string EngineType(string engineKey)
    {
        int i = engineKey.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? engineKey[(i + "engtype_".Length)..] : "GPU";
    }
}

/// <summary>
/// GPU temperature via the same kernel graphics API Task Manager uses (D3DKMTQueryAdapterInfo with
/// KMTQAITYPE_ADAPTERPERFDATA). Needs Windows 10 2004+ and a WDDM 2.4+ driver. Dedicated NVIDIA/AMD
/// GPUs usually report it; many integrated GPUs report 0 (= unsupported), in which case we return null.
/// </summary>
internal sealed class GpuTemperature : IDisposable
{
    private const int KMTQAITYPE_ADAPTERPERFDATA = 62;
    private static readonly TimeSpan ReenumerateInterval = TimeSpan.FromSeconds(30);

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTERINFO
    {
        public uint hAdapter;
        public uint LuidLowPart;
        public int LuidHighPart;
        public uint NumOfSources;
        public int bPrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ENUMADAPTERS2
    {
        public uint NumAdapters;
        public IntPtr pAdapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTER_PERFDATA
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOC;
        public ulong MemoryBandwidth;
        public ulong PCIEBandwidth;
        public uint FanRPM;
        public uint Power;
        public uint Temperature; // tenths of a degree Celsius
        public byte PowerStateOverride;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER
    {
        public uint hAdapter;
    }

    [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 args);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO args);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER args);

    private readonly List<(uint Handle, long Luid)> _adapters = new();
    private DateTime _lastEnum = DateTime.MinValue;
    private bool _apiMissing;

    /// <summary>Temperature in °C of the given adapter (or the hottest one), or null if unavailable.</summary>
    public float? Read(long? preferredLuid)
    {
        if (_apiMissing) return null;

        try
        {
            // Re-enumerate occasionally to pick up driver restarts / external GPUs.
            if (DateTime.UtcNow - _lastEnum > ReenumerateInterval)
            {
                Enumerate();
                _lastEnum = DateTime.UtcNow;
            }

            float? preferred = null, hottest = null;
            foreach (var (handle, luid) in _adapters)
            {
                float? t = Query(handle);
                if (t is null) continue;
                if (luid == preferredLuid) preferred = t;
                if (hottest is null || t > hottest) hottest = t;
            }
            return preferred ?? hottest;
        }
        catch (EntryPointNotFoundException) { _apiMissing = true; return null; } // very old Windows
        catch (DllNotFoundException) { _apiMissing = true; return null; }
    }

    private static float? Query(uint handle)
    {
        var perf = new D3DKMT_ADAPTER_PERFDATA { PhysicalAdapterIndex = 0 };
        int size = Marshal.SizeOf<D3DKMT_ADAPTER_PERFDATA>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(perf, buf, false);
            var q = new D3DKMT_QUERYADAPTERINFO
            {
                hAdapter = handle,
                Type = KMTQAITYPE_ADAPTERPERFDATA,
                pPrivateDriverData = buf,
                PrivateDriverDataSize = (uint)size,
            };
            if (D3DKMTQueryAdapterInfo(ref q) != 0) return null; // not supported by this adapter/driver
            perf = Marshal.PtrToStructure<D3DKMT_ADAPTER_PERFDATA>(buf);
            if (perf.Temperature == 0 || perf.Temperature > 1500) return null; // 0 = not reported
            return perf.Temperature / 10f;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private void Enumerate()
    {
        CloseAll();

        var e = new D3DKMT_ENUMADAPTERS2();
        D3DKMTEnumAdapters2(ref e); // first call (null buffer) just reports the adapter count
        if (e.NumAdapters == 0 || e.NumAdapters > 64) return;

        int itemSize = Marshal.SizeOf<D3DKMT_ADAPTERINFO>();
        e.pAdapters = Marshal.AllocHGlobal(itemSize * (int)e.NumAdapters);
        try
        {
            if (D3DKMTEnumAdapters2(ref e) != 0) return;
            for (int i = 0; i < e.NumAdapters; i++)
            {
                var info = Marshal.PtrToStructure<D3DKMT_ADAPTERINFO>(e.pAdapters + i * itemSize);
                long luid = ((long)(uint)info.LuidHighPart << 32) | info.LuidLowPart;
                _adapters.Add((info.hAdapter, luid));
            }
        }
        finally { Marshal.FreeHGlobal(e.pAdapters); }
    }

    private void CloseAll()
    {
        foreach (var (handle, _) in _adapters)
        {
            var c = new D3DKMT_CLOSEADAPTER { hAdapter = handle };
            try { D3DKMTCloseAdapter(ref c); } catch { }
        }
        _adapters.Clear();
    }

    public void Dispose() => CloseAll();
}

/// <summary>Physical memory in use, matching Task Manager's "In use" (total minus available).</summary>
internal static class RamMonitor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    /// <summary>Returns (percent used, GB used, GB total), or null if the call fails.</summary>
    public static (float Percent, double UsedGB, double TotalGB)? Read()
    {
        var s = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref s) || s.ullTotalPhys == 0) return null;

        const double GB = 1024.0 * 1024 * 1024; // Task Manager's "GB" is really GiB
        ulong used = s.ullTotalPhys - Math.Min(s.ullAvailPhys, s.ullTotalPhys);
        return ((float)(100.0 * used / s.ullTotalPhys), used / GB, s.ullTotalPhys / GB);
    }
}

/// <summary>
/// CPU temperature. Windows has no built-in, no-admin API for the real CPU sensor (tools like HWiNFO
/// load a kernel driver), so this tries, in order:
///   1. LibreHardwareMonitor / OpenHardwareMonitor, if running: they publish real sensor readings over WMI.
///   2. ACPI thermal zones ("Thermal Zone Information" performance counters): no admin needed. Usually a
///      sensible value on laptops, but on many desktops it's missing or a fixed placeholder, so it's
///      labelled as an estimate.
/// </summary>
internal sealed class CpuTemperature
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(30);
    private static readonly (string Namespace, string Name)[] Monitors =
    {
        (@"root\LibreHardwareMonitor", "LibreHardwareMonitor"),
        (@"root\OpenHardwareMonitor", "OpenHardwareMonitor"),
    };
    // Preferred sensor names (Intel package, AMD Tctl/Tdie), checked in this order.
    private static readonly string[] Preferred = { "CPU Package", "Core (Tctl/Tdie)", "Core (Tdie)", "Core (Tctl)", "Package" };

    private (string Namespace, string Name)? _monitor;
    private DateTime _nextProbe = DateTime.MinValue;
    private PerformanceCounterCategory? _zones;
    private bool _zonesChecked;

    /// <summary>Where the last reading came from, e.g. "LibreHardwareMonitor" or "estimate".</summary>
    public string? Source { get; private set; }

    public float? Read()
    {
        if (_monitor is null && DateTime.UtcNow >= _nextProbe)
        {
            _nextProbe = DateTime.UtcNow + ProbeInterval;
            _monitor = null;
            foreach (var m in Monitors)
                if (ReadWmi(m.Namespace) is not null) { _monitor = m; break; }
        }

        if (_monitor is { } mon)
        {
            if (ReadWmi(mon.Namespace) is float t)
            {
                Source = mon.Name;
                return t;
            }
            _monitor = null; // the monitor app was closed; probe again later
        }

        if (ReadThermalZone() is float z)
        {
            Source = "estimate";
            return z;
        }

        Source = null;
        return null;
    }

    private static float? ReadWmi(string ns)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(ns,
                "SELECT Name, Value, Identifier FROM Sensor WHERE SensorType='Temperature'");
            var cpu = new List<(string Name, float Value)>();
            foreach (System.Management.ManagementBaseObject o in searcher.Get())
            {
                using (o)
                {
                    string id = o["Identifier"] as string ?? "";
                    string name = o["Name"] as string ?? "";
                    if (!id.Contains("cpu", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Contains("Distance", StringComparison.OrdinalIgnoreCase)) continue; // "distance to TjMax", not a temperature
                    float v = Convert.ToSingle(o["Value"]);
                    if (v > 0 && v < 150) cpu.Add((name, v));
                }
            }
            if (cpu.Count == 0) return null;
            foreach (var p in Preferred)
                foreach (var s in cpu)
                    if (s.Name.Equals(p, StringComparison.OrdinalIgnoreCase)) return s.Value;
            return cpu.Max(s => s.Value);
        }
        catch { return null; } // namespace missing (monitor not running) or access denied
    }

    private float? ReadThermalZone()
    {
        if (!_zonesChecked)
        {
            _zonesChecked = true;
            try
            {
                if (PerformanceCounterCategory.Exists("Thermal Zone Information"))
                    _zones = new PerformanceCounterCategory("Thermal Zone Information");
            }
            catch { }
        }
        if (_zones is null) return null;

        try
        {
            var data = _zones.ReadCategory();
            bool precise = data.Contains("High Precision Temperature"); // tenths of a kelvin (Windows 10+)
            var samples = data[precise ? "High Precision Temperature" : "Temperature"];
            if (samples is null) return null;

            float? best = null;
            bool bestIsCpu = false;
            foreach (InstanceData d in samples.Values)
            {
                float kelvin = d.RawValue / (precise ? 10f : 1f);
                float c = kelvin - 273.15f;
                if (c <= 0 || c >= 125) continue;
                bool isCpu = d.InstanceName.Contains("CPU", StringComparison.OrdinalIgnoreCase);
                // Prefer a zone named after the CPU; otherwise take the hottest zone.
                if (best is null || (isCpu && !bestIsCpu) || (isCpu == bestIsCpu && c > best))
                {
                    best = c;
                    bestIsCpu = isCpu;
                }
            }
            return best;
        }
        catch { return null; }
    }
}
