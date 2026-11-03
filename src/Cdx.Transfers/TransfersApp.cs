using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Cdx;

// Le programme : une fenêtre qui montre tous les transferts de la suite. Il ne fait aucun transfert lui-même : il tient la liste de ce que les autres programmes annoncent (« xfer »), la montre,
// et leur renvoie les ordres de la page (« xfer-ctl »). Ce qu'il garde sur disque : les derniers transferts terminés (transferts.json).
sealed class TransfersApp : ApplicationContext, IHostApp
{
    sealed class Entry
    {
        public string Key = "", App = "", Kind = "", Title = "", State = "", From = "", To = "", Current = "", Error = "", Path = "";
        public int Id; public long Done, Total; public double Speed; public int Items; public bool CanPause, CanRetry, Hist;
        public JsonElement? Extra;
        public DateTime Seen, Started, Ended;
        public bool Live => State is "queued" or "running" or "paused";
    }

    static readonly string[] Kinds = { "copy", "move", "delete", "download", "torrent" };
    static readonly string[] States = { "queued", "running", "paused", "done", "failed", "canceled" };
    static readonly string[] Risky = { ".exe", ".msi", ".bat", ".cmd", ".com", ".scr", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".hta", ".lnk", ".jar", ".reg", ".pif", ".cpl", ".msc" };
    const int KeepHistory = 100;

    string theme;
    readonly string root;
    readonly IDisposable watch;
    TransfersForm form;
    CoreWebView2Environment env;
    readonly Dictionary<string, Entry> entries = new();
    readonly HashSet<string> dirty = new(), removed = new();
    readonly System.Windows.Forms.Timer flush = new() { Interval = 250 }, sweep = new() { Interval = 3000 };
    bool historyDirty; int histSeq;
    public readonly Control Anchor = new Control();   // installe le contexte de synchronisation de l'interface et sert à y poser un travail

    static string HistFile => Path.Combine(Host.DataDir, "transferts.json");

    public TransfersApp(string theme)
    {
        this.theme = theme ?? Suite.ReadAccent(); root = Host.FindWebRoot();
        _ = Anchor.Handle;
        LoadHistory();
        if (theme == null) watch = Suite.Watch(this.theme, a => { this.theme = a; form?.Send(new { type = "theme", value = a }); });
        flush.Tick += (s, e) => Flush(); flush.Start();
        sweep.Tick += (s, e) => Sweep(); sweep.Start();
    }

    public bool Closing { get; private set; }

    protected override void ExitThreadCore() { Closing = true; flush.Dispose(); sweep.Dispose(); watch?.Dispose(); SaveHistory(); base.ExitThreadCore(); }

    // ---------- historique ----------
    void LoadHistory()
    {
        try
        {
            if (!File.Exists(HistFile)) return;
            using var d = JsonDocument.Parse(File.ReadAllText(HistFile));
            foreach (var v in d.RootElement.GetProperty("items").EnumerateArray())
            {
                string S(string k) => v.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : "";
                long L(string k) => v.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetInt64(out var n) ? n : 0;
                var e = new Entry { Key = "h" + (++histSeq), App = S("app"), Kind = S("kind"), Title = S("title"), State = S("state"), From = S("from"), To = S("to"), Error = S("error"), Path = S("path"),
                    Done = L("done"), Total = L("total"), Items = (int)L("items"), Hist = true, Started = DateTimeOffset.FromUnixTimeMilliseconds(L("started")).UtcDateTime, Ended = DateTimeOffset.FromUnixTimeMilliseconds(L("ended")).UtcDateTime };
                if (Kinds.Contains(e.Kind) && !e.Live && States.Contains(e.State)) entries[e.Key] = e;
            }
        }
        catch (Exception ex) { Host.Log("transferts.json illisible, historique vide : " + ex.Message); }
    }

    void SaveHistory()
    {
        if (!historyDirty) return; historyDirty = false;
        try
        {
            var fin = entries.Values.Where(e => !e.Live).OrderByDescending(e => e.Ended).Take(KeepHistory).Reverse().Select(e => new
            {
                app = e.App, kind = e.Kind, title = e.Title, state = e.State, from = e.From, to = e.To, error = e.Error, path = e.Path, done = e.Done, total = e.Total, items = e.Items,
                started = new DateTimeOffset(e.Started, TimeSpan.Zero).ToUnixTimeMilliseconds(), ended = new DateTimeOffset(e.Ended, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            }).ToList();
            AtomicFile.WriteJson(HistFile, new { version = 1, items = fin });
        }
        catch (Exception ex) { Host.Log("transferts.json : " + ex.Message); }
    }

    // On ne garde que les 100 derniers terminés.
    void Trim()
    {
        var old = entries.Values.Where(e => !e.Live).OrderByDescending(e => e.Ended).Skip(KeepHistory).Select(e => e.Key).ToList();
        foreach (var k in old) { entries.Remove(k); removed.Add(k); dirty.Remove(k); }
    }

    // ---------- fenêtre ----------
    public async void Open()
    {
        try
        {
            if (form != null && !form.IsDisposed) { if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal; Host.ForceForeground(form.Handle); return; }
            env ??= await CoreWebView2Environment.CreateAsync(null, Path.Combine(Host.DataDir, "webview-transfers"), new CoreWebView2EnvironmentOptions("--disable-lcd-text"));
            var f = new TransfersForm(this); form = f;
            f.FormClosed += (s, e) => ExitThread();
            double scale = f.DeviceDpi / 96.0;
            var wa = Screen.PrimaryScreen.WorkingArea;
            int w = (int)(760 * scale), h = (int)(520 * scale);
            f.Bounds = new Rectangle(wa.Right - w - (int)(24 * scale), wa.Bottom - h - (int)(24 * scale), w, h);   // en bas à droite, hors du chemin
            f.Show(); Host.ForceForeground(f.Handle);
            await f.InitWebAsync(env, root, "transfers/transfers.html?theme=" + theme, scale);
            Host.Log("Transferts ouvert");
        }
        catch (Exception ex) { Host.Log("Transferts : " + ex); }
    }

    // Les programmes producteurs redisent ce qui est en cours (on vient de naître, ou de se relancer).
    void AskSync() { _ = Task.Run(() => { foreach (var a in new[] { "explorer", "browser" }) Bus.Send(a, "xfer-sync", new { }, "transfers"); }); }

    // ---------- messages du bus ----------
    // show · xfer { id, kind, title, state, done, total, speed, ... } (validé champ par champ : un message mal formé est refusé).
    public bool OnBus(string type, string from, JsonElement data)
    {
        if (type == "show") { Open(); return true; }
        if (type != "xfer") return false;
        if (!Bus.Apps.Contains(from) || from == "transfers") return false;
        string S(string k, int max = 300) { if (data.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String) { string s = v.GetString(); return s.Length > max ? s[..max] : s; } return ""; }
        long L(string k) => data.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? Math.Max(n, -1) : 0;
        double D(string k) => data.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && !double.IsNaN(n) && !double.IsInfinity(n) ? Math.Max(n, 0) : 0;
        bool B(string k) => data.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
        if (!data.TryGetProperty("id", out var idv) || idv.ValueKind != JsonValueKind.Number || !idv.TryGetInt32(out int id) || id < 0) return false;
        string kind = S("kind", 20), state = S("state", 20);
        if (!Kinds.Contains(kind) || !States.Contains(state)) return false;

        string key = from + "#" + id;
        bool isNew = !entries.TryGetValue(key, out var e);
        if (isNew) { e = new Entry { Key = key, App = from, Id = id, Started = DateTime.UtcNow }; entries[key] = e; }
        else if (!e.Live && state is "queued" or "running" or "paused") { e.Started = DateTime.UtcNow; e.Ended = default; }   // le même numéro repart (nouvel essai)
        bool wasLive = !isNew && e.Live;
        e.Kind = kind; e.Title = S("title"); e.State = state; e.From = S("from"); e.To = S("to"); e.Current = S("current"); e.Error = S("error", 500); e.Path = S("path", 520);
        e.Done = L("done"); e.Total = L("total"); e.Speed = D("speed"); e.Items = (int)Math.Min(L("items"), int.MaxValue); e.CanPause = B("canPause"); e.CanRetry = B("canRetry");
        e.Extra = data.TryGetProperty("extra", out var x) && x.ValueKind == JsonValueKind.Object ? x.Clone() : null;
        e.Seen = DateTime.UtcNow; e.Hist = false;
        dirty.Add(key);
        if (!e.Live && (isNew || wasLive)) Finished(e);
        return true;
    }

    // Un transfert vient de se terminer : l'historique le garde, et la barre du shell le dit s'il a duré (le navigateur annonce déjà ses téléchargements).
    void Finished(Entry e)
    {
        e.Ended = DateTime.UtcNow; historyDirty = true; Trim();
        if (e.Kind == "download" || (e.Ended - e.Started).TotalSeconds < 5 || e.State == "canceled") return;
        string title = e.State == "done" ? (e.Kind == "delete" ? "Suppression terminée" : e.Kind == "move" ? "Déplacement terminé" : e.Kind == "torrent" ? "Torrent terminé" : "Copie terminée") : "Transfert échoué";
        string text = e.Title + (e.State == "failed" && e.Error.Length > 0 ? " : " + e.Error : ""), level = e.State == "done" ? "info" : "warn";
        _ = Task.Run(() => Bus.Send("shell", "notify", new { title, text, level }, "transfers"));
    }

    // Un programme qui ne donne plus signe de vie (fermé, planté) : ses transferts en cours sont déclarés interrompus.
    void Sweep()
    {
        var now = DateTime.UtcNow;
        foreach (var e in entries.Values.Where(e => e.Live && (now - e.Seen).TotalSeconds > 12).ToList())
        {
            e.State = "failed"; e.Error = "Interrompu : le programme qui le faisait ne répond plus."; e.CanPause = false; e.CanRetry = false; e.Speed = 0;
            dirty.Add(e.Key); Finished(e);
        }
        SaveHistory();
    }

    // ---------- vers la page ----------
    object Dto(Entry e) => new
    {
        key = e.Key, app = e.App, kind = e.Kind, title = e.Title, state = e.State, from = e.From, to = e.To, current = e.Current, error = e.Error, done = e.Done, total = e.Total, speed = e.Speed,
        items = e.Items, canPause = e.CanPause && e.Live, canRetry = e.CanRetry && e.State is "failed" or "canceled", hasPath = e.Path.Length > 0, hist = e.Hist, extra = e.Extra,
        started = new DateTimeOffset(e.Started, TimeSpan.Zero).ToUnixTimeMilliseconds(), ended = e.Ended == default ? 0 : new DateTimeOffset(e.Ended, TimeSpan.Zero).ToUnixTimeMilliseconds(),
    };

    void Flush()
    {
        if (form == null || form.IsDisposed || (dirty.Count == 0 && removed.Count == 0)) { if (form == null) { dirty.Clear(); removed.Clear(); } return; }
        if (removed.Count > 0) { form.Send(new { type = "xf.removed", keys = removed.ToArray() }); removed.Clear(); }
        if (dirty.Count > 0) { var items = dirty.Where(entries.ContainsKey).Select(k => Dto(entries[k])).ToList(); dirty.Clear(); if (items.Count > 0) form.Send(new { type = "xf.items", items }); }
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
            string Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            Entry E() => Str("key") is { } k && entries.TryGetValue(k, out var e) ? e : null;
            switch (cmd)
            {
                case "ready":
                    dirty.Clear(); removed.Clear();
                    from.Send(new { type = "xf.hello", items = entries.Values.Select(Dto).ToList() });
                    AskSync(); break;
                case "xf.ctl":
                    {
                        var e = E(); string action = Str("action");
                        if (e == null || !e.Live && action != "retry" || action is not ("pause" or "resume" or "cancel" or "retry")) break;
                        if (e.Hist) break;
                        string app = e.App; int id = e.Id;
                        _ = Task.Run(() => Bus.Send(app, "xfer-ctl", new { id, action }, "transfers"));
                        if (action == "retry") { entries.Remove(e.Key); removed.Add(e.Key); dirty.Remove(e.Key); historyDirty = true; }   // le nouvel essai revient sous un autre numéro
                        break;
                    }
                case "xf.open": Open(E()); break;
                case "xf.reveal": Reveal(E()); break;
                case "xf.remove": { var e = E(); if (e != null && !e.Live) { entries.Remove(e.Key); removed.Add(e.Key); dirty.Remove(e.Key); historyDirty = true; SaveHistory(); } break; }
                case "xf.clear":
                    foreach (var e in entries.Values.Where(e => !e.Live).ToList()) { entries.Remove(e.Key); removed.Add(e.Key); dirty.Remove(e.Key); }
                    historyDirty = true; SaveHistory(); break;
            }
        }
        catch (Exception ex) { Host.Log("message mal formé ou échec : " + ex.Message + " :: " + json); }
    }

    // « Ouvrir » : le fichier ou le dossier du transfert, sauf s'il peut s'exécuter (un programme téléchargé ne se lance pas d'un clic ici : on l'affiche dans son dossier).
    void Open(Entry e)
    {
        if (e == null || e.State != "done" || e.Path.Length == 0 || !Path.IsPathRooted(e.Path) || !Path.Exists(e.Path)) return;
        if (File.Exists(e.Path) && Risky.Contains(Path.GetExtension(e.Path).ToLowerInvariant())) { Reveal(e); return; }
        try { Process.Start(new ProcessStartInfo(e.Path) { UseShellExecute = true }); } catch (Exception ex) { Host.Log("ouvrir " + e.Path + " : " + ex.Message); }
    }

    void Reveal(Entry e)
    {
        if (e == null || e.Path.Length == 0 || !Path.IsPathRooted(e.Path)) return;
        string p = e.Path;
        if (!Path.Exists(p)) p = Path.GetDirectoryName(p.TrimEnd('\\')) ?? p;
        if (!Path.Exists(p)) return;
        string target = p;
        _ = Task.Run(() => Bus.Send("explorer", "select", new { path = target }, "transfers", launch: true));
    }

    // Canal de test (debug.txt) : state · eval <js>.
    public async Task<string> HandleControl(string line)
    {
        int sp = line.IndexOf(' '); string c = sp < 0 ? line : line[..sp], a = sp < 0 ? "" : line[(sp + 1)..];
        switch (c)
        {
            case "state": return (form == null ? "pas de fenêtre" : "fenêtre ouverte") + ", " + entries.Count + " transfert(s), " + entries.Values.Count(e => e.Live) + " en cours";
            case "entries": return JsonSerializer.Serialize(entries.Values.Select(e => new { e.Key, e.Kind, e.Title, e.State, e.Done, e.Total, e.Hist }));
            case "eval": return form?.Web.CoreWebView2 == null ? "pas de fenêtre" : await form.Web.CoreWebView2.ExecuteScriptAsync(a);
            case "cmd": if (form != null) OnMessage(form, a); return "ok";
            default: return "commande inconnue";
        }
    }
}
