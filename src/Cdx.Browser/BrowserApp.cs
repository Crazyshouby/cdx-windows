using System.Drawing;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Cdx;

// Le programme : un navigateur à plusieurs fenêtres, dont le profil (cookies, sessions) est à part de celui des pages de la barre. Les extensions non
// empaquetées déposées dans %LOCALAPPDATA%\CdxShell\extensions\<nom> sont chargées à l'ouverture (gestionnaire : cdx://extensions). La session (fenêtres,
// onglets, groupes) est réécrite à chaque changement et retrouvée au lancement suivant, même après un plantage.
sealed class BrowserApp : ApplicationContext, IHostApp
{
    string theme;
    readonly IDisposable watch;
    readonly SideButtons side;
    CoreWebView2Environment uiEnv;
    readonly List<BrowserForm> windows = new();
    BrowserForm lastActive;
    bool restored; int count;
    System.Windows.Forms.Timer sessionTimer;

    public string Root { get; }
    public CoreWebView2Environment BrowserEnv { get; private set; }
    public bool Closing { get; private set; }
    public int WindowCount => windows.Count;
    public readonly Control Anchor = new Control();   // un fil d'interface où poser les commandes du canal de test

    public BrowserApp(string theme)
    {
        this.theme = theme ?? Suite.ReadAccent(); Root = Host.FindWebRoot();
        _ = Anchor.Handle;
        if (theme == null) watch = Suite.Watch(this.theme, a => { this.theme = a; foreach (var w in windows.ToArray()) w.SetTheme(a); });
        side = new SideButtons(() => windows.Where(w => !w.IsDisposed).Cast<HostForm>().ToArray());
        Xfer.Active = () => BrowserData.Downloads.Where(d => d.State == "progress").Select(BrowserData.XferOf).ToList();
        Xfer.Control = BrowserData.Control;
    }

    protected override void ExitThreadCore() { Closing = true; watch?.Dispose(); side.Dispose(); BrowserData.FlushHistory(); base.ExitThreadCore(); }

    BrowserForm Current => lastActive != null && !lastActive.IsDisposed && windows.Contains(lastActive) ? lastActive : windows.LastOrDefault(w => !w.IsDisposed);

    async Task EnvAsync()
    {
        uiEnv ??= await CoreWebView2Environment.CreateAsync(null, Path.Combine(Host.DataDir, "webview-browser"), new CoreWebView2EnvironmentOptions("--disable-lcd-text"));
        BrowserEnv ??= await CoreWebView2Environment.CreateAsync(null, Path.Combine(Host.DataDir, "navigateur"), new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = true, Language = "fr" });
    }

    // Une adresse à ouvrir (lancement, bus, second lancement) : dans la fenêtre active s'il y en a une ; sinon la session précédente est retrouvée, puis l'adresse ouverte.
    public async void Open(string url)
    {
        try
        {
            await EnvAsync();
            var cur = Current;
            if (cur != null)
            {
                if (cur.WindowState == FormWindowState.Minimized) cur.WindowState = FormWindowState.Normal;
                cur.Activate(); Host.ForceForeground(cur.Handle);
                if (url != null) await cur.NewTabAsync(url, true);
                return;
            }
            if (!restored)
            {
                restored = true;
                var s = BrowserSettings.Current.RestoreSession ? Session.Read() : new SessionFile();
                var wins = s.Windows.Where(w => w.Tabs.Count > 0).ToList();
                if (wins.Count > 0)
                {
                    restoring = true;   // pas d'écriture de la session tant qu'elle n'est pas entièrement retrouvée
                    try { for (int i = 0; i < wins.Count; i++) await NewWindowAsync(wins[i], i == wins.Count - 1 ? url : null); }
                    finally { restoring = false; SessionChanged(); }
                    Host.Log($"session retrouvée : {wins.Count} fenêtre(s), {wins.Sum(w => w.Tabs.Count)} onglet(s)");
                    return;
                }
            }
            await NewWindowAsync(null, url);
        }
        catch (Exception ex) { Host.Log("navigateur : " + ex); }
    }

    public void NewWindow(string url) => _ = NewWindowAsync(null, url);
    async Task<BrowserForm> NewWindowAsync(SessionWindow w, string url)
    {
        await EnvAsync();
        var f = new BrowserForm(this); windows.Add(f);
        f.Activated += (s, e) => lastActive = f;
        f.FormClosed += (s, e) => { windows.Remove(f); if (windows.Count == 0) ExitThread(); };
        double scale = f.DeviceDpi / 96.0;
        int off = (count++ % 6) * 28;
        f.Bounds = w != null && w.W > 0 ? new Rectangle(w.X, w.Y, w.W, w.H) : new Rectangle(120 + off, 50 + off, (int)(1280 * scale), (int)(800 * scale));
        if (!Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(f.Bounds))) f.Bounds = new Rectangle(120, 50, (int)(1280 * scale), (int)(800 * scale));   // un écran disparu
        f.Show(); Host.ForceForeground(f.Handle); lastActive = f;
        await f.InitWebAsync(uiEnv, Root, "browser/navigateur.html?theme=" + theme, scale);
        if (w != null) { await f.RestoreAsync(w, url); if (w.Max) f.Maximize(); }
        else await f.NewTabAsync(url ?? "cdx://newtab", true);
        Host.Log("fenêtre du navigateur ouverte (" + windows.Count + ")");
        return f;
    }

    // « Ouvrir dans une nouvelle fenêtre » : l'onglet change de fenêtre sans être rechargé.
    public async void MoveToNewWindow(BrowserForm from, BrowserTab t)
    {
        try
        {
            var f = new BrowserForm(this); windows.Add(f);
            f.Activated += (s, e) => lastActive = f;
            f.FormClosed += (s, e) => { windows.Remove(f); if (windows.Count == 0) ExitThread(); };
            double scale = f.DeviceDpi / 96.0; int off = (count++ % 6) * 28;
            f.Bounds = new Rectangle(120 + off, 50 + off, (int)(1280 * scale), (int)(800 * scale));
            f.Show(); Host.ForceForeground(f.Handle); lastActive = f;
            await f.InitWebAsync(uiEnv, Root, "browser/navigateur.html?theme=" + theme, scale);
            var moved = from.Detach(t); if (moved != null) f.Attach(moved);
        }
        catch (Exception ex) { Host.Log("nouvelle fenêtre : " + ex); }
    }

    // ---------- session (réécrite un instant après chaque changement ; tout de suite à la fermeture) ----------
    bool restoring;
    public void SessionChanged()
    {
        if (Closing || !restored || restoring) return;
        if (sessionTimer == null) { sessionTimer = new System.Windows.Forms.Timer { Interval = 500 }; sessionTimer.Tick += (s, e) => { sessionTimer.Stop(); SaveSession(); }; }
        sessionTimer.Stop(); sessionTimer.Start();
    }
    void SaveSession()
    {
        try { Session.Write(new SessionFile { Windows = windows.Where(w => !w.IsDisposed && w.Tabs.Count > 0).Select(w => w.Snapshot()).ToList() }); }
        catch (Exception ex) { Host.Log("session : " + ex.Message); }
    }
    // Une fenêtre se ferme : la dernière emporte la session telle qu'elle est (pour la retrouver) ; une autre en est retirée.
    public void WindowClosing(BrowserForm f)
    {
        sessionTimer?.Stop();
        if (windows.Count <= 1) { SaveSession(); Closing = true; }
        else { windows.Remove(f); SaveSession(); }
    }

    public void PushAll() { foreach (var w in windows.ToArray()) if (!w.IsDisposed) w.Push(); }
    public void PushDownloads() { foreach (var w in windows.ToArray()) if (!w.IsDisposed) w.PushDownloads(); }
    public void ApplySettingsAll() { foreach (var w in windows.ToArray()) if (!w.IsDisposed) w.ApplySettingsAll(); }

    public void OnMessage(HostForm from, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string cmd = r.GetProperty("cmd").GetString();
            if (cmd == "win.overlay") { (from as BrowserForm)?.SetOverlay(r.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True); return; }   // un menu de la barre de titre : les pages web posées dessous sont masquées le temps qu'il est ouvert
            if (cmd.StartsWith("win.")) { Host.WindowCommand(from, cmd, r); return; }
            if (cmd.StartsWith("br.")) { (from as BrowserForm)?.Command(cmd, r); return; }
            Host.CommonMessage(from, cmd, r);
        }
        catch (Exception ex) { Host.Log("message mal formé ou échec : " + ex.Message + " :: " + json); }
    }

    // Les messages du bus (SUITE.md §3.3) : open-url { url } (http ou https seulement) et open-file { path }.
    public bool OnBus(string type, string from, JsonElement data)
    {
        if (Xfer.OnBus(type, data)) return true;
        if (type == "open-file")
        {
            // un fichier local que le navigateur sait afficher (page web, PDF, image, texte, son, vidéo) : chemin absolu d'un fichier qui existe
            string p = data.TryGetProperty("path", out var pv) && pv.ValueKind == JsonValueKind.String ? pv.GetString() : null;
            if (!Bus.IsBrowsableFile(p)) return false;
            Open(new Uri(p).AbsoluteUri); return true;
        }
        if (type != "open-url") return false;
        if (!data.TryGetProperty("url", out var u)) { Open(null); return true; }
        string url = u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        if (url == null || url.Length == 0 || url.Length > 2000 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) return false;
        Open(url); return true;
    }

    // Canal de test (debug.txt) : launch <adresse> · state · eval <js> · evalui <js> · cmd <json> · use <n° de fenêtre, 0 = la dernière active>.
    int useWindow = -1;
    BrowserForm Target => useWindow >= 0 && useWindow < windows.Count ? windows[useWindow] : Current;
    public async Task<string> HandleControl(string line)
    {
        int sp = line.IndexOf(' '); string c = sp < 0 ? line : line[..sp], a = sp < 0 ? "" : line[(sp + 1)..];
        var b = Target;
        switch (c)
        {
            case "launch": Open(a.StartsWith("cdx:navigateur") ? null : a); return "ok";
            case "use": useWindow = int.TryParse(a, out var n) ? n - 1 : -1; return "ok";
            case "state":
            {
                if (b == null) return "pas de navigateur";
                var st = (dynamic)b.State();
                return JsonSerializer.Serialize(new { active = (int?)st.active, split = (int?)st.split, tabs = st.tabs, groups = st.groups, closed = (int)st.closed, downloads = BrowserData.Downloads.Select(d => d.Info()), windows = windows.Select(w => w.State()), window = windows.IndexOf(b) + 1 });
            }
            case "eval": return b == null ? "pas de navigateur" : await b.EvalAsync(a, false);
            case "evalui": return b == null ? "pas de navigateur" : await b.EvalAsync(a, true);
            case "cmd": { using var d = JsonDocument.Parse(a); string cm = d.RootElement.GetProperty("cmd").GetString(); if (cm.StartsWith("win.") && b != null) Host.WindowCommand(b, cm, d.RootElement); else b?.Command(cm, d.RootElement); return "ok"; }
            // ---- le pilotage d'une page (Pilote / Claude) : des scripts fixes sur l'onglet actif, jamais du JavaScript reçu ----
            case "page.text": return b?.Active?.Web.CoreWebView2 == null ? "ERREUR pas de page" : await b.EvalAsync(PageText.Replace("MAX", (int.TryParse(a, out var mx) && mx > 0 ? Math.Min(mx, 200000) : 40000).ToString()), false);
            case "page.find": return b?.Active?.Web.CoreWebView2 == null ? "ERREUR pas de page" : await b.EvalAsync(PageFind, false);
            case "page.click": return b?.Active?.Web.CoreWebView2 == null ? "ERREUR pas de page" : await b.EvalAsync(PageClick.Replace("ARG", JsonSerializer.Serialize(a)), false);
            case "page.fill": return b?.Active?.Web.CoreWebView2 == null ? "ERREUR pas de page" : await b.EvalAsync(PageFill.Replace("ARG", JsonSerializer.Serialize(a)), false);
            case "page.scroll": return b?.Active?.Web.CoreWebView2 == null ? "ERREUR pas de page" : await b.EvalAsync("(() => { window.scrollBy(0, " + (int.TryParse(a, out var dy) ? dy : 600) + "); return JSON.stringify({ y: Math.round(window.scrollY), max: Math.round(document.documentElement.scrollHeight - window.innerHeight) }); })()", false);
            case "page.shot":
            {
                var cw = b?.Active?.Web.CoreWebView2; if (cw == null) return "ERREUR pas de page";
                using var ms = new MemoryStream();
                await cw.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                return Convert.ToBase64String(ms.ToArray());
            }
            default: return "commande inconnue";
        }
    }

    // Les scripts de pilotage. Chacun rend une chaîne JSON (ExecuteScriptAsync rend la valeur sérialisée : une chaîne JSON dans une chaîne JSON).
    const string PageText = """
        (() => { const t = (document.body ? document.body.innerText : '').replace(/\n{3,}/g, '\n\n'); return JSON.stringify({ title: document.title, url: location.href, length: t.length, text: t.slice(0, MAX) }); })()
        """;
    // Les éléments sur lesquels on peut agir, numérotés (le numéro est posé sur l'élément : page.click / page.fill le retrouvent).
    const string PageFind = """
        (() => {
          const out = []; let n = 0;
          for (const e of document.querySelectorAll('a[href], button, input, select, textarea, [role=button], [role=link], [role=textbox], [contenteditable=true], summary, [onclick]')) {
            if (n >= 400) break;
            const r = e.getBoundingClientRect(); if (!r.width && !r.height) continue;
            const st = getComputedStyle(e); if (st.visibility === 'hidden' || st.display === 'none') continue;
            if (e.tagName === 'INPUT' && e.type === 'hidden') continue;
            n++; e.dataset.cdxN = String(n);
            const text = (e.innerText || e.value || e.placeholder || e.getAttribute('aria-label') || e.title || e.alt || e.name || '').trim().replace(/\s+/g, ' ').slice(0, 90);
            const o = { n, tag: e.tagName.toLowerCase(), text };
            if (e.type && e.tagName !== 'A') o.type = e.type;
            if (e.href && e.tagName === 'A') o.href = String(e.href).slice(0, 300);
            if (e.name || e.id) o.name = e.name || e.id;
            if (e.tagName === 'SELECT') o.options = [...e.options].slice(0, 30).map(x => x.text.trim().slice(0, 60));
            out.push(o);
          }
          return JSON.stringify({ count: n, elements: out });
        })()
        """;
    // ARG : {"n":3} ou {"text":"Se connecter"} ou {"selector":"#ok"}. Clique l'élément (après l'avoir fait apparaître).
    const string PageClick = """
        (() => {
          const a = JSON.parse(ARG); let e = null;
          if (a.n) e = document.querySelector('[data-cdx-n="' + a.n + '"]');
          else if (a.selector) { try { e = document.querySelector(a.selector); } catch (x) { return JSON.stringify({ ok: false, error: 'sélecteur invalide' }); } }
          else if (a.text) { const want = String(a.text).trim().toLowerCase(); const all = [...document.querySelectorAll('a, button, input[type=submit], input[type=button], [role=button], [role=link], summary, label')]; e = all.find(x => (x.innerText || x.value || x.getAttribute('aria-label') || '').trim().toLowerCase() === want) || all.find(x => (x.innerText || x.value || x.getAttribute('aria-label') || '').trim().toLowerCase().includes(want)); }
          if (!e) return JSON.stringify({ ok: false, error: 'élément introuvable' });
          e.scrollIntoView({ block: 'center' }); e.focus && e.focus(); e.click();
          return JSON.stringify({ ok: true, tag: e.tagName.toLowerCase(), text: (e.innerText || e.value || '').trim().slice(0, 80), href: e.href ? String(e.href).slice(0, 300) : undefined });
        })()
        """;
    // ARG : {"n":3,"value":"…","submit":true} ou {"selector":"#q",…}. Remplit un champ comme une frappe (événements input/change), et valide (Entrée / formulaire) si demandé.
    const string PageFill = """
        (() => {
          const a = JSON.parse(ARG); let e = null;
          if (a.n) e = document.querySelector('[data-cdx-n="' + a.n + '"]');
          else if (a.selector) { try { e = document.querySelector(a.selector); } catch (x) { return JSON.stringify({ ok: false, error: 'sélecteur invalide' }); } }
          else if (a.name) e = document.querySelector('[name="' + a.name + '"], #' + CSS.escape(a.name));
          if (!e) return JSON.stringify({ ok: false, error: 'champ introuvable' });
          e.scrollIntoView({ block: 'center' }); e.focus && e.focus();
          const v = String(a.value == null ? '' : a.value);
          if (e.tagName === 'SELECT') { const o = [...e.options].find(x => x.value === v || x.text.trim() === v); if (!o) return JSON.stringify({ ok: false, error: 'option inconnue' }); e.value = o.value; }
          else if (e.isContentEditable) { e.textContent = v; }
          else if (e.type === 'checkbox' || e.type === 'radio') { e.checked = v === 'true' || v === '1' || v === 'on'; }
          else { const proto = e.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype; const d = Object.getOwnPropertyDescriptor(proto, 'value'); if (d && d.set) d.set.call(e, v); else e.value = v; }
          e.dispatchEvent(new Event('input', { bubbles: true })); e.dispatchEvent(new Event('change', { bubbles: true }));
          if (a.submit) { e.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, bubbles: true })); e.dispatchEvent(new KeyboardEvent('keyup', { key: 'Enter', code: 'Enter', keyCode: 13, bubbles: true })); if (e.form) { if (e.form.requestSubmit) e.form.requestSubmit(); else e.form.submit(); } }
          return JSON.stringify({ ok: true, tag: e.tagName.toLowerCase(), name: e.name || e.id || '' });
        })()
        """;
}
