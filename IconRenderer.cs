using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace TaskbarMonitor;

/// <summary>Draws usage numbers into small tray icons.</summary>
internal static class IconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>One metric per icon. The fill rises from the bottom in the level color;
    /// a strip along the top in the metric's own color tells the icons apart.</summary>
    /// <param name="value">0-100, or null to show "--".</param>
    public static Icon Render(float? value, Color fill, Color identity)
    {
        return Draw((g, w, h) =>
        {
            var rect = new Rectangle(0, 0, w, h);
            DrawBackground(g, rect);
            if (value is float v && v > 0)
            {
                int f = (int)Math.Round(h * Math.Min(v, 100) / 100f);
                using var fb = new SolidBrush(Color.FromArgb(210, fill));
                g.FillRectangle(fb, 0, h - f, w, f);
            }
            using (var strip = new SolidBrush(identity))
                g.FillRectangle(strip, 0, 0, w, Math.Max(2, h / 8));
            DrawFittedText(g, Format(value), rect);
        });
    }

    /// <summary>One row per metric (top to bottom). Each fill grows left to right in the level color;
    /// a stripe on the left in the metric's own color tells the rows apart.</summary>
    public static Icon RenderCombined(IReadOnlyList<(float? Value, Color Fill, Color Identity)> rows)
    {
        return Draw((g, w, h) =>
        {
            int n = Math.Max(1, rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                int y0 = h * i / n, y1 = h * (i + 1) / n;
                DrawRow(g, new Rectangle(0, y0, w, y1 - y0), rows[i]);
            }
        });

        static void DrawRow(Graphics g, Rectangle r, (float? Value, Color Fill, Color Identity) row)
        {
            DrawBackground(g, r);
            if (row.Value is float v && v > 0)
            {
                int f = (int)Math.Round(r.Width * Math.Min(v, 100) / 100f);
                using var fb = new SolidBrush(Color.FromArgb(220, row.Fill));
                g.FillRectangle(fb, r.X, r.Y, f, r.Height);
            }
            using (var stripe = new SolidBrush(row.Identity))
                g.FillRectangle(stripe, r.X, r.Y, Math.Max(1, r.Width / 12), r.Height);
            DrawFittedText(g, Format(row.Value), r);
        }
    }

    private static string Format(float? value) => value is float v ? ((int)Math.Round(v)).ToString() : "--";

    private static void DrawBackground(Graphics g, Rectangle r)
    {
        using var bg = new SolidBrush(Color.FromArgb(235, 28, 28, 32));
        g.FillRectangle(bg, r);
    }

    private static Icon Draw(Action<Graphics, int, int> paint)
    {
        Size size = SystemInformation.SmallIconSize; // 16 at 100% DPI, 20/24/32 when scaled
        using var bmp = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            paint(g, size.Width, size.Height);
        }

        // GetHicon allocates an unmanaged handle; clone into a managed Icon and free it.
        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static void DrawFittedText(Graphics g, string text, Rectangle bounds)
    {
        // Start large and shrink until the glyphs fit.
        float px = bounds.Height * 1.3f;
        using var path = new GraphicsPath();
        RectangleF tb;
        while (true)
        {
            path.Reset();
            path.AddString(text, Fonts.Family, Fonts.Style, px, PointF.Empty, StringFormat.GenericTypographic);
            tb = path.GetBounds();
            if ((tb.Width <= bounds.Width - 1 && tb.Height <= bounds.Height - 1) || px < 4) break;
            px -= 0.5f;
        }

        // Center the actual glyph bounds in the area.
        using var m = new Matrix();
        m.Translate(bounds.X + (bounds.Width - tb.Width) / 2f - tb.X,
                    bounds.Y + (bounds.Height - tb.Height) / 2f - tb.Y);
        path.Transform(m);

        float outlineWidth = Math.Max(1.2f, bounds.Height / 10f);
        using (var outline = new Pen(Color.FromArgb(160, 0, 0, 0), outlineWidth) { LineJoin = LineJoin.Round })
            g.DrawPath(outline, path);
        g.FillPath(Brushes.White, path);
    }
}
