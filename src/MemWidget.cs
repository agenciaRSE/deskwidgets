using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// "Disable All Extensions" (Chrome Web Store) toggles every other extension when its toolbar action fires.
// The widget presses the shortcut the user gave that action in chrome://extensions/shortcuts.
static class ChromeExt
{
    public const string Id = "ailfldpmpboolaihojfagmmfbhcgohne";
    public const string ShortcutsUrl = "chrome://extensions/shortcuts";
    static readonly string Profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\User Data\Default");
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static DateTime prefStamp, secureStamp;

    public static bool Installed;
    public static int Enabled = -1;    // other extensions enabled in the Default profile
    public static string Shortcut;     // e.g. "Ctrl+Shift+8"
    public static bool? Disabling;     // true = the extension is keeping the others off

    public static void Refresh()
    {
        try
        {
            var sp = Path.Combine(Profile, "Secure Preferences");
            var st = File.GetLastWriteTimeUtc(sp);
            if (st != secureStamp)
            {
                secureStamp = st;
                var d = Json.Deserialize<Dictionary<string, object>>(ReadShared(sp));
                var settings = Dig(d, "extensions", "settings") as Dictionary<string, object>;
                int n = 0; bool inst = false;
                if (settings != null)
                    foreach (var kv in settings)
                    {
                        var e = kv.Value as Dictionary<string, object>;
                        if (e == null) continue;
                        object loc, dis;
                        if (!e.TryGetValue("location", out loc)) continue;
                        if (kv.Key == Id) { inst = true; continue; }
                        int l = Convert.ToInt32(loc);
                        if (l == 5 || l == 10) continue; // built into Chrome
                        bool off = e.TryGetValue("disable_reasons", out dis) && dis is System.Collections.ICollection && ((System.Collections.ICollection)dis).Count > 0;
                        if (!off) n++;
                    }
                Installed = inst;
                Enabled = n;
            }

            var pp = Path.Combine(Profile, "Preferences");
            st = File.GetLastWriteTimeUtc(pp);
            if (st != prefStamp)
            {
                prefStamp = st;
                var d = Json.Deserialize<Dictionary<string, object>>(ReadShared(pp));
                var cmds = Dig(d, "extensions", "commands") as Dictionary<string, object>;
                Shortcut = null;
                if (cmds != null)
                    foreach (var kv in cmds)
                    {
                        var c = kv.Value as Dictionary<string, object>;
                        if (c == null || !kv.Key.StartsWith("windows:") || !Id.Equals(c.ContainsKey("extension") ? c["extension"] : null)) continue;
                        Shortcut = kv.Key.Substring(8);
                    }
            }
            Disabling = ReadState();
        }
        catch { }
    }

    // Last "isDisablingOtherExts" written to the extension's chrome.storage.sync LevelDB log.
    static bool? ReadState()
    {
        try
        {
            var dir = Path.Combine(Profile, @"Sync Extension Settings\" + Id);
            foreach (var f in Directory.GetFiles(dir, "*.log").Concat(Directory.GetFiles(dir, "*.ldb").OrderByDescending(x => x)))
            {
                var m = Regex.Matches(Encoding.UTF8.GetString(ReadBytes(f)), "isDisablingOtherExts.{0,3}?(true|false)");
                if (m.Count > 0) return m[m.Count - 1].Groups[1].Value == "true";
            }
        }
        catch { }
        return null;
    }

    static object Dig(Dictionary<string, object> d, params string[] path)
    {
        object o = d;
        foreach (var k in path) { var x = o as Dictionary<string, object>; if (x == null || !x.TryGetValue(k, out o)) return null; }
        return o;
    }

    static byte[] ReadBytes(string f)
    {
        using (var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var b = new byte[s.Length]; int r = 0;
            while (r < b.Length) { int k = s.Read(b, r, b.Length - r); if (k <= 0) break; r += k; }
            return b;
        }
    }
    static string ReadShared(string f) { return Encoding.UTF8.GetString(ReadBytes(f)); }

    // Chrome does not allow a global scope for "Activate the extension" (_execute_action), so the shortcut only
    // works in a focused Chrome window. Bring each window of your Chrome to the front in turn (most recent first),
    // press the shortcut and stop when the extension's storage changes: a window of a profile without the
    // extension does nothing. Then hand focus back. Runs off the UI thread; true = the state changed.
    public static bool Toggle(ICollection<int> browserPids)
    {
        var keys = Keys();
        if (keys == null || browserPids == null || browserPids.Count == 0) return false;
        var wins = new List<IntPtr>();
        MemNative.EnumWindows((h, l) =>
        {
            int pid; MemNative.GetWindowThreadProcessId(h, out pid);
            if (browserPids.Contains(pid) && MemNative.IsWindowVisible(h) && !MemNative.IsIconic(h) && MemNative.GetWindowTextLength(h) > 0)
            {
                var cls = new StringBuilder(64); MemNative.GetClassName(h, cls, 64);
                if (cls.ToString() == "Chrome_WidgetWin_1") wins.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        if (wins.Count == 0) return false;

        var prev = MemNative.GetForegroundWindow();
        bool changed = false;
        try
        {
            foreach (var w in wins.Take(4))
            {
                string before = Signature();
                MemNative.SetForegroundWindow(w);
                for (int i = 0; i < 10 && MemNative.GetForegroundWindow() != w; i++) Thread.Sleep(30);
                if (MemNative.GetForegroundWindow() != w) continue;
                Thread.Sleep(60);
                Press(keys);
                for (int i = 0; i < 15 && !changed; i++) { Thread.Sleep(100); changed = Signature() != before; }
                if (changed) break;
            }
        }
        finally { if (prev != IntPtr.Zero) MemNative.SetForegroundWindow(prev); }
        return changed;
    }

    static List<byte> Keys()
    {
        if (string.IsNullOrEmpty(Shortcut)) return null;
        var keys = new List<byte>();
        foreach (var t in Shortcut.Split('+'))
        {
            string k = t.Trim();
            if (k == "Ctrl") keys.Add(0x11);
            else if (k == "Shift") keys.Add(0x10);
            else if (k == "Alt") keys.Add(0x12);
            else if (k.Length == 1 && char.IsLetterOrDigit(k[0])) keys.Add((byte)char.ToUpperInvariant(k[0]));
            else return null;
        }
        return keys;
    }

    static void Press(List<byte> keys)
    {
        foreach (var k in keys) MemNative.keybd_event(k, 0, 0, UIntPtr.Zero);
        for (int i = keys.Count - 1; i >= 0; i--) MemNative.keybd_event(keys[i], 0, 2 /* KEYUP */, UIntPtr.Zero);
    }

    // Size + write time of the extension's storage files: changes as soon as the toggle saves its new state.
    static string Signature()
    {
        try
        {
            var dir = Path.Combine(Profile, @"Sync Extension Settings\" + Id);
            return string.Join("|", new DirectoryInfo(dir).GetFiles().Select(f => f.Name + ":" + f.Length + ":" + f.LastWriteTimeUtc.Ticks));
        }
        catch { return ""; }
    }
}

sealed class MemWidget : Widget
{
    const float X = 16, W = 268, R = X + W;
    readonly History ramH = new History(200);   // 10 min at one sample every 3 s
    readonly object snapLock = new object();
    MemSnapshot snap = new MemSnapshot();
    volatile bool scanning;
    int ticks;
    // Keys of groups being closed, for immediate feedback until they disappear.
    readonly HashSet<string> busy = new HashSet<string>();
    string flash; DateTime flashUntil;
    DateTime extBusyUntil;

    public MemWidget() : base("mem", 300, 420) { }

    protected override void Tick()
    {
        if (ticks++ % 3 == 0) Scan();
        if (ticks % 5 == 1) ThreadPool.QueueUserWorkItem(_ => ChromeExt.Refresh());
        KDrivePause.Check(snap.KDrive != null);
    }

    void Scan()
    {
        if (scanning) return;
        scanning = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var s = MemScan.Take();
                lock (snapLock)
                {
                    snap = s;
                    if (s.RamTotal > 0) ramH.Push((float)(100.0 * s.RamUsed / s.RamTotal));
                    var keys = new HashSet<string>(s.Apps.Concat(s.Sessions).Concat(s.Browsers).Select(g => g.Key));
                    if (s.Orphans != null) keys.Add(s.Orphans.Key);
                    if (s.KDrive != null) keys.Add(s.KDrive.Key);
                    busy.RemoveWhere(k => !keys.Contains(k));
                }
                BeginInvoke((Action)(() => CheckAlert(s)));
            }
            catch { }
            finally { scanning = false; }
        });
    }

    public static string Fmt(long bytes)
    {
        double mb = bytes / 1048576.0;
        return mb >= 1000 ? (mb / 1024).ToString("0.0") + " GB" : mb.ToString("0") + " MB";
    }
    static string Gb(ulong bytes) { return (bytes / 1073741824.0).ToString("0.0"); }

    public static string Ago(DateTime since)
    {
        if (since == DateTime.MinValue) return "inactiva";
        var t = DateTime.Now - since;
        if (t.TotalMinutes < 1) return "inactiva ahora";
        if (t.TotalHours < 1) return "inactiva " + (int)t.TotalMinutes + " min";
        if (t.TotalDays < 1) return "inactiva " + (int)t.TotalHours + " h";
        return "inactiva " + (int)t.TotalDays + " d";
    }

    // Only idle sessions can be closed: busy/shell/waiting ones are in the middle of something.
    static string StatusText(string st)
    {
        switch (st)
        {
            case "busy": return "trabajando";
            case "shell": return "ejecutando un comando";
            case "waiting": return "esperando tu respuesta";
            case "unknown": return "sin datos de sesión";
            default: return st;
        }
    }

    string Fit(Graphics g, string s, Font f, float max)
    {
        if (TW(g, s, f) <= max) return s;
        while (s.Length > 1 && TW(g, s + "…", f) > max) s = s.Substring(0, s.Length - 1);
        return s.TrimEnd() + "…";
    }

    // One line: title, dim detail if it fits, size, and an action pill.
    void Row(Graphics g, float y, Group grp, string detail, string pill, Color pc, Action act)
    {
        bool isBusy; lock (snapLock) isBusy = busy.Contains(grp.Key);
        float sizeRight = R - 66;
        string size = Fmt(grp.Mem);
        float titleMax = sizeRight - TW(g, size, fSmall) - 10 - X;
        string title = Fit(g, grp.Title, fText, titleMax);
        T(g, title, fText, Theme.Text, X, y);
        float dx = X + TW(g, title, fText) + 6;
        float room = sizeRight - TW(g, size, fSmall) - 8 - dx;
        if (!string.IsNullOrEmpty(detail) && room > 30) T(g, Fit(g, detail, fSmall, room), fSmall, Theme.Dim, dx, y + 2);
        TR(g, size, fSmall, Theme.Dim, sizeRight, y + 2);
        var r = new RectangleF(R - 58, y - 1, 58, 20);
        if (isBusy) T(g, "cerrando…", fSmall, Theme.Dim, r.X + 2, y + 2);
        else if (act != null) ActionPill(g, r, pill, pc, act);
        else if (pill != null) { float tw = TW(g, pill, fSmall); T(g, pill, fSmall, Theme.Dim, r.X + (r.Width - tw) / 2, y + 2); }
    }

    protected override void Draw(Graphics g)
    {
        MemSnapshot s; lock (snapLock) s = snap;
        double frac = s.RamTotal > 0 ? (double)s.RamUsed / s.RamTotal : 0;

        Eyebrow(g, "MEMORIA", X, 14);
        if (flash != null && DateTime.Now < flashUntil) TR(g, flash, fSmall, Theme.Green, HeaderRight, 14);
        else if (s.CommitLimit > 0) TR(g, "comprometida " + Gb(s.CommitTotal) + " / " + Gb(s.CommitLimit) + " GB", fSmall, Theme.Dim, HeaderRight, 14);
        T(g, Gb(s.RamUsed) + " / " + Gb(s.RamTotal) + " GB", fTitle, Theme.Text, X, 30);
        TR(g, (frac * 100).ToString("0") + " %", fTitle, Theme.Load(frac), R, 30);
        Spark(g, new RectangleF(X, 56, W, 34), ramH, 100, Theme.Violet, true);
        T(g, "últimos 10 min", fSmall, Theme.Dim, X, 92);
        TR(g, "libres " + Gb(s.RamTotal - s.RamUsed) + " GB", fSmall, Theme.Dim, R, 92);

        float y = 118;
        // Biggest apps (Claude Code sessions, test browsers and kDrive have their own rows below).
        Eyebrow(g, "QUÉ OCUPA MÁS", X, y); y += 18;
        foreach (var a in s.Apps.Take(5))
        {
            var grp = a;
            if (grp.CanClose) Row(g, y, grp, grp.Detail, "Cerrar", Theme.Dim, () => AskCloseApp(grp));
            else Row(g, y, grp, grp.Detail, null, Theme.Dim, null);
            y += 24;
        }

        // Claude Code sessions: the main thing to close when memory runs short.
        if (s.Sessions.Count > 0)
        {
            y += 8;
            long total = s.Sessions.Sum(x => x.Mem);
            Eyebrow(g, "CLAUDE CODE · " + s.Sessions.Count + (s.Sessions.Count == 1 ? " SESIÓN" : " SESIONES"), X, y);
            TR(g, Fmt(total), fSmall, Theme.Dim, R, y);
            y += 18;
            foreach (var ss in s.Sessions.Take(6))
            {
                var grp = ss;
                // Idle for half an hour or more: the obvious thing to close, so it stands out.
                bool stale = grp.StatusSince != DateTime.MinValue && (DateTime.Now - grp.StatusSince).TotalMinutes >= 30;
                if (grp.CanClose) Row(g, y, grp, Ago(grp.StatusSince), "Cerrar", stale ? Theme.Amber : Theme.Dim, () => AskCloseSession(grp));
                else Row(g, y, grp, StatusText(grp.Status), "en uso", Theme.Green, null);
                y += 24;
            }
            if (s.Sessions.Count > 6) { T(g, "+" + (s.Sessions.Count - 6) + " más", fSmall, Theme.Dim, X, y); y += 18; }
        }

        // Test browsers and leftovers.
        if (s.Browsers.Count > 0 || s.Orphans != null)
        {
            y += 8;
            Eyebrow(g, "RESTOS DE PRUEBAS", X, y); y += 18;
            foreach (var b in s.Browsers.Take(4))
            {
                var grp = b;
                Row(g, y, grp, grp.Detail, "Cerrar", Theme.Amber, () => AskCloseBrowser(grp));
                y += 24;
            }
            if (s.Orphans != null)
            {
                var grp = s.Orphans;
                Row(g, y, grp, grp.Procs.Count(p => p.Name == "node") + " procesos", "Limpiar", Theme.Amber, () => AskCloseOrphans(grp));
                y += 24;
            }
        }

        y += 6;
        Line(g, X, y, W);
        y += 12;

        // kDrive: pause = close it now and reopen it automatically later.
        if (KDrivePause.Paused)
        {
            T(g, "kDrive", fText, Theme.Text, X, y);
            var left = KDrivePause.ResumeAt - DateTime.Now;
            string when = "en pausa · vuelve " + (left.TotalMinutes < 1 ? "ya" : "en " + (left.TotalMinutes >= 60 ? (int)left.TotalHours + " h " + left.Minutes + " min" : Math.Ceiling(left.TotalMinutes) + " min"));
            T(g, when, fSmall, Theme.Amber, X + TW(g, "kDrive", fText) + 6, y + 2);
            ActionPill(g, new RectangleF(R - 72, y - 1, 72, 20), "Reanudar", Theme.Green, () => KDrivePause.Resume());
        }
        else if (s.KDrive != null)
        {
            bool kb; lock (snapLock) kb = busy.Contains(s.KDrive.Key);
            T(g, "kDrive", fText, Theme.Text, X, y);
            T(g, Fmt(s.KDrive.Mem), fSmall, Theme.Dim, X + TW(g, "kDrive", fText) + 6, y + 2);
            if (kb) TR(g, "pausando…", fSmall, Theme.Dim, R, y + 2);
            else
            {
                T(g, "pausar", fSmall, Theme.Dim, R - 128 - TW(g, "pausar", fSmall) - 6, y + 2);
                var kdg = s.KDrive; string exe = s.KDriveExe;
                ActionPill(g, new RectangleF(R - 128, y - 1, 46, 20), "30 min", Theme.Amber, () => AskPause(kdg, exe, 30));
                ActionPill(g, new RectangleF(R - 78, y - 1, 36, 20), "1 h", Theme.Amber, () => AskPause(kdg, exe, 60));
                ActionPill(g, new RectangleF(R - 38, y - 1, 38, 20), "2 h", Theme.Amber, () => AskPause(kdg, exe, 120));
            }
        }
        else
        {
            T(g, "kDrive", fText, Theme.Text, X, y);
            T(g, "cerrado", fSmall, Theme.Dim, X + TW(g, "kDrive", fText) + 6, y + 2);
            if (KDrivePause.Exe != null) ActionPill(g, new RectangleF(R - 58, y - 1, 58, 20), "Abrir", Theme.Green, () => KDrivePause.Resume());
        }
        y += 26;

        // Chrome extensions, through "Disable All Extensions".
        if (ChromeExt.Installed)
        {
            T(g, "Extensiones", fText, Theme.Text, X, y);
            string info = ChromeExt.Enabled >= 0 ? ChromeExt.Enabled + " activas" : "";
            if (s.ChromeExtMem > 0) info += (info.Length > 0 ? " · " : "") + Fmt(s.ChromeExtMem);
            T(g, info, fSmall, Theme.Dim, X + TW(g, "Extensiones", fText) + 6, y + 2);
            var pr = new RectangleF(R - 72, y - 1, 72, 20);
            if (DateTime.Now < extBusyUntil) TR(g, "cambiando…", fSmall, Theme.Dim, R, y + 2);
            else if (!s.ChromeRunning) TR(g, "Chrome cerrado", fSmall, Theme.Dim, R, y + 2);
            else if (ChromeExt.Shortcut == null) ActionPill(g, pr, "Configurar", Theme.Amber, ShowExtSetup);
            else if (ChromeExt.Disabling == true) ActionPill(g, pr, "Encender", Theme.Green, ToggleExt);
            else if (ChromeExt.Disabling == false) ActionPill(g, pr, "Apagar", Theme.Amber, ToggleExt);
            else ActionPill(g, pr, "Alternar", Theme.Blue, ToggleExt);
            y += 26;
        }

        y += 4;
        // Primary action: highlighted in blue once memory is tight.
        var br = new RectangleF(X, y, W, 32);
        Fill(g, br, 7, frac >= AlertAt ? (BtnHot ? Color.FromArgb(90, Theme.Blue) : Color.FromArgb(60, Theme.Blue)) : (BtnHot ? Theme.Hot : Theme.Track));
        Btn = br;
        string bl = "Liberar memoria…";
        T(g, bl, fText, frac >= AlertAt ? Theme.Blue : Theme.Text, br.X + (br.Width - TW(g, bl, fText)) / 2, br.Y + 8);
        y += 32;

        FitHeight(y + 14);
    }

    // ---- actions ----

    bool Confirm(string text, string title)
    {
        return MessageBox.Show(this, text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    void Run(Group grp, bool polite)
    {
        long mem = grp.Mem;
        lock (snapLock) busy.Add(grp.Key);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { MemScan.CloseGroup(grp, polite); } catch { }
            BeginInvoke((Action)(() => { flash = "✓ liberados ≈ " + Fmt(mem); flashUntil = DateTime.Now.AddSeconds(10); Scan(); Invalidate(); }));
        });
    }

    void AskCloseApp(Group grp)
    {
        MemSnapshot s; lock (snapLock) s = snap;
        // Closing a terminal ends the Claude Code sessions running inside it.
        var inside = s.Sessions.Where(x => x.Procs.Count > 0 && Under(x.Procs[0], grp)).ToList();
        string extra = grp.Title == "Chrome" ? "\n\nChrome guarda las pestañas: al abrirlo podrás restaurarlas." : "\n\nSe pedirá guardar si hay cambios sin guardar.";
        if (inside.Count > 0) extra += "\n\n⚠ También se cerrarán " + inside.Count + " sesiones de Claude Code: " + string.Join(", ", inside.Select(x => x.Title)) + ".";
        if (Confirm("¿Cerrar «" + grp.Title + "»? Libera ≈ " + Fmt(grp.Mem) + "." + extra, "Cerrar " + grp.Title)) Run(grp, true);
    }

    static bool Under(Proc p, Group g)
    {
        for (var a = p.Parent; a != null; a = a.Parent) if (a.Owner == g) return true;
        return false;
    }

    void AskCloseSession(Group grp)
    {
        string resume = ResumeCommand(grp);
        string text = "¿Cerrar la sesión de Claude Code «" + grp.Title + "»?\n\nLibera ≈ " + Fmt(grp.Mem) + " (Claude y sus " + (grp.Procs.Count - 1) + " procesos: MCP, shells…).";
        if (resume != null) text += "\n\nLa conversación queda guardada. Al cerrar se copia al portapapeles el comando para seguirla:\n" + resume;
        if (!Confirm(text, "Cerrar sesión de Claude Code")) return;
        if (resume != null) try { Clipboard.SetText(resume); } catch { }
        Run(grp, false);
    }

    public static string ResumeCommand(Group grp)
    {
        return grp.SessionId == null ? null : (grp.Cwd != null ? "cd \"" + grp.Cwd + "\"; " : "") + "claude --resume " + grp.SessionId;
    }

    void AskCloseBrowser(Group grp)
    {
        if (Confirm("¿Cerrar el navegador de pruebas (" + grp.Detail + ")?\n\nLibera ≈ " + Fmt(grp.Mem) + ". Si una sesión de Claude lo está usando, agent-browser lo abrirá de nuevo cuando lo necesite.", "Cerrar navegador de pruebas"))
            Run(grp, false);
    }

    void AskCloseOrphans(Group grp)
    {
        var what = grp.Procs.Where(p => p.Name == "node" && p.Cmd != null).Select(p => Summary(p.Cmd)).Distinct().Take(5);
        if (Confirm("¿Cerrar los servidores MCP que quedaron abiertos tras cerrar su sesión de Claude?\n\n" + string.Join("\n", what) + "\n\nLibera ≈ " + Fmt(grp.Mem) + ". Ninguna sesión viva los usa.", "Limpiar MCP huérfanos"))
            Run(grp, false);
    }

    static string Summary(string cmd)
    {
        var m = Regex.Match(cmd, @"https?://[^\s""]+");
        if (m.Success) return "· mcp-remote " + m.Value;
        if (cmd.IndexOf("codegraph", StringComparison.OrdinalIgnoreCase) >= 0) return "· codegraph";
        var parts = cmd.Replace("\"", "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return "· " + string.Join(" ", parts.Skip(1).Take(2).Select(p => Path.GetFileName(p.TrimEnd('\\', '/'))));
    }

    void AskPause(Group kd, string exe, int minutes)
    {
        if (exe == null || !File.Exists(exe)) { MessageBox.Show(this, "No encuentro el ejecutable de kDrive para volver a abrirlo luego.", "kDrive"); return; }
        string when = minutes < 60 ? minutes + " min" : (minutes / 60) + " h";
        if (!Confirm("¿Pausar kDrive " + when + "?\n\nSe cierra ahora (libera ≈ " + Fmt(kd.Mem) + ") y se vuelve a abrir solo a las " + DateTime.Now.AddMinutes(minutes).ToString("HH:mm") +
                     ". Lo que cambies mientras tanto se sincroniza al reabrirse.\n\nSi cierras los widgets antes, kDrive se reabre al cerrarlos.", "Pausar kDrive")) return;
        KDrivePause.Start(exe, minutes);
        Run(kd, true);
    }

    // Chrome's own windows belong to its browser process: the root of each "Chrome" group.
    public static List<int> ChromePids(MemSnapshot s)
    {
        return s.Apps.Where(g => g.Key.StartsWith("app:chrome:") && g.Procs.Count > 0).Select(g => g.Procs[0].Pid).ToList();
    }

    void ToggleExt()
    {
        extBusyUntil = DateTime.Now.AddSeconds(30);
        List<int> pids; lock (snapLock) pids = ChromePids(snap);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            bool ok = ChromeExt.Toggle(pids);
            Thread.Sleep(800);
            ChromeExt.Refresh();
            BeginInvoke((Action)(() =>
            {
                extBusyUntil = DateTime.MinValue;
                if (!ok) { flash = "✕ Chrome no respondió al atajo"; flashUntil = DateTime.Now.AddSeconds(10); }
                Invalidate();
            }));
        });
    }

    void ShowExtSetup()
    {
        try { Clipboard.SetText(ChromeExt.ShortcutsUrl); } catch { }
        MessageBox.Show(this,
            "Para encender y apagar las extensiones desde aquí, dale un atajo a «Disable All Extensions»:\n\n" +
            "1. En Chrome, pega en la barra de direcciones: " + ChromeExt.ShortcutsUrl + " (ya está copiado).\n" +
            "2. En «Disable All Extensions» › «Activar la extensión», pulsa Ctrl+Shift+8. El ámbito «En Chrome» vale.\n\n" +
            "Al pulsar el botón, el widget trae Chrome al frente un momento, pulsa el atajo y te devuelve a la app en la que estabas. " +
            "El widget detecta el atajo solo en unos segundos.",
            "Extensiones de Chrome", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    public void OnExit() { KDrivePause.ResumeIfPaused(); }

    // ---- "Liberar memoria" panel ----

    FreePanel panel;
    protected override void OnButton() { ShowFreePanel(); }

    public void ShowFreePanel()
    {
        if (panel == null || panel.IsDisposed)
            panel = new FreePanel(() => { flash = "✓ memoria liberada"; flashUntil = DateTime.Now.AddSeconds(10); Scan(); Invalidate(); });
        panel.Show();
        if (panel.WindowState == FormWindowState.Minimized) panel.WindowState = FormWindowState.Normal;
        panel.Activate();
        panel.Reload();
    }

    // ---- alert when memory stays high ----

    const double AlertAt = 0.85, RearmBelow = 0.80;
    int highScans;
    bool armed = true;
    DateTime lastAlert = DateTime.MinValue;
    public event Action<string, string> Alert;
    public static bool AlertsOn { get { return Settings.Bool("mem.alert", true); } set { Settings.SetBool("mem.alert", value); Settings.Save(); } }

    // Called after each scan (every 3 s): 10 scans in a row at 85 % or more = 30 s of real pressure, not a spike.
    void CheckAlert(MemSnapshot s)
    {
        if (s.RamTotal == 0) return;
        double frac = (double)s.RamUsed / s.RamTotal;
        if (frac < RearmBelow) { armed = true; highScans = 0; return; }
        highScans = frac >= AlertAt ? highScans + 1 : 0;
        if (!armed || highScans < 10 || !AlertsOn || (DateTime.Now - lastAlert).TotalMinutes < 20) return;
        armed = false;
        lastAlert = DateTime.Now;
        var top = s.Apps.Where(g => g.CanClose).Take(2).Select(g => g.Title + " " + Fmt(g.Mem));
        long safe = (s.Orphans != null ? s.Orphans.Mem : 0) + s.Browsers.Sum(g => g.Mem)
            + s.Sessions.Where(g => g.CanClose && g.StatusSince != DateTime.MinValue && (DateTime.Now - g.StatusSince).TotalMinutes >= 30).Sum(g => g.Mem);
        string body = "Lo que más ocupa: " + string.Join(", ", top) + "."
            + (safe > 0 ? " Puedes liberar ≈ " + Fmt(safe) + " sin riesgo." : "") + " Pulsa aquí para liberar memoria.";
        if (Alert != null) Alert("Memoria al " + (frac * 100).ToString("0") + " %", body);
    }
}

// kDrive "pause": closed now, reopened at a time saved in settings.ini, so it also survives a widget restart.
static class KDrivePause
{
    static DateTime startedAt;
    public static string Exe { get { return Settings.Get("kdrive.exe"); } }
    public static DateTime ResumeAt
    {
        get { long t; return long.TryParse(Settings.Get("kdrive.resume"), out t) && t > 0 ? new DateTime(t, DateTimeKind.Local) : DateTime.MinValue; }
    }
    public static bool Paused { get { return ResumeAt != DateTime.MinValue; } }

    public static void Start(string exe, int minutes)
    {
        startedAt = DateTime.Now;
        Settings.Set("kdrive.exe", exe);
        Settings.Set("kdrive.resume", DateTime.Now.AddMinutes(minutes).Ticks);
        Settings.Save();
    }

    // Called every second: reopen when the time is up; forget the pause if you reopened kDrive yourself.
    public static void Check(bool running)
    {
        if (!Paused) return;
        if (DateTime.Now >= ResumeAt) { Resume(); return; }
        if (running && (DateTime.Now - startedAt).TotalSeconds > 60) Clear();
    }

    public static void Resume()
    {
        var exe = Exe;
        Clear();
        if (exe != null && File.Exists(exe) && Process.GetProcessesByName("kDrive").Length == 0)
            try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) }); } catch { }
    }

    public static void ResumeIfPaused() { if (Paused) Resume(); }

    static void Clear() { Settings.Set("kdrive.resume", 0); Settings.Save(); }
}
