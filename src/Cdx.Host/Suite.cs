using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Cdx;

// Les réglages communs de la suite : %APPDATA%\CDX\suite.json (voir cdx-ui/docs/SUITE.md). Aujourd'hui : l'accent.
static class Suite
{
    // Les seize thèmes de cdx-ui/tokens.json (les apparences de KDX 1.6) ; les anciens noms (cdx-ui 1.x) restent lus, et ramenés au nom actuel.
    public static readonly string[] Accents = { "rouge", "marine", "ambre", "jean", "tramway", "sarcelle", "chocolat", "lavande", "oxygene", "couchant", "techno", "reve-bleu", "reve-vert", "reve-violet", "lagon", "turquoise" };
    static readonly Dictionary<string, string> Aliases = new() { ["bleu"] = "jean", ["vert"] = "reve-vert", ["violet"] = "reve-violet", ["charbon"] = "techno", ["cdx"] = "rouge" };
    public static string? Canonical(string? s) => s == null ? null : Accents.Contains(s) ? s : Aliases.TryGetValue(s, out var c) ? c : null;
    // CDX_SUITE_DIR remplace le dossier (réservé aux essais : ne jamais toucher aux vrais réglages).
    public static string Dir => Environment.GetEnvironmentVariable("CDX_SUITE_DIR") is { Length: > 0 } d ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CDX");
    public static string FilePath => Path.Combine(Dir, "suite.json");
    static string AppsFile => Path.Combine(Dir, "apps.json");

    public static bool IsAccent([NotNullWhen(true)] string? s) => Canonical(s) != null;

    // %APPDATA%\KDX (ancien nom de la suite) devient %APPDATA%\CDX, une fois.
    public static void MigrateOldDir() => Host.MigrateOldDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KDX"), Dir, "CDX_SUITE_DIR");

    // L'accent écrit dans suite.json, ou `fallback` si le fichier manque, est abîmé ou d'une autre version : une application ne plante jamais pour cela.
    public static string ReadAccent(string fallback = "rouge")
    {
        try { return ParseAccent(System.IO.File.ReadAllText(FilePath), fallback); }
        catch (Exception) { return fallback; }
    }

    // Le contenu d'un suite.json → l'accent (même réponse que cdx_bus.h et cdx_bus.rs : cas communs dans cdx-ui bus/bus-vectors.json, rejoués par --selftest-bus).
    public static string ParseAccent(string json, string fallback = "rouge")
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
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
        AtomicFile.WriteJson(FilePath, new { version = 1, accent });
    }

    // Surveille le fichier ; `onChange` reçoit le nouvel accent, sur le fil qui a appelé Watch, seulement quand il change vraiment.
    // Par événement (FileSystemWatcher sur le dossier : l'écriture atomique arrive en « Renamed ») et un sondage de secours toutes les 5 s si un
    // événement se perd. Avant : un sondage toutes les 250 ms dans chaque programme, une cinquantaine d'accès disque par seconde avec la suite ouverte,
    // pour un fichier qui change quelques fois par mois.
    public static IDisposable Watch(string current, Action<string> onChange)
    {
        var ctx = SynchronizationContext.Current;
        string cur = current; (DateTime, long) last = Stamp(); var gate = new object();
        void Check()
        {
            try
            {
                string a;
                lock (gate)
                {
                    var s = Stamp(); if (s == last) return; last = s;
                    a = ReadAccent(cur);
                    if (a == cur) return;
                    cur = a;
                }
                if (ctx != null) ctx.Post(__ => onChange(a), null); else onChange(a);
            }
            catch (Exception ex) { Host.Log("suite.json : " + ex.Message); }
        }
        FileSystemWatcher? fsw = null;
        try
        {
            Directory.CreateDirectory(Dir);
            fsw = new FileSystemWatcher(Dir, "suite.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            fsw.Changed += (s, e) => Check(); fsw.Created += (s, e) => Check(); fsw.Renamed += (s, e) => Check();
            fsw.Error += (s, e) => Check();
            fsw.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Host.Log("suite.json : surveillance par événement impossible (" + ex.Message + "), sondage seul"); fsw?.Dispose(); fsw = null; }
        var timer = new System.Threading.Timer(_ => Check(), null, fsw == null ? 250 : 5000, fsw == null ? 250 : 5000);
        return new Both(fsw, timer);
    }

    sealed class Both(IDisposable? a, IDisposable? b) : IDisposable { public void Dispose() { a?.Dispose(); b?.Dispose(); } }

    static (DateTime, long) Stamp()
    {
        var f = new FileInfo(FilePath);
        return f.Exists ? (f.LastWriteTimeUtc, f.Length) : (DateTime.MinValue, -1);
    }

    // apps.json : le chemin de l'exécutable de chaque programme de la suite, écrit par chacun au démarrage (voir SUITE.md §3.5).
    // Lire, ajouter sa ligne, réécrire : sous un verrou nommé, sinon deux programmes lancés ensemble effacent l'entrée l'un de l'autre.
    public static void RegisterApp(string name, string exePath)
    {
        try
        {
            using var m = new Mutex(false, @"Local\cdx.apps");
            bool owned = false;
            try { owned = m.WaitOne(2000); } catch (AbandonedMutexException) { owned = true; }
            try
            {
                var map = new SortedDictionary<string, string>();
                try { using var d = JsonDocument.Parse(System.IO.File.ReadAllText(AppsFile)); foreach (var p in d.RootElement.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } path) map[p.Name] = path; } catch (Exception) { }
                if (map.TryGetValue(name, out var old) && old == exePath) return;
                map[name] = exePath;
                AtomicFile.WriteJson(AppsFile, map);
            }
            finally { if (owned) m.ReleaseMutex(); }
        }
        catch (Exception ex) { Host.Log("apps.json : " + ex.Message); }
    }

    // Le chemin enregistré d'une application de la suite (liste blanche : seuls ces noms sont acceptés), ou null.
    public static string? AppPath(string name)
    {
        if (SuiteApps.ByName(name) == null) return null;   // liste blanche : les programmes de la suite (SuiteApps.cs)
        try
        {
            using var d = JsonDocument.Parse(System.IO.File.ReadAllText(AppsFile));
            if (d.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && System.IO.File.Exists(v.GetString())) return v.GetString();
        }
        catch (Exception) { }
        return null;
    }
}

