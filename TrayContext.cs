using System.Diagnostics;

namespace TaskbarMonitor;

/// <summary>Owns the display (taskbar panel or tray icons), the menu, and the background sampler.</summary>
internal sealed class TrayContext : ApplicationContext
{
    public static readonly Color CpuAccent = Color.FromArgb(30, 120, 220);  // blue
    public static readonly Color GpuAccent = Color.FromArgb(40, 170, 80);   // green
    public static readonly Color RamAccent = Color.FromArgb(150, 90, 210);  // purple (like Task Manager)
    public static readonly Color TempAccent = Color.FromArgb(225, 85, 150); // pink
    // (Accents only identify tray icons. Fills and rings use the green→red level colors in Levels.cs.)
    private static readonly int[] Intervals = { 500, 1000, 2000, 5000 };
    private static readonly TimeSpan WidgetRetry = TimeSpan.FromSeconds(5);

    private sealed record MetricDef(Metrics Flag, string Label, Color Accent);
    private static readonly MetricDef[] AllMetrics =
    {
        new(Metrics.Cpu, "CPU", CpuAccent),
        new(Metrics.Gpu, "GPU", GpuAccent),
        new(Metrics.Ram, "RAM", RamAccent),
        new(Metrics.Temp, "TEMP", TempAccent),
    };

    private readonly record struct Sample(
        float? Cpu, float? Gpu, string? Engine, float? GpuTemp, float? Ram, double RamUsedGB, double RamTotalGB)
    {
        /// <summary>0-100 for usage; °C for temperature (so a full ring/icon = 100 °C).</summary>
        public float? Value(Metrics m) => m switch
        {
            Metrics.Cpu => Cpu,
            Metrics.Gpu => Gpu,
            Metrics.Ram => Ram,
            _ => GpuTemp,
        };

        public string Text(Metrics m) => Value(m) is float v
            ? (m == Metrics.Temp ? $"{Math.Round(v)}°" : $"{Math.Round(v)}%")
            : "--";

        public Color Level(Metrics m) => Value(m) is float v ? Levels.For(m, v) : Levels.Green;
    }

    private readonly Dictionary<Metrics, NotifyIcon> _icons = new();     // one per metric
    private readonly Dictionary<Metrics, string?> _iconKeys = new();     // what each icon currently shows
    private readonly NotifyIcon _comboIcon;
    private string? _comboKey;
    private TaskbarWidget? _widget;
    private DateTime _nextWidgetAttempt = DateTime.MinValue;
    private DisplayMode _mode;
    private Metrics _shown;

    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _displayMenu;
    private readonly ToolStripMenuItem _showMenu;
    private readonly ToolStripMenuItem _ringNumberItem;
    private int _ringSize;
    private readonly Control _ui; // marshals sampler results onto the UI thread

    private readonly Thread _sampler;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stopping;
    private volatile int _intervalMs;
    private Sample _last;

    public TrayContext()
    {
        _ui = new Control();
        _ui.CreateControl();
        _intervalMs = Intervals.Contains(Settings.IntervalMs) ? Settings.IntervalMs : 1000;
        _mode = Settings.Mode;
        _shown = Settings.Shown;

        // ----- Menu -----
        _menu = new ContextMenuStrip();
        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };

        _displayMenu = new ToolStripMenuItem("Display");
        AddMode("Taskbar panel", DisplayMode.TaskbarWidget);
        AddMode("One tray icon", DisplayMode.CombinedIcon);
        AddMode("Separate tray icons", DisplayMode.SeparateIcons);

        _showMenu = new ToolStripMenuItem("Show");
        foreach (var m in AllMetrics)
        {
            var item = new ToolStripMenuItem(m.Label) { Tag = m.Flag, Checked = _shown.HasFlag(m.Flag) };
            item.Click += (_, _) => ToggleMetric(m.Flag);
            _showMenu.DropDownItems.Add(item);
        }

        _ringNumberItem = new ToolStripMenuItem("Numbers inside rings") { Checked = Settings.RingShowsNumber, CheckOnClick = true };
        _ringNumberItem.CheckedChanged += (_, _) =>
        {
            Settings.RingShowsNumber = _ringNumberItem.Checked;
            if (_widget is not null) _widget.RingShowsNumber = _ringNumberItem.Checked;
        };

        _ringSize = Settings.RingSizePercent;
        var sizeMenu = new ToolStripMenuItem("Size");
        foreach (int pct in new[] { 50, 64, 80, 100 })
        {
            var item = new ToolStripMenuItem($"{pct}%") { Tag = pct, Checked = pct == _ringSize };
            item.Click += (_, _) =>
            {
                _ringSize = pct;
                Settings.RingSizePercent = pct;
                foreach (ToolStripMenuItem i in sizeMenu.DropDownItems) i.Checked = (int)i.Tag! == pct;
                if (_widget is not null) _widget.RingScale = pct / 100f;
            };
            sizeMenu.DropDownItems.Add(item);
        }

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
            _showMenu,
            _ringNumberItem,
            sizeMenu,
            intervalMenu,
            startupItem,
            new ToolStripMenuItem("Open Task Manager", null, (_, _) => OpenTaskManager()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()),
        });

        // ----- Tray icons (created hidden; ApplyMode decides which to show) -----
        // Created right-to-left so they usually appear as CPU, GPU, RAM from left to right.
        foreach (var m in AllMetrics.Reverse())
            _icons[m.Flag] = CreateIcon(IconRenderer.Render(null, Levels.Green, m.Accent), $"{m.Label}: --");
        _comboIcon = CreateIcon(IconRenderer.RenderCombined(ShownMetrics().Select(m => ((float?)null, Levels.Green, m.Accent)).ToList()), "PC Monitor");
        ApplyMode();

        // ----- Sampling thread -----
        _sampler = new Thread(SampleLoop) { IsBackground = true, Name = "Sampler", Priority = ThreadPriority.BelowNormal };
        _sampler.Start();
    }

    private IEnumerable<MetricDef> ShownMetrics() => AllMetrics.Where(m => _shown.HasFlag(m.Flag));

    private void AddMode(string text, DisplayMode mode)
    {
        var item = new ToolStripMenuItem(text) { Tag = mode, Checked = mode == _mode };
        item.Click += (_, _) =>
        {
            _mode = mode;
            Settings.Mode = mode;
            foreach (ToolStripMenuItem i in _displayMenu.DropDownItems) i.Checked = (DisplayMode)i.Tag! == mode;
            _nextWidgetAttempt = DateTime.MinValue;
            Refresh();
        };
        _displayMenu.DropDownItems.Add(item);
    }

    private void ToggleMetric(Metrics flag)
    {
        var next = _shown ^ flag;
        if (next == Metrics.None) return; // keep at least one
        _shown = next;
        Settings.Shown = next;
        foreach (ToolStripMenuItem i in _showMenu.DropDownItems) i.Checked = _shown.HasFlag((Metrics)i.Tag!);
        Refresh();
    }

    /// <summary>Re-apply the display and redraw everything with the last sample.</summary>
    private void Refresh()
    {
        _comboKey = null;
        foreach (var k in _iconKeys.Keys.ToList()) _iconKeys[k] = null;
        ApplyMode();
        OnSample(_last);
    }

    private NotifyIcon CreateIcon(Icon icon, string text)
    {
        var ni = new NotifyIcon { Icon = icon, Text = text, ContextMenuStrip = _menu, Visible = false };
        ni.DoubleClick += (_, _) => OpenTaskManager();
        return ni;
    }

    /// <summary>Shows whatever the current mode needs. Called on changes and every tick.</summary>
    private void ApplyMode()
    {
        bool wantWidget = _mode == DisplayMode.TaskbarWidget;

        if (wantWidget)
        {
            // Keep the panel attached; re-create it if Explorer restarted or the taskbar moved.
            if (_widget is not null && !(_widget.IsAlive && _widget.Reposition()))
            {
                _widget.Dispose();
                _widget = null;
            }
            if (_widget is null && DateTime.UtcNow >= _nextWidgetAttempt)
            {
                _widget = TaskbarWidget.TryCreate(_menu, OpenTaskManager);
                if (_widget is null) _nextWidgetAttempt = DateTime.UtcNow + WidgetRetry;
                else
                {
                    _widget.RingShowsNumber = _ringNumberItem.Checked;
                    _widget.RingScale = _ringSize / 100f;
                    _widget.SetValues(PanelItems(_last), BuildTooltip(_last));
                }
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
        foreach (var (flag, icon) in _icons)
            SetVisible(icon, _mode == DisplayMode.SeparateIcons && _shown.HasFlag(flag));
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
            var ram = RamMonitor.Read();
            var sample = new Sample(c, g, gpu.BusiestEngine, t, ram?.Percent, ram?.UsedGB ?? 0, ram?.TotalGB ?? 0);

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

        string tempText = s.GpuTemp is float t ? $" ({Math.Round(t)} °C)" : "";
        _statusItem.Text = $"CPU {Pct(s.Cpu)}   ·   GPU {Pct(s.Gpu)}{tempText}   ·   RAM {Pct(s.Ram)}";

        string tooltip = BuildTooltip(s);
        _widget?.SetValues(PanelItems(s), tooltip);

        foreach (var m in AllMetrics)
        {
            var icon = _icons[m.Flag];
            if (icon.Visible && Changed(_iconKeys.GetValueOrDefault(m.Flag), s.Text(m.Flag), out var key))
            {
                _iconKeys[m.Flag] = key;
                SetIcon(icon, IconRenderer.Render(s.Value(m.Flag), s.Level(m.Flag), m.Accent));
            }
            icon.Text = Truncate(m.Flag switch
            {
                Metrics.Cpu => CpuLine(s),
                Metrics.Gpu => GpuLines(s),
                Metrics.Ram => RamLine(s),
                _ => s.GpuTemp is float gt ? $"GPU temp: {Math.Round(gt)} °C" : "GPU temp: unavailable",
            });
        }

        var shown = ShownMetrics().ToList();
        string comboNow = string.Join("|", shown.Select(m => m.Label + s.Text(m.Flag)));
        if (_comboIcon.Visible && Changed(_comboKey, comboNow, out var ck))
        {
            _comboKey = ck;
            SetIcon(_comboIcon, IconRenderer.RenderCombined(shown.Select(m => (s.Value(m.Flag), s.Level(m.Flag), m.Accent)).ToList()));
        }
        _comboIcon.Text = Truncate(tooltip);

        static bool Changed(string? before, string now, out string key)
        {
            key = now;
            return before != now;
        }
    }

    private List<PanelItem> PanelItems(Sample s) =>
        ShownMetrics().Select(m => new PanelItem(m.Label, s.Value(m.Flag), s.Text(m.Flag), s.Level(m.Flag))).ToList();

    private static string Pct(float? v) => v is float f ? $"{Math.Round(f)}%" : "--";

    // The tooltip always shows every metric, whatever is selected under "Show".
    private static string BuildTooltip(Sample s) => $"{CpuLine(s)}\n{GpuLines(s)}\n{RamLine(s)}";

    private static string CpuLine(Sample s) => s.Cpu is null ? "CPU: unavailable" : $"CPU: {Pct(s.Cpu)}";

    private static string GpuLines(Sample s)
    {
        string text = s.Gpu is null
            ? "GPU: unavailable"
            : $"GPU: {Pct(s.Gpu)}" + (s.Engine is null ? "" : $" ({s.Engine})");
        if (s.GpuTemp is float t) text += $"\nGPU temp: {Math.Round(t)} °C";
        return text;
    }

    private static string RamLine(Sample s) =>
        s.Ram is null ? "RAM: unavailable" : $"RAM: {Pct(s.Ram)} ({s.RamUsedGB:0.0} / {s.RamTotalGB:0.0} GB)";

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

        // Remove the panel first: it's a child of Explorer's taskbar, so keep the UI thread free of waits until it's gone.
        _widget?.Dispose();
        _widget = null;
        _sampler.Join(1000);

        foreach (var ni in _icons.Values.Append(_comboIcon))
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
