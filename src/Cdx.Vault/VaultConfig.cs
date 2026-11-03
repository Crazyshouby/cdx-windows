using System.Text.Json;

namespace Cdx.Coffre;

// Où est le coffre : CDX_NOTES_DIR (essais isolés), sinon notes.json dans le dossier des données de la suite, sinon « Documents\Notes CDX ».
// L'application et le serveur MCP lisent la même réponse : ils travaillent toujours sur le même dossier.
public static class VaultConfig
{
    static string DataDir => Environment.GetEnvironmentVariable("CDX_DATA_DIR") is { Length: > 0 } d ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CdxShell");
    public static string FilePath => Path.Combine(DataDir, "notes.json");
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Notes CDX");

    public static string Locate()
    {
        var env = Environment.GetEnvironmentVariable("CDX_NOTES_DIR"); if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
        try
        {
            if (File.Exists(FilePath))
            {
                using var d = JsonDocument.Parse(File.ReadAllText(FilePath));
                if (d.RootElement.TryGetProperty("vault", out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())) return Path.GetFullPath(v.GetString());
            }
        }
        catch { }
        // Le coffre s'appelait « Notes KDX » (suite KDX, jusqu'au 31/10/2026) : renommé une fois ; s'il ne peut pas l'être (ouvert ailleurs), on continue dedans.
        string old = Path.Combine(Path.GetDirectoryName(DefaultRoot), "Notes KDX");
        if (!Directory.Exists(DefaultRoot) && Directory.Exists(old))
        {
            try { Directory.Move(old, DefaultRoot); } catch { return old; }
        }
        return DefaultRoot;
    }

    public static void Save(string root)
    {
        AtomicFile.WriteJson(FilePath, new { version = 1, vault = root }, new JsonSerializerOptions { WriteIndented = true });
    }

    // Ouvre le coffre ; s'il n'existe pas encore, le crée avec quelques notes pour commencer (jamais dans un dossier qui contient déjà des fichiers).
    public static Vault Open(bool create = true)
    {
        string root = Locate();
        if (!Directory.Exists(root))
        {
            if (!create) throw new VaultException("introuvable", "Le coffre de notes n'existe pas encore : ouvrir l'application Notes une fois pour le créer.");
            Directory.CreateDirectory(root); Seed(root);
        }
        else if (create && !Directory.EnumerateFileSystemEntries(root).Any()) Seed(root);
        return new Vault(root);
    }

    public static void Seed(string root)
    {
        void W(string rel, string text) { string f = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(f)); File.WriteAllText(f, text, new System.Text.UTF8Encoding(false)); }
        W("Accueil.md", "# Accueil\n\nBienvenue dans tes notes. Ce dossier ne contient que des fichiers Markdown : tu peux les ouvrir avec n'importe quel éditeur, ou avec Obsidian.\n\n- Les projets sont dans [[Projets]] ; chacun a une fiche (voir [[Exemple de projet]]).\n- Écrire `[[Nom d'une note]]` crée un lien ; les notes qui pointent ici apparaissent dans « Liens entrants ».\n- Une #étiquette se met n'importe où dans le texte.\n- Claude peut lire, créer et ranger ces notes (serveur MCP « cdx-notes »), sans jamais rien supprimer : ce qu'il retire part dans `.trash`.\n");
        W("Projets/Projets.md", "# Projets\n\nLa liste vivante est dans la vue **Projets** de l'application. Pour en créer un : nouvelle note à partir du modèle [[Projet]].\n");
        W("Projets/Exemple de projet.md", "---\ntype: projet\nstatut: actif\ndepot: \npriorite: normale\nresume: Un exemple de fiche de projet, à garder comme modèle ou à supprimer.\ntags: [exemple]\n---\n# Exemple de projet\n\n## Objectif\n\nMontrer à quoi ressemble une fiche : propriétés en haut, objectif, actions, notes.\n\n## Prochaines actions\n\n- [ ] Lire la note [[Accueil]]\n- [ ] Créer mon premier vrai projet 📅 2026-12-01\n- [x] Ouvrir l'application Notes\n\n## Journal\n\n");
        W("Modèles/Projet.md", Vault.ProjectTemplate.Replace("{nom}", "Nom du projet").Replace("{depot}", "").Replace("{resume}", ""));
    }
}

