using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Kdx;

// Un programme de la suite : reçoit les messages des pages de ses fenêtres.
interface IHostApp
{
    void OnMessage(HostForm from, string json);
}

// Ce que les trois programmes (shell, Explorateur, navigateur) font de la même façon.
static class Host
{
    public static string AppName = "shell";
    // KDX_DATA_DIR remplace le dossier des données (réservé aux essais isolés : ni le vrai profil du navigateur, ni les vrais journaux).
    public static string DataDir => Environment.GetEnvironmentVariable("KDX_DATA_DIR") is { Length: > 0 } d ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KdxShell");

    public static void Init(string app)
    {
        AppName = app;
        Directory.CreateDirectory(DataDir);
    }

    // Un journal par programme : journal.log (shell), journal-explorer.log, journal-browser.log.
    public static void Log(string m)
    {
        try { File.AppendAllText(Path.Combine(DataDir, AppName == "shell" ? "journal.log" : $"journal-{AppName}.log"), DateTime.Now.ToString("HH:mm:ss.fff ") + m + "\n"); } catch { }
    }

    // « --theme <nom> » puis, facultatif, une cible (chemin d'un dossier, adresse web...).
    public static (string theme, string target) ParseArgs(string[] args)
    {
        string theme = null, target = null;   // theme null : celui de la suite (suite.json)
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--theme" && i + 1 < args.Length) theme = args[++i];
            else if (!args[i].StartsWith("--") && target == null) target = args[i];
        }
        return (theme, target);
    }

    // Le dossier des pages (web/) : celui qui contient kdx/kdx-ui.css, en remontant depuis l'exécutable (ou posé à côté de lui une fois installé).
    public static string FindWebRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "web", "kdx", "kdx-ui.css"))) return Path.Combine(d.FullName, "web");
            if (File.Exists(Path.Combine(d.FullName, "kdx", "kdx-ui.css"))) return d.FullName;
        }
        throw new DirectoryNotFoundException("web/kdx/kdx-ui.css introuvable au-dessus de " + AppContext.BaseDirectory);
    }

    // Windows refuse parfois SetForegroundWindow à un processus qui n'a pas le focus (ce qui est notre cas quand la demande vient d'un crochet
    // clavier) : on se rattache alors un instant au fil de la fenêtre qui l'a.
    public static void ForceForeground(IntPtr h)
    {
        if (!N.IsWindow(h)) return;
        if (N.IsIconic(h)) N.ShowWindow(h, 9 /* SW_RESTORE */);
        if (N.SetForegroundWindow(h)) return;
        uint fgThread = N.GetWindowThreadProcessId(N.GetForegroundWindow(), out _), me = N.GetCurrentThreadId();
        N.AttachThreadInput(me, fgThread, true); N.SetForegroundWindow(h); N.AttachThreadInput(me, fgThread, false);
    }

    // Les messages « win.* » des fenêtres sans cadre de Windows : la page dessine son cadre et nous demande de déplacer, redimensionner, réduire...
    public static void WindowCommand(HostForm f, string cmd, JsonElement r)
    {
        switch (cmd)
        {
            case "win.drag": N.ReleaseCapture(); N.SendMessage(f.Handle, 0x00A1 /* WM_NCLBUTTONDOWN */, new IntPtr(2) /* HTCAPTION */, IntPtr.Zero); break;
            case "win.resize": N.ReleaseCapture(); N.SendMessage(f.Handle, 0x00A1, new IntPtr(r.GetProperty("edge").GetInt32()), IntPtr.Zero); break;
            case "win.min": f.WindowState = FormWindowState.Minimized; break;
            case "win.max":
                if (f.WindowState == FormWindowState.Maximized) f.WindowState = FormWindowState.Normal;
                else f.Maximize();
                break;
            case "win.close": f.Close(); break;
            case "win.title": f.Text = r.GetProperty("t").GetString(); break;
        }
    }

    // L'icône d'un fichier ou d'un programme, en PNG (base64) à la taille demandée, gardée en mémoire.
    static readonly Dictionary<string, string> icons = new();

    // Vrai dans le shell : l'icône d'un raccourci (.lnk) est celle de son programme, sans la flèche de raccourci que Windows superpose à celle du fichier .lnk.
    // L'Explorateur garde la flèche (c'est bien un raccourci dans une liste de fichiers).
    public static bool ResolveShortcuts;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint n);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    // L'image de l'icône d'un raccourci : le fichier et l'index d'icône qu'il désigne (IconLocation), sinon son programme ; null si on ne sait pas la résoudre
    // (raccourci vers une appli du Store, sans cible) : l'appelant garde alors l'icône du .lnk.
    static Bitmap ShortcutIcon(string lnk)
    {
        object sh = null, sc = null;
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell"); if (t == null) return null;
            sh = Activator.CreateInstance(t);
            sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
            string Get(string n) => sc.GetType().InvokeMember(n, System.Reflection.BindingFlags.GetProperty, null, sc, null) as string ?? "";
            string target = Get("TargetPath"), loc = Get("IconLocation"), file; int idx = 0;
            int comma = loc.LastIndexOf(',');
            if (comma >= 0) { file = loc[..comma]; int.TryParse(loc[(comma + 1)..], out idx); } else file = loc;
            if (string.IsNullOrWhiteSpace(file)) file = target;
            file = Environment.ExpandEnvironmentVariables(file.Trim().Trim('"'));
            if (!File.Exists(file)) return null;
            var large = new IntPtr[1]; var small = new IntPtr[1];
            if (ExtractIconEx(file, idx, large, small, 1) > 0 && large[0] != IntPtr.Zero)
            {
                try { using var ic = Icon.FromHandle(large[0]); return new Bitmap(ic.ToBitmap()); }
                finally { DestroyIcon(large[0]); if (small[0] != IntPtr.Zero) DestroyIcon(small[0]); }
            }
            if (File.Exists(target)) { using var ic = Icon.ExtractAssociatedIcon(target); return ic == null ? null : new Bitmap(ic.ToBitmap()); }
            return null;
        }
        catch { return null; }
        finally { if (sc != null) Marshal.FinalReleaseComObject(sc); if (sh != null) Marshal.FinalReleaseComObject(sh); }
    }

    public static string IconPng(string path, int size = 16)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string ck = size + "|" + path;
        if (icons.TryGetValue(ck, out var cached)) return cached;
        string png = null;
        try
        {
            using var clean = ResolveShortcuts && path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutIcon(path) : null;
            using var big = clean == null ? Icon.ExtractAssociatedIcon(path) : null;
            if (clean != null || big != null)
            {
                using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    if (clean != null) g.DrawImage(clean, new Rectangle(0, 0, size, size)); else g.DrawIcon(big, new Rectangle(0, 0, size, size));
                }
                using var ms = new MemoryStream(); bmp.Save(ms, ImageFormat.Png); png = Convert.ToBase64String(ms.ToArray());
            }
        }
        catch (Exception ex) { Log("icône de " + path + " : " + ex.Message); }
        icons[ck] = png;
        return png;
    }

    // Les messages communs à toutes les pages : « log » et « icon ». True = traité.
    public static bool CommonMessage(HostForm from, string cmd, JsonElement r)
    {
        switch (cmd)
        {
            case "log": Log($"[{from.Name}] {(r.TryGetProperty("msg", out var m) ? m.GetString() : "")}"); return true;
            case "icon":
            {
                string p = r.TryGetProperty("path", out var pv) ? pv.GetString() : null;
                int sz = r.TryGetProperty("size", out var szv) && szv.TryGetInt32(out var szi) ? szi : 16;
                from.Send(new { type = "icon", path = p, size = sz, png = IconPng(p, sz) });
                return true;
            }
        }
        return false;
    }

    // ---------- canal de test ----------
    // Actif seulement si %LOCALAPPDATA%\KdxShell\debug.txt existe, et réservé à l'utilisateur courant : permet de commander un programme et de lire
    // son état sans aucune entrée simulée. `handle` reçoit une ligne et rend la réponse ; il est appelé sur le fil de l'interface (`ui`).
    public static void StartControlPipe(string app, Control ui, Func<string, Task<string>> handle, Func<bool> done)
    {
        string pipeName = Bus.PipeName(app) + ".ctl";
        if (!File.Exists(Path.Combine(DataDir, "debug.txt"))) return;
        new Thread(() =>
        {
            while (!done())
            {
                try
                {
                    using var srv = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    srv.WaitForConnection();
                    using var rd = new StreamReader(srv); using var wr = new StreamWriter(srv) { AutoFlush = true };
                    string line = rd.ReadLine() ?? "", result = null; var fin = new ManualResetEventSlim();
                    ui.BeginInvoke(new Action(async () => { try { result = await handle(line); } catch (Exception ex) { result = "ERREUR " + ex.Message; } fin.Set(); }));
                    fin.Wait(30000);
                    wr.WriteLine((result ?? "délai dépassé").Replace("\r", " ").Replace("\n", " "));
                }
                catch (Exception ex) { Log("canal de contrôle : " + ex.Message); Thread.Sleep(500); }
            }
        }) { IsBackground = true }.Start();
        Log($"canal de contrôle actif ({pipeName}, debug.txt)");
    }
}
