using System.Drawing;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Cdx;

// Le programme : une fenêtre qui affiche (xterm.js) la sortie de plusieurs pseudo-consoles (ConPty.cs), une par volet, et leur envoie les touches.
// La page décide des onglets, des volets et de l'affichage ; ici on ne fait que le lien avec Windows : lancer les shells, faire passer les octets, le presse-papiers.
//   page → programme   ready · term.new {sid,cols,rows,cwd?} · term.in {sid,data} · term.resize {sid,cols,rows} · term.close {sid} · term.focus {sid}
//                      · term.copy {text} · term.paste {sid} · log · win.* · suite.accent
//   programme → page   hello {build,cwd?} · out {sid,b64} (octets VT/UTF-8 du shell) · exit {sid,code} · paste {sid,text} · open-tab {cwd} · theme {value}
//   bus                show · open-path {path} (un onglet dans ce dossier ; la fenêtre passe devant)
// Le numéro de session (sid) est choisi par la page ; le programme refuse un numéro déjà pris.
sealed class TerminalApp : ApplicationContext, ICdxApp
{
    // Une session : un shell dans sa pseudo-console. Les octets lus pendant que l'interface était occupée sont envoyés ensemble : un shell qui crache beaucoup de
    // texte ne fait pas un message par lecture.
    sealed class Session
    {
        public int Sid; public ConPty Pty;
        public readonly MemoryStream Pending = new(); public bool FlushQueued;
    }

    const int MaxSessions = 64, MaxMessage = 256 * 1024;

    string theme; readonly string root;
    readonly IDisposable watch;
    TerminalForm form;
    CoreWebView2Environment env;
    readonly Dictionary<int, Session> sessions = new();   // fil de l'interface seulement
    int focusSid;                                           // le volet qui a le focus (le canal de test y écrit)
    string pendingDir;                                      // dossier demandé avant que la page soit prête : le premier onglet s'y ouvre
    bool pageReady;
    public Control Anchor { get; } = new Control();         // installe le contexte de synchronisation de l'interface et sert à y poser un travail

    public TerminalApp(string theme, string startDir = null)
    {
        pendingDir = startDir;
        this.theme = theme ?? Suite.ReadAccent(); root = Host.FindWebRoot();
        _ = Anchor.Handle;
        if (theme == null) watch = Suite.Watch(this.theme, a => { this.theme = a; form?.Send(new { type = "theme", value = a }); });
    }

    public bool Closing { get; private set; }

    protected override void ExitThreadCore()
    {
        Closing = true; watch?.Dispose();
        foreach (var s in sessions.Values.ToList()) s.Pty.Dispose();
        sessions.Clear();
        base.ExitThreadCore();
    }

    // ---------- fenêtre ----------
    public async void Open()
    {
        try
        {
            if (form != null && !form.IsDisposed) { Host.ForceForeground(form.Handle); return; }
            env ??= await WebEnv.Ui();   // l'environnement partagé des programmes « outils » (WebEnv.cs)
            var f = new TerminalForm(this); form = f;
            f.FormClosed += (s, e) => ExitThread();
            double scale = f.DeviceDpi / 96.0;
            f.Bounds = new Rectangle(160, 100, (int)(1000 * scale), (int)(620 * scale));
            f.Show(); Host.ForceForeground(f.Handle);
            await f.InitWebAsync(env, root, "terminal/terminal.html?theme=" + theme, scale);
            Host.Log("Terminal ouvert");
        }
        catch (Exception ex) { Host.Log("Terminal : " + ex); }
    }

    // Un dossier qu'on accepte comme dossier de départ d'un shell : chemin complet, qui existe (un fichier donne son dossier). Sinon null.
    public static string ValidDir(string p)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(p) || p.Length > 520 || p.IndexOf((char)0) >= 0 || !Path.IsPathFullyQualified(p)) return null;
            if (File.Exists(p)) p = Path.GetDirectoryName(p);
            return p != null && Directory.Exists(p) ? p : null;
        }
        catch (Exception) { return null; }
    }

    // Messages du bus : « show » (un second lancement demande à la première instance de passer devant) et « open-path » (un onglet dans ce dossier : « Ouvrir dans un terminal » de l'Explorateur).
    public bool OnBus(string type, string from, JsonElement data)
    {
        if (type == "show") { Open(); return true; }
        if (type != "open-path") return false;
        string dir = data.TryGetProperty("path", out var pv) && pv.ValueKind == JsonValueKind.String ? ValidDir(pv.GetString()) : null;
        if (dir == null) return false;
        if (pageReady && form != null && !form.IsDisposed) { form.Send(new { type = "open-tab", cwd = dir }); Host.ForceForeground(form.Handle); }
        else { pendingDir = dir; Open(); }
        return true;
    }

    // Canal de test (debug.txt) : state · eval <js> · write <texte> (écrit dans le volet qui a le focus ; \r et \n écrits en toutes lettres deviennent des touches :
    // aucune entrée simulée sur le bureau) · write@<sid> <texte> (dans un volet précis).
    public async Task<string> HandleControl(string line)
    {
        int sp = line.IndexOf(' '); string c = sp < 0 ? line : line[..sp], a = sp < 0 ? "" : line[(sp + 1)..];
        switch (c)
        {
            case "state":
                return (form == null ? "pas de fenêtre" : "fenêtre ouverte") + ", " + (sessions.Count == 0 ? "pas de session" : sessions.Count + " session(s) : " + string.Join(", ", sessions.Values.Select(s => "session " + s.Sid + " (pid " + s.Pty.ProcessId + ")"))) + ", focus " + focusSid;
            case "eval": return form?.Web.CoreWebView2 == null ? "pas de fenêtre" : await form.Web.CoreWebView2.ExecuteScriptAsync(a);
            case "cmd": if (form != null) OnMessage(form, a); return "ok";
            default:
                if (c == "write" || c.StartsWith("write@"))
                {
                    int sid = c == "write" ? focusSid : int.TryParse(c[6..], out var n) ? n : -1;
                    if (!sessions.TryGetValue(sid, out var s)) return "pas de session " + sid;
                    s.Pty.Write(a.Replace("\\r", "\r").Replace("\\n", "\n")); return "ok";
                }
                return "commande inconnue";
        }
    }

    // ---------- les shells ----------
    // PowerShell 7 s'il est installé, sinon Windows PowerShell. Pas seulement une question de goût : Windows PowerShell 5.1 filtre ce qu'on tape par la page de code OEM
    // de la console (850 en France), et fait donc disparaître « € » de la saisie ; PowerShell 7 prend tout l'Unicode.
    // CDX_TERMINAL_SHELL remplace la commande entière (essais).
    static string ShellCommand()
    {
        var o = Environment.GetEnvironmentVariable("CDX_TERMINAL_SHELL");
        if (!string.IsNullOrWhiteSpace(o)) return o;
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "pwsh.exe"),   // l'installation du Store
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            try { candidates.Add(Path.Combine(dir.Trim(), "pwsh.exe")); } catch (ArgumentException) { }
        foreach (var c in candidates) if (File.Exists(c)) return "\"" + c + "\" -NoLogo";
        return "\"" + Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") + "\" -NoLogo";
    }

    void StartSession(int sid, int cols, int rows, string cwd)
    {
        if (sessions.ContainsKey(sid) || sessions.Count >= MaxSessions) { Host.Log("session " + sid + " refusée (déjà prise ou trop de sessions)"); return; }
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var s = new Session { Sid = sid };
            s.Pty = ConPty.Start(ShellCommand(), ValidDir(cwd) ?? (Directory.Exists(home) ? home : null), cols, rows);
            sessions[sid] = s;
            if (focusSid == 0) focusSid = sid;
            s.Pty.Output += bytes => OnOutput(s, bytes);
            s.Pty.Exited += code => Post(() =>
            {
                // Le shell s'est terminé de lui-même : la page ferme le volet (et la fenêtre avec le dernier). Une session fermée par la page n'est plus dans la liste.
                if (!sessions.TryGetValue(sid, out var cur) || cur != s) return;
                Host.Log("le shell de la session " + sid + " s'est terminé (code " + code + ")");
                Flush(s); sessions.Remove(sid); s.Pty.Dispose();
                form?.Send(new { type = "exit", sid, code });
            });
            Host.Log($"session {sid} : {ShellCommand()} ({cols}×{rows}), pid {s.Pty.ProcessId}");
        }
        catch (Exception ex)
        {
            Host.Log("shell : " + ex.Message);
            form?.Send(new { type = "out", sid, b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("\r\n\x1b[31mImpossible de lancer le shell : " + ex.Message.Replace("\r", " ").Replace("\n", " ") + "\x1b[0m\r\n")) });
        }
    }

    void CloseSession(int sid)
    {
        if (!sessions.Remove(sid, out var s)) return;
        if (focusSid == sid) focusSid = sessions.Keys.FirstOrDefault();
        s.Pty.Dispose();
    }

    void Post(Action a) { try { Anchor.BeginInvoke(a); } catch (InvalidOperationException) { /* l'interface est déjà fermée */ } }

    void OnOutput(Session s, byte[] bytes)   // fil de lecture de la pseudo-console
    {
        lock (s.Pending)
        {
            s.Pending.Write(bytes, 0, bytes.Length);
            if (s.FlushQueued) return;
            s.FlushQueued = true;
        }
        Post(() => Flush(s));
    }

    void Flush(Session s)
    {
        byte[] data;
        lock (s.Pending) { data = s.Pending.ToArray(); s.Pending.SetLength(0); s.FlushQueued = false; }
        for (int i = 0; i < data.Length; i += MaxMessage)
        {
            int n = Math.Min(MaxMessage, data.Length - i);
            form?.Send(new { type = "out", sid = s.Sid, b64 = Convert.ToBase64String(data, i, n) });
        }
    }

    // ---------- messages de la page ----------
    public void OnMessage(HostForm from, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string cmd = r.GetProperty("cmd").GetString();
            if (cmd.StartsWith("win.")) { Host.WindowCommand(from, cmd, r); return; }
            if (Host.CommonMessage(from, cmd, r)) return;
            int Int(string k, int d) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : d;
            string Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int Cols() => Math.Clamp(Int("cols", 80), 2, 1000);
            int Rows() => Math.Clamp(Int("rows", 24), 1, 1000);
            Session Sess() => sessions.TryGetValue(Int("sid", -1), out var s) ? s : null;
            switch (cmd)
            {
                case "ready": pageReady = true; from.Send(new { type = "hello", build = Environment.OSVersion.Version.Build, cwd = pendingDir }); pendingDir = null; break;
                case "term.new": StartSession(Int("sid", -1), Cols(), Rows(), Str("cwd")); break;
                case "term.close": CloseSession(Int("sid", -1)); break;
                case "term.focus": if (sessions.ContainsKey(Int("sid", -1))) focusSid = Int("sid", -1); break;
                case "term.in": { var s = Str("data"); if (s != null) Sess()?.Pty.Write(s); break; }
                case "term.resize": Sess()?.Pty.Resize(Cols(), Rows()); break;
                case "term.copy":
                    {
                        var s = Str("text");
                        if (!string.IsNullOrEmpty(s)) try { Clipboard.SetText(s); } catch (Exception ex) { Host.Log("presse-papiers : " + ex.Message); }
                        break;
                    }
                case "term.paste":
                    {
                        if (Sess() == null) break;
                        string s = null;
                        try { if (Clipboard.ContainsText()) s = Clipboard.GetText(); } catch (Exception ex) { Host.Log("presse-papiers : " + ex.Message); }
                        if (!string.IsNullOrEmpty(s)) from.Send(new { type = "paste", sid = Int("sid", -1), text = s });
                        break;
                    }
            }
        }
        catch (Exception ex) { Host.Log("message mal formé ou échec : " + ex.Message + " :: " + json); }
    }
}
