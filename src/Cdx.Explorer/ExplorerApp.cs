using System.Drawing;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Cdx;

// Le programme : une ou plusieurs fenêtres de l'Explorateur, qui s'arrête avec la dernière.
sealed class ExplorerApp : ApplicationContext, ICdxApp, IPageApp
{
    public HostForm ControlTarget => windows.Count == 0 ? null : windows[0];   // la page visée par eval et cmd du canal de test (CdxApp.Control)
    string theme; readonly string root;
    readonly IDisposable watch;
    readonly List<HostForm> windows = new();
    readonly SideButtons side;
    CoreWebView2Environment env;
    int count;
    public readonly JobQueue Jobs = new();   // copier, déplacer, supprimer, chercher : en arrière-plan, l'un après l'autre (lot 4)
    readonly Dictionary<(HostForm, string), CancellationTokenSource> lists = new();   // la lecture de dossier en cours de chaque onglet
    readonly Dictionary<(HostForm, string), CancellationTokenSource> searches = new();    // la recherche dans les sous-dossiers en cours de chaque onglet
    readonly Dictionary<(HostForm, string), DirWatcher> watchers = new();             // la surveillance du dossier de chaque onglet
    public Control Anchor { get; } = new Control();   // installe le contexte de synchronisation de l'interface et sert à y poser un travail

    public ExplorerApp(string theme)
    {
        this.theme = theme ?? Suite.ReadAccent(); root = Host.FindWebRoot();
        _ = Anchor.Handle;
        if (theme == null) watch = Suite.Watch(this.theme, a => { this.theme = a; foreach (var w in windows.ToArray()) w.Send(new { type = "theme", value = a }); });
        side = new SideButtons(() => windows.ToArray());
        Jobs.Changed += j => { Xfer.Publish(XferOf(j)); Anchor.BeginInvoke(new Action(() => { Broadcast(new { type = "fs.job", job = JobInfo(j) }); ExitWhenIdle(); })); };
        Fs.Queue = Jobs; Fs.WindowAlive = h => windows.Any(w => !w.IsDisposed && w.Handle == h);
        Xfer.Active = () => Jobs.Snapshot().Where(j => j.State is JobState.Queued or JobState.Running or JobState.Paused).Select(XferOf).ToList();
        Xfer.Control = (id, action) =>
        {
            var j = Jobs.Get(id); if (j == null) return false;
            switch (action)
            {
                case "pause": return Jobs.Pause(id) || j.State == JobState.Paused;
                case "resume": return Jobs.Resume(id) || j.State == JobState.Running;
                case "cancel": return Jobs.Cancel(id) || j.State == JobState.Canceled;
                case "retry": { var again = j.State is JobState.Failed or JobState.Canceled ? j.Retry?.Invoke() : null; return again != null; }
            }
            return false;
        };
    }

    // Ce que la fenêtre des transferts voit d'une tâche (copie, déplacement, suppression).
    static XferInfo XferOf(Job j) => new()
    {
        Id = j.Id, Kind = j.Kind, Title = j.Title, State = j.State.ToString().ToLowerInvariant(), Done = j.Done, Total = j.Total, Speed = j.Speed, Current = j.Current, Error = j.Error,
        From = j.From, To = j.To, Path = j.Path, Items = j.Items, CanPause = j.Kind is "copy" or "move" or "delete", CanRetry = j.Retry != null && j.Failed.Count > 0,
    };

    // Sans fenêtre, le programme reste tant qu'un transfert est en cours (fermer la dernière fenêtre n'interrompt pas une copie), puis s'arrête un instant après la dernière annonce.
    System.Windows.Forms.Timer exitTimer;
    void ExitWhenIdle()
    {
        if (windows.Count > 0 || Jobs.Busy || exitTimer != null || Closing) return;
        exitTimer = new System.Windows.Forms.Timer { Interval = 2500 };
        exitTimer.Tick += (s, e) => { exitTimer.Dispose(); exitTimer = null; if (windows.Count == 0 && !Jobs.Busy) ExitThread(); };
        exitTimer.Start();
    }

    static object JobInfo(Job j) => new { id = j.Id, kind = j.Kind, title = j.Title, state = j.State.ToString().ToLowerInvariant(), done = j.Done, total = j.Total, speed = j.Speed, current = j.Current, error = j.Error };

    void Broadcast(object msg) { foreach (var w in windows.ToArray()) if (!w.IsDisposed) w.Send(msg); }

    // Lit un dossier hors du fil d'interface : une nouvelle demande pour le même onglet annule la précédente ; une réponse devenue inutile n'est jamais envoyée.
    void ListAsync(HostForm from, string path, string tab)
    {
        var key = (from, tab ?? "");
        if (lists.TryGetValue(key, out var old)) old.Cancel();
        var cts = lists[key] = new CancellationTokenSource(); var ct = cts.Token;
        // chaque paquet repart vers la page dans l'ordre, sur le fil d'interface ; un paquet devenu inutile (autre dossier demandé, fenêtre fermée) n'est jamais envoyé
        void Emit(object msg) => Anchor.BeginInvoke(new Action(() => { if (!ct.IsCancellationRequested && !from.IsDisposed) from.Send(msg); }));
        _ = Task.Run(() => { try { Fs.ListStream(path, tab, ct, Emit); } catch (OperationCanceledException) { } catch (Exception ex) { Emit(new { type = "fs.list", tab, error = ex.Message }); } })
            .ContinueWith(t => Anchor.BeginInvoke(new Action(() => { if (lists.TryGetValue(key, out var cur) && cur == cts) lists.Remove(key); })));
    }

    // Surveille le dossier d'un onglet (remplace la surveillance précédente de cet onglet ; un chemin vide l'arrête). Un dossier qu'on ne peut pas surveiller (réseau coupé...) est ignoré.
    void Watch(HostForm from, string tab, string path)
    {
        var key = (from, tab ?? "");
        if (watchers.Remove(key, out var old)) old.Dispose();
        if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path) || !Directory.Exists(path)) return;
        try { watchers[key] = new DirWatcher(path, () => Anchor.BeginInvoke(new Action(() => { if (!from.IsDisposed && watchers.ContainsKey(key)) from.Send(new { type = "fs.changed", tab, path }); }))); }
        catch (Exception ex) { Host.Log("surveillance de " + path + " impossible : " + ex.Message); }
    }

    // Lance une recherche dans les sous-dossiers pour un onglet (remplace la précédente) ; les résultats reviennent par paquets (fs.found), le dernier avec done = true.
    void SearchStart(HostForm from, JsonElement r)
    {
        string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string tab = S("tab"); int sid = r.TryGetProperty("sid", out var sv) && sv.TryGetInt32(out var si) ? si : 0; string root = S("root") ?? "";
        var key = (from, tab ?? "");
        if (searches.TryGetValue(key, out var old)) old.Cancel();
        if (root.Length > 0 && (!Path.IsPathRooted(root) || root.Length > 520 || !Directory.Exists(root))) { from.Send(new { type = "fs.found", tab, sid, items = new object[0], scanned = 0, done = true, truncated = false, error = "Dossier introuvable : " + root }); return; }
        var spec = new SearchSpec(root, (S("q") ?? "").Length > 200 ? S("q")[..200] : S("q"), S("kind"), S("date"), S("size"));
        var cts = searches[key] = new CancellationTokenSource(); var ct = cts.Token;
        void Emit(object msg) => Anchor.BeginInvoke(new Action(() => { if (!ct.IsCancellationRequested && !from.IsDisposed) from.Send(msg); }));
        _ = Task.Run(() => { try { Search.Run(spec, tab, sid, ct, Emit); } catch (OperationCanceledException) { } catch (Exception ex) { Emit(new { type = "fs.found", tab, sid, items = new object[0], scanned = 0, done = true, truncated = false, error = ex.Message }); } })
            .ContinueWith(t => Anchor.BeginInvoke(new Action(() => { if (searches.TryGetValue(key, out var cur) && cur == cts) searches.Remove(key); })));
    }

    void SearchStop(HostForm from, string tab) { if (searches.Remove((from, tab ?? ""), out var c)) c.Cancel(); }

    void CancelLists(HostForm f) { foreach (var k in searches.Keys.Where(k => k.Item1 == f).ToList()) { searches[k].Cancel(); searches.Remove(k); }  foreach (var k in watchers.Keys.Where(k => k.Item1 == f).ToList()) { watchers[k].Dispose(); watchers.Remove(k); }  foreach (var k in lists.Keys.Where(k => k.Item1 == f).ToList()) { lists[k].Cancel(); lists.Remove(k); } }

    public bool Closing { get; private set; }

    protected override void ExitThreadCore() { Closing = true; watch?.Dispose(); side.Dispose(); base.ExitThreadCore(); }

    public async void Open(string path, string select = null)
    {
        try
        {
            bool restore = path == null && windows.Count == 0;   // ouverte sans dossier et seule : la page retrouve les onglets de la dernière session
            path ??= Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            env ??= await WebEnv.Ui();   // l'environnement partagé des programmes « outils » (WebEnv.cs)
            var f = new ExplorerForm(this);
            windows.Add(f);
            var hnd = f.Handle; f.FormClosed += (s, e) => { Undo.Forget(hnd); CancelLists(f); windows.Remove(f); if (windows.Count == 0) { if (Jobs.Busy) Host.Log("explorateur : dernière fenêtre fermée, un transfert continue"); else ExitThread(); } };
            double scale = f.DeviceDpi / 96.0;
            int off = (count++ % 6) * 28;
            f.Bounds = new Rectangle(160 + off, 70 + off, (int)(1180 * scale), (int)(720 * scale));
            f.Show(); Host.ForceForeground(f.Handle);
            await f.InitWebAsync(env, root, "explorer/explorer.html?theme=" + theme + "&path=" + Uri.EscapeDataString(path) + (select != null ? "&select=" + Uri.EscapeDataString(select) : "") + (restore ? "&restore=1" : ""), scale);
            Host.Log("explorateur ouvert : " + path);
        }
        catch (Exception ex) { Host.Log("explorateur : " + ex); }
    }

    // Les messages du bus (SUITE.md §3.2) : open-path { path? } et select { path }. Chaque champ est validé ; un chemin qui n'existe pas est refusé.
    public bool OnBus(string type, string from, JsonElement data)
    {
        if (Xfer.OnBus(type, data)) return true;
        static bool Rooted(JsonElement d, out string p)
        {
            p = d.TryGetProperty("path", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return p != null && p.Length > 0 && p.Length <= 520 && Path.IsPathRooted(p) && p.IndexOf((char)0) < 0;
        }
        switch (type)
        {
            case "open-path":
                if (!data.TryGetProperty("path", out _)) { Open(null); return true; }
                if (!Rooted(data, out var dir)) return false;
                if (Directory.Exists(dir)) { Open(dir); return true; }
                if (File.Exists(dir)) { Open(Path.GetDirectoryName(dir), Path.GetFileName(dir)); return true; }
                return false;
            case "select":
                if (!Rooted(data, out var it)) return false;
                if (!File.Exists(it) && !Directory.Exists(it)) return false;
                Open(Path.GetDirectoryName(it) ?? it, Path.GetFileName(it)); return true;
        }
        return false;
    }

    // Canal de test (debug.txt) : state (nombre de fenêtres) · open <dossier> · eval <js> (dans la première fenêtre).
    public Task<string> HandleControl(string line) => Task.FromResult(ControlLine(line));

    string ControlLine(string line)
    {
        int sp = line.IndexOf(' '); string c = sp < 0 ? line : line[..sp], a = sp < 0 ? "" : line[(sp + 1)..];
        switch (c)
        {
            case "state": return windows.Count + " fenêtre(s)";
            case "open": Open(a); return "ok";
            default: return "commande inconnue";
        }
    }

    // Le glisser vers l'extérieur : la page détecte que le pointeur quitte la fenêtre avec des éléments saisis et nous les confie ; Windows prend le relais (boucle de glisser-déposer
    // native, annulable par Échap) et le résultat revient à la page (fs.dragend) pour qu'elle relise le dossier si les éléments ont été déplacés.
    void DragOut(HostForm from, JsonElement r)
    {
        var paths = r.TryGetProperty("paths", out var pv) && pv.ValueKind == JsonValueKind.Array
            ? pv.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).Where(p => p.Length <= 520 && Path.IsPathRooted(p) && (File.Exists(p) || Directory.Exists(p))).Take(5000).ToArray()
            : Array.Empty<string>();
        if (paths.Length == 0) return;
        from.BeginInvoke(new Action(() =>
        {
            try
            {
                var data = new DataObject(DataFormats.FileDrop, paths);
                var effect = from.DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
                Host.Log("glisser vers l'extérieur : " + paths.Length + " élément(s), effet " + effect);
                from.Send(new { type = "fs.dragend", effect = (int)effect });
            }
            catch (Exception ex) { Host.Log("glisser vers l'extérieur : " + ex.Message); from.Send(new { type = "fs.dragend", effect = 0 }); }
        }));
    }

    public void OnMessage(HostForm from, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string cmd = r.GetProperty("cmd").GetString();
            if (cmd == "fs.open" && r.TryGetProperty("path", out var pp) && pp.ValueKind == JsonValueKind.String && Bus.IsBrowsableFile(pp.GetString()))
            {
                // une page web, un PDF, une image... s'ouvre dans le navigateur CDX (lancé au besoin) ; s'il est introuvable, l'ouvreur de Windows reprend la main
                string file = pp.GetString(); var owner = from.Handle; var send = (Action<object>)from.Send; var rr = r.Clone();
                _ = Task.Run(() => { if (!Bus.Send("browser", "open-file", new { path = file }, "explorer", launch: true)) Anchor.BeginInvoke(new Action(() => Fs.Handle(owner, send, cmd, rr))); });
                return;
            }
            if (cmd == "fs.dropfiles")
            {
                string dir = r.TryGetProperty("dir", out var dv) && dv.ValueKind == JsonValueKind.String ? dv.GetString() : null, tab = r.TryGetProperty("tab", out var tv) && tv.ValueKind == JsonValueKind.String ? tv.GetString() : null;
                bool Flag(string k) => r.TryGetProperty(k, out var fv) && fv.ValueKind == JsonValueKind.True;
                Fs.DropFiles(from.Handle, from.Send, from.DroppedPaths, dir, Flag("shift"), Flag("ctrl"), tab);
                return;
            }
            if (cmd == "fs.dragout") { DragOut(from, r); return; }
            if (cmd == "fs.list") { ListAsync(from, r.TryGetProperty("path", out var lp) && lp.ValueKind == JsonValueKind.String ? lp.GetString() : null, r.TryGetProperty("tab", out var lt) && lt.ValueKind == JsonValueKind.String ? lt.GetString() : null); return; }
            if (cmd == "fs.newwindow") { Open(r.TryGetProperty("path", out var np) && np.ValueKind == JsonValueKind.String && np.GetString().Length > 0 && Directory.Exists(np.GetString()) ? np.GetString() : null); return; }
            if (cmd == "fs.menudata")
            {
                _ = Task.Run(() => (sendto: ShellData.SendTo().Select(s => new { name = s.Name, path = s.Path, kind = s.Kind }).ToList(), news: ShellData.NewKinds().Select(k => new { name = k.Name, ext = k.Ext }).ToList()))
                    .ContinueWith(t => Anchor.BeginInvoke(new Action(() => { if (!from.IsDisposed && !t.IsFaulted) from.Send(new { type = "menudata", sendto = t.Result.sendto, news = t.Result.news }); })));
                return;
            }
            if (cmd == "fs.openwith.list")
            {
                string ext = r.TryGetProperty("ext", out var ev) && ev.ValueKind == JsonValueKind.String ? ev.GetString() : null;
                _ = Task.Run(() => ShellData.OpenWith(ext).Select(p => new { name = p.name, exe = p.exe }).ToList())
                    .ContinueWith(t => Anchor.BeginInvoke(new Action(() => { if (!from.IsDisposed && !t.IsFaulted) from.Send(new { type = "openwith", ext, items = t.Result }); })));
                return;
            }
            if (cmd == "fs.search") { SearchStart(from, r); return; }
            if (cmd == "fs.search.stop") { SearchStop(from, r.TryGetProperty("tab", out var sst) && sst.ValueKind == JsonValueKind.String ? sst.GetString() : null); return; }
            if (cmd == "fs.shellmenu")
            {
                var sp = r.TryGetProperty("paths", out var spv) && spv.ValueKind == JsonValueKind.Array ? spv.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToArray() : Array.Empty<string>();
                bool shift = r.TryGetProperty("shift", out var shv) && shv.ValueKind == JsonValueKind.True;
                var pt = Cursor.Position;   // le menu s'ouvre sous le pointeur (le clic qui l'a demandé)
                try { if (ShellMenu.Show(from.Handle, sp, pt.X, pt.Y, shift) == 0) from.Send(new { type = "fs.done", tab = (string)null, error = "Windows n'a pas de menu pour cet élément." }); }
                catch (Exception ex) { Host.Log("menu natif : " + ex.Message); from.Send(new { type = "fs.done", tab = (string)null, error = "Le menu de Windows n'a pas pu s'ouvrir : " + ex.Message }); }
                return;
            }
            if (cmd == "fs.watch") { Watch(from, r.TryGetProperty("tab", out var wt) && wt.ValueKind == JsonValueKind.String ? wt.GetString() : null, r.TryGetProperty("path", out var wp) && wp.ValueKind == JsonValueKind.String ? wp.GetString() : null); return; }
            if (cmd == "fs.settings.get") { from.Send(new { type = "settings", values = Settings.All() }); return; }
            if (cmd == "fs.settings.set")
            {
                if (r.TryGetProperty("key", out var sk) && sk.ValueKind == JsonValueKind.String && Settings.Set(sk.GetString(), r.TryGetProperty("value", out var sv) ? sv : default)) Broadcast(new { type = "settings", values = Settings.All() });
                return;
            }
            if (cmd is "fs.job.pause" or "fs.job.resume" or "fs.job.cancel")
            {
                if (r.TryGetProperty("id", out var jid) && jid.TryGetInt32(out int id)) { if (cmd == "fs.job.pause") Jobs.Pause(id); else if (cmd == "fs.job.resume") Jobs.Resume(id); else Jobs.Cancel(id); }
                return;
            }
            if (cmd == "fs.showxfers") { Xfer.Show(); return; }
            if (cmd.StartsWith("fs.")) { Fs.Handle(from.Handle, from.Send, cmd, r); return; }
            if (cmd.StartsWith("win.")) { Host.WindowCommand(from, cmd, r); return; }
            if (Host.CommonMessage(from, cmd, r)) return;
            if (cmd == "ready") { from.Send(new { type = "user", name = Environment.UserName }); return; }
        }
        catch (Exception ex) { Host.Log("message mal formé ou échec : " + ex.Message + " :: " + json); }
    }
}
