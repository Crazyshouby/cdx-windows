using System.Drawing;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Cdx;

static class SelfTest
{
    // cdx-shell.exe --selftest-browser : ce qu'offre WebView2 à un navigateur construit dessus. Charge une extension non empaquetée (script de contenu)
    // dans un profil de test, puis une page qui sonde les systèmes de DRM et les codecs. Résultat dans selftest-browser.txt.
    [STAThread]
    public static void RunBrowser()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        string ext = Path.Combine(Path.GetTempPath(), "cdx-ext-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(ext);
        File.WriteAllText(Path.Combine(ext, "manifest.json"), "{\"manifest_version\":3,\"name\":\"cdx-test\",\"version\":\"1.0\",\"content_scripts\":[{\"matches\":[\"https://cdx.shell/*\"],\"js\":[\"c.js\"],\"run_at\":\"document_start\"}]}");
        File.WriteAllText(Path.Combine(ext, "c.js"), "document.documentElement.setAttribute('data-cdx-ext','appliquée');");

        var lines = new List<string>(); bool done = false; int bad = 0;
        void Check(string what, bool ok) { lines.Add((ok ? "OK     " : "ÉCHEC  ") + what); if (!ok) bad++; }

        // ---- logique pure : à quelles pages vont les mots de passe importés (rien n'est écrit : la liste est rendue telle quelle) ----
        var savedCreds = Passwords.All.ToList(); Passwords.All.Clear();
        try
        {
            Passwords.All.Add(new Credential { Url = "https://exemple.fr/connexion", User = "a", Password = "p" });
            Passwords.All.Add(new Credential { Url = "https://compte.exemple.net/", User = "b", Password = "p" });
            Passwords.All.Add(new Credential { Url = "http://exemple.org/", User = "c", Password = "p" });
            Passwords.All.Add(new Credential { Url = "http://127.0.0.1:8767/login.html", User = "d", Password = "p" });
            int N(string url) => Passwords.For(url).Count;
            Check("mots de passe : même origine https, autre chemin : proposé", N("https://exemple.fr/autre/page") == 1 && N("https://EXEMPLE.fr/") == 1);
            Check("mots de passe : sous-domaine, domaine parent, autre port : jamais", N("https://www.exemple.fr/") == 0 && N("https://exemple.net/") == 0 && N("https://exemple.fr:8443/") == 0);
            Check("mots de passe : jamais sur http (ni une entrée http sur sa page http), sauf la boucle locale", N("http://exemple.fr/") == 0 && N("http://exemple.org/") == 0 && N("http://127.0.0.1:8767/x") == 1 && N("http://localhost:8767/x") == 0);
            Check("mots de passe : adresse invalide ou schéma inconnu : rien", N("pas une adresse") == 0 && N("file:///C:/x.html") == 0 && N(null) == 0);
        }
        catch (Exception ex) { Check("mots de passe : exception " + ex.Message, false); }
        finally { Passwords.All.Clear(); Passwords.All.AddRange(savedCreds); }
        var form = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(300, 200) };
        var web = new WebView2 { Dock = DockStyle.Fill }; form.Controls.Add(web);

        void Finish(bool ok)
        {
            if (done) return; done = true;
            File.WriteAllLines(Path.Combine(Host.DataDir, "selftest-browser.txt"), lines);
            Environment.ExitCode = ok && bad == 0 ? 0 : 1;
            try { Directory.Delete(ext, true); } catch { }
            Application.ExitThread();
        }

        form.Load += async (s, e) =>
        {
            try
            {
                var opts = new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = true };
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Host.DataDir, "webview-selftest-browser"), opts);
                await web.EnsureCoreWebView2Async(env);
                var c = web.CoreWebView2;
                lines.Add("WebView2 : moteur " + env.BrowserVersionString);
                try { var x = await c.Profile.AddBrowserExtensionAsync(ext); lines.Add($"extension non empaquetée : installée (id {x.Id}, active {x.IsEnabled})"); }
                catch (Exception ex) { lines.Add("extension non empaquetée : ÉCHEC " + ex.Message); }
                c.SetVirtualHostNameToFolderMapping("cdx.shell", Host.FindWebRoot(), CoreWebView2HostResourceAccessKind.Allow);
                c.WebMessageReceived += (s2, e2) =>
                {
                    using var d = JsonDocument.Parse(e2.WebMessageAsJson); var r = d.RootElement;
                    lines.Add("extension : script de contenu " + r.GetProperty("extension").GetString());
                    lines.Add("agent utilisateur : " + r.GetProperty("userAgent").GetString());
                    foreach (var kv in r.GetProperty("drm").EnumerateObject()) lines.Add($"DRM {kv.Name} : {kv.Value.GetString()}");
                    foreach (var kv in r.GetProperty("codecs").EnumerateObject()) lines.Add($"codec {kv.Name} : {(string.IsNullOrEmpty(kv.Value.GetString()) ? "non" : kv.Value.GetString())}");
                    Finish(true);
                };
                c.Navigate("https://cdx.shell/browser/_test-browser.html");
            }
            catch (Exception ex) { lines.Add("EXCEPTION " + ex.Message); Finish(false); }
        };
        var timer = new System.Windows.Forms.Timer { Interval = 25000 }; timer.Tick += (s, e) => { lines.Add("délai dépassé"); Finish(false); }; timer.Start();
        form.Show();
        Application.Run();
    }
}
