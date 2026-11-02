using System.Diagnostics;

namespace TaskbarMonitor;

/// <summary>Owns the display (taskbar widget or tray icons), the menu, and the background sampler.</summary>
internal sealed class TrayContext : ApplicationContext
{
    public static readonly Color CpuAccent = Color.FromArgb(30, 120, 220);  // blue
    public static readonly Color GpuAccent = Color.FromArgb(40, 170, 80);   // green
    private static readonly int[] Intervals = { 500, 1000, 2000, 5000 };
    private static readonly TimeSpan WidgetRetry = TimeSpan.FromSeconds(5);

    private readonly NotifyIcon _cpuIcon, _gpuIcon, _comboIcon;
    private TaskbarWidget? _widget;
    private DateTime _nextWidgetAttempt = DateTime.MinValue;
    private DisplayMode _mode;

    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _displayMenu;
    private readonly Control _ui; // marshals sampler results onto the UI thread

    private readonly Thread _sampler;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stopping;
    private volatile int _intervalMs;

    // Last values, and what each icon currently shows (to avoid redrawing unchanged icons).
    private Sample _last;
    private string? _cpuIconKey, _gpuIconKey, _comboIconKey;

    private readonly record struct Sample(float? Cpu, float? Gpu, string? Engine, float? GpuTemp);

    public TrayContext()
    {
        _ui = new Control();
        _ui.CreateControl();
        _intervalMs = Intervals.Contains(Settings.IntervalMs) ? Settings.IntervalMs : 1000;
        _mode = Settings.Mode;

        // ----- Menu -----
        _menu = new ContextMenuStrip();
        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };

        _displayMenu = new ToolStripMenuItem("Display");
        AddMode("Taskbar panel (CPU + GPU)", DisplayMode.TaskbarWidget);
        AddMode("One tray icon (CPU + GPU)", DisplayMode.CombinedIcon);
        AddMode("Two tray icons", DisplayMode.TwoIcons);
        AddMode("CPU tray icon only", DisplayMode.CpuIconOnly);
        AddMode("GPU tray icon only", DisplayMode.GpuIconOnly);

        var intervalMenu = new ToolStripMenuItem("Update every");
        foreach (int ms in Intervals)
        {
            var item = new ToolStripMenuItem(ms < 1000 ? $"{ms / 1000.0:0.#} s" : $"{ms / 1000} s") { Tag = ms, Checked = ms == _intervalMs };
            item.Click += (_, _) => SetInterval((int)item.Tag!, intervalMenu);
            intervalMenu.DropDownItems.Add(item);
        }

        var startupItem = new ToolStripMenuItem("Start with Windows") { Checked = Settings.StartWithWindows, CheckOnClick = true };
        startupItem.CheckedChanged += (_, _) =>
        {
            try { Settings.StartWithWindows = startupItem.Checked; }
            catch (Exception ex) { MessageBox.Show("Couldn't update startup setting:\n" + ex.Message, "Taskbar Monitor"); }
        };

        _menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem,
            new ToolStripSeparator(),
            _displayMenu,
            intervalMenu,
            startupItem,
            new ToolStripMenuItem("Open Task Manager", null, (_, _) => OpenTaskManager()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()),
        });

        // ----- Tray icons (created hidden; ApplyMode decides which to show) -----
        _gpuIcon = CreateIcon(IconRenderer.Render(null, GpuAccent), "GPU: --");
        _cpuIcon = CreateIcon(IconRenderer.Render(null, CpuAccent), "CPU: --");
        _comboIcon = CreateIcon(IconRenderer.RenderCombined(null, CpuAccent, null, GpuAccent), "CPU / GPU");
        ApplyMode();

        // ----- Sampling thread -----
        _sampler = new Thread(SampleLoop) { IsBackground = true, Name = "Sampler", Priority = ThreadPriority.BelowNormal };
        _sampler.Start();
    }

    private void AddMode(string text, DisplayMode mode)
    {
        var item = new ToolStripMenuItem(text) { Tag = mode, Checked = mode == _mode };
        item.Click += (_, _) =>
        {
            _mode = mode;
            Settings.Mode = mode;
            foreach (ToolStripMenuItem i in _displayMenu.DropDownItems) i.Checked = (DisplayMode)i.Tag! == mode;
            _nextWidgetAttempt = DateTime.MinValue;
            ApplyMode();
        };
        _displayMenu.DropDownItems.Add(item);
    }

    private NotifyIcon CreateIcon(Icon icon, string text)
    {
        var ni = new NotifyIcon { Icon = icon, Text = text, ContextMenuStrip = _menu, Visible = false };
        ni.DoubleClick += (_, _) => OpenTaskManager();
        return ni;
    }

    /// <summary>Shows whatever the current mode needs. Called on mode change and every tick.</summary>
    private void ApplyMode()
    {
        bool wantWidget = _mode == DisplayMode.TaskbarWidget;

        if (wantWidget)
        {
            // Keep the widget attached; re-create it if Explorer restarted or the taskbar moved.
            if (_widget is not null && !(_widget.IsAlive && _widget.Reposition()))
            {
                _widget.Dispose();
                _widget = null;
            }
            if (_widget is null && DateTime.UtcNow >= _nextWidgetAttempt)
            {
                _widget = TaskbarWidget.TryCreate(_menu, OpenTaskManager);
                if (_widget is null) _nextWidgetAttempt = DateTime.UtcNow + WidgetRetry;
                else _widget.SetValues(_last.Cpu, _last.Gpu, BuildTooltip(_last));
            }
        }
        else if (_widget is not null)
        {
            _widget.Dispose();
            _widget = null;
        }

        // If the panel can't attach (e.g. vertical taskbar, Explorer still starting), fall back to one icon.
        bool widgetFailed = wantWidget && _widget is null;
        SetVisible(_comboIcon, _mode == DisplayMode.CombinedIcon || widgetFailed);
        SetVisible(_cpuIcon, _mode is DisplayMode.TwoIcons or DisplayMode.CpuIconOnly);
        SetVisible(_gpuIcon, _mode is DisplayMode.TwoIcons or DisplayMode.GpuIconOnly);

    }

    private void SetVisible(NotifyIcon ni, bool visible)
    {
        if (ni.Visible == visible) return;
        ni.Visible = visible;
        if (visible) SchedulePromote();
    }

    /// <summary>Windows 11: once Explorer has registered a newly shown icon, ask it to keep it on the taskbar.</summary>
    private static void SchedulePromote()
    {
        var t = new System.Windows.Forms.Timer { Interval = 4000 };
        t.Tick += (_, _) => { t.Dispose(); Settings.PromoteTrayIcons(); };
        t.Start();
    }

    private void SampleLoop()
    {
        using var cpu = new CpuMonitor();
        var gpu = new GpuMonitor();
        using var temp = new GpuTemperature();
        _wake.WaitOne(500); // let the first counter sample settle

        while (!_stopping)
        {
            float? c = cpu.Read();
            float? g = gpu.Read();
            float? t = temp.Read(gpu.BusiestAdapterLuid);
            var sample = new Sample(c, g, gpu.BusiestEngine, t);

            try { _ui.BeginInvoke(() => OnSample(sample)); }
            catch (InvalidOperationException) { break; } // UI is shutting down

            _wake.WaitOne(_intervalMs);
        }
    }

    private void OnSample(Sample s)
    {
        if (_stopping) return;
        _last = s;
        ApplyMode();

        string cpuText = s.Cpu is float c ? $"{Math.Round(c)}%" : "--";
        string gpuText = s.Gpu is float g ? $"{Math.Round(g)}%" : "--";
        string tempText = s.GpuTemp is float t ? $"   ·   {Math.Round(t)} °C" : "";
        _statusItem.Text = $"CPU {cpuText}   ·   GPU {gpuText}{tempText}";

        string tooltip = BuildTooltip(s);
        _widget?.SetValues(s.Cpu, s.Gpu, tooltip);

        if (_cpuIcon.Visible && Changed(ref _cpuIconKey, cpuText))
            SetIcon(_cpuIcon, IconRenderer.Render(s.Cpu, CpuAccent));
        if (_gpuIcon.Visible && Changed(ref _gpuIconKey, gpuText))
            SetIcon(_gpuIcon, IconRenderer.Render(s.Gpu, GpuAccent));
        if (_comboIcon.Visible && Changed(ref _comboIconKey, cpuText + "|" + gpuText))
            SetIcon(_comboIcon, IconRenderer.RenderCombined(s.Cpu, CpuAccent, s.Gpu, GpuAccent));

        _cpuIcon.Text = Truncate($"CPU: {cpuText}");
        _gpuIcon.Text = Truncate(GpuLines(s));
        _comboIcon.Text = Truncate(tooltip);

        static bool Changed(ref string? key, string now)
        {
            if (key == now) return false;
            key = now;
            return true;
        }
    }

    private static string BuildTooltip(Sample s) =>
        $"CPU: {(s.Cpu is float c ? $"{Math.Round(c)}%" : "unavailable")}\n" + GpuLines(s);

    private static string GpuLines(Sample s)
    {
        string text = s.Gpu is float g
            ? $"GPU: {Math.Round(g)}%" + (s.Engine is null ? "" : $" ({s.Engine})")
            : "GPU: unavailable";
        if (s.GpuTemp is float t) text += $"\nGPU temp: {Math.Round(t)} °C";
        return text;
    }

    // NotifyIcon tooltips are limited to 127 characters.
    private static string Truncate(string s) => s.Length <= 127 ? s : s[..127];

    private static void SetIcon(NotifyIcon target, Icon icon)
    {
        var old = target.Icon;
        target.Icon = icon;
        old?.Dispose();
    }

    private void SetInterval(int ms, ToolStripMenuItem parent)
    {
        _intervalMs = ms;
        Settings.IntervalMs = ms;
        foreach (ToolStripMenuItem i in parent.DropDownItems) i.Checked = (int)i.Tag! == ms;
        _wake.Set();
    }

    private static void OpenTaskManager()
    {
        try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); }
        catch { /* user cancelled UAC or Task Manager is disabled */ }
    }

    protected override void ExitThreadCore()
    {
        _stopping = true;
        _wake.Set();

        // Remove the widget first: it's a child of Explorer's taskbar, so keep the UI thread free of waits until it's gone.
        _widget?.Dispose();
        _widget = null;
        _sampler.Join(1000);

        foreach (var ni in new[] { _cpuIcon, _gpuIcon, _comboIcon })
        {
            ni.Visible = false;
            ni.Icon?.Dispose();
            ni.Dispose();
        }
        _menu.Dispose();
        _ui.Dispose();
        base.ExitThreadCore();
    }
}
