using System.Drawing;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Cdx;

// Le programme : une fenêtre qui liste les dépôts git des dossiers choisis. La page demande, le programme lit git (Git.cs) et répond ; rien n'est écrit dans un dépôt.
sealed class ReposApp : ApplicationContext, IHostApp
{
    string theme; readonly string root;
    readonly IDisposable watch;
    ReposForm form;
    CoreWebView2Environment env;
    readonly List<string> roots = new();
    readonly List<string> hidden = new();   // projets que Valentin ne veut plus voir (copies de sauvegarde, essais) : un réglage de cette application, rien dans les dépôts
    readonly Dictionary<string, RepoInfo> infos = new(StringComparer.OrdinalIgnoreCase);
    CancellationTokenSource scanCts;
    int scanId;
    public readonly Control Anchor = new Control();   // installe le contexte de synchronisation de l'interface et sert à y poser un travail

    static string RootsFile => Path.Combine(Host.DataDir, "repos.json");

    public ReposApp(string theme)
    {
        this.theme = theme ?? Suite.ReadAccent(); root = Host.FindWebRoot();
        _ = Anchor.Handle;
        LoadRoots();
        if (theme == null) watch = Suite.Watch(this.theme, a => { this.theme = a; form?.Send(new { type = "theme", value = a }); });
    }

    public bool Closing { get; private set; }

    protected override void ExitThreadCore() { Closing = true; scanCts?.Cancel(); watch?.Dispose(); base.ExitThreadCore(); }

    // ---------- dossiers à explorer ----------
    // Par défaut : le dossier « Dev » du profil, s'il existe. Les autres s'ajoutent depuis la page et se retrouvent dans repos.json.
    void LoadRoots()
    {
        try
        {
            if (File.Exists(RootsFile))
            {
                using var d = JsonDocument.Parse(File.ReadAllText(RootsFile));
                foreach (var v in d.RootElement.GetProperty("roots").EnumerateArray()) if (v.ValueKind == JsonValueKind.String && Directory.Exists(v.GetString())) roots.Add(v.GetString());
                if (d.RootElement.TryGetProperty("hidden", out var h)) foreach (var v in h.EnumerateArray()) if (v.ValueKind == JsonValueKind.String) hidden.Add(v.GetString());
                return;
            }
        }
        catch (Exception ex) { Host.Log("repos.json illisible, dossiers par défaut : " + ex.Message); }
        string dev = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Dev");
        if (Directory.Exists(dev)) roots.Add(dev);
    }

    void SaveRoots()
    {
        try
        {
            string tmp = RootsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new { roots, hidden }));
            File.Move(tmp, RootsFile, true);
        }
        catch (Exception ex) { Host.Log("repos.json : " + ex.Message); }
    }

    // ---------- fenêtre ----------
    public async void Open()
    {
        try
        {
            if (form != null && !form.IsDisposed) { Host.ForceForeground(form.Handle); return; }
            env ??= await CoreWebView2Environment.CreateAsync(null, Path.Combine(Host.DataDir, "webview-repos"), new CoreWebView2EnvironmentOptions("--disable-lcd-text"));
            var f = new ReposForm(this); form = f;
            f.FormClosed += (s, e) => ExitThread();
            double scale = f.DeviceDpi / 96.0;
            f.Bounds = new Rectangle(180, 80, (int)(1100 * scale), (int)(700 * scale));
            f.Show(); Host.ForceForeground(f.Handle);
            await f.InitWebAsync(env, root, "repos/repos.html?theme=" + theme, scale);
            Host.Log("Dépôts ouvert");
        }
        catch (Exception ex) { Host.Log("Dépôts : " + ex); }
    }

    // Le message « show » du bus : un second lancement demande à la première instance de passer devant.
    public bool OnBus(string type, string from, JsonElement data)
    {
        if (type != "show") return false;
        Open(); return true;
    }

    // Canal de test (debug.txt) : state · eval <js>.
    public async Task<string> HandleControl(string line)
    {
        int sp = line.IndexOf(' '); string c = sp < 0 ? line : line[..sp], a = sp < 0 ? "" : line[(sp + 1)..];
        switch (c)
        {
            case "state": return (form == null ? "pas de fenêtre" : "fenêtre ouverte") + ", " + infos.Count + " dépôt(s), " + roots.Count + " dossier(s)";
            case "eval": return form?.Web.CoreWebView2 == null ? "pas de fenêtre" : await form.Web.CoreWebView2.ExecuteScriptAsync(a);
            case "cmd": if (form != null) OnMessage(form, a); return "ok";
            default: return "commande inconnue";
        }
    }

    // ---------- recherche ----------
    // Cherche les dépôts, puis lit chacun (quatre à la fois) et envoie chaque résultat à la page dès qu'il est prêt. Un nouveau lancement annule le précédent.
    async void StartScan()
    {
        scanCts?.Cancel();
        var cts = scanCts = new CancellationTokenSource(); var ct = cts.Token; int id = ++scanId;
        var rootsNow = roots.ToArray();
        form?.Send(new { type = "scan-start", id, roots = rootsNow });
        try
        {
            var paths = await Task.Run(() => Git.Scan(rootsNow, 4, ct));
            if (ct.IsCancellationRequested) return;
            infos.Clear();
            form?.Send(new { type = "scan-found", id, count = paths.Count });
            var sem = new SemaphoreSlim(4);
            await Task.WhenAll(paths.Select(async p =>
            {
                await sem.WaitAsync();
                try
                {
                    if (ct.IsCancellationRequested) return;
                    var r = await Task.Run(() => Git.Inspect(p));
                    if (ct.IsCancellationRequested) return;
                    infos[p] = r;
                    form?.Send(new { type = "repo", id, item = Dto(r) });
                }
                finally { sem.Release(); }
            }));
            if (!ct.IsCancellationRequested) form?.Send(new { type = "scan-end", id, count = paths.Count });
        }
        catch (Exception ex) { Host.Log("recherche : " + ex); }
    }

    // Ce que la page reçoit d'un dépôt : jamais l'adresse brute du remote (elle peut contenir un jeton), seulement l'adresse web des hébergeurs connus.
    static object Dto(RepoInfo r) => new
    {
        path = r.Path, name = r.Name, branch = r.Branch, detached = r.Detached, upstream = r.Upstream, hasRemote = r.Remote != null,
        web = r.WebUrl, webLabel = r.WebUrl?.Replace("https://", ""), changed = r.Changed, untracked = r.Untracked, ahead = r.Ahead, behind = r.Behind,
        noCommits = r.NoCommits, lastHash = r.LastHash, lastDate = r.LastDate, lastAuthor = r.LastAuthor, lastSubject = r.LastSubject, error = r.Error,
    };

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
            switch (cmd)
            {
                case "ready":
                    from.Send(new { type = "hello", git = Git.Version(), roots, hidden });
                    StartScan(); break;
                case "repos.scan": StartScan(); break;
                case "repos.detail": Detail(from, Str("path")); break;
                case "repos.folder": OpenFolder(Str("path")); break;
                case "repos.web": OpenWeb(Str("path")); break;
                case "repos.addroot": AddRoot(from); break;
                case "repos.hide":
                    {
                        string p = Str("path");
                        if (p == null || !infos.ContainsKey(p)) break;
                        bool on = !r.TryGetProperty("hide", out var hv) || hv.ValueKind != JsonValueKind.False;
                        hidden.RemoveAll(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase));
                        if (on) hidden.Add(p);
                        SaveRoots(); from.Send(new { type = "hidden", hidden });
                        break;
                    }
                case "repos.removeroot":
                    {
                        string p = Str("path");
                        if (p != null && roots.RemoveAll(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase)) > 0) { SaveRoots(); from.Send(new { type = "roots", roots }); StartScan(); }
                        break;
                    }
            }
        }
        catch (Exception ex) { Host.Log("message mal formé ou échec : " + ex.Message + " :: " + json); }
    }

    // Les derniers enregistrements et les fichiers modifiés d'un dépôt de la liste (un chemin qui n'y est pas est refusé).
    void Detail(HostForm to, string path)
    {
        if (path == null || !infos.ContainsKey(path)) return;
        _ = Task.Run(() =>
        {
            var commits = Git.Log(path).Select(c => new { hash = c.Hash, date = c.Date, author = c.Author, subject = c.Subject }).ToList();
            var files = Git.Changes(path).Select(f => new { code = f.Code, path = f.Path }).ToList();
            Anchor.BeginInvoke(new Action(() => to.Send(new { type = "detail", path, commits, files })));
        });
    }

    void OpenFolder(string path)
    {
        if (path == null || !infos.ContainsKey(path)) return;
        _ = Task.Run(() =>
        {
            if (!Bus.Send("explorer", "open-path", new { path }, "repos", launch: true))
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false }); } catch (Exception ex) { Host.Log("dossier : " + ex.Message); }
        });
    }

    void OpenWeb(string path)
    {
        if (path == null || !infos.TryGetValue(path, out var r) || r.WebUrl == null) return;
        string url = r.WebUrl;
        _ = Task.Run(() =>
        {
            if (!Bus.Send("browser", "open-url", new { url }, "repos", launch: true))
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { Host.Log("adresse web : " + ex.Message); }
        });
    }

    void AddRoot(HostForm owner)
    {
        using var dlg = new FolderBrowserDialog { Description = "Dossier à explorer à la recherche de projets git", UseDescriptionForTitle = true, ShowNewFolderButton = false };
        if (dlg.ShowDialog(owner) != DialogResult.OK || string.IsNullOrEmpty(dlg.SelectedPath)) return;
        if (!roots.Any(x => string.Equals(x, dlg.SelectedPath, StringComparison.OrdinalIgnoreCase))) roots.Add(dlg.SelectedPath);
        SaveRoots(); owner.Send(new { type = "roots", roots }); StartScan();
    }
}
