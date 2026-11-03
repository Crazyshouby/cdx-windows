using System.IO.Compression;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Cdx;

// uBlock Origin Lite dans le profil de la page cachée (option A de l'étape 1). WebView2 n'a pas de boutique d'extensions : on installe nous-mêmes la dernière
// archive Chromium publiée sur GitHub (uBOL-home), et on vérifie une fois par semaine s'il y en a une plus récente. Repris de cdx-browser
// (Cdx.Browser/BrowserData.cs, Extensions.InstallZipAsync).
//
// <données>\youtube\extensions\uBOLite\     l'extension dépliée (à part de celle du navigateur : chaque profil a la sienne)
// <données>\youtube\extensions\ubol.json    { version, id, verifie } : la version installée, son identifiant dans le profil, la date de la dernière vérification
static class Ubol
{
    const string Api = "https://api.github.com/repos/uBlockOrigin/uBOL-home/releases/latest";
    static readonly TimeSpan Intervalle = TimeSpan.FromDays(7);
    static string Dossier => Path.Combine(YtDonnees.Extensions, "uBOLite");
    static string Etat => Path.Combine(YtDonnees.Extensions, "ubol.json");

    sealed class EtatUbol { public string version { get; set; } public string id { get; set; } public DateTime verifie { get; set; } }

    // Vrai quand l'extension vient d'être ajoutée au profil pendant ce lancement (installation, mise à jour, changement de dossier) : constaté le 02/11/2026,
    // elle laisse alors passer les encarts publicitaires de toute la session ; au lancement suivant, plus rien. Voir PageCachee.DemarrerAsync.
    public static bool VientDEtreAjoute;

    // Appelé avant la première navigation de la page cachée. Ne lève jamais : sans bloqueur, la page marche quand même (et le journal le dit).
    public static async Task<string> PreparerAsync(CoreWebView2Profile profil, bool forcerVerification = false)
    {
        var e = Lire();
        try
        {
            bool absent = !File.Exists(Path.Combine(Dossier, "manifest.json"));
            if (absent || forcerVerification || DateTime.UtcNow - e.verifie > Intervalle)
            {
                try
                {
                    var (tag, url) = await DerniereAsync();
                    e.verifie = DateTime.UtcNow;
                    if (absent || tag != e.version)
                    {
                        Host.Log($"uBOL : {(absent ? "installation" : "mise à jour " + e.version + " →")} {tag}");
                        await RetirerAsync(profil, e);
                        await DeplierAsync(url);
                        e.version = tag; e.id = null;
                    }
                    else Host.Log($"uBOL : {e.version} est la dernière version");
                }
                catch (Exception ex) when (absent)
                {
                    // GitHub injoignable au premier lancement : la copie déjà installée par cdx-browser, s'il y en a une
                    string cdxBrowser = Path.Combine(Host.DataDir, "extensions", "uBOLite");   // celle du navigateur de la suite
                    if (!File.Exists(Path.Combine(cdxBrowser, "manifest.json"))) throw new Exception("GitHub injoignable (" + ex.Message + ") et aucune copie locale d'uBOL");
                    Host.Log("uBOL : GitHub injoignable (" + ex.Message + "), copie de celle de cdx-browser");
                    Copier(cdxBrowser, Dossier); e.version = VersionDuManifeste(); e.id = null;
                }
                catch (Exception ex) { Host.Log("uBOL : vérification impossible, on garde " + e.version + " (" + ex.Message + ")"); }
            }
            var ext = await ChargerAsync(profil, e);
            Ecrire(e);
            return $"uBOL {e.version} ({(ext.IsEnabled ? "actif" : "inactif")})";
        }
        catch (Exception ex)
        {
            Host.Log("uBOL : ÉCHEC, la page tourne sans bloqueur : " + ex.Message);
            Veille.Noter("bloqueur", "uBOL absent : " + ex.Message);
            return null;
        }
    }

    static async Task<(string tag, string url)> DerniereAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("cdx-youtube");
        using var doc = JsonDocument.Parse(await http.GetStringAsync(Api));
        string tag = doc.RootElement.GetProperty("tag_name").GetString();
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
            if (a.GetProperty("name").GetString() is { } n && n.EndsWith(".chromium.zip")) return (tag, a.GetProperty("browser_download_url").GetString());
        throw new Exception("aucune archive Chromium dans la publication " + tag);
    }

    static async Task DeplierAsync(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("cdx-youtube");
        string zip = Path.Combine(Path.GetTempPath(), "cdx-youtube-ubol-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
        await using (var s = await http.GetStreamAsync(url)) await using (var f = File.Create(zip)) await s.CopyToAsync(f);
        try
        {
            if (Directory.Exists(Dossier)) Directory.Delete(Dossier, true);
            Directory.CreateDirectory(Dossier);
            using var z = ZipFile.OpenRead(zip);
            // l'archive peut avoir ou non un dossier de tête ; le manifeste finit à la racine du dossier
            string tete = z.Entries.Any(x => x.FullName == "manifest.json") ? "" : z.Entries.FirstOrDefault(x => x.FullName.EndsWith("/manifest.json"))?.FullName.Replace("manifest.json", "") ?? "";
            foreach (var x in z.Entries)
            {
                if (!x.FullName.StartsWith(tete) || x.FullName.EndsWith("/")) continue;
                string plein = Path.GetFullPath(Path.Combine(Dossier, x.FullName[tete.Length..]));
                if (!plein.StartsWith(Dossier + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;   // chemin qui sort du dossier : ignoré
                Directory.CreateDirectory(Path.GetDirectoryName(plein)); x.ExtractToFile(plein, true);
            }
            if (!File.Exists(Path.Combine(Dossier, "manifest.json"))) throw new Exception("l'archive ne contient pas de manifest.json");
        }
        finally { try { File.Delete(zip); } catch { } }
    }

    // Toute autre copie d'uBlock dans le profil (version précédente, ou celle d'un profil repris de l'étape 1, chargée depuis un autre dossier) est retirée.
    static async Task RetirerAsync(CoreWebView2Profile profil, EtatUbol e, string garder = null)
    {
        foreach (var x in await profil.GetBrowserExtensionsAsync())
            if (x.Name.Contains("uBlock", StringComparison.OrdinalIgnoreCase) && x.Id != garder)
            {
                try { await x.RemoveAsync(); Host.Log($"uBOL : ancienne copie retirée du profil ({x.Id})"); } catch (Exception ex) { Host.Log("uBOL : retrait de " + x.Id + " : " + ex.Message); }
            }
    }

    static async Task<CoreWebView2BrowserExtension> ChargerAsync(CoreWebView2Profile profil, EtatUbol e)
    {
        var liste = await profil.GetBrowserExtensionsAsync();
        var deja = e.id == null ? null : liste.FirstOrDefault(x => x.Id == e.id);
        await RetirerAsync(profil, e, deja?.Id);
        var ext = deja ?? await profil.AddBrowserExtensionAsync(Dossier);
        if (!ext.IsEnabled) await ext.EnableAsync(true);
        VientDEtreAjoute = deja == null;
        if (deja == null) Host.Log($"uBOL : chargé dans le profil ({ext.Id})");
        e.id = ext.Id;
        return ext;
    }

    static string VersionDuManifeste()
    {
        try { using var d = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dossier, "manifest.json"))); return d.RootElement.GetProperty("version").GetString(); } catch { return "?"; }
    }

    // Après un déplacement du dossier : l'identifiant noté désigne l'ancienne copie ; au prochain démarrage, elle est retirée et la nouvelle chargée.
    public static void OublierIdentifiant() { var e = Lire(); e.id = null; Ecrire(e); }

    static EtatUbol Lire() { try { return JsonSerializer.Deserialize<EtatUbol>(File.ReadAllText(Etat)) ?? new(); } catch { return new(); } }
    static void Ecrire(EtatUbol e) { try { AtomicFile.WriteJson(Etat, e); } catch (Exception ex) { Host.Log("ubol : état non enregistré : " + ex.Message); } }

    public static void Copier(string s, string d)
    {
        Directory.CreateDirectory(d);
        foreach (var f in Directory.GetFiles(s)) File.Copy(f, Path.Combine(d, Path.GetFileName(f)), true);
        foreach (var x in Directory.GetDirectories(s)) Copier(x, Path.Combine(d, Path.GetFileName(x)));
    }
}
