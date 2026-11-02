using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cdx;

// Une application épinglée. Bar : affichée dans la barre ; Tile : tuile du menu Démarrer. Ic : icône pixel de la charte (repli quand
// la cible n'est pas un fichier dont on peut extraire l'icône : cdx:…, ms-settings:…, shell:…, noms nus comme wt.exe).
sealed record Pin(string Name, string Target, string Ic, bool Bar, bool Tile);

// Les épingles, dans %LOCALAPPDATA%\CdxShell\epingles.json : { "version": 1, "pins": [ { name, target, ic, bar, tile, barIndex } ] }.
// L'ordre du fichier est celui des tuiles du menu Démarrer ; barIndex donne l'ordre dans la barre (les deux sont indépendants).
// Écriture atomique ; fichier absent ou abîmé : valeurs par défaut ; une cible qui n'existe plus ou mal formée est écartée à la lecture.
sealed class PinStore
{
    public const int Max = 60;
    readonly string file;
    public List<Pin> Items { get; private set; }
    readonly List<string> barOrder = new();   // cibles des épingles de la barre, de gauche à droite
    // Les épingles par défaut déjà proposées : une nouvelle application de la suite (Notes…) est ajoutée une fois aux tuiles, puis plus jamais (on peut la retirer).
    readonly HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
    static readonly string[] Legacy = { "cdx:explorer", "ms-settings:", "wt.exe", "chrome.exe", "cdx:navigateur", "cdx:repos", "calc.exe", "notepad.exe", "shell:My Pictures" };   // les neuf d'origine

    // Les icônes des applications de la suite sont celles de la famille pixel (cdx-ui, pixel_icons.py APPS, 01/11/2026) ; les autres, des pictogrammes génériques.
    public static readonly Pin[] Defaults =
    {
        new("Explorateur", "cdx:explorer", "explorateur", true, true), new("Paramètres", "ms-settings:", "reglages", true, true), new("Terminal", "wt.exe", "console", true, true),
        new("Chrome", "chrome.exe", "serveur", true, true), new("Navigateur CDX", "cdx:navigateur", "navigateur", true, true), new("Dépôts", "cdx:repos", "depots", false, true),
        new("Calculatrice", "calc.exe", "apps", false, true), new("Bloc-notes", "notepad.exe", "journal", false, true), new("Images", "shell:My Pictures", "image", false, true),
        new("Notes", "cdx:notes", "notes", false, true), new("Terminal CDX", "cdx:terminal", "terminal", false, true), new("Agenda", "cdx:agenda", "agenda", false, true), new("Courrier", "cdx:courrier", "courrier", false, true), new("Pilote", "cdx:pilote", "pilote", false, true), new("YouTube", "cdx:youtube", "video", false, true), new("Audio Linker", "cdx:audio_linker", "volume", false, true),
    };

    public PinStore(string file) { this.file = file; SetItems(Defaults.ToList()); foreach (var d in Defaults) seen.Add(d.Target); Load(); }

    static readonly Regex BareExe = new(@"^[A-Za-z0-9_.\-]+\.(exe|msc|cpl)$", RegexOptions.IgnoreCase);

    // Ce qu'on accepte d'épingler : un nom court, et une cible que Windows sait lancer (schéma connu, programme nu, ou chemin qui existe).
    public static bool Valid(string name, string target)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || string.IsNullOrWhiteSpace(target) || target.Length > 520) return false;
        if (name.Any(char.IsControl) || target.Any(char.IsControl)) return false;
        if (target.StartsWith("cdx:") || target.StartsWith("ms-settings:") || target.StartsWith("shell:")) return true;
        if (BareExe.IsMatch(target)) return true;
        try { return Path.IsPathFullyQualified(target) && (File.Exists(target) || Directory.Exists(target)); } catch { return false; }
    }

    // Ce qu'on accepte de recevoir par glisser-déposer de fichiers : un programme, un raccourci, un script ou un dossier (pas un document quelconque).
    public static bool Pinnable(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) return false;
            if (Directory.Exists(path)) return true;
            return File.Exists(path) && Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".lnk" or ".bat" or ".cmd" or ".msi";
        }
        catch { return false; }
    }

    void SetItems(List<Pin> items, Dictionary<string, int> barIndex = null)
    {
        Items = items;
        var bar = items.Where(p => p.Bar).Select((p, i) => (p, i)).OrderBy(x => barIndex != null && barIndex.TryGetValue(x.p.Target, out var k) ? k : int.MaxValue).ThenBy(x => x.i).Select(x => x.p.Target);
        barOrder.Clear(); barOrder.AddRange(bar);
    }

    void Load()
    {
        try
        {
            if (!File.Exists(file)) return;
            using var d = JsonDocument.Parse(File.ReadAllText(file));
            var root = d.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var v) || v.GetInt32() != 1 || !root.TryGetProperty("pins", out var arr) || arr.ValueKind != JsonValueKind.Array) { Host.Log("épingles : fichier d'une autre version ou abîmé, valeurs par défaut"); return; }
            string S(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
            bool B(JsonElement e, string k) => e.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.True;
            var list = new List<Pin>(); var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in arr.EnumerateArray())
            {
                string n = S(e, "name"), t = S(e, "target");
                if (t != null && t.StartsWith("kdx:", StringComparison.OrdinalIgnoreCase)) t = "cdx:" + t[4..];   // épingles écrites quand la suite s'appelait KDX
                if (!Valid(n, t) || list.Any(p => Same(p.Target, t))) continue;
                bool bar = B(e, "bar"), tile = B(e, "tile");
                if (!bar && !tile) continue;
                string ic = S(e, "ic"); if (ic != null && !Regex.IsMatch(ic, "^[a-z0-9_-]{1,30}$")) ic = null;
                var suite = Defaults.FirstOrDefault(x => Same(x.Target, t) && x.Target.StartsWith("cdx:", StringComparison.OrdinalIgnoreCase));
                if (suite != null) ic = suite.Ic;   // une application de la suite porte son icône de la famille (01/11/2026), même dans un fichier écrit avec un pictogramme générique
                list.Add(new Pin(n, t, ic, bar, tile));
                if (bar && e.TryGetProperty("barIndex", out var bi) && bi.ValueKind == JsonValueKind.Number && bi.TryGetInt32(out var bii)) idx[t] = bii;
                if (list.Count >= Max) break;
            }
            SetItems(list, idx);
            // une application ajoutée à la suite depuis la dernière écriture du fichier : sa tuile est proposée une fois
            bool hasSeen = root.TryGetProperty("seen", out var sv) && sv.ValueKind == JsonValueKind.Array;
            seen.Clear();
            if (hasSeen) { foreach (var x in sv.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) seen.Add(x.GetString()); }
            else foreach (var t in Legacy) seen.Add(t);
            bool added = false;
            foreach (var dflt in Defaults) if (!seen.Contains(dflt.Target)) { seen.Add(dflt.Target); if (Find(dflt.Target) == null && Items.Count < Max) { Items.Add(dflt with { Bar = false }); added = true; } }
            if (added || !hasSeen) Save();
        }
        catch (Exception ex) { Host.Log("épingles illisibles, valeurs par défaut : " + ex.Message); SetItems(Defaults.ToList()); seen.Clear(); foreach (var d in Defaults) seen.Add(d.Target); }
    }

    void Save()
    {
        try
        {
            var tmp = file + ".tmp";
            var pins = Items.Select(p => new { name = p.Name, target = p.Target, ic = p.Ic, bar = p.Bar, tile = p.Tile, barIndex = p.Bar ? barOrder.FindIndex(t => Same(t, p.Target)) : -1 });
            File.WriteAllText(tmp, JsonSerializer.Serialize(new { version = 1, seen = seen.ToArray(), pins }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            File.Move(tmp, file, true);
        }
        catch (Exception ex) { Host.Log("épingles : écriture échouée : " + ex.Message); }
    }

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    public Pin Find(string target) => Items.FirstOrDefault(p => Same(p.Target, target));
    public List<Pin> OnBar => barOrder.Select(Find).Where(p => p != null).ToList();
    public int BarIndex(string target) => barOrder.FindIndex(t => Same(t, target));   // rang dans la barre, −1 si absente

    // Place une cible dans l'ordre de la barre : devant `before` (une autre épingle de la barre), sinon à la fin.
    void Place(string target, string before)
    {
        barOrder.RemoveAll(t => Same(t, target));
        int at = string.IsNullOrEmpty(before) ? -1 : barOrder.FindIndex(t => Same(t, before));
        if (at < 0) barOrder.Add(target); else barOrder.Insert(at, target);
    }

    // Épingler à la barre : une tuile déjà connue passe à Bar ; sinon une nouvelle épingle, réservée à la barre.
    // `before` : la cible devant laquelle l'insérer (glisser-déposer) ; vide = à droite des autres.
    public bool AddToBar(string name, string target, string ic = null, string before = null)
    {
        if (!Valid(name, target)) return false;
        int i = Items.FindIndex(p => Same(p.Target, target));
        if (i >= 0) { if (!Items[i].Bar) Items[i] = Items[i] with { Bar = true }; }
        else { if (Items.Count >= Max) return false; Items.Add(new Pin(name.Trim(), target, ic, true, false)); }
        Place(Find(target).Target, before);
        Save(); return true;
    }

    // Retirer de la barre : la tuile du menu Démarrer reste ; une épingle qui n'était que dans la barre disparaît.
    public bool RemoveFromBar(string target)
    {
        int i = Items.FindIndex(p => Same(p.Target, target) && p.Bar);
        if (i < 0) return false;
        barOrder.RemoveAll(t => Same(t, target));
        if (Items[i].Tile) Items[i] = Items[i] with { Bar = false }; else Items.RemoveAt(i);
        Save(); return true;
    }

    // Décale d'un cran (−1 ou +1) dans la barre.
    public bool Move(string target, int d)
    {
        int k = barOrder.FindIndex(t => Same(t, target)), k2 = k + d;
        if (k < 0 || (d != -1 && d != 1) || k2 < 0 || k2 >= barOrder.Count) return false;
        (barOrder[k], barOrder[k2]) = (barOrder[k2], barOrder[k]);
        Save(); return true;
    }

    // Glisser une épingle de la barre devant une autre (ou à la fin si `before` est vide).
    public bool MoveTo(string target, string before)
    {
        if (!barOrder.Any(t => Same(t, target)) || Same(target, before ?? "")) return false;
        if (!string.IsNullOrEmpty(before) && !barOrder.Any(t => Same(t, before))) return false;
        var old = barOrder.ToList();
        Place(Find(target).Target, before);
        if (old.SequenceEqual(barOrder)) return false;
        Save(); return true;
    }
}


