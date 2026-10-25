using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

sealed class NetWidget : Widget
{
    const string PingHost = "1.1.1.1";
    const string DownUrl = "https://speed.cloudflare.com/__down?bytes=25000000";
    const string UpUrl = "https://speed.cloudflare.com/__up";

    readonly Wlan wlan = new Wlan();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly History down = new History(60), up = new History(60);

    NetworkInterface nic;
    int nicIndex, nicRecheck;
    long lastRx = -1, lastTx;
    double lastT, downNow, upNow;
    WifiInfo wifi;
    volatile string gateway = "";
    string ipLocal = "";
    volatile IPAddress localIp, localMask;

    // Filled by background threads; reads are reference swaps, so no lock beyond the ping queue.
    readonly object pingLock = new object();
    readonly Queue<double> pings = new Queue<double>();
    double routerMs = double.NaN;
    readonly System.Threading.Timer pingTimer, scanTimer;
    volatile List<Device> devices;
    readonly Dictionary<string, string> names = new Dictionary<string, string>();
    readonly ToolTip tip = new ToolTip { InitialDelay = 200, AutoPopDelay = 30000 };
    RectangleF devRect;
    bool tipShown;

    volatile bool testing;
    volatile string testResult = "";
    DateTime testAt;

    public NetWidget() : base("net", 300, 336)
    {
        NetworkChange.NetworkAddressChanged += (s, e) => nicRecheck = 0;
        pingTimer = new System.Threading.Timer(PingLoop, null, 500, Timeout.Infinite);
        scanTimer = new System.Threading.Timer(ScanLoop, null, 3000, Timeout.Infinite);
    }

    // ---------- sampling ----------

    protected override void Tick()
    {
        if (nic == null || --nicRecheck <= 0) PickNic();
        if (nic == null) { wifi = null; down.Push(0); up.Push(0); downNow = upNow = 0; return; }

        IPInterfaceStatistics st;
        try { st = nic.GetIPStatistics(); } catch { nicRecheck = 0; return; }
        double t = clock.Elapsed.TotalSeconds;
        if (lastRx >= 0 && t > lastT)
        {
            downNow = Math.Max(0, (st.BytesReceived - lastRx) * 8 / (t - lastT));
            upNow = Math.Max(0, (st.BytesSent - lastTx) * 8 / (t - lastT));
        }
        lastRx = st.BytesReceived; lastTx = st.BytesSent; lastT = t;
        down.Push((float)downNow); up.Push((float)upNow);

        wifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? wlan.Query(new Guid(nic.Id)) : null;
    }

    static IPv4InterfaceProperties V4(NetworkInterface n)
    {
        try { return n.GetIPProperties().GetIPv4Properties(); } catch { return null; }
    }

    static string V4Gateway(NetworkInterface n)
    {
        var gw = n.GetIPProperties().GatewayAddresses
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
        return gw == null ? "" : gw.ToString();
    }

    // Physical adapter carrying traffic: the route to the internet when it is Wi-Fi/Ethernet,
    // otherwise (VPN, WARP, Hyper-V switch...) the first physical adapter with a gateway.
    void PickNic()
    {
        nicRecheck = 15;
        uint best;
        int bestIdx = Native.GetBestInterface(0x01010101, out best) == 0 ? (int)best : -1;
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                && (n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                    || n.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                    || n.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet)
                && V4(n) != null && V4Gateway(n) != "")
            .ToList();
        var pick = candidates.FirstOrDefault(n => V4(n).Index == bestIdx)
            ?? candidates.FirstOrDefault(n => n.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
            ?? candidates.FirstOrDefault();

        if (pick == null) { nic = null; gateway = ipLocal = ""; localIp = null; return; }
        if (nic == null || nic.Id != pick.Id) lastRx = -1;
        nic = pick;
        nicIndex = V4(pick).Index;
        gateway = V4Gateway(pick);
        var ua = pick.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
        localIp = ua == null ? null : ua.Address;
        localMask = ua == null ? null : ua.IPv4Mask;
        ipLocal = localIp == null ? "" : localIp.ToString();
    }

    static double PingMs(string host)
    {
        try
        {
            using (var p = new Ping())
            {
                var r = p.Send(host, 1000);
                return r.Status == IPStatus.Success ? r.RoundtripTime : -1;
            }
        }
        catch { return -1; }
    }

    void PingLoop(object _)
    {
        try
        {
            double ms = PingMs(PingHost);
            lock (pingLock) { pings.Enqueue(ms); while (pings.Count > 30) pings.Dequeue(); }
            string gw = gateway;
            routerMs = gw == "" ? double.NaN : PingMs(gw);
        }
        finally { pingTimer.Change(2000, Timeout.Infinite); }
    }

    // Average, jitter (mean change between consecutive replies) and loss over the last minute.
    void PingStats(out double avg, out double jitter, out double loss)
    {
        double[] s;
        lock (pingLock) s = pings.ToArray();
        var ok = s.Where(v => v >= 0).ToArray();
        avg = ok.Length == 0 ? double.NaN : ok.Average();
        jitter = ok.Length < 2 ? double.NaN : Enumerable.Range(1, ok.Length - 1).Average(i => Math.Abs(ok[i] - ok[i - 1]));
        loss = s.Length == 0 ? double.NaN : 100.0 * (s.Length - ok.Length) / s.Length;
    }

    // Ping sweep fills the ARP cache (even hosts that drop ICMP answer ARP), then read it.
    void ScanLoop(object _)
    {
        try
        {
            IPAddress ip = localIp, mask = localMask;
            if (ip == null || mask == null) return;
            uint me = NetMath.ToHost(ip), m = NetMath.ToHost(mask);
            if (m < 0xFFFFFF00) m = 0xFFFFFF00; // shortcut: big subnets only scan our /24, enough for home/office LANs
            uint net = me & m, bc = net | ~m;

            var tasks = new List<Task>();
            for (uint h = net + 1; h < bc; h++)
            {
                if (h == me) continue;
                var p = new Ping();
                tasks.Add(p.SendPingAsync(NetMath.FromHost(h), 500).ContinueWith(t => { var ex = t.Exception; p.Dispose(); }));
            }
            Task.WaitAll(tasks.ToArray(), 5000);
            Thread.Sleep(1000);

            var list = Arp.Read(nicIndex, bc);
            var unknown = list.Where(d => !names.ContainsKey(d.Ip.ToString())).ToList();
            Task.WaitAll(unknown.Select(d => Task.Run(() =>
            {
                string n = "";
                try { var e = Dns.GetHostEntry(d.Ip); if (e.HostName != d.Ip.ToString()) n = e.HostName; } catch { }
                lock (names) names[d.Ip.ToString()] = n;
            })).ToArray(), 6000);
            lock (names) foreach (var d in list) { string n; names.TryGetValue(d.Ip.ToString(), out n); d.Name = n ?? ""; }
            devices = list;
        }
        catch { }
        finally { scanTimer.Change(devices == null ? 30000 : 180000, Timeout.Infinite); }
    }

    protected override void OnButton()
    {
        if (testing) return;
        testing = true;
        testResult = "";
        Task.Run(() =>
        {
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
                double dl = Download(), ul = Upload();
                testResult = "↓ " + Mbps(dl) + "   ↑ " + Mbps(ul);
                testAt = DateTime.Now;
            }
            catch (WebException ex) { testResult = "Error: " + ex.Status; }
            catch (Exception ex) { testResult = "Error: " + ex.GetType().Name.Replace("Exception", ""); }
            finally { testing = false; }
        });
    }

    // Both tests run for at most 8 s and then abort the transfer, so slow links never hit a timeout.
    // Download clock starts at the first byte, so DNS/TLS setup is excluded.
    static double Download()
    {
        var buf = new byte[1 << 16];
        long total = 0;
        Stopwatch sw = null;
        // The endpoint refuses more than 25 MB per request, so chain requests until 8 s pass.
        while (sw == null || sw.ElapsedMilliseconds < 8000)
        {
            var req = (HttpWebRequest)WebRequest.Create(DownUrl);
            req.Timeout = req.ReadWriteTimeout = 10000;
            try
            {
                var s = req.GetResponse().GetResponseStream();
                int n = s.Read(buf, 0, buf.Length);
                if (sw == null) sw = Stopwatch.StartNew(); else total += n;
                while (n > 0 && sw.ElapsedMilliseconds < 8000) { n = s.Read(buf, 0, buf.Length); total += n; }
            }
            finally { req.Abort(); }
        }
        return total * 8 / sw.Elapsed.TotalSeconds;
    }

    static double Upload()
    {
        const int chunk = 1 << 16;
        var buf = new byte[chunk];
        var req = (HttpWebRequest)WebRequest.Create(UpUrl);
        req.Method = "POST";
        req.Timeout = req.ReadWriteTimeout = 10000;
        req.ContentLength = 200L * 1000 * 1000;
        req.AllowWriteStreamBuffering = false;
        try
        {
            var s = req.GetRequestStream();
            long total = 0;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 8000 && total < req.ContentLength) { s.Write(buf, 0, chunk); total += chunk; }
            return total * 8 / sw.Elapsed.TotalSeconds;
        }
        finally { req.Abort(); }
    }

    // ---------- quality rating ----------

    // Overall verdict shown in the header badge. signal = -1 on Ethernet; ping/jitter/loss NaN while unknown.
    // Jitter weighs about as much as ping: it is what breaks video calls and games.
    static string Rate(int signal, double ping, double jitter, double loss, out Color color)
    {
        int score = 100;
        if (signal >= 0) score -= signal >= 75 ? 0 : signal >= 50 ? 15 : signal >= 30 ? 35 : 60;
        if (!double.IsNaN(ping)) score -= ping <= 30 ? 0 : ping <= 80 ? 10 : ping <= 150 ? 25 : 45;
        if (!double.IsNaN(jitter)) score -= jitter <= 10 ? 0 : jitter <= 30 ? 10 : jitter <= 60 ? 20 : 35;
        if (!double.IsNaN(loss)) score -= loss < 1 ? 0 : loss < 5 ? 25 : 60;
        if (score >= 85) { color = Theme.Green; return "Excelente"; }
        if (score >= 65) { color = Theme.Green; return "Buena"; }
        if (score >= 40) { color = Theme.Amber; return "Regular"; }
        color = Theme.Red; return "Mala";
    }

    // ---------- drawing ----------

    static string Rate(double bps)
    {
        if (double.IsNaN(bps)) return "—";
        if (bps < 1e6) return (bps / 1e3).ToString("0") + " kb/s";
        return (bps / 1e6).ToString(bps < 1e7 ? "0.0" : "0") + " Mb/s";
    }
    static string Mbps(double bps) { return (bps / 1e6).ToString(bps < 1e7 ? "0.0" : "0") + " Mb/s"; }
    static string Ms(double ms) { return double.IsNaN(ms) || ms < 0 ? "—" : ms.ToString("0") + " ms"; }

    protected override void Draw(Graphics g)
    {
        const float x = 16, w = 268, r = x + w;
        double avg, jitter, loss;
        PingStats(out avg, out jitter, out loss);
        var wf = wifi;

        // Header
        Eyebrow(g, "CONEXIÓN", x, 14);
        if (nic == null) Badge(g, "Sin red", Theme.Red, r, 11);
        // The speed test saturates the line on purpose; rating it then would always say "Mala".
        else if (testing) Badge(g, "Midiendo…", Theme.Blue, r, 11);
        else
        {
            Color qc;
            string q = Rate(wf == null ? -1 : wf.Signal, avg, jitter, loss, out qc);
            if (!double.IsNaN(loss) && loss >= 100) { q = "Sin internet"; qc = Theme.Red; }
            Badge(g, q, qc, r, 11);
        }
        string name = nic == null ? "Desconectado" : wf != null ? wf.Ssid : "Ethernet";
        T(g, name, fTitle, Theme.Text, x, 30);
        string detail = nic == null ? "Sin adaptador activo"
            : wf != null ? wf.Standard + " · " + wf.Band + " · canal " + wf.Channel
            : nic.Description;
        T(g, detail, fSmall, Theme.Dim, x, 52);

        // Signal / link
        float y = 74;
        if (wf != null)
        {
            Color sc = wf.Signal >= 60 ? Theme.Green : wf.Signal >= 35 ? Theme.Amber : Theme.Red;
            T(g, "Señal " + wf.Signal + " %  ·  " + wf.Rssi + " dBm", fText, Theme.Text, x, y);
            TR(g, "Enlace " + wf.RxMbps.ToString("0") + " Mb/s", fSmall, Theme.Dim, r, y + 2);
            Bar(g, x, y + 21, w, 5, wf.Signal / 100.0, sc);
        }
        else if (nic != null)
        {
            T(g, "Enlace " + Mbps(nic.Speed), fText, Theme.Text, x, y);
        }

        // Throughput
        y = 110;
        Eyebrow(g, "↓ DESCARGA", x, y);
        Eyebrow(g, "↑ SUBIDA", x + w / 2, y);
        T(g, Rate(downNow), fBig, Theme.Blue, x, y + 13);
        T(g, Rate(upNow), fBig, Theme.Violet, x + w / 2, y + 13);
        var sr = new RectangleF(x, y + 46, w, 36);
        float max = Math.Max(Math.Max(down.Max(), up.Max()), 100000f) * 1.15f;
        Spark(g, sr, down, max, Theme.Blue, true);
        Spark(g, sr, up, max, Theme.Violet, false);

        // Latency
        y = 202;
        Color lc = double.IsNaN(avg) ? Theme.Text : avg <= 50 ? Theme.Green : avg <= 120 ? Theme.Amber : Theme.Red;
        Stat(g, "INTERNET", Ms(avg), lc, x, y);
        Stat(g, "JITTER", Ms(jitter), Theme.Text, x + 70, y);
        Stat(g, "PÉRDIDA", double.IsNaN(loss) ? "—" : loss.ToString("0") + " %", loss > 0 ? Theme.Amber : Theme.Text, x + 138, y);
        Stat(g, "ROUTER", Ms(routerMs), Theme.Text, x + 210, y);

        // Devices
        y = 244;
        Line(g, x, y, w);
        y += 10;
        var devs = devices;
        T(g, "Dispositivos en la red", fText, Theme.Text, x, y);
        TR(g, devs == null ? "…" : devs.Count.ToString(), fTitle, Theme.Text, r, y - 2);
        T(g, ipLocal == "" ? "—" : ipLocal + "  →  router " + gateway, fSmall, Theme.Dim, x, y + 18);
        devRect = new RectangleF(x, y - 4, w, 38);

        // Speed test
        y = 296;
        Button(g, new RectangleF(x, y, 136, 28), testing ? "Midiendo…" : "Test de velocidad", !testing);
        string res = testResult;
        if (res != "")
        {
            TR(g, res, fSmall, res.StartsWith("Error") ? Theme.Red : Theme.Text, r, y + 2);
            if (!res.StartsWith("Error")) TR(g, Ago(testAt), fSmall, Theme.Dim, r, y + 15);
        }
    }

    static string Ago(DateTime t)
    {
        var m = (int)(DateTime.Now - t).TotalMinutes;
        return m < 1 ? "ahora" : m < 60 ? "hace " + m + " min" : "hace " + (m / 60) + " h";
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var devs = devices;
        bool over = devs != null && devRect.Contains(Logical(e.Location));
        if (over && !tipShown)
        {
            var sb = new StringBuilder();
            foreach (var d in devs.OrderBy(d => NetMath.ToHost(d.Ip)))
                sb.AppendLine(d.Ip + "\t" + d.Mac + (d.Name == "" ? "" : "\t" + d.Name));
            tip.Show(sb.Length == 0 ? "Ningún dispositivo detectado" : sb.ToString().TrimEnd(), this, e.X + 12, e.Y + 16);
            tipShown = true;
        }
        else if (!over && tipShown) { tip.Hide(this); tipShown = false; }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (tipShown) { tip.Hide(this); tipShown = false; }
    }
}
