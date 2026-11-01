using System.Text;
using System.Text.Json;
using WeCantSpell.Hunspell;

namespace Cdx;

// Le correcteur orthographique de la suite (décision du 01/11/2026) : Hunspell en C# pur (WeCantSpell.Hunspell) et le dictionnaire français de Dicollecte
// (web/dict/fr : fr.aff, fr.dic, licence MPL 2.0 ; celui de LibreOffice et de Firefox). Le correcteur de Chromium ne convenait pas : sa langue suit celle de
// Windows et non le texte (bogue WebView2 n° 5294), et avec nos menus à nous (AreDefaultContextMenusEnabled = false) ses suggestions n'arrivent jamais
// (ContextMenuRequested n'est pas levé). Les pages (cdx-spell.js) envoient les mots à vérifier et demandent des suggestions au clic droit ; les mots ajoutés
// vont dans dictionnaire-perso.txt du dossier des données. Le dictionnaire (1,4 Mo, environ une seconde) se charge en arrière-plan au premier mot.
static class Spell
{
    public static string DictDir { get; set; }        // essais : un autre dossier (sinon CDX_DICT_DIR, sinon web/dict/fr)
    public static string PersonalFile { get; set; }   // essais : un fichier jetable (sinon dictionnaire-perso.txt du dossier des données)
    static WordList words; static Task loading; static readonly object gate = new();
    static HashSet<string> personal;
    static readonly HashSet<string> Elisions = new(StringComparer.OrdinalIgnoreCase) { "l", "d", "qu", "n", "s", "j", "m", "t", "c", "jusqu", "lorsqu", "puisqu", "quoiqu" };

    static string Dir { get { if (DictDir != null) return DictDir; string env = Environment.GetEnvironmentVariable("CDX_DICT_DIR"); return !string.IsNullOrEmpty(env) ? env : Path.Combine(Host.FindWebRoot(), "dict", "fr"); } }
    static string Perso => PersonalFile ?? Path.Combine(Host.DataDir, "dictionnaire-perso.txt");
    public static bool Available { get { try { return File.Exists(Path.Combine(Dir, "fr.dic")) && File.Exists(Path.Combine(Dir, "fr.aff")); } catch (Exception) { return false; } } }
    public static bool Ready => words != null;

    public static Task Load()
    {
        lock (gate)
        {
            return loading ??= Task.Run(() =>
            {
                try
                {
                    string dir = Dir; var sw = System.Diagnostics.Stopwatch.StartNew();
                    var list = WordList.CreateFromFiles(Path.Combine(dir, "fr.dic"), Path.Combine(dir, "fr.aff"));
                    LoadPersonal(); words = list;
                    Host.Log($"correcteur : dictionnaire chargé ({sw.ElapsedMilliseconds} ms)");
                }
                catch (Exception ex) { Host.Log("correcteur : pas de dictionnaire (" + ex.Message + ")"); }
            });
        }
    }

    static void LoadPersonal()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try { if (File.Exists(Perso)) foreach (var l in File.ReadAllLines(Perso, Encoding.UTF8)) { string w = l.Trim(); if (w.Length > 0) set.Add(w); } }
        catch (Exception ex) { Host.Log("correcteur : " + ex.Message); }
        lock (gate) personal = set;
    }

    // Le mot existe-t-il ? Hunspell traite lui-même les majuscules (« Bonjour » en début de phrase, « BONJOUR ») ; on ajoute les élisions (« l'école »,
    // « qu'il », apostrophe droite ou typographique) et les mots composés par trait d'union dont chaque partie existe (« dis-moi »).
    public static bool Check(string w)
    {
        if (string.IsNullOrEmpty(w) || words == null || IsPersonal(w)) return true;
        if (words.Check(w)) return true;
        int ap = w.IndexOfAny(new[] { '\'', '’' });
        if (ap > 0 && ap + 1 < w.Length && Elisions.Contains(w[..ap])) return Check(w[(ap + 1)..]);
        if (w.Contains('-')) { var parts = w.Split('-', StringSplitOptions.RemoveEmptyEntries); if (parts.Length > 1 && parts.All(Check)) return true; }
        return false;
    }
    static bool IsPersonal(string w)
    {
        lock (gate)
        {
            if (personal == null || personal.Count == 0) return false;
            return personal.Contains(w) || personal.Contains(w.ToLowerInvariant()) || (w.Length > 1 && char.IsUpper(w[0]) && personal.Contains(char.ToLowerInvariant(w[0]) + w[1..]));
        }
    }

    public static List<string> Suggest(string w, int max = 8)
    {
        var list = new List<string>();
        if (words == null || string.IsNullOrEmpty(w)) return list;
        try { foreach (var s in words.Suggest(w)) { if (!list.Contains(s)) list.Add(s); if (list.Count >= max) break; } }
        catch (Exception ex) { Host.Log("correcteur : " + ex.Message); }
        return list;
    }

    // Ajoute un mot au dictionnaire personnel (une ligne par mot, UTF-8).
    public static void Add(string w)
    {
        w = (w ?? "").Trim(); if (w.Length == 0) return;
        lock (gate) { personal ??= new HashSet<string>(StringComparer.Ordinal); if (!personal.Add(w)) return; }
        try { File.AppendAllText(Perso, w + "\n", new UTF8Encoding(false)); } catch (Exception ex) { Host.Log("correcteur : " + ex.Message); }
    }

    // Les messages des pages (cdx-spell.js). Rend la réponse à envoyer à la page, ou null si le message n'est pas pour le correcteur.
    // Synchrone : le dictionnaire doit être chargé (Load() attendu), sinon la page reçoit sp.off (rien n'est souligné).
    public static object Reply(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json); var r = d.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("cmd", out var c) || c.ValueKind != JsonValueKind.String) return null;
            string cmd = c.GetString();
            if (!cmd.StartsWith("sp.")) return null;
            string id = r.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : "";
            if (words == null) return new { type = "sp.off" };
            switch (cmd)
            {
                case "sp.check":
                    var bad = new List<string>();
                    if (r.TryGetProperty("words", out var ws) && ws.ValueKind == JsonValueKind.Array) foreach (var x in ws.EnumerateArray()) { string w = x.GetString(); if (w != null && !Check(w)) bad.Add(w); }
                    return new { type = "sp.result", id, bad };
                case "sp.suggest":
                    string word = r.TryGetProperty("word", out var wd) ? wd.GetString() ?? "" : "";
                    return new { type = "sp.suggestions", id, word, list = Suggest(word) };
                case "sp.add":
                    string add = (r.TryGetProperty("word", out var ad) ? ad.GetString() ?? "" : "").Trim();
                    if (add.Length == 0) return null;
                    Add(add); return new { type = "sp.added", word = add };
                default: return null;
            }
        }
        catch (Exception ex) { Host.Log("correcteur : " + ex.Message); return null; }
    }

    // Depuis HostForm : vrai si le message est pour le correcteur ; la réponse part en arrière-plan (le temps de charger le dictionnaire la première fois).
    public static bool Handle(HostForm form, string json)
    {
        if (!json.Contains("\"cmd\":\"sp.")) return false;
        _ = Task.Run(async () =>
        {
            await Load();
            var reply = Reply(json);
            if (reply == null) return;
            try { if (!form.IsDisposed) form.BeginInvoke(() => { if (!form.IsDisposed) form.Send(reply); }); } catch (Exception) { }
        });
        return true;
    }
}
