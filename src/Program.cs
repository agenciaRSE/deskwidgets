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
        using (var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "DeskWidgets.Show"))
        using (new Mutex(true, "DeskWidgets.Singleton", out fresh))
        {
            // Already running (e.g. opened from the shortcut): bring that instance's widgets back instead.
            if (!fresh) { showSignal.Set(); return; }
            // A failed paint or tick (e.g. while the GPU driver resets) is logged and the widgets keep running,
            // instead of a dialog nobody sees. Background-thread crashes still end the process: log them first,
            // DeskWidgets.ps1 then restarts it.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            string lastUiError = null;
            Application.ThreadException += (s, e) =>
            {
                // A paint that fails once fails every second: log each distinct error once.
                var text = e.Exception.ToString();
                if (text != lastUiError) Log("UI thread: " + text);
                lastUiError = text;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("background thread, process ending: " + e.ExceptionObject);
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

            pc.Analyze += (s, e) => ShowAdvisor();
            var menu = BuildMenu();
            net.ContextMenuStrip = pc.ContextMenuStrip = cl.ContextMenuStrip = menu;
            tray = new NotifyIcon { Icon = MakeIcon(), Text = AppName, ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += (s, e) => ShowAll();
            var ui = SynchronizationContext.Current; // set by WinForms once the first control exists
            ThreadPool.RegisterWaitForSingleObject(showSignal, (o, t) => ui.Post(_ => ShowAll(), null), null, -1, false);
            cl.Notify += (title, body) => tray.ShowBalloonTip(10000, title, body, ToolTipIcon.Warning);

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
        var iTop = new ToolStripMenuItem("Fijar todos encima de las ventanas");
        var iRun = new ToolStripMenuItem("Iniciar con Windows");
        var iExit = new ToolStripMenuItem("Salir");
        var iAi = new ToolStripMenuItem("Analizar con Claude…") { Font = new Font(m.Font, FontStyle.Bold) };
        iAi.Click += (s, e) => ShowAdvisor();
        iNet.Click += (s, e) => Toggle(net, "net");
        iPc.Click += (s, e) => Toggle(pc, "pc");
        iCl.Click += (s, e) => Toggle(cl, "claude");
        iTop.Click += (s, e) =>
        {
            bool v = !(net.Pinned && pc.Pinned && cl.Pinned);
            net.Pinned = pc.Pinned = cl.Pinned = v;
        };
        iRun.Click += (s, e) => SetAutostart(!IsAutostart());
        iExit.Click += (s, e) => { tray.Visible = false; Settings.Save(); Application.Exit(); };
        m.Items.AddRange(new ToolStripItem[] { iAi, new ToolStripSeparator(), iNet, iPc, iCl, new ToolStripSeparator(), iTop, iRun, new ToolStripSeparator(), iExit });
        m.Opening += (s, e) =>
        {
            iNet.Checked = net.Visible;
            iPc.Checked = pc.Visible;
            iCl.Checked = cl.Visible;
            iTop.Checked = net.Pinned && pc.Pinned && cl.Pinned;
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

    static void ShowAll()
    {
        foreach (var w in new Form[] { net, pc, cl })
        {
            w.Show();
            w.Activate();
        }
        Settings.SetBool("net.visible", true);
        Settings.SetBool("pc.visible", true);
        Settings.SetBool("claude.visible", true);
        Settings.Save();
    }

    static void Toggle(Form f, string id)
    {
        if (f.Visible) f.Hide(); else f.Show();
        Settings.SetBool(id + ".visible", f.Visible);
        Settings.Save();
    }

    // Same file DeskWidgets.ps1 writes startup failures and restarts to.
    static void Log(string text)
    {
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "error.log"),
                DateTime.Now.ToString("s") + " pid " + System.Diagnostics.Process.GetCurrentProcess().Id + " " + text + "\r\n\r\n");
        }
        catch { }
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
            // Minus -Child, so Windows starts the supervisor rather than an unwatched widget process, and inside a
            // headless conhost like install.ps1 does, so no Windows Terminal tab hosts it.
            if (on) k.SetValue(AppName, "\"" + System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "conhost.exe")
                + "\" --headless " + Environment.CommandLine.Replace(" -Child", ""));
            else k.DeleteValue(AppName, false);
        }
    }

    // App icon drawn at runtime: three bars on a round tile, laid out on a 32-unit grid.
    static Bitmap DrawIcon(int size)
    {
        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform(size / 32f, size / 32f);
            using (var b = new SolidBrush(Theme.Bg)) g.FillEllipse(b, 0, 0, 31, 31);
            using (var b = new SolidBrush(Theme.Blue))
            {
                g.FillRectangle(b, 8, 17, 4, 7);
                g.FillRectangle(b, 14, 12, 4, 12);
                g.FillRectangle(b, 20, 7, 4, 17);
            }
        }
        return bmp;
    }

    static Icon MakeIcon()
    {
        using (var bmp = DrawIcon(32)) return Icon.FromHandle(bmp.GetHicon());
    }

    // Writes the icon as a 256px PNG-in-ICO for the shortcuts; called by install.ps1.
    public static void SaveIcon(string path)
    {
        byte[] png;
        using (var bmp = DrawIcon(256))
        using (var ms = new System.IO.MemoryStream()) { bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png); png = ms.ToArray(); }
        using (var w = new System.IO.BinaryWriter(System.IO.File.Create(path)))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)1);  // ICONDIR: reserved, type icon, one image
            w.Write((byte)0); w.Write((byte)0);                         // 0 = 256 px wide and high
            w.Write((byte)0); w.Write((byte)0);                         // no palette, reserved
            w.Write((short)1); w.Write((short)32);                      // planes, bits per pixel
            w.Write(png.Length); w.Write(6 + 16);                       // image size, offset after this entry
            w.Write(png);
        }
    }
}
