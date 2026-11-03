using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace Cdx;

// ---- données du navigateur : favoris, historique, téléchargements, réglages, mots de passe, session, extensions ----
class Bookmark { public string Title { get; set; } = ""; public string Url { get; set; } = ""; public long Added { get; set; } }
class HistoryEntry { public string Title { get; set; } = ""; public string Url { get; set; } = ""; public long Time { get; set; } }

class DownloadItem
{
    public bool Announced;   // la fin du téléchargement a déjà été annoncée au shell
    public int Id; public string Name = "", Path = "", Url = "", State = "progress"; public long Received, Total;
    public CoreWebView2DownloadOperation Op;
    public bool Paused, Canceled;            // pour la fenêtre des transferts (la page interne du navigateur ne connaît que progress · done · error)
    public double Speed; public DateTime Tick = DateTime.UtcNow; public long TickBytes;
    public DateTime Started = DateTime.UtcNow;
    public object Info() => new { id = Id, name = Name, path = Path, url = Url, state = State, received = Received, total = Total };
}

static class BrowserData
{
    static string F(string n) => System.IO.Path.Combine(Host.DataDir, n);
    // Un fichier qui existe mais ne se lit pas n'est jamais écrasé par une liste vide : abîmé, il est mis de côté (« .corrompu ») ;
    // illisible pour une autre raison (verrouillé, droits), il n'est plus réécrit de la session.
    static readonly HashSet<string> unreadable = new();
    static List<T> Load<T>(string n)
    {
        string f = F(n);
        try { return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(f)) ?? new(); }
        catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException) { return new(); }
        catch (JsonException ex)
        {
            Host.Log(n + " abîmé, mis de côté en " + n + ".corrompu : " + ex.Message);
            try { File.Move(f, f + ".corrompu", true); } catch { unreadable.Add(n); }
            return new();
        }
        catch (Exception ex) { Host.Log(n + " illisible, il ne sera pas réécrit pendant cette session : " + ex.Message); unreadable.Add(n); return new(); }
    }
    static void Save<T>(string n, List<T> l)
    {
        if (unreadable.Contains(n)) return;
        try { AtomicFile.WriteJson(F(n), l); } catch (Exception ex) { Host.Log("sauvegarde " + n + " : " + ex.Message); }
    }

    public static readonly List<Bookmark> Bookmarks = Load<Bookmark>("favoris.json");
    public static readonly List<HistoryEntry> History = Load<HistoryEntry>("historique.json");
    public static readonly List<DownloadItem> Downloads = new();
    static int dlSeq;

    public static void SaveBookmarks() => Save("favoris.json", Bookmarks);
    public static void SaveHistory() { if (History.Count > 5000) History.RemoveRange(0, History.Count - 5000); Save("historique.json", History); }

    // L'historique (jusqu'à 5 000 entrées) n'est plus réécrit à chaque page : au plus une fois toutes les 2 s, et à la sortie.
    // Minuteur WinForms : il tourne sur le fil de l'interface, comme AddHistory, donc la liste n'est jamais lue pendant qu'on la modifie.
    static System.Windows.Forms.Timer historyTimer;
    static void ScheduleHistorySave()
    {
        if (historyTimer == null)
        {
            historyTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            historyTimer.Tick += (s, e) => { historyTimer.Stop(); SaveHistory(); };
        }
        if (!historyTimer.Enabled) historyTimer.Start();
    }
    // À la sortie du programme (BrowserApp.ExitThreadCore) : l'historique en attente est écrit.
    public static void FlushHistory() { if (historyTimer is { Enabled: true }) { historyTimer.Stop(); SaveHistory(); } }
    public static bool IsStarred(string url) => Bookmarks.Any(b => b.Url == url);
    public static void ToggleStar(string url, string title)
    {
        var b = Bookmarks.FirstOrDefault(x => x.Url == url);
        if (b != null) Bookmarks.Remove(b); else Bookmarks.Add(new Bookmark { Title = string.IsNullOrWhiteSpace(title) ? url : title, Url = url, Added = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        SaveBookmarks();
    }
    public static void AddHistory(string url, string title)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var last = History.LastOrDefault();
        if (last != null && last.Url == url && now - last.Time < 5000) { last.Title = title; return; }   // rechargement : pas de doublon
        History.Add(new HistoryEntry { Title = string.IsNullOrWhiteSpace(title) ? url : title, Url = url, Time = now });
        ScheduleHistorySave();
    }
    public static int NextDownloadId() => ++dlSeq;

    // Ce que la fenêtre « Transferts » voit d'un téléchargement.
    public static XferInfo XferOf(DownloadItem d)
    {
        string site = ""; try { site = new Uri(d.Url).Host; } catch { }
        string st = d.State == "done" ? "done" : d.State == "error" ? (d.Canceled ? "canceled" : "failed") : d.Paused ? "paused" : "running";
        return new XferInfo
        {
            Id = d.Id, Kind = "download", Title = d.Name, State = st, From = site, To = "Téléchargements", Path = d.Path, Done = d.Received, Total = Math.Max(d.Total, 0), Speed = st == "running" ? d.Speed : 0,
            Error = st == "failed" ? "Le téléchargement a été interrompu." : null, CanPause = d.Op != null && (st == "running" || st == "paused"),
        };
    }

    // Les ordres de la fenêtre : pause, reprise, annulation (sur le fil d'interface).
    public static bool Control(int id, string action)
    {
        var d = Downloads.FirstOrDefault(x => x.Id == id); var op = d?.Op; if (op == null) return false;
        try
        {
            switch (action)
            {
                case "pause": if (d.State == "progress" && !d.Paused) { op.Pause(); return true; } return false;
                case "resume": if (d.Paused && op.CanResume) { op.Resume(); return true; } return false;
                case "cancel": if (d.State == "progress") { op.Cancel(); return true; } return false;
            }
        }
        catch (Exception ex) { Host.Log("téléchargement : " + action + " : " + ex.Message); }
        return false;
    }

    // ---------- import des favoris d'un autre navigateur ----------
    // Les profils de Chrome et d'Edge qui ont un fichier « Bookmarks » (JSON), avec le nom du profil lu dans « Local State ».
    public static List<object> ImportSources()
    {
        var list = new List<object>();
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var (name, dir) in new[] { ("Chrome", System.IO.Path.Combine(local, "Google", "Chrome", "User Data")), ("Edge", System.IO.Path.Combine(local, "Microsoft", "Edge", "User Data")) })
        {
            if (!Directory.Exists(dir)) continue;
            var names = new Dictionary<string, string>();
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(dir, "Local State")));
                if (doc.RootElement.TryGetProperty("profile", out var p) && p.TryGetProperty("info_cache", out var ic))
                    foreach (var e in ic.EnumerateObject()) if (e.Value.TryGetProperty("name", out var n)) names[e.Name] = n.GetString();
            }
            catch { }
            foreach (var pd in Directory.GetDirectories(dir))
            {
                string f = System.IO.Path.Combine(pd, "Bookmarks"); if (!File.Exists(f)) continue;
                int count = 0; try { count = ParseBookmarks(f).Count; } catch { }
                string key = System.IO.Path.GetFileName(pd);
                list.Add(new { browser = name, profile = names.TryGetValue(key, out var pn) ? pn : key, path = f, count });
            }
        }
        return list;
    }

    // Un fichier « Bookmarks » de Chrome/Edge (JSON) ou un export HTML (Netscape, tous navigateurs) : la liste (titre, adresse).
    public static List<(string title, string url)> ParseBookmarks(string path)
    {
        var o = new List<(string, string)>();
        string text = File.ReadAllText(path);
        if (text.TrimStart().StartsWith("{"))
        {
            using var doc = JsonDocument.Parse(text);
            void Walk(JsonElement n)
            {
                if (n.ValueKind != JsonValueKind.Object) return;
                if (n.TryGetProperty("type", out var t) && t.GetString() == "url" && n.TryGetProperty("url", out var u)) { o.Add((n.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "", u.GetString() ?? "")); return; }
                if (n.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array) foreach (var c in ch.EnumerateArray()) Walk(c);
            }
            if (doc.RootElement.TryGetProperty("roots", out var roots)) foreach (var r in roots.EnumerateObject()) Walk(r.Value);
        }
        else
            foreach (Match m in Regex.Matches(text, @"<A\s[^>]*HREF=""([^""]*)""[^>]*>(.*?)</A>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                o.Add((System.Net.WebUtility.HtmlDecode(Regex.Replace(m.Groups[2].Value, "<[^>]+>", "")).Trim(), System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)));
        return o.Where(x => x.Item2.StartsWith("http://") || x.Item2.StartsWith("https://")).ToList();
    }

    public static (int added, int skipped) ImportBookmarks(string path)
    {
        int added = 0, skipped = 0; long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var (title, url) in ParseBookmarks(path))
        {
            if (Bookmarks.Any(b => b.Url == url)) { skipped++; continue; }
            Bookmarks.Add(new Bookmark { Title = string.IsNullOrWhiteSpace(title) ? url : title, Url = url, Added = now++ }); added++;
        }
        if (added > 0) SaveBookmarks();
        return (added, skipped);
    }
}

// ---------- réglages du navigateur (reglages-navigateur.json) ----------
class BrowserSettings
{
    public string Search { get; set; } = "google";
    public string DownloadDir { get; set; } = "";         // vide : le dossier Téléchargements de Windows
    public bool AskWhere { get; set; }                     // demander où enregistrer chaque téléchargement
    public bool RestoreSession { get; set; } = true;
    public bool SavePasswords { get; set; } = true;        // le gestionnaire de mots de passe du moteur propose d'enregistrer
    public bool Autofill { get; set; } = true;             // remplissage automatique (formulaires du moteur, et nos mots de passe importés)

    public static readonly (string id, string name, string url)[] Engines =
    {
        ("google", "Google", "https://www.google.com/search?q="), ("duckduckgo", "DuckDuckGo", "https://duckduckgo.com/?q="), ("qwant", "Qwant", "https://www.qwant.com/?q="),
        ("bing", "Bing", "https://www.bing.com/search?q="), ("brave", "Brave Search", "https://search.brave.com/search?q="), ("startpage", "Startpage", "https://www.startpage.com/do/search?q="),
    };
    public string SearchUrl => Engines.FirstOrDefault(e => e.id == Search).url ?? Engines[0].url;
    public string DownloadsFolder => DownloadDir.Length > 0 && Directory.Exists(DownloadDir) ? DownloadDir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    static string F => Path.Combine(Host.DataDir, "reglages-navigateur.json");
    public static BrowserSettings Current = Load();
    static BrowserSettings Load() { try { return JsonSerializer.Deserialize<BrowserSettings>(File.ReadAllText(F)) ?? new(); } catch { return new(); } }
    public void Save() { try { AtomicFile.WriteJson(F, this, new JsonSerializerOptions { WriteIndented = true }); } catch (Exception ex) { Host.Log("réglages : " + ex.Message); } }
    public object Payload() => new { search = Search, downloadDir = DownloadDir, downloadsFolder = DownloadsFolder, askWhere = AskWhere, restoreSession = RestoreSession, savePasswords = SavePasswords, autofill = Autofill, engines = Engines.Select(e => new { id = e.id, name = e.name }) };

    // Un réglage changé depuis la page cdx://reglages. Vrai si connu.
    public bool Set(string key, JsonElement v)
    {
        bool B() => v.ValueKind == JsonValueKind.True; string S() => v.ValueKind == JsonValueKind.String ? v.GetString() : "";
        switch (key)
        {
            case "search": if (Engines.Any(e => e.id == S())) Search = S(); break;
            case "downloadDir": DownloadDir = S(); break;
            case "askWhere": AskWhere = B(); break;
            case "restoreSession": RestoreSession = B(); break;
            case "savePasswords": SavePasswords = B(); break;
            case "autofill": Autofill = B(); break;
            default: return false;
        }
        Save(); return true;
    }
}

// ---------- mots de passe importés (export CSV de Chrome, Edge, Firefox...), chiffrés pour l'utilisateur Windows (DPAPI) dans motsdepasse.bin ----------
class Credential { public string Url { get; set; } = ""; public string User { get; set; } = ""; public string Password { get; set; } = ""; public string Name { get; set; } = ""; }

static class Passwords
{
    static string F => Path.Combine(Host.DataDir, "motsdepasse.bin");
    static bool loadFailed;   // le fichier existe mais n'a pas pu être lu : il est mis de côté avant la première écriture, jamais écrasé
    public static readonly List<Credential> All = Load();

    static List<Credential> Load()
    {
        try { if (!File.Exists(F)) return new(); return JsonSerializer.Deserialize<List<Credential>>(Encoding.UTF8.GetString(Dpapi.Unprotect(File.ReadAllBytes(F)))) ?? new(); }
        catch (Exception ex) { Host.Log("mots de passe illisibles : " + ex.Message); loadFailed = true; return new(); }
    }
    public static void Save()
    {
        try
        {
            if (loadFailed && File.Exists(F)) { File.Move(F, F + ".illisible-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), false); Host.Log("ancien motsdepasse.bin illisible conservé à côté"); }
            loadFailed = false;
            if (All.Count == 0) { File.Delete(F); return; }
            AtomicFile.WriteAllBytes(F, Dpapi.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(All)), "CDX navigateur"));
        }
        catch (Exception ex) { Host.Log("mots de passe : " + ex.Message); }
    }

    // L'origine d'une adresse (schéma, hôte, port), ou null. Seules comptent https, et http sur la boucle locale (rien ne passe sur le réseau) :
    // sur un http ordinaire, n'importe quel intermédiaire lirait le mot de passe posé dans la page.
    static string OriginOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttps && !(u.Scheme == Uri.UriSchemeHttp && u.IsLoopback)) return null;
        return u.Scheme + "://" + u.IdnHost.ToLowerInvariant() + ":" + u.Port;
    }
    // Les identifiants valables pour une page : exactement la même origine. Plus de domaine parent ni de sous-domaine (un identifiant de x.github.io
    // ne part pas sur github.io, ni l'inverse), plus de http.
    public static List<Credential> For(string url)
    {
        string o = OriginOf(url); if (o == null) return new();
        return All.Where(c => OriginOf(c.Url) == o).ToList();
    }
    public static bool Remove(string url, string user) { int n = All.RemoveAll(c => c.Url == url && c.User == user); if (n > 0) Save(); return n > 0; }

    // Un export CSV (en-tête name,url,username,password[,note] : Chrome, Edge ; Firefox : url,username,password,...). Doublons (même adresse, même nom) ignorés.
    public static (int added, int skipped) ImportCsv(string path)
    {
        var rows = ParseCsv(File.ReadAllText(path, Encoding.UTF8)); if (rows.Count == 0) return (0, 0);
        var head = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(params string[] names) { foreach (var n in names) { int i = head.IndexOf(n); if (i >= 0) return i; } return -1; }
        int cu = Col("url", "login_uri", "web site", "website"), cn = Col("username", "login_username", "login name", "user"), cp = Col("password", "login_password"), ct = Col("name", "title");
        if (cu < 0 || cp < 0) throw new InvalidOperationException("colonnes url / password introuvables dans l'en-tête");
        int added = 0, skipped = 0;
        foreach (var r in rows.Skip(1))
        {
            string G(int i) => i >= 0 && i < r.Count ? r[i] : "";
            string url = G(cu).Trim(), user = G(cn).Trim(), pw = G(cp);
            if (url.Length == 0 || pw.Length == 0) { skipped++; continue; }
            if (All.Any(c => c.Url == url && c.User == user)) { skipped++; continue; }
            All.Add(new Credential { Url = url, User = user, Password = pw, Name = G(ct).Trim() }); added++;
        }
        if (added > 0) Save();
        return (added, skipped);
    }

    // CSV avec champs entre guillemets (virgules et retours à la ligne dedans, guillemets doublés).
    public static List<List<string>> ParseCsv(string s)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var f = new StringBuilder(); bool q = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (q) { if (c == '"') { if (i + 1 < s.Length && s[i + 1] == '"') { f.Append('"'); i++; } else q = false; } else f.Append(c); }
            else if (c == '"') q = true;
            else if (c == ',') { row.Add(f.ToString()); f.Clear(); }
            else if (c == '\r') { }
            else if (c == '\n') { row.Add(f.ToString()); f.Clear(); if (row.Count > 1 || row[0].Length > 0) rows.Add(row); row = new(); }
            else f.Append(c);
        }
        if (f.Length > 0 || row.Count > 0) { row.Add(f.ToString()); rows.Add(row); }
        return rows;
    }

    // Le script posé dans une page de connexion. Il ne remplit RIEN tout seul : il attend un vrai geste de l'utilisateur (clic ou touche, isTrusted :
    // un script de la page ne peut pas en fabriquer, contrairement à focus()) dans le champ « mot de passe » ou le champ de texte du même formulaire, puis
    // remplit l'identifiant et le mot de passe. Les valeurs restent dans la fermeture du script, jamais dans une variable globale lisible par la page ;
    // elles sont passées en JSON (jamais concaténées dans du code).
    public static string FillScript(Credential c) => "(() => { if (window.__cdxFillArmed) return; window.__cdxFillArmed = true; const c = " + JsonSerializer.Serialize(new { user = c.User, password = c.Password }) + @";
const desc = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value');
const set = (el, v) => { desc.set.call(el, v); el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true })); };
const visible = e => e.offsetParent !== null;
const userOf = pw => { const all = [...(pw.form || document).querySelectorAll('input')]; for (let i = all.indexOf(pw) - 1; i >= 0; i--) { const t = (all[i].type || 'text').toLowerCase(); if (['text', 'email', 'tel'].includes(t) && visible(all[i])) return all[i]; } return null; };
const pwFor = el => { if (!(el instanceof HTMLInputElement)) return null; const t = (el.type || 'text').toLowerCase(); if (t === 'password') return el;
  if (!['text', 'email', 'tel'].includes(t)) return null; return [...(el.form || document).querySelectorAll('input[type=password]')].find(visible) || null; };
const on = e => { if (!e.isTrusted) return; const pw = pwFor(e.target); if (!pw || pw.dataset.cdxFilled) return; const u = userOf(pw); if (u && !u.value) set(u, c.user); if (!pw.value) set(pw, c.password); pw.dataset.cdxFilled = '1'; };
document.addEventListener('pointerdown', on, true); document.addEventListener('keyup', on, true); })();";
}

// ---------- la session : fenêtres, onglets, groupes (session.json, réécrit à chaque changement, pas seulement à la fermeture) ----------
class SessionTab { public string Url { get; set; } = ""; public string Title { get; set; } = ""; public int Group { get; set; } }
class SessionGroup { public int Id { get; set; } public string Name { get; set; } = ""; public string Color { get; set; } = "accent"; public bool Collapsed { get; set; } }
class SessionWindow { public int X { get; set; } public int Y { get; set; } public int W { get; set; } public int H { get; set; } public bool Max { get; set; } public int Active { get; set; } public List<SessionTab> Tabs { get; set; } = new(); public List<SessionGroup> Groups { get; set; } = new(); }
class SessionFile { public List<SessionWindow> Windows { get; set; } = new(); }

static class Session
{
    static string F => Path.Combine(Host.DataDir, "session.json");
    public static SessionFile Read()
    {
        try
        {
            string t = File.ReadAllText(F);
            if (t.TrimStart().StartsWith("[")) { var urls = JsonSerializer.Deserialize<List<string>>(t) ?? new(); return new SessionFile { Windows = { new SessionWindow { Tabs = urls.Select(u => new SessionTab { Url = u }).ToList() } } }; }   // ancien format : une liste d'adresses
            return JsonSerializer.Deserialize<SessionFile>(t) ?? new();
        }
        catch { return new(); }
    }
    public static void Write(SessionFile s)
    {
        try { AtomicFile.WriteJson(F, s); }
        catch (Exception ex) { Host.Log("session : " + ex.Message); }
    }
}

// ---------- extensions : dossiers non empaquetés sous <données>\extensions\<nom>, chargés dans le profil du navigateur ----------
static class Extensions
{
    public static string Dir => Path.Combine(Host.DataDir, "extensions");
    public static CoreWebView2Profile Profile;
    static readonly Dictionary<string, string> folderOf = new();   // id → dossier (pour la page de réglages de l'extension, et la suppression)
    public const string UbolApi = "https://api.github.com/repos/uBlockOrigin/uBOL-home/releases/latest";
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };

    static string IndexFile => Path.Combine(Dir, "index.json");
    static void SaveIndex() { try { AtomicFile.WriteJson(IndexFile, folderOf); } catch (Exception ex) { Host.Log("extensions\\index.json : " + ex.Message); } }

    // Au premier onglet : chaque dossier avec un manifest.json (sauf ceux qui commencent par « _ ») est ajouté au profil ; un dossier déjà connu garde son identifiant.
    public static async Task LoadAllAsync(CoreWebView2Profile p)
    {
        if (Profile != null) return; Profile = p; Directory.CreateDirectory(Dir);
        try { foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(IndexFile)) ?? new()) folderOf[kv.Key] = kv.Value; } catch { }
        foreach (var d in Folders()) await AddAsync(d);
    }
    static IEnumerable<string> Folders() => Directory.Exists(Dir) ? Directory.GetDirectories(Dir).Where(x => !Path.GetFileName(x).StartsWith("_") && File.Exists(Path.Combine(x, "manifest.json"))) : Array.Empty<string>();

    static async Task<CoreWebView2BrowserExtension> AddAsync(string folder)
    {
        try
        {
            var x = await Profile.AddBrowserExtensionAsync(folder);
            folderOf[x.Id] = folder; SaveIndex();
            Host.Log($"extension chargée : {Path.GetFileName(folder)} ({x.Id})"); return x;
        }
        catch (Exception ex) { Host.Log($"extension {Path.GetFileName(folder)} refusée : {ex.Message}"); return null; }
    }

    public static async Task<List<object>> ListAsync()
    {
        var o = new List<object>(); if (Profile == null) return o;
        foreach (var x in await Profile.GetBrowserExtensionsAsync())
        {
            folderOf.TryGetValue(x.Id, out var folder);
            // les extensions intégrées au moteur (presse-papiers, lecteur PDF d'Edge) n'ont pas de dossier à nous : montrées, mais ni désactivables ni retirables
            o.Add(new { id = x.Id, name = x.Name, enabled = x.IsEnabled, options = OptionsUrl(x.Id) != null, folder = folder != null ? Path.GetFileName(folder) : "", version = Version(folder), builtin = folder == null });
        }
        return o;
    }
    static string Version(string folder) { try { using var d = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json"))); return d.RootElement.TryGetProperty("version", out var v) ? v.GetString() : ""; } catch { return ""; } }

    // La page de réglages de l'extension (options_page ou options_ui.page du manifeste), ouverte dans un onglet.
    public static string OptionsUrl(string id)
    {
        if (!folderOf.TryGetValue(id, out var folder)) return null;
        try
        {
            using var d = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json"))); var r = d.RootElement;
            string p = r.TryGetProperty("options_page", out var op) ? op.GetString() : r.TryGetProperty("options_ui", out var ou) && ou.TryGetProperty("page", out var pg) ? pg.GetString() : null;
            return p == null ? null : "chrome-extension://" + id + "/" + p.TrimStart('/');
        }
        catch { return null; }
    }

    public static async Task<bool> EnableAsync(string id, bool on)
    {
        var x = (await Profile.GetBrowserExtensionsAsync()).FirstOrDefault(e => e.Id == id); if (x == null) return false;
        await x.EnableAsync(on); Host.Log($"extension {x.Name} : {(on ? "activée" : "désactivée")}"); return true;
    }
    public static async Task<bool> RemoveAsync(string id)
    {
        var x = (await Profile.GetBrowserExtensionsAsync()).FirstOrDefault(e => e.Id == id); if (x == null) return false;
        await x.RemoveAsync();
        if (folderOf.Remove(id, out var folder)) { SaveIndex(); try { if (folder.StartsWith(Dir, StringComparison.OrdinalIgnoreCase)) Directory.Delete(folder, true); } catch (Exception ex) { Host.Log("dossier de l'extension : " + ex.Message); } }
        Host.Log("extension retirée : " + x.Name); return true;
    }

    // Un dossier d'extension choisi par l'utilisateur : copié sous extensions\<nom>, puis chargé.
    public static async Task<string> AddFolderAsync(string src)
    {
        if (!File.Exists(Path.Combine(src, "manifest.json"))) return "Ce dossier n'a pas de manifest.json.";
        string name = Path.GetFileName(src.TrimEnd('\\', '/')); string dst = Path.Combine(Dir, name);
        if (!string.Equals(Path.GetFullPath(src).TrimEnd('\\'), Path.GetFullPath(dst).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) { if (Directory.Exists(dst)) Directory.Delete(dst, true); CopyDir(src, dst); }
        return await AddAsync(dst) != null ? null : "Le moteur a refusé cette extension (voir le journal).";
    }
    static void CopyDir(string s, string d)
    {
        Directory.CreateDirectory(d);
        foreach (var f in Directory.GetFiles(s)) File.Copy(f, Path.Combine(d, Path.GetFileName(f)), true);
        foreach (var x in Directory.GetDirectories(s)) CopyDir(x, Path.Combine(d, Path.GetFileName(x)));
    }

    // L'antipub uBlock Origin Lite : la dernière version publiée (GitHub), ou l'archive zip donnée ; téléchargée, dépliée sous extensions\uBOLite, chargée.
    public static async Task<string> InstallZipAsync(string url, string name, Action<string> progress)
    {
        try
        {
            // Le nom devient un dossier sous extensions\ qui est d'abord effacé : jamais « .. », un chemin ou un nom réservé.
            if (name == null || !System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_-]{1,40}$")) return "Nom d'extension refusé.";
            // L'archive vient de GitHub (les publications d'uBOL), ou d'un serveur local (essais) ; rien d'autre.
            if (!string.IsNullOrEmpty(url) && !(Uri.TryCreate(url, UriKind.Absolute, out var src)
                    && ((src.Scheme == Uri.UriSchemeHttps && src.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) || (src.IsLoopback && (src.Scheme == Uri.UriSchemeHttp || src.Scheme == Uri.UriSchemeHttps)))))
                return "Adresse refusée : seules les archives publiées sur https://github.com sont acceptées.";
            if (http.DefaultRequestHeaders.UserAgent.Count == 0) http.DefaultRequestHeaders.UserAgent.ParseAdd("cdx-browser");   // une fois (il s'allongeait à chaque installation)
            if (string.IsNullOrEmpty(url))
            {
                progress("Recherche de la dernière version…");
                using var doc = JsonDocument.Parse(await http.GetStringAsync(UbolApi));
                foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
                    if (a.GetProperty("name").GetString() is { } n && n.EndsWith(".chromium.zip")) { url = a.GetProperty("browser_download_url").GetString(); break; }
                if (url == null) return "Aucune archive Chromium dans la dernière publication.";
            }
            progress("Téléchargement de " + url + "…");
            string zip = Path.Combine(Path.GetTempPath(), "cdx-ext-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
            await using (var s = await http.GetStreamAsync(url)) await using (var f = File.Create(zip)) await s.CopyToAsync(f);
            string dst = Path.GetFullPath(Path.Combine(Dir, name));
            if (!dst.StartsWith(Path.GetFullPath(Dir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return "Nom d'extension refusé.";
            progress("Installation…");
            // une version précédente est d'abord retirée du profil, puis le dossier remplacé
            foreach (var kv in folderOf.Where(k => string.Equals(k.Value, dst, StringComparison.OrdinalIgnoreCase)).ToList()) { try { await RemoveAsync(kv.Key); } catch { } }
            if (Directory.Exists(dst)) Directory.Delete(dst, true);
            Directory.CreateDirectory(dst);
            using (var z = ZipFile.OpenRead(zip))
            {
                // l'archive peut avoir ou non un dossier de tête ; le manifeste finit à la racine de dst
                string top = z.Entries.Any(e => e.FullName == "manifest.json") ? "" : z.Entries.FirstOrDefault(e => e.FullName.EndsWith("/manifest.json"))?.FullName.Replace("manifest.json", "") ?? "";
                foreach (var e in z.Entries)
                {
                    if (!e.FullName.StartsWith(top) || e.FullName.EndsWith("/")) continue;
                    string rel = e.FullName[top.Length..]; string full = Path.GetFullPath(Path.Combine(dst, rel));
                    if (!full.StartsWith(dst + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;   // chemin qui sort du dossier : ignoré
                    Directory.CreateDirectory(Path.GetDirectoryName(full)); e.ExtractToFile(full, true);
                }
            }
            File.Delete(zip);
            if (!File.Exists(Path.Combine(dst, "manifest.json"))) return "L'archive ne contient pas d'extension (manifest.json).";
            var x = await AddAsync(dst);
            return x == null ? "Le moteur a refusé cette extension (voir le journal)." : null;
        }
        catch (Exception ex) { Host.Log("installation d'extension : " + ex.Message); return "Échec : " + ex.Message; }
    }
}
