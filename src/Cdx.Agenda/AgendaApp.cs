using System.Drawing;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Cdx;

// Le programme : une fenêtre « Agenda » sur le compte Google de la suite (GoogleAuth) : les agendas et leurs événements (Google Calendar), les listes de tâches
// (Google Tasks). La page web/agenda/agenda.html dessine le mois, la semaine, la liste, les tâches ; le programme parle aux API et garde un cache (agenda-cache.json)
// pour afficher quelque chose hors ligne. Réglages propres : agenda.json (agendas masqués).
sealed class AgendaApp : ApplicationContext, IHostApp
{
    string theme;
    readonly string root;
    readonly IDisposable watch;
    AgendaForm form;
    CoreWebView2Environment env;
    readonly HashSet<string> hidden = new();
    JsonElement? calendars;              // la liste des agendas (calendarList), telle que Google la rend
    DateTime calendarsAt;
    object lastEvents, lastTasks;        // ce qui a été envoyé en dernier (cache)
    string lastFrom = "", lastTo = "";
    bool busy; string lastError = "";
    public readonly Control Anchor = new Control();   // installe le contexte de synchronisation de l'interface et sert à y poser un travail

    static string SettingsFile => Path.Combine(Host.DataDir, "agenda.json");
    static string CacheFile => Path.Combine(Host.DataDir, "agenda-cache.json");
    static readonly string[] Scopes = { GoogleAuth.ScopeCalendar, GoogleAuth.ScopeTasks };

    public AgendaApp(string theme)
    {
        this.theme = theme ?? Suite.ReadAccent(); root = Host.FindWebRoot();
        _ = Anchor.Handle;
        try { using var d = JsonDocument.Parse(File.ReadAllText(SettingsFile)); if (d.RootElement.TryGetProperty("hidden", out var h)) foreach (var x in h.EnumerateArray()) hidden.Add(x.GetString()); } catch { }
        try
        {
            using var d = JsonDocument.Parse(File.ReadAllText(CacheFile)); var r = d.RootElement;
            if (r.TryGetProperty("events", out var ev)) lastEvents = JsonSerializer.Deserialize<object>(ev.GetRawText());
            if (r.TryGetProperty("tasks", out var tk)) lastTasks = JsonSerializer.Deserialize<object>(tk.GetRawText());
        }
        catch { }
        if (theme == null) watch = Suite.Watch(this.theme, a => { this.theme = a; form?.Send(new { type = "theme", value = a }); });
    }

    public bool Closing { get; private set; }
    protected override void ExitThreadCore() { Closing = true; watch?.Dispose(); base.ExitThreadCore(); }

    void SaveSettings() { try { File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new { hidden })); } catch (Exception ex) { Host.Log("agenda.json : " + ex.Message); } }
    void SaveCache() { try { File.WriteAllText(CacheFile, JsonSerializer.Serialize(new { events = lastEvents, tasks = lastTasks })); } catch (Exception ex) { Host.Log("cache : " + ex.Message); } }

    public async void Open()
    {
        try
        {
            if (form != null && !form.IsDisposed)
            {
                if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
                form.Activate(); Host.ForceForeground(form.Handle); return;
            }
            env ??= await CoreWebView2Environment.CreateAsync(null, Path.Combine(Host.DataDir, "webview-agenda"), new CoreWebView2EnvironmentOptions("--disable-lcd-text"));
            var f = new AgendaForm(this); form = f;
            f.FormClosed += (s, e) => { if (form == f) form = null; ExitThread(); };
            double scale = f.DeviceDpi / 96.0;
            f.Bounds = new Rectangle(200, 80, (int)(1180 * scale), (int)(760 * scale));
            f.Show(); Host.ForceForeground(f.Handle);
            await f.InitWebAsync(env, root, "agenda/agenda.html?theme=" + theme, scale);
            Host.Log("agenda ouvert");
        }
        catch (Exception ex) { Host.Log("agenda : " + ex); }
    }

    // ---------- état envoyé à la page ----------
    void PushState() => form?.Send(new { type = "state", connected = GoogleAuth.IsConnected, email = GoogleAuth.Email, hasClient = GoogleAuth.HasClient, busy, error = lastError, clientFile = GoogleAuth.ClientFile });
    void Fail(string what, Exception ex)
    {
        lastError = what + " : " + ex.Message; Host.Log(lastError);
        if (ex is GoogleApiException { Status: 403 } ge && ge.Message.Contains("API", StringComparison.OrdinalIgnoreCase)) lastError += " (l'API n'est peut-être pas activée dans le projet Google Cloud)";
        form?.Send(new { type = "error", text = lastError }); PushState();
    }

    // ---------- Google Calendar ----------
    async Task<JsonElement> CalendarsAsync()
    {
        if (calendars != null && (DateTime.UtcNow - calendarsAt).TotalMinutes < 10) return calendars.Value;
        using var d = await GoogleAuth.ApiAsync(HttpMethod.Get, "/calendar/v3/users/me/calendarList?maxResults=250");
        calendars = JsonDocument.Parse(d.RootElement.GetRawText()).RootElement; calendarsAt = DateTime.UtcNow;
        return calendars.Value;
    }
    static string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
    static string S2(JsonElement e, string k, string k2) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Object ? S(v, k2) : "";

    async Task LoadEventsAsync(string from, string to)
    {
        if (!GoogleAuth.IsConnected) { PushState(); return; }
        busy = true; PushState();
        try
        {
            var cl = await CalendarsAsync();
            var cals = cl.TryGetProperty("items", out var its) ? its.EnumerateArray().Select(c => new { id = S(c, "id"), name = S(c, "summaryOverride") is { Length: > 0 } so ? so : S(c, "summary"), color = S(c, "backgroundColor"), primary = c.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True, on = !hidden.Contains(S(c, "id")), writable = S(c, "accessRole") is "owner" or "writer" }).ToList() : new();
            var items = new List<object>();
            // les dates reçues sont des jours locaux ; Google veut des instants RFC 3339 : minuit local, fuseau de la machine
            string min = new DateTimeOffset(DateTime.Parse(from)).ToString("yyyy-MM-dd'T'HH:mm:ssK"), max = new DateTimeOffset(DateTime.Parse(to)).ToString("yyyy-MM-dd'T'HH:mm:ssK");
            foreach (var c in cals.Where(c => c.on))
            {
                string page = null;
                do
                {
                    using var d = await GoogleAuth.ApiAsync(HttpMethod.Get, $"/calendar/v3/calendars/{Uri.EscapeDataString(c.id)}/events?singleEvents=true&orderBy=startTime&maxResults=2500&timeMin={Uri.EscapeDataString(min)}&timeMax={Uri.EscapeDataString(max)}" + (page != null ? "&pageToken=" + Uri.EscapeDataString(page) : ""));
                    var r = d.RootElement;
                    if (r.TryGetProperty("items", out var evs))
                        foreach (var e in evs.EnumerateArray())
                        {
                            if (S(e, "status") == "cancelled") continue;
                            bool allDay = e.TryGetProperty("start", out var st) && st.TryGetProperty("date", out _);
                            items.Add(new { id = S(e, "id"), cal = c.id, calName = c.name, color = S(e, "colorId") is { Length: > 0 } ? c.color : c.color, title = S(e, "summary") is { Length: > 0 } t ? t : "(sans titre)", allDay, start = allDay ? S2(e, "start", "date") : S2(e, "start", "dateTime"), end = allDay ? S2(e, "end", "date") : S2(e, "end", "dateTime"), location = S(e, "location"), description = S(e, "description"), link = S(e, "htmlLink"), writable = c.writable });
                        }
                    page = S(r, "nextPageToken"); if (page.Length == 0) page = null;
                } while (page != null);
            }
            lastEvents = new { from, to, calendars = cals, items }; lastFrom = from; lastTo = to; lastError = "";
            form?.Send(new { type = "events", from, to, calendars = cals, items, fresh = true }); SaveCache();
        }
        catch (Exception ex) { Fail("Lecture des agendas", ex); }
        finally { busy = false; PushState(); }
    }

    async Task SaveEventAsync(JsonElement r)
    {
        string St(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool allDay = r.TryGetProperty("allDay", out var ad) && ad.ValueKind == JsonValueKind.True;
        string cal = St("cal"), id = St("id");
        if (string.IsNullOrEmpty(cal) || string.IsNullOrEmpty(St("title")) || string.IsNullOrEmpty(St("start")) || string.IsNullOrEmpty(St("end"))) { form?.Send(new { type = "error", text = "Il manque le titre, l'agenda ou les dates." }); return; }
        object When(string v) => allDay ? new { date = v } : new { dateTime = new DateTimeOffset(DateTime.Parse(v)).ToString("yyyy-MM-dd'T'HH:mm:ssK") };
        var body = new Dictionary<string, object> { ["summary"] = St("title"), ["start"] = When(St("start")), ["end"] = When(St("end")), ["location"] = St("location") ?? "", ["description"] = St("description") ?? "" };
        try
        {
            if (string.IsNullOrEmpty(id)) { using var d = await GoogleAuth.ApiAsync(HttpMethod.Post, $"/calendar/v3/calendars/{Uri.EscapeDataString(cal)}/events", body); Host.Log("événement créé : " + St("title")); }
            else { using var d = await GoogleAuth.ApiAsync(HttpMethod.Patch, $"/calendar/v3/calendars/{Uri.EscapeDataString(cal)}/events/{Uri.EscapeDataString(id)}", body); Host.Log("événement modifié : " + St("title")); }
            form?.Send(new { type = "saved", what = "event" });
            if (lastFrom.Length > 0) await LoadEventsAsync(lastFrom, lastTo);
        }
        catch (Exception ex) { Fail("Enregistrement de l'événement", ex); }
    }

    async Task DeleteEventAsync(string cal, string id)
    {
        try { using var d = await GoogleAuth.ApiAsync(HttpMethod.Delete, $"/calendar/v3/calendars/{Uri.EscapeDataString(cal)}/events/{Uri.EscapeDataString(id)}"); Host.Log("événement supprimé"); if (lastFrom.Length > 0) await LoadEventsAsync(lastFrom, lastTo); }
        catch (Exception ex) { Fail("Suppression de l'événement", ex); }
    }

    // ---------- Google Tasks ----------
    async Task LoadTasksAsync()
    {
        if (!GoogleAuth.IsConnected) return;
        try
        {
            var lists = new List<object>();
            using var d = await GoogleAuth.ApiAsync(HttpMethod.Get, "/tasks/v1/users/@me/lists?maxResults=100");
            if (d.RootElement.TryGetProperty("items", out var ls))
                foreach (var l in ls.EnumerateArray())
                {
                    string lid = S(l, "id"); var tasks = new List<object>();
                    using var t = await GoogleAuth.ApiAsync(HttpMethod.Get, $"/tasks/v1/lists/{Uri.EscapeDataString(lid)}/tasks?showCompleted=true&showHidden=true&maxResults=100");
                    if (t.RootElement.TryGetProperty("items", out var ts))
                        foreach (var x in ts.EnumerateArray())
                            if (S(x, "title").Length > 0 || S(x, "notes").Length > 0)
                                tasks.Add(new { id = S(x, "id"), title = S(x, "title"), done = S(x, "status") == "completed", due = S(x, "due"), notes = S(x, "notes"), position = S(x, "position"), parent = S(x, "parent") });
                    lists.Add(new { id = lid, title = S(l, "title"), tasks = tasks.OrderBy(o => ((dynamic)o).position).ToList() });
                }
            lastTasks = new { lists }; lastError = "";
            form?.Send(new { type = "tasks", lists, fresh = true }); SaveCache(); PushState();
        }
        catch (Exception ex) { Fail("Lecture des tâches", ex); }
    }

    async Task TaskCommandAsync(string cmd, JsonElement r)
    {
        string St(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string list = St("list"), id = St("id"); if (string.IsNullOrEmpty(list)) return;
        try
        {
            switch (cmd)
            {
                case "ag.task.add":
                {
                    if (string.IsNullOrWhiteSpace(St("title"))) return;
                    var body = new Dictionary<string, object> { ["title"] = St("title").Trim() };
                    if (St("due") is { Length: > 0 } due) body["due"] = due.Length == 10 ? due + "T00:00:00.000Z" : due;
                    using var d = await GoogleAuth.ApiAsync(HttpMethod.Post, $"/tasks/v1/lists/{Uri.EscapeDataString(list)}/tasks", body); break;
                }
                case "ag.task.toggle":
                {
                    bool done = r.TryGetProperty("done", out var dv) && dv.ValueKind == JsonValueKind.True;
                    var body = done ? new Dictionary<string, object> { ["status"] = "completed" } : new Dictionary<string, object> { ["status"] = "needsAction", ["completed"] = null };
                    using var d = await GoogleAuth.ApiAsync(HttpMethod.Patch, $"/tasks/v1/lists/{Uri.EscapeDataString(list)}/tasks/{Uri.EscapeDataString(id)}", body); break;
                }
                case "ag.task.edit":
                {
                    var body = new Dictionary<string, object>();
                    if (St("title") != null) body["title"] = St("title"); if (St("notes") != null) body["notes"] = St("notes");
                    if (St("due") != null) body["due"] = St("due").Length == 0 ? null : St("due").Length == 10 ? St("due") + "T00:00:00.000Z" : St("due");
                    using var d = await GoogleAuth.ApiAsync(HttpMethod.Patch, $"/tasks/v1/lists/{Uri.EscapeDataString(list)}/tasks/{Uri.EscapeDataString(id)}", body); break;
                }
                case "ag.task.delete": { using var d = await GoogleAuth.ApiAsync(HttpMethod.Delete, $"/tasks/v1/lists/{Uri.EscapeDataString(list)}/tasks/{Uri.EscapeDataString(id)}"); break; }
                case "ag.list.add": { if (string.IsNullOrWhiteSpace(St("title"))) return; using var d = await GoogleAuth.ApiAsync(HttpMethod.Post, "/tasks/v1/users/@me/lists", new { title = St("title").Trim() }); break; }
            }
            await LoadTasksAsync();
        }
        catch (Exception ex) { Fail("Tâches", ex); }
    }

    // ---------- compte ----------
    async Task ConnectAsync()
    {
        busy = true; lastError = ""; PushState();
        try
        {
            string err = await GoogleAuth.ConnectAsync(Scopes);
            if (err != null) { lastError = err; Host.Log("Google : " + err); }
        }
        finally { busy = false; PushState(); }
        if (GoogleAuth.IsConnected) { if (lastFrom.Length > 0) await LoadEventsAsync(lastFrom, lastTo); await LoadTasksAsync(); }
    }

    // ---------- messages de la page ----------
    public async void OnMessage(HostForm from, string json)
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
                case "ag.ready":
                    PushState();
                    if (lastEvents != null) from.Send(JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(lastEvents)).Concat(new[] { new KeyValuePair<string, object>("type", "events"), new("fresh", false) }).ToDictionary(k => k.Key, k => k.Value));
                    if (lastTasks != null) from.Send(JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(lastTasks)).Concat(new[] { new KeyValuePair<string, object>("type", "tasks"), new("fresh", false) }).ToDictionary(k => k.Key, k => k.Value));
                    break;
                case "ag.connect": await ConnectAsync(); break;
                case "ag.disconnect": await GoogleAuth.DisconnectAsync(); lastEvents = null; lastTasks = null; calendars = null; SaveCache(); PushState(); break;
                case "ag.load": await LoadEventsAsync(St("from"), St("to")); break;
                case "ag.tasks": await LoadTasksAsync(); break;
                case "ag.event.save": await SaveEventAsync(r); break;
                case "ag.event.delete": await DeleteEventAsync(St("cal"), St("id")); break;
                case "ag.cal.toggle": { bool on = r.TryGetProperty("on", out var ov) && ov.ValueKind == JsonValueKind.True; if (on) hidden.Remove(St("id")); else hidden.Add(St("id")); SaveSettings(); if (lastFrom.Length > 0) await LoadEventsAsync(lastFrom, lastTo); break; }
                case "ag.task.add": case "ag.task.toggle": case "ag.task.edit": case "ag.task.delete": case "ag.list.add": await TaskCommandAsync(cmd, r); break;
                case "ag.open": if (St("url") is { Length: > 0 } u && u.StartsWith("https://")) _ = Task.Run(() => Bus.Send("browser", "open-url", new { url = u }, "agenda", launch: true)); break;
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

    // Canal de test (debug.txt) : state · eval <js> · cmd <json> · google-url (l'adresse de consentement en attente).
    static readonly JsonSerializerOptions PilotJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public async Task<string> HandleControl(string line)
    {
        int sp = line.IndexOf(' '); string c = sp < 0 ? line : line[..sp], a = sp < 0 ? "" : line[(sp + 1)..];
        switch (c)
        {
            case "state": return JsonSerializer.Serialize(new { connected = GoogleAuth.IsConnected, email = GoogleAuth.Email, hasClient = GoogleAuth.HasClient, busy, error = lastError, hidden, from = lastFrom, to = lastTo });
            case "google-url": return GoogleAuth.PendingUrl ?? "";
            // ---- le pilotage (Pilote / Claude) : lire les événements d'une plage de jours et les tâches ----
            case "events":
            {
                if (!GoogleAuth.IsConnected) return "ERREUR compte Google non relié dans Agenda";
                var parts = a.Split(' ', StringSplitOptions.RemoveEmptyEntries); if (parts.Length < 2) return "ERREUR attendu : events AAAA-MM-JJ AAAA-MM-JJ";
                await LoadEventsAsync(parts[0], parts[1]);
                return lastError.Length > 0 ? "ERREUR " + lastError : JsonSerializer.Serialize(lastEvents, PilotJson);
            }
            case "tasks":
            {
                if (!GoogleAuth.IsConnected) return "ERREUR compte Google non relié dans Agenda";
                await LoadTasksAsync();
                return lastError.Length > 0 ? "ERREUR " + lastError : JsonSerializer.Serialize(lastTasks, PilotJson);
            }
            case "eval": return form == null ? "pas de fenêtre" : await form.Web.CoreWebView2.ExecuteScriptAsync(a);
            case "cmd": if (form != null) OnMessage(form, a); return "ok";
            default: return "commande inconnue";
        }
    }
}
