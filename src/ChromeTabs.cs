using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Automation;

// One Chrome process and the tabs, frames and extensions it runs, as Chrome's own task manager lists them.
sealed class ChromeTask
{
    public int Pid;
    public readonly List<string> Tasks = new List<string>();   // "Tab: …", "Subframe: …", "Extension: …"
    public string Label;                                       // best name to show
    public int More;                                           // other tasks sharing the process
}

// Windows cannot tell which tab a Chrome process serves (no URL in its command line, chrome.processes is
// Dev-channel only, remote debugging is blocked on the default profile). Chrome's task manager (Shift+Esc)
// knows, and exposes its table through UI Automation: read it from there.
static class ChromeTabs
{
    // null = could not read; empty = read, nothing found. Error says why, in Spanish, for the widget.
    public static List<ChromeTask> Read(ICollection<int> browserPids, ICollection<int> chromePids, out string error)
    {
        error = null;
        var tm = Find(browserPids);
        bool opened = false;
        var prev = MemNative.GetForegroundWindow();
        try
        {
            if (tm == null)
            {
                tm = Open(browserPids);
                opened = tm != null;
                if (tm == null) { error = "Chrome no abrió su administrador de tareas"; return null; }
            }
            SelectFirstTab(tm);
            // Fresh window: rows appear a moment after it opens.
            List<ChromeTask> list = null;
            for (int i = 0; i < 20; i++)
            {
                list = Parse(tm, chromePids);
                if (list.Count > 0) break;
                Thread.Sleep(150);
            }
            if (list.Count == 0) error = "sin datos: activa la columna «ID de proceso» en el administrador de tareas de Chrome (clic derecho en la cabecera)";
            return list;
        }
        catch (Exception e) { error = "no se pudo leer (" + e.GetType().Name + ")"; return null; }
        finally
        {
            if (opened)
                try { MemNative.PostMessage(new IntPtr(tm.Current.NativeWindowHandle), 0x10 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero); } catch { }
            if (prev != IntPtr.Zero && prev != MemNative.GetForegroundWindow()) MemNative.SetForegroundWindow(prev);
        }
    }

    static readonly Condition ChromeWin = new PropertyCondition(AutomationElement.ClassNameProperty, "Chrome_WidgetWin_1");
    static readonly Condition TmView = new PropertyCondition(AutomationElement.ClassNameProperty, "TaskManagerView");
    static readonly Condition Table = new PropertyCondition(AutomationElement.ClassNameProperty, "TableView");
    static readonly Condition TabItem = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);

    // The task manager is a top-level Chrome window holding a TaskManagerView (its title is localized).
    static AutomationElement Find(ICollection<int> browserPids)
    {
        foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, ChromeWin))
        {
            if (!browserPids.Contains(w.Current.ProcessId)) continue;
            if (w.FindFirst(TreeScope.Descendants, TmView) != null) return w;
        }
        return null;
    }

    // Bring a Chrome window to the front and press Shift+Esc, which opens the task manager.
    static AutomationElement Open(ICollection<int> browserPids)
    {
        IntPtr target = IntPtr.Zero;
        MemNative.EnumWindows((h, l) =>
        {
            int pid; MemNative.GetWindowThreadProcessId(h, out pid);
            if (browserPids.Contains(pid) && MemNative.IsWindowVisible(h) && !MemNative.IsIconic(h) && MemNative.GetWindowTextLength(h) > 0)
            {
                var cls = new StringBuilder(64); MemNative.GetClassName(h, cls, 64);
                if (cls.ToString() == "Chrome_WidgetWin_1") { target = h; return false; }
            }
            return true;
        }, IntPtr.Zero);
        if (target == IntPtr.Zero) return null;
        MemNative.SetForegroundWindow(target);
        for (int i = 0; i < 10 && MemNative.GetForegroundWindow() != target; i++) Thread.Sleep(30);
        if (MemNative.GetForegroundWindow() != target) return null;
        Thread.Sleep(60);
        MemNative.keybd_event(0x10, 0, 0, UIntPtr.Zero);
        MemNative.keybd_event(0x1B, 0, 0, UIntPtr.Zero);
        MemNative.keybd_event(0x1B, 0, 2, UIntPtr.Zero);
        MemNative.keybd_event(0x10, 0, 2, UIntPtr.Zero);
        for (int i = 0; i < 20; i++)
        {
            Thread.Sleep(150);
            var tm = Find(browserPids);
            if (tm != null) return tm;
        }
        return null;
    }

    // First tab of the task manager = "Tabs & extensions" (the others are "Browser" and "All tasks").
    static void SelectFirstTab(AutomationElement tm)
    {
        try
        {
            var t = tm.FindFirst(TreeScope.Descendants, TabItem);
            object p;
            if (t != null && t.TryGetCurrentPattern(SelectionItemPattern.Pattern, out p) && !((SelectionItemPattern)p).Current.IsSelected)
            { ((SelectionItemPattern)p).Select(); Thread.Sleep(200); }
        }
        catch { }
    }

    // Rows of the table: [task, memory, CPU, network, PID…] (columns can be rearranged; task is always first).
    // A process with several tasks shows its numbers on its first row only: the rows below without a PID belong to it.
    static List<ChromeTask> Parse(AutomationElement tm, ICollection<int> chromePids)
    {
        var list = new List<ChromeTask>();
        var table = tm.FindFirst(TreeScope.Descendants, Table);
        if (table == null) return list;
        var walk = TreeWalker.RawViewWalker;
        ChromeTask cur = null;
        for (var row = walk.GetFirstChild(table); row != null; row = walk.GetNextSibling(row))
        {
            var cells = new List<string>();
            for (var c = walk.GetFirstChild(row); c != null; c = walk.GetNextSibling(c)) cells.Add(c.Current.Name ?? "");
            if (cells.Count < 2 || cells[0].Length == 0) continue;
            int pid = 0;
            foreach (var s in cells.Skip(1)) { int v; if (int.TryParse(s, out v) && chromePids.Contains(v)) { pid = v; break; } }
            if (pid != 0) { cur = new ChromeTask { Pid = pid }; list.Add(cur); }
            else if (cur == null || cells.Skip(1).Any(s => s.Length > 0 && s != "–")) continue; // header or a row we cannot place
            cur.Tasks.Add(cells[0]);
        }
        foreach (var t in list)
        {
            // A tab or app names the process better than one of its frames.
            var main = t.Tasks.FirstOrDefault(x => x.StartsWith("Tab:") || x.StartsWith("App:")) ?? t.Tasks[0];
            t.Label = Pretty(main);
            t.More = t.Tasks.Count - 1;
        }
        return list;
    }

    public static string Pretty(string task)
    {
        int i = task.IndexOf(": ");
        if (i < 0) return task;
        string kind = task.Substring(0, i), rest = task.Substring(i + 2).Trim();
        if (kind.EndsWith("Subframe"))
        {
            Uri u;
            if (Uri.TryCreate(rest, UriKind.Absolute, out u) && !string.IsNullOrEmpty(u.Host)) rest = u.Host.Replace("www.", "");
            return rest + " (marco)";
        }
        if (kind == "Extension") return "Extensión " + rest;
        if (kind == "Tool") return rest + " (de Chrome)";
        return rest;
    }
}
