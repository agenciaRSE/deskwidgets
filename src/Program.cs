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
            var wa = Screen.PrimaryScreen.WorkingArea;
            net.PlaceDefault(wa.Right - net.Width - 16, wa.Top + 16);
            pc.PlaceDefault(wa.Right - pc.Width - 16, wa.Top + 16 + net.Height + 12);
            net.TopMost = pc.TopMost = Settings.Bool("topmost", false);

            var menu = BuildMenu();
            net.ContextMenuStrip = pc.ContextMenuStrip = menu;
            tray = new NotifyIcon { Icon = MakeIcon(), Text = AppName, ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += (s, e) => { net.Show(); pc.Show(); };

            if (Settings.Bool("net.visible", true)) net.Show();
            if (Settings.Bool("pc.visible", true)) pc.Show();
            Application.Run();
        }
    }

    static ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();
        var iNet = new ToolStripMenuItem("Widget de red");
        var iPc = new ToolStripMenuItem("Widget de rendimiento");
        var iTop = new ToolStripMenuItem("Siempre encima de las ventanas");
        var iRun = new ToolStripMenuItem("Iniciar con Windows");
        var iExit = new ToolStripMenuItem("Salir");
        iNet.Click += (s, e) => Toggle(net, "net");
        iPc.Click += (s, e) => Toggle(pc, "pc");
        iTop.Click += (s, e) =>
        {
            bool v = !net.TopMost;
            net.TopMost = pc.TopMost = v;
            if (!v) { net.SendToBack(); pc.SendToBack(); }
            Settings.SetBool("topmost", v);
            Settings.Save();
        };
        iRun.Click += (s, e) => SetAutostart(!IsAutostart());
        iExit.Click += (s, e) => { tray.Visible = false; Settings.Save(); Application.Exit(); };
        m.Items.AddRange(new ToolStripItem[] { iNet, iPc, new ToolStripSeparator(), iTop, iRun, new ToolStripSeparator(), iExit });
        m.Opening += (s, e) =>
        {
            iNet.Checked = net.Visible;
            iPc.Checked = pc.Visible;
            iTop.Checked = net.TopMost;
            iRun.Checked = IsAutostart();
        };
        return m;
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
            if (on) k.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
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
