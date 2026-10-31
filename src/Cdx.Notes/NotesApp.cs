using System.Drawing;
using System.Text.Json;
using Cdx.Coffre;
using Microsoft.Web.WebView2.Core;

namespace Cdx;

// Le programme : une fenêtre sur le coffre de notes. La page demande, le programme lit et écrit les fichiers (Cdx.Vault, les mêmes règles que le serveur MCP de Claude)
// et prévient la page de tout changement du dossier (ce que fait Claude, ou un autre éditeur, se voit aussitôt).
sealed class NotesApp : ApplicationContext, IHostApp
{
    string theme; readonly string root; readonly string openAtStart;
    readonly IDisposable watch;
    NotesForm form;
    CoreWebView2Environment env;
    Vault vault;
    FileSystemWatcher fsw; System.Threading.Timer debounce;
    readonly Dictionary<string, string> known = new(StringComparer.OrdinalIgnoreCase);   // notes ouvertes dans la page -> empreinte que la page connaît
    public readonly Control Anchor = new Control();   // installe le contexte de synchronisation de l'interface et sert à y poser un travail

    public NotesApp(string theme, string openAtStart)
    {
        this.theme = theme ?? Suite.ReadAccent(); root = Host.FindWebRoot(); this.openAtStart = openAtStart;
        _ = Anchor.Handle;
        if (theme == null) watch = Suite.Watch(this.theme, a => { this.theme = a; form?.Send(new { type = "theme", value = a }); });
    }

    public bool Closing { get; private set; }

    protected override void ExitThreadCore() { Closing = true; watch?.Dispose(); StopWatching(); base.ExitThreadCore(); }

    // ---------- le coffre ----------
    void OpenVault()
    {
        StopWatching(); known.Clear();
        vault = VaultConfig.Open(true);
        try
        {
            fsw = new FileSystemWatcher(vault.Root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size, InternalBufferSize = 64 * 1024 };
            FileSystemEventHandler h = (s, e) => Changed();
            fsw.Changed += h; fsw.Created += h; fsw.Deleted += h; fsw.Renamed += (s, e) => Changed();
            fsw.Error += (s, e) => Changed();   // tampon débordé : on relit tout
            fsw.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Host.Log("surveillance du coffre : " + ex.Message); }
        Host.Log($"coffre ouvert : {vault.Count} notes");
    }

    void StopWatching() { try { fsw?.Dispose(); } catch { } fsw = null; debounce?.Dispose(); debounce = null; }

    // Un changement du dossier : regroupé (300 ms) puis relu sur le fil de l'interface.
    void Changed()
    {
        if (Closing) return;
        debounce?.Dispose();
        debounce = new System.Threading.Timer(_ => { try { Anchor.BeginInvoke(new Action(OnVaultChanged)); } catch { } }, null, 300, Timeout.Infinite);
    }

    void OnVaultChanged()
    {
        if (vault == null || Closing) return;
        try
        {
            vault.Refresh(); SendIndex();
            foreach (var path in known.Keys.ToList())
            {
                NoteInfo n = vault.Get(path);
                if (n == null) { known.Remove(path); form?.Send(new { type = "n.gone", path }); continue; }
                string text = vault.Read(path, out string h);
                if (!string.Equals(h, known[path], StringComparison.OrdinalIgnoreCase)) { known[path] = h; form?.Send(new { type = "n.changed", path, text, hash = h, modified = Ms(n.Modified) }); }
            }
        }
        catch (Exception ex) { Host.Log("relecture du coffre : " + ex.Message); }
    }

    static long Ms(DateTime d) => new DateTimeOffset(d).ToUnixTimeMilliseconds();

    object IndexMessage() => new
    {
        type = "index",
        notes = vault.Notes.Select(n => new { p = n.Path, t = n.Title, m = Ms(n.Modified), g = n.Tags, y = n.Prop("type"), s = n.Prop("statut") }).ToList(),
        folders = vault.Folders(), images = vault.Attachments()
    };

    void SendIndex() => form?.Send(IndexMessage());

    public string ImageFile(string rel) => vault?.ImagePath(rel);

    // ---------- fenêtre ----------
    public async void Open()
    {
        try
        {
            if (form != null && !form.IsDisposed) { if (!NotesForm.NoFocus) Host.ForceForeground(form.Handle); return; }
            if (vault == null) OpenVault();
            env ??= await CoreWebView2Environment.CreateAsync(null, Path.Combine(Host.DataDir, "webview-notes"), new CoreWebView2EnvironmentOptions("--disable-lcd-text"));
            var f = new NotesForm(this); form = f;
            f.FormClosed += (s, e) => ExitThread();
            double scale = f.DeviceDpi / 96.0;
            f.Bounds = new Rectangle(160, 70, (int)(1240 * scale), (int)(780 * scale));
            f.Show(); if (!NotesForm.NoFocus) Host.ForceForeground(f.Handle);
            await f.InitWebAsync(env, root, "notes/notes.html?theme=" + theme, scale);
            Host.Log("Notes ouvert");
        }
        catch (Exception ex) { Host.Log("Notes : " + ex); }
    }

    // Les messages du bus : show (passer devant) et open-note { path } (ouvrir une note du coffre).
    public bool OnBus(string type, string from, JsonElement data)
    {
        if (type == "show") { Open(); return true; }
        if (type == "open-note")
        {
            string p = data.TryGetProperty("path", out var pv) && pv.ValueKind == JsonValueKind.String ? pv.GetString() : null;
            try { p = Vault.NormalizePath(p); } catch (VaultException) { return false; }
            Open(); form?.Send(new { type = "n.open", path = p }); return true;
        }
        return false;
    }

    // Canal de test (debug.txt) : state · eval <js>.
    public async Task<string> HandleControl(string line)
    {
        int sp = line.IndexOf(' '); string c = sp < 0 ? line : line[..sp], a = sp < 0 ? "" : line[(sp + 1)..];
        switch (c)
        {
            case "state": return (form == null ? "pas de fenêtre" : "fenêtre ouverte") + ", " + (vault?.Count ?? 0) + " note(s), " + known.Count + " ouverte(s)";
            case "eval": return form?.Web.CoreWebView2 == null ? "pas de fenêtre" : await form.Web.CoreWebView2.ExecuteScriptAsync(a);
            default: return "commande inconnue";
        }
    }

    // ---------- messages de la page ----------
    public void OnMessage(HostForm from, string json)
    {
        string cmd = null, path = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            cmd = r.GetProperty("cmd").GetString();
            if (cmd.StartsWith("win.")) { Host.WindowCommand(from, cmd, r); return; }
            if (Host.CommonMessage(from, cmd, r)) return;
            string Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            path = Str("path");
            switch (cmd)
            {
                case "ready":
                    from.Send(new { type = "hello", name = Path.GetFileName(vault.Root), root = vault.Root, pinnedRoot = Environment.GetEnvironmentVariable("CDX_NOTES_DIR") is { Length: > 0 }, open = openAtStart });
                    from.Send(IndexMessage());
                    break;
                case "n.refresh": vault.Refresh(); SendIndex(); break;
                case "n.read": Read(from, path); break;
                case "n.close": if (path != null) known.Remove(SafePath(path)); break;
                case "n.save": Save(from, path, Str("text"), Str("hash"), force: false); break;
                case "n.force": Save(from, path, Str("text"), null, force: true); break;
                case "n.create":
                    {
                        var n = vault.Create(path, Str("text") ?? ""); vault.Read(n.Path, out string h); known[n.Path] = h;
                        from.Send(new { type = "n.created", path = n.Path }); SendIndex(); break;
                    }
                case "n.folder": vault.CreateFolder(path); from.Send(new { type = "n.folder", path = Vault.NormalizePath(path, false) }); SendIndex(); break;
                case "n.move":
                    {
                        string to = Str("to"); int updated = vault.Move(path, to);
                        string a = Vault.NormalizePath(path), b = Vault.NormalizePath(to);
                        if (known.Remove(a)) { vault.Read(b, out string h); known[b] = h; }
                        from.Send(new { type = "n.moved", from = a, to = b, updated }); SendIndex(); break;
                    }
                case "n.movefolder":
                    {
                        string to = Str("to"); var moved = vault.MoveFolder(path, to);
                        foreach (var kv in moved.Moves) if (known.Remove(kv.Key)) { vault.Read(kv.Value, out string h); known[kv.Value] = h; }
                        from.Send(new { type = "n.foldermoved", from = Vault.NormalizePath(path, false), to = Vault.NormalizePath(to, false), moves = moved.Moves.Select(kv => new { from = kv.Key, to = kv.Value }).ToList(), updated = moved.Updated });
                        SendIndex(); break;
                    }
                case "n.trash": { string t = vault.Trash(path); known.Remove(Vault.NormalizePath(path)); from.Send(new { type = "n.trashed", path = Vault.NormalizePath(path), trash = t }); SendIndex(); break; }
                case "n.trashfolder":
                    {
                        var gone = vault.TrashFolder(path);
                        foreach (var g in gone) known.Remove(g);
                        from.Send(new { type = "n.foldertrashed", path = Vault.NormalizePath(path, false), notes = gone }); SendIndex(); break;
                    }
                case "n.reveal": Reveal(); break;
                case "n.openext": OpenExternal(Str("url")); break;
                case "n.choosevault": ChooseVault(from); break;
                case "n.mcpinfo": from.Send(new { type = "n.mcpinfo", exe = Path.Combine(AppContext.BaseDirectory, "cdx-notes-mcp.exe"), root = vault.Root }); break;
                // ---- navigation (lot 4), projets (lot 5), graphe (lot 6)
                case "n.backlinks": from.Send(new { type = "n.backlinks", path = Vault.NormalizePath(path), backlinks = vault.Backlinks(path).Select(b => new { p = b.Path, t = b.Title, line = b.Line, ctx = b.Context }).ToList() }); break;
                case "n.search":
                    {
                        string q = Str("q") ?? "";
                        from.Send(new { type = "n.results", q, hits = vault.Search(q, 50).Select(h => new { p = h.Path, t = h.Title, s = h.Snippet, line = h.Line, g = h.Tags }).ToList() });
                        break;
                    }
                case "n.projects": SendProjects(from); break;
                case "n.newproject": { var n = vault.CreateProject(Str("name"), Str("repo"), Str("summary")); from.Send(new { type = "n.created", path = n.Path }); SendIndex(); break; }
                case "n.toggletask": ToggleTask(from, path, r.TryGetProperty("line", out var lv) && lv.TryGetInt32(out var ln) ? ln : -1); break;
                case "n.setprop": { var n = vault.SetProperty(path, Str("key"), Str("value")); SendIndex(); if (known.ContainsKey(n.Path)) Read(from, n.Path); SendProjects(from); break; }
                case "n.openfolder": OpenFolder(Str("path")); break;
                case "n.repos": _ = Task.Run(() => { if (!Bus.Send("repos", "show", new { }, "notes", launch: true)) Host.Log("Dépôts introuvable"); }); break;
                case "n.graph": { var (nodes, edges) = vault.Graph(); from.Send(new { type = "n.graph", nodes, edges }); break; }
            }
        }
        catch (VaultException ex) { from.Send(new { type = "n.error", op = cmd, path, code = ex.Code, message = ex.Message }); }
        catch (Exception ex) { Host.Log("message mal formé ou échec : " + ex.Message + " :: " + json); from.Send(new { type = "n.error", op = cmd, path, code = "interne", message = "L'opération n'a pas pu être faite." }); }
    }

    static string SafePath(string p) { try { return Vault.NormalizePath(p); } catch (VaultException) { return p; } }

    // Les fiches de projet et les tâches ouvertes de tout le coffre (vue « Projets »).
    void SendProjects(HostForm to)
    {
        to.Send(new
        {
            type = "n.projects",
            projects = vault.Projects().Select(p => new { p = p.Path, name = p.Name, status = p.Status, repo = p.Repo, priority = p.Priority, summary = p.Summary, open = p.OpenTasks, done = p.DoneTasks, m = Ms(p.Modified), due = p.NextDue }).ToList(),
            tasks = vault.Tasks().Select(t => new { p = t.Path, text = t.Text, due = t.Due, line = t.Line }).ToList(),
        });
    }

    // Coche ou décoche la tâche d'une ligne (1 = première) d'une note : la note est réécrite, la page la verra changer par la surveillance du dossier.
    void ToggleTask(HostForm to, string path, int line)
    {
        string text = vault.Read(path, out string hash); var lines = text.Split('\n');
        if (line < 1 || line > lines.Length) throw new VaultException("ligne", "Cette ligne n'existe pas dans la note.");
        var m = System.Text.RegularExpressions.Regex.Match(lines[line - 1], @"^(\s*[-*+]\s+\[)( |x|X)(\])");
        if (!m.Success) throw new VaultException("tâche", "Cette ligne n'est pas une tâche.");
        lines[line - 1] = m.Groups[1].Value + (m.Groups[2].Value == " " ? "x" : " ") + m.Groups[3].Value + lines[line - 1][m.Length..];
        var n = vault.Overwrite(path, string.Join('\n', lines), hash);
        vault.Read(n.Path, out string h); if (known.ContainsKey(n.Path)) known[n.Path] = h;
        SendIndex(); SendProjects(to);
        if (known.ContainsKey(n.Path)) to.Send(new { type = "n.changed", path = n.Path, text = vault.Read(n.Path, out _), hash = h, modified = Ms(n.Modified) });
    }

    // Le dossier du dépôt d'un projet : dans l'Explorateur CDX (lancé au besoin), sinon celui de Windows.
    void OpenFolder(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathFullyQualified(dir) || !Directory.Exists(dir)) { form?.Send(new { type = "n.error", op = "n.openfolder", code = "dossier", message = "Le dossier du dépôt n'existe pas : " + dir }); return; }
        _ = Task.Run(() =>
        {
            if (!Bus.Send("explorer", "open-path", new { path = dir }, "notes", launch: true))
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { dir }, UseShellExecute = false }); } catch (Exception ex) { Host.Log("dossier : " + ex.Message); }
        });
    }

    void Read(HostForm to, string path)
    {
        string text = vault.Read(path, out string hash); var n = vault.Get(path);
        known[n.Path] = hash;
        to.Send(new
        {
            type = "n.note", path = n.Path, text, hash, modified = Ms(n.Modified),
            backlinks = vault.Backlinks(n.Path).Select(b => new { p = b.Path, t = b.Title, line = b.Line, ctx = b.Context }).ToList()
        });
    }

    void Save(HostForm to, string path, string text, string hash, bool force)
    {
        if (text == null) throw new VaultException("texte", "Le texte de la note manque.");
        if (force) vault.Read(path, out hash);
        var n = vault.Overwrite(path, text, hash);
        vault.Read(n.Path, out string h); known[n.Path] = h;
        to.Send(new { type = "n.saved", path = n.Path, hash = h, modified = Ms(n.Modified) });
        SendIndex();
    }

    // Le dossier du coffre dans l'Explorateur de la suite.
    void Reveal()
    {
        string path = vault.Root;
        _ = Task.Run(() =>
        {
            if (!Bus.Send("explorer", "open-path", new { path }, "notes", launch: true))
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false }); } catch (Exception ex) { Host.Log("dossier : " + ex.Message); }
        });
    }

    // Une adresse web cliquée dans une note : http ou https seulement, dans le navigateur de la suite.
    void OpenExternal(string url)
    {
        if (url == null || url.Length > 2000 || !Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https" && u.Scheme != "mailto")) return;
        _ = Task.Run(() =>
        {
            if (u.Scheme == "mailto" || !Bus.Send("browser", "open-url", new { url }, "notes", launch: true))
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { Host.Log("adresse web : " + ex.Message); }
        });
    }

    void ChooseVault(HostForm owner)
    {
        if (Environment.GetEnvironmentVariable("CDX_NOTES_DIR") is { Length: > 0 }) { owner.Send(new { type = "n.error", op = "n.choosevault", code = "imposé", message = "Le dossier des notes est imposé par la variable CDX_NOTES_DIR." }); return; }
        using var dlg = new FolderBrowserDialog { Description = "Dossier des notes (un coffre de fichiers Markdown ; un dossier vide reçoit quelques notes d'exemple)", UseDescriptionForTitle = true, ShowNewFolderButton = true, SelectedPath = vault.Root };
        if (dlg.ShowDialog(owner) != DialogResult.OK || string.IsNullOrEmpty(dlg.SelectedPath)) return;
        VaultConfig.Save(dlg.SelectedPath); OpenVault();
        owner.Send(new { type = "hello", name = Path.GetFileName(vault.Root), root = vault.Root, pinnedRoot = false, open = (string)null, reset = true });
        owner.Send(IndexMessage());
    }
}
