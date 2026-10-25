using System;
using System.Collections.Generic;
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

    public ClaudeWidget() : base("claude", 300, 322) { }

    protected override void Tick()
    {
        ReadLimits();
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
        if (topModel.Length > 0) Badge(g, topModel, Theme.Violet, r, 12);
        T(g, "Consumo de Claude", fTitle, Theme.Text, x, 30);
        T(g, scanned ? projects + " proyectos · uso local" : "calculando…", fSmall, Theme.Dim, x, 52);

        // Plan limits: the 5-hour and weekly windows as progress bars (from the sub-meter plugin).
        PlanBar(g, "Límite 5h", pct5h, reset5h, x, 78, w, r);
        PlanBar(g, "Límite semanal", pctWeek, resetWeek, x, 110, w, r);

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

    void PlanBar(Graphics g, string label, double pct, DateTime reset, float x, float y, float w, float r)
    {
        T(g, label, fSmall, Theme.Dim, x, y);
        if (pct < 0) { TR(g, "sin datos", fSmall, Theme.Dim, r, y); Bar(g, x, y + 17, w, 6, double.NaN, Theme.Track); return; }
        Color c = Theme.Load(pct / 100);
        TR(g, "reset " + Until(reset), fSmall, Theme.Dim, r, y);
        TR(g, pct.ToString("0") + " %", fSmall, c, r - 70, y);
        Bar(g, x, y + 17, w, 6, pct / 100, c);
    }
}
