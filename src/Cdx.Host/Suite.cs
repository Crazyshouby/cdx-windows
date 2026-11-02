using System.Text.Json;

namespace Cdx;

// Les réglages communs de la suite : %APPDATA%\CDX\suite.json (voir cdx-ui/docs/SUITE.md). Aujourd'hui : l'accent.
static class Suite
{
    // Les seize thèmes de cdx-ui/tokens.json (les apparences de KDX 1.6) ; les anciens noms (cdx-ui 1.x) restent lus, et ramenés au nom actuel.
    public static readonly string[] Accents = { "rouge", "marine", "ambre", "jean", "tramway", "sarcelle", "chocolat", "lavande", "oxygene", "couchant", "techno", "reve-bleu", "reve-vert", "reve-violet", "lagon", "turquoise" };
    static readonly Dictionary<string, string> Aliases = new() { ["bleu"] = "jean", ["vert"] = "reve-vert", ["violet"] = "reve-violet", ["charbon"] = "techno", ["cdx"] = "rouge" };
    public static string Canonical(string s) => s == null ? null : Accents.Contains(s) ? s : Aliases.TryGetValue(s, out var c) ? c : null;
    // CDX_SUITE_DIR remplace le dossier (réservé aux essais : ne jamais toucher aux vrais réglages).
    public static string Dir => Environment.GetEnvironmentVariable("CDX_SUITE_DIR") is { Length: > 0 } d ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CDX");
    public static string FilePath => Path.Combine(Dir, "suite.json");
    static string AppsFile => Path.Combine(Dir, "apps.json");

    public static bool IsAccent(string s) => Canonical(s) != null;

    // %APPDATA%\KDX (ancien nom de la suite) devient %APPDATA%\CDX, une fois.
    public static void MigrateOldDir() => Host.MigrateOldDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KDX"), Dir, "CDX_SUITE_DIR");

    // L'accent écrit dans suite.json, ou `fallback` si le fichier manque, est abîmé ou d'une autre version : une application ne plante jamais pour cela.
    public static string ReadAccent(string fallback = "rouge")
    {
        try
        {
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(FilePath));
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return fallback;
            if (r.TryGetProperty("version", out var v) && (v.ValueKind != JsonValueKind.Number || v.GetInt32() != 1)) return fallback;
            if (r.TryGetProperty("accent", out var a) && a.ValueKind == JsonValueKind.String && Canonical(a.GetString()) is { } c) return c;
        }
        catch (Exception) { }
        return fallback;
    }

    // Écriture atomique : un lecteur ne voit jamais un fichier à moitié écrit.
    public static void WriteAccent(string accent)
    {
        if (!IsAccent(accent)) throw new ArgumentException("accent inconnu : " + accent);
        Directory.CreateDirectory(Dir);
        AtomicWrite(FilePath, JsonSerializer.Serialize(new { version = 1, accent }));
    }

    static void AtomicWrite(string path, string text)
    {
        string tmp = path + ".tmp";
        System.IO.File.WriteAllText(tmp, text);
        System.IO.File.Move(tmp, path, true);
    }

    // Surveille le fichier (sondage toutes les 250 ms : date et taille) ; `onChange` reçoit le nouvel accent, sur le fil qui a appelé Watch,
    // seulement quand il change vraiment.
    public static IDisposable Watch(string current, Action<string> onChange)
    {
        var ctx = SynchronizationContext.Current;
        string cur = current; (DateTime, long) last = Stamp();
        var timer = new System.Threading.Timer(_ =>
        {
            try
            {
                var s = Stamp(); if (s == last) return; last = s;
                string a = ReadAccent(cur);
                if (a == cur) return;
                cur = a;
                if (ctx != null) ctx.Post(__ => onChange(a), null); else onChange(a);
            }
            catch (Exception ex) { Host.Log("suite.json : " + ex.Message); }
        }, null, 250, 250);
        return timer;
    }

    static (DateTime, long) Stamp()
    {
        var f = new FileInfo(FilePath);
        return f.Exists ? (f.LastWriteTimeUtc, f.Length) : (DateTime.MinValue, -1);
    }

    // apps.json : le chemin de l'exécutable de chaque programme de la suite, écrit par chacun au démarrage (voir SUITE.md §3.5).
    public static void RegisterApp(string name, string exePath)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var map = new SortedDictionary<string, string>();
            try { using var d = JsonDocument.Parse(System.IO.File.ReadAllText(AppsFile)); foreach (var p in d.RootElement.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.String) map[p.Name] = p.Value.GetString(); } catch (Exception) { }
            if (map.TryGetValue(name, out var old) && old == exePath) return;
            map[name] = exePath;
            AtomicWrite(AppsFile, JsonSerializer.Serialize(map));
        }
        catch (Exception ex) { Host.Log("apps.json : " + ex.Message); }
    }

    // Le chemin enregistré d'une application de la suite (liste blanche : seuls ces noms sont acceptés), ou null.
    public static string AppPath(string name)
    {
        if (name is not ("shell" or "explorer" or "browser" or "repos" or "transfers" or "notes" or "terminal" or "agenda" or "mail" or "pilote" or "youtube" or "audio_linker" or "messages")) return null;
        try
        {
            using var d = JsonDocument.Parse(System.IO.File.ReadAllText(AppsFile));
            if (d.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && System.IO.File.Exists(v.GetString())) return v.GetString();
        }
        catch (Exception) { }
        return null;
    }
}

