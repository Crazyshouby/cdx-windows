namespace Cdx;

// Les programmes de la suite, en un seul endroit : leur nom sur le bus, leur exécutable, le schéma « cdx:… » qui les lance (tuiles, épingles,
// liens entre programmes) et s'ils acceptent un argument (« cdx:explorer?C:\Dev »). Le bus (Bus.Apps), la liste blanche d'apps.json (Suite.AppPath),
// le shell (inscription des voisins, Launch) la lisent ; --selftest-bus vérifie que chaque programme .NET est bien compilé à côté du shell.
// Avant : six listes recopiées, déjà désynchronisées (quatre programmes manquaient aux voisins du shell ; « cdx:reposXYZ » lançait Dépôts).
// Ajouter un programme : une ligne ici (et son projet dans Cdx.sln, son composant dans l'installeur).
sealed record SuiteApp(string Name, string Exe, string? Scheme, bool TakesArgument, bool DotNet = true);

static class SuiteApps
{
    public static readonly SuiteApp[] All =
    {
        new("shell", "cdx-shell", null, false),
        new("explorer", "cdx-explorer", "cdx:explorer", true),
        new("browser", "cdx-browser", "cdx:navigateur", true),
        new("repos", "cdx-repos", "cdx:repos", false),
        new("transfers", "cdx-transfers", null, false),
        new("notes", "cdx-notes", "cdx:notes", true),
        new("terminal", "cdx-terminal", "cdx:terminal", true),
        new("agenda", "cdx-agenda", "cdx:agenda", false),
        new("mail", "cdx-mail", "cdx:courrier", false),
        new("youtube", "cdx-youtube", "cdx:youtube", false),
        new("audio_linker", "cdx-audio_linker", "cdx:audio_linker", false, DotNet: false),   // C++ (src/cdx-audio_linker)
        new("messages", "cdx-messages", null, false, DotNet: false),                        // Rust (dépôt cdx-messages), installé à part
    };

    public static readonly string[] Names = All.Select(a => a.Name).ToArray();

    public static SuiteApp? ByName(string name) => Array.Find(All, a => a.Name == name);

    // « cdx:explorer » ou « cdx:explorer?argument » ; jamais « cdx:explorerXYZ ».
    public static SuiteApp? ByTarget(string? target) =>
        target == null ? null : Array.Find(All, a => a.Scheme != null && (target.Equals(a.Scheme, StringComparison.OrdinalIgnoreCase) || target.StartsWith(a.Scheme + "?", StringComparison.OrdinalIgnoreCase)));

    // L'argument d'une cible (ce qui suit « ? », décodé), si le programme en accepte un.
    public static string? ArgumentOf(SuiteApp app, string target)
    {
        int q = target.IndexOf('?');
        return app.TakesArgument && q > 0 && q + 1 < target.Length ? Uri.UnescapeDataString(target[(q + 1)..]) : null;
    }
}
