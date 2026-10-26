using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

sealed class PcWidget : Widget
{
    readonly Pdh pdh = new Pdh();
    readonly IntPtr cCpu, cPerf, cFreq, cGpu, cIdle, cRead, cWrite, cProcs;
    readonly History cpuH = new History(60), ramH = new History(60);
    readonly string cpuName;
    readonly DriveInfo[] drives;
    readonly long[] driveFree, driveTotal;

    double cpu, ghz, gpu, disk, rd, wr, ramFrac;
    int procs, ticks;
    ulong ramUsed, ramTotal;

    static DriveInfo[] FixedDrives()
    {
        return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Take(4).ToArray();
    }

    // name, RAM MB, CPU %: the heaviest closable foreground apps, so they can be closed from the widget.
    readonly object offLock = new object();
    sealed class Off { public string Name; public double Ram, Cpu; }
    List<Off> offenders = new List<Off>();
    bool sampling;

    public PcWidget() : base("pc", 300, 406 + FixedDrives().Length * 22)
    {
        cCpu = pdh.Add(@"\Processor Information(_Total)\% Processor Utility");
        if (cCpu == IntPtr.Zero) cCpu = pdh.Add(@"\Processor(_Total)\% Processor Time");
        cPerf = pdh.Add(@"\Processor Information(_Total)\% Processor Performance");
        cFreq = pdh.Add(@"\Processor Information(_Total)\Processor Frequency");
        cGpu = pdh.Add(@"\GPU Engine(*engtype_3D)\Utilization Percentage");
        cIdle = pdh.Add(@"\PhysicalDisk(_Total)\% Idle Time");
        cRead = pdh.Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        cWrite = pdh.Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
        cProcs = pdh.Add(@"\System\Processes");
        pdh.Collect(); // rate counters need two samples

        drives = FixedDrives();
        driveFree = new long[drives.Length];
        driveTotal = new long[drives.Length];
        using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
            cpuName = k == null ? "" : ((k.GetValue("ProcessorNameString") as string) ?? "").Trim();
    }

    static double Clamp(double v) { return double.IsNaN(v) ? double.NaN : Math.Max(0, Math.Min(100, v)); }

    protected override void Tick()
    {
        pdh.Collect();
        cpu = Clamp(pdh.Get(cCpu));
        ghz = pdh.Get(cFreq) * pdh.Get(cPerf) / 100 / 1000;
        gpu = Clamp(pdh.Sum(cGpu));
        disk = Clamp(100 - pdh.Get(cIdle));
        rd = pdh.Get(cRead);
        wr = pdh.Get(cWrite);
        procs = (int)pdh.Get(cProcs);

        var m = new Native.MEMORYSTATUSEX { dwLength = 64 };
        if (Native.GlobalMemoryStatusEx(ref m))
        {
            ramTotal = m.ullTotalPhys;
            ramUsed = m.ullTotalPhys - m.ullAvailPhys;
            ramFrac = (double)ramUsed / ramTotal;
        }
        cpuH.Push((float)(double.IsNaN(cpu) ? 0 : cpu));
        ramH.Push((float)(ramFrac * 100));

        if (ticks++ % 10 == 0)
            for (int i = 0; i < drives.Length; i++)
                try { driveFree[i] = drives[i].TotalFreeSpace; driveTotal[i] = drives[i].TotalSize; } catch { }

        if (!sampling && ticks % 8 == 1) SampleOffenders();
    }

    // The heaviest closable windowed apps, sampled off the UI thread (Procs.Top sleeps 1.5 s).
    void SampleOffenders()
    {
        sampling = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var top = Procs.Top()
                    .Where(p => (bool)p["closable"] && (bool)p["has_window"] && Convert.ToDouble(p["ram_mb"]) >= 600)
                    .OrderByDescending(p => Convert.ToDouble(p["ram_mb"]))
                    .Take(2)
                    .Select(p => new Off { Name = (string)p["name"], Ram = Convert.ToDouble(p["ram_mb"]), Cpu = Convert.ToDouble(p["cpu_pct"]) })
                    .ToList();
                lock (offLock) offenders = top;
            }
            catch { }
            finally { sampling = false; }
        });
    }

    static string Gb(double bytes) { return (bytes / (1L << 30)).ToString(bytes < 100L * (1L << 30) ? "0.0" : "0"); }
    static string Mb(double bps) { return double.IsNaN(bps) ? "—" : (bps / 1e6).ToString(bps < 1e7 ? "0.0" : "0"); }
    static string Pct(double v) { return double.IsNaN(v) ? "—" : v.ToString("0") + " %"; }

    static string Uptime()
    {
        var t = TimeSpan.FromMilliseconds(Native.GetTickCount64());
        return t.TotalDays >= 1 ? (int)t.TotalDays + " d " + t.Hours + " h" : t.Hours + " h " + t.Minutes + " min";
    }

    protected override void Draw(Graphics g)
    {
        const float x = 16, w = 268, r = x + w;

        Eyebrow(g, "SISTEMA", x, 14);
        TR(g, "encendido " + Uptime(), fSmall, Theme.Dim, HeaderRight, 14);
        T(g, Environment.MachineName, fTitle, Theme.Text, x, 30);
        T(g, cpuName.Length > 46 ? cpuName.Substring(0, 46) + "…" : cpuName, fSmall, Theme.Dim, x, 52);

        // CPU
        float y = 76;
        T(g, "CPU", fText, Theme.Text, x, y);
        string cpuTxt = Pct(cpu) + (double.IsNaN(ghz) || ghz <= 0 ? "" : "  ·  " + ghz.ToString("0.00") + " GHz");
        TR(g, cpuTxt, fText, Theme.Load(cpu / 100), r, y);
        Spark(g, new RectangleF(x, y + 20, w, 30), cpuH, 100, Theme.Blue, true);

        // RAM
        y = 136;
        T(g, "Memoria", fText, Theme.Text, x, y);
        TR(g, Gb(ramUsed) + " / " + Gb(ramTotal) + " GB  ·  " + (ramFrac * 100).ToString("0") + " %", fText, Theme.Load(ramFrac), r, y);
        Spark(g, new RectangleF(x, y + 20, w, 30), ramH, 100, Theme.Violet, true);

        // GPU
        y = 196;
        T(g, "GPU", fText, Theme.Text, x, y);
        TR(g, Pct(gpu), fText, Theme.Load(gpu / 100), r, y);
        Bar(g, x, y + 20, w, 5, gpu / 100, Theme.Load(gpu / 100));

        // Disk activity
        y = 230;
        T(g, "Disco", fText, Theme.Text, x, y);
        TR(g, "L " + Mb(rd) + "  ·  E " + Mb(wr) + " MB/s  ·  " + Pct(disk), fText, Theme.Text, r, y);
        Bar(g, x, y + 20, w, 5, disk / 100, Theme.Load(disk / 100));

        // Drives
        y = 266;
        for (int i = 0; i < drives.Length; i++, y += 22)
        {
            if (driveTotal[i] <= 0) continue;
            double used = driveTotal[i] - driveFree[i], frac = used / driveTotal[i];
            T(g, drives[i].Name.TrimEnd('\\'), fSmall, Theme.Dim, x, y);
            Bar(g, x + 26, y + 5, 150, 5, frac, frac < 0.9 ? Theme.Blue : Theme.Red);
            TR(g, Gb(used) + " / " + Gb(driveTotal[i]) + " GB", fSmall, Theme.Dim, r, y);
        }

        // Footer
        y += 4;
        string foot = procs + " procesos";
        var ps = SystemInformation.PowerStatus;
        if ((ps.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) == 0 && ps.BatteryChargeStatus != BatteryChargeStatus.Unknown)
            foot += "  ·  batería " + (ps.BatteryLifePercent * 100).ToString("0") + " %" + (ps.PowerLineStatus == PowerLineStatus.Online ? " (cargando)" : "");
        T(g, foot, fSmall, Theme.Dim, x, y);

        // Heavy apps you can close straight from the widget (RAM/CPU hogs with a window).
        y += 26;
        Eyebrow(g, "APPS QUE MÁS CONSUMEN", x, y);
        y += 18;
        List<Off> offs;
        lock (offLock) offs = offenders;
        if (offs.Count == 0)
            T(g, "nada destacable ahora", fSmall, Theme.Dim, x, y + 2);
        for (int i = 0; i < 2; i++, y += 24)
        {
            if (i >= offs.Count) continue;
            var o = offs[i];
            T(g, o.Name.Length > 18 ? o.Name.Substring(0, 18) + "…" : o.Name, fText, Theme.Text, x, y);
            string use = Gb(o.Ram * 1048576.0) + " GB" + (o.Cpu >= 5 ? "  ·  " + o.Cpu.ToString("0") + " %" : "");
            TR(g, use, fSmall, Theme.Dim, r - 66, y + 1);
            string name = o.Name;
            ActionPill(g, new RectangleF(r - 58, y - 1, 58, 20), "Cerrar", Theme.Red, () => AskClose(name));
        }

        Button(g, new RectangleF(x, y + 4, w, 30), "✦  Analizar con Claude", true);
    }

    void AskClose(string name)
    {
        var ans = MessageBox.Show(this, "¿Cerrar todas las ventanas de «" + name + "»?\n\nSe pedirá guardar si hay cambios sin guardar.",
            "DeskWidgets", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (ans != DialogResult.Yes) return;
        ThreadPool.QueueUserWorkItem(_ => { try { Procs.Close(name); } catch { } SampleOffenders(); });
    }

    public event EventHandler Analyze;
    protected override void OnButton() { if (Analyze != null) Analyze(this, EventArgs.Empty); }

    static object Num(double v, int digits) { return double.IsNaN(v) ? null : (object)Math.Round(v, digits); }

    public Dictionary<string, object> Snapshot()
    {
        var ps = SystemInformation.PowerStatus;
        bool battery = (ps.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) == 0 && ps.BatteryChargeStatus != BatteryChargeStatus.Unknown;
        return new Dictionary<string, object>
        {
            { "cpu_model", cpuName },
            { "logical_cores", Environment.ProcessorCount },
            { "cpu_pct", Num(cpu, 0) },
            { "cpu_ghz", Num(ghz, 2) },
            { "ram_used_gb", Math.Round(ramUsed / 1073741824.0, 1) },
            { "ram_total_gb", Math.Round(ramTotal / 1073741824.0, 1) },
            { "gpu_pct", Num(gpu, 0) },
            { "disk_active_pct", Num(disk, 0) },
            { "disk_read_mbs", Num(rd / 1e6, 1) },
            { "disk_write_mbs", Num(wr / 1e6, 1) },
            { "drives", drives.Select((d, i) => new Dictionary<string, object> {
                { "drive", d.Name }, { "free_gb", Math.Round(driveFree[i] / 1073741824.0) }, { "total_gb", Math.Round(driveTotal[i] / 1073741824.0) } }).ToList() },
            { "uptime_hours", Math.Round(Native.GetTickCount64() / 3600000.0, 1) },
            { "process_count", procs },
            { "battery_pct", battery ? (object)Math.Round(ps.BatteryLifePercent * 100) : null },
        };
    }
}
