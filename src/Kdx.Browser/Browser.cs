using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Kdx;

// ---- données du navigateur : favoris, historique, téléchargements ----
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
}

class BrowserTab
{
    public int Id; public WebView2 Web; public string Title = "Nouvel onglet", Url = "", Favicon = "", Status = "";
    public bool Loading;
}

// Le navigateur : la page ui/navigateur.html dessine tout le cadre (titre, onglets, barre d'adresse, barre d'état) et réserve un rectangle ;
// chaque onglet est un WebView2 posé par-dessus ce rectangle. Les pages internes (kdx://) sont des pages de la charte dans le même moteur.
class BrowserForm : HostForm
{
    const string Ui = "https://kdx.shell/browser/";
    const string SearchUrl = "https://www.google.com/search?q=";
    static readonly double[] Zooms = { 0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5 };

    protected override bool ToolWindow => false;
    readonly List<BrowserTab> tabs = new();
    BrowserTab active; int nextTab = 1;
    Rectangle view = new Rectangle(8, 120, 800, 400);
    bool fullscreen; Rectangle savedBounds; FormWindowState savedState;
    bool findOpen;
    readonly BrowserApp sh;

    public BrowserForm(BrowserApp s) : base(s, "navigateur")
    {
        sh = s; ShowInTaskbar = true; MinimumSize = new Size(800, 500); Text = "Navigateur"; Icon = SystemIcons.Application;
    }

    // ---------- adresses ----------
    static string Internal(string p) => Ui + "interne.html?p=" + p;
    static bool IsInternalUrl(string u) => u != null && u.StartsWith(Ui + "interne.html");
    static string InternalName(string u) { var m = Regex.Match(u ?? "", @"[?&]p=([a-z]+)"); return m.Success ? m.Groups[1].Value : "newtab"; }
    static string DisplayUrl(string u) => IsInternalUrl(u) ? (InternalName(u) == "newtab" ? "" : "kdx://" + InternalName(u)) : (u == "about:blank" ? "" : u ?? "");

    // Ce qu'on tape dans la barre d'adresse : une adresse complète, un nom de domaine, une page interne, ou une recherche.
    public static string Resolve(string input)
    {
        input = (input ?? "").Trim();
        if (input.Length == 0) return Internal("newtab");
        if (input.StartsWith("kdx://", StringComparison.OrdinalIgnoreCase)) return Internal(input[6..].Trim('/'));
        if (Regex.IsMatch(input, @"^[a-zA-Z][a-zA-Z0-9+.\-]*://") || input.StartsWith("about:") || input.StartsWith("data:")) return input;
        if (!input.Contains(' ') && (Regex.IsMatch(input, @"^(localhost|\d{1,3}(\.\d{1,3}){3})(:\d+)?(/.*)?$") || Regex.IsMatch(input, @"^[^\s/]+\.[a-zA-Z]{2,}(:\d+)?([/?#].*)?$"))) return "https://" + input;
        return SearchUrl + Uri.EscapeDataString(input);
    }

    // ---------- onglets ----------
    // L'accent de la suite a changé : le cadre et les pages internes (kdx://) se recolorent.
    public void SetTheme(string accent)
    {
        var msg = new { type = "theme", value = accent };
        Send(msg);
        foreach (var t in tabs.ToArray())
            if (IsInternalUrl(t.Url) && t.Web.CoreWebView2 != null) t.Web.CoreWebView2.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(msg));
    }

    public async Task<BrowserTab> NewTabAsync(string url, bool activate = true)
    {
        var t = new BrowserTab { Id = nextTab++, Web = new WebView2 { DefaultBackgroundColor = Color.FromArgb(0x22, 0x22, 0x22), Visible = false, Bounds = view } };
        tabs.Add(t); Controls.Add(t.Web); t.Web.BringToFront();
        await t.Web.EnsureCoreWebView2Async(sh.BrowserEnv);
        Wire(t);
        await LoadExtensionsOnce(t.Web.CoreWebView2);
        if (activate) Select(t);
        if (url != null) t.Web.CoreWebView2.Navigate(Resolve(url));
        return t;
    }

    static bool extensionsLoaded;
    static async Task LoadExtensionsOnce(CoreWebView2 c)
    {
        if (extensionsLoaded) return; extensionsLoaded = true;
        string dir = Path.Combine(Host.DataDir, "extensions"); Directory.CreateDirectory(dir);
        foreach (var d in Directory.GetDirectories(dir).Where(x => !Path.GetFileName(x).StartsWith("_") && File.Exists(Path.Combine(x, "manifest.json"))))
        {
            try { var x = await c.Profile.AddBrowserExtensionAsync(d); Host.Log($"extension chargée : {Path.GetFileName(d)} ({x.Id})"); }
            catch (Exception ex) { Host.Log($"extension {Path.GetFileName(d)} refusée : {ex.Message}"); }
        }
    }

    // Les pages web sont des vues natives posées sur la page du cadre : un menu qui descend de la barre de titre passerait dessous. Elles sont masquées le temps du menu.
    bool overlayOn;
    public void SetOverlay(bool on) { overlayOn = on; if (active != null) active.Web.Visible = !on; }

    void Select(BrowserTab t)
    {
        active = t;
        foreach (var x in tabs) x.Web.Visible = x == t && !overlayOn;
        t.Web.Bounds = fullscreen ? ClientRectangle : view; t.Web.BringToFront();
        Push();
        if (IsBlank(t)) Send(new { type = "focusAddr" });
    }

    static bool IsBlank(BrowserTab t) => string.IsNullOrEmpty(t.Url) || t.Url == "about:blank" || (IsInternalUrl(t.Url) && InternalName(t.Url) == "newtab");

    void CloseTab(BrowserTab t)
    {
        int i = tabs.IndexOf(t); if (i < 0) return;
        tabs.RemoveAt(i); Controls.Remove(t.Web);
        try { t.Web.Dispose(); } catch { }
        if (tabs.Count == 0) { Close(); return; }
        if (active == t) Select(tabs[Math.Min(i, tabs.Count - 1)]); else Push();
    }

    public async Task RestoreAsync(string url)
    {
        var first = await NewTabAsync(url ?? null, true);
        if (url == null)
        {
            // session précédente : les adresses des onglets ouverts à la fermeture
            string f = Path.Combine(Host.DataDir, "session.json"); List<string> urls = null;
            try { urls = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(f)); } catch { }
            if (urls != null && urls.Count > 0)
            {
                first.Web.CoreWebView2.Navigate(urls[0]);
                foreach (var u in urls.Skip(1)) await NewTabAsync(u, false);
                Select(first);
            }
            else first.Web.CoreWebView2.Navigate(Internal("newtab"));
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try { File.WriteAllText(Path.Combine(Host.DataDir, "session.json"), JsonSerializer.Serialize(tabs.Where(t => !IsBlank(t) && !string.IsNullOrEmpty(t.Url)).Select(t => t.Url).ToList())); } catch { }
        base.OnFormClosing(e);
    }

    // ---------- événements d'un onglet ----------
    void Wire(BrowserTab t)
    {
        var c = t.Web.CoreWebView2;
        c.SetVirtualHostNameToFolderMapping("kdx.shell", sh.Root, CoreWebView2HostResourceAccessKind.DenyCors);   // les pages internes (kdx://) sont des fichiers de la charte
        c.Settings.IsZoomControlEnabled = false; c.Settings.AreBrowserAcceleratorKeysEnabled = false; c.Settings.IsStatusBarEnabled = false;
        c.DocumentTitleChanged += (s, e) => { t.Title = string.IsNullOrWhiteSpace(c.DocumentTitle) ? DisplayUrl(c.Source) : c.DocumentTitle; if (t.Title == "") t.Title = "Nouvel onglet"; Push(); };
        c.SourceChanged += (s, e) => { t.Url = c.Source; Push(); };
        c.HistoryChanged += (s, e) => Push();
        c.NavigationStarting += (s, e) => { t.Loading = true; t.Status = ""; Push(); };
        c.NavigationCompleted += (s, e) =>
        {
            t.Loading = false; t.Url = c.Source;
            if (e.IsSuccess && !IsInternalUrl(c.Source) && c.Source.StartsWith("http")) BrowserData.AddHistory(c.Source, c.DocumentTitle);
            Push();
        };
        c.FaviconChanged += async (s, e) =>
        {
            try
            {
                if (string.IsNullOrEmpty(c.FaviconUri)) { t.Favicon = ""; Push(); return; }
                using var st = await c.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png); using var ms = new MemoryStream(); await st.CopyToAsync(ms);
                t.Favicon = Convert.ToBase64String(ms.ToArray()); Push();
            }
            catch { }
        };
        c.StatusBarTextChanged += (s, e) => { t.Status = c.StatusBarText ?? ""; if (t == active) Send(new { type = "status", text = t.Status }); };
        c.WindowCloseRequested += (s, e) => CloseTab(t);
        c.NewWindowRequested += async (s, e) =>
        {
            var d = e.GetDeferral();
            try { var nt = await NewTabAsync(null, true); e.NewWindow = nt.Web.CoreWebView2; e.Handled = true; }   // le nouvel onglet garde le lien « opener » : les fenêtres de connexion fonctionnent
            catch (Exception ex) { Host.Log("nouvel onglet : " + ex.Message); }
            finally { d.Complete(); }
        };
        c.DownloadStarting += (s, e) => StartDownload(e);
        c.ContainsFullScreenElementChanged += (s, e) => SetFullscreen(c.ContainsFullScreenElement);
        c.WebMessageReceived += (s, e) => FromInternalPage(t, e);
        // Le contrôle WinForms garde son contrôleur dans un champ privé (aucun accesseur public) : on le lit pour capter les raccourcis clavier des pages.
        var ctl = typeof(WebView2).GetField("_coreWebView2Controller", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(t.Web) as CoreWebView2Controller;
        if (ctl == null) Host.Log("raccourcis des pages : contrôleur WebView2 introuvable");
        else ctl.AcceleratorKeyPressed += (s, e) =>
        {
            if ((e.KeyEventKind == CoreWebView2KeyEventKind.KeyDown || e.KeyEventKind == CoreWebView2KeyEventKind.SystemKeyDown) && Shortcut(e.VirtualKey)) e.Handled = true;
        };
        c.Find.ActiveMatchIndexChanged += (s, e) => SendFind(c);
        c.Find.MatchCountChanged += (s, e) => SendFind(c);
    }

    CoreWebView2FindOptions MakeFind(string term)
    {
        var o = sh.BrowserEnv.CreateFindOptions(); o.FindTerm = term; o.ShouldHighlightAllMatches = true; o.SuppressDefaultFindDialog = true; return o;
    }

    void SendFind(CoreWebView2 c) { if (active != null && active.Web.CoreWebView2 == c) Send(new { type = "find", index = c.Find.ActiveMatchIndex, count = c.Find.MatchCount }); }

    // ---------- raccourcis clavier (page web au premier plan) ----------
    bool Shortcut(uint vk)
    {
        bool ctrl = (ModifierKeys & Keys.Control) != 0, alt = (ModifierKeys & Keys.Alt) != 0, shift = (ModifierKeys & Keys.Shift) != 0;
        if (ctrl && !alt)
        {
            switch (vk)
            {
                case 0x54: _ = NewTabAsync(Internal("newtab"), true); return true;   // Ctrl+T
                case 0x57: if (active != null) CloseTab(active); return true;                                                                  // Ctrl+W
                case 0x4C: Send(new { type = "focusAddr" }); return true;                                                                       // Ctrl+L
                case 0x52: active?.Web.CoreWebView2.Reload(); return true;                                                                      // Ctrl+R
                case 0x46: OpenFind(); return true;                                                                                             // Ctrl+F
                case 0x44: ToggleStar(); return true;                                                                                           // Ctrl+D
                case 0x48: OpenInternal("historique"); return true;                                                                             // Ctrl+H
                case 0x4A: OpenInternal("telechargements"); return true;                                                                        // Ctrl+J
                case 0x09: CycleTab(shift ? -1 : 1); return true;                                                                               // Ctrl+Tab
                case 0xBB: case 0x6B: Zoom(+1); return true;                                                                                    // Ctrl +
                case 0xBD: case 0x6D: Zoom(-1); return true;                                                                                    // Ctrl -
                case 0x30: case 0x60: Zoom(0); return true;                                                                                     // Ctrl 0
            }
            if (vk >= 0x31 && vk <= 0x39) { int n = (int)(vk - 0x31); if (n < tabs.Count) Select(vk == 0x39 ? tabs[^1] : tabs[n]); return true; }
        }
        if (alt && !ctrl) { if (vk == 0x25) { if (active?.Web.CoreWebView2.CanGoBack == true) active.Web.CoreWebView2.GoBack(); return true; } if (vk == 0x27) { if (active?.Web.CoreWebView2.CanGoForward == true) active.Web.CoreWebView2.GoForward(); return true; } }
        switch (vk)
        {
            case 0x74: active?.Web.CoreWebView2.Reload(); return true;   // F5
            case 0x7A: SetFullscreen(!fullscreen); return true;           // F11
            case 0x7B: active?.Web.CoreWebView2.OpenDevToolsWindow(); return true;   // F12
            case 0x1B: if (findOpen) { CloseFind(); return true; } break;
        }
        return false;
    }

    void CycleTab(int d) { if (tabs.Count < 2) return; Select(tabs[(tabs.IndexOf(active) + d + tabs.Count) % tabs.Count]); }

    void Zoom(int dir)
    {
        if (active == null) return;
        double z = active.Web.ZoomFactor; int i = Array.FindIndex(Zooms, x => Math.Abs(x - z) < 0.01); if (i < 0) i = Array.IndexOf(Zooms, 1.0);
        active.Web.ZoomFactor = dir == 0 ? 1 : Zooms[Math.Clamp(i + dir, 0, Zooms.Length - 1)];
        Push();
    }

    void OpenFind() { findOpen = true; Send(new { type = "openFind" }); }
    void CloseFind() { findOpen = false; try { active?.Web.CoreWebView2.Find.Stop(); } catch { } Send(new { type = "closeFind" }); active?.Web.Focus(); }

    void ToggleStar() { if (active == null || IsBlank(active)) return; BrowserData.ToggleStar(active.Url, active.Title); Push(); }

    public void OpenInternal(string page)
    {
        var existing = tabs.FirstOrDefault(x => IsInternalUrl(x.Url) && InternalName(x.Url) == page);
        if (existing != null) { Select(existing); return; }
        _ = NewTabAsync(Internal(page), true);
    }

    // ---------- plein écran (vidéo, F11) ----------
    void SetFullscreen(bool on)
    {
        if (on == fullscreen) return;
        fullscreen = on;
        if (on)
        {
            savedBounds = Bounds; savedState = WindowState; WindowState = FormWindowState.Normal;
            Bounds = Screen.FromControl(this).Bounds; TopMost = true; Web.Visible = false;
            if (active != null) active.Web.Bounds = ClientRectangle;
        }
        else
        {
            TopMost = false; Web.Visible = true; Bounds = savedBounds; WindowState = savedState;
            if (active != null) active.Web.Bounds = view;
        }
    }

    // ---------- téléchargements ----------
    void StartDownload(CoreWebView2DownloadStartingEventArgs e)
    {
        e.Handled = true;   // pas la fenêtre de téléchargement d'Edge : la page « kdx://telechargements » les affiche
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"); Directory.CreateDirectory(dir);
        string name = Path.GetFileName(e.ResultFilePath); if (string.IsNullOrEmpty(name)) name = "telechargement";
        string path = Path.Combine(dir, name);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
        e.ResultFilePath = path;
        var op = e.DownloadOperation;
        var d = new DownloadItem { Id = BrowserData.NextDownloadId(), Name = Path.GetFileName(path), Path = path, Url = op.Uri, Total = op.TotalBytesToReceive.HasValue ? (long)op.TotalBytesToReceive.Value : -1, Op = op };
        BrowserData.Downloads.Insert(0, d);
        op.BytesReceivedChanged += (s, a) =>
        {
            d.Received = op.BytesReceived; d.Total = op.TotalBytesToReceive.HasValue ? (long)op.TotalBytesToReceive.Value : -1;
            double dt = (DateTime.UtcNow - d.Tick).TotalSeconds;
            if (dt >= 0.5) { double inst = (d.Received - d.TickBytes) / dt; d.Speed = d.Speed <= 0 ? inst : d.Speed * 0.7 + inst * 0.3; d.Tick = DateTime.UtcNow; d.TickBytes = d.Received; }
            PushDownloads(); Xfer.Publish(BrowserData.XferOf(d));
        };
        op.StateChanged += (s, a) =>
        {
            var why = op.State == CoreWebView2DownloadState.Interrupted ? op.InterruptReason : CoreWebView2DownloadInterruptReason.None;
            d.Paused = why == CoreWebView2DownloadInterruptReason.UserPaused; d.Canceled = why == CoreWebView2DownloadInterruptReason.UserCanceled;
            d.State = op.State == CoreWebView2DownloadState.Completed ? "done" : op.State == CoreWebView2DownloadState.Interrupted ? (d.Paused ? "progress" : "error") : "progress";
            if (d.State != "progress") d.Speed = 0;
            bool finished = d.State != "progress" && !d.Canceled && !d.Announced; if (finished) d.Announced = true;
            if (d.State == "done") { try { d.Received = new FileInfo(d.Path).Length; if (d.Total < 0) d.Total = d.Received; } catch { } }   // un petit fichier n'émet parfois aucun avis de progression
            PushDownloads(); Xfer.Publish(BrowserData.XferOf(d));
            // La barre du shell garde la trace (SUITE.md §3.1) : « Téléchargement terminé » ou « échoué ».
            if (finished) { string nm = d.Name, st = d.State; _ = Task.Run(() => Bus.Send("shell", "notify", new { title = st == "done" ? "Téléchargement terminé" : "Téléchargement échoué", text = nm, level = st == "done" ? "info" : "warn" }, "browser")); }
        };
        Host.Log("téléchargement : " + path);
        PushDownloads();
    }

    void PushDownloads() { Send(new { type = "downloads", active = BrowserData.Downloads.Count(x => x.State == "progress"), total = BrowserData.Downloads.Count }); if (active?.Web.CoreWebView2 != null && IsInternalUrl(active.Url)) active.Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "downloads", items = BrowserData.Downloads.Select(x => x.Info()) })); }

    // Les pages internes lisent des noms en minuscules (title, url, time...) : on projette les données plutôt que de sérialiser les classes telles quelles.
    static object DataPayload() => new
    {
        type = "data",
        bookmarks = BrowserData.Bookmarks.OrderByDescending(b => b.Added).Select(b => new { title = b.Title, url = b.Url, added = b.Added }),
        history = BrowserData.History.AsEnumerable().Reverse().Take(1000).Select(h => new { title = h.Title, url = h.Url, time = h.Time }),
        downloads = BrowserData.Downloads.Select(x => x.Info())
    };

    // ---------- pages internes (kdx://) : seules les pages de la charte sont écoutées ----------
    void FromInternalPage(BrowserTab t, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsInternalUrl(e.Source)) return;   // une page web quelconque ne commande rien
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson); var r = doc.RootElement;
            string cmd = r.GetProperty("cmd").GetString();
            string Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            void Reply(object o) => t.Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(o));
            switch (cmd)
            {
                case "in.data": Reply(DataPayload()); break;
                case "in.navigate": t.Web.CoreWebView2.Navigate(Resolve(Str("url"))); break;
                case "in.bookmark.remove": { var b = BrowserData.Bookmarks.FirstOrDefault(x => x.Url == Str("url")); if (b != null) { BrowserData.Bookmarks.Remove(b); BrowserData.SaveBookmarks(); } Reply(DataPayload()); Push(); break; }
                case "in.history.clear": BrowserData.History.Clear(); BrowserData.SaveHistory(); Reply(DataPayload()); break;
                case "in.dl.open": { var d = BrowserData.Downloads.FirstOrDefault(x => x.Id == r.GetProperty("id").GetInt32()); if (d != null && File.Exists(d.Path)) Process.Start(new ProcessStartInfo(d.Path) { UseShellExecute = true }); break; }
                case "in.dl.reveal": { var d = BrowserData.Downloads.FirstOrDefault(x => x.Id == r.GetProperty("id").GetInt32()); if (d != null) { string rp = d.Path; _ = Task.Run(() => { if (!Bus.Send("explorer", "select", new { path = rp }, "browser", launch: true)) Process.Start("explorer.exe", "/select,\"" + rp + "\""); }); } break; }
                case "in.dl.cancel": { var d = BrowserData.Downloads.FirstOrDefault(x => x.Id == r.GetProperty("id").GetInt32()); d?.Op?.Cancel(); break; }
                case "in.dl.clear": BrowserData.Downloads.RemoveAll(x => x.State != "progress"); PushDownloads(); break;
            }
        }
        catch (Exception ex) { Host.Log("page interne : " + ex.Message); }
    }

    // ---------- commandes venues de la page ui/navigateur.html ----------
    public void Command(string cmd, JsonElement r)
    {
        string Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int Int(string k) => r.TryGetProperty(k, out var v) && v.TryGetInt32(out var i) ? i : 0;
        var c = active?.Web.CoreWebView2;
        switch (cmd)
        {
            case "br.ready": Push(); PushDownloads(); break;
            case "br.layout":
                view = new Rectangle(Int("x"), Int("y"), Math.Max(50, Int("w")), Math.Max(50, Int("h")));
                if (!fullscreen) foreach (var t in tabs) t.Web.Bounds = view;
                break;
            case "br.newtab": _ = NewTabAsync(Str("url") ?? Internal("newtab"), true); break;
            case "br.close": { var t = tabs.FirstOrDefault(x => x.Id == Int("id")); if (t != null) CloseTab(t); break; }
            case "br.select": { var t = tabs.FirstOrDefault(x => x.Id == Int("id")); if (t != null) Select(t); break; }
            case "br.go": if (c != null) { c.Navigate(Resolve(Str("url"))); active.Web.Focus(); } break;
            case "br.back": if (c?.CanGoBack == true) c.GoBack(); break;
            case "br.fwd": if (c?.CanGoForward == true) c.GoForward(); break;
            case "br.reload": c?.Reload(); break;
            case "br.stop": c?.Stop(); break;
            case "br.star": ToggleStar(); break;
            case "br.zoom": Zoom(Int("d")); break;
            case "br.devtools": c?.OpenDevToolsWindow(); break;
            case "br.fullscreen": SetFullscreen(!fullscreen); break;
            case "br.internal": OpenInternal(Str("p")); break;
            case "in.xfers": case "br.xfers": Xfer.Show(); break;
            case "br.focus": active?.Web.Focus(); break;
            case "br.findOpen": findOpen = true; break;
            case "br.findClose": CloseFind(); break;
            case "br.find":
                if (c != null)
                {
                    string term = Str("text") ?? ""; bool back = Int("dir") < 0, first = Int("first") == 1;
                    if (term.Length == 0) { c.Find.Stop(); Send(new { type = "find", index = 0, count = 0 }); break; }
                    if (first) _ = c.Find.StartAsync(MakeFind(term));
                    else if (back) c.Find.FindPrevious(); else c.Find.FindNext();
                }
                break;
            case "br.shortcut": Shortcut((uint)Int("vk")); break;
        }
    }

    // Pour les tests (canal de contrôle) : l'état des onglets, et l'évaluation d'un script dans la page active ou dans le cadre.
    public string StateJson() => JsonSerializer.Serialize(new { active = active?.Id, tabs = tabs.Select(t => new { t.Id, t.Title, Url = t.Url, t.Loading, back = t.Web.CoreWebView2?.CanGoBack, zoom = t.Web.ZoomFactor, starred = BrowserData.IsStarred(t.Url), favicon = t.Favicon.Length > 0 }), downloads = BrowserData.Downloads.Select(d => d.Info()) });
    public async Task<string> EvalAsync(string js, bool ui) => await (ui ? Web.CoreWebView2 : active.Web.CoreWebView2).ExecuteScriptAsync(js);

    void Push()
    {
        var items = tabs.Select(t =>
        {
            var c = t.Web.CoreWebView2;
            return new
            {
                id = t.Id, title = t.Title, url = DisplayUrl(t.Url), loading = t.Loading, favicon = t.Favicon,
                canBack = c?.CanGoBack ?? false, canFwd = c?.CanGoForward ?? false, zoom = (int)Math.Round(t.Web.ZoomFactor * 100),
                starred = BrowserData.IsStarred(t.Url), internalPage = IsInternalUrl(t.Url)
            };
        }).ToList();
        Send(new { type = "tabs", active = active?.Id ?? 0, items });
        if (active != null) Text = active.Title + " — Navigateur";
    }
}
