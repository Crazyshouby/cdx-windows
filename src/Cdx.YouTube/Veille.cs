namespace Cdx;

// Le journal de veille : ce qui casse quand YouTube change. Une ligne par constat, dédoublonnée sur la durée d'un lancement :
//   format     un « renderer » ou « view model » de vidéo que la couche de lecture ne connaît pas, ou un champ attendu absent
//   pub        une publicité trouvée dans les données (filtrée par la couche de lecture) ou affichée dans la page malgré uBOL
//   extraction une réponse youtubei illisible, une attente sans réponse, le repli sur le crochet fetch
//   bloqueur   uBOL absent, désactivé, avertissement anti-bloqueur de YouTube
//   session    session Google perdue, consentement UE redemandé
// Fichier : <données>\youtube\veille.log. Les réponses brutes qui ont posé problème sont gardées sous <données>\youtube\brut\ (jamais dans un dépôt).
static class Veille
{
    static readonly HashSet<string> vus = new();
    static readonly object verrou = new();
    public static int Compte;

    public static void Noter(string genre, string texte)
    {
        lock (verrou)
        {
            if (!vus.Add(genre + "|" + texte)) return;
            Compte++;
            try { Directory.CreateDirectory(YtDonnees.Racine); File.AppendAllText(Path.Combine(YtDonnees.Racine, "veille.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {genre,-10}  {texte}\n"); } catch { }
        }
        Host.Log($"veille [{genre}] {texte}");
    }

    public static void GarderBrut(string nom, string? json)   // null : fichier vide
    {
        try { Directory.CreateDirectory(YtDonnees.Brut); File.WriteAllText(Path.Combine(YtDonnees.Brut, $"{DateTime.Now:yyyyMMdd-HHmmss}-{nom}.json"), json); } catch { }
    }
}
