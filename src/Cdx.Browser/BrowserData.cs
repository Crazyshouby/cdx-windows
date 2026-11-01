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
    static List<T> Load<T>(string n) { try { return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(F(n))) ?? new(); } catch { return new(); } }
    static void Save<T>(string n, List<T> l) { try { File.WriteAllText(F(n), JsonSerializer.Serialize(l)); } catch (Exception ex) { Host.Log("sauvegarde " + n + " : " + ex.Message); } }

    public static readonly List<Bookmark> Bookmarks = Load<Bookmark>("favoris.json");
    public static readonly List<HistoryEntry> History = Load<HistoryEntry>("historique.json");
    public static readonly List<DownloadItem> Downloads = new();
    static int dlSeq;

    public static void SaveBookmarks() => Save("favoris.json", Bookmarks);
    public static void SaveHistory() { if (History.Count > 5000) History.RemoveRange(0, History.Count - 5000); Save("historique.json", History); }
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
        SaveHistory();
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
    public void Save() { try { File.WriteAllText(F, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); } catch (Exception ex) { Host.Log("réglages : " + ex.Message); } }
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
    public static readonly List<Credential> All = Load();

    [StructLayout(LayoutKind.Sequential)] struct DATA_BLOB { public int cbData; public IntPtr pbData; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool CryptProtectData(ref DATA_BLOB i, string descr, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB o);
    [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptUnprotectData(ref DATA_BLOB i, IntPtr descr, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB o);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);

    static byte[] Dpapi(byte[] data, bool protect)
    {
        var inb = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        try
        {
            Marshal.Copy(data, 0, inb.pbData, data.Length);
            bool ok = protect ? CryptProtectData(ref inb, "CDX navigateur", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var o) : CryptUnprotectData(ref inb, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out o);
            if (!ok) throw new InvalidOperationException("DPAPI : " + Marshal.GetLastWin32Error());
            try { var r = new byte[o.cbData]; Marshal.Copy(o.pbData, r, 0, o.cbData); return r; } finally { LocalFree(o.pbData); }
        }
        finally { Marshal.FreeHGlobal(inb.pbData); }
    }

    static List<Credential> Load()
    {
        try { if (!File.Exists(F)) return new(); return JsonSerializer.Deserialize<List<Credential>>(Encoding.UTF8.GetString(Dpapi(File.ReadAllBytes(F), false))) ?? new(); }
        catch (Exception ex) { Host.Log("mots de passe illisibles : " + ex.Message); return new(); }
    }
    public static void Save()
    {
        try { if (All.Count == 0) { File.Delete(F); return; } File.WriteAllBytes(F, Dpapi(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(All)), true)); }
        catch (Exception ex) { Host.Log("mots de passe : " + ex.Message); }
    }

    static string HostOf(string url) { try { return new Uri(url).Host.ToLowerInvariant(); } catch { return ""; } }
    // Les identifiants valables pour une page : même hôte, ou l'hôte enregistré est un domaine parent (accounts.exemple.fr ⊂ exemple.fr).
    public static List<Credential> For(string url)
    {
        string h = HostOf(url); if (h.Length == 0) return new();
        return All.Where(c => { string ch = HostOf(c.Url); return ch.Length > 0 && (h == ch || h.EndsWith("." + ch) || ch.EndsWith("." + h)); }).ToList();
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

    // Le script posé dans une page de connexion : remplit le premier champ « mot de passe » et le champ de texte qui le précède, puis réessaie quelques secondes
    // (formulaires construits après coup). Les valeurs sont passées en JSON (jamais concaténées dans du code).
    public static string FillScript(Credential c) => "(() => { const c = " + JsonSerializer.Serialize(new { user = c.User, password = c.Password }) + @";
const set = (el, v) => { const d = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value'); d.set.call(el, v); el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true })); };
const fill = () => { const pw = [...document.querySelectorAll('input[type=password]')].find(e => e.offsetParent !== null); if (!pw || pw.dataset.cdxFilled) return !!pw;
  const all = [...document.querySelectorAll('input')]; let u = null; for (let i = all.indexOf(pw) - 1; i >= 0; i--) { const t = (all[i].type || 'text').toLowerCase(); if (['text', 'email', 'tel'].includes(t) && all[i].offsetParent !== null) { u = all[i]; break; } }
  if (u && !u.value) set(u, c.user); if (!pw.value) set(pw, c.password); pw.dataset.cdxFilled = '1'; return true; };
if (!fill()) { let n = 0; const t = setInterval(() => { if (fill() || ++n > 16) clearInterval(t); }, 500); } })();";
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
        try { string tmp = F + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(s)); File.Move(tmp, F, true); }
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
    static void SaveIndex() { try { File.WriteAllText(IndexFile, JsonSerializer.Serialize(folderOf)); } catch { } }

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
            http.DefaultRequestHeaders.UserAgent.ParseAdd("cdx-browser");
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
            string dst = Path.Combine(Dir, name);
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
