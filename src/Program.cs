using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    const string AppName = "DeskWidgets";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static NetWidget net;
    static PcWidget pc;
    static ClaudeWidget cl;
    static NotifyIcon tray;

    [STAThread]
    static void Main()
    {
        bool fresh;
        using (new Mutex(true, "DeskWidgets.Singleton", out fresh))
        {
            if (!fresh) return;
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Settings.Load();

            net = new NetWidget();
            pc = new PcWidget();
            cl = new ClaudeWidget();
            var wa = Screen.PrimaryScreen.WorkingArea;
            net.PlaceDefault(wa.Right - net.Width - 16, wa.Top + 16);
            pc.PlaceDefault(wa.Right - pc.Width - 16, wa.Top + 16 + net.Height + 12);
            cl.PlaceDefault(wa.Right - cl.Width - 16, wa.Top + 16 + net.Height + 12 + pc.Height + 12);
            net.TopMost = pc.TopMost = cl.TopMost = Settings.Bool("topmost", false);

            pc.Analyze += (s, e) => ShowAdvisor();
            var menu = BuildMenu();
            net.ContextMenuStrip = pc.ContextMenuStrip = cl.ContextMenuStrip = menu;
            tray = new NotifyIcon { Icon = MakeIcon(), Text = AppName, ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += (s, e) => { net.Show(); pc.Show(); cl.Show(); };

            if (Settings.Bool("net.visible", true)) net.Show();
            if (Settings.Bool("pc.visible", true)) pc.Show();
            if (Settings.Bool("claude.visible", true)) cl.Show();
            Application.Run();
        }
    }

    static ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();
        var iNet = new ToolStripMenuItem("Widget de red");
        var iPc = new ToolStripMenuItem("Widget de rendimiento");
        var iCl = new ToolStripMenuItem("Widget de Claude");
        var iTop = new ToolStripMenuItem("Siempre encima de las ventanas");
        var iRun = new ToolStripMenuItem("Iniciar con Windows");
        var iExit = new ToolStripMenuItem("Salir");
        var iAi = new ToolStripMenuItem("Analizar con Claude…") { Font = new Font(m.Font, FontStyle.Bold) };
        iAi.Click += (s, e) => ShowAdvisor();
        iNet.Click += (s, e) => Toggle(net, "net");
        iPc.Click += (s, e) => Toggle(pc, "pc");
        iCl.Click += (s, e) => Toggle(cl, "claude");
        iTop.Click += (s, e) =>
        {
            bool v = !net.TopMost;
            net.TopMost = pc.TopMost = cl.TopMost = v;
            if (!v) { net.SendToBack(); pc.SendToBack(); cl.SendToBack(); }
            Settings.SetBool("topmost", v);
            Settings.Save();
        };
        iRun.Click += (s, e) => SetAutostart(!IsAutostart());
        iExit.Click += (s, e) => { tray.Visible = false; Settings.Save(); Application.Exit(); };
        m.Items.AddRange(new ToolStripItem[] { iAi, new ToolStripSeparator(), iNet, iPc, iCl, new ToolStripSeparator(), iTop, iRun, new ToolStripSeparator(), iExit });
        m.Opening += (s, e) =>
        {
            iNet.Checked = net.Visible;
            iPc.Checked = pc.Visible;
            iCl.Checked = cl.Visible;
            iTop.Checked = net.TopMost;
            iRun.Checked = IsAutostart();
        };
        return m;
    }

    static AdvisorForm advisor;

    // One analysis window, reused; opening it starts a fresh analysis unless one is already running.
    static void ShowAdvisor()
    {
        if (advisor == null || advisor.IsDisposed)
            advisor = new AdvisorForm(() =>
            {
                var d = pc.Snapshot();
                d["network"] = net.Snapshot();
                return d;
            }, () => net.Gateway);
        advisor.Show();
        if (advisor.WindowState == FormWindowState.Minimized) advisor.WindowState = FormWindowState.Normal;
        advisor.Activate();
        advisor.Run();
    }

    static void Toggle(Form f, string id)
    {
        if (f.Visible) f.Hide(); else f.Show();
        Settings.SetBool(id + ".visible", f.Visible);
        Settings.Save();
    }

    static bool IsAutostart()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(AppName) != null;
    }

    static void SetAutostart(bool on)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            // The full command line, not ExecutablePath: hosted by DeskWidgets.ps1 that would be bare powershell.exe.
            if (on) k.SetValue(AppName, Environment.CommandLine);
            else k.DeleteValue(AppName, false);
        }
    }

    // Tray icon drawn at runtime: three bars on a rounded tile.
    static Icon MakeIcon()
    {
        using (var bmp = new Bitmap(32, 32))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(Theme.Bg)) g.FillEllipse(b, 0, 0, 31, 31);
                using (var b = new SolidBrush(Theme.Blue))
                {
                    g.FillRectangle(b, 8, 17, 4, 7);
                    g.FillRectangle(b, 14, 12, 4, 12);
                    g.FillRectangle(b, 20, 7, 4, 17);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }
}
