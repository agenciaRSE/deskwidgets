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

    public ClaudeWidget() : base("claude", 300, 274) { }

    protected override void Tick()
    {
        if (!scanning && (!scanned || ticks++ % 15 == 0))
        {
            scanning = true;
            ThreadPool.QueueUserWorkItem(_ => { try { Scan(); } catch { } finally { scanning = false; scanned = true; } });
        }
    }
    int ticks;

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

    static string Str(string s, string key)
    {
        int i = s.IndexOf(key, StringComparison.Ordinal);
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

        // Today hero: tokens left, API-equivalent cost right.
        Eyebrow(g, "HOY", x, 80);
        T(g, Tk(todayTok) + " tok", fBig, Theme.Blue, x, 96);
        TR(g, Usd(todayCost), fBig, Theme.Violet, r, 96);
        T(g, "Entrada " + Tk(todayIn) + "  ·  Salida " + Tk(todayOut) + "  ·  Caché " + Tk(todayCache),
            fSmall, Theme.Dim, x, 126);

        // 14-day sparkline.
        Spark(g, new RectangleF(x, 148, w, 34), spark, spark.Max(), Theme.Green, true);
        T(g, "últimos 14 días", fSmall, Theme.Dim, x, 186);

        Line(g, x, 210, w);
        Stat(g, "ESTA SEMANA", Tk(weekTok) + " · " + Usd(weekCost), Theme.Text, x, 222);
        Stat(g, "ESTE MES", Tk(monthTok) + " · " + Usd(monthCost), Theme.Text, x + 140, 222);
    }
}
