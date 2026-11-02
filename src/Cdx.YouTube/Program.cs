using System.Text;
using System.Text.Json;

namespace Cdx;

// cdx-youtube.exe [--theme <nom>] [--page-visible] [--extraction csharp|fetch] [--trace]
//   ouvre « YouTube » : une page YouTube cachée (profil WebView2 à elle, session Google, uBlock Origin Lite) fait le travail ; la fenêtre de la charte
//   affiche l'accueil, la recherche et la fiche d'une vidéo. Une seule instance : un second lancement fait passer la première devant (message « show »).
//   --theme          un des seize thèmes de la charte pour ce lancement (sinon l'accent de la suite)
//   --page-visible   montre la page YouTube « cachée » à côté (mise au point), avec les outils de développement
//   --extraction     csharp (défaut : WebResourceResponseReceived) ou fetch (crochet sur fetch dès le départ, pour comparer)
//   --trace          écrit au journal chaque requête faite à youtube.com par la page cachée, et garde les réponses sous youtube\brut\
// cdx-youtube.exe --essai [--requete "…"] [--video <id>]
//   parcours automatique (accueil, recherche, suite, fiche, suggestion, retour, et sans compte la fenêtre de connexion) ; résultat dans
//   journal-youtube.log, code de sortie 0 si tout passe. Ne montre jamais la fenêtre de connexion d'elle-même. Voir tests\youtube.ps1.
// cdx-youtube.exe --sonder <chemin> [--sonder <chemin>…]
//   mise au point et veille : la page cachée charge chaque adresse de YouTube (/@chaine/videos, /playlist?list=…) et garde son ytInitialData sous
//   youtube\brut\ (à rejouer avec --lire), puis le programme s'arrête.
// cdx-youtube.exe --lire <réponse.json>
//   passe une réponse brute de YouTube dans la couche de lecture (YtLecture) et écrit ce qu'elle en tire ; aucune fenêtre, aucun réseau.
sealed class Options
{
    public string Theme, Requete = "documentaire espace", FichierALire, Video, Liste;
    public List<string> Sonder = new();
    public bool PageVisible, Essai;
    public PageCachee.Extraction Extraction = PageCachee.Extraction.CSharp;

    public static Options Lire(string[] a)
    {
        var o = new Options();
        for (int i = 0; i < a.Length; i++)
        {
            string Suivant() => i + 1 < a.Length ? a[++i] : throw new ArgumentException("valeur manquante après " + a[i]);
            switch (a[i])
            {
                case "--theme": o.Theme = Suivant(); if (!Suite.IsAccent(o.Theme)) throw new ArgumentException("thème inconnu : " + o.Theme); o.Theme = Suite.Canonical(o.Theme); break;
                case "--page-visible": o.PageVisible = true; break;
                case "--extraction": o.Extraction = Suivant() == "fetch" ? PageCachee.Extraction.Fetch : PageCachee.Extraction.CSharp; break;
                case "--trace": PageCachee.Trace = true; break;
                case "--essai": o.Essai = true; break;
                case "--requete": o.Requete = Suivant(); break;
                case "--video": o.Video = Suivant(); break;
                case "--liste": o.Liste = Suivant(); break;
                case "--lire": o.FichierALire = Suivant(); break;
                case "--sonder": o.Sonder.Add(Suivant()); break;
                default: throw new ArgumentException("option inconnue : " + a[i]);
            }
        }
        return o;
    }
}

// Les données de YouTube, dans celles de la suite : <données>\youtube\ (%LOCALAPPDATA%\CdxShell\youtube, ou CDX_DATA_DIR pour un essai isolé).
static class YtDonnees
{
    public static string Racine => Path.Combine(Host.DataDir, "youtube");
    public static string Profil => Path.Combine(Racine, "profil");                       // la page YouTube cachée : cookies, session Google, uBOL
    public static string ProfilInterface => Path.Combine(Host.DataDir, "webview-youtube");   // la page de l'interface (comme webview-repos) : aucun cookie YouTube
    public static string Extensions => Path.Combine(Racine, "extensions");
    public static string Captures => Path.Combine(Racine, "captures");
    public static string Brut => Path.Combine(Racine, "brut");

    // Une fois : les données du prototype (dépôt CDX-YouTube, %LOCALAPPDATA%\CdxYouTube) rejoignent celles de la suite. Le profil est déplacé, pas copié :
    // la session Google suit, sans reconnexion. uBOL change de dossier : son identifiant est oublié, il sera rechargé depuis le nouveau.
    // À défaut, le profil connecté de l'étape 1 est copié. Rien n'est fait pour un essai isolé (CDX_DATA_DIR), ni si le profil existe déjà.
    public static void Migrer()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CDX_DATA_DIR")) || Directory.Exists(Profil)) return;
        string ancien = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CdxYouTube");
        try
        {
            Directory.CreateDirectory(Racine);
            if (Directory.Exists(Path.Combine(ancien, "profil")))
            {
                foreach (var d in new[] { "profil", "extensions", "brut" })
                    if (Directory.Exists(Path.Combine(ancien, d))) Directory.Move(Path.Combine(ancien, d), Path.Combine(Racine, d));
                foreach (var f in new[] { "veille.log", "session-ouverte" })
                    if (File.Exists(Path.Combine(ancien, f))) File.Move(Path.Combine(ancien, f), Path.Combine(Racine, f));
                Ubol.OublierIdentifiant();
                File.WriteAllText(Path.Combine(ancien, "DEPLACE.txt"), $"{DateTime.Now:s} : profil, extensions, réponses brutes et veille déplacés vers {Racine} (cdx-youtube de la suite CDX).\n");
                Host.Log("données de l'ancien CDX-YouTube déplacées : " + ancien + " → " + Racine);
            }
            else if (Directory.Exists(Path.Combine(ancien, "etape1", "profils", "compte")))
            {
                Ubol.Copier(Path.Combine(ancien, "etape1", "profils", "compte"), Profil);
                Host.Log("profil connecté de l'étape 1 repris : " + Path.Combine(ancien, "etape1", "profils", "compte"));
            }
        }
        catch (Exception ex) { Host.Log("reprise des données de l'ancien CDX-YouTube impossible : " + ex.Message); }
    }
}

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Host.Init("youtube");
        Options o;
        try { o = Options.Lire(args); }
        catch (Exception ex) { Host.Log("arguments : " + ex.Message); AttachConsole(-1); Console.Error.WriteLine(ex.Message); return 2; }
        if (o.FichierALire != null) { AttachConsole(-1); Console.OutputEncoding = Encoding.UTF8; return LireFichier(o.FichierALire); }

        if (!SingleInstance.Acquire("youtube", out var mutex))
        {
            if (o.Essai || o.Sonder.Count > 0) { Host.Log("essai impossible : cdx-youtube tourne déjà (le profil de la page cachée est pris)"); return 4; }
            SingleInstance.Relay("youtube", "show", new { }); return 0;
        }
        using (mutex)
        {
            YtDonnees.Migrer();
            Host.Log($"=== cdx-youtube{(o.Essai ? ", essai" : "")}, extraction {o.Extraction} ===");
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => Host.Log("exception (interface) : " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Host.Log("exception fatale : " + e.ExceptionObject);

            var app = new YouTubeApp(o);
            Bus.Listen("youtube", app.OnBus);
            if (!o.Essai) Suite.RegisterApp("youtube", Environment.ProcessPath);
            app.Anchor.BeginInvoke(new Action(app.Open));   // dans la boucle de messages : WebView2 en a besoin
            Host.StartControlPipe("youtube", app.Anchor, app.HandleControl, () => app.Closing);
            Host.StartPilotPipe("youtube", app.Anchor, app.HandleControl, () => app.Closing);
            Application.Run(app);
            Host.Log($"=== fin, code {app.Code} ===");
            return app.Code;
        }
    }

    // --lire : une réponse brute (ytInitialData, youtubei browse/search/next/get_watch) passée dans la couche de lecture. Sert à vérifier YtLecture sans
    // réseau, et à rejouer une réponse gardée par la veille sous youtube\brut\.
    static int LireFichier(string f)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(f));
        var r = YtLecture.Normaliser(doc.RootElement);
        if (YtLecture.IdDeFiche(r) != null)
        {
            var x = YtLecture.LireFiche(r);
            Console.WriteLine($"fiche {x.Id} : « {x.Titre} » [{x.Chaine} · {x.Abonnes}] {x.Vues} · {x.Date} · description {x.Description?.Length ?? 0} car. · {x.PubsFiltrees} pubs filtrées");
            if (x.Liste != null)
            {
                Console.WriteLine($"liste en cours {x.Liste.Id} : « {x.Liste.Titre} » de {x.Liste.Auteur}, {x.Liste.Index + 1}/{x.Liste.Total}, {x.Liste.Videos.Count} vidéos dans le panneau");
                foreach (var v in x.Liste.Videos.Take(3)) Console.WriteLine($"  liste  {v.Index,3} {v.Id}  {v.Titre}  [{v.Chaine}]  {v.Duree}");
            }
            foreach (var v in x.Suggestions) Console.WriteLine($"  {v.Genre,-6} {v.Id}  {v.Titre}  [{v.Chaine}]  {v.Duree} · {v.Vues} · {v.Date}{(v.Miniature == null ? "  (sans miniature)" : "")}");
            return x.Titre == null ? 1 : 0;
        }
        if (Chemin(r, "contents", "twoColumnBrowseResultsRenderer", "tabs") is { ValueKind: JsonValueKind.Array } && r.TryGetProperty("header", out _) && !r.ToString().Contains("\"playlistMetadataRenderer\""))
        {
            var c = YtLecture.LireChaine(r, null);
            Console.WriteLine($"chaîne {c.Id} : « {c.Nom} » {c.Pseudo} · {c.Abonnes} · {c.NbVideos} · description {c.Description?.Length ?? 0} car. · avatar {(c.Avatar != null ? "oui" : "non")}");
            Console.WriteLine("onglets : " + string.Join(" | ", c.Onglets.Select(o => (o.Actif ? "*" : "") + o.Titre + " " + o.Url)));
        }
        else if (r.ToString().Contains("\"playlistMetadataRenderer\""))
        {
            var e = YtLecture.LireEnteteListe(r, null);
            Console.WriteLine($"liste « {e.Titre} » de {e.Auteur} ({e.AuteurId}) · {e.Infos} · description {e.Description?.Length ?? 0} car.");
        }
        var l = YtLecture.LireListe(r);
        Console.WriteLine($"liste : {l.Videos.Count} vidéos, suite {(l.Suite ? "oui" : "non")}{(l.EstSuite ? " (réponse de suite)" : "")}, {l.PubsFiltrees} pubs filtrées{(l.Messages.Count > 0 ? ", messages : " + string.Join(" | ", l.Messages) : "")}");
        foreach (var v in l.Videos) Console.WriteLine($"  {v.Genre,-6} {v.Id}  {v.Titre}  [{v.Chaine}]  {v.Duree} · {v.Vues} · {v.Date}{(v.Miniature == null ? "  (sans miniature)" : "")}");
        return 0;
    }

    static JsonElement? Chemin(JsonElement e, params string[] cles)
    {
        foreach (var c in cles) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(c, out e)) return null; }
        return e;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
}
