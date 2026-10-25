using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

static class Native
{
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();

    [DllImport("iphlpapi.dll")] public static extern int GetBestInterface(uint destAddr, out uint index);
    [DllImport("iphlpapi.dll")] public static extern int GetIpNetTable(IntPtr table, ref int size, bool order);
}

// Performance counters through PDH with English paths, so it works on any Windows language.
sealed class Pdh
{
    const uint FMT = 0x200 | 0x8000; // PDH_FMT_DOUBLE | PDH_FMT_NOCAP100
    const uint MORE_DATA = 0x800007D2;

    [StructLayout(LayoutKind.Explicit)]
    struct Value { [FieldOffset(0)] public uint CStatus; [FieldOffset(8)] public double V; }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQuery(string src, IntPtr user, out IntPtr q);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounter(IntPtr q, string path, IntPtr user, out IntPtr c);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr q);
    [DllImport("pdh.dll")] static extern uint PdhGetFormattedCounterValue(IntPtr c, uint fmt, IntPtr type, out Value v);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhGetFormattedCounterArray(IntPtr c, uint fmt, ref uint size, out uint count, IntPtr buf);

    readonly IntPtr q;
    public Pdh() { PdhOpenQuery(null, IntPtr.Zero, out q); }

    public IntPtr Add(string path)
    {
        IntPtr c;
        return PdhAddEnglishCounter(q, path, IntPtr.Zero, out c) == 0 ? c : IntPtr.Zero;
    }

    public void Collect() { PdhCollectQueryData(q); }

    public double Get(IntPtr c)
    {
        Value v;
        if (c == IntPtr.Zero || PdhGetFormattedCounterValue(c, FMT, IntPtr.Zero, out v) != 0) return double.NaN;
        return v.V;
    }

    // Sum of every instance of a wildcard counter, e.g. all 3D GPU engines.
    public double Sum(IntPtr c)
    {
        if (c == IntPtr.Zero) return double.NaN;
        uint size = 0, count;
        if (PdhGetFormattedCounterArray(c, FMT, ref size, out count, IntPtr.Zero) != MORE_DATA) return 0;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(c, FMT, ref size, out count, buf) != 0) return double.NaN;
            double s = 0;
            // PDH_FMT_COUNTERVALUE_ITEM is 24 bytes on x86 and x64; the double sits at offset 16.
            for (int i = 0; i < count; i++) s += BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buf, i * 24 + 16));
            return s;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}

sealed class WifiInfo
{
    public string Ssid, Bssid;
    public int Signal, Rssi, Channel, Phy;
    public double RxMbps, TxMbps;

    public string Standard
    {
        get
        {
            switch (Phy)
            {
                case 4: return "802.11a"; case 5: return "802.11b"; case 6: return "802.11g";
                case 7: return "Wi-Fi 4"; case 8: return "Wi-Fi 5"; case 10: return "Wi-Fi 6"; case 11: return "Wi-Fi 7";
                default: return "Wi-Fi";
            }
        }
    }
    public string Band { get { return Channel == 0 ? "" : Channel <= 14 ? "2,4 GHz" : "5 GHz"; } }
}

// Native Wi-Fi API: signal, RSSI, link rate and channel without spawning netsh.
sealed class Wlan
{
    [DllImport("wlanapi.dll")] static extern uint WlanOpenHandle(uint ver, IntPtr r, out uint neg, out IntPtr h);
    [DllImport("wlanapi.dll")] static extern uint WlanQueryInterface(IntPtr h, ref Guid g, uint op, IntPtr r, out uint size, out IntPtr data, IntPtr type);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);

    readonly IntPtr h;
    public Wlan() { uint v; if (WlanOpenHandle(2, IntPtr.Zero, out v, out h) != 0) h = IntPtr.Zero; }

    int QueryInt(Guid g, uint op)
    {
        uint size; IntPtr p;
        if (WlanQueryInterface(h, ref g, op, IntPtr.Zero, out size, out p, IntPtr.Zero) != 0) return 0;
        try { return Marshal.ReadInt32(p); } finally { WlanFreeMemory(p); }
    }

    public WifiInfo Query(Guid g)
    {
        if (h == IntPtr.Zero) return null;
        uint size; IntPtr p;
        if (WlanQueryInterface(h, ref g, 7 /* current_connection */, IntPtr.Zero, out size, out p, IntPtr.Zero) != 0) return null;
        var w = new WifiInfo();
        try
        {
            if (Marshal.ReadInt32(p, 0) != 1) return null; // wlan_interface_state_connected
            // WLAN_CONNECTION_ATTRIBUTES offsets: association attributes start at 520.
            var ssid = new byte[Math.Min(Marshal.ReadInt32(p, 520), 32)];
            Marshal.Copy(IntPtr.Add(p, 524), ssid, 0, ssid.Length);
            w.Ssid = Encoding.UTF8.GetString(ssid);
            var mac = new byte[6];
            Marshal.Copy(IntPtr.Add(p, 560), mac, 0, 6);
            w.Bssid = BitConverter.ToString(mac).Replace('-', ':').ToLowerInvariant();
            w.Phy = Marshal.ReadInt32(p, 568);
            w.Signal = Marshal.ReadInt32(p, 576);
            w.RxMbps = Marshal.ReadInt32(p, 580) / 1000.0;
            w.TxMbps = Marshal.ReadInt32(p, 584) / 1000.0;
        }
        finally { WlanFreeMemory(p); }
        w.Rssi = QueryInt(g, 0x10000102);
        w.Channel = QueryInt(g, 8);
        return w;
    }
}

sealed class Device { public IPAddress Ip; public string Mac, Name; }

static class Arp
{
    // IPv4 neighbours learnt on one interface (MIB_IPNETROW = 24 bytes).
    public static List<Device> Read(int ifIndex, uint broadcast)
    {
        var list = new List<Device>();
        int size = 0;
        Native.GetIpNetTable(IntPtr.Zero, ref size, true);
        if (size <= 0) return list;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (Native.GetIpNetTable(buf, ref size, true) != 0) return list;
            int n = Marshal.ReadInt32(buf);
            for (int i = 0; i < n; i++)
            {
                int off = 4 + i * 24;
                int type = Marshal.ReadInt32(buf, off + 20);
                if (Marshal.ReadInt32(buf, off) != ifIndex || (type != 3 && type != 4) || Marshal.ReadInt32(buf, off + 4) != 6) continue;
                var mac = new byte[6];
                Marshal.Copy(IntPtr.Add(buf, off + 8), mac, 0, 6);
                if ((mac[0] & 1) != 0) continue; // multicast / broadcast MAC
                uint addr = (uint)Marshal.ReadInt32(buf, off + 16);
                var ip = new IPAddress(addr);
                byte first = ip.GetAddressBytes()[0];
                if (first >= 224 || NetMath.ToHost(ip) == broadcast) continue;
                list.Add(new Device { Ip = ip, Mac = BitConverter.ToString(mac).Replace('-', ':').ToLowerInvariant() });
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }
}

static class NetMath
{
    public static uint ToHost(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }
    public static IPAddress FromHost(uint v)
    {
        return new IPAddress(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
    }
}
