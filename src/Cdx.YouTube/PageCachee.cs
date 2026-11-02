using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Cdx;

// La page YouTube cachée : une WebView2 hors de l'écran, sur un profil persistant (session Google, cookies) avec uBOL. Elle fait le vrai travail ;
// l'interface ne lui parle que par les méthodes ci-dessous et ne voit que le modèle de YtLecture.
//
// Navigation : un seul Navigate(), au démarrage (et pour la connexion Google). Ensuite tout passe par la navigation interne de YouTube : l'évènement
// « yt-navigate » que déclenche un clic sur un lien de YouTube (yt-simple-endpoint), avec le même « endpoint » que le lien.
// Extraction : WebResourceResponseReceived + GetContentAsync sur youtubei/v1/browse, search, next (côté C#, aucun script dans la page). Si une réponse
// attendue n'arrive pas par là, secours : crochet sur fetch qui renvoie le JSON par chrome.webview.postMessage (posé à la demande, voir Secours).
// Lecture (étape 3) : le contrôle WebView2 de la page quitte sa fenêtre cachée et se pose dans la fenêtre de l'interface, sur le rectangle du lecteur
// de la fiche (MontrerLecteurAsync) ; un style posé dans la page n'y laisse que le lecteur de YouTube (#movie_player), à la taille de la vue.
sealed class PageCachee
{
    public enum Extraction { CSharp, Fetch }

    readonly Form fenetre = new() { Text = "YouTube (page cachée) — CDX-YouTube", ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Size = new Size(1280, 800), Location = new Point(-20000, -20000) };
    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    CoreWebView2Environment env;
    CoreWebView2 core;
    readonly Extraction mode;
    readonly bool visible, muet;
    bool crochetPose, enConnexion;
    TaskCompletionSource<bool> navigation;

    public bool Connecte { get; private set; }
    public string Compte { get; private set; }        // « Nom (@pseudo) » du compte connecté, lu dans le menu de l'avatar
    readonly Dictionary<string, string> pseudos = new();   // id de chaîne → @pseudo (YouTube réécrit /channel/<id>/… en /@pseudo/…)
    public string Bloqueur { get; private set; }
    public event Action<string> EtatChange;           // texte pour l'interface (session, bloqueur)
    public event Action<Liste, string> SuiteRecue;    // une suite de liste arrivée (défilement) : (liste, genre browse|search)
    public event Action ConnexionTerminee;
    public event Action<Fiche> FicheSpontanee;        // le lecteur est passé seul à une autre vidéo (lecture automatique, écran de fin) : sa fiche
    public event Action<bool> PleinEcran;             // le lecteur de YouTube demande (ou quitte) le plein écran
    public readonly Dictionary<string, int> Recues = new() { ["csharp"] = 0, ["fetch"] = 0, ["ytInitialData"] = 0, ["page"] = 0, ["echecs"] = 0 };

    // muet : l'essai automatique lit les vidéos sans le son
    public PageCachee(Extraction mode, bool visible, bool muet = false)
    {
        this.mode = mode; this.visible = visible; this.muet = muet;
        fenetre.Controls.Add(web);
        if (visible) { fenetre.Location = new Point(40, 40); fenetre.ShowInTaskbar = true; }
        // fermer la fenêtre de connexion ne ferme pas la page : elle retourne se cacher
        fenetre.FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing && !fin) { e.Cancel = true; _ = FinConnexionAsync(false); } };
    }
    bool fin;
    public void Fermer() { fin = true; try { fenetre.Close(); } catch { } }

    // ---------- démarrage ----------

    public async Task<Liste> DemarrerAsync()
    {
        fenetre.Show();
        Directory.CreateDirectory(YtDonnees.Profil);
        var opts = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required") { AreBrowserExtensionsEnabled = true, Language = "fr" };
        Host.Log("runtime WebView2 " + CoreWebView2Environment.GetAvailableBrowserVersionString() + ", profil " + YtDonnees.Profil);
        env = await CoreWebView2Environment.CreateAsync(null, YtDonnees.Profil, opts);
        await web.EnsureCoreWebView2Async(env);
        core = web.CoreWebView2;
        core.IsMuted = muet;   // le clic a eu lieu dans la page de l'interface, pas dans celle-ci : d'où la lecture automatique permise (autoplay-policy)
        core.ContainsFullScreenElementChanged += (_, _) => PleinEcran?.Invoke(core.ContainsFullScreenElement);
        core.Settings.AreDefaultContextMenusEnabled = visible;   // pas de menu du clic droit de WebView2 sur le lecteur
        core.Settings.AreBrowserAcceleratorKeysEnabled = visible;   // ni les raccourcis du navigateur (Alt+←, Ctrl+R…) qui changeraient la page cachée
        // les touches qui doivent revenir à l'interface quand le focus est dans le lecteur
        web.KeyDown += (_, e) => { if (e.KeyCode is Keys.Escape or Keys.F11) Touche?.Invoke(e.KeyCode == Keys.Escape ? "Echap" : "F11"); };
        core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
        core.Settings.AreDevToolsEnabled = visible;
        core.NavigationCompleted += async (_, e) =>
        {
            navigation?.TrySetResult(e.IsSuccess);
            if (enConnexion) await SuivreConnexionAsync();
        };
        core.NewWindowRequested += (_, e) => { e.Handled = true; core.Navigate(e.Uri); };   // fenêtres surgissantes du login : dans la même vue
        core.WebMessageReceived += (_, e) => RecuParFetch(e);

        Bloqueur = await Ubol.PreparerAsync(core.Profile);
        core.WebResourceResponseReceived += RecuParCSharp;   // reçoit toutes les réponses, sans filtre à déclarer
        if (mode == Extraction.Fetch) await PoserCrochetAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await AllerAsync("https://www.youtube.com/");
        Host.Log($"YouTube chargé en {sw.ElapsedMilliseconds} ms");
        if (Ubol.VientDEtreAjoute)
        {
            // uBOL tout juste ajouté n'agit pas encore sur cette page : on lui laisse le temps de s'installer, puis YouTube est rechargé une fois
            await Task.Delay(3000);
            await AllerAsync("https://www.youtube.com/");
            Host.Log("uBOL vient d'être ajouté : YouTube rechargé une fois");
        }
        await ConsentementAsync();
        await LireSessionAsync();
        return await ListeInitialeAsync();
    }

    async Task AllerAsync(string url, int delai = 45)
    {
        navigation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        core.Navigate(url);
        if (await Task.WhenAny(navigation.Task, Task.Delay(delai * 1000)) != navigation.Task) throw new TimeoutException("chargement de " + url + " : rien après " + delai + " s");
    }

    // Les données de la page chargée par Navigate() (le premier affichage seulement) : elles sont dans le HTML (ytInitialData), pas dans une réponse youtubei.
    async Task<Liste> ListeInitialeAsync()
    {
        string brut = null;
        for (int i = 0; i < 40 && string.IsNullOrEmpty(brut); i++)
        {
            var r = await core.ExecuteScriptAsync("window.ytInitialData ? JSON.stringify(window.ytInitialData) : ''");
            brut = JsonSerializer.Deserialize<string>(r);
            if (string.IsNullOrEmpty(brut)) await Task.Delay(250);
        }
        if (string.IsNullOrEmpty(brut)) { Veille.Noter("extraction", "ytInitialData absent de la page d'accueil"); return new Liste(); }
        Recues["ytInitialData"]++;
        using var doc = JsonDocument.Parse(brut);
        var l = YtLecture.LireListe(doc.RootElement);
        Host.Log($"accueil (ytInitialData, {brut.Length / 1024} Ko) : {l.Videos.Count} vidéos, {l.PubsFiltrees} pubs filtrées");
        if (l.Videos.Count == 0 && Connecte) Veille.GarderBrut("accueil-vide", brut);
        return l;
    }

    // ---------- consentement UE et session ----------

    // Hors connexion, dans l'UE : une boîte « Avant d'accéder à YouTube » dans la page (ytd-consent-bump-v2-lightbox), ou une redirection vers consent.youtube.com.
    async Task ConsentementAsync()
    {
        for (int i = 0; i < 2 && Hote(core.Source).StartsWith("consent."); i++)
        {
            Host.Log("consentement : page consent.youtube.com, clic sur « Tout refuser »");
            Veille.Noter("session", "consentement UE redemandé (consent.youtube.com)");
            navigation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            string r = await core.ExecuteScriptAsync("""
                (() => { const b = [...document.querySelectorAll('button, input[type=submit]')]
                    .find(x => /tout refuser|reject all/i.test(x.innerText || x.value || x.getAttribute('aria-label') || ''));
                  if (b) { b.click(); return true; } return false; })()
                """);
            if (r != "true") { Veille.Noter("session", "consentement : bouton « Tout refuser » introuvable"); return; }
            await Task.WhenAny(navigation.Task, Task.Delay(30000));
        }
        if (!Hote(core.Source).EndsWith("youtube.com")) return;
        for (int i = 0; i < 12; i++)
        {
            string r = await core.ExecuteScriptAsync("""
                (() => { const d = document.querySelector('ytd-consent-bump-v2-lightbox, tp-yt-paper-dialog #consent-bump');
                  if (!d) return 'absente';
                  const b = [...d.querySelectorAll('button')].find(x => /tout refuser|reject all/i.test(x.innerText || x.getAttribute('aria-label') || ''));
                  if (!b) return 'sans bouton'; b.click(); return 'refusée'; })()
                """);
            if (r == "\"refusée\"") { Host.Log("consentement : boîte dans la page, « Tout refuser »"); Veille.Noter("session", "consentement UE redemandé (boîte dans la page)"); await Task.Delay(1500); return; }
            if (r == "\"sans bouton\"") Veille.Noter("session", "consentement : boîte sans bouton « Tout refuser » reconnu");
            await Task.Delay(250);
        }
    }

    public async Task<bool> LireSessionAsync()
    {
        string r = await core.ExecuteScriptAsync("window.ytcfg && ytcfg.get ? !!ytcfg.get('LOGGED_IN') : null");
        bool avant = Connecte;
        Connecte = r == "true";
        Host.Log("session : " + (Connecte ? "connecté" : r == "null" ? "ytcfg absent" : "non connecté"));
        string marque = Path.Combine(YtDonnees.Racine, "session-ouverte");
        if (Connecte) File.WriteAllText(marque, DateTime.Now.ToString("s"));
        else if (File.Exists(marque)) { Veille.Noter("session", "session Google perdue (ouverte le " + File.ReadAllText(marque) + ")"); File.Delete(marque); }
        if (avant != Connecte) EtatChange?.Invoke(null);
        return Connecte;
    }

    // ---------- connexion Google : la page sort de sa cachette le temps de se connecter ----------

    public async Task ConnexionAsync()
    {
        if (enConnexion) { fenetre.Activate(); return; }
        await CacherLecteurAsync();
        enConnexion = true;
        var ecran = Screen.PrimaryScreen.WorkingArea;
        fenetre.Size = new Size(Math.Min(1000, ecran.Width - 80), Math.Min(760, ecran.Height - 80));
        fenetre.Location = new Point(ecran.X + (ecran.Width - fenetre.Width) / 2, ecran.Y + (ecran.Height - fenetre.Height) / 2);
        fenetre.ShowInTaskbar = true; fenetre.Text = "Connexion au compte Google — CDX-YouTube (fermer pour annuler)";
        fenetre.Activate();
        Host.Log("connexion : fenêtre de login Google affichée");
        core.Navigate("https://accounts.google.com/ServiceLogin?service=youtube&hl=fr&continue=" + Uri.EscapeDataString("https://www.youtube.com/signin?action_handle_signin=true&next=%2F"));
        await Task.CompletedTask;
    }

    async Task SuivreConnexionAsync()
    {
        string h = Hote(core.Source);
        if (h != "www.youtube.com") return;
        await Task.Delay(800);
        if (await LireSessionAsync()) await FinConnexionAsync(true);
    }

    public bool FenetreAffichee => fenetre.Left > -10000;
    public Task AnnulerConnexionAsync() => FinConnexionAsync(false);

    async Task FinConnexionAsync(bool reussie)
    {
        if (!enConnexion) return;
        enConnexion = false;
        if (!visible) { fenetre.Location = new Point(-20000, -20000); fenetre.ShowInTaskbar = false; }
        fenetre.Text = "YouTube (page cachée) — CDX-YouTube";
        Host.Log("connexion : " + (reussie ? "réussie, fenêtre recachée" : "annulée, fenêtre recachée"));
        if (!Hote(core.Source).EndsWith("youtube.com")) { try { await AllerAsync("https://www.youtube.com/"); } catch (Exception ex) { Host.Log("retour à YouTube : " + ex.Message); } }
        await ConsentementAsync();
        await LireSessionAsync();
        EtatChange?.Invoke(null);
        ConnexionTerminee?.Invoke();
    }

    public Task<Liste> RelireAccueilInitialAsync() => ListeInitialeAsync();

    // ---------- navigation interne et attente de la réponse youtubei ----------

    // Une demande en attente : les genres de réponse youtubei qui la satisfont, un filtre sur la réponse, et l'adresse de la page attendue (secours par la page).
    sealed class Attente
    {
        public string[] Genres; public Func<JsonElement, bool> Filtre; public Func<string, bool> Adresse;
        public TaskCompletionSource<(string json, string par)> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    readonly List<Attente> attentes = new();

    // Un « endpoint » de YouTube, tel qu'il figure sur ses liens, envoyé à l'application YouTube comme le fait un clic (évènement yt-navigate).
    async Task<bool> NaviguerAsync(object endpoint)
    {
        string js = "(() => { const app = document.querySelector('ytd-app'); if (!app) return 'ytd-app absent';"
            + "app.dispatchEvent(new CustomEvent('yt-navigate', { bubbles: true, composed: true, detail: { endpoint: " + JsonSerializer.Serialize(endpoint) + " } })); return 'ok'; })()";
        string r = await core.ExecuteScriptAsync(js);
        if (r != "\"ok\"") { Veille.Noter("extraction", "navigation interne impossible : " + r); return false; }
        return true;
    }

    // L'adresse et les données de la page affichée par YouTube (ytd-page-manager). YouTube garde certaines pages en cache (l'accueil, une page déjà vue) :
    // il les réaffiche sans aucune requête, et c'est le seul endroit où les lire.
    const string AdressePage = "(() => { const m = document.querySelector('ytd-page-manager'); const d = m && m.getCurrentData ? m.getCurrentData() : null; return d && d.response ? d.url : ''; })()";
    const string DonneesPage = "JSON.stringify(document.querySelector('ytd-page-manager').getCurrentData().response)";

    // Déclenche la navigation et attend : la réponse youtubei (côté C#, ou crochet fetch), ou à défaut la page affichée quand elle a l'adresse attendue.
    async Task<string> AttendreAsync(Attente a, Func<Task<bool>> declencher, int secondes = 15)
    {
        lock (attentes) attentes.Add(a);
        try
        {
            if (!await declencher()) return null;
            var fin = DateTime.UtcNow.AddSeconds(secondes);
            while (DateTime.UtcNow < fin)
            {
                if (await Task.WhenAny(a.Tcs.Task, Task.Delay(400)) == a.Tcs.Task) return a.Tcs.Task.Result.json;
                if (a.Adresse == null) continue;
                string url = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(AdressePage));
                if (string.IsNullOrEmpty(url) || !a.Adresse(url)) continue;
                // la page est arrivée ; la réponse réseau, s'il y en a une, arrive d'habitude avant : on lui laisse un instant
                if (await Task.WhenAny(a.Tcs.Task, Task.Delay(300)) == a.Tcs.Task) return a.Tcs.Task.Result.json;
                string json = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(DonneesPage));
                using var doc = JsonDocument.Parse(json);
                if (!a.Filtre(YtLecture.Normaliser(doc.RootElement))) continue;
                Recues["page"]++;
                Host.Log($"page {url} lue dans ytd-page-manager ({json.Length / 1024} Ko) : aucune requête youtubei (cache de YouTube)");
                return json;
            }
            Recues["echecs"]++;
            Veille.Noter("extraction", $"aucune réponse youtubei/v1/{string.Join("|", a.Genres)} ni page attendue en {secondes} s (mode {ModeExtraction})");
            // dernier secours : le crochet fetch, pour les demandes suivantes
            if (!crochetPose) { Host.Log("extraction : passage au crochet fetch"); await PoserCrochetAsync(); }
            return null;
        }
        finally { lock (attentes) attentes.Remove(a); }
    }

    public Task<Liste> AccueilAsync() => FilAsync("FEwhat_to_watch", "/", 3854, "accueil");
    public Task<Liste> AbonnementsAsync() => FilAsync("FEsubscriptions", "/feed/subscriptions", 96368, "abonnements");

    // Une page « browse » de YouTube (accueil, abonnements), comme un clic sur l'entrée du menu de gauche.
    async Task<Liste> FilAsync(string browseId, string url, int rootVe, string quoi)
    {
        var a = new Attente { Genres = new[] { "browse" }, Filtre = r => !EstSuite(r) && r.ToString().Contains(browseId), Adresse = u => u == url };
        string brut = await AttendreAsync(a, () => NaviguerAsync(new
        {
            commandMetadata = new { webCommandMetadata = new { url, webPageType = "WEB_PAGE_TYPE_BROWSE", rootVe, apiUrl = "/youtubei/v1/browse" } },
            browseEndpoint = new { browseId },
        }));
        return Liste(quoi, brut);
    }

    public async Task<Liste> RechercheAsync(string q)
    {
        string url = "/results?search_query=" + Uri.EscapeDataString(q).Replace("%20", "+");
        var a = new Attente
        {
            Genres = new[] { "search" }, Filtre = r => !EstSuite(r),
            Adresse = u => u.StartsWith("/results?") && System.Web.HttpUtility.ParseQueryString(u[9..])["search_query"] == q,
        };
        string brut = await AttendreAsync(a, () => NaviguerAsync(new
        {
            commandMetadata = new { webCommandMetadata = new { url, webPageType = "WEB_PAGE_TYPE_SEARCH", rootVe = 4724 } },
            searchEndpoint = new { query = q },
        }));
        return Liste("recherche", brut);
    }

    // Une vidéo, seule ou dans une liste (liste de lecture : elle est lue dans l'ordre de la liste ; mix « RD… » : YouTube l'enchaîne).
    public async Task<Fiche> VideoAsync(string id, string liste = null, int? index = null)
    {
        // la fiche vient de « next », ou de « get_watch » (lecteur + fiche en une seule réponse, vu le 02/11/2026) ; YtLecture.Normaliser fait le tri
        var a = new Attente { Genres = new[] { "next", "get_watch" }, Filtre = r => YtLecture.IdDeFiche(r) == id, Adresse = u => u.StartsWith("/watch?") && u.Contains("v=" + id) && (liste == null || u.Contains("list=" + liste)) };
        var we = new Dictionary<string, object> { ["videoId"] = id };
        string url = "/watch?v=" + id;
        if (liste != null) { we["playlistId"] = liste; url += "&list=" + liste; }
        if (liste != null && index != null) { we["index"] = index.Value; url += "&index=" + (index.Value + 1); }
        string brut = await AttendreAsync(a, () => NaviguerAsync(new
        {
            commandMetadata = new { webCommandMetadata = new { url, webPageType = "WEB_PAGE_TYPE_WATCH", rootVe = 3832 } },
            watchEndpoint = we,
        }));
        if (brut == null) return null;
        idFiche = id;
        using var doc = JsonDocument.Parse(brut);
        var f = YtLecture.LireFiche(YtLecture.Normaliser(doc.RootElement));
        if (f.Titre == null || f.Chaine == null) Veille.GarderBrut("fiche-" + id, brut);   // pour corriger YtLecture (cdx-youtube --lire)
        Host.Log($"fiche {id} : « {f.Titre} » [{f.Chaine}], description {f.Description?.Length ?? 0} car., {f.Suggestions.Count} suggestions, {f.PubsFiltrees} pubs filtrées");
        return f;
    }

    // Une chaîne, sur un onglet : par défaut « Vidéos » (params bien connus, la réponse contient de toute façon la liste des onglets avec les leurs).
    public async Task<(Chaine chaine, Liste liste)> ChaineAsync(string id, string url = null, string param = null)
    {
        url ??= "/channel/" + id + "/videos"; param ??= "EgZ2aWRlb3PyBgQKAjoA";
        string suffixe = url[url.LastIndexOf('/')..];
        pseudos.TryGetValue(id, out var pseudo);
        var a = new Attente
        {
            Genres = new[] { "browse" }, Filtre = r => !EstSuite(r) && YtLecture.IdDeChaine(r) == id,
            Adresse = u => u.StartsWith(url) || u.StartsWith("/channel/" + id + suffixe) || (pseudo != null && u.StartsWith("/" + pseudo + suffixe)),
        };
        var be = new Dictionary<string, object> { ["browseId"] = id };
        if (!string.IsNullOrEmpty(param)) be["params"] = param;
        string brut = await AttendreAsync(a, () => NaviguerAsync(new
        {
            commandMetadata = new { webCommandMetadata = new { url, webPageType = "WEB_PAGE_TYPE_CHANNEL", rootVe = 3611, apiUrl = "/youtubei/v1/browse" } },
            browseEndpoint = be,
        }));
        if (brut == null) return (null, null);
        using var doc = JsonDocument.Parse(brut);
        var c = YtLecture.LireChaine(doc.RootElement, id);
        if (c.Pseudo != null) pseudos[id] = c.Pseudo;
        var l = Liste("chaîne " + (c.Nom ?? id) + " " + suffixe, brut);
        return (c, l);
    }

    // La page d'une liste de lecture (browseId « VL » + identifiant de la liste).
    public async Task<(EnteteListe entete, Liste liste)> ListeAsync(string id)
    {
        var a = new Attente { Genres = new[] { "browse" }, Filtre = r => !EstSuite(r) && r.GetRawText().Contains("\"VL" + id + "\""), Adresse = u => u.StartsWith("/playlist?list=" + id) };
        string brut = await AttendreAsync(a, () => NaviguerAsync(new
        {
            commandMetadata = new { webCommandMetadata = new { url = "/playlist?list=" + id, webPageType = "WEB_PAGE_TYPE_PLAYLIST", rootVe = 5754, apiUrl = "/youtubei/v1/browse" } },
            browseEndpoint = new { browseId = "VL" + id },
        }));
        if (brut == null) return (null, null);
        using var doc = JsonDocument.Parse(brut);
        var e = YtLecture.LireEnteteListe(doc.RootElement, id);
        var l = Liste("liste " + id, brut);
        e.PremierId = l.Videos.FirstOrDefault(v => v.Genre == "video")?.Id;
        return (e, l);
    }

    // ---------- compte ----------

    // Le nom du compte connecté : un clic sur l'avatar de la page (comme l'utilisateur), YouTube demande son menu (account/account_menu), qu'on lit ; puis
    // le menu est refermé.
    public async Task LireCompteAsync()
    {
        if (!Connecte) { Compte = null; return; }
        var a = new Attente { Genres = new[] { "compte" }, Filtre = _ => true };
        string brut = await AttendreAsync(a, async () =>
            await core.ExecuteScriptAsync("(() => { const b = document.querySelector('#avatar-btn, ytd-topbar-menu-button-renderer button[aria-label]'); if (!b) return false; b.click(); return true; })()") == "true", 10);
        await core.ExecuteScriptAsync("(() => { document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true })); const d = document.querySelector('tp-yt-iron-dropdown:not([aria-hidden=true]) ytd-multi-page-menu-renderer'); if (d) document.querySelector('#avatar-btn')?.click(); })()");
        if (brut == null) { Host.Log("compte : menu de l'avatar sans réponse"); return; }
        using var doc = JsonDocument.Parse(brut);
        Compte = YtLecture.LireCompte(doc.RootElement);
        Host.Log("compte : " + (Compte ?? "nom illisible"));
    }

    // Déconnexion : les cookies du profil de la page cachée sont effacés (session Google comprise), puis YouTube rechargé, sans compte. uBOL et le reste du
    // profil restent. Le consentement UE est redemandé (et refusé seul).
    public async Task<Liste> DeconnexionAsync()
    {
        await CacherLecteurAsync();
        int avant = (await core.CookieManager.GetCookiesAsync("https://www.youtube.com")).Count;
        await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.Cookies);
        try { File.Delete(Path.Combine(YtDonnees.Racine, "session-ouverte")); } catch { }
        Compte = null; idFiche = null;
        await AllerAsync("https://www.youtube.com/");
        await ConsentementAsync();
        await LireSessionAsync();
        Host.Log($"déconnexion : {avant} cookies youtube.com effacés, session {(Connecte ? "TOUJOURS ouverte" : "fermée")}");
        return await ListeInitialeAsync();
    }

    public async Task<int> NombreDeCookiesAsync() => (await core.CookieManager.GetCookiesAsync("https://www.youtube.com")).Count;

    string idFiche;   // la vidéo dont la fiche est affichée (pour reconnaître un changement de vidéo fait par le lecteur lui-même)

    // ---------- le lecteur dans la fenêtre de l'interface ----------

    public bool LecteurAffiche { get; private set; }
    public Control HoteDuLecteur => web.Parent;
    public Rectangle BoundsDuLecteur => web.Bounds;

    // Pose le contrôle WebView2 de la page dans `hote` (la fenêtre de l'interface), sur `r` (pixels de la fenêtre), au-dessus de la page de l'interface.
    // La page n'est pas rechargée : le contrôle change seulement de fenêtre (comme les onglets de cdx-browser).
    public async Task MontrerLecteurAsync(Control hote, Rectangle r)
    {
        if (enConnexion || core == null) return;
        if (LecteurAffiche) { web.Bounds = r; return; }
        // La vue reste invisible le temps que le style « lecteur seul » soit posé et que YouTube ait redimensionné son lecteur : sans cela, des morceaux du
        // site YouTube passaient à l'écran une fraction de seconde (remarqué le 02/11/2026).
        LecteurAffiche = true;
        web.Visible = false;
        if (web.Parent != hote)
        {
            web.Parent?.Controls.Remove(web);
            web.Dock = DockStyle.None; web.Bounds = r;
            hote.Controls.Add(web);
        }
        web.Bounds = r; web.BringToFront();
        await ModeLecteurAsync(true);
        await Task.Delay(250);
        if (LecteurAffiche) { web.Visible = !recouvert; Host.Log($"lecteur affiché ({r.Width} × {r.Height})"); }
    }

    // Le lecteur quitte l'interface : en pause, la page reprend sa forme et retourne dans sa fenêtre cachée.
    public async Task CacherLecteurAsync()
    {
        if (!LecteurAffiche) return;
        LecteurAffiche = false;
        await PauseAsync();
        await ModeLecteurAsync(false);
        web.Parent?.Controls.Remove(web);
        web.Dock = DockStyle.Fill; web.Visible = true;
        fenetre.Controls.Add(web);
        Host.Log("lecteur recaché");
    }

    // Un menu de la fenêtre s'ouvre par-dessus : le lecteur (une vue native, toujours au-dessus de la page) s'efface le temps du menu.
    bool recouvert;
    public bool Recouvert => recouvert;
    public void Recouvrir(bool menuOuvert) { recouvert = menuOuvert; if (LecteurAffiche) web.Visible = !menuOuvert; }

    // En plein écran, la barre effacée : le curseur disparaît aussi au-dessus de la vidéo (le style « lecteur seul » le force sinon à la flèche).
    public async Task CurseurCacheAsync(bool cache)
    {
        string js = cache
            ? "(() => { if (!document.getElementById('cdx-curseur')) { const s = document.createElement('style'); s.id = 'cdx-curseur'; s.textContent = '#movie_player, #movie_player * { cursor: none !important; }'; document.documentElement.appendChild(s); } })()"
            : "(() => { const s = document.getElementById('cdx-curseur'); if (s) s.remove(); })()";
        try { await core.ExecuteScriptAsync(js); } catch { }
    }

    public void Placer(Rectangle r) { if (LecteurAffiche) web.Bounds = r; }

    // Le style « lecteur seul » : #movie_player occupe toute la vue, le reste de la page de YouTube est dessous. YouTube recalcule la taille de la vidéo
    // à l'évènement resize.
    const string StyleLecteur = """
        html, body { overflow: hidden !important; background: #000 !important; }
        /* La page YouTube entière est invisible, sauf le lecteur : à grande taille (plein écran), YouTube passe à sa mise en page large et ses colonnes (vidéos
           suggérées, chaîne, description) passaient par-dessus la vidéo (vu le 02/11/2026). La visibilité se transmet aux descendants : #movie_player la rétablit. */
        body { visibility: hidden !important; }
        #movie_player { visibility: visible !important; }
        /* Aucun ancêtre du lecteur ne doit lui servir de référentiel : sinon « position: fixed » le garde dans sa colonne au lieu de remplir la vue. */
        :has(> * #movie_player), :has(> #movie_player), ytd-app :has(#movie_player) { transform: none !important; contain: none !important; filter: none !important;
          will-change: auto !important; perspective: none !important; container-type: normal !important; backdrop-filter: none !important; }
        #movie_player { position: fixed !important; inset: 0 !important; width: 100vw !important; height: 100vh !important; z-index: 2147483000 !important; background: #000 !important; }
        #movie_player .html5-video-container, #movie_player video.html5-main-video { width: 100vw !important; height: 100vh !important; left: 0 !important; top: 0 !important; object-fit: contain !important; }
        ytd-masthead, #masthead-container, tp-yt-app-drawer, ytd-miniplayer { display: none !important; }
        /* toute l'interface du lecteur de YouTube : ce sont les commandes de CDX, sous la vidéo, qui le pilotent. Restent l'image, les sous-titres et le rond de chargement. */
        #movie_player .ytp-chrome-top, #movie_player .ytp-chrome-bottom, #movie_player .ytp-gradient-top, #movie_player .ytp-gradient-bottom,
        #movie_player .ytp-ce-element, #movie_player .ytp-endscreen-content, #movie_player .ytp-autonav-endscreen, #movie_player .ytp-autonav-endscreen-countdown-overlay,
        #movie_player .ytp-pause-overlay, #movie_player .ytp-cards-button, #movie_player .ytp-cards-teaser, #movie_player .ytp-paid-content-overlay,
        #movie_player .iv-branding, #movie_player .ytp-watermark, #movie_player .ytp-suggested-action, #movie_player .ytp-tooltip, #movie_player .ytp-bezel-text-wrapper,
        #movie_player .ytp-bezel, #movie_player .ytp-contextmenu, #movie_player .ytp-popup, #movie_player .ytp-impression-link, #movie_player .ytp-player-content,
        #movie_player .ytp-overlay, #movie_player .ytp-videowall-still, #movie_player .ytp-show-cards-title, #movie_player .branding-img-container,
        #movie_player .ytp-fullerscreen-edu-button, #movie_player .ytp-miniplayer-ui, #movie_player .ytp-storyboard-framepreview, #movie_player .ytp-doubletap-ui-legacy,
        #movie_player .ytp-featured-product, #movie_player .ytp-info-panel-preview, #movie_player .ytp-iv-player-content, #movie_player .annotation,
        #movie_player .ytp-fullscreen-grid, #movie_player .ytp-fullscreen-quick-actions, #movie_player .ytp-fullscreen-metadata, #movie_player .ytp-overlay-bottom-right,
        #movie_player .ytp-overlays-container, #movie_player .ytp-fullscreen-grid-peeking, #movie_player .ytp-title, #movie_player .ytp-chapter-container { display: none !important; }
        #movie_player .ytp-caption-window-container { display: block !important; }
        #movie_player { cursor: default !important; }
        """;

    async Task ModeLecteurAsync(bool on)
    {
        string js = "(() => { let s = document.getElementById('cdx-lecteur');"
            + (on ? "if (!s) { s = document.createElement('style'); s.id = 'cdx-lecteur'; s.textContent = " + JsonSerializer.Serialize(StyleLecteur) + "; document.documentElement.appendChild(s); }"
                  : "if (s) s.remove();")
            + "window.scrollTo(0, 0); window.dispatchEvent(new Event('resize')); setTimeout(() => window.dispatchEvent(new Event('resize')), 300); })()";
        try { await core.ExecuteScriptAsync(js); } catch (Exception ex) { Host.Log("mode lecteur : " + ex.Message); }
    }

    public async Task LireAsync()
    {
        try { await core.ExecuteScriptAsync("(() => { const p = document.getElementById('movie_player'); if (p && p.playVideo) p.playVideo(); })()"); } catch { }
    }

    public async Task PauseAsync()
    {
        try { await core.ExecuteScriptAsync("(() => { const p = document.getElementById('movie_player'); if (p && p.pauseVideo) p.pauseVideo(); const v = document.querySelector('video'); if (v) v.pause(); })()"); } catch { }
    }

    // L'état du lecteur, pour les commandes de l'interface et l'essai : position, durée, chargé, pause, état, volume, vitesse, qualité, sous-titres, place dans
    // une liste, pub en cours (classe ad-showing). Lu par l'API du lecteur de YouTube (#movie_player). Rend du JSON.
    public Task<string> EtatLecteurAsync() => core.ExecuteScriptAsync("""
        (() => { const p = document.getElementById('movie_player'), v = document.querySelector('#movie_player video') || document.querySelector('video');
          const f = (g, d) => { try { const x = g(); return x === undefined ? d : x; } catch (e) { return d; } };
          // includeAsr : les sous-titres automatiques aussi (sans lui, la liste est vide alors que YouTube en affiche)
          const pistes = f(() => (p.getOption('captions', 'tracklist', { includeAsr: true }) || []).map(x => ({ code: x.languageCode, nom: x.displayName || x.languageName || x.languageCode })), []);
          const piste = f(() => { const t = p.getOption('captions', 'track'); return t && t.languageCode ? t.languageCode : ''; }, '');
          const liste = f(() => p.getPlaylist(), null);
          return { id: new URLSearchParams(location.search).get('v'), t: v ? Math.round(v.currentTime * 10) / 10 : null, duree: v && isFinite(v.duration) ? Math.round(v.duration) : null,
            pause: v ? v.paused : null, etat: f(() => p.getPlayerState(), null), charge: f(() => Math.round(p.getVideoLoadedFraction() * 1000) / 1000, 0),
            pub: !!(p && p.classList.contains('ad-showing')), muet: f(() => p.isMuted(), v ? v.muted : null), volume: f(() => p.getVolume(), null),
            vitesse: f(() => p.getPlaybackRate(), 1), vitesses: f(() => p.getAvailablePlaybackRates(), [1]),
            qualite: f(() => p.getPlaybackQuality(), ''), qualites: f(() => p.getAvailableQualityLevels(), []),
            pistes, piste, dansListe: !!(liste && liste.length), place: f(() => p.getPlaylistIndex(), -1), taille: liste ? liste.length : 0,
            largeur: innerWidth, hauteur: innerHeight, lecteur: p ? Math.round(p.getBoundingClientRect().width) + ' x ' + Math.round(p.getBoundingClientRect().height) : null }; })()
        """);

    // Une commande des boutons de l'interface, passée à l'API du lecteur de YouTube. Les valeurs viennent de la page de l'interface : vérifiées par l'appelant.
    public async Task<string> CommandeAsync(string action, JsonElement valeur)
    {
        string v = valeur.ValueKind == JsonValueKind.Undefined ? "null" : valeur.GetRawText();
        string js = "((a, v) => { const p = document.getElementById('movie_player'); if (!p || !p.playVideo) return 'pas de lecteur';"
            + "switch (a) {"
            + " case 'basculer': if (p.getPlayerState() === 1 || p.getPlayerState() === 3) p.pauseVideo(); else p.playVideo(); break;"
            + " case 'lire': p.playVideo(); break;"
            + " case 'pause': p.pauseVideo(); break;"
            + " case 'aller': p.seekTo(Math.max(0, +v), true); break;"
            + " case 'avancer': p.seekTo(Math.max(0, p.getCurrentTime() + (+v)), true); break;"
            + " case 'volume': p.unMute(); p.setVolume(Math.max(0, Math.min(100, +v))); break;"
            + " case 'muet': if (v) p.mute(); else p.unMute(); break;"
            + " case 'vitesse': p.setPlaybackRate(+v); break;"
            + " case 'qualite': p.setPlaybackQualityRange(String(v), String(v)); break;"
            + " case 'soustitres': if (!v) { p.setOption('captions', 'track', {}); p.unloadModule('captions'); } else { p.loadModule('captions'); setTimeout(() => p.setOption('captions', 'track', { languageCode: String(v) }), 300); } break;"
            + " case 'suivante': p.nextVideo(); break;"
            + " case 'precedente': if (p.getCurrentTime() > 5) p.seekTo(0, true); else p.previousVideo(); break;"
            + " default: return 'commande inconnue'; }"
            + " return 'ok'; })(" + JsonSerializer.Serialize(action) + ", " + v + ")";
        try { return JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(js)); }
        catch (Exception ex) { Host.Log("commande du lecteur " + action + " : " + ex.Message); return "erreur"; }
    }

    // YouTube demande le plein écran (double-clic sur la vidéo, touche F) : on le lui refuse (il remplirait la vue sans nos commandes) et c'est notre plein écran
    // qui bascule ; Échap dans le lecteur le quitte.
    public async Task QuitterPleinEcranYouTubeAsync() { try { await core.ExecuteScriptAsync("document.fullscreenElement && document.exitFullscreen()"); } catch { } }
    public event Action<string> Touche;   // Échap, F11 frappées dans le lecteur

    // La suite d'une liste (accueil ou recherche) : on fait défiler la page cachée jusqu'en bas, YouTube demande la suite comme pour un vrai défilement.
    public async Task<Liste> PlusAsync(string genre)
    {
        var a = new Attente { Genres = new[] { genre }, Filtre = EstSuite };
        string brut = await AttendreAsync(a, async () =>
        {
            await core.ExecuteScriptAsync("window.scrollTo(0, document.documentElement.scrollHeight)");
            return true;
        });
        return Liste(genre == "browse" ? "accueil (suite)" : "recherche (suite)", brut);
    }

    // Une suite de liste : appendContinuationItemsAction dans onResponseReceivedActions / Commands / Endpoints. (Une première page de recherche a aussi
    // onResponseReceivedCommands, mais pour une commande publicitaire : la présence de la clé ne suffit pas.)
    static bool EstSuite(JsonElement r) => YtLecture.EstSuite(r);

    Liste Liste(string quoi, string brut)
    {
        if (brut == null) return null;
        using var doc = JsonDocument.Parse(brut);
        var l = YtLecture.LireListe(doc.RootElement);
        Host.Log($"{quoi} : {brut.Length / 1024} Ko, {l.Videos.Count} vidéos, suite {(l.Suite ? "oui" : "non")}, {l.PubsFiltrees} pubs filtrées");
        if (l.Videos.Count == 0 && l.Messages.Count == 0) Veille.GarderBrut(quoi.Split(' ')[0] + "-vide", brut);
        return l;
    }

    // ---------- réception des réponses ----------

    static string GenreDe(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Host != "www.youtube.com") return null;
        return u.AbsolutePath switch { "/youtubei/v1/browse" => "browse", "/youtubei/v1/search" => "search", "/youtubei/v1/next" => "next", "/youtubei/v1/get_watch" => "get_watch", "/youtubei/v1/account/account_menu" => "compte", _ => null };
    }

    // Côté C# : WebView2 nous montre chaque réponse ; on lit le corps de celles qui nous intéressent.
    async void RecuParCSharp(object s, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        if (Trace && e.Request.Uri.Contains("youtube.com/") && !Regex.IsMatch(e.Request.Uri, @"\.(js|css|png|jpg|webp|woff2?|svg|ico)(\?|$)|/s/|/generate_204|/api/stats|/log_event|/ptracking|/pagead"))
            Host.Log($"   trace {e.Request.Method} {e.Response.StatusCode} {Court(e.Request.Uri)}");
        string genre = GenreDe(e.Request.Uri);
        if (genre == null || e.Request.Method != "POST") return;
        try
        {
            using var st = await e.Response.GetContentAsync();
            if (st == null) { Recues["echecs"]++; Veille.Noter("extraction", $"GetContentAsync sans contenu pour {genre} (HTTP {e.Response.StatusCode})"); return; }
            using var rd = new StreamReader(st, Encoding.UTF8);
            string json = await rd.ReadToEndAsync();
            Recues["csharp"]++;
            if (Trace) Veille.GarderBrut("trace-" + genre, json);
            Distribuer(genre, json, "csharp");
        }
        catch (Exception ex) { Recues["echecs"]++; Veille.Noter("extraction", $"GetContentAsync a échoué pour {genre} : {ex.GetType().Name} {ex.Message}"); }
    }

    // Secours : le crochet fetch de la page renvoie { cdx: 'yt', url, json }.
    void RecuParFetch(CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var d = JsonDocument.Parse(e.WebMessageAsJson);
            if (!d.RootElement.TryGetProperty("cdx", out var c) || c.GetString() != "yt") return;
            string genre = GenreDe(d.RootElement.GetProperty("url").GetString());
            if (genre == null) return;
            Recues["fetch"]++;
            Distribuer(genre, d.RootElement.GetProperty("json").GetString(), "fetch");
        }
        catch (Exception ex) { Host.Log("crochet fetch : message illisible : " + ex.Message); }
    }

    // Une réponse arrive parfois deux fois (C# et crochet fetch) : la première complète l'attente, la seconde ne trouve plus personne.
    void Distribuer(string genre, string json, string par)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (Exception ex) { Veille.Noter("extraction", $"réponse {genre} illisible ({par}) : {ex.Message}"); Veille.GarderBrut(genre + "-illisible", json); return; }
        using (doc)
        {
            var r = YtLecture.Normaliser(doc.RootElement);
            bool servi = false;
            lock (attentes)
                foreach (var a in attentes.Where(a => a.Genres.Contains(genre) && !a.Tcs.Task.IsCompleted))
                    if (a.Filtre(r)) { a.Tcs.TrySetResult((json, par)); servi = true; }
            Host.Log($"réponse youtubei/v1/{genre} ({par}, {json.Length / 1024} Ko){(servi ? "" : " — non demandée")}");
            // le lecteur est peut-être passé seul à une autre vidéo : la fiche suivra si la page y va bien (la réponse arrive avant le changement d'adresse)
            if (!servi && genre is "next" or "get_watch" && YtLecture.IdDeFiche(r) is { } id && id != idFiche && idFiche != null)
                _ = ConfirmerFicheAsync(id, YtLecture.LireFiche(r));
            // une suite non demandée (la page a défilé d'elle-même) : on la transmet quand même
            if (!servi && genre is "browse" or "search" && EstSuite(r)) SuiteRecue?.Invoke(YtLecture.LireListe(r), genre);
        }
    }

    async Task ConfirmerFicheAsync(string id, Fiche f)
    {
        for (int i = 0; i < 15; i++)
        {
            if (core.Source.Contains("v=" + id))
            {
                if (id == idFiche) return;
                idFiche = id;
                Host.Log($"le lecteur est passé seul à {id} : « {f.Titre} »");
                FicheSpontanee?.Invoke(f);
                return;
            }
            await Task.Delay(200);
        }
    }

    // Le crochet fetch : posé dans la page en cours ET pour les prochains chargements. Il ne modifie rien : il lit une copie de la réponse.
    const string Crochet = """
        (() => {
          if (window.__cdxCrochet) return; window.__cdxCrochet = true;
          const f = window.fetch, re = /\/youtubei\/v1\/(browse|search|next|get_watch)(\?|$)/;
          window.fetch = function (input, init) {
            const p = f.apply(this, arguments);
            try {
              const url = typeof input === 'string' ? input : input && input.url;
              if (url && re.test(url) && window.chrome && chrome.webview)
                p.then(r => r.clone().text()).then(t => chrome.webview.postMessage({ cdx: 'yt', url: new URL(url, location.href).href, json: t })).catch(() => {});
            } catch (e) {}
            return p;
          };
        })();
        """;

    async Task PoserCrochetAsync()
    {
        if (crochetPose) return;
        crochetPose = true;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Crochet);
        if (core.Source?.StartsWith("https://www.youtube.com") == true) await core.ExecuteScriptAsync(Crochet);
        Host.Log("extraction : crochet fetch posé");
    }

    // ---------- outils ----------

    public async Task<string> ScriptAsync(string js) => await core.ExecuteScriptAsync(js);

    // --sonder : charge une adresse de YouTube (Navigate : mise au point seulement) et garde ses données sous brut\.
    public async Task SonderAsync(string chemin)
    {
        try
        {
            await AllerAsync("https://www.youtube.com" + (chemin.StartsWith('/') ? "" : "/") + chemin);
            string brut = "";
            for (int i = 0; i < 40 && brut.Length == 0; i++)
            {
                // les données de la page affichée (ytd-page-manager), sinon celles du HTML (ytInitialData)
                brut = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("(() => { try { const m = document.querySelector('ytd-page-manager'); const d = m && m.getCurrentData ? m.getCurrentData() : null; if (d && d.response) return JSON.stringify(d.response); } catch (e) {} return window.ytInitialData ? JSON.stringify(window.ytInitialData) : ''; })()")) ?? "";
                if (brut.Length == 0) await Task.Delay(250);
            }
            if (brut.Length == 0) Host.Log("sonde : " + await core.ExecuteScriptAsync("JSON.stringify({ titre: document.title, yid: typeof window.ytInitialData, pm: !!document.querySelector('ytd-page-manager'), app: !!document.querySelector('ytd-app') })"));
            string nom = "sonde-" + Regex.Replace(chemin, "[^A-Za-z0-9_-]+", "_").Trim('_');
            Veille.GarderBrut(nom, brut);
            Host.Log($"sonde {chemin} : {core.Source} ; ytInitialData {brut.Length / 1024} Ko → brut\\…-{nom}.json");
        }
        catch (Exception ex) { Host.Log($"sonde {chemin} : {ex.Message}"); }
    }

    // Pour l'essai : un script exécuté comme s'il venait d'un geste de l'utilisateur (protocole DevTools, userGesture) : un clic sur le bouton plein écran du
    // lecteur, sur un lien de YouTube… sans simuler la souris ni le clavier.
    public Task<string> GesteAsync(string js) => core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression = js, userGesture = true, awaitPromise = true }));
    public bool EnPleinEcran => core?.ContainsFullScreenElement == true;

    public async Task CaptureAsync(string nom)
    {
        Directory.CreateDirectory(YtDonnees.Captures);
        await using var f = File.Create(Path.Combine(YtDonnees.Captures, nom + ".png"));
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, f);
    }

    // Veille des pubs dans la page (encarts visibles malgré uBOL, avertissement anti-bloqueur) : relevé après chaque affichage.
    public async Task VeillePageAsync()
    {
        try
        {
            string r = await core.ExecuteScriptAsync("""
                (() => ({
                  encarts: [...document.querySelectorAll('ytd-ad-slot-renderer, ytd-in-feed-ad-layout-renderer, ytd-promoted-sparkles-web-renderer, #player-ads > *, ytd-banner-promo-renderer')].filter(x => x.offsetParent && x.offsetHeight > 0).length,
                  antiBloqueur: !!document.querySelector('ytd-enforcement-message-view-model'),
                  pubLecteur: !!document.querySelector('#movie_player.ad-showing'),
                  url: location.pathname
                }))()
                """);
            using var d = JsonDocument.Parse(r);
            var x = d.RootElement;
            if (x.GetProperty("encarts").GetInt32() > 0) Veille.Noter("pub", $"{x.GetProperty("encarts").GetInt32()} encart(s) publicitaire(s) visible(s) malgré uBOL sur {x.GetProperty("url").GetString()}");
            if (x.GetProperty("antiBloqueur").GetBoolean()) Veille.Noter("bloqueur", "avertissement anti-bloqueur de YouTube affiché sur " + x.GetProperty("url").GetString());
            if (x.GetProperty("pubLecteur").GetBoolean()) Veille.Noter("pub", "pub vidéo dans le lecteur (ad-showing) sur " + x.GetProperty("url").GetString());
        }
        catch (Exception ex) { Host.Log("veille de la page : " + ex.Message); }
    }

    public static bool Trace;
    static string Court(string u) { int i = u.IndexOf('?'); return i < 0 || i > 140 ? u[..Math.Min(u.Length, 140)] : u[..i] + "?…"; }

    public string ModeExtraction => crochetPose ? "crochet fetch" : "WebResourceResponseReceived (C#)";

    static string Hote(string u) => Uri.TryCreate(u, UriKind.Absolute, out var x) ? x.Host : "";
}
