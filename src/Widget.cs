using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class Theme
{
    public static readonly Color Bg = Color.FromArgb(18, 20, 26);
    public static readonly Color Text = Color.FromArgb(232, 234, 240);
    public static readonly Color Dim = Color.FromArgb(138, 144, 160);
    public static readonly Color Track = Color.FromArgb(40, 44, 54);
    public static readonly Color Hot = Color.FromArgb(56, 62, 76);
    public static readonly Color Blue = Color.FromArgb(76, 194, 255);
    public static readonly Color Violet = Color.FromArgb(167, 139, 250);
    public static readonly Color Green = Color.FromArgb(61, 220, 151);
    public static readonly Color Amber = Color.FromArgb(255, 190, 70);
    public static readonly Color Red = Color.FromArgb(255, 95, 95);

    // Load colour: green, amber from 60 %, red from 85 %.
    public static Color Load(double frac) { return frac < 0.6 ? Green : frac < 0.85 ? Amber : Red; }
}

static class Settings
{
    static readonly string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskWidgets");
    static readonly string file = Path.Combine(dir, "settings.ini");
    static readonly Dictionary<string, string> d = new Dictionary<string, string>();

    public static void Load()
    {
        try
        {
            foreach (var line in File.ReadAllLines(file))
            {
                int i = line.IndexOf('=');
                if (i > 0) d[line.Substring(0, i)] = line.Substring(i + 1);
            }
        }
        catch { }
    }
    public static void Save()
    {
        try { Directory.CreateDirectory(dir); File.WriteAllLines(file, d.Select(kv => kv.Key + "=" + kv.Value)); }
        catch { }
    }
    public static string Get(string k) { string v; return d.TryGetValue(k, out v) ? v : null; }
    public static void Set(string k, object v) { d[k] = v.ToString(); }
    public static bool Bool(string k, bool def) { var v = Get(k); return v == null ? def : v == "1"; }
    public static void SetBool(string k, bool v) { d[k] = v ? "1" : "0"; }
}

sealed class History
{
    readonly float[] v; int n, head;
    public History(int cap) { v = new float[cap]; }
    public int Count { get { return n; } }
    public int Capacity { get { return v.Length; } }
    public float this[int i] { get { return v[(head - n + i + v.Length) % v.Length]; } }
    public void Push(float x) { v[head] = x; head = (head + 1) % v.Length; if (n < v.Length) n++; }
    public float Max() { float m = 0; for (int i = 0; i < n; i++) m = Math.Max(m, this[i]); return m; }
}

// Borderless, DPI-aware, draggable desktop panel. Subclasses draw in logical 96-dpi units.
abstract class Widget : Form
{
    protected readonly float S;
    protected readonly Font fTitle, fBig, fText, fSmall;
    protected RectangleF Btn;
    protected bool BtnHot;
    // Extra clickable zones with their own actions (close buttons, etc.), rebuilt each Draw.
    readonly List<KeyValuePair<RectangleF, Action>> zones = new List<KeyValuePair<RectangleF, Action>>();
    RectangleF hotZone;
    readonly string id;
    readonly bool hasPos;
    readonly Timer timer = new Timer();
    static readonly StringFormat SF = (StringFormat)StringFormat.GenericTypographic.Clone();

    protected Widget(string id, int w, int h)
    {
        this.id = id;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Theme.Bg;
        Text = "DeskWidgets " + id;
        using (var g = CreateGraphics()) S = g.DpiX / 96f;
        ClientSize = new Size((int)Math.Round(w * S), (int)Math.Round(h * S));

        fTitle = new Font("Segoe UI Semibold", 16f, GraphicsUnit.Pixel);
        fBig = new Font("Segoe UI Semibold", 21f, GraphicsUnit.Pixel);
        fText = new Font("Segoe UI", 13f, GraphicsUnit.Pixel);
        fSmall = new Font("Segoe UI", 11f, GraphicsUnit.Pixel);

        int x, y;
        if (int.TryParse(Settings.Get(id + ".x"), out x) && int.TryParse(Settings.Get(id + ".y"), out y)
            && Screen.AllScreens.Any(s => s.WorkingArea.Contains(x + 20, y + 20)))
        {
            Location = new Point(x, y);
            hasPos = true;
        }

        timer.Interval = 1000;
        timer.Tick += (s, e) => { if (Visible) { Tick(); Invalidate(); } };
        timer.Start();
    }

    public void PlaceDefault(int x, int y) { if (!hasPos) Location = new Point(x, y); }

    protected abstract void Tick();
    protected abstract void Draw(Graphics g);
    protected virtual void OnButton() { }

    // Tool window (no Alt+Tab) that never steals focus from the app you are using.
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000; return cp; }
    }
    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2, border = 0x00362C28; // DWMWCP_ROUND, BGR of Theme.Track-ish
        Native.DwmSetWindowAttribute(Handle, 33, ref round, 4);
        Native.DwmSetWindowAttribute(Handle, 34, ref border, 4);
    }

    protected override void OnShown(EventArgs e) { base.OnShown(e); Tick(); Invalidate(); }

    protected override void WndProc(ref Message m)
    {
        // Desktop mode: keep the widget under every other window.
        if (m.Msg == 0x46 /* WM_WINDOWPOSCHANGING */ && !TopMost)
        {
            var wp = (Native.WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(Native.WINDOWPOS));
            wp.hwndInsertAfter = new IntPtr(1); // HWND_BOTTOM
            wp.flags &= ~4u;                    // clear SWP_NOZORDER
            Marshal.StructureToPtr(wp, m.LParam, false);
        }
        base.WndProc(ref m);
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        Settings.Set(id + ".x", Left);
        Settings.Set(id + ".y", Top);
        Settings.Save();
    }

    protected PointF Logical(Point p) { return new PointF(p.X / S, p.Y / S); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var lp = Logical(e.Location);
        foreach (var z in zones) if (z.Key.Contains(lp)) { z.Value(); Invalidate(); return; }
        if (!Btn.IsEmpty && Btn.Contains(lp)) { OnButton(); Invalidate(); return; }
        Native.ReleaseCapture();
        Native.SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, new IntPtr(2) /* HTCAPTION */, IntPtr.Zero);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var lp = Logical(e.Location);
        RectangleF hz = RectangleF.Empty;
        foreach (var z in zones) if (z.Key.Contains(lp)) { hz = z.Key; break; }
        bool hot = (!Btn.IsEmpty && Btn.Contains(lp)) || !hz.IsEmpty;
        if (hot != BtnHot || hz != hotZone)
        {
            BtnHot = !hz.IsEmpty ? false : (!Btn.IsEmpty && Btn.Contains(lp));
            hotZone = hz;
            Cursor = hot ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (BtnHot || !hotZone.IsEmpty) { BtnHot = false; hotZone = RectangleF.Empty; Cursor = Cursors.Default; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.ScaleTransform(S, S);
        Btn = RectangleF.Empty;
        zones.Clear();
        Draw(g);
    }

    // A small pill button with its own action (e.g. "Cerrar"). Registers a click zone.
    protected void ActionPill(Graphics g, RectangleF r, string label, Color c, Action onClick)
    {
        zones.Add(new KeyValuePair<RectangleF, Action>(r, onClick));
        bool hot = hotZone == r;
        Fill(g, r, r.Height / 2, hot ? Color.FromArgb(70, c) : Color.FromArgb(40, c));
        float tw = TW(g, label, fSmall);
        T(g, label, fSmall, c, r.X + (r.Width - tw) / 2, r.Y + (r.Height - 15) / 2);
    }

    // ---- drawing helpers (logical units) ----

    protected static float TW(Graphics g, string s, Font f) { return g.MeasureString(s, f, PointF.Empty, SF).Width; }

    protected static void T(Graphics g, string s, Font f, Color c, float x, float y)
    {
        using (var b = new SolidBrush(c)) g.DrawString(s, f, b, x, y, SF);
    }
    protected static void TR(Graphics g, string s, Font f, Color c, float right, float y) { T(g, s, f, c, right - TW(g, s, f), y); }

    protected void Eyebrow(Graphics g, string s, float x, float y) { T(g, s, fSmall, Theme.Dim, x, y); }

    protected static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected static void Fill(Graphics g, RectangleF r, float rad, Color c)
    {
        using (var p = Round(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
    }

    protected static void Bar(Graphics g, float x, float y, float w, float h, double frac, Color c)
    {
        Fill(g, new RectangleF(x, y, w, h), h / 2, Theme.Track);
        if (double.IsNaN(frac)) return;
        float fw = (float)(w * Math.Max(0, Math.Min(1, frac)));
        if (fw >= 1) Fill(g, new RectangleF(x, y, Math.Max(fw, h), h), h / 2, c);
    }

    protected static void Spark(Graphics g, RectangleF r, History h, float max, Color c, bool fill)
    {
        if (h.Count < 2 || max <= 0) return;
        float step = r.Width / (h.Capacity - 1);
        var pts = new PointF[h.Count];
        for (int i = 0; i < h.Count; i++)
            pts[i] = new PointF(r.Right - (h.Count - 1 - i) * step, r.Bottom - Math.Min(1f, h[i] / max) * r.Height);
        if (fill)
        {
            using (var path = new GraphicsPath())
            {
                path.AddLines(pts);
                path.AddLine(pts[pts.Length - 1], new PointF(pts[pts.Length - 1].X, r.Bottom));
                path.AddLine(new PointF(pts[pts.Length - 1].X, r.Bottom), new PointF(pts[0].X, r.Bottom));
                path.CloseFigure();
                var gr = RectangleF.Inflate(r, 0, 1);
                using (var b = new LinearGradientBrush(gr, Color.FromArgb(80, c), Color.FromArgb(0, c), 90f)) g.FillPath(b, path);
            }
        }
        using (var p = new Pen(c, 1.5f) { LineJoin = LineJoin.Round }) g.DrawLines(p, pts);
    }

    protected static void Line(Graphics g, float x, float y, float w)
    {
        using (var p = new Pen(Theme.Track, 1f)) g.DrawLine(p, x, y, x + w, y);
    }

    protected void Button(Graphics g, RectangleF r, string label, bool enabled)
    {
        Btn = r;
        Fill(g, r, 7, BtnHot && enabled ? Theme.Hot : Theme.Track);
        float tw = TW(g, label, fText);
        T(g, label, fText, enabled ? Theme.Text : Theme.Dim, r.X + (r.Width - tw) / 2, r.Y + (r.Height - 17) / 2);
    }

    protected void Badge(Graphics g, string s, Color c, float right, float y)
    {
        float w = TW(g, s, fSmall) + 14;
        Fill(g, new RectangleF(right - w, y, w, 18), 9, Color.FromArgb(40, c));
        T(g, s, fSmall, c, right - w + 7, y + 2);
    }

    // Label above a value, used for metric columns.
    protected void Stat(Graphics g, string label, string value, Color c, float x, float y)
    {
        Eyebrow(g, label, x, y);
        T(g, value, fText, c, x, y + 14);
    }
}
