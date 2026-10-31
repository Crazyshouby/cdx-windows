using System.Drawing;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Kdx;

// Le programme : un navigateur, dont le profil (cookies, sessions) est à part de celui des pages de la barre. Les extensions non empaquetées déposées
// dans %LOCALAPPDATA%\KdxShell\extensions\<nom> sont chargées à l'ouverture.
sealed class BrowserApp : ApplicationContext, IHostApp
{
    string theme;
    readonly IDisposable watch;
    readonly SideButtons side;
    CoreWebView2Environment uiEnv;
    BrowserForm browser;

    public string Root { get; }
    public CoreWebView2Environment BrowserEnv { get; private set; }
    public bool Closing { get; private set; }
    public readonly Control Anchor = new Control();   // un fil d'interface où poser les commandes du canal de test

    public BrowserApp(string theme)
    {
        this.theme = theme ?? Suite.ReadAccent(); Root = Host.FindWebRoot();
        _ = Anchor.Handle;
        if (theme == null) watch = Suite.Watch(this.theme, a => { this.theme = a; browser?.SetTheme(a); });
        side = new SideButtons(() => browser != null && !browser.IsDisposed ? new HostForm[] { browser } : Array.Empty<HostForm>());
        Xfer.Active = () => BrowserData.Downloads.Where(d => d.State == "progress").Select(BrowserData.XferOf).ToList();
        Xfer.Control = BrowserData.Control;
    }

    protected override void ExitThreadCore() { Closing = true; watch?.Dispose(); side.Dispose(); base.ExitThreadCore(); }

    public async void Open(string url)
    {
        try
        {
            uiEnv ??= await CoreWebView2Environment.CreateAsync(null, Path.Combine(Host.DataDir, "webview-browser"), new CoreWebView2EnvironmentOptions("--disable-lcd-text"));
            BrowserEnv ??= await CoreWebView2Environment.CreateAsync(null, Path.Combine(Host.DataDir, "navigateur"),
                new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = true, Language = "fr" });
            if (browser != null && !browser.IsDisposed)
            {
                if (browser.WindowState == FormWindowState.Minimized) browser.WindowState = FormWindowState.Normal;
                browser.Activate(); Host.ForceForeground(browser.Handle);
                if (url != null) await browser.NewTabAsync(url, true);
                return;
            }
            var f = new BrowserForm(this); browser = f;
            f.FormClosed += (s, e) => { if (browser == f) browser = null; ExitThread(); };
            double scale = f.DeviceDpi / 96.0;
            f.Bounds = new Rectangle(120, 50, (int)(1280 * scale), (int)(800 * scale));
            f.Show(); Host.ForceForeground(f.Handle);
            await f.InitWebAsync(uiEnv, Root, "browser/navigateur.html?theme=" + theme, scale);
            await f.RestoreAsync(url);
            Host.Log("navigateur ouvert");
        }
        catch (Exception ex) { Host.Log("navigateur : " + ex); }
    }

    public void OnMessage(HostForm from, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string cmd = r.GetProperty("cmd").GetString();
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

    // Canal de test (debug.txt) : launch <adresse> · state · eval <js> · evalui <js> · cmd <json>.
    public async Task<string> HandleControl(string line)
    {
        int sp = line.IndexOf(' '); string c = sp < 0 ? line : line[..sp], a = sp < 0 ? "" : line[(sp + 1)..];
        switch (c)
        {
            case "launch": Open(a.StartsWith("kdx:navigateur") ? null : a); return "ok";
            case "state": return browser?.StateJson() ?? "pas de navigateur";
            case "eval": return browser == null ? "pas de navigateur" : await browser.EvalAsync(a, false);
            case "evalui": return browser == null ? "pas de navigateur" : await browser.EvalAsync(a, true);
            case "cmd": { using var d = JsonDocument.Parse(a); string cm = d.RootElement.GetProperty("cmd").GetString(); if (cm.StartsWith("win.") && browser != null) Host.WindowCommand(browser, cm, d.RootElement); else browser?.Command(cm, d.RootElement); return "ok"; }
            default: return "commande inconnue";
        }
    }
}
