using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

// One thing the panel can free: a group to close, kDrive to pause or the Chrome extensions to switch off.
sealed class Suggestion
{
    public string Title, Detail;
    public long Mem;
    public bool Recommended, Checked;
    public Func<bool> Execute;       // runs off the UI thread; false = could not do it
    public string Resume;            // claude --resume command, for closed sessions
    public int State;                // 0 pending, 1 working, 2 done, 3 failed
}

// The list, drawn by hand: whole rows are click targets, with a check box, title, detail and size.
sealed class SuggestionList : Control
{
    public readonly List<Suggestion> Items = new List<Suggestion>();
    public bool Locked;
    public event Action Changed;
    readonly float S;
    readonly Font fHead = new Font("Segoe UI", 11f, GraphicsUnit.Pixel);
    readonly Font fTitle = new Font("Segoe UI Semibold", 14f, GraphicsUnit.Pixel);
    readonly Font fText = new Font("Segoe UI", 12f, GraphicsUnit.Pixel);
    readonly List<KeyValuePair<RectangleF, Suggestion>> rows = new List<KeyValuePair<RectangleF, Suggestion>>();
    Suggestion hot;
    const float RowH = 56, HeadH = 34, Pad = 16;

    public SuggestionList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        using (var g = CreateGraphics()) S = g.DpiX / 96f;
    }

    public void Relayout()
    {
        var rec = Items.Where(i => i.Recommended).ToList();
        var more = Items.Where(i => !i.Recommended).ToList();
        float h = Pad + HeadH + Math.Max(1, rec.Count) * (RowH + 8) + (more.Count > 0 ? HeadH + more.Count * (RowH + 8) : 0) + Pad;
        Height = (int)Math.Ceiling(h * S);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.ScaleTransform(S, S);
        rows.Clear();
        float w = ClientSize.Width / S, y = Pad;
        var rec = Items.Where(i => i.Recommended).ToList();
        var more = Items.Where(i => !i.Recommended).ToList();

        y = Section(g, "RECOMENDADO · SIN RIESGO", y, w);
        if (rec.Count == 0) { Draw(g, "Ahora no hay nada que cerrar sin riesgo. Mira las demás opciones.", fText, Theme.Dim, Pad, y + 4); y += RowH + 8; }
        foreach (var s in rec) { Row(g, s, y, w); y += RowH + 8; }
        if (more.Count > 0)
        {
            y = Section(g, "MÁS OPCIONES · REVÍSALAS ANTES", y, w);
            foreach (var s in more) { Row(g, s, y, w); y += RowH + 8; }
        }
    }

    float Section(Graphics g, string t, float y, float w)
    {
        Draw(g, t, fHead, Theme.Dim, Pad, y + 12);
        return y + HeadH;
    }

    void Row(Graphics g, Suggestion s, float y, float w)
    {
        var r = new RectangleF(Pad, y, w - Pad * 2, RowH);
        rows.Add(new KeyValuePair<RectangleF, Suggestion>(r, s));
        Widget_Fill(g, r, 8, s == hot && !Locked ? Theme.Hot : Color.FromArgb(28, 31, 39));

        // Check box, or the progress mark once it runs.
        var box = new RectangleF(r.X + 14, y + (RowH - 18) / 2, 18, 18);
        if (s.State == 0)
        {
            if (s.Checked) Widget_Fill(g, box, 4, Theme.Blue);
            else using (var p = new Pen(Theme.Dim, 1.5f)) using (var path = Round(box, 4)) g.DrawPath(p, path);
            if (s.Checked) using (var p = new Pen(Theme.Bg, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(p, new[] { new PointF(box.X + 4, box.Y + 9.5f), new PointF(box.X + 7.5f, box.Y + 13), new PointF(box.X + 14, box.Y + 5.5f) });
        }
        else
        {
            string mark = s.State == 1 ? "…" : s.State == 2 ? "✓" : "✕";
            Color c = s.State == 1 ? Theme.Amber : s.State == 2 ? Theme.Green : Theme.Red;
            Draw(g, mark, fTitle, c, box.X + 2, box.Y - 1);
        }

        float tx = box.Right + 14;
        string size = MemWidget.Fmt(s.Mem);
        float sw = g.MeasureString(size, fText).Width;
        float maxW = r.Right - 16 - sw - 12 - tx;
        Draw(g, Fit(g, s.Title, fTitle, maxW), fTitle, s.State == 0 && !s.Checked ? Theme.Dim : Theme.Text, tx, y + 9);
        Draw(g, Fit(g, s.Detail, fText, maxW), fText, Theme.Dim, tx, y + 30);
        Draw(g, size, fText, s.Checked || s.State > 0 ? Theme.Text : Theme.Dim, r.Right - 16 - sw, y + 19);
    }

    static string Fit(Graphics g, string s, Font f, float max)
    {
        if (string.IsNullOrEmpty(s) || g.MeasureString(s, f).Width <= max) return s;
        while (s.Length > 1 && g.MeasureString(s + "…", f).Width > max) s = s.Substring(0, s.Length - 1);
        return s.TrimEnd() + "…";
    }

    static void Draw(Graphics g, string s, Font f, Color c, float x, float y) { using (var b = new SolidBrush(c)) g.DrawString(s, f, b, x, y); }

    static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath(); float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
    static void Widget_Fill(Graphics g, RectangleF r, float rad, Color c) { using (var p = Round(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p); }

    Suggestion At(Point p)
    {
        var lp = new PointF(p.X / S, p.Y / S);
        foreach (var kv in rows) if (kv.Key.Contains(lp)) return kv.Value;
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var h = At(e.Location);
        Cursor = h != null && !Locked ? Cursors.Hand : Cursors.Default;
        if (h != hot) { hot = h; Invalidate(); }
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hot != null) { hot = null; Invalidate(); } }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        var s = At(e.Location);
        if (s == null || Locked || s.State != 0) return;
        s.Checked = !s.Checked;
        Invalidate();
        if (Changed != null) Changed();
    }
}

sealed class FreePanel : Form
{
    readonly Label status, summary;
    readonly SuggestionList list;
    readonly Panel scroll;
    readonly Button go, again;
    readonly Action done;
    bool running, finished;
    ulong ramBefore, ramTotal;

    public FreePanel(Action onDone)
    {
        done = onDone;
        Text = "DeskWidgets · Liberar memoria";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 10f);
        StartPosition = FormStartPosition.CenterScreen;
        // Sizes in 96-dpi units, scaled by hand: the list draws itself at the screen's DPI.
        float k; using (var g = CreateGraphics()) k = g.DpiX / 96f;
        Func<int, int> D = v => (int)Math.Round(v * k);
        ClientSize = new Size(D(600), D(640));
        MinimumSize = new Size(D(480), D(420));

        var top = new Panel { Dock = DockStyle.Top, Height = D(92), Padding = new Padding(D(20), D(16), D(20), D(4)) };
        summary = new Label { Dock = DockStyle.Top, Height = D(40), ForeColor = Theme.Dim };
        status = new Label { Dock = DockStyle.Top, Height = D(32), Font = new Font("Segoe UI Semibold", 14f), ForeColor = Theme.Text };
        top.Controls.Add(summary);
        top.Controls.Add(status);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = D(64), Padding = new Padding(D(20), D(12), D(20), D(14)) };
        go = FlatButton("Liberar", true);
        go.Dock = DockStyle.Right;
        go.Width = D(220);
        go.Click += (s, e) => { if (finished) Close(); else Free(); };
        go.EnabledChanged += (s, e) => go.BackColor = go.Enabled ? Theme.Blue : Theme.Track;
        again = FlatButton("Volver a revisar", false);
        again.Dock = DockStyle.Left;
        again.Width = D(150);
        again.Visible = false;
        again.Click += (s, e) => Reload();
        bottom.Controls.Add(go);
        bottom.Controls.Add(again);

        scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        list = new SuggestionList { Location = new Point(0, 0) };
        list.Changed += UpdateTotal;
        scroll.Controls.Add(list);
        scroll.Resize += (s, e) => list.Width = scroll.ClientSize.Width;

        Controls.Add(scroll);
        Controls.Add(bottom);
        Controls.Add(top);
        list.Width = scroll.ClientSize.Width;
    }

    static Button FlatButton(string text, bool primary)
    {
        var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = primary ? Theme.Blue : Theme.Track, ForeColor = primary ? Theme.Bg : Theme.Text, Cursor = Cursors.Hand };
        if (primary) b.Font = new Font("Segoe UI Semibold", 10f);
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(Theme.Blue) : Theme.Hot;
        return b;
    }

    // Fresh scan every time the panel opens: what was there a minute ago may be gone.
    public void Reload()
    {
        if (running) return;
        finished = false;
        again.Visible = false;
        status.Text = "Revisando qué se puede liberar…";
        status.ForeColor = Theme.Text;
        summary.Text = "";
        go.Enabled = false;
        go.Text = "Liberar";
        list.Locked = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            MemSnapshot s = null;
            try { ChromeExt.Refresh(); s = MemScan.Take(); } catch { }
            BeginInvoke((Action)(() => Present(s)));
        });
    }

    void Present(MemSnapshot s)
    {
        list.Items.Clear();
        list.Locked = false;
        if (s == null) { status.Text = "No se pudo leer la lista de procesos"; status.ForeColor = Theme.Red; again.Visible = true; return; }
        ramBefore = s.RamUsed; ramTotal = s.RamTotal;
        list.Items.AddRange(Build(s));
        list.Relayout();
        status.Text = "Liberar memoria";
        double frac = s.RamTotal > 0 ? (double)s.RamUsed / s.RamTotal : 0;
        summary.Text = "RAM en uso " + (frac * 100).ToString("0") + " % · " + Gb(s.RamUsed) + " de " + Gb(s.RamTotal) + " GB. Marca lo que quieras cerrar.";
        UpdateTotal();
    }

    static string Gb(ulong b) { return (b / 1073741824.0).ToString("0.0"); }

    static List<Suggestion> Build(MemSnapshot s)
    {
        var l = new List<Suggestion>();
        if (s.Orphans != null)
        {
            var g = s.Orphans;
            l.Add(new Suggestion { Title = "MCP huérfanos", Detail = g.Procs.Count(p => p.Name == "node") + " servidores sin su sesión de Claude: nadie los usa", Mem = g.Mem, Recommended = true, Execute = () => Close(g, false) });
        }
        foreach (var b in s.Browsers)
        {
            var g = b;
            l.Add(new Suggestion { Title = g.Title, Detail = g.Detail + " · agent-browser lo reabre si hace falta", Mem = g.Mem, Recommended = true, Execute = () => Close(g, false) });
        }
        foreach (var ss in s.Sessions.Where(x => x.CanClose))
        {
            var g = ss;
            var idle = g.StatusSince == DateTime.MinValue ? TimeSpan.Zero : DateTime.Now - g.StatusSince;
            l.Add(new Suggestion
            {
                Title = "Sesión de Claude Code · " + g.Title,
                Detail = MemWidget.Ago(g.StatusSince) + " · podrás reanudarla (se copia el comando)",
                Mem = g.Mem,
                Recommended = idle.TotalMinutes >= 30,
                Resume = MemWidget.ResumeCommand(g),
                Execute = () => Close(g, false),
            });
        }
        if (s.KDrive != null && !KDrivePause.Paused && s.KDriveExe != null)
        {
            var g = s.KDrive; string exe = s.KDriveExe;
            l.Add(new Suggestion
            {
                Title = "Pausar kDrive 1 h",
                Detail = "Se reabre solo a las " + DateTime.Now.AddHours(1).ToString("HH:mm") + " y sincroniza lo pendiente",
                Mem = g.Mem,
                Execute = () => { KDrivePause.Start(exe, 60); return Close(g, true); },
            });
        }
        if (ChromeExt.Installed && ChromeExt.Global && ChromeExt.Disabling == false && s.ChromeRunning && s.ChromeExtMem > 0)
            l.Add(new Suggestion
            {
                Title = "Apagar las extensiones de Chrome",
                Detail = ChromeExt.Enabled + " activas · vuelven a encenderse desde el widget",
                Mem = s.ChromeExtMem,
                Execute = () => { ChromeExt.Toggle(); Thread.Sleep(2500); return true; },
            });
        // Apps last, never preselected: they may hold unsaved work. Not the terminal running Claude sessions.
        foreach (var a in s.Apps.Where(a => a.CanClose && a.Mem >= 150L << 20).Take(6))
        {
            var g = a;
            if (s.Sessions.Any(x => x.Procs.Count > 0 && Under(x.Procs[0], g))) continue;
            l.Add(new Suggestion
            {
                Title = "Cerrar " + g.Title,
                Detail = g.Title == "Chrome" ? "Guarda las pestañas: al abrirlo podrás restaurarlas" : "Pide guardar si hay cambios sin guardar",
                Mem = g.Mem,
                Execute = () => Close(g, true),
            });
        }
        foreach (var x in l) x.Checked = x.Recommended;
        // Recommended by size; the rest from least to most disruptive (apps last), as built above.
        return l.Where(x => x.Recommended).OrderByDescending(x => x.Mem).Concat(l.Where(x => !x.Recommended)).ToList();
    }

    static bool Under(Proc p, Group g)
    {
        for (var a = p.Parent; a != null; a = a.Parent) if (a.Owner == g) return true;
        return false;
    }

    static bool Close(Group g, bool polite)
    {
        try { MemScan.CloseGroup(g, polite); return true; } catch { return false; }
    }

    void UpdateTotal()
    {
        var sel = list.Items.Where(i => i.Checked).ToList();
        go.Enabled = sel.Count > 0;
        go.Text = sel.Count == 0 ? "Marca algo para liberar" : "Liberar ≈ " + MemWidget.Fmt(sel.Sum(i => i.Mem));
    }

    void Free()
    {
        var sel = list.Items.Where(i => i.Checked && i.State == 0).ToList();
        if (sel.Count == 0 || running) return;
        running = true;
        list.Locked = true;
        go.Enabled = false;
        go.Text = "Liberando…";
        status.Text = "Liberando memoria…";
        var resumes = sel.Where(i => i.Resume != null).Select(i => i.Resume).ToList();
        if (resumes.Count > 0) try { Clipboard.SetText(string.Join("\r\n", resumes)); } catch { }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            foreach (var i in sel)
            {
                var item = i;
                BeginInvoke((Action)(() => { item.State = 1; list.Invalidate(); }));
                bool ok = false;
                try { ok = item.Execute(); } catch { }
                BeginInvoke((Action)(() => { item.State = ok ? 2 : 3; list.Invalidate(); }));
            }
            Thread.Sleep(2500); // let Windows hand the pages back before measuring
            var m = new Native.MEMORYSTATUSEX { dwLength = 64 };
            Native.GlobalMemoryStatusEx(ref m);
            ulong after = m.ullTotalPhys - m.ullAvailPhys;
            BeginInvoke((Action)(() => Finish(sel, resumes.Count, after)));
        });
    }

    void Finish(List<Suggestion> sel, int resumes, ulong after)
    {
        running = false;
        finished = true;
        int ok = sel.Count(i => i.State == 2), failed = sel.Count(i => i.State == 3);
        long freed = ramBefore > after ? (long)(ramBefore - after) : 0;
        status.Text = freed > 0 ? "Liberados " + MemWidget.Fmt(freed) : "Hecho";
        status.ForeColor = failed > 0 ? Theme.Amber : Theme.Green;
        var parts = new List<string>();
        if (ramTotal > 0) parts.Add("RAM " + (100.0 * ramBefore / ramTotal).ToString("0") + " % → " + (100.0 * after / ramTotal).ToString("0") + " %");
        if (failed > 0) parts.Add(failed + " no se pudieron cerrar");
        if (resumes > 0) parts.Add(resumes == 1 ? "comando para reanudar la sesión copiado" : "comandos para reanudar las " + resumes + " sesiones copiados");
        if (KDrivePause.Paused) parts.Add("kDrive vuelve a las " + KDrivePause.ResumeAt.ToString("HH:mm"));
        summary.Text = string.Join(" · ", parts) + ".";
        go.Enabled = true;
        go.Text = "Cerrar";
        again.Visible = true;
        if (done != null) done();
    }
}
