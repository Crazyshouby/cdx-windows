using System.Drawing;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Cdx;

// Le programme : une fenêtre « Pilote », le chat avec Claude réservé à l'organisation et à la création des projets. La page web/pilote/pilote.html dessine
// les conversations, le fil et le volet des actions ; le programme tient les processus Claude Code (un par conversation, ClaudeSession), décide seul ce qui
// peut l'être (PilotPolicy) et demande le reste à Valentin, garde les conversations (pilote.json, pilote\<id>.json) et sert les outils (PilotTools).
sealed class PiloteApp : ApplicationContext, IHostApp
{
    string theme;
    readonly string root;
    readonly IDisposable watch;
    PiloteForm form;
    CoreWebView2Environment env;
    public readonly Control Anchor = new Control();
    public bool Closing { get; private set; }

    // Des propriétés (pas des champs) : HostForm.Send sérialise avec les options par défaut, qui ignorent les champs.
    sealed class Conv { public string id { get; set; } public string session { get; set; } public string title { get; set; } = ""; public string created { get; set; } public string updated { get; set; } public int turns { get; set; } }
    sealed class Item { public string kind { get; set; } public string text { get; set; } public string name { get; set; } public string target { get; set; } public string id { get; set; } public bool? ok { get; set; } public string result { get; set; } public string at { get; set; } public string decision { get; set; } public string title { get; set; } public string detail { get; set; } }
    sealed class Pending { public string conv; public string tool; public string input; public string title; public string detail; }
    readonly List<Conv> convs = new();
    readonly Dictionary<string, List<Item>> items = new();
    readonly Dictionary<string, ClaudeSession> sessions = new();
    readonly Dictionary<string, Pending> pending = new();
    readonly Dictionary<string, System.Text.StringBuilder> buffers = new();   // le texte de la réponse en cours, par conversation
    readonly HashSet<string> restartAfterTurn = new();
    string current;
    string level = "libre", model = "", effort = "";
    string lastError = "";
    double quotaPart = -1, quotaSeven = -1; long quotaReset, quotaSevenReset;
    static readonly JsonSerializerOptions JsonOpts = new() { IncludeFields = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static string StoreFile => Path.Combine(Host.DataDir, "pilote.json");
    static string ConvDir => Path.Combine(Host.DataDir, "pilote");
    static string McpFile => Path.Combine(Host.DataDir, "pilote-mcp.json");
    // Le dossier des projets (CDX_PILOTE_ROOT pour les essais, sinon Dev\ dans le profil) et le coffre de Notes (notes.json, CDX_NOTES_DIR, sinon Documents\Notes CDX).
    public string RootDir => Environment.GetEnvironmentVariable("CDX_PILOTE_ROOT") is { Length: > 0 } r ? r : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Dev");
    public string VaultDir
    {
        get
        {
            string e = Environment.GetEnvironmentVariable("CDX_NOTES_DIR"); if (!string.IsNullOrWhiteSpace(e)) return Path.GetFullPath(e);
            try { using var d = JsonDocument.Parse(File.ReadAllText(Path.Combine(Host.DataDir, "notes.json"))); if (d.RootElement.TryGetProperty("vault", out var v) && v.ValueKind == JsonValueKind.String && v.GetString().Length > 0) return Path.GetFullPath(v.GetString()); } catch (Exception) { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Notes CDX");
        }
    }
    public string CurrentTitle => convs.FirstOrDefault(c => c.id == current)?.title ?? "";

    public PiloteApp(string theme)
    {
        this.theme = theme ?? Suite.ReadAccent(); root = Host.FindWebRoot();
        _ = Anchor.Handle;
        Load();
        if (theme == null) watch = Suite.Watch(this.theme, a => { this.theme = a; form?.Send(new { type = "theme", value = a }); });
    }

    protected override void ExitThreadCore()
    {
        Closing = true; watch?.Dispose();
        foreach (var s in sessions.Values) s.Kill();
        base.ExitThreadCore();
    }

    // ---------- conversations ----------
    void Load()
    {
        try
        {
            if (!File.Exists(StoreFile)) return;
            using var d = JsonDocument.Parse(File.ReadAllText(StoreFile)); var r = d.RootElement;
            if (r.TryGetProperty("conversations", out var cs)) foreach (var c in cs.EnumerateArray()) { var x = JsonSerializer.Deserialize<Conv>(c.GetRawText(), JsonOpts); if (x?.id != null) convs.Add(x); }
            if (r.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.String && PilotPolicy.Levels.Contains(l.GetString())) level = l.GetString();
            if (r.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String) model = m.GetString();
            if (r.TryGetProperty("effort", out var e) && e.ValueKind == JsonValueKind.String) effort = e.GetString();
            if (r.TryGetProperty("current", out var cu) && cu.ValueKind == JsonValueKind.String && convs.Any(c => c.id == cu.GetString())) current = cu.GetString();
        }
        catch (Exception ex) { Host.Log("pilote.json : " + ex.Message); }
    }
    void Save()
    {
        try { File.WriteAllText(StoreFile, JsonSerializer.Serialize(new { conversations = convs, level, model, effort, current }, JsonOpts)); }
        catch (Exception ex) { Host.Log("pilote.json : " + ex.Message); }
    }
    List<Item> Items(string conv)
    {
        if (items.TryGetValue(conv, out var l)) return l;
        l = new List<Item>();
        try { string f = Path.Combine(ConvDir, conv + ".json"); if (File.Exists(f)) l = JsonSerializer.Deserialize<List<Item>>(File.ReadAllText(f), JsonOpts) ?? l; } catch (Exception ex) { Host.Log("conversation " + conv + " : " + ex.Message); }
        items[conv] = l; return l;
    }
    void SaveItems(string conv)
    {
        try { Directory.CreateDirectory(ConvDir); File.WriteAllText(Path.Combine(ConvDir, conv + ".json"), JsonSerializer.Serialize(Items(conv), JsonOpts)); }
        catch (Exception ex) { Host.Log("conversation " + conv + " : " + ex.Message); }
    }
    Conv NewConv()
    {
        var c = new Conv { id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..4], session = Guid.NewGuid().ToString(), created = DateTime.Now.ToString("s"), updated = DateTime.Now.ToString("s") };
        convs.Insert(0, c); current = c.id; items[c.id] = new List<Item>(); Save(); return c;
    }
    static string Now => DateTime.Now.ToString("HH:mm");

    // ---------- fenêtre ----------
    public async void Open()
    {
        try
        {
            if (form != null && !form.IsDisposed)
            {
                if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
                form.Activate(); Host.ForceForeground(form.Handle); return;
            }
            env ??= await CoreWebView2Environment.CreateAsync(null, Path.Combine(Host.DataDir, "webview-pilote"), new CoreWebView2EnvironmentOptions("--disable-lcd-text"));
            var f = new PiloteForm(this); form = f;
            f.FormClosed += (s, e) => { if (form == f) form = null; ExitThread(); };
            double scale = f.DeviceDpi / 96.0;
            f.Bounds = new Rectangle(160, 60, (int)(1240 * scale), (int)(800 * scale));
            f.Show(); Host.ForceForeground(f.Handle);
            await f.InitWebAsync(env, root, "pilote/pilote.html?theme=" + theme, scale);
            Host.Log("pilote ouvert");
        }
        catch (Exception ex) { Host.Log("pilote : " + ex); }
    }

    // ---------- état envoyé à la page ----------
    void PushState()
    {
        bool busy = current != null && sessions.TryGetValue(current, out var s) && s.Busy;
        form?.Send(new
        {
            type = "state", conversations = convs.Select(c => new { c.id, c.title, c.updated, c.turns, busy = sessions.TryGetValue(c.id, out var x) && x.Busy }), current, busy, level, model, effort,
            claude = ClaudeSession.ExeFound(), claudeExe = ClaudeSession.FindExe() ?? "", error = lastError, root = RootDir, vault = VaultDir,
            quota = quotaPart < 0 ? null : new { part = quotaPart, reset = quotaReset, seven = quotaSeven, sevenReset = quotaSevenReset },
            pending = pending.Where(p => p.Value.conv == current).Select(p => new { id = p.Key, p.Value.tool, p.Value.title, p.Value.detail, input = ParseOr(p.Value.input) }),
        });
    }
    static object ParseOr(string json) { try { return JsonSerializer.Deserialize<object>(json); } catch (Exception) { return null; } }
    void PushItems() { if (current != null) form?.Send(new { type = "items", conv = current, items = Items(current), partial = buffers.TryGetValue(current, out var b) ? b.ToString() : "" }); }

    // ---------- Claude ----------
    string SystemPrompt() =>
        "Tu es Pilote, l'assistant de Valentin dans sa suite CDX (Windows), réservé à l'organisation et à la création de ses projets. Tu parles français, simplement. " +
        $"Tu travailles sur son PC : le dossier des projets est {RootDir}, le coffre de notes est {VaultDir}. " +
        "Outils : les notes (mcp__cdx-notes__* ; vault_info d'abord ; les fiches de projet ont « type: projet » : projects_list, projects_create, tasks_list) ; " +
        "la suite (mcp__cdx-suite__* : suite_state d'abord ; mail_search et mail_read LISENT le courrier Gmail ; agenda_events et agenda_tasks LISENT l'agenda et les tâches ; app_open ouvre un programme sur une cible ; app_state donne son état ; app_command lui envoie une commande dont l'effet se voit dans sa fenêtre, pas dans le résultat (ne sert pas à lire) ; browser_open, browser_read, browser_find, browser_click, browser_fill, browser_screenshot pour naviguer, lire et agir sur le web dans le navigateur CDX ; project_folder crée le dossier d'un projet avec git ; notify prévient dans la barre ; app_restart ferme et relance un programme quand un outil dit qu'il tourne dans une version antérieure). " +
        "Créer un projet = project_folder (dossier + git), puis projects_create (la fiche dans Notes, avec le chemin du dossier en « repo » et un résumé), puis, si on te le demande, un README dans le dossier et les tâches ou échéances (fiche, ou Agenda par app_command ag.task.add / ag.event.save). " +
        "Reste dans ce rôle : organiser, planifier, créer, documenter, retrouver, chercher sur le web. Pour écrire le code d'un projet, Valentin passe par Claude Code lui-même : propose-le plutôt que de le faire. " +
        "Règles : lis ce qu'il faut, pas plus. Jamais d'envoi de courrier, de suppression ni d'envoi vers un dépôt distant sans son accord explicite ; une demande d'accord s'affiche chez lui pour toute action sensible : annonce-la en une ligne avant. En cas de doute, pose une question courte (AskUserQuestion pour un choix). " +
        "Réponses courtes, en Markdown léger (listes, gras, code), sans titres inutiles.";

    static string Esc(string s) => s.Replace("\\", "\\\\");
    void WriteMcpConfig()
    {
        string dir = AppContext.BaseDirectory;
        var servers = new Dictionary<string, object>();
        var envSuite = new Dictionary<string, string>(); var envNotes = new Dictionary<string, string>();
        foreach (var k in new[] { "CDX_PIPE_PREFIX", "CDX_DATA_DIR", "CDX_SUITE_DIR" }) { string v = Environment.GetEnvironmentVariable(k); if (!string.IsNullOrEmpty(v)) { envSuite[k] = v; envNotes[k] = v; } }
        if (Environment.GetEnvironmentVariable("CDX_NOTES_DIR") is { Length: > 0 } nd) envNotes["CDX_NOTES_DIR"] = nd;
        servers["cdx-suite"] = new { command = Path.Combine(dir, "cdx-pilote-mcp.exe"), args = Array.Empty<string>(), env = envSuite };
        string notes = Path.Combine(dir, "cdx-notes-mcp.exe");
        if (File.Exists(notes)) servers["cdx-notes"] = new { command = notes, args = Array.Empty<string>(), env = envNotes };
        File.WriteAllText(McpFile, JsonSerializer.Serialize(new { mcpServers = servers }, JsonOpts));
    }

    ClaudeSession SessionFor(Conv c)
    {
        if (sessions.TryGetValue(c.id, out var s) && s.Alive) return s;
        string exe = ClaudeSession.FindExe() ?? throw new InvalidOperationException("Claude Code est introuvable (claude.exe) : installe-le, ou indique son chemin dans CDX_CLAUDE_EXE.");
        Directory.CreateDirectory(RootDir);
        WriteMcpConfig();
        PilotMemory.Ensure(VaultDir);
        var args = ClaudeSession.Arguments(c.session, c.turns > 0, model, effort, McpFile, PilotPolicy.ReadTools, new[] { RootDir, VaultDir }.Where(Directory.Exists), SystemPrompt() + PilotMemory.Prompt(VaultDir));
        s = new ClaudeSession(c.id, OnEvent, OnExit);
        // la mémoire automatique de Claude Code (dossier ~/.claude, lié à Dev) est coupée : la mémoire de Pilote est dans le coffre (PilotMemory)
        s.Start(exe, args, RootDir, new Dictionary<string, string> { ["CDX_PILOTE_CONV"] = c.id, ["CLAUDE_CODE_DISABLE_AUTO_MEMORY"] = "1" });
        sessions[c.id] = s;
        Host.Log($"claude {(c.turns > 0 ? "repris" : "ouvert")} pour la conversation {c.id} (session {c.session}, pid {s.Pid})");
        return s;
    }

    void SendMessage(string text)
    {
        text = text?.Trim(); if (string.IsNullOrEmpty(text)) return;
        var c = convs.FirstOrDefault(x => x.id == current) ?? NewConv();
        if (sessions.TryGetValue(c.id, out var cur) && cur.Busy) { form?.Send(new { type = "error", text = "Claude travaille encore : attends la fin, ou arrête-le." }); return; }
        ClaudeSession s;
        try { s = SessionFor(c); }
        catch (Exception ex) { lastError = ex.Message; Host.Log(lastError); form?.Send(new { type = "error", text = lastError }); PushState(); return; }
        if (c.title.Length == 0) { c.title = text.Length > 60 ? text[..57] + "…" : text; c.title = c.title.Split('\n')[0]; }
        Items(c.id).Add(new Item { kind = "user", text = text, at = Now }); c.updated = DateTime.Now.ToString("s"); c.turns++; Save(); SaveItems(c.id);
        buffers[c.id] = new System.Text.StringBuilder(); lastError = "";
        s.Send(text);
        form?.Send(new { type = "item", conv = c.id, item = Items(c.id)[^1] });
        PushState();
    }

    void FlushBuffer(string conv)
    {
        if (!buffers.TryGetValue(conv, out var b) || b.Length == 0) return;
        Items(conv).Add(new Item { kind = "assistant", text = b.ToString(), at = Now }); b.Clear();
        form?.Send(new { type = "item", conv, item = Items(conv)[^1] });
    }

    void OnEvent(ClaudeSession s, ClaudeEvent e)
    {
        string conv = s.Conv; var c = convs.FirstOrDefault(x => x.id == conv);
        switch (e.Kind)
        {
            case "text":
                if (!buffers.TryGetValue(conv, out var b)) buffers[conv] = b = new System.Text.StringBuilder();
                b.Append(e.Text); form?.Send(new { type = "delta", conv, text = e.Text });
                break;
            case "thinking": form?.Send(new { type = "thinking", conv }); break;
            case "status": form?.Send(new { type = "status", conv, text = e.Text }); break;
            case "init":
                if (c != null && !string.IsNullOrEmpty(e.SessionId) && e.SessionId != c.session) { c.session = e.SessionId; Save(); }
                form?.Send(new { type = "init", conv, model = e.Text, mcp = e.Mcp }); Host.Log("claude : session " + e.SessionId + ", modèle " + e.Text + ", MCP " + e.Mcp);
                break;
            case "tool":
            {
                FlushBuffer(conv);
                var it = new Item { kind = "tool", id = e.ToolId, name = Nice(e.ToolName), target = e.Target, at = Now };
                Items(conv).Add(it); form?.Send(new { type = "item", conv, item = it });
                break;
            }
            case "result_tool":
            {
                var it = Items(conv).LastOrDefault(x => x.kind == "tool" && x.id == e.ToolId);
                if (it != null) { it.ok = !e.IsError; it.result = e.Text.Length > 300 ? e.Text[..297] + "…" : e.Text; form?.Send(new { type = "toolresult", conv, id = it.id, ok = it.ok, result = it.result }); }
                break;
            }
            case "permission":
            {
                JsonElement input; try { using var d = JsonDocument.Parse(e.Input); input = d.RootElement.Clone(); } catch (Exception) { input = default; }
                var (title, detail) = PilotPolicy.Describe(e.ToolName, input);
                var decision = PilotPolicy.Decide(e.ToolName, input, new[] { RootDir, VaultDir }, level);
                if (decision == PilotPolicy.Decision.Allow)
                {
                    s.Answer(e.RequestId, true, e.Input, null);
                    var it = new Item { kind = "ask", id = e.RequestId, title = title, detail = detail, decision = "auto", at = Now }; Items(conv).Add(it); form?.Send(new { type = "item", conv, item = it });
                    Host.Log("accord automatique : " + title + " · " + detail.Split('\n')[0]);
                }
                else
                {
                    FlushBuffer(conv);
                    pending[e.RequestId] = new Pending { conv = conv, tool = e.ToolName, input = e.Input, title = title, detail = detail };
                    var it = new Item { kind = "ask", id = e.RequestId, title = title, detail = detail, decision = "attente", at = Now }; Items(conv).Add(it);
                    form?.Send(new { type = "ask", conv, id = e.RequestId, tool = e.ToolName, title, detail, input = JsonSerializer.Deserialize<object>(e.Input) });
                    if (form != null && (form.WindowState == FormWindowState.Minimized || N.GetForegroundWindow() != form.Handle)) _ = Task.Run(() => Bus.Send("shell", "notify", new { title = "Pilote attend ton accord", text = title + (detail.Length > 0 ? " · " + detail.Split('\n')[0] : ""), level = "info" }, "pilote"));
                    PushState();
                }
                break;
            }
            case "quota":
                quotaPart = e.Part; quotaReset = e.ResetsAt; quotaSeven = e.SevenPart; quotaSevenReset = e.SevenResetsAt;
                form?.Send(new { type = "quota", part = quotaPart, reset = quotaReset, seven = quotaSeven, sevenReset = quotaSevenReset });
                break;
            case "result":
            {
                if (buffers.TryGetValue(conv, out var bb) && bb.Length == 0 && e.Text.Length > 0 && !e.IsError) bb.Append(e.Text);
                FlushBuffer(conv);
                if (e.IsError || (e.Subtype.Length > 0 && e.Subtype != "success"))
                {
                    string why = e.Text.Length > 0 ? e.Text : e.Subtype;
                    Items(conv).Add(new Item { kind = "note", text = "Claude s'est arrêté : " + why, at = Now }); form?.Send(new { type = "item", conv, item = Items(conv)[^1] });
                    // une session que Claude Code ne retrouve plus : la prochaine fois, on repart d'une session neuve (la conversation affichée reste)
                    if (c != null && why.Contains("No conversation found", StringComparison.OrdinalIgnoreCase)) { c.session = Guid.NewGuid().ToString(); c.turns = 0; Save(); }
                }
                if (c != null) { c.updated = DateTime.Now.ToString("s"); Save(); }
                SaveItems(conv);
                form?.Send(new { type = "result", conv, cost = e.CostUsd, input = e.InputTokens, output = e.OutputTokens, ms = e.DurationMs, ctxUsed = e.ContextUsed, ctxMax = e.ContextMax, error = e.IsError });
                if (restartAfterTurn.Remove(conv)) { s.Kill(); sessions.Remove(conv); }
                PushState();
                break;
            }
        }
    }

    void OnExit(ClaudeSession s, int code)
    {
        string conv = s.Conv;
        if (sessions.TryGetValue(conv, out var cur) && cur == s) sessions.Remove(conv);
        foreach (var k in pending.Where(p => p.Value.conv == conv).Select(p => p.Key).ToList()) { pending.Remove(k); form?.Send(new { type = "askdone", conv, id = k }); }
        bool hadTurn = buffers.TryGetValue(conv, out var b);
        FlushBuffer(conv);
        if (hadTurn && code != 0 && !Closing)
        {
            Items(conv).Add(new Item { kind = "note", text = code == -1 || code == 143 ? "Arrêté." : $"Claude Code s'est arrêté (code {code}). Regarde journal-pilote.log.", at = Now });
            form?.Send(new { type = "item", conv, item = Items(conv)[^1] });
            SaveItems(conv);
        }
        Host.Log($"claude : processus de la conversation {conv} terminé (code {code})");
        form?.Send(new { type = "result", conv, error = code != 0 });
        PushState();
    }

    static string Nice(string tool) => tool.StartsWith("mcp__cdx-suite__") ? "Suite · " + tool["mcp__cdx-suite__".Length..] : tool.StartsWith("mcp__cdx-notes__") ? "Notes · " + tool["mcp__cdx-notes__".Length..] : tool.StartsWith("mcp__") ? tool[5..] : tool;

    void AnswerPending(string id, bool allow, string message, string updatedInput)
    {
        if (!pending.Remove(id, out var p)) return;
        if (sessions.TryGetValue(p.conv, out var s) && s.Alive) s.Answer(id, allow, allow ? (updatedInput ?? p.input) : null, message);
        var it = Items(p.conv).LastOrDefault(x => x.kind == "ask" && x.id == id);
        if (it != null) { it.decision = allow ? "autorisé" : "refusé"; if (!allow && !string.IsNullOrEmpty(message)) it.detail = it.detail + "\n→ " + message; }
        SaveItems(p.conv);
        form?.Send(new { type = "askdone", conv = p.conv, id, decision = allow ? "autorisé" : "refusé" });
        Host.Log((allow ? "autorisé : " : "refusé : ") + p.title);
        PushState();
    }

    void Stop(string conv)
    {
        if (!sessions.TryGetValue(conv, out var s) || !s.Busy) return;
        s.Interrupt();
        var t = new System.Windows.Forms.Timer { Interval = 4000 };
        t.Tick += (o, e) => { t.Stop(); t.Dispose(); if (sessions.TryGetValue(conv, out var x) && x == s && s.Busy) { Host.Log("claude : interruption sans réponse, processus arrêté"); s.Kill(); } };
        t.Start();
    }

    // Les outils appelés par Claude via le tube (PilotTools) : notés au journal ; la page les voit déjà par le flux.
    public void NoteToolCall(string name, string args) => Host.Log("outil " + name + " " + (args.Length > 200 ? args[..200] + "…" : args));

    // ---------- messages de la page ----------
    public void OnMessage(HostForm from, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string cmd = r.GetProperty("cmd").GetString();
            string St(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (cmd.StartsWith("win.")) { Host.WindowCommand(from, cmd, r); return; }
            switch (cmd)
            {
                case "pl.ready": PushState(); PushItems(); break;
                case "pl.new": NewConv(); PushState(); PushItems(); break;
                case "pl.open": if (convs.Any(c => c.id == St("id"))) { current = St("id"); Save(); PushState(); PushItems(); } break;
                case "pl.send": if (St("id") != null && convs.Any(c => c.id == St("id"))) current = St("id"); SendMessage(St("text")); break;
                case "pl.stop": if (current != null) Stop(current); break;
                case "pl.answer":
                {
                    bool allow = r.TryGetProperty("allow", out var av) && av.ValueKind == JsonValueKind.True;
                    string upd = r.TryGetProperty("updatedInput", out var ui) && ui.ValueKind == JsonValueKind.Object ? ui.GetRawText() : null;
                    AnswerPending(St("id"), allow, St("message"), upd);
                    break;
                }
                case "pl.rename": { var c = convs.FirstOrDefault(x => x.id == St("id")); if (c != null && !string.IsNullOrWhiteSpace(St("title"))) { c.title = St("title").Trim(); Save(); PushState(); } break; }
                case "pl.delete":
                {
                    var c = convs.FirstOrDefault(x => x.id == St("id")); if (c == null) break;
                    if (sessions.Remove(c.id, out var s)) s.Kill();
                    foreach (var k in pending.Where(p => p.Value.conv == c.id).Select(p => p.Key).ToList()) pending.Remove(k);
                    convs.Remove(c); items.Remove(c.id); buffers.Remove(c.id);
                    try { string f = Path.Combine(ConvDir, c.id + ".json"); if (File.Exists(f)) File.Delete(f); } catch (Exception ex) { Host.Log("suppression : " + ex.Message); }
                    if (current == c.id) current = convs.FirstOrDefault()?.id;
                    Save(); PushState(); PushItems(); break;
                }
                case "pl.level": if (PilotPolicy.Levels.Contains(St("value"))) { level = St("value"); Save(); PushState(); } break;
                case "pl.model": case "pl.effort":
                {
                    string v = St("value") ?? "";
                    if (cmd == "pl.model") model = v; else effort = v;
                    Save();
                    // le processus de chaque conversation prend le réglage au message suivant (--resume, rien n'est perdu) ; celui qui travaille attend la fin du tour
                    foreach (var kv in sessions.ToList()) { if (kv.Value.Busy) restartAfterTurn.Add(kv.Key); else { kv.Value.Kill(); sessions.Remove(kv.Key); } }
                    PushState(); break;
                }
                case "pl.memory": PilotMemory.Ensure(VaultDir); _ = Task.Run(() => Bus.Send("notes", "open-note", new { path = PilotMemory.Folder + "/Profil.md" }, "pilote", launch: true)); break;
                case "pl.open-path": if (St("path") is { Length: > 0 } p && Directory.Exists(p)) _ = Task.Run(() => Bus.Send("explorer", "open-path", new { path = p }, "pilote", launch: true)); break;
                case "pl.open-url": if (St("url") is { Length: > 0 } u && (u.StartsWith("https://") || u.StartsWith("http://"))) _ = Task.Run(() => { if (!Bus.Send("browser", "open-url", new { url = u }, "pilote", launch: true)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u) { UseShellExecute = true }); }); break;
                default: Host.CommonMessage(from, cmd, r); break;
            }
        }
        catch (Exception ex) { Host.Log("message mal formé ou échec : " + ex.Message + " :: " + json); }
    }

    // Le bus : show (passer devant).
    public bool OnBus(string type, string from, JsonElement data)
    {
        if (type != "show") return false;
        Anchor.BeginInvoke(new Action(Open)); return true;
    }

    // Canal de test (debug.txt) : state · eval <js> · cmd <json>.
    public async Task<string> HandleControl(string line)
    {
        int sp = line.IndexOf(' '); string c = sp < 0 ? line : line[..sp], a = sp < 0 ? "" : line[(sp + 1)..];
        switch (c)
        {
            case "state":
                return JsonSerializer.Serialize(new
                {
                    current, level, model, effort, claude = ClaudeSession.ExeFound(), exe = ClaudeSession.FindExe(), error = lastError, root = RootDir, vault = VaultDir,
                    conversations = convs.Select(x => new { x.id, x.session, x.title, x.turns, items = Items(x.id).Count, busy = sessions.TryGetValue(x.id, out var s) && s.Busy, alive = sessions.TryGetValue(x.id, out var s2) && s2.Alive, pid = sessions.TryGetValue(x.id, out var s3) ? s3.Pid : 0 }),
                    pending = pending.Select(p => new { id = p.Key, p.Value.conv, p.Value.tool, p.Value.title }), launched = PilotTools.Launched, quota = quotaPart,
                }, JsonOpts);
            case "items": return JsonSerializer.Serialize(current == null ? new List<Item>() : Items(current), JsonOpts);
            case "eval": return form == null ? "pas de fenêtre" : await form.Web.CoreWebView2.ExecuteScriptAsync(a);
            case "cmd": if (form != null) OnMessage(form, a); return "ok";
            default: return "commande inconnue";
        }
    }
}
