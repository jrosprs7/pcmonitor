using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TaskbarMonitor;

/// <summary>One ring gauge on the taskbar panel.</summary>
/// <param name="Value">Ring fill, 0-100 (null = unavailable).</param>
/// <param name="ValueText">Text shown in the ring when "number" mode is on, e.g. "23%" or "61°".</param>
/// <param name="RingColor">Fill color for the ring (green/yellow/orange/red by level).</param>
internal readonly record struct PanelItem(string Label, float? Value, string ValueText, Color RingColor);

/// <summary>
/// A small rectangular panel placed on the taskbar, just left of the notification area, holding
/// one ring gauge per metric. Each ring fills clockwise from 12 o'clock with the usage, and the
/// middle shows the label (CPU, GPU, RAM, TEMP) or, optionally, the number.
///
/// Windows doesn't let tray icons be wider than a square, so this is a real child window of the
/// taskbar (the same technique tools like TrafficMonitor use). It moves and hides with the taskbar,
/// including auto-hide and full-screen apps. Only the primary horizontal taskbar is supported;
/// if it can't attach, the caller falls back to a tray icon.
/// </summary>
internal sealed class TaskbarWidget : Control
{
    private readonly IntPtr _taskbar;
    private readonly ContextMenuStrip _menu;
    private readonly Action _onDoubleClick;
    private readonly HoverTip _tip = new();

    private IReadOnlyList<PanelItem> _items = Array.Empty<PanelItem>();
    private string _tipText = "";
    private bool _lightTheme;
    private Color _key; // transparent color for the layered window
    private int _lastHeight = -1;
    private int _ring;          // ring diameter in pixels
    private float _textPx;      // font size for the text inside the rings
    private bool _ringShowsNumber;

    // Windows 10: the taskbar button area (ReBarWindow32) is shrunk so it doesn't run under us.
    private IntPtr _rebar;
    private bool _rebarShrunk;

    private TaskbarWidget(IntPtr taskbar, ContextMenuStrip menu, Action onDoubleClick)
    {
        _taskbar = taskbar;
        _menu = menu;
        _onDoubleClick = onDoubleClick;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        UpdateTheme();
    }

    /// <summary>Creates and attaches the widget, or returns null if the taskbar isn't usable.</summary>
    public static TaskbarWidget? TryCreate(ContextMenuStrip menu, Action onDoubleClick)
    {
        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return null;

        var w = new TaskbarWidget(taskbar, menu, onDoubleClick);
        try
        {
            w.CreateControl();
            if (w.IsHandleCreated && w.Reposition()) return w;
        }
        catch { /* fall through */ }
        w.Dispose();
        return null;
    }

    /// <summary>True while the taskbar we're attached to still exists (Explorer hasn't restarted).</summary>
    public bool IsAlive => IsHandleCreated && IsWindow(_taskbar) && FindWindow("Shell_TrayWnd", null) == _taskbar;

    public void SetValues(IReadOnlyList<PanelItem> items, string tooltip)
    {
        bool layoutChanged = items.Count != _items.Count;
        bool changed = layoutChanged || !items.Select(Key).SequenceEqual(_items.Select(Key));
        _items = items;
        _tipText = tooltip;
        if (layoutChanged) Reposition();
        if (changed) Invalidate();
        if (_tip.Visible) _tip.SetText(_tipText, this);

        static (string, string, int?, Color) Key(PanelItem i) => (i.Label, i.ValueText, i.Value is float f ? (int)Math.Round(f) : null, i.RingColor);
    }

    public bool RingShowsNumber
    {
        get => _ringShowsNumber;
        set { _ringShowsNumber = value; Invalidate(); }
    }

    /// <summary>Re-reads the taskbar layout and moves the widget. Returns false if it can't be placed.</summary>
    public bool Reposition()
    {
        if (!IsAlive) return false;
        if (!GetWindowRect(_taskbar, out RECT tb)) return false;
        if (tb.Height > tb.Width) return false; // vertical taskbar (Windows 10) - not supported

        IntPtr tray = FindWindowEx(_taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray == IntPtr.Zero || !GetWindowRect(tray, out RECT tr) || tr.Width <= 0) return false;

        if (UpdateTheme() || tb.Height != _lastHeight) ApplyFont(tb.Height);

        int h = tb.Height;
        int n = Math.Max(1, _items.Count);
        int w = PadX(h) * 2 + n * _ring + (n - 1) * RingGap(h);
        int screenX = tr.Left - w - Math.Max(2, h / 12);
        if (screenX <= tb.Left + tb.Width / 3) return false; // not enough room

        // Child window coordinates are relative to the taskbar's client area.
        var pt = new POINT { X = screenX, Y = tb.Top };
        MapWindowPoints(IntPtr.Zero, _taskbar, ref pt, 1);

        ShrinkWin10TaskButtons(screenX);

        // HWND_TOP keeps us above the Windows 11 taskbar's XAML content.
        SetWindowPos(Handle, HWND_TOP, pt.X, pt.Y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        return true;
    }

    private static int PadX(int h) => Math.Max(2, h / 16);
    private static int PadY(int h) => Math.Max(2, h / 12);
    private static int RingGap(int h) => Math.Max(3, h / 10);

    private void ApplyFont(int taskbarHeight)
    {
        _lastHeight = taskbarHeight;
        _ring = taskbarHeight - PadY(taskbarHeight) * 2; // ~40px on a 48px Windows 11 taskbar

        // One text size for every ring: the largest at which the widest text still fits inside.
        using var family = new FontFamily("Segoe UI");
        using var path = new GraphicsPath();
        float px = _ring * 0.45f;
        while (px > 6)
        {
            float widest = 0;
            foreach (var t in new[] { "CPU", "GPU", "RAM", "TEMP", "100%", "100°" })
            {
                path.Reset();
                path.AddString(t, family, (int)FontStyle.Bold, px, PointF.Empty, StringFormat.GenericTypographic);
                widest = Math.Max(widest, path.GetBounds().Width);
            }
            if (widest <= _ring * 0.80f) break;
            px -= 0.5f;
        }
        _textPx = px;
    }

    /// <returns>true if the theme changed.</returns>
    private bool UpdateTheme()
    {
        bool light = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light = key?.GetValue("SystemUsesLightTheme") is int i && i != 0;
        }
        catch { }

        // The key color is made fully transparent. Picking one close to the taskbar's own color makes
        // the anti-aliased text edges blend in.
        Color newKey = light ? Color.FromArgb(254, 254, 253) : Color.FromArgb(2, 2, 3);
        bool changed = light != _lightTheme || newKey != _key;
        _lightTheme = light;
        _key = newKey;
        if (changed && IsHandleCreated)
        {
            ApplyLayered();
            Invalidate();
        }
        return changed;
    }

    // ----- Win32 window setup -----

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (_taskbar != IntPtr.Zero)
            {
                cp.Parent = _taskbar;
                cp.Style = WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS;
                cp.ExStyle |= WS_EX_LAYERED;
            }
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyLayered();
    }

    private void ApplyLayered() =>
        SetLayeredWindowAttributes(Handle, (uint)ColorTranslator.ToWin32(_key), 255, LWA_COLORKEY);

    // ----- Painting -----

    protected override void OnPaintBackground(PaintEventArgs e) { /* everything is drawn in OnPaint */ }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_key);
        if (_ring <= 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color text = _lightTheme ? Color.FromArgb(20, 20, 20) : Color.FromArgb(245, 245, 245);
        Color dim = _lightTheme ? Color.FromArgb(150, 150, 150) : Color.FromArgb(120, 120, 120);
        Color track = _lightTheme ? Color.FromArgb(210, 210, 210) : Color.FromArgb(70, 70, 70);

        int h = Height;
        float thick = Math.Max(3f, _ring * 0.15f);

        using var family = new FontFamily("Segoe UI");
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var box = new RectangleF(PadX(h) + i * (_ring + RingGap(h)), PadY(h), _ring, _ring);
            var arc = RectangleF.Inflate(box, -thick / 2, -thick / 2);

            // Track, then the filled part clockwise from 12 o'clock.
            using (var p = new Pen(track, thick)) g.DrawEllipse(p, arc);
            if (item.Value is float v && v > 0)
            {
                float sweep = 360f * Math.Min(v, 100f) / 100f;
                using var p = new Pen(item.RingColor, thick);
                if (sweep >= 360f) g.DrawEllipse(p, arc);
                else
                {
                    p.StartCap = p.EndCap = LineCap.Round;
                    g.DrawArc(p, arc, -90f, Math.Max(sweep, 1f));
                }
            }

            // Label (or number) centred on its actual glyph bounds.
            string centre = _ringShowsNumber ? item.ValueText : item.Label;
            using var path = new GraphicsPath();
            path.AddString(centre, family, (int)FontStyle.Bold, _textPx, PointF.Empty, StringFormat.GenericTypographic);
            RectangleF tb = path.GetBounds();
            using (var m = new Matrix())
            {
                m.Translate(box.X + (box.Width - tb.Width) / 2f - tb.X, box.Y + (box.Height - tb.Height) / 2f - tb.Y);
                path.Transform(m);
            }
            using var brush = new SolidBrush(item.Value is null ? dim : text);
            g.FillPath(brush, path);
        }
    }

    // ----- Mouse -----

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Right) return;
        _tip.Hide();
        // Same trick NotifyIcon uses so the menu closes when you click elsewhere.
        SetForegroundWindow(_menu.Handle);
        _menu.Show(Cursor.Position);
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        _onDoubleClick();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (!_menu.Visible) _tip.SetText(_tipText, this, show: true);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _tip.Hide();
    }

    // ----- Windows 10 taskbar button area -----

    private void ShrinkWin10TaskButtons(int widgetScreenLeft)
    {
        // Windows 11's taskbar is XAML-based and centred; leave it alone.
        if (FindWindowEx(_taskbar, IntPtr.Zero, "Windows.UI.Composition.DesktopWindowContentBridge", null) != IntPtr.Zero)
            return;

        if (_rebar == IntPtr.Zero || !IsWindow(_rebar))
            _rebar = FindWindowEx(_taskbar, IntPtr.Zero, "ReBarWindow32", null);
        if (_rebar == IntPtr.Zero || !IsWindowVisible(_rebar) || !GetWindowRect(_rebar, out RECT rb)) return;

        int maxRight = widgetScreenLeft - 2;
        if (rb.Right <= maxRight) return; // Explorer hasn't grown it back; nothing to do

        var pt = new POINT { X = rb.Left, Y = rb.Top };
        MapWindowPoints(IntPtr.Zero, _taskbar, ref pt, 1);
        MoveWindow(_rebar, pt.X, pt.Y, maxRight - rb.Left, rb.Height, true);
        _rebarShrunk = true;
    }

    private void RestoreWin10TaskButtons()
    {
        if (!_rebarShrunk || !IsWindow(_rebar) || !IsWindow(_taskbar)) return;
        IntPtr tray = FindWindowEx(_taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray == IntPtr.Zero || !GetWindowRect(tray, out RECT tr) || !GetWindowRect(_rebar, out RECT rb)) return;
        var pt = new POINT { X = rb.Left, Y = rb.Top };
        MapWindowPoints(IntPtr.Zero, _taskbar, ref pt, 1);
        MoveWindow(_rebar, pt.X, pt.Y, tr.Left - rb.Left, rb.Height, true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            RestoreWin10TaskButtons();
            _tip.Dispose();
        }
        base.Dispose(disposing);
    }

    // ----- Interop -----

    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000;
    private const int WS_EX_LAYERED = 0x00080000;
    private const uint LWA_COLORKEY = 0x1;
    private const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string lpszClass, string? lpszWindow);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int MapWindowPoints(IntPtr from, IntPtr to, ref POINT pt, uint count);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
}

/// <summary>
/// A tooltip that updates live while the mouse is over the widget. (The standard WinForms ToolTip
/// doesn't work reliably on a window hosted inside another process's window.)
/// </summary>
internal sealed class HoverTip : Form
{
    private string _text = "";

    public HoverTip()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Font = SystemFonts.StatusFont ?? SystemFonts.DefaultFont;
        DoubleBuffered = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/ | 0x00000008 /*TOPMOST*/;
            return cp;
        }
    }

    public void SetText(string text, Control anchor, bool show = false)
    {
        _text = text;
        Size textSize = TextRenderer.MeasureText(_text, Font);
        int pad = Math.Max(6, Font.Height / 2);
        Size = new Size(textSize.Width + pad * 2, textSize.Height + pad * 2);

        // Place above the widget (or below if the taskbar is at the top of the screen).
        Rectangle a = anchor.RectangleToScreen(anchor.ClientRectangle);
        Rectangle screen = Screen.FromRectangle(a).Bounds;
        int x = Math.Clamp(a.Left + (a.Width - Width) / 2, screen.Left + 4, screen.Right - Width - 4);
        int y = a.Top > screen.Top + screen.Height / 2 ? a.Top - Height - 8 : a.Bottom + 8;
        Location = new Point(x, y);

        Invalidate();
        if (show && !Visible) Show();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        bool light = SystemInformation.HighContrast ? false : IsLight();
        Color bg = light ? Color.FromArgb(249, 249, 249) : Color.FromArgb(43, 43, 43);
        Color fg = light ? Color.Black : Color.White;
        Color border = light ? Color.FromArgb(200, 200, 200) : Color.FromArgb(80, 80, 80);

        e.Graphics.Clear(bg);
        using (var p = new Pen(border)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        int pad = Math.Max(6, Font.Height / 2);
        TextRenderer.DrawText(e.Graphics, _text, Font, new Point(pad, pad), fg, bg);
    }

    private static bool IsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int i && i != 0;
        }
        catch { return false; }
    }
}
