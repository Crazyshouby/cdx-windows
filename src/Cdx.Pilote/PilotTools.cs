using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cdx;

// Les outils que Pilote prête à Claude : la suite vue par Claude Code (serveur MCP « cdx-suite », exécutable cdx-pilote-mcp, un relais qui parle à ce tube).
// Tube \\.\pipe\<préfixe>pilote.outils (utilisateur courant), une connexion par requête : une ligne JSON { op: "list" } ou { op: "call", name, args } →
// une ligne JSON { tools: [...] } ou { content: [...], isError }. Le travail se fait ici, hors du fil de l'interface : bus de la suite, canaux de pilotage
// (.pilot) des programmes, lancement d'un programme absent. Les accords, eux, passent par le protocole de Claude Code (PiloteApp), pas par ici.
static class PilotTools
{
    public const string Pipe = "pilote.outils";
    static PiloteApp app;
    static readonly List<int> launched = new();   // les programmes que Pilote a lancés (pid) : un essai isolé les retrouve pour les fermer
    public static int[] Launched { get { lock (launched) return launched.ToArray(); } }

    public static void Serve(PiloteApp a)
    {
        app = a;
        new Thread(() =>
        {
            string name = Bus.Prefix + Pipe;
            while (!app.Closing)
            {
                NamedPipeServerStream srv = null;
                try
                {
                    srv = new NamedPipeServerStream(name, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    srv.WaitForConnection();
                    var conn = srv; srv = null;
                    new Thread(() => Handle(conn)) { IsBackground = true }.Start();
                }
                catch (Exception ex) { try { srv?.Dispose(); } catch (Exception) { } Host.Log("outils : " + ex.Message); Thread.Sleep(500); }
            }
        }) { IsBackground = true, Name = "pilote-outils" }.Start();
        Host.Log("tube des outils ouvert (" + Bus.Prefix + Pipe + ")");
    }

    static void Handle(NamedPipeServerStream conn)
    {
        using (conn)
        {
            try
            {
                using var rd = new StreamReader(conn, new UTF8Encoding(false)); using var wr = new StreamWriter(conn, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                string line = rd.ReadLine(); if (line == null) return;
                string res;
                try { res = Request(line); }
                catch (Exception ex) { Host.Log("outils : " + ex); res = Fail("Erreur interne : " + ex.Message); }
                wr.WriteLine(res);
                try { conn.WaitForPipeDrain(); } catch (Exception) { }   // la réponse est lue avant la fermeture du tube
            }
            catch (Exception ex) { Host.Log("outils (connexion) : " + ex.Message); }
        }
    }

    static string Request(string line)
    {
        var o = JsonNode.Parse(line) as JsonObject ?? throw new ArgumentException("requête illisible");
        string op = o["op"]?.GetValue<string>();
        if (op == "list") return new JsonObject { ["tools"] = ToolList() }.ToJsonString();
        if (op != "call") throw new ArgumentException("opération inconnue");
        string name = o["name"]?.GetValue<string>() ?? "";
        var args = o["args"] as JsonObject ?? new JsonObject();
        var tool = Tools.FirstOrDefault(t => t.Name == name);
        if (tool == null) return Fail("Outil inconnu : " + name);
        app.NoteToolCall(name, args.ToJsonString());
        try { return tool.Run(args); }
        catch (ArgException ex) { return Fail(ex.Message); }
    }

    // ---------- définitions ----------
    sealed record Tool(string Name, string Description, JsonObject Schema, bool ReadOnly, Func<JsonObject, string> Run);
    sealed class ArgException : Exception { public ArgException(string m) : base(m) { } }

    static JsonObject Obj(params (string name, string type, string desc)[] props)
    {
        var p = new JsonObject(); var r = new JsonArray();
        foreach (var (name, type, desc) in props) { p[name] = new JsonObject { ["type"] = type, ["description"] = desc }; if (!desc.StartsWith("(facultatif)")) r.Add(name); }
        return new JsonObject { ["type"] = "object", ["properties"] = p, ["required"] = r, ["additionalProperties"] = false };
    }
    static readonly string[] AppNames = { "explorer", "browser", "notes", "terminal", "repos", "agenda", "courrier", "transfers" };

    static List<Tool> tools;
    static List<Tool> Tools => tools ??= new List<Tool>
    {
        new("suite_state", "L'état de la suite CDX : les programmes qui tournent, leurs fenêtres (titre, position), les dossiers de travail (projets, coffre de notes). À lire en premier.", Obj(), true, a => Ok(SuiteState())),
        new("app_open", "Ouvre (et lance au besoin) un programme de la suite sur une cible : explorer (dossier ou fichier à montrer), browser (adresse http(s) ou fichier), terminal (dossier de départ), notes (chemin d'une note), repos, agenda, courrier, transfers (passer devant).",
            Obj(("app", "string", "explorer, browser, terminal, notes, repos, agenda, courrier ou transfers."), ("target", "string", "(facultatif) Chemin, adresse ou note selon le programme.")), false, a => Ok(AppOpen(S(a, "app"), S(a, "target", false)))),
        new("app_state", "L'état d'un programme de la suite qui tourne (ce que son canal de pilotage rend : fenêtres, onglets, compte relié…).", Obj(("app", "string", "explorer, browser, terminal, notes, repos, agenda, courrier ou transfers.")), true, a => Ok(Pilot(S(a, "app"), "state"))),
        new("app_command", "Envoie une commande de page à un programme de la suite, telle que sa page la lui enverrait (JSON avec « cmd »). Agenda : ag.event.save {title,cal,allDay,start,end,location,description}, ag.task.add {list,title,due}, ag.load {from,to}. Courrier : ml.list {label,q}, ml.open {id}, ml.send {to,subject,text}. Navigateur : br.newtab {url}, br.go {url}, br.back, br.reload, br.close {id}. Terminal : t.newtab {cwd}. L'effet se lit ensuite avec app_state.",
            Obj(("app", "string", "Le programme."), ("command", "string", "La commande : un objet JSON avec « cmd » et ses champs.")), false, a => Ok(AppCommand(S(a, "app"), S(a, "command")))),
        new("browser_tabs", "Les onglets du navigateur CDX : numéro, titre, adresse, celui qui est actif.", Obj(), true, a => Ok(BrowserTabs())),
        new("browser_open", "Ouvre une adresse dans un nouvel onglet du navigateur CDX (lancé au besoin) et attend la fin du chargement. Rend le titre et l'adresse obtenus.", Obj(("url", "string", "Adresse http ou https.")), false, a => Ok(BrowserOpen(S(a, "url")))),
        new("browser_navigate", "Va à une adresse dans l'onglet actif du navigateur CDX et attend la fin du chargement.", Obj(("url", "string", "Adresse http ou https.")), false, a => Ok(BrowserNavigate(S(a, "url")))),
        new("browser_read", "Le texte lisible de la page active du navigateur CDX (titre, adresse, texte), pour la lire et décider.", Obj(("max_chars", "integer", "(facultatif) Longueur maximale du texte (40000 par défaut, 200000 au plus).")), true, a => Ok(Browser("page.text " + I(a, "max_chars", 40000)))),
        new("browser_find", "Les éléments de la page active sur lesquels on peut agir, numérotés : liens, boutons, champs, listes (texte, adresse, nom). Les numéros servent à browser_click et browser_fill.", Obj(), true, a => Ok(Browser("page.find"))),
        new("browser_click", "Clique un élément de la page active : par son numéro (browser_find), par son texte, ou par un sélecteur CSS. Attend un éventuel chargement et rend la nouvelle adresse.", Obj(("n", "integer", "(facultatif) Numéro donné par browser_find."), ("text", "string", "(facultatif) Texte du lien ou du bouton."), ("selector", "string", "(facultatif) Sélecteur CSS.")), false, a => Ok(BrowserClick(a))),
        new("browser_fill", "Remplit un champ de la page active (numéro de browser_find, sélecteur CSS ou nom) avec une valeur, comme une frappe ; « submit » valide ensuite (Entrée, formulaire).", Obj(("value", "string", "La valeur à écrire (pour une case : true/false ; pour une liste : le texte ou la valeur de l'option)."), ("n", "integer", "(facultatif) Numéro donné par browser_find."), ("selector", "string", "(facultatif) Sélecteur CSS."), ("name", "string", "(facultatif) Attribut name ou id du champ."), ("submit", "boolean", "(facultatif) Valider après avoir rempli.")), false, a => Ok(BrowserFill(a))),
        new("browser_back", "Revient à la page précédente de l'onglet actif.", Obj(), false, a => Ok(BrowserCmd("{\"cmd\":\"br.back\"}", true))),
        new("browser_scroll", "Fait défiler la page active (vers le bas par défaut) et rend la position.", Obj(("dy", "integer", "(facultatif) Pixels, négatif pour remonter (600 par défaut).")), false, a => Ok(Browser("page.scroll " + I(a, "dy", 600)))),
        new("browser_screenshot", "Une capture (PNG) de la page active du navigateur CDX, pour la voir telle qu'elle est.", Obj(), true, a => Screenshot()),
        new("notify", "Une notification dans la barre CDX (titre court, texte).", Obj(("title", "string", "Titre (80 caractères au plus)."), ("text", "string", "(facultatif) Texte (400 caractères au plus).")), false, a => Ok(Notify(S(a, "title"), S(a, "text", false)))),
        new("project_folder", "Crée le dossier d'un projet dans le dossier des projets de Valentin (Dev\\), avec un dépôt git initialisé, et rend son chemin. S'il existe déjà, le dit sans rien toucher. La fiche du projet se crée à part avec projects_create (notes).",
            Obj(("name", "string", "Nom du projet (deviendra le nom du dossier)."), ("git", "boolean", "(facultatif) Initialiser un dépôt git (oui par défaut).")), false, a => Ok(ProjectFolder(S(a, "name"), !(a["git"] is JsonValue gv && gv.TryGetValue<bool>(out var g)) || g))),
    };

    static JsonArray ToolList()
    {
        var arr = new JsonArray();
        foreach (var t in Tools) arr.Add(new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["inputSchema"] = t.Schema.DeepClone(), ["annotations"] = new JsonObject { ["readOnlyHint"] = t.ReadOnly, ["destructiveHint"] = false, ["idempotentHint"] = false, ["openWorldHint"] = t.Name.StartsWith("browser_") } });
        return arr;
    }

    static string Ok(string text) => text != null && text.StartsWith("ERREUR") ? Fail(text) : new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text ?? "" }), ["isError"] = false }.ToJsonString();
    static string Fail(string text) => new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["isError"] = true }.ToJsonString();
    static string S(JsonObject a, string k, bool required = true)
    {
        string v = a[k] is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : a[k] is JsonObject jo ? jo.ToJsonString() : null;
        if (required && string.IsNullOrWhiteSpace(v)) throw new ArgException("Il manque « " + k + " ».");
        return v?.Trim() ?? "";
    }
    static int I(JsonObject a, string k, int def) => a[k] is JsonValue jv && jv.TryGetValue<int>(out var n) ? n : def;

    // ---------- la suite ----------
    static bool PipeExists(string app) { try { string n = Bus.PipeName(app); return Directory.GetFiles(@"\\.\pipe\").Any(f => Path.GetFileName(f) == n); } catch (Exception) { return false; } }
    static string BusName(string app) => app == "courrier" ? "mail" : app;
    static string ExeOf(string app) => app switch { "mail" => "cdx-mail.exe", _ => "cdx-" + app + ".exe" };

    // Lance un programme de la suite s'il ne tourne pas (chemin enregistré, sinon l'exécutable à côté de Pilote), et attend son tube. True : il tourne.
    static bool EnsureRunning(string app, string arg)
    {
        if (PipeExists(app)) return true;
        string exe = Suite.AppPath(app); if (exe == null) { string f = Path.Combine(AppContext.BaseDirectory, ExeOf(app)); if (File.Exists(f)) exe = f; }
        if (exe == null) return false;
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) };
            if (!string.IsNullOrEmpty(arg)) psi.ArgumentList.Add(arg);
            var p = Process.Start(psi); if (p != null) lock (launched) launched.Add(p.Id);
            Host.Log("lancé : " + exe + (arg != null ? " " + arg : ""));
        }
        catch (Exception ex) { Host.Log("lancement de " + app + " : " + ex.Message); return false; }
        var t0 = DateTime.UtcNow;
        while ((DateTime.UtcNow - t0).TotalSeconds < 15) { if (PipeExists(app)) { Thread.Sleep(300); return true; } Thread.Sleep(150); }
        return false;
    }

    static string Pilot(string app, string line)
    {
        string a = BusName(app); if (!AppNames.Contains(app) && app != "mail") return "ERREUR programme inconnu : " + app;
        if (!PipeExists(a)) return "ERREUR " + app + " ne tourne pas (app_open le lance).";
        return Host.Pilot(a, line) ?? "ERREUR " + app + " ne répond pas";
    }

    static string SuiteState()
    {
        var running = Bus.Apps.Where(x => x != "pilote" && PipeExists(x)).Select(x => x == "mail" ? "courrier" : x).ToList();
        var wins = new List<object>();
        N.EnumWindows((h, _) =>
        {
            if (!N.IsWindowVisible(h)) return true;
            string exe = Path.GetFileName(N.ExePathOf(h) ?? "").ToLowerInvariant();
            if (!(exe.StartsWith("cdx-") || exe == "kirc.exe" || exe == "audiolinker.exe")) return true;
            string title = N.TitleOf(h); if (title.Length == 0) return true;
            N.GetWindowRect(h, out var rc);
            wins.Add(new { app = exe.Replace(".exe", ""), title, x = rc.Left, y = rc.Top, w = rc.Right - rc.Left, h = rc.Bottom - rc.Top, minimized = N.IsIconic(h) });
            return true;
        }, IntPtr.Zero);
        return JsonSerializer.Serialize(new { running, windows = wins, projectsRoot = app.RootDir, notesVault = app.VaultDir, conversation = app.CurrentTitle }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    static string AppOpen(string appName, string target)
    {
        if (!AppNames.Contains(appName)) throw new ArgException("Programme inconnu : " + appName + " (explorer, browser, terminal, notes, repos, agenda, courrier, transfers).");
        string a = BusName(appName); target = string.IsNullOrWhiteSpace(target) ? null : target.Trim();
        string type = null; object data = new { };
        switch (a)
        {
            case "browser":
                if (target != null && Bus.IsBrowsableFile(target)) { type = "open-file"; data = new { path = target }; }
                else if (target != null) { if (!Uri.TryCreate(target, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) throw new ArgException("Adresse http(s) ou fichier affichable attendu."); type = "open-url"; data = new { url = target }; }
                else type = "open-url";
                break;
            case "explorer":
                if (target != null && File.Exists(target)) { type = "select"; data = new { path = Path.GetFullPath(target) }; }
                else if (target != null && Directory.Exists(target)) { type = "open-path"; data = new { path = Path.GetFullPath(target) }; target = Path.GetFullPath(target); }
                else if (target != null) throw new ArgException("Dossier ou fichier introuvable : " + target);
                else type = "open-path";
                break;
            case "terminal":
                if (target != null && File.Exists(target)) target = Path.GetDirectoryName(target);
                if (target != null && !Directory.Exists(target)) throw new ArgException("Dossier introuvable : " + target);
                type = target != null ? "open-path" : "show"; if (target != null) { target = Path.GetFullPath(target); data = new { path = target }; }
                break;
            case "notes": type = target != null ? "open-note" : "show"; if (target != null) data = new { path = target }; break;
            default: type = "show"; target = null; break;
        }
        if (!EnsureRunning(a, target)) throw new ArgException("Impossible de lancer " + appName + ".");
        bool sent = Bus.Send(a, type, data, "pilote");
        return sent ? "ok : " + appName + (target != null ? " · " + target : "") : "ERREUR " + appName + " n'a pas pris le message";
    }

    static string AppCommand(string appName, string command)
    {
        JsonObject o;
        try { o = JsonNode.Parse(command) as JsonObject; } catch (Exception) { o = null; }
        if (o == null || o["cmd"] is not JsonValue) throw new ArgException("La commande doit être un objet JSON avec « cmd ».");
        string cmd = o["cmd"].GetValue<string>(); if (cmd.StartsWith("win.")) throw new ArgException("Les commandes de fenêtre ne passent pas par là.");
        string r = Pilot(appName, "cmd " + o.ToJsonString());
        return r;
    }

    static string Notify(string title, string text)
    {
        if (title.Length > 80) title = title[..80]; if (text != null && text.Length > 400) text = text[..400];
        return Bus.Send("shell", "notify", new { title, text = text ?? "", level = "info" }, "pilote") ? "ok" : "ERREUR la barre CDX ne tourne pas";
    }

    static readonly Regex BadName = new(@"[<>:""/\\|?*\x00-\x1f]");
    static string ProjectFolder(string name, bool git)
    {
        string clean = BadName.Replace(name, "").Trim().TrimEnd('.');
        if (clean.Length == 0 || clean.Length > 100) throw new ArgException("Nom de projet invalide.");
        string path = Path.Combine(app.RootDir, clean);
        if (Directory.Exists(path)) return JsonSerializer.Serialize(new { path, exists = true, git = Directory.Exists(Path.Combine(path, ".git")) });
        Directory.CreateDirectory(path);
        bool gitOk = false;
        if (git)
        {
            try
            {
                var psi = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = path, RedirectStandardOutput = true, RedirectStandardError = true };
                psi.ArgumentList.Add("init"); psi.ArgumentList.Add("-q");
                using var p = Process.Start(psi); p.WaitForExit(20000); gitOk = p.HasExited && p.ExitCode == 0;
            }
            catch (Exception ex) { Host.Log("git init : " + ex.Message); }
        }
        Host.Log("dossier de projet créé : " + path);
        return JsonSerializer.Serialize(new { path, exists = false, git = gitOk });
    }

    // ---------- le navigateur ----------
    static string Browser(string line) { string r = Pilot("browser", line); return r != null && r.StartsWith("\"") ? Unquote(r) : r; }
    static string Unquote(string jsonString) { try { return JsonSerializer.Deserialize<string>(jsonString); } catch (Exception) { return jsonString; } }
    sealed class TabInfo { public int Id; public string Title; public string Url; public bool Loading; }
    static (List<TabInfo> tabs, int active) Tabs()
    {
        string st = Pilot("browser", "state"); if (st == null || st.StartsWith("ERREUR") || st == "pas de navigateur") return (new List<TabInfo>(), 0);
        using var d = JsonDocument.Parse(st); var r = d.RootElement;
        var tabs = new List<TabInfo>();
        if (r.TryGetProperty("tabs", out var ts)) foreach (var t in ts.EnumerateArray()) tabs.Add(new TabInfo { Id = t.GetProperty("Id").GetInt32(), Title = t.TryGetProperty("Title", out var ti) ? ti.GetString() ?? "" : "", Url = t.TryGetProperty("Url", out var u) ? u.GetString() ?? "" : "", Loading = t.TryGetProperty("Loading", out var l) && l.ValueKind == JsonValueKind.True });
        int active = r.TryGetProperty("active", out var av) && av.ValueKind == JsonValueKind.Number ? av.GetInt32() : 0;
        return (tabs, active);
    }
    static string BrowserTabs()
    {
        if (!PipeExists("browser")) return "Le navigateur ne tourne pas.";
        var (tabs, active) = Tabs();
        return JsonSerializer.Serialize(tabs.Select(t => new { id = t.Id, title = t.Title, url = t.Url, active = t.Id == active, loading = t.Loading }), new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
    // Attend la fin du chargement de l'onglet actif (ou que son adresse ait changé, puis la fin) ; rend titre et adresse.
    static string WaitLoaded(string urlBefore, int ms = 20000)
    {
        var t0 = DateTime.UtcNow; TabInfo last = null; bool changed = urlBefore == null;
        Thread.Sleep(400);
        while ((DateTime.UtcNow - t0).TotalMilliseconds < ms)
        {
            var (tabs, active) = Tabs(); last = tabs.FirstOrDefault(t => t.Id == active) ?? tabs.FirstOrDefault();
            if (last == null) break;
            if (!changed && last.Url != urlBefore) changed = true;
            if ((changed || (DateTime.UtcNow - t0).TotalMilliseconds > 2500) && !last.Loading) break;
            Thread.Sleep(250);
        }
        if (last != null) Thread.Sleep(300);   // le texte de la page se pose juste après
        return last == null ? "ERREUR pas d'onglet" : JsonSerializer.Serialize(new { title = last.Title, url = last.Url, loading = last.Loading }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
    static string CheckUrl(string url) { if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) throw new ArgException("Adresse http(s) attendue."); return u.AbsoluteUri; }
    static string BrowserOpen(string url)
    {
        url = CheckUrl(url);
        bool was = PipeExists("browser");
        if (!EnsureRunning("browser", url)) throw new ArgException("Impossible de lancer le navigateur.");
        if (was && !Bus.Send("browser", "open-url", new { url }, "pilote")) return "ERREUR le navigateur n'a pas pris l'adresse";
        return WaitLoaded(null);
    }
    static string BrowserNavigate(string url)
    {
        url = CheckUrl(url);
        if (!PipeExists("browser")) return BrowserOpen(url);
        var (tabs, active) = Tabs(); string before = tabs.FirstOrDefault(t => t.Id == active)?.Url;
        string r = Pilot("browser", "cmd " + JsonSerializer.Serialize(new { cmd = "br.go", url }));
        if (r != "ok") return "ERREUR " + r;
        return WaitLoaded(before);
    }
    static string BrowserCmd(string json, bool wait)
    {
        var (tabs, active) = Tabs(); string before = tabs.FirstOrDefault(t => t.Id == active)?.Url;
        string r = Pilot("browser", "cmd " + json); if (r != "ok") return "ERREUR " + r;
        return wait ? WaitLoaded(before) : "ok";
    }
    static string BrowserClick(JsonObject a)
    {
        var arg = new JsonObject();
        if (a["n"] is JsonValue nv && nv.TryGetValue<int>(out var n)) arg["n"] = n;
        if (a["text"] is JsonValue tv && tv.TryGetValue<string>(out var t) && t.Length > 0) arg["text"] = t;
        if (a["selector"] is JsonValue sv && sv.TryGetValue<string>(out var s) && s.Length > 0) arg["selector"] = s;
        if (arg.Count == 0) throw new ArgException("Donner n, text ou selector.");
        var (tabs, active) = Tabs(); string before = tabs.FirstOrDefault(x => x.Id == active)?.Url;
        string r = Browser("page.click " + arg.ToJsonString());
        if (r == null || r.StartsWith("ERREUR")) return r ?? "ERREUR";
        string after = WaitLoaded(before, 8000);
        return r.TrimEnd('}') + ", \"page\": " + after + "}";
    }
    static string BrowserFill(JsonObject a)
    {
        var arg = new JsonObject { ["value"] = S(a, "value") };
        if (a["n"] is JsonValue nv && nv.TryGetValue<int>(out var n)) arg["n"] = n;
        if (a["selector"] is JsonValue sv && sv.TryGetValue<string>(out var s) && s.Length > 0) arg["selector"] = s;
        if (a["name"] is JsonValue mv && mv.TryGetValue<string>(out var m) && m.Length > 0) arg["name"] = m;
        if (a["submit"] is JsonValue bv && bv.TryGetValue<bool>(out var b) && b) arg["submit"] = true;
        if (arg.Count == 1) throw new ArgException("Donner n, selector ou name.");
        var (tabs, active) = Tabs(); string before = tabs.FirstOrDefault(x => x.Id == active)?.Url;
        string r = Browser("page.fill " + arg.ToJsonString());
        if (r == null || r.StartsWith("ERREUR")) return r ?? "ERREUR";
        if (arg["submit"] != null) { string after = WaitLoaded(before, 8000); return r.TrimEnd('}') + ", \"page\": " + after + "}"; }
        return r;
    }
    static string Screenshot()
    {
        string b64 = Pilot("browser", "page.shot");
        if (b64 == null || b64.StartsWith("ERREUR")) return Fail(b64 ?? "ERREUR");
        return new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "image", ["data"] = b64, ["mimeType"] = "image/png" }), ["isError"] = false }.ToJsonString();
    }
}
