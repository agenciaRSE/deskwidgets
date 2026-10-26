using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

// Reads Claude Code's own transcripts (~/.claude/projects/**/*.jsonl, append-only) and shows token
// usage and the API-equivalent cost per day. Scans incrementally on a worker thread: each file is read
// only from the byte offset reached last time, so steady-state cost is a few KB per refresh.
sealed class ClaudeWidget : Widget
{
    sealed class Day { public double In, Out, CacheR, CacheW, Cost; public readonly Dictionary<string, double> Models = new Dictionary<string, double>(); }

    readonly string root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
    readonly object gate = new object();
    readonly Dictionary<string, long> offsets = new Dictionary<string, long>();
    readonly Dictionary<DateTime, Day> days = new Dictionary<DateTime, Day>();
    int projects;
    volatile bool scanning, scanned;

    // Display fields, recomputed after each scan and read by Draw.
    double todayTok, todayCost, todayIn, todayOut, todayCache, weekTok, weekCost, monthTok, monthCost;
    string topModel = "";
    History spark = new History(14);

    // Plan limits: the 5-hour and weekly windows, as the better-claude "sub-meter" plugin records them.
    // It writes percentUsed + resetsAt to its store whenever Claude Code runs; we just read that file.
    readonly string storeDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "plugins", "store");
    double pct5h = -1, pctWeek = -1;
    DateTime reset5h, resetWeek;

    // Animations ported from the mobile web app (agenciaRSE/claude-usage-mobile): Clawd, the Claude Code
    // mascot, acts out the fullest plan window, and the bars ease toward each new value. Same moods,
    // keyframes and timings as its CSS; all of it off when Windows "Animation effects" is off.
    static readonly Color Clay = Color.FromArgb(217, 119, 87), Sweat = Color.FromArgb(143, 196, 255);
    const float U = 3, CX = 239, CY = 34;    // px per sprite unit; top-left of the sprite's viewBox (-1,-3)
    readonly Font fZ = new Font("Segoe UI", 2.2f, FontStyle.Bold, GraphicsUnit.Pixel);
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer { Interval = 33 };
    readonly bool motion;
    float jumpAt = -10, lastT;
    double show5h = -1, showWeek = -1;       // bar values easing toward pct5h / pctWeek

    public ClaudeWidget() : base("claude", 300, 322)
    {
        bool on = true;
        Native.SystemParametersInfo(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, ref on, 0);
        motion = on;
        anim.Tick += (s, e) => { if (Visible) Animate(); };
        if (motion) anim.Start();
    }

    // ~30 fps. Repaints only the sprite unless a bar is still moving or glowing.
    void Animate()
    {
        float t = Now, k = 1 - (float)Math.Exp(-(t - lastT) / 0.25);
        lastT = t;
        bool bars = Ease(ref show5h, pct5h, k) | Ease(ref showWeek, pctWeek, k);
        if (bars || Math.Max(pct5h, pctWeek) >= 90) Invalidate();
        else Invalidate(Rectangle.Round(new RectangleF((CX - 4) * S, (CY - 4) * S, (15 * U + 8) * S, (12 * U + 8) * S)));
    }

    static bool Ease(ref double v, double target, float k)
    {
        if (target < 0) { v = target; return false; }
        if (v < 0) v = 0;                    // first reading grows from empty, like the web app
        if (Math.Abs(target - v) < 0.05) { v = target; return false; }
        v += (target - v) * k;
        return true;
    }

    float Now { get { return clock.ElapsedMilliseconds / 1000f; } }

    // Click on Clawd: jump and rescan now.
    protected override void OnButton() { jumpAt = Now; ticks = 0; Tick(); }

    // Desktop notifications at 75 / 90 / 100 % and when a window that hit 100 % resets, as the web app's
    // push alerts. Keys are window|resetsAt|threshold, persisted so a restart doesn't repeat them;
    // a new period has a new resetsAt, which re-arms them.
    public event Action<string, string> Notify;
    static readonly int[] Thresholds = { 100, 90, 75 };

    void CheckAlerts()
    {
        string old = Settings.Get("claude.sent") ?? "";
        var sent = new HashSet<string>(old.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
        var keep = new HashSet<string>();
        Alert(sent, keep, "ventana 5 h", pct5h, reset5h);
        Alert(sent, keep, "semana", pctWeek, resetWeek);
        string now = string.Join(";", keep.OrderBy(k => k).ToArray());
        if (now != old) { Settings.Set("claude.sent", now); Settings.Save(); }
    }

    void Alert(HashSet<string> sent, HashSet<string> keep, string name, double pct, DateTime reset)
    {
        if (pct < 0) { keep.UnionWith(sent.Where(k => k.StartsWith(name + "|"))); return; }   // no data: keep state
        string key = name + "|" + reset.Ticks + "|";
        keep.UnionWith(sent.Where(k => k.StartsWith(key)));   // older periods are dropped
        if (reset <= DateTime.UtcNow)
        {
            if (keep.Contains(key + 100) && keep.Add(key + "reset"))
                Raise(char.ToUpper(name[0]) + name.Substring(1) + " reiniciada", "Vuelves a tener uso disponible en Claude Code.");
            return;
        }
        int t = Thresholds.FirstOrDefault(x => pct >= x);
        if (t == 0 || keep.Contains(key + t)) return;
        foreach (int x in Thresholds) if (x <= t) keep.Add(key + x);   // 70 -> 95 % sends one alert, not two
        string at = "";
        if (reset != DateTime.MaxValue)
        {
            var local = reset.ToLocalTime();
            at = " Reinicia a las " + local.ToString(reset - DateTime.UtcNow > TimeSpan.FromHours(20) ? "ddd HH:mm" : "HH:mm",
                new System.Globalization.CultureInfo("es-ES")) + ".";
        }
        if (t >= 100) Raise("Límite de " + name + " alcanzado", "Se ha agotado la " + name + "." + at);
        else Raise("Claude Code: " + name + " al " + pct.ToString("0") + " %", "Has pasado el " + t + " %." + at);
    }

    void Raise(string title, string body) { var h = Notify; if (h != null) h(title, body); }

    protected override void Tick()
    {
        ReadLimits();
        CheckAlerts();
        if (!scanning && (!scanned || ticks++ % 15 == 0))
        {
            scanning = true;
            ThreadPool.QueueUserWorkItem(_ => { try { Scan(); } catch { } finally { scanning = false; scanned = true; } });
        }
    }
    int ticks;

    // sub-meter_*.json: { "limits": [ { kind, percentUsed, resetsAt }, ... ] }. Tiny file, parsed by hand.
    void ReadLimits()
    {
        try
        {
            var f = Directory.GetFiles(storeDir, "sub-meter_*.json").FirstOrDefault();
            if (f == null) return;
            string s = File.ReadAllText(f);
            ReadWindow(s, "five_hour", ref pct5h, ref reset5h);
            ReadWindow(s, "seven_day", ref pctWeek, ref resetWeek);
        }
        catch { }
    }

    static void ReadWindow(string s, string kind, ref double pct, ref DateTime reset)
    {
        int k = s.IndexOf("\"" + kind + "\"", StringComparison.Ordinal);
        if (k < 0) return;
        double p = Grab(s, "\"percentUsed\":", k);
        string ts = QuotedAfter(s, "\"resetsAt\"", k);
        DateTime r = DateTime.MaxValue;
        if (ts != null) try { r = DateTimeOffset.Parse(ts).UtcDateTime; } catch { }
        // A window past its reset time has rolled over: show it empty.
        pct = r <= DateTime.UtcNow ? 0 : p;
        reset = r;
    }

    // First double-quoted string after a bare key, tolerant of ": " spacing.
    static string QuotedAfter(string s, string key, int from)
    {
        int i = s.IndexOf(key, from, StringComparison.Ordinal);
        if (i < 0) return null;
        int q1 = s.IndexOf('"', i + key.Length);
        if (q1 < 0) return null;
        int q2 = s.IndexOf('"', q1 + 1);
        return q2 < 0 ? null : s.Substring(q1 + 1, q2 - q1 - 1);
    }

    static string Until(DateTime resetUtc)
    {
        var t = resetUtc - DateTime.UtcNow;
        if (t.TotalSeconds <= 0) return "ahora";
        if (t.TotalDays >= 1) return (int)t.TotalDays + "d " + t.Hours + "h";
        if (t.TotalHours >= 1) return t.Hours + "h " + t.Minutes + "m";
        return Math.Max(1, (int)t.TotalMinutes) + "m";
    }

    // USD per 1M tokens: input, output, cache write (5 min), cache read. Family picked by name.
    static double[] Rates(string model)
    {
        if (model.IndexOf("opus", StringComparison.Ordinal) >= 0) return new[] { 15.0, 75.0, 18.75, 1.5 };
        if (model.IndexOf("sonnet", StringComparison.Ordinal) >= 0) return new[] { 3.0, 15.0, 3.75, 0.3 };
        return new[] { 0.8, 4.0, 1.0, 0.08 }; // fable / haiku-class
    }

    void Scan()
    {
        string[] files;
        try { files = Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories); }
        catch { return; }
        var projDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        lock (gate)
        {
            foreach (var f in files)
            {
                projDirs.Add(Path.GetDirectoryName(f));
                long off;
                offsets.TryGetValue(f, out off);
                long len;
                try { len = new FileInfo(f).Length; } catch { continue; }
                if (len < off) off = 0;          // file rotated/truncated: reparse
                if (len == off) continue;        // nothing new

                string chunk;
                try
                {
                    using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        fs.Seek(off, SeekOrigin.Begin);
                        using (var sr = new StreamReader(fs, Encoding.UTF8)) chunk = sr.ReadToEnd();
                    }
                }
                catch { continue; }

                int cut = chunk.LastIndexOf('\n');
                if (cut < 0) continue;           // no complete line yet
                string whole = chunk.Substring(0, cut + 1);
                offsets[f] = off + Encoding.UTF8.GetByteCount(whole);

                foreach (var line in whole.Split('\n'))
                    if (line.Length > 0 && line.IndexOf("\"usage\"", StringComparison.Ordinal) >= 0)
                        Accumulate(line);
            }
            projects = projDirs.Count;
            Recompute();
        }
    }

    static long Grab(string s, string key, int from)
    {
        int i = s.IndexOf(key, from, StringComparison.Ordinal);
        if (i < 0) return 0;
        i += key.Length;
        while (i < s.Length && s[i] == ' ') i++;
        int j = i;
        while (j < s.Length && s[j] >= '0' && s[j] <= '9') j++;
        long v;
        return long.TryParse(s.Substring(i, j - i), out v) ? v : 0;
    }

    static string Str(string s, string key, int from = 0)
    {
        int i = s.IndexOf(key, from, StringComparison.Ordinal);
        if (i < 0) return null;
        i += key.Length;
        int j = s.IndexOf('"', i);
        return j < 0 ? null : s.Substring(i, j - i);
    }

    void Accumulate(string line)
    {
        string model = Str(line, "\"model\":\"");
        string ts = Str(line, "\"timestamp\":\"");
        if (model == null || ts == null || model.IndexOf("claude", StringComparison.Ordinal) < 0) return;

        // Count only the top-level usage totals, not the per-iteration repeats that follow.
        int u = line.IndexOf("\"usage\":{", StringComparison.Ordinal);
        if (u < 0) return;
        int end = line.IndexOf("\"iterations\"", u, StringComparison.Ordinal);
        string slice = end > u ? line.Substring(u, end - u) : line.Substring(u);

        double inp = Grab(slice, "\"input_tokens\":", 0);
        double cw = Grab(slice, "\"cache_creation_input_tokens\":", 0);
        double cr = Grab(slice, "\"cache_read_input_tokens\":", 0);
        double outp = Grab(slice, "\"output_tokens\":", 0);
        if (inp + cw + cr + outp == 0) return;

        DateTime date;
        try { date = DateTimeOffset.Parse(ts).LocalDateTime.Date; } catch { return; }

        Day d;
        if (!days.TryGetValue(date, out d)) days[date] = d = new Day();
        d.In += inp; d.Out += outp; d.CacheW += cw; d.CacheR += cr;
        var r = Rates(model);
        d.Cost += (inp * r[0] + outp * r[1] + cw * r[2] + cr * r[3]) / 1e6;

        double mt;
        d.Models[model] = (d.Models.TryGetValue(model, out mt) ? mt : 0) + inp + cw + cr + outp;
    }

    static double Tok(Day d) { return d.In + d.Out + d.CacheR + d.CacheW; }

    void Recompute()
    {
        DateTime today = DateTime.Today, monthStart = new DateTime(today.Year, today.Month, 1);
        todayTok = todayCost = todayIn = todayOut = todayCache = weekTok = weekCost = monthTok = monthCost = 0;
        topModel = "";

        Day t;
        if (days.TryGetValue(today, out t))
        {
            todayIn = t.In; todayOut = t.Out; todayCache = t.CacheR + t.CacheW;
            todayTok = Tok(t); todayCost = t.Cost;
            if (t.Models.Count > 0) topModel = Pretty(t.Models.OrderByDescending(kv => kv.Value).First().Key);
        }
        foreach (var kv in days)
        {
            if (kv.Key > today.AddDays(-7)) { weekTok += Tok(kv.Value); weekCost += kv.Value.Cost; }
            if (kv.Key >= monthStart) { monthTok += Tok(kv.Value); monthCost += kv.Value.Cost; }
        }
        var s = new History(14);
        for (int i = 13; i >= 0; i--)
        {
            Day d;
            s.Push(days.TryGetValue(today.AddDays(-i), out d) ? (float)Tok(d) : 0f);
        }
        spark = s;
    }

    static string Pretty(string model)
    {
        string m = model.Replace("claude-", "").Replace("[1m]", "");
        var p = m.Split('-');
        return p.Length < 2 ? m : p[0] + " " + string.Join(".", p.Skip(1).ToArray());
    }

    static string Tk(double v)
    {
        if (v >= 1e9) return (v / 1e9).ToString("0.0") + " G";
        if (v >= 1e6) return (v / 1e6).ToString("0.0") + " M";
        if (v >= 1e3) return (v / 1e3).ToString("0") + " K";
        return v.ToString("0");
    }
    static string Usd(double v) { return "≈ " + v.ToString(v < 100 ? "0.00" : "0") + " $"; }

    protected override void Draw(Graphics g)
    {
        const float x = 16, w = 268, r = x + w;

        Eyebrow(g, "CLAUDE", x, 14);
        if (topModel.Length > 0) Badge(g, topModel, Theme.Violet, HeaderRight, 12);
        T(g, "Consumo de Claude", fTitle, Theme.Text, x, 30);
        T(g, scanned ? projects + " proyectos · uso local" : "calculando…", fSmall, Theme.Dim, x, 52);
        DrawClawd(g);

        // Plan limits: the 5-hour and weekly windows as progress bars (from the sub-meter plugin).
        PlanBar(g, "Límite 5h", pct5h, motion ? show5h : pct5h, reset5h, x, 78, w, r);
        PlanBar(g, "Límite semanal", pctWeek, motion ? showWeek : pctWeek, resetWeek, x, 110, w, r);

        // Today hero: tokens left, API-equivalent cost right.
        Eyebrow(g, "HOY", x, 148);
        T(g, Tk(todayTok) + " tok", fBig, Theme.Blue, x, 164);
        TR(g, Usd(todayCost), fBig, Theme.Violet, r, 164);
        T(g, "Entrada " + Tk(todayIn) + "  ·  Salida " + Tk(todayOut) + "  ·  Caché " + Tk(todayCache),
            fSmall, Theme.Dim, x, 194);

        // 14-day sparkline.
        Spark(g, new RectangleF(x, 214, w, 32), spark, spark.Max(), Theme.Green, true);
        T(g, "últimos 14 días", fSmall, Theme.Dim, x, 250);

        Line(g, x, 274, w);
        Stat(g, "ESTA SEMANA", Tk(weekTok) + " · " + Usd(weekCost), Theme.Text, x, 286);
        Stat(g, "ESTE MES", Tk(monthTok) + " · " + Usd(monthCost), Theme.Text, x + 140, 286);
    }

    // shown: the eased value the bar and its % count up through; pct: the real one, for colour.
    void PlanBar(Graphics g, string label, double pct, double shown, DateTime reset, float x, float y, float w, float r)
    {
        T(g, label, fSmall, Theme.Dim, x, y);
        if (pct < 0) { TR(g, "sin datos", fSmall, Theme.Dim, r, y); Bar(g, x, y + 17, w, 6, double.NaN, Theme.Track); return; }
        Color c = Theme.Load(pct / 100);
        TR(g, "reset " + Until(reset), fSmall, Theme.Dim, r, y);
        TR(g, Math.Max(0, shown).ToString("0") + " %", fSmall, c, r - 70, y);
        // >= 90 %: the bar glows (CSS glow, brightness 1.35 at mid-cycle, 1.2 s).
        Bar(g, x, y + 17, w, 6, Math.Max(0, shown) / 100, motion && pct >= 90 ? Mix(c, Color.White, 0.35f * Pulse(Now, 1.2f)) : c);
    }

    // 0 -> 1 -> 0 eased over one period, the shape of CSS "0%,100% {a} 50% {b}" with ease-in-out.
    static float Pulse(float t, float period) { return (1 - (float)Math.Cos(2 * Math.PI * t / period)) / 2; }
    static float Frac(float t, float period) { return t / period - (float)Math.Floor(t / period); }
    static Color Mix(Color a, Color b, float f)
    {
        return Color.FromArgb(a.A, (int)(a.R + (b.R - a.R) * f), (int)(a.G + (b.G - a.G) * f), (int)(a.B + (b.B - a.B) * f));
    }

    // Clawd as in the web app's SVG (viewBox -1 -3 15 12). Mood from the fullest window:
    // calm < 40 floats and blinks, busy walks, warn >= 75 sweats, danger >= 90 shakes and waves,
    // full = 100 sleeps (zzz), no data = greyed out. Click = jump + rescan.
    void DrawClawd(Graphics g)
    {
        Btn = new RectangleF(CX, CY, 15 * U, 12 * U);
        double top = Math.Max(pct5h, pctWeek);
        bool full = top >= 100, danger = top >= 90 && !full, warn = top >= 75 && top < 90, busy = top >= 40 && top < 75;
        float t = motion ? Now : 0, j = (t - jumpAt) / 0.6f;
        Color body = top < 0 ? Mix(Clay, Theme.Dim, 0.65f) : BtnHot ? Mix(Clay, Color.White, 0.15f) : Clay;

        var saved = g.Save();
        g.TranslateTransform(CX + U, CY + 3 * U);     // origin at sprite (0,0)
        g.ScaleTransform(U, U);

        if (!full)
        {
            float s = Pulse(t, 3);                    // shadow breathes with the bob
            using (var b = new SolidBrush(Color.FromArgb((int)(255 * (0.45f - 0.15f * s)), 0, 0, 0)))
                g.FillEllipse(b, 6.5f - 3 * (1 - 0.15f * s), 8.3f, 6 * (1 - 0.15f * s), 0.9f);
        }
        if (full)
        {
            float p = Frac(t, 3), a = motion ? (p < 0.3f ? p / 0.3f : 1 - (p - 0.3f) / 0.7f) : 1;
            using (var b = new SolidBrush(Color.FromArgb((int)(255 * a), Theme.Dim)))
            {
                g.DrawString("z", fZ, b, 10.5f + p, -2.6f + 1 - 2.5f * p);
                g.DrawString("z", fZ, b, 12 + p, -4.1f + 1 - 2.5f * p);
            }
        }
        if ((warn || danger) && motion)
        {
            float p = Frac(t, danger ? 1 : 1.6f), e = p * p, a = p < 0.15f ? p / 0.15f : 1 - (p - 0.15f) / 0.85f;
            Fill(g, new RectangleF(11.6f + 0.6f * e, 0.2f + 3.2f * e, 0.8f, 1.2f), 0.4f, Color.FromArgb((int)(255 * a), Sweat));
        }

        // Whole body: bob (faster when busier), shake, breathe, or the click jump on top of everything.
        float dy = 0, sy = 1, rot = 0, dx = 0;
        if (j >= 0 && j < 1)
        {
            if (j < 0.35f) { dy = -2.4f * j / 0.35f; sy = 1 + 0.05f * j / 0.35f; }
            else if (j < 0.7f) { float q = (j - 0.35f) / 0.35f; dy = -2.4f * (1 - q); sy = 1.05f - 0.13f * q; }
            else sy = 0.92f + 0.08f * (j - 0.7f) / 0.3f;
        }
        else if (danger) { float w = (float)Math.Sin(2 * Math.PI * t / 0.35f); dx = -0.25f * w; rot = -1.5f * w; }
        else if (full) { float b = Pulse(t, 4); sy = 1 - 0.05f * b; dy = 0.15f * b; }
        else dy = -0.45f * Pulse(t, busy ? 1.6f : warn ? 1.2f : 3);

        g.TranslateTransform(6.5f + dx, 4.5f + dy);   // transform-origin: centre of the body
        g.RotateTransform(rot);
        g.ScaleTransform(1, sy);
        g.TranslateTransform(-6.5f, -4.5f);

        using (var b = new SolidBrush(body))
        {
            // Legs step while walking; the pairs alternate so it reads as a walk.
            float step = busy ? 0.8f : warn ? 0.6f : 0, l1 = step > 0 ? -0.5f * Pulse(t, step) : 0, l2 = step > 0 ? -0.5f * Pulse(t + step / 2, step) : 0;
            g.FillRectangle(b, 2, 6 + l1, 1, 2); g.FillRectangle(b, 4, 6 + l2, 1, 2);
            g.FillRectangle(b, 8, 6 + l1, 1, 2); g.FillRectangle(b, 10, 6 + l2, 1, 2);
            g.FillRectangle(b, 2, 1, 9, 5);
            float wave = danger ? -1.2f * Pulse(t, 0.5f) : 0, wave2 = danger ? -1.2f - wave : 0;
            g.FillRectangle(b, 0, 3 + wave, 2, 2);
            g.FillRectangle(b, 11, 3 + wave2, 2, 2);
        }
        using (var b = new SolidBrush(Theme.Bg))
        {
            if (full) { g.FillRectangle(b, 3.6f, 3.2f, 1.8f, 0.5f); g.FillRectangle(b, 7.6f, 3.2f, 1.8f, 0.5f); }
            else
            {
                // Blink: 4.2 s cycle, eyes squash to 10 % at 95 % of it.
                float p = Frac(t, 4.2f), ey = !motion || p < 0.93f ? 1 : p < 0.95f ? 1 - 0.9f * (p - 0.93f) / 0.02f : 0.1f + 0.9f * (p - 0.95f) / 0.05f;
                float h = 2 * ey, y0 = 3.2f - 1.2f * ey;   // squash around 60 % of the eye height
                g.FillRectangle(b, 4, y0, 1, h); g.FillRectangle(b, 8, y0, 1, h);
            }
        }
        g.Restore(saved);
    }
}
