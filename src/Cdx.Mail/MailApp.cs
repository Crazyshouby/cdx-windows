using System.Drawing;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Cdx;

// Le programme : une fenêtre « Courrier » sur la boîte Gmail du compte Google de la suite (GoogleAuth, portée gmail.modify). La page web/mail/courrier.html
// dessine les libellés, la liste des fils, le fil ouvert, la fenêtre d'écriture ; le programme parle à l'API Gmail (fils, messages, libellés, envoi, pièces
// jointes) et garde la dernière liste de la boîte de réception en cache (courrier-cache.json) pour l'afficher hors ligne. Un nouveau courrier est signalé à la barre.
sealed class MailApp : ApplicationContext, IHostApp
{
    string theme;
    readonly string root;
    readonly IDisposable watch;
    MailForm form;
    CoreWebView2Environment env;
    public readonly Control Anchor = new Control();   // installe le contexte de synchronisation de l'interface et sert à y poser un travail
    readonly System.Windows.Forms.Timer poll = new() { Interval = 90_000 };
    bool busy; string lastError = "";
    string curLabel = "INBOX", curQuery = "";
    List<object> lastThreads; List<object> lastLabels;
    int lastUnread = -1;
    // Le quota Gmail se compte en unités par minute et par utilisateur : threads.get en coûte 10, labels.get 1. On relit donc un fil seulement si sa version
    // (historyId) a changé depuis la dernière fois, on ne demande les compteurs que pour la boîte de réception et le libellé ouvert, et on s'arrête 30 s quand Google dit stop.
    readonly Dictionary<string, (string history, object summary)> threadCache = new();
    readonly Dictionary<string, (int unread, int total)> counts = new();
    DateTime quotaUntil = DateTime.MinValue; int loadSeq;
    static readonly string[] Scopes = { GoogleAuth.ScopeGmail };
    static string CacheFile => Path.Combine(Host.DataDir, "courrier-cache.json");

    public MailApp(string theme)
    {
        this.theme = theme ?? Suite.ReadAccent(); root = Host.FindWebRoot();
        _ = Anchor.Handle;
        try
        {
            using var d = JsonDocument.Parse(File.ReadAllText(CacheFile)); var r = d.RootElement;
            if (r.TryGetProperty("threads", out var t)) lastThreads = JsonSerializer.Deserialize<List<object>>(t.GetRawText());
            if (r.TryGetProperty("labels", out var l)) lastLabels = JsonSerializer.Deserialize<List<object>>(l.GetRawText());
            if (r.TryGetProperty("cache", out var c)) foreach (var e in c.EnumerateObject()) threadCache[e.Name] = (e.Value.GetProperty("h").GetString(), JsonSerializer.Deserialize<object>(e.Value.GetProperty("s").GetRawText()));
        }
        catch { }
        if (theme == null) watch = Suite.Watch(this.theme, a => { this.theme = a; form?.Send(new { type = "theme", value = a }); });
        poll.Tick += async (s, e) => { try { await PollAsync(); } catch (Exception ex) { Host.Log("relevé : " + ex.Message); } };
    }

    public bool Closing { get; private set; }
    protected override void ExitThreadCore() { Closing = true; poll.Dispose(); watch?.Dispose(); base.ExitThreadCore(); }
    void SaveCache() { try { AtomicFile.WriteAllText(CacheFile, JsonSerializer.Serialize(new { threads = lastThreads, labels = lastLabels, cache = threadCache.ToDictionary(k => k.Key, k => new { h = k.Value.history, s = k.Value.summary }) })); } catch (Exception ex) { Host.Log("cache : " + ex.Message); } }
    // Google a refusé pour quota : on attend 30 s avant tout nouvel appel, et on le dit.
    bool QuotaHold() { if (DateTime.UtcNow >= quotaUntil) return false; form?.Send(new { type = "error", text = $"Quota Google dépassé : nouvel essai possible dans {(int)(quotaUntil - DateTime.UtcNow).TotalSeconds + 1} s." }); return true; }
    bool IsQuota(Exception ex) { if (ex is GoogleApiException g && (g.Status == 429 || g.Message.Contains("Quota exceeded", StringComparison.OrdinalIgnoreCase))) { quotaUntil = DateTime.UtcNow.AddSeconds(30); return true; } return false; }

    public async void Open()
    {
        try
        {
            if (form != null && !form.IsDisposed)
            {
                if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
                form.Activate(); Host.ForceForeground(form.Handle); return;
            }
            env ??= await CoreWebView2Environment.CreateAsync(null, Path.Combine(Host.DataDir, "webview-mail"), new CoreWebView2EnvironmentOptions("--disable-lcd-text"));
            var f = new MailForm(this); form = f;
            f.FormClosed += (s, e) => { if (form == f) form = null; ExitThread(); };
            double scale = f.DeviceDpi / 96.0;
            f.Bounds = new Rectangle(160, 60, (int)(1320 * scale), (int)(800 * scale));
            f.Show(); Host.ForceForeground(f.Handle);
            await f.InitWebAsync(env, root, "mail/courrier.html?theme=" + theme, scale);
            // un lien d'un courrier (cadre isolé, cible _blank) part dans le navigateur de la suite, jamais dans une fenêtre du moteur
            f.Web.CoreWebView2.NewWindowRequested += (s, e) => { e.Handled = true; string u = e.Uri; if (u != null && (u.StartsWith("https://") || u.StartsWith("http://"))) _ = Task.Run(() => Bus.Send("browser", "open-url", new { url = u }, "mail", launch: true)); };
            poll.Start();
            Host.Log("courrier ouvert");
        }
        catch (Exception ex) { Host.Log("courrier : " + ex); }
    }

    // ---------- état ----------
    void PushState() => form?.Send(new { type = "state", connected = GoogleAuth.IsConnected && GoogleAuth.HasScope(GoogleAuth.ScopeGmail), email = GoogleAuth.Email, hasClient = GoogleAuth.HasClient, busy, error = lastError, clientFile = GoogleAuth.ClientFile, label = curLabel, q = curQuery });
    void Fail(string what, Exception ex) { lastError = IsQuota(ex) ? what + " : quota Google dépassé (trop d'appels en une minute), nouvel essai dans 30 s." : what + " : " + ex.Message; Host.Log(what + " : " + ex.Message); form?.Send(new { type = "error", text = lastError }); PushState(); }
    bool Ready => GoogleAuth.IsConnected && GoogleAuth.HasScope(GoogleAuth.ScopeGmail);
    static string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
    static int I(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    // ---------- libellés ----------
    async Task LoadLabelsAsync()
    {
        if (!Ready || QuotaHold()) return;
        try
        {
            using var d = await GoogleAuth.ApiAsync(HttpMethod.Get, Gmail.Me + "/labels");
            var raw = d.RootElement.TryGetProperty("labels", out var ls) ? ls.EnumerateArray().Select(l => (id: S(l, "id"), name: S(l, "name"), type: S(l, "type"))).Where(l => !Gmail.Hidden(l.id)).ToList() : new();
            // les compteurs : la boîte de réception et le libellé ouvert seulement (une unité chacun ; une boîte peut avoir des centaines de libellés)
            foreach (var id in new[] { "INBOX", curLabel }.Distinct().Where(x => raw.Any(l => l.id == x)))
            {
                try { using var g = await GoogleAuth.ApiAsync(HttpMethod.Get, Gmail.Me + "/labels/" + Uri.EscapeDataString(id)); counts[id] = (I(g.RootElement, "threadsUnread"), I(g.RootElement, "threadsTotal")); }
                catch (Exception ex) { Host.Log("libellé " + id + " : " + ex.Message); if (IsQuota(ex)) break; }
            }
            var items = new List<object>();
            foreach (var id in Gmail.SystemOrder) { var l = raw.FirstOrDefault(x => x.id == id); if (l.id != null) items.Add(new { id, name = Gmail.LabelName(id, l.name), system = true, unread = counts.TryGetValue(id, out var c) ? c.unread : 0, total = counts.TryGetValue(id, out var c2) ? c2.total : 0 }); }
            items.Add(new { id = "ALL", name = "Tous les messages", system = true, unread = 0, total = 0 });
            foreach (var l in raw.Where(x => x.type == "user").OrderBy(x => x.name, StringComparer.CurrentCultureIgnoreCase)) items.Add(new { id = l.id, name = l.name, system = false, unread = counts.TryGetValue(l.id, out var c) ? c.unread : 0, total = counts.TryGetValue(l.id, out var c2) ? c2.total : 0 });
            lastLabels = items; form?.Send(new { type = "labels", items, fresh = true }); SaveCache();
            if (counts.TryGetValue("INBOX", out var inbox)) lastUnread = inbox.unread;
        }
        catch (Exception ex) { Fail("Lecture des libellés", ex); }
    }

    // ---------- fils ----------
    async Task<List<object>> FetchThreadsAsync(string label, string q, int max = 50)
    {
        string path = Gmail.Me + "/threads?maxResults=" + max + (label == "ALL" ? "" : "&labelIds=" + Uri.EscapeDataString(label)) + (label is "SPAM" or "TRASH" ? "&includeSpamTrash=true" : "") + (q.Length > 0 ? "&q=" + Uri.EscapeDataString(q) : "");
        using var d = await GoogleAuth.ApiAsync(HttpMethod.Get, path);
        var ids = d.RootElement.TryGetProperty("threads", out var ts) ? ts.EnumerateArray().Select(t => (id: S(t, "id"), history: S(t, "historyId"))).ToList() : new();
        var result = new object[ids.Count]; var gate = new SemaphoreSlim(8); int fetched = 0;
        await Task.WhenAll(ids.Select(async (th, i) =>
        {
            string id = th.id;
            if (threadCache.TryGetValue(id, out var cached) && cached.history == th.history && th.history.Length > 0) { result[i] = cached.summary; return; }   // inchangé depuis la dernière lecture : pas d'appel
            await gate.WaitAsync();
            try
            {
                Interlocked.Increment(ref fetched);
                using var t = await GoogleAuth.ApiAsync(HttpMethod.Get, Gmail.Me + "/threads/" + Uri.EscapeDataString(id) + "?format=metadata&metadataHeaders=From&metadataHeaders=To&metadataHeaders=Subject&metadataHeaders=Date");
                var msgs = t.RootElement.TryGetProperty("messages", out var ms) ? ms.EnumerateArray().Select(Gmail.Parse).ToList() : new();
                if (msgs.Count == 0) return;
                var last = msgs.OrderBy(m => m.Date).Last();
                var froms = msgs.Select(m => m.From).Where(f => f.Length > 0).Select(f => { var n = System.Text.RegularExpressions.Regex.Match(f, @"^""?([^""<]+?)""?\s*<"); return n.Success ? n.Groups[1].Value.Trim() : f; }).Distinct().ToList();
                result[i] = new { id, subject = msgs[0].Subject.Length > 0 ? msgs[0].Subject : "(sans objet)", from = string.Join(", ", froms), snippet = last.Snippet, date = last.Date, count = msgs.Count, unread = msgs.Any(m => m.Unread), starred = msgs.Any(m => m.Starred), labels = msgs.SelectMany(m => m.Labels).Distinct().ToList() };
                lock (threadCache) threadCache[id] = (th.history, result[i]);
            }
            catch (Exception ex) { Host.Log("fil " + id + " : " + ex.Message); IsQuota(ex); }
            finally { gate.Release(); }
        }));
        if (fetched > 0) Host.Log($"fils relus : {fetched} sur {ids.Count}");
        return result.Where(r => r != null).ToList();
    }

    async Task LoadThreadsAsync(string label, string q)
    {
        curLabel = label ?? "INBOX"; curQuery = q ?? "";
        if (!Ready || QuotaHold()) { PushState(); return; }
        int seq = ++loadSeq;   // une lecture plus récente rend celle-ci caduque
        busy = true; PushState();
        try
        {
            var items = await FetchThreadsAsync(curLabel, curQuery);
            if (seq != loadSeq) return;
            if (curLabel == "INBOX" && curQuery.Length == 0) lastThreads = items;
            SaveCache();
            lastError = ""; form?.Send(new { type = "threads", label = curLabel, q = curQuery, items, fresh = true });
        }
        catch (Exception ex) { if (seq == loadSeq) Fail("Lecture des messages", ex); }
        finally { if (seq == loadSeq) { busy = false; PushState(); } }
    }

    async Task OpenThreadAsync(string id)
    {
        if (!Ready || string.IsNullOrEmpty(id) || QuotaHold()) return;
        try
        {
            using var t = await GoogleAuth.ApiAsync(HttpMethod.Get, Gmail.Me + "/threads/" + Uri.EscapeDataString(id) + "?format=full");
            var msgs = t.RootElement.TryGetProperty("messages", out var ms) ? ms.EnumerateArray().Select(Gmail.Parse).OrderBy(m => m.Date).ToList() : new();
            form?.Send(new { type = "thread", id, messages = msgs.Select(m => m.Full()) });
            // ouvrir un fil le marque lu
            if (msgs.Any(m => m.Unread)) { using var r = await GoogleAuth.ApiAsync(HttpMethod.Post, Gmail.Me + "/threads/" + Uri.EscapeDataString(id) + "/modify", new { removeLabelIds = new[] { "UNREAD" } }); form?.Send(new { type = "flag", id, unread = false }); _ = LoadLabelsAsync(); }
        }
        catch (Exception ex) { Fail("Lecture du fil", ex); }
    }

    // archive · trash · spam · inbox · read · unread · star · unstar
    async Task ActAsync(string id, string what)
    {
        if (!Ready || string.IsNullOrEmpty(id) || QuotaHold()) return;
        try
        {
            string p = Gmail.Me + "/threads/" + Uri.EscapeDataString(id);
            object body = what switch
            {
                "archive" => new { removeLabelIds = new[] { "INBOX" } }, "inbox" => new { addLabelIds = new[] { "INBOX" }, removeLabelIds = new[] { "TRASH", "SPAM" } }, "spam" => new { addLabelIds = new[] { "SPAM" }, removeLabelIds = new[] { "INBOX" } },
                "read" => new { removeLabelIds = new[] { "UNREAD" } }, "unread" => new { addLabelIds = new[] { "UNREAD" } }, "star" => new { addLabelIds = new[] { "STARRED" } }, "unstar" => new { removeLabelIds = new[] { "STARRED" } }, _ => null,
            };
            if (what == "trash") { using var r = await GoogleAuth.ApiAsync(HttpMethod.Post, p + "/trash"); }
            else if (body != null) { using var r = await GoogleAuth.ApiAsync(HttpMethod.Post, p + "/modify", body); }
            else return;
            Host.Log($"fil {id} : {what}");
            form?.Send(new { type = "done", what, id });
            await LoadThreadsAsync(curLabel, curQuery); _ = LoadLabelsAsync();
        }
        catch (Exception ex) { Fail("Action sur le fil", ex); }
    }

    // ---------- pièces jointes ----------
    async Task<byte[]> AttachmentBytesAsync(string msgId, string attId)
    {
        string txt = await GoogleAuthRaw.SendAsync(HttpMethod.Get, Gmail.Me + "/messages/" + Uri.EscapeDataString(msgId) + "/attachments/" + Uri.EscapeDataString(attId), null, null);
        using var d = JsonDocument.Parse(txt); return Gmail.B64UrlDecode(S(d.RootElement, "data"));
    }
    async Task SaveAttachmentAsync(string msgId, string attId, string name, string dir)
    {
        try
        {
            var bytes = await AttachmentBytesAsync(msgId, attId);
            string safe = string.Concat((name ?? "piece-jointe").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)); if (safe.Length == 0) safe = "piece-jointe";
            string folder = dir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"); Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, safe); for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, Path.GetFileNameWithoutExtension(safe) + $" ({i})" + Path.GetExtension(safe));
            await File.WriteAllBytesAsync(path, bytes);
            Host.Log("pièce jointe enregistrée : " + path);
            form?.Send(new { type = "saved", path, name = Path.GetFileName(path) });
            _ = Task.Run(() => Bus.Send("shell", "notify", new { title = "Pièce jointe enregistrée", text = Path.GetFileName(path), level = "info" }, "mail"));
        }
        catch (Exception ex) { Fail("Pièce jointe", ex); }
    }

    // ---------- envoi ----------
    async Task SendAsync(JsonElement r)
    {
        string St(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
        if (!Ready) return;
        if (St("to").Trim().Length == 0 && St("cc").Trim().Length == 0 && St("bcc").Trim().Length == 0) { form?.Send(new { type = "error", text = "Il manque un destinataire." }); return; }
        try
        {
            var files = new List<(string name, byte[] data, string mime)>();
            if (r.TryGetProperty("files", out var fs) && fs.ValueKind == JsonValueKind.Array)
                foreach (var f in fs.EnumerateArray()) { string p = f.GetString(); if (File.Exists(p)) files.Add((Path.GetFileName(p), await File.ReadAllBytesAsync(p), Gmail.Mime(p))); }
            if (r.TryGetProperty("forward", out var fw) && fw.ValueKind == JsonValueKind.Array)   // pièces jointes d'un message transféré, relues chez Google
                foreach (var a in fw.EnumerateArray()) files.Add((S(a, "name"), await AttachmentBytesAsync(S(a, "msg"), S(a, "id")), S(a, "mime")));
            long total = files.Sum(f => f.data.LongLength);
            if (total > 25 * 1024 * 1024) { form?.Send(new { type = "error", text = "Pièces jointes trop lourdes (25 Mo au plus)." }); return; }
            var raw = Gmail.BuildRaw(GoogleAuth.Email, St("to"), St("cc"), St("bcc"), St("subject"), St("text"), St("inReplyTo"), St("references"), files);
            string path = "/upload/gmail/v1/users/me/messages/send?uploadType=media" + (St("threadId").Length > 0 ? "" : "");
            string res = await GoogleAuthRaw.SendAsync(HttpMethod.Post, path, raw, "message/rfc822");
            using var d = JsonDocument.Parse(res);
            Host.Log("courrier envoyé à " + St("to") + (files.Count > 0 ? $" ({files.Count} pièce(s) jointe(s))" : ""));
            form?.Send(new { type = "sent", id = S(d.RootElement, "id") });
            await LoadThreadsAsync(curLabel, curQuery); _ = LoadLabelsAsync();
        }
        catch (Exception ex) { Fail("Envoi", ex); }
    }

    // ---------- relevé périodique : un nouveau courrier est signalé à la barre ----------
    async Task PollAsync()
    {
        if (!Ready || form == null) return;
        using var g = await GoogleAuth.ApiAsync(HttpMethod.Get, Gmail.Me + "/labels/INBOX");
        int unread = I(g.RootElement, "threadsUnread");
        if (lastUnread >= 0 && unread > lastUnread)
        {
            int n = unread - lastUnread;
            _ = Task.Run(() => Bus.Send("shell", "notify", new { title = n == 1 ? "Nouveau courrier" : n + " nouveaux courriers", text = "Boîte de réception : " + unread + " non lu(s)", level = "info" }, "mail"));
            await LoadLabelsAsync(); if (curLabel == "INBOX") await LoadThreadsAsync(curLabel, curQuery);
        }
        lastUnread = unread;
    }

    // ---------- compte ----------
    async Task ConnectAsync()
    {
        busy = true; lastError = ""; PushState();
        try { string err = await GoogleAuth.ConnectAsync(Scopes); if (err != null) { lastError = err; Host.Log("Google : " + err); } }
        finally { busy = false; PushState(); }
        if (Ready) { await LoadThreadsAsync("INBOX", ""); await LoadLabelsAsync(); }
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
                case "ml.ready":
                    PushState();
                    if (lastLabels != null) from.Send(new { type = "labels", items = lastLabels, fresh = false });
                    if (lastThreads != null) from.Send(new { type = "threads", label = "INBOX", q = "", items = lastThreads, fresh = false });
                    if (Ready) { await LoadThreadsAsync("INBOX", ""); await LoadLabelsAsync(); }
                    break;
                case "ml.connect": await ConnectAsync(); break;
                case "ml.disconnect": await GoogleAuth.DisconnectAsync(); lastThreads = null; lastLabels = null; SaveCache(); PushState(); break;
                case "ml.labels": await LoadLabelsAsync(); break;
                case "ml.list": await LoadThreadsAsync(St("label") ?? curLabel, St("q") ?? ""); break;
                case "ml.open": await OpenThreadAsync(St("id")); break;
                case "ml.act": await ActAsync(St("id"), St("what")); break;
                case "ml.attach": await SaveAttachmentAsync(St("msg"), St("id"), St("name"), St("dir")); break;
                case "ml.pick":
                {
                    using var dlg = new OpenFileDialog { Title = "Pièces jointes", Multiselect = true, Filter = "Tous les fichiers|*.*" };
                    if (dlg.ShowDialog(from) == DialogResult.OK) from.Send(new { type = "picked", files = dlg.FileNames.Select(p => new { path = p, name = Path.GetFileName(p), size = new FileInfo(p).Length }) });
                    break;
                }
                case "ml.send": await SendAsync(r); break;
                case "ml.link": if (St("url") is { Length: > 0 } u && (u.StartsWith("https://") || u.StartsWith("http://"))) _ = Task.Run(() => Bus.Send("browser", "open-url", new { url = u }, "mail", launch: true)); break;
                case "ml.reveal": if (St("path") is { Length: > 0 } p2) _ = Task.Run(() => { if (!Bus.Send("explorer", "select", new { path = p2 }, "mail", launch: true)) System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + p2 + "\""); }); break;
                default: Host.CommonMessage(from, cmd, r); break;
            }
        }
        catch (Exception ex) { Host.Log("message mal formé ou échec : " + ex.Message + " :: " + json); }
    }

    public bool OnBus(string type, string from, JsonElement data)
    {
        if (type != "show") return false;
        Anchor.BeginInvoke(new Action(Open)); return true;
    }

    // Canal de test (debug.txt) : state · eval <js> · cmd <json> · google-url · poll (relevé immédiat).
    public async Task<string> HandleControl(string line)
    {
        int sp = line.IndexOf(' '); string c = sp < 0 ? line : line[..sp], a = sp < 0 ? "" : line[(sp + 1)..];
        switch (c)
        {
            case "state": return JsonSerializer.Serialize(new { connected = Ready, email = GoogleAuth.Email, hasClient = GoogleAuth.HasClient, busy, error = lastError, label = curLabel, q = curQuery, unread = lastUnread });
            case "google-url": return GoogleAuth.PendingUrl ?? "";
            case "poll": try { await PollAsync(); return "ok"; } catch (Exception ex) { return "erreur : " + ex.Message; }
            case "eval": return form == null ? "pas de fenêtre" : await form.Web.CoreWebView2.ExecuteScriptAsync(a);
            case "cmd": if (form != null) OnMessage(form, a); return "ok";
            default: return "commande inconnue";
        }
    }
}
