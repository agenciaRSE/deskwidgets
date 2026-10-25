using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Runs Claude Code in headless mode (`claude -p`), so the analysis uses the Claude subscription
// the user is logged in with. No API key is stored by this app.
static class Claude
{
    const string Schema = "{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"status\",\"summary\",\"actions\"],\"properties\":{"
        + "\"status\":{\"type\":\"string\",\"enum\":[\"ok\",\"warning\",\"critical\"]},"
        + "\"summary\":{\"type\":\"string\"},"
        + "\"actions\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"additionalProperties\":false,"
        + "\"required\":[\"kind\",\"title\",\"detail\",\"process\",\"ip\",\"mac\"],\"properties\":{"
        + "\"kind\":{\"type\":\"string\",\"enum\":[\"close_process\",\"remove_device\",\"info\"]},"
        + "\"title\":{\"type\":\"string\"},\"detail\":{\"type\":\"string\"},"
        + "\"process\":{\"type\":\"string\"},\"ip\":{\"type\":\"string\"},\"mac\":{\"type\":\"string\"}}}}}}";

    const string SystemPrompt =
        "Eres un técnico experto en rendimiento de Windows y redes domésticas. Recibes por stdin un JSON con el estado " +
        "actual de un PC (CPU, RAM, GPU, disco, procesos agrupados por nombre) y de su conexión (Wi-Fi, latencia, pérdida, " +
        "tráfico, dispositivos de la red local con IP, MAC y nombre). Responde siempre en español, claro y sin jerga.\n" +
        "Reglas:\n" +
        "- summary: 2-4 frases con el diagnóstico y la causa más probable de lo que va lento.\n" +
        "- actions: como máximo 6, de mayor a menor impacto. Solo acciones justificadas por los datos.\n" +
        "- close_process: solo procesos de la lista con nombre exacto en 'process', que sean aplicaciones de usuario y " +
        "cuyo cierre libere CPU o RAM de forma notable. Nunca procesos del sistema, de seguridad, controladores, " +
        "explorer, svchost, ni DeskWidgets o claude. Avisa en 'detail' si cerrarlo puede perder trabajo sin guardar.\n" +
        "- remove_device: solo dispositivos de la lista que parezcan desconocidos o sospechosos, o que puedan saturar la red; " +
        "rellena 'ip' y 'mac' exactos y explica por qué (fabricante probable por el OUI de la MAC). El router y este PC no.\n" +
        "- info: cualquier otra recomendación (canal o banda Wi-Fi, cobertura, disco lleno, reiniciar, etc.).\n" +
        "- Deja en blanco los campos que no apliquen. Si todo va bien, status ok y pocas acciones.";

    // Windows command-line quoting rules (CommandLineToArgvW).
    static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in s)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') sb.Append('\\', slashes * 2 + 1);
            else sb.Append('\\', slashes);
            slashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }

    public static Dictionary<string, object> Analyze(string snapshotJson)
    {
        var args = string.Join(" ", new[]
        {
            "-p", Quote("Analiza el estado del PC y de la red que llega por stdin y dame recomendaciones."),
            "--system-prompt", Quote(SystemPrompt),
            "--output-format", "json",
            "--json-schema", Quote(Schema),
            "--model", "opus",
            "--tools", "\"\"",
            "--setting-sources", "\"\"",
            "--strict-mcp-config",
            "--disable-slash-commands",
            "--no-session-persistence",
        });
        var psi = new ProcessStartInfo("claude", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetTempPath(),
        };

        Process p;
        try { p = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new Exception("No se encontró Claude Code. Instálalo y ejecuta «claude» una vez para iniciar sesión con tu suscripción.");
        }
        using (p)
        {
            using (var stdin = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false))) stdin.Write(snapshotJson);
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(180000))
            {
                try { p.Kill(); } catch { }
                throw new Exception("Claude no respondió en 3 minutos.");
            }
            string output = outTask.Result, error = errTask.Result;
            Dictionary<string, object> res;
            try { res = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(output); }
            catch { throw new Exception(FirstLine(error != "" ? error : output, "Claude devolvió una respuesta ilegible.")); }
            object isError, structured;
            if (res.TryGetValue("is_error", out isError) && true.Equals(isError))
                throw new Exception(FirstLine(res.ContainsKey("result") ? res["result"] as string : null, "Claude devolvió un error. ¿Has iniciado sesión? Ejecuta «claude» en una terminal."));
            if (!res.TryGetValue("structured_output", out structured) || !(structured is Dictionary<string, object>))
                throw new Exception("Claude no devolvió el análisis estructurado.");
            return (Dictionary<string, object>)structured;
        }
    }

    static string FirstLine(string s, string fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        s = s.Trim();
        int i = s.IndexOf('\n');
        return i > 0 ? s.Substring(0, i).Trim() : s;
    }
}

static class Procs
{
    // Never offered for closing, whatever the model says.
    static readonly HashSet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
        "lsaiso", "svchost", "explorer", "dwm", "fontdrvhost", "sihost", "ctfmon", "taskhostw", "spoolsv", "audiodg",
        "conhost", "dllhost", "RuntimeBroker", "SearchHost", "SearchIndexer", "StartMenuExperienceHost",
        "ShellExperienceHost", "TextInputHost", "SecurityHealthService", "SecurityHealthSystray", "MsMpEng", "NisSrv",
        "MpDefenderCoreService", "WmiPrvSE", "WUDFHost", "dasHost", "LsaIso", "DeskWidgets", "claude",
    };

    public static bool CanClose(string name)
    {
        // Our own host too (powershell when launched via DeskWidgets.ps1): closing it would kill the widgets.
        return !string.IsNullOrEmpty(name) && !Protected.Contains(name)
            && !name.Equals(Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase);
    }

    // CPU (sampled over 1.5 s) and RAM per process name, top 25, for the model to reason about.
    public static List<Dictionary<string, object>> Top()
    {
        var procs = Process.GetProcesses();
        var t0 = new Dictionary<int, TimeSpan>();
        foreach (var p in procs) try { t0[p.Id] = p.TotalProcessorTime; } catch { }
        var sw = Stopwatch.StartNew();
        Thread.Sleep(1500);
        double elapsed = sw.Elapsed.TotalMilliseconds * Environment.ProcessorCount;

        var groups = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase); // cpu, ram, count, window
        foreach (var p in procs)
        {
            try
            {
                double cpu = 0;
                TimeSpan a;
                if (t0.TryGetValue(p.Id, out a)) { p.Refresh(); cpu = (p.TotalProcessorTime - a).TotalMilliseconds / elapsed * 100; }
                double[] g;
                if (!groups.TryGetValue(p.ProcessName, out g)) groups[p.ProcessName] = g = new double[4];
                g[0] += cpu;
                g[1] += p.WorkingSet64 / 1048576.0;
                g[2]++;
                if (p.MainWindowHandle != IntPtr.Zero) g[3] = 1;
            }
            catch { }
            finally { p.Dispose(); }
        }
        var top = groups.OrderByDescending(kv => kv.Value[0]).Take(15)
            .Union(groups.OrderByDescending(kv => kv.Value[1]).Take(15))
            .Take(25);
        return top.Select(kv => new Dictionary<string, object>
        {
            { "name", kv.Key },
            { "cpu_pct", Math.Round(kv.Value[0], 1) },
            { "ram_mb", Math.Round(kv.Value[1]) },
            { "instances", (int)kv.Value[2] },
            { "has_window", kv.Value[3] > 0 },
            { "closable", CanClose(kv.Key) },
        }).ToList();
    }

    // Closes every instance of an app in the user's session: windowed ones politely first.
    public static int Close(string name)
    {
        int session = Process.GetCurrentProcess().SessionId, closed = 0;
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                try
                {
                    if (p.SessionId != session) continue;
                    if (!(p.MainWindowHandle != IntPtr.Zero && p.CloseMainWindow() && p.WaitForExit(3000))) p.Kill();
                    closed++;
                }
                catch { }
            }
        }
        return closed;
    }
}

sealed class AdvisorForm : Form
{
    readonly Func<Dictionary<string, object>> snapshot;
    readonly Func<string> gateway;
    readonly FlowLayoutPanel list;
    readonly Label status, summary;
    readonly Button again;
    readonly ProgressBar busy;
    bool running;

    public AdvisorForm(Func<Dictionary<string, object>> snapshot, Func<string> gateway)
    {
        this.snapshot = snapshot;
        this.gateway = gateway;
        Text = "DeskWidgets · Análisis con Claude";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 10f);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(560, 640);
        MinimumSize = new Size(420, 400);

        var top = new Panel { Dock = DockStyle.Top, Height = 58, Padding = new Padding(20, 16, 20, 8) };
        status = new Label { Dock = DockStyle.Top, Height = 28, Font = new Font("Segoe UI Semibold", 13f), ForeColor = Theme.Text };
        // The summary scrolls with the cards, so long diagnoses are never cut off.
        summary = new Label { AutoSize = true, ForeColor = Theme.Dim, Margin = new Padding(4, 0, 4, 12) };
        busy = new ProgressBar { Dock = DockStyle.Bottom, Height = 4, Style = ProgressBarStyle.Marquee, Visible = false };
        top.Controls.Add(busy);
        top.Controls.Add(status);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(20, 10, 20, 12) };
        again = FlatButton("Volver a analizar", true);
        again.Dock = DockStyle.Right;
        again.Width = 170;
        again.Click += (s, e) => Run();
        again.EnabledChanged += (s, e) => again.BackColor = again.Enabled ? Theme.Blue : Theme.Track;
        bottom.Controls.Add(again);

        list = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16, 4, 16, 4) };
        list.Resize += (s, e) =>
        {
            foreach (Control c in list.Controls)
                if (c is Label) c.MaximumSize = new Size(CardWidth(), 0); else c.Width = CardWidth();
        };

        Controls.Add(list);
        Controls.Add(bottom);
        Controls.Add(top);
    }

    int CardWidth() { return list.ClientSize.Width - list.Padding.Horizontal - 6; }

    void Summary(string text)
    {
        summary.Text = text;
        summary.MaximumSize = new Size(CardWidth(), 0);
        if (!list.Controls.Contains(summary)) { list.Controls.Add(summary); list.Controls.SetChildIndex(summary, 0); }
    }

    static Button FlatButton(string text, bool primary)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Theme.Blue : Theme.Track,
            ForeColor = primary ? Theme.Bg : Theme.Text,
            Height = 32,
            AutoSize = true,
            Cursor = Cursors.Hand,
            Padding = new Padding(10, 0, 10, 0),
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(Theme.Blue) : Theme.Hot;
        return b;
    }

    public void Run()
    {
        if (running) return;
        running = true;
        again.Enabled = false;
        busy.Visible = true;
        status.Text = "Analizando con Claude…";
        status.ForeColor = Theme.Text;
        list.Controls.Clear();
        Summary("Midiendo procesos y enviando el estado del PC y de la red a Claude. Suele tardar entre 20 segundos y 2 minutos; puedes seguir trabajando.");

        var data = snapshot();
        Task.Run(() =>
        {
            data["processes"] = Procs.Top();
            return Claude.Analyze(new JavaScriptSerializer().Serialize(data));
        }).ContinueWith(t =>
        {
            running = false;
            again.Enabled = true;
            busy.Visible = false;
            if (t.IsFaulted) ShowError(t.Exception.GetBaseException().Message);
            else Render(t.Result);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void ShowError(string msg)
    {
        status.Text = "No se pudo completar el análisis";
        status.ForeColor = Theme.Red;
        Summary(msg + "\nTus datos no se han modificado. Pulsa «Volver a analizar» para reintentarlo.");
    }

    static string Str(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? v.ToString() : ""; }

    void Render(Dictionary<string, object> r)
    {
        string st = Str(r, "status");
        status.Text = st == "critical" ? "Hay problemas importantes" : st == "warning" ? "Se puede mejorar" : "Todo en orden";
        status.ForeColor = st == "critical" ? Theme.Red : st == "warning" ? Theme.Amber : Theme.Green;
        Summary(Str(r, "summary") + "\nAnalizado a las " + DateTime.Now.ToString("HH:mm") + ".");

        var actions = r.ContainsKey("actions") ? r["actions"] as IEnumerable : null;
        if (actions == null) return;
        foreach (var o in actions.OfType<Dictionary<string, object>>()) list.Controls.Add(Card(o));
        if (list.Controls.Count == 0) list.Controls.Add(new Label { Text = "Sin recomendaciones.", ForeColor = Theme.Dim, AutoSize = true, Margin = new Padding(4, 8, 4, 4) });
    }

    Control Card(Dictionary<string, object> a)
    {
        string kind = Str(a, "kind"), proc = Str(a, "process"), ip = Str(a, "ip"), mac = Str(a, "mac");
        var card = new TableLayoutPanel
        {
            Width = CardWidth(),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Color.FromArgb(28, 31, 39),
            Padding = new Padding(14, 12, 14, 12),
            Margin = new Padding(0, 0, 0, 10),
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        string tag = kind == "close_process" ? "PROCESO" : kind == "remove_device" ? "DISPOSITIVO" : "CONSEJO";
        card.Controls.Add(new Label { Text = tag, ForeColor = Theme.Dim, Font = new Font("Segoe UI", 8f), AutoSize = true });
        card.Controls.Add(new Label { Text = Str(a, "title"), Font = new Font("Segoe UI Semibold", 11f), ForeColor = Theme.Text, AutoSize = true, MaximumSize = new Size(CardWidth() - 30, 0) });
        card.Controls.Add(new Label { Text = Str(a, "detail"), ForeColor = Theme.Dim, AutoSize = true, MaximumSize = new Size(CardWidth() - 30, 0), Margin = new Padding(3, 4, 3, 8) });

        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
        if (kind == "close_process" && Procs.CanClose(proc) && Process.GetProcessesByName(proc).Length > 0)
        {
            var b = FlatButton("Cerrar " + proc, true);
            b.Click += (s, e) =>
            {
                if (MessageBox.Show(this, "Se cerrarán todas las ventanas y procesos de «" + proc + "». Lo que no esté guardado se perderá.\n\n¿Cerrar ahora?",
                    "Cerrar " + proc, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                int n = Procs.Close(proc);
                b.Enabled = false;
                b.Text = n > 0 ? "Cerrado (" + n + ")" : "No se pudo cerrar";
            };
            row.Controls.Add(b);
        }
        else if (kind == "remove_device")
        {
            // Windows cannot disconnect other devices; that is done from the router's admin page.
            string gw = gateway();
            if (gw != "")
            {
                var b = FlatButton("Abrir el router", true);
                b.Click += (s, e) => { try { Process.Start("http://" + gw); } catch { } };
                row.Controls.Add(b);
            }
            if (mac != "")
            {
                var c = FlatButton("Copiar MAC", false);
                c.Click += (s, e) => { Clipboard.SetText(mac); c.Text = "MAC copiada"; };
                row.Controls.Add(c);
            }
            card.Controls.Add(new Label { Text = (ip == "" ? "" : ip + "  ·  ") + mac, ForeColor = Theme.Dim, Font = new Font("Consolas", 9f), AutoSize = true });
        }
        if (row.Controls.Count > 0) card.Controls.Add(row);
        return card;
    }
}
