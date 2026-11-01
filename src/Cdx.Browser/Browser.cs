using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Cdx;

class BrowserTab
{
    public int Id; public WebView2 Web; public string Title = "Nouvel onglet", Url = "", Favicon = "", Status = "";
    public bool Loading; public int Group;   // 0 : hors groupe
    public CoreWebView2Controller Ctl;
    public string Filled;                    // adresse où nos identifiants ont été posés (une seule fois par page)
}
class TabGroup { public int Id; public string Name = ""; public string Color = "accent"; public bool Collapsed; }

// Le navigateur : la page ui/navigateur.html dessine tout le cadre (titre, onglets, barre d'adresse, barre d'état) et réserve un ou deux rectangles (pages
// scindées) ; chaque onglet est un WebView2 posé par-dessus. Les pages internes (cdx://) sont des pages de la charte dans le même moteur.
// Plusieurs fenêtres : BrowserApp les tient ; favoris, historique, réglages, mots de passe et extensions sont communs (BrowserData.cs).
class BrowserForm : HostForm
{
    const string Ui = "https://cdx.shell/browser/";
    static readonly double[] Zooms = { 0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5 };
    public static readonly string[] GroupColors = { "accent", "rouge", "ambre", "vert", "gris" };

    protected override bool ToolWindow => false;
    public readonly List<BrowserTab> Tabs = new();
    public readonly List<TabGroup> Groups = new();
    BrowserTab active; int nextTab = 1, nextGroup = 1;
    BrowserTab pane1, pane2;                 // pages scindées : les deux onglets affichés (pane2 null : une seule page) ; active est l'un des deux
    Rectangle view = new Rectangle(8, 120, 800, 400), view2 = Rectangle.Empty;
    bool fullscreen; Rectangle savedBounds; FormWindowState savedState;
    bool findOpen;
    readonly List<SessionTab> closed = new();   // les derniers onglets fermés (Ctrl+Maj+T)
    readonly BrowserApp sh;
    static BrowserSettings S => BrowserSettings.Current;

    public BrowserForm(BrowserApp s) : base(s, "navigateur")
    {
        sh = s; ShowInTaskbar = true; MinimumSize = new Size(800, 500); Text = "Navigateur"; Icon = SystemIcons.Application;
    }

    // ---------- adresses ----------
    static string Internal(string p) => Ui + "interne.html?p=" + p;
    static bool IsInternalUrl(string u) => u != null && u.StartsWith(Ui + "interne.html");
    static string InternalName(string u) { var m = Regex.Match(u ?? "", @"[?&]p=([a-z]+)"); return m.Success ? m.Groups[1].Value : "newtab"; }
    static string DisplayUrl(string u) => IsInternalUrl(u) ? (InternalName(u) == "newtab" ? "" : "cdx://" + InternalName(u)) : (u == "about:blank" ? "" : u ?? "");

    // Ce qu'on tape dans la barre d'adresse : une adresse complète, un nom de domaine, une page interne, ou une recherche (moteur des réglages).
    public static string Resolve(string input)
    {
        input = (input ?? "").Trim();
        if (input.Length == 0) return Internal("newtab");
        if (input.StartsWith("cdx://", StringComparison.OrdinalIgnoreCase)) return Internal(input[6..].Trim('/'));
        if (Regex.IsMatch(input, @"^[a-zA-Z][a-zA-Z0-9+.\-]*://") || input.StartsWith("about:") || input.StartsWith("data:")) return input;
        if (!input.Contains(' ') && (Regex.IsMatch(input, @"^(localhost|\d{1,3}(\.\d{1,3}){3})(:\d+)?(/.*)?$") || Regex.IsMatch(input, @"^[^\s/]+\.[a-zA-Z]{2,}(:\d+)?([/?#].*)?$"))) return "https://" + input;
        return S.SearchUrl + Uri.EscapeDataString(input);
    }

    // ---------- onglets ----------
    // L'accent de la suite a changé : le cadre et les pages internes (cdx://) se recolorent.
    public void SetTheme(string accent)
    {
        var msg = new { type = "theme", value = accent };
        Send(msg);
        foreach (var t in Tabs.ToArray())
            if (IsInternalUrl(t.Url) && t.Web.CoreWebView2 != null) t.Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msg));
    }

    public async Task<BrowserTab> NewTabAsync(string url, bool activate = true, int group = 0, int after = -1)
    {
        var t = new BrowserTab { Id = nextTab++, Web = new WebView2 { DefaultBackgroundColor = Color.FromArgb(0x22, 0x22, 0x22), Visible = false, Bounds = view }, Group = group };
        if (after >= 0 && after < Tabs.Count) Tabs.Insert(after + 1, t); else Tabs.Add(t);
        if (group != 0) PlaceInGroup(t);
        Controls.Add(t.Web); t.Web.BringToFront();
        await t.Web.EnsureCoreWebView2Async(sh.BrowserEnv);
        if (t.Web.IsDisposed || !Tabs.Contains(t)) return t;   // fermé pendant la création
        Wire(t);
        await Extensions.LoadAllAsync(t.Web.CoreWebView2.Profile);
        if (activate) Select(t); else Push();
        if (url != null) t.Web.CoreWebView2.Navigate(Resolve(url));
        return t;
    }

    // Les pages web sont des vues natives posées sur la page du cadre : un menu qui descend de la barre de titre passerait dessous. Elles sont masquées le temps du menu.
    bool overlayOn;
    public void SetOverlay(bool on) { overlayOn = on; Apply(); }

    // Pose les vues : l'onglet actif (et le second, scindé) visibles sur leurs rectangles, les autres cachées.
    void Apply()
    {
        foreach (var x in Tabs) x.Web.Visible = !overlayOn && (x == pane1 || x == pane2);
        if (pane1 != null) { pane1.Web.Bounds = fullscreen ? ClientRectangle : view; pane1.Web.BringToFront(); }
        if (pane2 != null) { pane2.Web.Bounds = fullscreen ? ClientRectangle : view2; pane2.Web.BringToFront(); if (fullscreen) pane2.Web.Visible = false; }
    }

    public void Select(BrowserTab t)
    {
        if (t == null || !Tabs.Contains(t)) return;
        var g = GroupOf(t); if (g != null && g.Collapsed) g.Collapsed = false;   // choisir un onglet d'un groupe replié le déplie
        if (pane2 != null && t != pane1 && t != pane2) { if (active == pane2) pane2 = t; else pane1 = t; }   // scindé : l'onglet choisi prend la place du volet actif
        else if (pane2 == null) pane1 = t;
        active = t; Apply(); Push();
        if (IsBlank(t)) Send(new { type = "focusAddr" });
    }
    public BrowserTab Active => active;

    static bool IsBlank(BrowserTab t) => string.IsNullOrEmpty(t.Url) || t.Url == "about:blank" || (IsInternalUrl(t.Url) && InternalName(t.Url) == "newtab");

    public void CloseTab(BrowserTab t)
    {
        int i = Tabs.IndexOf(t); if (i < 0) return;
        if (!IsBlank(t)) { closed.Add(new SessionTab { Url = t.Url, Title = t.Title, Group = t.Group }); if (closed.Count > 10) closed.RemoveAt(0); }
        Tabs.RemoveAt(i); Controls.Remove(t.Web);
        try { t.Web.Dispose(); } catch { }
        if (t == pane2) pane2 = null; else if (t == pane1) { pane1 = pane2; pane2 = null; }
        PruneGroups();
        if (Tabs.Count == 0) { Close(); return; }
        if (active == t) Select(pane1 ?? NearestVisible(i)); else { Apply(); Push(); }
    }
    BrowserTab NearestVisible(int i)
    {
        var vis = Tabs.Where(x => GroupOf(x)?.Collapsed != true).ToList(); if (vis.Count == 0) return Tabs[Math.Min(i, Tabs.Count - 1)];
        return vis.OrderBy(x => Math.Abs(Tabs.IndexOf(x) - i)).ThenBy(x => Tabs.IndexOf(x) < i ? 1 : 0).First();
    }
    public void ReopenClosed() { if (closed.Count == 0) return; var c = closed[^1]; closed.RemoveAt(closed.Count - 1); _ = NewTabAsync(c.Url, true, Groups.Any(g => g.Id == c.Group) ? c.Group : 0); }

    // Un onglet retiré de cette fenêtre (il part dans une autre) : la vue n'est pas détruite.
    public BrowserTab Detach(BrowserTab t)
    {
        int i = Tabs.IndexOf(t); if (i < 0) return null;
        Tabs.RemoveAt(i); Controls.Remove(t.Web); t.Group = 0; PruneGroups();
        if (t == pane2) pane2 = null; else if (t == pane1) { pane1 = pane2; pane2 = null; }
        if (Tabs.Count == 0) { Close(); return t; }
        if (active == t) Select(pane1 ?? NearestVisible(i)); else { Apply(); Push(); }
        return t;
    }
    // Un onglet venu d'une autre fenêtre : le contrôle WebView2 change de fenêtre parente (sa vue le suit : la page n'est pas rechargée).
    public void Attach(BrowserTab t)
    {
        t.Id = nextTab++; t.Group = 0; Tabs.Add(t);
        try { Controls.Add(t.Web); } catch (Exception ex) { Host.Log("onglet déplacé : " + ex.Message); }
        t.Web.Bounds = view; Select(t);
    }

    // ---------- groupes d'onglets ----------
    public TabGroup GroupOf(BrowserTab t) => t.Group == 0 ? null : Groups.FirstOrDefault(g => g.Id == t.Group);
    void PruneGroups() { Groups.RemoveAll(g => !Tabs.Any(t => t.Group == g.Id)); }
    // Les onglets d'un groupe restent côte à côte : l'onglet est déplacé juste après le dernier du groupe.
    void PlaceInGroup(BrowserTab t)
    {
        var last = Tabs.LastOrDefault(x => x != t && x.Group == t.Group); if (last == null) return;
        Tabs.Remove(t); Tabs.Insert(Tabs.IndexOf(last) + 1, t);
    }
    public TabGroup NewGroup(string name, string color, IEnumerable<BrowserTab> members)
    {
        var g = new TabGroup { Id = nextGroup++, Name = name ?? "", Color = GroupColors.Contains(color) ? color : "accent" };
        Groups.Add(g);
        foreach (var t in members.ToList()) { t.Group = g.Id; PlaceInGroup(t); }
        PruneGroups(); Push(); return g;
    }
    public void AddToGroup(BrowserTab t, int gid) { if (!Groups.Any(g => g.Id == gid)) return; t.Group = gid; PlaceInGroup(t); PruneGroups(); Push(); }
    public void Ungroup(BrowserTab t) { t.Group = 0; PruneGroups(); Push(); }
    public void GroupCommand(int gid, string what, string name, string color, bool on)
    {
        var g = Groups.FirstOrDefault(x => x.Id == gid); if (g == null) return;
        switch (what)
        {
            case "rename": g.Name = name ?? ""; break;
            case "color": if (GroupColors.Contains(color)) g.Color = color; break;
            case "collapse":
                g.Collapsed = on;
                if (on && active != null && active.Group == gid) { var other = Tabs.FirstOrDefault(t => t.Group != gid); if (other != null) Select(other); else g.Collapsed = false; }
                break;
            case "ungroup": foreach (var t in Tabs) if (t.Group == gid) t.Group = 0; PruneGroups(); break;
            case "close": foreach (var t in Tabs.Where(t => t.Group == gid).ToList()) CloseTab(t); return;
        }
        Push();
    }

    // ---------- pages scindées ----------
    public void Split(BrowserTab second)
    {
        if (active == null) return;
        if (second == null) { second = Tabs.FirstOrDefault(t => t != active && GroupOf(t)?.Collapsed != true); if (second == null) { _ = SplitNewAsync(); return; } }
        if (second == active) return;
        pane1 = active; pane2 = second; Apply(); Push();
    }
    async Task SplitNewAsync() { var t = await NewTabAsync(Internal("newtab"), false); if (Tabs.Contains(t)) { pane1 = active; pane2 = t; Apply(); Push(); } }
    public void Unsplit() { if (pane2 == null) return; pane2 = null; pane1 = active; Apply(); Push(); }
    public bool IsSplit => pane2 != null;

    // ---------- session ----------
    public SessionWindow Snapshot()
    {
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        return new SessionWindow
        {
            X = b.X, Y = b.Y, W = b.Width, H = b.Height, Max = WindowState == FormWindowState.Maximized, Active = Math.Max(0, Tabs.IndexOf(active)),
            Tabs = Tabs.Where(t => !IsBlank(t) || Tabs.Count == 1).Select(t => new SessionTab { Url = t.Url ?? "", Title = t.Title, Group = t.Group }).ToList(),
            Groups = Groups.Select(g => new SessionGroup { Id = g.Id, Name = g.Name, Color = g.Color, Collapsed = g.Collapsed }).ToList(),
        };
    }
    public async Task RestoreAsync(SessionWindow w, string url)
    {
        foreach (var g in w.Groups) { Groups.Add(new TabGroup { Id = g.Id, Name = g.Name, Color = g.Color, Collapsed = g.Collapsed }); nextGroup = Math.Max(nextGroup, g.Id + 1); }
        var urls = w.Tabs.Where(t => !string.IsNullOrEmpty(t.Url)).ToList();
        if (urls.Count == 0 && url == null) { await NewTabAsync(Internal("newtab"), true); return; }
        BrowserTab act = null;
        for (int i = 0; i < urls.Count; i++) { var t = await NewTabAsync(urls[i].Url, i == 0, Groups.Any(g => g.Id == urls[i].Group) ? urls[i].Group : 0); if (i == w.Active) act = t; }
        if (url != null) await NewTabAsync(url, true); else if (act != null) Select(act);
        PruneGroups(); Push();
    }

    bool closingHandled;
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!closingHandled) { closingHandled = true; sh.WindowClosing(this); }
        base.OnFormClosing(e);
    }

    // ---------- événements d'un onglet ----------
    void Wire(BrowserTab t)
    {
        var c = t.Web.CoreWebView2;
        c.SetVirtualHostNameToFolderMapping("cdx.shell", sh.Root, CoreWebView2HostResourceAccessKind.DenyCors);   // les pages internes (cdx://) sont des fichiers de la charte
        c.Settings.IsZoomControlEnabled = false; c.Settings.AreBrowserAcceleratorKeysEnabled = false; c.Settings.IsStatusBarEnabled = false;
        ApplySettings(t);
        c.DocumentTitleChanged += (s, e) => { t.Title = string.IsNullOrWhiteSpace(c.DocumentTitle) ? DisplayUrl(c.Source) : c.DocumentTitle; if (t.Title == "") t.Title = "Nouvel onglet"; Push(); };
        c.SourceChanged += (s, e) => { t.Url = c.Source; Push(); };
        c.HistoryChanged += (s, e) => Push();
        c.NavigationStarting += (s, e) => { t.Loading = true; t.Status = ""; Push(); };
        c.NavigationCompleted += (s, e) =>
        {
            t.Loading = false; t.Url = c.Source;
            if (e.IsSuccess && !IsInternalUrl(c.Source) && c.Source.StartsWith("http")) { BrowserData.AddHistory(c.Source, c.DocumentTitle); Autofill(t); }
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
            try { var nt = await NewTabAsync(null, true, t.Group, Tabs.IndexOf(t)); e.NewWindow = nt.Web.CoreWebView2; e.Handled = true; }   // le nouvel onglet garde le lien « opener » : les fenêtres de connexion fonctionnent
            catch (Exception ex) { Host.Log("nouvel onglet : " + ex.Message); }
            finally { d.Complete(); }
        };
        c.DownloadStarting += (s, e) => StartDownload(e);
        c.ContainsFullScreenElementChanged += (s, e) => SetFullscreen(c.ContainsFullScreenElement);
        c.WebMessageReceived += (s, e) => FromInternalPage(t, e);
        // Le contrôle WinForms garde son contrôleur dans un champ privé (aucun accesseur public) : on le lit pour capter les raccourcis clavier des pages, le focus et le changement de fenêtre.
        t.Ctl = typeof(WebView2).GetField("_coreWebView2Controller", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(t.Web) as CoreWebView2Controller;
        if (t.Ctl == null) Host.Log("raccourcis des pages : contrôleur WebView2 introuvable");
        else
        {
            t.Ctl.AcceleratorKeyPressed += (s, e) =>
            {
                if ((e.KeyEventKind == CoreWebView2KeyEventKind.KeyDown || e.KeyEventKind == CoreWebView2KeyEventKind.SystemKeyDown) && Shortcut(e.VirtualKey)) e.Handled = true;
            };
            t.Ctl.GotFocus += (s, e) => { if (pane2 != null && t != active && (t == pane1 || t == pane2)) { active = t; Push(); } };   // pages scindées : le volet cliqué devient l'actif
        }
        c.Find.ActiveMatchIndexChanged += (s, e) => SendFind(c);
        c.Find.MatchCountChanged += (s, e) => SendFind(c);
    }

    public void ApplySettings(BrowserTab t)
    {
        var st = t.Web.CoreWebView2?.Settings; if (st == null) return;
        try { st.IsPasswordAutosaveEnabled = S.SavePasswords; st.IsGeneralAutofillEnabled = S.Autofill; } catch (Exception ex) { Host.Log("réglages du moteur : " + ex.Message); }
    }
    public void ApplySettingsAll() { foreach (var t in Tabs) ApplySettings(t); }

    // Nos identifiants importés : posés une fois par adresse, sur les pages http(s) qui ont un champ mot de passe.
    void Autofill(BrowserTab t)
    {
        if (!S.Autofill || t.Filled == t.Url) return;
        var creds = Passwords.For(t.Url); if (creds.Count == 0) return;
        t.Filled = t.Url; _ = t.Web.CoreWebView2.ExecuteScriptAsync(Passwords.FillScript(creds[0]));
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
                case 0x54: if (shift) ReopenClosed(); else _ = NewTabAsync(Internal("newtab"), true); return true;   // Ctrl+T, Ctrl+Maj+T
                case 0x57: if (active != null) CloseTab(active); return true;                                     // Ctrl+W
                case 0x4E: sh.NewWindow(null); return true;                                                        // Ctrl+N
                case 0x4C: Send(new { type = "focusAddr" }); return true;                                          // Ctrl+L
                case 0x52: active?.Web.CoreWebView2.Reload(); return true;                                         // Ctrl+R
                case 0x46: OpenFind(); return true;                                                                // Ctrl+F
                case 0x44: ToggleStar(); return true;                                                              // Ctrl+D
                case 0x48: OpenInternal("historique"); return true;                                                // Ctrl+H
                case 0x4A: OpenInternal("telechargements"); return true;                                           // Ctrl+J
                case 0x09: CycleTab(shift ? -1 : 1); return true;                                                  // Ctrl+Tab
                case 0xDC: if (pane2 != null) Unsplit(); else Split(null); return true;                            // Ctrl+\ : scinder / réunir
                case 0xBB: case 0x6B: Zoom(+1); return true;                                                       // Ctrl +
                case 0xBD: case 0x6D: Zoom(-1); return true;                                                       // Ctrl -
                case 0x30: case 0x60: Zoom(0); return true;                                                        // Ctrl 0
            }
            if (vk >= 0x31 && vk <= 0x39) { int n = (int)(vk - 0x31); if (n < Tabs.Count) Select(vk == 0x39 ? Tabs[^1] : Tabs[n]); return true; }
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

    void CycleTab(int d)
    {
        var vis = Tabs.Where(t => GroupOf(t)?.Collapsed != true).ToList(); if (vis.Count < 2) return;
        int i = vis.IndexOf(active); Select(vis[((i < 0 ? 0 : i) + d + vis.Count) % vis.Count]);
    }

    void Zoom(int dir)
    {
        if (active == null) return;
        double z = active.Web.ZoomFactor; int i = Array.FindIndex(Zooms, x => Math.Abs(x - z) < 0.01); if (i < 0) i = Array.IndexOf(Zooms, 1.0);
        active.Web.ZoomFactor = dir == 0 ? 1 : Zooms[Math.Clamp(i + dir, 0, Zooms.Length - 1)];
        Push();
    }

    void OpenFind() { findOpen = true; Send(new { type = "openFind" }); }
    void CloseFind() { findOpen = false; try { active?.Web.CoreWebView2.Find.Stop(); } catch { } Send(new { type = "closeFind" }); active?.Web.Focus(); }

    void ToggleStar() { if (active == null || IsBlank(active)) return; BrowserData.ToggleStar(active.Url, active.Title); sh.PushAll(); }

    public void OpenInternal(string page)
    {
        var existing = Tabs.FirstOrDefault(x => IsInternalUrl(x.Url) && InternalName(x.Url) == page);
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
        }
        else { TopMost = false; Web.Visible = true; Bounds = savedBounds; WindowState = savedState; }
        Apply();
    }

    // ---------- téléchargements ----------
    void StartDownload(CoreWebView2DownloadStartingEventArgs e)
    {
        e.Handled = true;   // pas la fenêtre de téléchargement d'Edge : la page « cdx://telechargements » les affiche
        string dir = S.DownloadsFolder; try { Directory.CreateDirectory(dir); } catch { }
        string name = Path.GetFileName(e.ResultFilePath); if (string.IsNullOrEmpty(name)) name = "telechargement";
        string path = Path.Combine(dir, name);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
        if (S.AskWhere)
        {
            // « Demander où enregistrer » : la boîte de Windows, le téléchargement attend (deferral)
            var d = e.GetDeferral();
            BeginInvoke(new Action(() =>
            {
                try
                {
                    using var dlg = new SaveFileDialog { FileName = Path.GetFileName(path), InitialDirectory = dir, Title = "Enregistrer le téléchargement", OverwritePrompt = true };
                    if (dlg.ShowDialog(this) == DialogResult.OK) { e.ResultFilePath = dlg.FileName; Track(e); } else e.Cancel = true;
                }
                finally { d.Complete(); }
            }));
            return;
        }
        e.ResultFilePath = path; Track(e);
    }
    void Track(CoreWebView2DownloadStartingEventArgs e)
    {
        string path = e.ResultFilePath;
        var op = e.DownloadOperation;
        var d = new DownloadItem { Id = BrowserData.NextDownloadId(), Name = Path.GetFileName(path), Path = path, Url = op.Uri, Total = op.TotalBytesToReceive.HasValue ? (long)op.TotalBytesToReceive.Value : -1, Op = op };
        BrowserData.Downloads.Insert(0, d);
        op.BytesReceivedChanged += (s, a) =>
        {
            d.Received = op.BytesReceived; d.Total = op.TotalBytesToReceive.HasValue ? (long)op.TotalBytesToReceive.Value : -1;
            double dt = (DateTime.UtcNow - d.Tick).TotalSeconds;
            if (dt >= 0.5) { double inst = (d.Received - d.TickBytes) / dt; d.Speed = d.Speed <= 0 ? inst : d.Speed * 0.7 + inst * 0.3; d.Tick = DateTime.UtcNow; d.TickBytes = d.Received; }
            sh.PushDownloads(); Xfer.Publish(BrowserData.XferOf(d));
        };
        op.StateChanged += (s, a) =>
        {
            var why = op.State == CoreWebView2DownloadState.Interrupted ? op.InterruptReason : CoreWebView2DownloadInterruptReason.None;
            d.Paused = why == CoreWebView2DownloadInterruptReason.UserPaused; d.Canceled = why == CoreWebView2DownloadInterruptReason.UserCanceled;
            d.State = op.State == CoreWebView2DownloadState.Completed ? "done" : op.State == CoreWebView2DownloadState.Interrupted ? (d.Paused ? "progress" : "error") : "progress";
            if (d.State != "progress") d.Speed = 0;
            bool finished = d.State != "progress" && !d.Canceled && !d.Announced; if (finished) d.Announced = true;
            if (d.State == "done") { try { d.Received = new FileInfo(d.Path).Length; if (d.Total < 0) d.Total = d.Received; } catch { } }   // un petit fichier n'émet parfois aucun avis de progression
            sh.PushDownloads(); Xfer.Publish(BrowserData.XferOf(d));
            // La barre du shell garde la trace (SUITE.md §3.1) : « Téléchargement terminé » ou « échoué ».
            if (finished) { string nm = d.Name, st = d.State; _ = Task.Run(() => Bus.Send("shell", "notify", new { title = st == "done" ? "Téléchargement terminé" : "Téléchargement échoué", text = nm, level = st == "done" ? "info" : "warn" }, "browser")); }
        };
        Host.Log("téléchargement : " + path);
        sh.PushDownloads();
    }

    public void PushDownloads()
    {
        Send(new { type = "downloads", active = BrowserData.Downloads.Count(x => x.State == "progress"), total = BrowserData.Downloads.Count });
        foreach (var t in Tabs) if (t.Web.CoreWebView2 != null && IsInternalUrl(t.Url) && InternalName(t.Url) == "telechargements") t.Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "downloads", items = BrowserData.Downloads.Select(x => x.Info()) }));
    }

    // Les pages internes lisent des noms en minuscules (title, url, time...) : on projette les données plutôt que de sérialiser les classes telles quelles.
    static object DataPayload() => new
    {
        type = "data",
        bookmarks = BrowserData.Bookmarks.OrderByDescending(b => b.Added).Select(b => new { title = b.Title, url = b.Url, added = b.Added }),
        history = BrowserData.History.AsEnumerable().Reverse().Take(1000).Select(h => new { title = h.Title, url = h.Url, time = h.Time }),
        downloads = BrowserData.Downloads.Select(x => x.Info()),
        passwords = Passwords.All.Select(c => new { url = c.Url, user = c.User, name = c.Name }),   // jamais le mot de passe lui-même
        settings = S.Payload(), sources = BrowserData.ImportSources(),
    };

    // ---------- pages internes (cdx://) : seules les pages de la charte sont écoutées ----------
    async void FromInternalPage(BrowserTab t, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsInternalUrl(e.Source)) return;   // une page web quelconque ne commande rien
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson); var r = doc.RootElement;
            string cmd = r.GetProperty("cmd").GetString();
            string Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            void Reply(object o) { if (t.Web.CoreWebView2 != null) t.Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(o)); }
            async Task ReplyExt() => Reply(new { type = "extensions", items = await Extensions.ListAsync() });
            switch (cmd)
            {
                case "in.data": Reply(DataPayload()); break;
                case "in.navigate": t.Web.CoreWebView2.Navigate(Resolve(Str("url"))); break;
                case "in.bookmark.remove": { var b = BrowserData.Bookmarks.FirstOrDefault(x => x.Url == Str("url")); if (b != null) { BrowserData.Bookmarks.Remove(b); BrowserData.SaveBookmarks(); } Reply(DataPayload()); sh.PushAll(); break; }
                case "in.history.clear": BrowserData.History.Clear(); BrowserData.SaveHistory(); Reply(DataPayload()); break;
                case "in.dl.open": { var d = BrowserData.Downloads.FirstOrDefault(x => x.Id == r.GetProperty("id").GetInt32()); if (d != null && File.Exists(d.Path)) Process.Start(new ProcessStartInfo(d.Path) { UseShellExecute = true }); break; }
                case "in.dl.reveal": { var d = BrowserData.Downloads.FirstOrDefault(x => x.Id == r.GetProperty("id").GetInt32()); if (d != null) { string rp = d.Path; _ = Task.Run(() => { if (!Bus.Send("explorer", "select", new { path = rp }, "browser", launch: true)) Process.Start("explorer.exe", "/select,\"" + rp + "\""); }); } break; }
                case "in.dl.cancel": { var d = BrowserData.Downloads.FirstOrDefault(x => x.Id == r.GetProperty("id").GetInt32()); d?.Op?.Cancel(); break; }
                case "in.dl.clear": BrowserData.Downloads.RemoveAll(x => x.State != "progress"); sh.PushDownloads(); break;
                case "in.xfers": Xfer.Show(); break;
                // réglages
                case "in.settings.set": if (r.TryGetProperty("value", out var sv) && S.Set(Str("key"), sv)) { sh.ApplySettingsAll(); Reply(DataPayload()); } break;
                case "in.settings.choosedir":
                {
                    using var dlg = new FolderBrowserDialog { Description = "Dossier des téléchargements", UseDescriptionForTitle = true, ShowNewFolderButton = true, SelectedPath = S.DownloadsFolder };
                    if (dlg.ShowDialog(this) == DialogResult.OK) { S.DownloadDir = dlg.SelectedPath; S.Save(); }
                    Reply(DataPayload()); break;
                }
                case "in.settings.clear":
                {
                    // effacer les données de navigation du profil : cookies et sessions, cache, et (si demandé) l'historique du navigateur
                    var kinds = CoreWebView2BrowsingDataKinds.Cookies | CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage | CoreWebView2BrowsingDataKinds.LocalStorage | CoreWebView2BrowsingDataKinds.IndexedDb | CoreWebView2BrowsingDataKinds.ServiceWorkers;
                    bool hist = r.TryGetProperty("history", out var hv) && hv.ValueKind == JsonValueKind.True;
                    if (hist) { kinds |= CoreWebView2BrowsingDataKinds.BrowsingHistory; BrowserData.History.Clear(); BrowserData.SaveHistory(); }
                    await t.Web.CoreWebView2.Profile.ClearBrowsingDataAsync(kinds);
                    Host.Log("données de navigation effacées" + (hist ? " (avec l'historique)" : ""));
                    Reply(new { type = "done", what = "clear" }); Reply(DataPayload()); break;
                }
                // import
                case "in.import.bookmarks":
                {
                    string p = Str("path");
                    if (p == null) { using var dlg = new OpenFileDialog { Title = "Favoris à importer", Filter = "Favoris de Chrome ou Edge (Bookmarks)|Bookmarks|Export HTML de favoris|*.html;*.htm|Tous|*.*" }; if (dlg.ShowDialog(this) != DialogResult.OK) break; p = dlg.FileName; }
                    try { var (a, sk) = BrowserData.ImportBookmarks(p); Reply(new { type = "done", what = "bookmarks", added = a, skipped = sk }); Host.Log($"favoris importés de {p} : {a} ajoutés, {sk} déjà là"); }
                    catch (Exception ex) { Reply(new { type = "done", what = "bookmarks", error = ex.Message }); }
                    Reply(DataPayload()); sh.PushAll(); break;
                }
                case "in.import.passwords":
                {
                    string p = Str("path");
                    if (p == null) { using var dlg = new OpenFileDialog { Title = "Mots de passe exportés (CSV)", Filter = "Export CSV|*.csv|Tous|*.*" }; if (dlg.ShowDialog(this) != DialogResult.OK) break; p = dlg.FileName; }
                    try { var (a, sk) = Passwords.ImportCsv(p); Reply(new { type = "done", what = "passwords", added = a, skipped = sk }); Host.Log($"mots de passe importés de {Path.GetFileName(p)} : {a} ajoutés, {sk} ignorés"); }
                    catch (Exception ex) { Reply(new { type = "done", what = "passwords", error = ex.Message }); }
                    Reply(DataPayload()); break;
                }
                case "in.pw.remove": Passwords.Remove(Str("url"), Str("user")); Reply(DataPayload()); break;
                case "in.pw.clear": Passwords.All.Clear(); Passwords.Save(); Reply(DataPayload()); break;
                // extensions
                case "in.ext.list": await ReplyExt(); break;
                case "in.ext.enable": await Extensions.EnableAsync(Str("id"), r.TryGetProperty("on", out var ov) && ov.ValueKind == JsonValueKind.True); await ReplyExt(); break;
                case "in.ext.remove": await Extensions.RemoveAsync(Str("id")); await ReplyExt(); break;
                case "in.ext.options": { var u = Extensions.OptionsUrl(Str("id")); if (u != null) _ = NewTabAsync(u, true); break; }
                case "in.ext.add":
                {
                    using var dlg = new FolderBrowserDialog { Description = "Dossier d'une extension non empaquetée (avec son manifest.json)", UseDescriptionForTitle = true, ShowNewFolderButton = false };
                    if (dlg.ShowDialog(this) != DialogResult.OK) break;
                    string err = await Extensions.AddFolderAsync(dlg.SelectedPath);
                    Reply(new { type = "done", what = "ext", error = err }); await ReplyExt(); break;
                }
                case "in.ext.install":
                {
                    string err = await Extensions.InstallZipAsync(Str("url"), Str("name") ?? "uBOLite", txt => Reply(new { type = "progress", text = txt }));
                    Reply(new { type = "done", what = "ext", error = err }); await ReplyExt(); break;
                }
            }
        }
        catch (Exception ex) { Host.Log("page interne : " + ex.Message); }
    }

    // ---------- commandes venues de la page ui/navigateur.html ----------
    public void Command(string cmd, JsonElement r)
    {
        string Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int Int(string k) => r.TryGetProperty(k, out var v) && v.TryGetInt32(out var i) ? i : 0;
        bool Bool(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
        BrowserTab Tab() => Tabs.FirstOrDefault(x => x.Id == Int("id"));
        var c = active?.Web.CoreWebView2;
        switch (cmd)
        {
            case "br.ready": Push(); PushDownloads(); break;
            case "br.layout":
                view = new Rectangle(Int("x"), Int("y"), Math.Max(50, Int("w")), Math.Max(50, Int("h")));
                view2 = r.TryGetProperty("x2", out _) ? new Rectangle(Int("x2"), Int("y2"), Math.Max(50, Int("w2")), Math.Max(50, Int("h2"))) : Rectangle.Empty;
                if (!fullscreen) Apply();
                break;
            case "br.newtab": _ = NewTabAsync(Str("url") ?? Internal("newtab"), true); break;
            case "br.close": { var t = Tab(); if (t != null) CloseTab(t); break; }
            case "br.closeothers": { var keep = Tab(); if (keep != null) foreach (var t in Tabs.Where(x => x != keep).ToList()) CloseTab(t); break; }
            case "br.select": { var t = Tab(); if (t != null) Select(t); break; }
            case "br.duplicate": { var t = Tab(); if (t != null) _ = NewTabAsync(t.Url, true, t.Group, Tabs.IndexOf(t)); break; }
            case "br.reopen": ReopenClosed(); break;
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
            // fenêtres
            case "br.newwindow": sh.NewWindow(Str("url")); break;
            case "br.towindow": { var t = Tab(); if (t != null) sh.MoveToNewWindow(this, t); break; }
            // groupes
            case "br.group.new": { var ids = r.TryGetProperty("ids", out var iv) && iv.ValueKind == JsonValueKind.Array ? iv.EnumerateArray().Select(x => x.GetInt32()).ToList() : new List<int> { Int("id") }; NewGroup(Str("name"), Str("color"), Tabs.Where(t => ids.Contains(t.Id))); break; }
            case "br.group.add": { var t = Tab(); if (t != null) AddToGroup(t, Int("group")); break; }
            case "br.group.leave": { var t = Tab(); if (t != null) Ungroup(t); break; }
            case "br.group": GroupCommand(Int("group"), Str("what"), Str("name"), Str("color"), Bool("on")); break;
            // pages scindées
            case "br.split": Split(Tab()); break;
            case "br.unsplit": Unsplit(); break;
        }
    }

    // Pour les tests (canal de contrôle) : l'état des onglets, et l'évaluation d'un script dans la page active ou dans le cadre.
    public object State() => new
    {
        active = active?.Id, split = pane2?.Id, left = pane1?.Id,
        tabs = Tabs.Select(t => new { t.Id, t.Title, Url = t.Url, t.Loading, back = t.Web.CoreWebView2?.CanGoBack, zoom = t.Web.ZoomFactor, starred = BrowserData.IsStarred(t.Url), favicon = t.Favicon.Length > 0, group = t.Group, bounds = new[] { t.Web.Bounds.X, t.Web.Bounds.Y, t.Web.Bounds.Width, t.Web.Bounds.Height }, visible = t.Web.Visible }),
        groups = Groups.Select(g => new { g.Id, g.Name, g.Color, g.Collapsed }), closed = closed.Count,
        bounds = new[] { Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height },
    };
    public async Task<string> EvalAsync(string js, bool ui) => await (ui ? Web.CoreWebView2 : active.Web.CoreWebView2).ExecuteScriptAsync(js);

    public void Push()
    {
        var items = Tabs.Select(t =>
        {
            var c = t.Web.CoreWebView2;
            return new
            {
                id = t.Id, title = t.Title, url = DisplayUrl(t.Url), loading = t.Loading, favicon = t.Favicon,
                canBack = c?.CanGoBack ?? false, canFwd = c?.CanGoForward ?? false, zoom = (int)Math.Round(t.Web.ZoomFactor * 100),
                starred = BrowserData.IsStarred(t.Url), internalPage = IsInternalUrl(t.Url), group = t.Group,
            };
        }).ToList();
        Send(new { type = "tabs", active = active?.Id ?? 0, split = pane2?.Id ?? 0, left = pane1?.Id ?? 0, items, groups = Groups.Select(g => new { id = g.Id, name = g.Name, color = g.Color, collapsed = g.Collapsed }), closed = closed.Count, windows = sh.WindowCount });
        if (active != null) Text = active.Title + " — Navigateur";
        sh.SessionChanged();
    }
}
