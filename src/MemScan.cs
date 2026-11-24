using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;

// Native calls used only by the memory widget.
static class MemNative
{
    [DllImport("ntdll.dll")] public static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);
    [DllImport("ntdll.dll")] public static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr buf, int len, out int ret);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] public static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll")] public static extern bool GetProcessTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    public delegate bool EnumProc(IntPtr h, IntPtr p);

    public const uint QueryLimited = 0x1000, Terminate = 0x0001;
}

sealed class Proc
{
    public int Pid, Ppid;
    public long Created;   // FILETIME, tells a reused PID apart
    public string Name;    // image name, without .exe
    public long Mem;       // private working set (what Task Manager calls "Memory")
    public string Cmd;     // only for the process names that need it
    public Proc Parent;
    public readonly List<Proc> Kids = new List<Proc>();
    public Group Owner;
}

enum GroupKind { App, Session, Browser, Orphans, KDrive }

sealed class Group
{
    public GroupKind Kind;
    public string Key, Title, Detail;
    public long Mem;
    public readonly List<Proc> Procs = new List<Proc>();
    public bool CanClose = true;
    // Claude Code session
    public string SessionId, Cwd, Status;
    public DateTime StatusSince;
    // agent-browser
    public string BrowserSession;
}

sealed class MemSnapshot
{
    public ulong RamTotal, RamUsed, CommitTotal, CommitLimit;
    public List<Group> Apps = new List<Group>();       // biggest app groups, heaviest first
    public List<Group> Sessions = new List<Group>();   // Claude Code CLI sessions
    public List<Group> Browsers = new List<Group>();   // agent-browser test browsers
    public Group Orphans;                              // MCP servers whose Claude session is gone
    public Group KDrive;                               // null when not running
    public string KDriveExe;
    public long ChromeExtMem;
    public int ChromeExtProcs;
    public bool ChromeRunning;
}

static class MemScan
{
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly string SessionsDir = Path.Combine(Home, @".claude\sessions");
    static readonly string AgentDir = Path.Combine(Home, ".agent-browser");
    static readonly int Self = Process.GetCurrentProcess().Id;
    static readonly int SessionId = Process.GetCurrentProcess().SessionId;
    // Command lines never change for a process: cache by pid + creation time.
    static readonly Dictionary<long, string> cmdCache = new Dictionary<long, string>();
    static readonly HashSet<string> NeedCmd = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "chrome", "claude", "node", "kDrive" };

    // Every process in one syscall (SystemProcessInformation), no handle per process.
    static List<Proc> List()
    {
        var list = new List<Proc>();
        if (IntPtr.Size != 8) return list;
        int size = 1 << 20, ret;
        IntPtr buf = IntPtr.Zero;
        try
        {
            for (int tries = 0; tries < 6; tries++)
            {
                buf = Marshal.AllocHGlobal(size);
                int st = MemNative.NtQuerySystemInformation(5, buf, size, out ret);
                if (st == 0) break;
                Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;
                if (st != unchecked((int)0xC0000004)) return list; // not STATUS_INFO_LENGTH_MISMATCH
                size = Math.Max(size * 2, ret + 65536);
            }
            if (buf == IntPtr.Zero) return list;
            int off = 0;
            while (true)
            {
                IntPtr e = buf + off;
                int next = Marshal.ReadInt32(e, 0);
                var p = new Proc
                {
                    Mem = Marshal.ReadInt64(e, 8),
                    Created = Marshal.ReadInt64(e, 32),
                    Pid = (int)Marshal.ReadInt64(e, 80),
                    Ppid = (int)Marshal.ReadInt64(e, 88),
                };
                int sess = Marshal.ReadInt32(e, 100);
                short len = Marshal.ReadInt16(e, 56);
                IntPtr name = Marshal.ReadIntPtr(e, 64);
                p.Name = len > 0 && name != IntPtr.Zero ? Marshal.PtrToStringUni(name, len / 2) : (p.Pid == 0 ? "Idle" : "System");
                if (p.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) p.Name = p.Name.Substring(0, p.Name.Length - 4);
                if (sess == SessionId || p.Pid <= 4) list.Add(p);
                if (next == 0) break;
                off += next;
            }
        }
        finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
        return list;
    }

    // Command line via ProcessCommandLineInformation (60): works without debug rights for our own processes.
    static string CmdLine(int pid)
    {
        IntPtr h = MemNative.OpenProcess(MemNative.QueryLimited, false, pid);
        if (h == IntPtr.Zero) return "";
        IntPtr buf = Marshal.AllocHGlobal(65536);
        try
        {
            int ret;
            if (MemNative.NtQueryInformationProcess(h, 60, buf, 65536, out ret) != 0) return "";
            short len = Marshal.ReadInt16(buf, 0);
            IntPtr s = Marshal.ReadIntPtr(buf, 8);
            return len > 0 ? Marshal.PtrToStringUni(s, len / 2) : "";
        }
        finally { Marshal.FreeHGlobal(buf); MemNative.CloseHandle(h); }
    }

    static long CacheKey(Proc p) { return ((long)p.Pid << 40) ^ p.Created; }

    public static MemSnapshot Take()
    {
        var snap = new MemSnapshot();
        // GlobalMemoryStatusEx, not GetPerformanceInfo: that one walks every handle in the system (~400 ms).
        var m = new Native.MEMORYSTATUSEX { dwLength = 64 };
        if (Native.GlobalMemoryStatusEx(ref m))
        {
            snap.RamTotal = m.ullTotalPhys;
            snap.RamUsed = m.ullTotalPhys - m.ullAvailPhys;
            snap.CommitLimit = m.ullTotalPageFile;
            snap.CommitTotal = m.ullTotalPageFile - m.ullAvailPageFile;
        }

        var all = List();
        var byPid = all.ToDictionary(p => p.Pid);
        var alive = new HashSet<long>();
        foreach (var p in all)
        {
            Proc par;
            // A parent only counts if it is older than the child: otherwise the PID was reused.
            if (p.Pid != p.Ppid && byPid.TryGetValue(p.Ppid, out par) && par.Created <= p.Created) { p.Parent = par; par.Kids.Add(p); }
            if (NeedCmd.Contains(p.Name))
            {
                long k = CacheKey(p);
                alive.Add(k);
                string c;
                lock (cmdCache) if (!cmdCache.TryGetValue(k, out c)) cmdCache[k] = c = CmdLine(p.Pid);
                p.Cmd = c;
            }
        }
        lock (cmdCache) foreach (var k in cmdCache.Keys.Where(k => !alive.Contains(k)).ToList()) cmdCache.Remove(k);

        // 1. agent-browser daemons claim their test Chrome.
        var agentPids = ReadAgentSessions();
        foreach (var d in all.Where(p => p.Name.StartsWith("agent-browser", StringComparison.OrdinalIgnoreCase)))
        {
            if (d.Parent != null && d.Parent.Name.StartsWith("agent-browser", StringComparison.OrdinalIgnoreCase)) continue;
            string session; agentPids.TryGetValue(d.Pid, out session);
            var g = new Group { Kind = GroupKind.Browser, Key = "ab:" + d.Pid, BrowserSession = session ?? "" };
            Claim(g, d);
            string url = session == null ? null : ReadTargetUrl(session);
            g.Title = "Navegador de pruebas" + (session != null && session != "default" ? " · " + session : "");
            g.Detail = Host(url) ?? "agent-browser";
            var owner = Ancestor(d, p => IsCliClaude(p));
            if (owner != null) g.Detail += " · de " + SessionLabel(owner.Pid);
            snap.Browsers.Add(g);
        }
        // Test Chrome whose daemon is gone (agent-browser user-data-dir in its command line).
        foreach (var c in all.Where(p => p.Owner == null && p.Name == "chrome" && (p.Parent == null || p.Parent.Name != "chrome")
                                         && p.Cmd != null && p.Cmd.Contains("agent-browser-chrome")))
        {
            var g = new Group { Kind = GroupKind.Browser, Key = "ab:" + c.Pid, Title = "Navegador de pruebas", Detail = "sin agent-browser (huérfano)" };
            Claim(g, c);
            snap.Browsers.Add(g);
        }

        // 2. Claude Code CLI sessions, with their MCP servers and shells.
        foreach (var c in all.Where(IsCliClaude))
        {
            if (Ancestor(c, IsCliClaude) != null) continue; // a nested claude (e.g. claude -p) belongs to its session
            var g = new Group { Kind = GroupKind.Session, Key = "cc:" + c.Pid };
            ReadSession(c.Pid, g);
            Claim(g, c);
            g.CanClose = g.Status == "idle";
            snap.Sessions.Add(g);
        }

        // 3. Claude Desktop (Electron in WindowsApps) and the MCP servers it starts.
        foreach (var c in all.Where(p => p.Name == "claude" && p.Owner == null && (p.Parent == null || p.Parent.Name != "claude")))
        {
            var g = new Group { Kind = GroupKind.App, Key = "app:claude-desktop", Title = "Claude Desktop", Detail = "app + MCP" };
            Claim(g, c);
            snap.Apps.Add(g);
        }

        // 4. MCP servers / node left behind when their Claude session ended.
        var orphans = new Group { Kind = GroupKind.Orphans, Key = "orphans", Title = "MCP huérfanos", Detail = "su sesión de Claude ya terminó" };
        foreach (var n in all.Where(p => p.Owner == null && p.Name == "node" && IsMcp(p.Cmd)))
        {
            if (n.Parent != null && n.Parent.Name == "node" && n.Parent.Owner == null) continue; // the root claims it
            if (!Abandoned(n)) continue;
            var root = n.Parent != null && n.Parent.Name == "cmd" && n.Parent.Owner == null && n.Parent.Kids.Count == 1 ? n.Parent : n;
            Claim(orphans, root);
        }
        if (orphans.Procs.Count > 0) snap.Orphans = orphans;

        // 5. kDrive (sync server + its client UI).
        var kd = all.FirstOrDefault(p => p.Owner == null && p.Name == "kDrive" && (p.Parent == null || p.Parent.Name != "kDrive"));
        if (kd != null)
        {
            var g = new Group { Kind = GroupKind.KDrive, Key = "kdrive", Title = "kDrive" };
            Claim(g, kd);
            foreach (var p in all.Where(p => p.Owner == null && p.Name == "kDrive")) Claim(g, p);
            snap.KDrive = g;
            snap.KDriveExe = ExePath(kd.Cmd);
        }

        // 6. Your Chrome (everything not claimed by agent-browser), with the share of its extensions.
        foreach (var c in all.Where(p => p.Owner == null && p.Name == "chrome" && (p.Parent == null || p.Parent.Name != "chrome")))
        {
            var g = new Group { Kind = GroupKind.App, Key = "app:chrome:" + c.Pid, Title = "Chrome" };
            Claim(g, c);
            snap.ChromeRunning = true;
            foreach (var p in g.Procs.Where(p => p.Cmd != null && p.Cmd.Contains("--extension-process")))
            { snap.ChromeExtMem += p.Mem; snap.ChromeExtProcs++; }
            snap.Apps.Add(g);
        }

        // 7. WebView2 counted under the app that hosts it (WhatsApp, Outlook, Raycast…).
        var hosts = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in all.Where(p => p.Owner == null && p.Name == "msedgewebview2"))
        {
            var host = Ancestor(w, p => p.Name != "msedgewebview2");
            string name = host != null ? host.Name : "WebView2";
            Group g;
            if (!hosts.TryGetValue(name, out g)) hosts[name] = g = new Group { Kind = GroupKind.App, Key = "app:" + name, Title = Pretty(name) };
            Claim(g, w);
            if (host != null && host.Owner == null) { Claim(g, host); }
        }
        foreach (var g in hosts.Values) { g.Detail = "con WebView2"; snap.Apps.Add(g); }

        // 8. Everything else, by app name. System processes and this widget are never offered.
        var rest = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in all.Where(p => p.Owner == null && p.Pid > 4))
        {
            Group g;
            if (!rest.TryGetValue(p.Name, out g)) rest[p.Name] = g = new Group { Kind = GroupKind.App, Key = "app:" + p.Name, Title = Pretty(p.Name) };
            g.Procs.Add(p); p.Owner = g;
        }
        foreach (var g in rest.Values)
        {
            g.CanClose = Procs.CanClose(g.Procs[0].Name) && !g.Procs.Any(p => p.Pid == Self || IsAncestorOfSelf(p, byPid));
            snap.Apps.Add(g);
        }
        foreach (var g in snap.Apps.Where(g => g.Procs.Any(p => p.Pid == Self || IsAncestorOfSelf(p, byPid)))) g.CanClose = false;

        foreach (var g in snap.Apps.Concat(snap.Sessions).Concat(snap.Browsers)) g.Mem = g.Procs.Sum(p => p.Mem);
        if (snap.Orphans != null) snap.Orphans.Mem = snap.Orphans.Procs.Sum(p => p.Mem);
        if (snap.KDrive != null) snap.KDrive.Mem = snap.KDrive.Procs.Sum(p => p.Mem);
        snap.Apps = snap.Apps.Where(g => g.Mem > 0).OrderByDescending(g => g.Mem).ToList();
        snap.Sessions = snap.Sessions.OrderByDescending(g => g.Mem).ToList();
        foreach (var g in snap.Apps) if (g.Detail == null) g.Detail = g.Procs.Count == 1 ? "1 proceso" : g.Procs.Count + " procesos";
        return snap;
    }

    static bool IsAncestorOfSelf(Proc p, Dictionary<int, Proc> byPid)
    {
        Proc s; if (!byPid.TryGetValue(Self, out s)) return false;
        for (var a = s.Parent; a != null; a = a.Parent) if (a == p) return true;
        return false;
    }

    // Claude Code CLI (native installer or npm); not the Desktop app, which runs from WindowsApps.
    static bool IsCliClaude(Proc p)
    {
        return p.Name == "claude" && p.Cmd != null && p.Cmd.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) < 0
            && p.Cmd.IndexOf("--type=", StringComparison.Ordinal) < 0;
    }

    static bool IsMcp(string cmd)
    {
        if (string.IsNullOrEmpty(cmd)) return false;
        string c = cmd.ToLowerInvariant();
        return c.Contains("mcp") || c.Contains("codegraph") || c.Contains("proxy.js") || c.Contains("modelcontextprotocol");
    }

    static readonly HashSet<string> Shells = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "cmd", "node", "powershell", "pwsh", "bash", "sh", "conhost", "OpenConsole" };
    // Nobody uses it any more: above the shells, the process that started it no longer exists.
    // Windows never re-parents, so a live parent (Warp, VS Code, Raycast…) means that app still owns it.
    static bool Abandoned(Proc p)
    {
        var a = p.Parent;
        while (a != null && Shells.Contains(a.Name)) a = a.Parent;
        return a == null;
    }

    static Proc Ancestor(Proc p, Func<Proc, bool> pred)
    {
        for (var a = p.Parent; a != null; a = a.Parent) if (pred(a)) return a;
        return null;
    }

    static void Claim(Group g, Proc root)
    {
        var stack = new Stack<Proc>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var p = stack.Pop();
            if (p.Owner != null) continue;
            p.Owner = g;
            g.Procs.Add(p);
            foreach (var k in p.Kids) stack.Push(k);
        }
    }

    static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "WhatsApp.Root", "WhatsApp" }, { "olk", "Outlook" }, { "PowerToys.Peek.UI", "PowerToys Peek" },
        { "SearchHost", "Búsqueda de Windows" }, { "msedge", "Edge" }, { "MsMpEng", "Defender" },
        { "Memory Compression", "Memoria comprimida" }, { "svchost", "Servicios de Windows" }, { "explorer", "Explorador" },
        { "warp", "Warp" }, { "powershell", "PowerShell" },
    };
    static string Pretty(string name) { string n; return Names.TryGetValue(name, out n) ? n : name; }

    static string ExePath(string cmd)
    {
        if (string.IsNullOrEmpty(cmd)) return null;
        cmd = cmd.Trim();
        if (cmd.StartsWith("\"")) { int i = cmd.IndexOf('"', 1); return i > 1 ? cmd.Substring(1, i - 1) : null; }
        int sp = cmd.IndexOf(' ');
        return sp > 0 ? cmd.Substring(0, sp) : cmd;
    }

    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

    // ~/.claude/sessions/<pid>.json, written by Claude Code: name, cwd, idle/busy.
    static void ReadSession(int pid, Group g)
    {
        g.Title = "claude " + pid;
        g.Status = "idle";
        try
        {
            var d = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(SessionsDir, pid + ".json")));
            object v;
            if (d.TryGetValue("cwd", out v) && v != null) { g.Cwd = v.ToString(); g.Title = Path.GetFileName(g.Cwd.TrimEnd('\\')); }
            if (d.TryGetValue("sessionId", out v) && v != null) g.SessionId = v.ToString();
            if (d.TryGetValue("status", out v) && v != null) g.Status = v.ToString();
            if ((d.TryGetValue("statusUpdatedAt", out v) || d.TryGetValue("updatedAt", out v)) && v != null)
                g.StatusSince = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(v)).LocalDateTime;
        }
        catch { g.Status = "unknown"; }
    }

    static string SessionLabel(int pid)
    {
        var g = new Group(); ReadSession(pid, g); return g.Title;
    }

    // ~/.agent-browser/<session>.pid holds the daemon PID; <session>.target its current page.
    static Dictionary<int, string> ReadAgentSessions()
    {
        var map = new Dictionary<int, string>();
        try
        {
            foreach (var f in Directory.GetFiles(AgentDir, "*.pid"))
            {
                int pid;
                if (int.TryParse(File.ReadAllText(f).Trim(), out pid)) map[pid] = Path.GetFileNameWithoutExtension(f);
            }
        }
        catch { }
        return map;
    }

    static string ReadTargetUrl(string session)
    {
        try
        {
            var d = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(AgentDir, session + ".target")));
            object v; return d.TryGetValue("url", out v) && v != null ? v.ToString() : null;
        }
        catch { return null; }
    }

    static string Host(string url)
    {
        Uri u;
        return url != null && Uri.TryCreate(url, UriKind.Absolute, out u) && !string.IsNullOrEmpty(u.Host) ? u.Host.Replace("www.", "") : null;
    }

    // ---- actions ----

    // Asks every visible window of the group to close (Chrome and apps save state), then terminates what is left.
    public static void CloseGroup(Group g, bool polite)
    {
        var pids = new HashSet<int>(g.Procs.Select(p => p.Pid));
        if (polite)
        {
            int n = 0;
            MemNative.EnumWindows((h, l) =>
            {
                int pid; MemNative.GetWindowThreadProcessId(h, out pid);
                if (pids.Contains(pid) && MemNative.IsWindowVisible(h)) { MemNative.PostMessage(h, 0x10 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero); n++; }
                return true;
            }, IntPtr.Zero);
            if (n > 0)
                for (int i = 0; i < 16 && g.Procs.Any(Alive); i++) Thread.Sleep(500);
        }
        // Children first, so no parent restarts a child we are about to kill.
        foreach (var p in g.Procs.OrderByDescending(Depth)) Kill(p);
    }

    static int Depth(Proc p) { int d = 0; for (var a = p.Parent; a != null; a = a.Parent) d++; return d; }

    static bool Alive(Proc p)
    {
        IntPtr h = MemNative.OpenProcess(MemNative.QueryLimited, false, p.Pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            long c, e, k, u;
            return MemNative.GetProcessTimes(h, out c, out e, out k, out u) && c == p.Created && e == 0;
        }
        finally { MemNative.CloseHandle(h); }
    }

    // Terminates only if the PID still belongs to the same process (creation time matches).
    static void Kill(Proc p)
    {
        if (p.Pid == Self || p.Pid <= 4) return;
        IntPtr h = MemNative.OpenProcess(MemNative.Terminate | MemNative.QueryLimited, false, p.Pid);
        if (h == IntPtr.Zero) return;
        try
        {
            long c, e, k, u;
            if (MemNative.GetProcessTimes(h, out c, out e, out k, out u) && c == p.Created) MemNative.TerminateProcess(h, 1);
        }
        finally { MemNative.CloseHandle(h); }
    }
}
