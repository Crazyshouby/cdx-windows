using System.Drawing;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace Cdx;

// Le programme : la fenêtre de la charte (web/youtube/youtube.html) et la page YouTube cachée (PageCachee). La page de l'interface demande (accueil,
// abonnements, recherche, vidéo…), le programme fait faire le travail à la page cachée et renvoie le modèle de YtLecture. L'interface ne parle jamais à
// YouTube (seules les miniatures sont chargées directement depuis i.ytimg.com). Pour lire une vidéo, le lecteur de la page cachée vient se poser dans la
// fenêtre, sur le rectangle que la fiche lui réserve (message « yt.lecteur »).
sealed class YouTubeApp : ApplicationContext, ICdxApp, IPageApp
{
    public HostForm? ControlTarget => form;   // la page visée par eval et cmd du canal de test (CdxApp.Control)
    readonly Options o;
    readonly PageCachee page;
    readonly SemaphoreSlim uneALaFois = new(1, 1);   // la page cachée ne fait qu'une navigation à la fois
    readonly IDisposable? surveillance;   // null : thème imposé par --theme, pas de suivi de l'accent
    readonly string root;
    YouTubeForm? form;                 // null avant la première ouverture
    CoreWebView2Environment? env;
    string theme;
    bool demarre, pret, uiPrete, connexionProposee, pleinEcran;
    Liste? accueil;                    // null tant que la page cachée n'a pas démarré
    string vueBrowse = "accueil";            // la dernière page « browse » demandée (accueil ou abonnements), à qui reviennent ses suites
    Rectangle rectLecteur, boundsAvantPleinEcran;
    FormWindowState etatAvantPleinEcran;
    readonly System.Windows.Forms.Timer veilleLecture = new() { Interval = 5000 };
    readonly System.Windows.Forms.Timer suiviLecture = new() { Interval = 250 };   // l'état du lecteur, pour nos commandes
    string? dernierEtatLecture, dernierBlocage; bool suiviEnCours;
    // plein écran : la barre de commandes s'efface après un moment sans mouvement de la souris (pendant la lecture), et revient au moindre mouvement
    readonly System.Windows.Forms.Timer souris = new() { Interval = 200 };
    static readonly TimeSpan DelaiBarre = TimeSpan.FromSeconds(3);
    Point dernierCurseur; DateTime derniereActivite; bool barreVisible = true;
    int demande;
    public int Code;
    public Control Anchor { get; } = new Control();   // installe le contexte de synchronisation de l'interface et sert à y poser un travail

    public YouTubeApp(Options o)
    {
        this.o = o; root = Host.FindWebRoot();
        _ = Anchor.Handle;
        page = new PageCachee(o.Extraction, o.PageVisible, muet: o.Essai);
        theme = o.Theme ?? Suite.ReadAccent();
        if (o.Theme == null) surveillance = Suite.Watch(theme, a => { theme = a; Envoyer(new { type = "theme", value = a }); });
        page.EtatChange += _ => EnvoyerEtat();
        page.SuiteRecue += (l, g) => { if (uiPrete) Envoyer(new { type = "liste", vue = g == "browse" ? vueBrowse : "recherche", ajout = true, videos = l.Videos, suite = l.Suite }); };
        page.ConnexionTerminee += () => _ = ApresConnexionAsync();
        page.FicheSpontanee += f => Envoyer(new { type = "fiche", value = f, spontanee = true });
        // le plein écran demandé par YouTube (double-clic sur la vidéo, touche F) : refusé à YouTube, c'est le nôtre qui bascule
        page.PleinEcran += async on => { if (!on) return; await page.QuitterPleinEcranYouTubeAsync(); PleinEcran(!pleinEcran); };
        page.Touche += t => { if (t == "Echap" && pleinEcran) PleinEcran(false); else if (t == "F11") PleinEcran(!pleinEcran); };
        suiviLecture.Tick += async (_, _) =>
        {
            if (!page.LecteurAffiche || suiviEnCours || form?.Web.CoreWebView2 == null) return;
            suiviEnCours = true;
            try
            {
                string e = await page.EtatLecteurAsync();
                // (le lecteur recaché oublie son blocage : à son retour, l'état est relu même s'il n'a pas changé)
                if (e != dernierEtatLecture || page.Bloque != (dernierBlocage != null))
                {
                    dernierEtatLecture = e;
                    // un écran de YouTube à la place de la vidéo (âge, contenu sensible, vidéo indisponible) : la vue native s'efface, la fiche montre le message
                    using (var d = JsonDocument.Parse(e))
                    {
                        var b = d.RootElement.TryGetProperty("blocage", out var x) && x.ValueKind == JsonValueKind.Object ? x : default;
                        string? cle = b.ValueKind == JsonValueKind.Object ? b.GetRawText() : null;
                        if (cle != dernierBlocage || page.Bloque != (cle != null))
                        {
                            dernierBlocage = cle;
                            page.Bloquer(cle != null);
                            DisposerPleinEcran();
                            Host.Log(cle == null ? "écran de YouTube disparu : la vidéo est de nouveau visible"
                                : $"écran de YouTube à la place de la vidéo : {b.GetProperty("statut").GetString()} « {b.GetProperty("raison").GetString()} », boutons : {string.Join(", ", b.GetProperty("boutons").EnumerateArray().Select(y => y.GetString()))}");
                        }
                    }
                    form.Web.CoreWebView2.PostWebMessageAsJson("{\"type\":\"lecture\",\"value\":" + e + "}");
                }
            }
            catch (Exception ex) { Host.Log("suivi de la lecture : " + ex.Message); }
            finally { suiviEnCours = false; }
        };
        suiviLecture.Start();
        souris.Tick += (_, _) =>
        {
            if (!pleinEcran || form == null) return;
            var c = Cursor.Position;
            bool bouge = c != dernierCurseur; dernierCurseur = c;
            switch (DecisionBarre(c, form.Bounds, bouge, dernierEtatLecture?.Contains("\"pause\":false") == true, page.Recouvert, DateTime.UtcNow - derniereActivite))
            {
                case Barre.Activite: Activite(); break;
                case Barre.Effacer: if (barreVisible) _ = MontrerBarreAsync(false); break;
            }
        };
        souris.Start();
        // pendant la lecture : la veille des pubs et de l'avertissement anti-bloqueur, toutes les 5 s
        veilleLecture.Tick += async (_, _) => { if (page.LecteurAffiche) await page.VeillePageAsync(); };
        veilleLecture.Start();
    }

    public bool Closing { get; private set; }
    protected override void ExitThreadCore() { Closing = true; veilleLecture.Dispose(); suiviLecture.Dispose(); souris.Dispose(); surveillance?.Dispose(); page.Fermer(); base.ExitThreadCore(); }

    public enum Barre { Rien, Activite, Effacer }

    // En plein écran, que faire de la barre, d'après la souris (position à l'écran, a-t-elle bougé), l'état de la lecture et le temps depuis la dernière activité.
    //  - souris dans la fenêtre : un mouvement est une activité (la barre revient) ; sans mouvement depuis le délai, la barre s'efface pendant la lecture, sauf
    //    si la souris est sur la barre ou qu'un menu est ouvert ; en pause, elle reste ;
    //  - souris hors de la fenêtre (sur un autre écran, où l'on travaille pendant que la vidéo passe) : ses mouvements ne comptent pas, et la barre s'efface
    //    après le délai, même en pause (03/11/2026, demandé par Valentin).
    public static Barre DecisionBarre(Point curseur, Rectangle fenetre, bool bouge, bool enLecture, bool menuOuvert, TimeSpan inactivite)
    {
        bool dedans = fenetre.Contains(curseur);
        if (dedans && bouge) return Barre.Activite;
        if (inactivite <= DelaiBarre) return Barre.Rien;
        if (!dedans) return Barre.Effacer;
        bool surBarre = curseur.Y >= fenetre.Bottom - 40;
        return enLecture && !surBarre && !menuOuvert ? Barre.Effacer : Barre.Rien;
    }

    // Un mouvement de souris, une commande : la barre revient (en plein écran) et le délai repart.
    void Activite()
    {
        derniereActivite = DateTime.UtcNow;
        if (pleinEcran && !barreVisible) _ = MontrerBarreAsync(true);
    }

    async Task MontrerBarreAsync(bool on)
    {
        if (on == barreVisible) return;
        barreVisible = on;
        Envoyer(new { type = "barre", value = on });   // la page efface ou remet la barre
        DisposerPleinEcran();
        await page.CurseurCacheAsync(!on);
    }

    // Plein écran : la vidéo occupe toute la fenêtre, toujours ; la page de l'interface, réduite à une bande de BarrePx en bas (la platine), passe DEVANT la
    // vidéo quand la barre est visible et derrière quand elle s'efface (03/11/2026 : la barre ne doit plus réduire la vidéo). Un menu, un dialogue ou un
    // écran de YouTube à la place de la vidéo : la page reprend toute la fenêtre (la vidéo s'efface alors, comme hors plein écran).
    public const int BarrePx = 30;
    void DisposerPleinEcran()
    {
        if (form == null) return;
        var w = form.Web;
        if (!pleinEcran) { if (w.Dock != DockStyle.Fill) w.Dock = DockStyle.Fill; page.AuDessus(); return; }
        var cr = form.ClientRectangle;
        page.Placer(cr);
        if (page.Recouvert || page.Bloque) { w.Dock = DockStyle.Fill; w.BringToFront(); return; }
        w.Dock = DockStyle.None;
        w.Bounds = new Rectangle(0, cr.Height - BarrePx, cr.Width, BarrePx);
        if (barreVisible) w.BringToFront(); else page.AuDessus();
    }

    // Notre plein écran : la fenêtre couvre l'écran ; la page passe en disposition « plein écran » (la vidéo et nos commandes en bas) et renvoie le
    // rectangle du lecteur (yt.lecteur), qui suit.
    void PleinEcran(bool on)
    {
        if (form == null || on == pleinEcran) return;
        pleinEcran = on;
        barreVisible = true; derniereActivite = DateTime.UtcNow; dernierCurseur = Cursor.Position;
        if (!on) _ = page.CurseurCacheAsync(false);
        Envoyer(new { type = "pleinecran", value = on });   // d'abord la disposition de la page, puis la taille de la fenêtre
        if (on)
        {
            boundsAvantPleinEcran = form.Bounds; etatAvantPleinEcran = form.WindowState; form.WindowState = FormWindowState.Normal;
            form.Bounds = Screen.FromControl(form).Bounds; form.TopMost = true;
        }
        else { form.TopMost = false; form.Bounds = boundsAvantPleinEcran; form.WindowState = etatAvantPleinEcran; }
        DisposerPleinEcran();
        Host.Log("plein écran : " + (on ? "oui" : "non"));
    }

    // Le modèle (Video, Fiche) part en camelCase : { id, titre, chaine… }.
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true };
    void Envoyer(object m) { if (form?.Web.CoreWebView2 != null) form.Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(m, Json)); }

    // ---------- fenêtre ----------
    public async void Open()
    {
        try
        {
            if (Host.Reveal(form)) return;
            env ??= await CoreWebView2Environment.CreateAsync(null, YtDonnees.ProfilInterface, new CoreWebView2EnvironmentOptions("--disable-lcd-text"));
            var f = new YouTubeForm(this); form = f;
            f.FormClosed += (s, e) => ExitThread();
            double scale = f.DeviceDpi / 96.0;
            var zone = Screen.PrimaryScreen!.WorkingArea;   // « ! » : la fenêtre s'ouvre dans une session ouverte, Windows y a toujours un écran principal
            f.Bounds = new Rectangle(zone.X + 120, zone.Y + 60, (int)Math.Min(1280 * scale, zone.Width - 160), (int)Math.Min(860 * scale, zone.Height - 100));
            f.Show(); Host.ForceForeground(f.Handle);
            await f.InitWebAsync(env, root, "youtube/youtube.html?theme=" + theme, scale);
            Host.Log("YouTube ouvert");
            if (!demarre) { demarre = true; await DemarrerPageAsync(); }
        }
        catch (Exception ex) { Host.Log("YouTube : " + ex); if (o.Essai) { Code = 1; ExitThread(); } }
    }

    async Task DemarrerPageAsync()
    {
        try
        {
            accueil = await page.DemarrerAsync();
            await page.LireCompteAsync();
            pret = true;
            Host.Log($"page cachée prête : {(page.Connecte ? "connecté" : "non connecté")}, {page.Bloqueur ?? "SANS bloqueur"}");
            EnvoyerEtat();
            if (uiPrete) EnvoyerListe("accueil", null, accueil, false);
            await page.VeillePageAsync();
            // pas de session : la fenêtre de connexion Google, une fois par lancement (l'essai automatique ne la montre jamais d'elle-même)
            if (!page.Connecte && !o.Essai && o.Sonder.Count == 0 && !connexionProposee) { connexionProposee = true; await page.ConnexionAsync(); }
            if (o.Sonder.Count > 0) { foreach (var c in o.Sonder) await page.SonderAsync(c); Code = 0; ExitThread(); return; }
            if (o.Essai) await EssaiAsync();
        }
        catch (Exception ex)
        {
            Host.Log("ERREUR au démarrage de la page cachée : " + ex);
            Envoyer(new { type = "erreur", value = "Démarrage impossible : " + ex.Message });
            if (o.Essai) { Code = 1; ExitThread(); }
        }
    }

    async Task ApresConnexionAsync()
    {
        if (!page.Connecte) return;
        await uneALaFois.WaitAsync();
        try { await page.LireCompteAsync(); accueil = await page.RelireAccueilInitialAsync(); EnvoyerEtat(); Envoyer(new { type = "liste", vue = "accueil", reinit = true, videos = accueil.Videos, suite = accueil.Suite, messages = accueil.Messages }); }
        finally { uneALaFois.Release(); }
    }

    void EnvoyerEtat() => Envoyer(new
    {
        type = "etat",
        value = new { pret, connecte = page.Connecte, compte = page.Compte, bloqueur = page.Bloqueur, extraction = page.ModeExtraction, veille = Veille.Compte },
    });

    void EnvoyerListe(string vue, string? requete, Liste l, bool ajout) => Envoyer(new
    {
        type = "liste", vue, requete, ajout, videos = l.Videos, suite = l.Suite, messages = l.Messages, pubs = l.PubsFiltrees,
    });

    // ---------- bus et canal de test ----------

    // « show » : un second lancement (ou une autre application de la suite) demande à la première instance de passer devant.
    public bool OnBus(string type, string from, JsonElement data)
    {
        if (type != "show") return false;
        Open(); return true;
    }

    // Canal de test (debug.txt) : state · eval <js> (page de l'interface) · page <js> (page YouTube cachée) · sans-connexion (referme la fenêtre de connexion Google) · cmd <json>.
    public async Task<string> HandleControl(string line)
    {
        int sp = line.IndexOf(' '); string c = sp < 0 ? line : line[..sp], a = sp < 0 ? "" : line[(sp + 1)..];
        switch (c)
        {
            case "state": return (form == null ? "pas de fenêtre" : "fenêtre ouverte") + ", page cachée " + (pret ? "prête" : "en chargement") + ", " + (page.Connecte ? "connecté" : "non connecté") + ", " + (page.Bloqueur ?? "SANS bloqueur") + ", veille " + Veille.Compte;
            case "page": return pret ? await page.ScriptAsync(a) : "page cachée en chargement";
            case "sans-connexion": await page.AnnulerConnexionAsync(); return "fenêtre de connexion refermée";
            default: return "commande inconnue";
        }
    }

    // ---------- messages de la page de l'interface ----------

    public void OnMessage(HostForm from, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement.Clone();
            string cmd = r.GetProperty("cmd").GetString() ?? throw new FormatException("cmd null");   // rattrapé plus bas, comme l'était le déréférencement
            if (cmd == "win.overlay") { page.Recouvrir(r.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True); DisposerPleinEcran(); return; }
            if (cmd.StartsWith("win.")) { Host.WindowCommand(from, cmd, r); return; }
            if (Host.CommonMessage(from, cmd, r)) return;
            _ = MessageAsync(cmd, r);
        }
        catch (Exception ex) { Host.Log("message mal formé ou échec : " + ex.Message + " :: " + json); }
    }

    async Task MessageAsync(string cmd, JsonElement r)
    {
        string? Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        switch (cmd)
        {
            case "ready":
                uiPrete = true; EnvoyerEtat();
                if (pret && accueil != null) EnvoyerListe("accueil", null, accueil, false);
                return;
            case "yt.connexion": if (pret) await page.ConnexionAsync(); return;
            case "yt.lecteur":
            {
                // la fiche réserve un rectangle au lecteur (pixels CSS = pixels de la fenêtre : la page est à l'échelle 1/scale), ou n'en a plus
                bool vis = r.TryGetProperty("visible", out var v) && v.ValueKind == JsonValueKind.True;
                int I(string k) => r.TryGetProperty(k, out var x) && x.TryGetInt32(out var n) ? n : 0;
                if (!pret || form == null) return;
                if (vis && pleinEcran && r.TryGetProperty("plein", out var pl) && pl.ValueKind == JsonValueKind.True)
                {
                    // plein écran : la vidéo prend toute la fenêtre, la page n'est plus qu'une bande devant elle (DisposerPleinEcran)
                    if (!page.LecteurAffiche) await page.MontrerLecteurAsync(form, form.ClientRectangle);
                    DisposerPleinEcran();
                    return;
                }
                if (vis)
                {
                    // un rectangle minuscule est une mise en page transitoire (la fenêtre change de taille) : on garde le précédent
                    if (I("w") <= 40 || I("h") <= 40) return;
                    var rect = new Rectangle(I("x"), I("y"), I("w"), I("h"));
                    if (r.TryGetProperty("mini", out var mi) && mi.ValueKind == JsonValueKind.True)
                    {
                        // le coin de l'interface (on parcourt YouTube) : la vidéo continue dans le mini-lecteur de YouTube
                        if (!page.LecteurAffiche) return;
                        if (!page.Mini && !await page.EntrerMiniAsync()) { await page.CacherLecteurAsync(); Envoyer(new { type = "mini", value = false }); return; }
                        page.Placer(rect);
                        return;
                    }
                    if (page.Mini) await page.SortirMiniAsync();   // de retour sur la fiche de la vidéo
                    rectLecteur = rect;
                    await page.MontrerLecteurAsync(form, rectLecteur);
                }
                else { if (pleinEcran) PleinEcran(false); await page.CacherLecteurAsync(); }
                return;
            }
            case "yt.cmd":
            {
                // les commandes de notre lecteur : liste blanche, valeurs bornées
                string? a = Str("action");
                r.TryGetProperty("valeur", out var val);
                bool okv = a switch
                {
                    "basculer" or "lire" or "pause" or "suivante" or "precedente" => true,
                    "aller" or "avancer" or "volume" or "vitesse" => val.ValueKind == JsonValueKind.Number,
                    "muet" => val.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "qualite" => val.ValueKind == JsonValueKind.String && Regex.IsMatch(val.GetString()!, "^[a-z0-9]{2,12}$"),   // « ! » : une valeur JSON de type texte ne se lit jamais null
                    "soustitres" => val.ValueKind == JsonValueKind.String && Regex.IsMatch(val.GetString()!, "^[A-Za-z0-9_-]{0,20}$"),   // (de même)
                    "confirmer" => val.ValueKind == JsonValueKind.Number && val.TryGetInt32(out var nb) && nb is >= 0 and < 10,
                    _ => false,
                };
                Activite();
                if (okv && a == "confirmer")
                {
                    // un bouton de l'écran de YouTube (« Je comprends et je souhaite continuer ») : cliqué dans la page cachée, puis la lecture repart
                    if (!page.LecteurAffiche || !page.Bloque) return;
                    await page.ConfirmerAsync(val.GetInt32());
                    for (int i = 0; i < 20 && page.Bloque; i++) await Task.Delay(250);
                    await page.LireAsync();
                }
                else if (okv && page.LecteurAffiche) await page.CommandeAsync(a!, val);   // « ! » : okv n'est vrai que pour une action de la liste blanche
                else if (a == "pleinecran") PleinEcran(!pleinEcran);
                else Host.Log("commande du lecteur refusée : " + a);
                return;
            }
            case "yt.accueil": case "yt.abonnements": case "yt.recherche": case "yt.video": case "yt.plus": case "yt.chaine": case "yt.liste": case "yt.deconnexion": case "yt.changer": break;
            default: Host.Log("commande inconnue de la page : " + cmd); return;
        }
        if (!pret) { Envoyer(new { type = "occupe", value = "YouTube se charge…" }); return; }
        int n = ++demande;
        await uneALaFois.WaitAsync();
        try
        {
            if (n != demande && cmd != "yt.plus") return;   // une demande plus récente attend : celle-ci est dépassée
            // quitter la vidéo pour parcourir YouTube : le mini-lecteur d'abord, sinon la navigation déchargerait la vidéo
            if (cmd is "yt.accueil" or "yt.abonnements" or "yt.recherche" or "yt.chaine" or "yt.liste" && page.LecteurAffiche && !page.Mini)
            {
                bool m = await page.EntrerMiniAsync();
                if (!m) await page.CacherLecteurAsync();
                Envoyer(new { type = "mini", value = m });
            }
            switch (cmd)
            {
                case "yt.accueil":
                {
                    Envoyer(new { type = "occupe", value = "Chargement de l'accueil…" });
                    var l = await page.AccueilAsync();
                    if (l == null) { Erreur("L'accueil n'a pas répondu (voir youtube\\veille.log)."); break; }
                    accueil = l; vueBrowse = "accueil"; EnvoyerListe("accueil", null, l, false);
                    break;
                }
                case "yt.abonnements":
                {
                    Envoyer(new { type = "occupe", value = "Chargement des abonnements…" });
                    var l = await page.AbonnementsAsync();
                    if (l == null) { Erreur("Les abonnements n'ont pas répondu (voir youtube\\veille.log)."); break; }
                    vueBrowse = "abonnements"; EnvoyerListe("abonnements", null, l, false);
                    break;
                }
                case "yt.recherche":
                {
                    string q = (Str("q") ?? "").Trim(); if (q.Length == 0) break;
                    if (q.Length > 200) q = q[..200];
                    Envoyer(new { type = "occupe", value = "Recherche de « " + q + " »…" });
                    var l = await page.RechercheAsync(q);
                    if (l == null) { Erreur("La recherche n'a pas répondu (voir youtube\\veille.log)."); break; }
                    EnvoyerListe("recherche", q, l, false);
                    break;
                }
                case "yt.video":
                {
                    string? id = Str("id"), liste = Str("liste");
                    if (id == null || !Regex.IsMatch(id, "^[A-Za-z0-9_-]{11}$")) break;   // un identifiant de vidéo, rien d'autre
                    if (liste != null && !Regex.IsMatch(liste, "^[A-Za-z0-9_-]{2,80}$")) liste = null;
                    int? index = liste != null && r.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var ixn) && ixn >= 0 ? ixn : null;
                    Envoyer(new { type = "occupe", value = liste == null ? "Ouverture de la vidéo…" : "Ouverture de la vidéo dans la liste…" });
                    if (page.Mini) { await page.SortirMiniAsync(); Envoyer(new { type = "mini", value = false }); }   // une autre vidéo : le mini-lecteur se referme
                    var f = await page.VideoAsync(id, liste, index);
                    if (f == null) { Erreur("La vidéo n'a pas répondu (voir youtube\\veille.log)."); break; }
                    Envoyer(new { type = "fiche", value = f });
                    await page.LireAsync();   // la lecture démarre d'elle-même d'habitude ; sinon, comme un clic sur lecture
                    break;
                }
                case "yt.chaine":
                {
                    string? id = Str("id"), url = Str("url"), prm = Str("params");
                    if (id == null || !Regex.IsMatch(id, "^UC[A-Za-z0-9_-]{22}$")) break;   // un identifiant de chaîne
                    if (url != null && !Regex.IsMatch(url, "^/(@[^/?#]+|channel/UC[A-Za-z0-9_-]{22})/[a-z]+$")) url = null;   // un onglet de cette chaîne, rien d'autre
                    if (prm != null && !Regex.IsMatch(prm, "^[A-Za-z0-9_%=-]{4,200}$")) prm = null;
                    Envoyer(new { type = "occupe", value = "Ouverture de la chaîne…" });
                    var (c, l) = await page.ChaineAsync(id, url, url == null ? null : prm ?? "");
                    if (l == null) { Erreur("La chaîne n'a pas répondu (voir youtube\\veille.log)."); break; }
                    vueBrowse = "chaine";
                    Envoyer(new { type = "liste", vue = "chaine", chaine = c, videos = l.Videos, suite = l.Suite, messages = l.Messages, pubs = l.PubsFiltrees });
                    break;
                }
                case "yt.liste":
                {
                    string? id = Str("id");
                    if (id == null || !Regex.IsMatch(id, "^[A-Za-z0-9_-]{10,80}$")) break;
                    Envoyer(new { type = "occupe", value = "Ouverture de la liste…" });
                    var (e, l) = await page.ListeAsync(id);
                    if (l == null) { Erreur("La liste n'a pas répondu (voir youtube\\veille.log)."); break; }
                    vueBrowse = "liste";
                    Envoyer(new { type = "liste", vue = "liste", entete = e, videos = l.Videos, suite = l.Suite, messages = l.Messages, pubs = l.PubsFiltrees });
                    break;
                }
                case "yt.deconnexion":
                case "yt.changer":
                {
                    Envoyer(new { type = "occupe", value = "Déconnexion…" });
                    accueil = await page.DeconnexionAsync();
                    vueBrowse = "accueil";
                    EnvoyerEtat();
                    Envoyer(new { type = "liste", vue = "accueil", reinit = true, videos = accueil.Videos, suite = accueil.Suite, messages = accueil.Messages });
                    if (cmd == "yt.changer") await page.ConnexionAsync();   // changer de compte : la fenêtre de connexion Google, pour l'autre compte
                    break;
                }
                case "yt.plus":
                {
                    string vue = Str("vue") is { } sv && sv is "recherche" or "abonnements" or "chaine" or "liste" ? sv : "accueil";
                    var l = await page.PlusAsync(vue == "recherche" ? "search" : "browse");
                    Envoyer(new { type = "liste", vue, ajout = true, videos = l?.Videos ?? new List<Video>(), suite = l?.Suite ?? false });
                    break;
                }
            }
            await page.VeillePageAsync();
            EnvoyerEtat();
        }
        catch (Exception ex) { Host.Log($"{cmd} : {ex}"); Erreur(ex.Message); }
        finally { uneALaFois.Release(); }
    }

    void Erreur(string t) => Envoyer(new { type = "erreur", value = t });

    // Pour l'essai : ce qui est au premier plan de la page YouTube (sondage en grille), hors image et sous-titres, et la place du lecteur dans la vue.
    const string VisiblesDuLecteur = """
        (() => { const out = new Map();
          for (let gx = 1; gx < 16; gx++) for (let gy = 1; gy < 10; gy++) {
            const x = innerWidth * gx / 16, y = innerHeight * gy / 10;
            const e = document.elementFromPoint(x, y); if (!e) continue;
            if (e.closest('.html5-video-container, .ytp-caption-window-container') || e.id === 'movie_player' || e.classList.contains('html5-video-player')) continue;
            const nom = e.tagName.toLowerCase() + (e.id ? '#' + e.id : '') + (typeof e.className === 'string' && e.className ? '.' + e.className.trim().split(/\s+/).slice(0, 3).join('.') : '');
            const anc = []; let a = e.parentElement; while (a && anc.length < 4) { anc.push(a.tagName.toLowerCase() + (a.id ? '#' + a.id : '')); a = a.parentElement; }
            if (!out.has(nom)) out.set(nom, nom + ' (' + Math.round(x) + ',' + Math.round(y) + ' ; dans ' + anc.join(' < ') + ')' + (e.innerText ? ' « ' + e.innerText.slice(0, 40).replace(/\s+/g, ' ') + ' »' : ''));
          }
          const r = document.getElementById('movie_player').getBoundingClientRect();
          return ([...out.values()].join(' | ') || 'aucun (seulement la vidéo)') + ' ; lecteur ' + Math.round(r.left) + ',' + Math.round(r.top) + ' ' + Math.round(r.width) + 'x' + Math.round(r.height) + ' dans une vue de ' + innerWidth + 'x' + innerHeight; })()
        """;

    // La fenêtre entière telle qu'elle est à l'écran, lecteur compris (une vue native : la capture de la page de l'interface ne le contient pas).
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    void CaptureFenetre(string nom)
    {
        if (form == null) return;   // (appelé par l'essai, fenêtre ouverte)
        try
        {
            using var bmp = new Bitmap(form.Width, form.Height);
            using (var g = Graphics.FromImage(bmp)) { var hdc = g.GetHdc(); PrintWindow(form.Handle, hdc, 2 /* PW_RENDERFULLCONTENT */); g.ReleaseHdc(hdc); }
            Directory.CreateDirectory(YtDonnees.Captures);
            bmp.Save(Path.Combine(YtDonnees.Captures, "ui-" + nom + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex) { Host.Log("capture de la fenêtre : " + ex.Message); }
    }

    // ---------- essai automatique (--essai) : le parcours complet, sans saisie ; résultat dans journal-youtube.log, captures sous youtube\captures ----------

    async Task EssaiAsync()
    {
        var form = this.form!;   // « ! » : l'essai part de DemarrerPageAsync, qu'Open appelle après avoir créé la fenêtre (qui ne change plus ensuite)
        int echecs = 0;
        void Verifier(bool ok, string quoi) { Host.Log((ok ? "OK      " : "ÉCHEC   ") + quoi); if (!ok) echecs++; }
        async Task Capture(string nom)
        {
            await Task.Delay(1500);
            Directory.CreateDirectory(YtDonnees.Captures);
            await using (var f = File.Create(Path.Combine(YtDonnees.Captures, "ui-" + nom + ".png"))) await form.Web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, f);
            await page.CaptureAsync("page-" + nom);
        }
        async Task<JsonElement> Etat() => JsonDocument.Parse(JsonSerializer.Deserialize<string>(await form.Web.CoreWebView2.ExecuteScriptAsync("JSON.stringify(window.__etat ? window.__etat() : null)")) ?? throw new InvalidOperationException("état de la page illisible")).RootElement;   // (une exception, comme avant : l'essai la note en échec)
        // Une demande faite comme la page la fait ; on attend la réponse (liste, fiche) ou une erreur. Rend vrai si une réponse est arrivée.
        async Task<bool> Demander(object m, int s = 30)
        {
            int avant = (await Etat()).GetProperty("recu").GetInt32();
            await form.Web.CoreWebView2.ExecuteScriptAsync("window.__demander(" + JsonSerializer.Serialize(m) + ")");
            for (int i = 0; i < s * 4; i++)
            {
                await Task.Delay(250);
                var e = await Etat();
                if (e.GetProperty("recu").GetInt32() > avant) return true;
                if (e.GetProperty("erreur").ValueKind == JsonValueKind.String) { Host.Log("   erreur affichée : " + e.GetProperty("erreur").GetString()); return false; }
            }
            Host.Log("   rien reçu en " + s + " s");
            return false;
        }
        try
        {
            for (int i = 0; i < 40 && !uiPrete; i++) await Task.Delay(250);
            Verifier(uiPrete, "interface chargée");
            Verifier(page.Bloqueur != null, "uBOL : " + (page.Bloqueur ?? "absent"));
            Host.Log("session : " + (page.Connecte ? "connecté" : "non connecté"));
            var accueil = this.accueil!;   // « ! » : DemarrerPageAsync l'a lu juste avant d'appeler l'essai (et l'essai ne demande pas l'accueil avant la dernière lecture ci-dessous)
            foreach (var v in accueil.Videos.Take(5)) Host.Log($"   accueil · {v.Titre} [{v.Chaine}] {v.Duree} · {v.Vues} · {v.Date}");
            Verifier(!page.Connecte || accueil.Videos.Count > 0, $"accueil initial (ytInitialData) : {accueil.Videos.Count} vidéos");
            await Capture("1-accueil");

            bool ok = await Demander(new { cmd = "yt.recherche", q = o.Requete });
            var et = await Etat();
            Verifier(ok && et.GetProperty("vue").GetString() == "recherche" && et.GetProperty("n").GetInt32() > 0, $"recherche « {o.Requete} » par navigation interne : {et.GetProperty("n").GetInt32()} vidéos affichées");
            await Capture("2-recherche");

            if (ok)
            {
                ok = await Demander(new { cmd = "yt.plus", vue = "recherche" }, 20);
                var et2 = await Etat();
                Verifier(ok && et2.GetProperty("n").GetInt32() > et.GetProperty("n").GetInt32(), $"suite de la recherche (défilement de la page cachée) : {et.GetProperty("n").GetInt32()} → {et2.GetProperty("n").GetInt32()} vidéos");
            }

            string? premier = o.Video ?? et.GetProperty("premier").GetString() ?? accueil.Videos.FirstOrDefault()?.Id;
            string? chaineFiche = null;
            if (premier != null)
            {
                ok = await Demander(new { cmd = "yt.video", id = premier });
                var ev = await Etat();
                Verifier(ok && ev.GetProperty("vue").GetString() == "video" && ev.GetProperty("id").GetString() == premier && ev.GetProperty("titre").GetString() is { Length: > 0 },
                    $"fiche de {premier} : « {ev.GetProperty("titre").GetString()} », description {ev.GetProperty("description").GetInt32()} car.");
                Verifier(ok && ev.GetProperty("suggestions").GetInt32() > 0, $"suggestions de la fiche : {ev.GetProperty("suggestions").GetInt32()}");
                chaineFiche = ev.GetProperty("chaineId").GetString();
                // étape 3 : le lecteur de la page cachée est posé dans la fenêtre, sur le rectangle de la fiche, et lit la vidéo (sans le son pendant l'essai)
                for (int i = 0; i < 20 && !page.LecteurAffiche; i++) await Task.Delay(250);
                var rect = await Etat();
                Verifier(page.LecteurAffiche && page.HoteDuLecteur == form, "lecteur posé dans la fenêtre de l'interface : " + page.BoundsDuLecteur + " ; rectangle de la fiche : " + rect.GetProperty("lecteur").GetRawText());
                var l1 = JsonDocument.Parse(await page.EtatLecteurAsync()).RootElement;
                await Task.Delay(4000);
                var l2 = JsonDocument.Parse(await page.EtatLecteurAsync()).RootElement;
                double T(JsonElement e) => e.GetProperty("t").ValueKind == JsonValueKind.Number ? e.GetProperty("t").GetDouble() : -1;
                Verifier(l2.GetProperty("pause").ValueKind == JsonValueKind.False && T(l2) > T(l1) + 1, $"lecture en cours : {T(l1)} s → {T(l2)} s en 4 s, lecteur {l2.GetProperty("lecteur").GetString()} dans une vue de {l2.GetProperty("largeur")} × {l2.GetProperty("hauteur")}");
                Verifier(!l2.GetProperty("pub").GetBoolean(), "pas de pub dans le lecteur (ad-showing)");
                if (o.SondeMini)
                {
                    // la page cachée part vers une recherche (navigation interne de YouTube) pendant que la vidéo joue : continue-t-elle, et où est son lecteur ?
                    const string Ou = "JSON.stringify({ chemin: location.pathname + location.search, mini: !!document.querySelector('ytd-miniplayer[active], ytd-app[miniplayer-is-active]'), dansMini: !!(document.getElementById('movie_player') && document.getElementById('movie_player').closest('ytd-miniplayer')), video: !!document.querySelector('video'), lecteurs: document.querySelectorAll('#movie_player').length })";
                    Host.Log("sonde mini : avant : " + await page.ScriptAsync(Ou) + " ; " + await page.EtatLecteurAsync());
                    // la touche « i » de YouTube (son mini-lecteur), en vraie frappe (CDP), la page ayant le focus
                    await page.ScriptAsync("document.activeElement && document.activeElement.blur && document.activeElement.blur()");
                    await page.CdpAsync("Input.dispatchKeyEvent", new { type = "keyDown", key = "i", code = "KeyI", windowsVirtualKeyCode = 73, nativeVirtualKeyCode = 73, text = "i" });
                    await page.CdpAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = "i", code = "KeyI", windowsVirtualKeyCode = 73, nativeVirtualKeyCode = 73 });
                    await Task.Delay(1500);
                    Host.Log("sonde mini : après « i » : " + await page.ScriptAsync(Ou) + " ; " + await page.EtatLecteurAsync());
                    var lr = await page.RechercheAsync("musique classique");
                    Host.Log($"sonde mini : recherche faite ({lr?.Videos.Count} vidéos)");
                    for (int k = 0; k < 3; k++) { await Task.Delay(2000); Host.Log($"sonde mini : +{2 * (k + 1)} s : " + await page.ScriptAsync(Ou) + " ; " + await page.EtatLecteurAsync()); }
                    var la = await page.AccueilAsync();
                    Host.Log($"sonde mini : accueil fait ({la?.Videos.Count} vidéos)");
                    await Task.Delay(2000); Host.Log("sonde mini : +2 s : " + await page.ScriptAsync(Ou) + " ; " + await page.EtatLecteurAsync());
                    Code = 0; ExitThread(); return;
                }
                await Capture("3-video");
                CaptureFenetre("3-video-fenetre");

                string? sugg = ev.GetProperty("premiereSuggestion").GetString();
                if (sugg != null)
                {
                    ok = await Demander(new { cmd = "yt.video", id = sugg });
                    Verifier(ok && (await Etat()).GetProperty("id").GetString() == sugg, "fiche d'une suggestion (deuxième navigation interne vers une vidéo) : « " + (await Etat()).GetProperty("titre").GetString() + " »");
                    // jusqu'à 10 s pour démarrer (un démarrage lent arrive : voir l'étape 1) ; la durée d'attente est notée, c'est une mesure
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    JsonElement l3 = default;
                    for (int i = 0; i < 40; i++)
                    {
                        l3 = JsonDocument.Parse(await page.EtatLecteurAsync()).RootElement;
                        if (l3.GetProperty("id").GetString() == sugg && T(l3) > 0.5 && l3.GetProperty("pause").ValueKind == JsonValueKind.False) break;
                        await Task.Delay(250);
                    }
                    Verifier(page.LecteurAffiche && l3.GetProperty("id").GetString() == sugg && T(l3) > 0.5 && l3.GetProperty("pause").ValueKind == JsonValueKind.False,
                        $"la suggestion se lit dans le même lecteur (démarrage en {sw.ElapsedMilliseconds / 1000.0:0.0} s) : {l3.GetRawText()}");

                    // ---- notre lecteur : l'interface de YouTube est masquée, nos commandes le pilotent ----
                    string chrome = await page.ScriptAsync("JSON.stringify(['.ytp-chrome-bottom', '.ytp-chrome-top', '.ytp-gradient-bottom', '.ytp-ce-element'].map(q => { const e = document.querySelector('#movie_player ' + q); return q + ':' + (e ? getComputedStyle(e).display : 'absent'); }))");
                    Verifier(!chrome.Contains(":block") && !chrome.Contains(":flex"), "interface du lecteur de YouTube masquée : " + chrome);
                    async Task<JsonElement> L() => JsonDocument.Parse(await page.EtatLecteurAsync()).RootElement;
                    async Task Cmd(object m) { await form.Web.CoreWebView2.ExecuteScriptAsync("cdx.send('yt.cmd', " + JsonSerializer.Serialize(m) + ")"); await Task.Delay(800); }
                    var ea0 = await Etat();
                    Verifier(ea0.GetProperty("commandes").GetBoolean() && ea0.GetProperty("affichage").GetProperty("max").GetInt32() > 0 && ea0.GetProperty("affichage").GetProperty("boutons").GetInt32() == 6,
                        "nos commandes affichées (6 boutons, plus de listes déroulantes) : " + ea0.GetProperty("affichage").GetRawText());

                    // le bouton « Masquer la colonne » : la colonne de droite disparaît, la vidéo s'agrandit ; un second clic la remet
                    int largeurAvant = page.BoundsDuLecteur.Width;
                    await form.Web.CoreWebView2.ExecuteScriptAsync("document.getElementById('bCote').click()");
                    for (int i = 0; i < 12 && page.BoundsDuLecteur.Width <= largeurAvant; i++) await Task.Delay(250);
                    int largeurSans = page.BoundsDuLecteur.Width;
                    bool coteCachee = await form.Web.CoreWebView2.ExecuteScriptAsync("getComputedStyle(document.querySelector('.cote')).display") == "\"none\"";
                    await form.Web.CoreWebView2.ExecuteScriptAsync("document.getElementById('bCote').click()");
                    for (int i = 0; i < 12 && page.BoundsDuLecteur.Width != largeurAvant; i++) await Task.Delay(250);
                    Verifier(coteCachee && largeurSans > largeurAvant && page.BoundsDuLecteur.Width == largeurAvant,
                        $"colonne de droite masquée puis remise : vidéo {largeurAvant} → {largeurSans} → {page.BoundsDuLecteur.Width} px de large");

                    // le menu de l'engrenage : vitesse, qualité, sous-titres ; ouvert sous la barre, il ne masque pas la vidéo ; une vitesse choisie dans son sous-menu
                    string Clic(string texte) => "(() => { const i = [...document.querySelectorAll('.menu[data-cdx] .menu-item')].reverse().find(x => x.innerText.trim().startsWith(" + JsonSerializer.Serialize(texte) + ")); if (!i) return false; i.click(); return true; })()";
                    await form.Web.CoreWebView2.ExecuteScriptAsync("document.getElementById('cReglages').click()");
                    await Task.Delay(300);
                    var em = await Etat();
                    var entrees = em.GetProperty("menu").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                    Verifier(entrees.Count(x => x.StartsWith("Vitesse") || x.StartsWith("Qualité") || x.StartsWith("Sous-titres")) == 3 && !em.GetProperty("recouvert").GetBoolean() && page.LecteurAffiche,
                        "menu de l'engrenage : " + string.Join(" / ", entrees) + (em.GetProperty("recouvert").GetBoolean() ? " (vidéo masquée)" : " (vidéo restée visible)"));
                    await form.Web.CoreWebView2.ExecuteScriptAsync(Clic("Vitesse"));
                    await Task.Delay(300);
                    bool choisi = await form.Web.CoreWebView2.ExecuteScriptAsync(Clic("1,5×")) == "true";
                    await Task.Delay(800);
                    Verifier(choisi && (await L()).GetProperty("vitesse").GetDouble() == 1.5 && (await Etat()).GetProperty("menu").GetArrayLength() == 0, "vitesse 1,5× choisie dans le sous-menu, menu refermé");
                    await Cmd(new { action = "vitesse", valeur = 1 });
                    await Cmd(new { action = "pause" });
                    Verifier((await L()).GetProperty("pause").ValueKind == JsonValueKind.True, "commande pause");
                    await Cmd(new { action = "aller", valeur = 60 });
                    double t60 = (await L()).GetProperty("t").GetDouble();
                    Verifier(Math.Abs(t60 - 60) < 3, $"commande position : aller à 60 s → {t60} s");
                    await Cmd(new { action = "basculer" });
                    Verifier((await L()).GetProperty("pause").ValueKind == JsonValueKind.False, "commande lecture (basculer)");
                    await Cmd(new { action = "volume", valeur = 35 });
                    Verifier((await L()).GetProperty("volume").GetInt32() == 35 && (await Etat()).GetProperty("affichage").GetProperty("vol").GetInt32() == 35, "commande volume : 35, curseur à jour");
                    await Cmd(new { action = "volume", valeur = 100 });
                    await Cmd(new { action = "vitesse", valeur = 1.5 });
                    Verifier((await L()).GetProperty("vitesse").GetDouble() == 1.5, "commande vitesse : 1,5×");
                    await Cmd(new { action = "vitesse", valeur = 1 });
                    var lq = await L();
                    var qualites = lq.GetProperty("qualites").EnumerateArray().Select(x => x.GetString()).ToList();
                    Verifier(qualites.Count >= 2, "qualités proposées : " + string.Join(", ", qualites) + " (actuelle " + lq.GetProperty("qualite").GetString() + ")");
                    string? basse = qualites.LastOrDefault(x => x != "auto");
                    if (basse != null)
                    {
                        await Cmd(new { action = "qualite", valeur = basse });
                        await Task.Delay(2000);
                        Host.Log($"   qualité demandée {basse} → lecteur en {(await L()).GetProperty("qualite").GetString()} (mesure, la qualité peut mettre du temps à changer)");
                        await Cmd(new { action = "qualite", valeur = "auto" });
                    }
                    var pistes = lq.GetProperty("pistes").EnumerateArray().ToList();
                    if (pistes.Count > 0)
                    {
                        string? code = pistes[0].GetProperty("code").GetString();
                        await Cmd(new { action = "soustitres", valeur = code });
                        await Task.Delay(1200);
                        Verifier((await L()).GetProperty("piste").GetString() == code, $"sous-titres « {code} » : piste active {(await L()).GetProperty("piste").GetString()}");
                        await Cmd(new { action = "soustitres", valeur = "" });
                    }
                    else Host.Log("   cette vidéo n'a pas de sous-titres proposés");

                    // ---- notre plein écran : demandé par nos commandes, puis par YouTube (double-clic, touche F : ici le bouton masqué, cliqué avec un geste) ----
                    var avant = form.Bounds;
                    var ecran = Screen.FromControl(form).Bounds;
                    await Cmd(new { action = "pleinecran" });
                    for (int i = 0; i < 12 && page.BoundsDuLecteur.Width != ecran.Width; i++) await Task.Delay(250);
                    await Task.Delay(300);
                    bool Devant() => form.Controls.GetChildIndex(form.Web) < form.Controls.GetChildIndex(page.VueDuLecteur);   // l'indice 0 est au premier plan
                    bool Bande() => form.Web.Dock == DockStyle.None && form.Web.Bounds == new Rectangle(0, form.ClientSize.Height - BarrePx, form.ClientSize.Width, BarrePx);
                    Verifier(pleinEcran && form.Bounds == ecran && page.BoundsDuLecteur.Size == form.ClientSize && Bande() && Devant() && (await Etat()).GetProperty("plein").GetBoolean(),
                        $"plein écran (nos commandes) : fenêtre {form.Bounds} (écran {ecran}), vidéo {page.BoundsDuLecteur} sur toute la fenêtre, nos commandes devant elle ({form.Web.Bounds}, devant : {Devant()}) ; la page : {(await Etat()).GetProperty("lecteur").GetRawText()}, plein {(await Etat()).GetProperty("plein")}, vue {await form.Web.CoreWebView2.ExecuteScriptAsync("innerWidth + ' x ' + innerHeight")}, style {await form.Web.CoreWebView2.ExecuteScriptAsync("(() => { const l = document.getElementById('lecteur'); const c = getComputedStyle(l); let p = l.parentElement, ch = []; while (p) { const s = getComputedStyle(p); if (s.transform !== 'none' || s.filter !== 'none' || s.contain !== 'none' || s.willChange !== 'auto' || s.containerType !== 'normal') ch.push((p.id || p.className) + ':' + s.transform + '/' + s.filter + '/' + s.contain + '/' + s.willChange + '/' + s.containerType); p = p.parentElement; } return [c.position, c.top, c.bottom, c.height, document.body.className, ch.join(' | ')].join(' ; '); })()")}");
                    CaptureFenetre("3b-plein-ecran");
                    await Task.Delay(1500);
                    await page.CaptureAsync("plein-ecran-page");
                    // rien de la page YouTube au premier plan, et le lecteur remplit la vue (la page passait par-dessus en mise en page large, 02/11/2026)
                    string vis = await page.ScriptAsync(VisiblesDuLecteur);
                    Verifier(vis.Contains("aucun (seulement la vid") && vis.Contains("lecteur 0,0 " + page.BoundsDuLecteur.Width + "x" + page.BoundsDuLecteur.Height), "plein écran : seulement la vidéo au premier plan : " + vis);
                    await page.ScriptAsync("document.getElementById('movie_player').dispatchEvent(new MouseEvent('mousemove', { bubbles: true, clientX: 800, clientY: 700 }))");
                    await Task.Delay(400);
                    vis = await page.ScriptAsync(VisiblesDuLecteur);
                    Verifier(vis.Contains("aucun (seulement la vid"), "… et après un mouvement de souris : " + vis);

                    // la barre s'efface après 3 s sans mouvement de la souris (pendant la lecture) : la vidéo prend toute la hauteur ; un mouvement la rappelle.
                    // (Mesure faite avec la vraie souris : si on la bouge pendant l'essai, la barre reste, à juste titre.)
                    for (int i = 0; i < 32 && barreVisible; i++) await Task.Delay(250);
                    await Task.Delay(400);
                    Verifier(!barreVisible && !(await Etat()).GetProperty("barre").GetBoolean() && page.BoundsDuLecteur.Height == form.ClientSize.Height && !Devant(),
                        $"plein écran, souris immobile : barre effacée (derrière la vidéo), vidéo {page.BoundsDuLecteur} sur toute la hauteur");
                    CaptureFenetre("3d-plein-ecran-sans-barre");
                    Activite();   // ce que fait un mouvement de la souris
                    await Task.Delay(600);
                    Verifier(barreVisible && (await Etat()).GetProperty("barre").GetBoolean() && page.BoundsDuLecteur.Height == form.ClientSize.Height && Bande() && Devant(),
                        $"mouvement de la souris : barre revenue devant la vidéo, qui garde toute la hauteur ({page.BoundsDuLecteur})");
                    // le menu de l'engrenage en plein écran : la page reprend toute la fenêtre pour l'afficher, puis redevient une bande
                    await form.Web.CoreWebView2.ExecuteScriptAsync("document.getElementById('cReglages').click()");
                    for (int i = 0; i < 12 && form.Web.Dock != DockStyle.Fill; i++) await Task.Delay(100);
                    await Task.Delay(500);
                    var emp = await Etat();
                    bool menuPlein = form.Web.Dock == DockStyle.Fill && emp.GetProperty("menu").GetArrayLength() >= 3 && emp.GetProperty("recouvert").GetBoolean();
                    await form.Web.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('.menu[data-cdx]').forEach(m => m.remove())");
                    for (int i = 0; i < 12 && !Bande(); i++) await Task.Delay(100);
                    Verifier(menuPlein && Bande() && page.BoundsDuLecteur.Size == form.ClientSize, $"plein écran : le menu de l'engrenage s'ouvre sur toute la fenêtre, puis la barre redevient une bande ({form.Web.Bounds})");
                    // la décision, sur des cas précis (sans bouger la vraie souris) : la fenêtre plein écran et un point hors d'elle (un autre écran)
                    var f = form.Bounds; var dedans = new Point(f.Left + f.Width / 2, f.Top + f.Height / 2); var surBarre = new Point(dedans.X, f.Bottom - 10);
                    var dehors = new Point(f.Right + 500, f.Top + 300); var court = TimeSpan.FromSeconds(1); var long_ = TimeSpan.FromSeconds(4);
                    var cas = new (string nom, Barre attendu, Barre obtenu)[]
                    {
                        ("dans la fenêtre, la souris bouge", Barre.Activite, DecisionBarre(dedans, f, true, true, false, long_)),
                        ("dans la fenêtre, immobile, en lecture, après le délai", Barre.Effacer, DecisionBarre(dedans, f, false, true, false, long_)),
                        ("dans la fenêtre, immobile, avant le délai", Barre.Rien, DecisionBarre(dedans, f, false, true, false, court)),
                        ("dans la fenêtre, en pause", Barre.Rien, DecisionBarre(dedans, f, false, false, false, long_)),
                        ("dans la fenêtre, sur la barre", Barre.Rien, DecisionBarre(surBarre, f, false, true, false, long_)),
                        ("dans la fenêtre, menu ouvert", Barre.Rien, DecisionBarre(dedans, f, false, true, true, long_)),
                        ("sur l'autre écran, la souris bouge, après le délai", Barre.Effacer, DecisionBarre(dehors, f, true, true, false, long_)),
                        ("sur l'autre écran, en pause, après le délai", Barre.Effacer, DecisionBarre(dehors, f, true, false, false, long_)),
                        ("sur l'autre écran, avant le délai", Barre.Rien, DecisionBarre(dehors, f, true, true, false, court)),
                    };
                    var faux = cas.Where(x => x.attendu != x.obtenu).ToList();
                    Verifier(faux.Count == 0, faux.Count == 0 ? $"décision de la barre : {cas.Length} cas justes (dont la souris sur un autre écran : la barre s'efface)"
                        : "décision de la barre fausse : " + string.Join(" ; ", faux.Select(x => $"{x.nom} → {x.obtenu} au lieu de {x.attendu}")));
                    await page.CaptureAsync("plein-ecran-page-souris");
                    await Cmd(new { action = "pleinecran" });
                    for (int i = 0; i < 12 && page.BoundsDuLecteur != rectLecteur; i++) await Task.Delay(250);
                    Verifier(!pleinEcran && form.Bounds == avant && page.BoundsDuLecteur == rectLecteur, $"sortie du plein écran : fenêtre {form.Bounds}, lecteur {page.BoundsDuLecteur}");
                    await page.GesteAsync("document.querySelector('#movie_player').requestFullscreen()");
                    for (int i = 0; i < 12 && !pleinEcran; i++) await Task.Delay(250);
                    await Task.Delay(400);
                    await Task.Delay(1500);
                    string visY = await page.ScriptAsync(VisiblesDuLecteur);
                    Verifier(visY.Contains("aucun (seulement la vid"), "plein écran demandé par YouTube : seulement la vidéo au premier plan : " + visY);
                    await page.CaptureAsync("plein-ecran-youtube");
                    CaptureFenetre("3c-plein-ecran-youtube");
                    Verifier(pleinEcran && !page.EnPleinEcran && form.Bounds == ecran, $"plein écran demandé par YouTube : refusé à YouTube ({(page.EnPleinEcran ? "encore en plein écran" : "sorti")}), notre plein écran à la place");
                    await Cmd(new { action = "pleinecran" });
                    for (int i = 0; i < 12 && pleinEcran; i++) await Task.Delay(250);

                    // le lecteur change de vidéo de lui-même (ici un clic sur un lien de la page YouTube, comme l'écran de fin) : la fiche suit
                    string lien = "";
                    for (int i = 0; i < 32 && lien.Length != 11; i++) { lien = JsonSerializer.Deserialize<string>(await page.ScriptAsync("(() => { const cur = new URLSearchParams(location.search).get('v'); const a = [...document.querySelectorAll('a[href*=\"watch?v=\"]')].find(x => !x.closest('ytd-ad-slot-renderer, ytd-in-feed-ad-layout-renderer, ytd-promoted-sparkles-web-renderer, #player-ads, [class*=\"ad-slot\"], [class*=\"promoted\"]') && !x.getAttribute('href').includes(cur) && !x.getAttribute('href').includes('list=')); return a ? new URLSearchParams(a.getAttribute('href').split('?')[1]).get('v') : ''; })()")) ?? ""; if (lien.Length != 11) await Task.Delay(250); }
                    if (lien.Length == 11)
                    {
                        Host.Log("   liens de vidéo dans la page YouTube : " + await page.ScriptAsync("document.querySelectorAll('a[href*=\"watch?v=\"]').length") + " ; clic sur " + lien);
                        await page.GesteAsync("[...document.querySelectorAll('a[href*=\"watch?v=" + lien + "\"]')][0].click()");
                        for (int i = 0; i < 40 && (await Etat()).GetProperty("id").GetString() != lien; i++) await Task.Delay(250);
                        var es = await Etat();
                        Verifier(es.GetProperty("id").GetString() == lien && es.GetProperty("vue").GetString() == "video", $"le lecteur passe seul à {lien} : la fiche suit (« {es.GetProperty("titre").GetString()} »)");
                    }
                    else Verifier(false, "aucun lien de vidéo dans la page YouTube pour l'essai de la fiche qui suit");
                }
            }

            string? idAvantAccueil = (await Etat()).GetProperty("id").GetString();
            bool lectureAvant = page.LecteurAffiche;
            if (lectureAvant)
            {
                // la vidéo doit jouer avant qu'on la quitte (celle ouverte par un lien de la page YouTube démarre parfois tard) : jusqu'à 10 s
                await page.LireAsync();
                for (int i = 0; i < 40; i++)
                {
                    var lv = JsonDocument.Parse(await page.EtatLecteurAsync()).RootElement;
                    if (lv.GetProperty("t").ValueKind == JsonValueKind.Number && lv.GetProperty("t").GetDouble() > 0.5 && lv.GetProperty("pause").ValueKind == JsonValueKind.False) break;
                    await Task.Delay(250);
                }
            }
            ok = await Demander(new { cmd = "yt.accueil" });
            var ea = await Etat();
            Verifier(ok && ea.GetProperty("vue").GetString() == "accueil" && (!page.Connecte || ea.GetProperty("n").GetInt32() > 0), $"retour à l'accueil par navigation interne : {ea.GetProperty("n").GetInt32()} vidéos");
            if (lectureAvant && idAvantAccueil != null)
            {
                // le mini-lecteur : la vidéo continue, dans le coin de l'interface, pendant qu'on parcourt YouTube
                for (int i = 0; i < 20 && !(page.Mini && page.BoundsDuLecteur.Width < 400); i++) await Task.Delay(250);
                double Tm(JsonElement e) => e.GetProperty("t").ValueKind == JsonValueKind.Number ? e.GetProperty("t").GetDouble() : -1;
                var m1 = JsonDocument.Parse(await page.EtatLecteurAsync()).RootElement;
                await Task.Delay(2500);
                var m2 = JsonDocument.Parse(await page.EtatLecteurAsync()).RootElement;
                var em1 = await Etat();
                Verifier(page.Mini && page.LecteurAffiche && page.HoteDuLecteur == form && page.BoundsDuLecteur.Width < 400 && em1.GetProperty("mini").GetBoolean()
                    && m2.GetProperty("id").GetString() == idAvantAccueil && Tm(m2) > Tm(m1) + 1,
                    $"en quittant la fiche, la vidéo continue dans le mini-lecteur : {page.BoundsDuLecteur}, {Tm(m1)} s → {Tm(m2)} s, id {m2.GetProperty("id").GetString()}");
                // revenir à la vidéo : la fiche revient, le lecteur reprend sa taille, sans recharger la vidéo
                await form.Web.CoreWebView2.ExecuteScriptAsync("document.getElementById('miniRevenir').click()");
                for (int i = 0; i < 20 && (page.Mini || page.BoundsDuLecteur.Width < 400); i++) await Task.Delay(250);
                var m3 = JsonDocument.Parse(await page.EtatLecteurAsync()).RootElement;
                var em2 = await Etat();
                Verifier(!page.Mini && em2.GetProperty("vue").GetString() == "video" && em2.GetProperty("id").GetString() == idAvantAccueil && page.BoundsDuLecteur.Width >= 400
                    && m3.GetProperty("id").GetString() == idAvantAccueil && Tm(m3) >= Tm(m2) && m3.GetProperty("pause").ValueKind == JsonValueKind.False,
                    $"retour du mini-lecteur à la fiche : {page.BoundsDuLecteur}, {Tm(m3)} s, toujours en lecture");
                // de nouveau l'accueil, puis la croix du mini-lecteur : la vidéo s'arrête et le lecteur retourne dans la page cachée
                ok = await Demander(new { cmd = "yt.accueil" });
                for (int i = 0; i < 20 && !page.Mini; i++) await Task.Delay(250);
                await Task.Delay(500);
                await form.Web.CoreWebView2.ExecuteScriptAsync("document.getElementById('miniFermer').click()");
            }
            for (int i = 0; i < 20 && (page.LecteurAffiche || page.HoteDuLecteur == form); i++) await Task.Delay(250);
            var l4 = JsonDocument.Parse(await page.EtatLecteurAsync()).RootElement;
            Verifier(!page.LecteurAffiche && !page.Mini && page.HoteDuLecteur != form && l4.GetProperty("pause").ValueKind != JsonValueKind.False && !(await Etat()).GetProperty("mini").GetBoolean(),
                "mini-lecteur fermé : le lecteur est recaché et en pause");
            if (ok && page.Connecte)
            {
                ok = await Demander(new { cmd = "yt.plus", vue = "accueil" }, 20);
                int n2 = (await Etat()).GetProperty("n").GetInt32();
                Verifier(ok && n2 > ea.GetProperty("n").GetInt32(), $"suite de l'accueil : {ea.GetProperty("n").GetInt32()} → {n2} vidéos");
            }
            await Capture("4-accueil-retour");

            if (page.Connecte)
            {
                ok = await Demander(new { cmd = "yt.abonnements" });
                var eb = await Etat();
                string msg = string.Join(" ", eb.GetProperty("messages").EnumerateArray().Select(x => x.GetString()));
                Verifier(ok && eb.GetProperty("vue").GetString() == "abonnements" && (eb.GetProperty("n").GetInt32() > 0 || msg.Contains("aucune chaîne")),
                    $"abonnements : {eb.GetProperty("n").GetInt32()} vidéos{(msg.Length > 0 ? " — " + msg : "")}");
                await Capture("5-abonnements");
            }

            // sans compte : la fenêtre de connexion Google s'affiche (on ne saisit rien), puis « fermer » la recache et ramène la page sur YouTube
            if (!page.Connecte)
            {
                await page.ConnexionAsync();
                await Task.Delay(6000);
                string login = await page.ScriptAsync("JSON.stringify({ hote: location.host, email: [...document.querySelectorAll('input[type=email], #identifierId')].some(x => x.getClientRects().length > 0), refus: /disallowed_useragent|pas sécurisé|not be secure/i.test(document.body.innerText) })");
                Verifier(page.FenetreAffichee && login.Contains("accounts.google.com") && login.Contains("\\\"email\\\":true"), "fenêtre de connexion Google affichée : " + login);
                await page.CaptureAsync("connexion");
                await page.AnnulerConnexionAsync();
                string ou = await page.ScriptAsync("location.host");
                Verifier(!page.FenetreAffichee && ou.Contains("www.youtube.com"), "fenêtre de connexion fermée : page recachée, retour sur " + ou);
            }

            // ---------- chaînes et listes ----------
            if (page.Connecte) Verifier(page.Compte != null, "nom du compte (menu de l'avatar) : " + (page.Compte ?? "illisible"));
            string? S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (chaineFiche != null)
            {
                ok = await Demander(new { cmd = "yt.chaine", id = chaineFiche });
                var ec = await Etat();
                var ch = ec.GetProperty("chaine");
                var onglets = ch.ValueKind == JsonValueKind.Object ? ch.GetProperty("onglets").EnumerateArray().ToList() : new List<JsonElement>();
                Verifier(ok && ec.GetProperty("vue").GetString() == "chaine" && ec.GetProperty("n").GetInt32() > 0 && onglets.Count >= 3,
                    $"chaîne {chaineFiche} : « {(ch.ValueKind == JsonValueKind.Object ? S(ch, "nom") : "?")} », {ec.GetProperty("n").GetInt32()} éléments, onglets : {string.Join(", ", onglets.Select(x => (x.GetProperty("actif").GetBoolean() ? "*" : "") + S(x, "titre")))}");
                await Capture("6-chaine");
                var opl = onglets.FirstOrDefault(x => S(x, "url")?.EndsWith("/playlists") == true);
                if (opl.ValueKind == JsonValueKind.Object)
                {
                    ok = await Demander(new { cmd = "yt.chaine", id = chaineFiche, url = S(opl, "url"), @params = S(opl, "params") });
                    var ep = await Etat();
                    var listes = ep.GetProperty("elements").EnumerateArray().Where(x => S(x, "genre") is "liste").ToList();
                    Verifier(ok && ep.GetProperty("vue").GetString() == "chaine" && listes.Count > 0, $"onglet Playlists de la chaîne : {listes.Count} listes sur {ep.GetProperty("n").GetInt32()} éléments");
                    if (listes.Count == 0) Host.Log("   (texte affiché : " + S(ep, "texteVue") + ")");
                }
                else Host.Log("   cette chaîne n'a pas d'onglet Playlists");
            }
            else Verifier(false, "la fiche n'a pas donné d'identifiant de chaîne");

            // une liste : sa page, puis « Tout lire » (lecture dans l'ordre de la liste), puis la vidéo suivante de la liste
            string? idListe = o.Liste;
            if (idListe == null)
            {
                var ep = await Etat();
                idListe = ep.GetProperty("elements").EnumerateArray().Where(x => S(x, "genre") is "liste").Select(x => S(x, "listeId")).FirstOrDefault();
            }
            idListe ??= "PLCwXWOyIR22vEAr4Kb1Bj1nMK7rmjJtFQ";   // « ☄️ Astronomie » (ARTE), si la chaîne n'en propose pas
            ok = await Demander(new { cmd = "yt.liste", id = idListe });
            var el = await Etat();
            var ent = el.GetProperty("entete");
            Verifier(ok && el.GetProperty("vue").GetString() == "liste" && el.GetProperty("n").GetInt32() > 0 && ent.ValueKind == JsonValueKind.Object && S(ent, "titre") != null,
                $"liste {idListe} : « {(ent.ValueKind == JsonValueKind.Object ? S(ent, "titre") : "?")} » de {(ent.ValueKind == JsonValueKind.Object ? S(ent, "auteur") : "?")}, {el.GetProperty("n").GetInt32()} vidéos");
            await Capture("7-liste");
            string? premierListe = ent.ValueKind == JsonValueKind.Object ? S(ent, "premierId") : null;
            if (premierListe != null)
            {
                ok = await Demander(new { cmd = "yt.video", id = premierListe, liste = idListe, index = 0 });
                var ef = await Etat();
                var li = ef.GetProperty("liste");
                Verifier(ok && li.ValueKind == JsonValueKind.Object && S(li, "id") == idListe && li.GetProperty("n").GetInt32() > 0,
                    $"« Tout lire » : fiche de {premierListe} dans la liste « {(li.ValueKind == JsonValueKind.Object ? S(li, "titre") : "?")} » ({(li.ValueKind == JsonValueKind.Object ? li.GetProperty("index").GetInt32() + 1 + " / " + li.GetProperty("total").GetInt32() : "pas de panneau")})");
                await Task.Delay(2500);
                var lt = JsonDocument.Parse(await page.EtatLecteurAsync()).RootElement;
                Verifier(page.LecteurAffiche && lt.GetProperty("id").GetString() == premierListe && lt.GetProperty("pause").ValueKind == JsonValueKind.False, $"la liste se lit : {lt.GetRawText()}");
                await Capture("8-liste-lecture");
                CaptureFenetre("8-liste-lecture-fenetre");
                string? deuxieme = li.ValueKind == JsonValueKind.Object ? S(li, "deuxieme") : null;
                if (deuxieme != null)
                {
                    ok = await Demander(new { cmd = "yt.video", id = deuxieme, liste = idListe, index = 1 });
                    var li2 = (await Etat()).GetProperty("liste");
                    Verifier(ok && li2.ValueKind == JsonValueKind.Object && li2.GetProperty("index").GetInt32() == 1, $"vidéo suivante de la liste : {deuxieme}, place {(li2.ValueKind == JsonValueKind.Object ? li2.GetProperty("index").GetInt32() + 1 : 0)}");
                }
            }
            // un mix : la vidéo et sa liste « RD » + id ; YouTube l'enchaîne sans panneau de liste
            if (premier != null)
            {
                ok = await Demander(new { cmd = "yt.video", id = premier, liste = "RD" + premier });
                await Task.Delay(2500);
                var lm = JsonDocument.Parse(await page.EtatLecteurAsync()).RootElement;
                string src = await page.ScriptAsync("location.search");
                Verifier(ok && (await Etat()).GetProperty("id").GetString() == premier && src.Contains("list=RD") && lm.GetProperty("pause").ValueKind == JsonValueKind.False, $"mix de {premier} : {src} ; {lm.GetRawText()}");
            }
            // une vidéo remplacée par un écran de YouTube (supprimée, ou avertissement d'âge / de contenu) : la vue native s'efface, la fiche montre le
            // message et les boutons de YouTube ; s'il y a un bouton (« Je comprends et je souhaite continuer »), il est cliqué et la vidéo doit se lire
            if (o.Bloquee != null)
            {
                ok = await Demander(new { cmd = "yt.video", id = o.Bloquee });
                for (int i = 0; i < 40 && !page.Bloque; i++) await Task.Delay(250);
                await Task.Delay(500);
                var eb = await Etat();
                var bl = eb.GetProperty("blocage");
                Verifier(ok && page.Bloque && !page.VueVisible && bl.ValueKind == JsonValueKind.Object && bl.GetProperty("texte").GetString() is { Length: > 0 },
                    $"vidéo {o.Bloquee} remplacée par un écran de YouTube : vue native effacée ({(page.VueVisible ? "ENCORE visible" : "oui")}), message dans la fiche : {(bl.ValueKind == JsonValueKind.Object ? bl.GetRawText() : "aucun")}");
                CaptureFenetre("9-blocage");
                if (bl.ValueKind == JsonValueKind.Object && bl.GetProperty("boutons").GetArrayLength() > 0)
                {
                    await form.Web.CoreWebView2.ExecuteScriptAsync("document.querySelector('#lecteur [data-a=confirmer]').click()");
                    for (int i = 0; i < 40 && page.Bloque; i++) await Task.Delay(250);
                    // 8 s : l'avertissement revenait environ 2 s après le démarrage (le lecteur redemandait ses données sans l'accord, 03/11/2026)
                    await Task.Delay(8000);
                    var lb = JsonDocument.Parse(await page.EtatLecteurAsync()).RootElement;
                    Verifier(!page.Bloque && page.VueVisible && lb.GetProperty("pause").ValueKind == JsonValueKind.False, $"bouton de l'écran de YouTube cliqué : la vidéo se lit ({lb.GetRawText()})");
                    CaptureFenetre("9b-blocage-confirme");
                }
            }

            ok = await Demander(new { cmd = "yt.accueil" });

            // sans compte (essai isolé) : la déconnexion efface les cookies du profil et recharge YouTube ; avec le vrai compte, on n'y touche pas
            if (!page.Connecte)
            {
                int avant = await page.NombreDeCookiesAsync();
                ok = await Demander(new { cmd = "yt.deconnexion" }, 40);
                int apres = await page.NombreDeCookiesAsync();
                var ed = await Etat();
                Verifier(ok && ed.GetProperty("vue").GetString() == "accueil" && !page.Connecte && !page.LecteurAffiche, $"déconnexion : {avant} cookies youtube.com avant, {apres} après (consentement redemandé et refusé), accueil affiché, historique vidé");
            }

            await page.VeillePageAsync();
            Host.Log($"extraction : {page.ModeExtraction} ; réponses lues : C# {page.Recues["csharp"]}, fetch {page.Recues["fetch"]}, page (cache) {page.Recues["page"]}, ytInitialData {page.Recues["ytInitialData"]}, échecs {page.Recues["echecs"]}");
            Host.Log($"veille : {Veille.Compte} constat(s) pendant l'essai (youtube\\veille.log)");
        }
        catch (Exception ex) { Host.Log("ERREUR pendant l'essai : " + ex); echecs++; }
        Host.Log(echecs == 0 ? "=== essai réussi ===" : $"=== essai : {echecs} échec(s) ===");
        Code = echecs == 0 ? 0 : 3;
        ExitThread();
    }
}
