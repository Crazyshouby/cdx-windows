using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cdx;

// Les réglages de l'Explorateur : un objet JSON dans %LOCALAPPDATA%\CdxShell\explorer.json (ou CDX_DATA_DIR), partagé par toutes les fenêtres : éléments cachés,
// extensions, vue et tri par dossier, dossiers épinglés, récents, session des onglets... La page demande (fs.settings.get) et enregistre une clé à la fois
// (fs.settings.set) ; l'hôte écrit de façon atomique et renvoie les réglages à toutes les fenêtres. Un fichier absent ou illisible donne des réglages vides.
static class Settings
{
    const int MaxValueBytes = 256 * 1024, MaxKeys = 200;
    static readonly Regex KeyOk = new(@"^[a-z0-9][a-z0-9._\-]{0,63}$", RegexOptions.Compiled);
    static readonly object gate = new();
    static JsonObject cache;

    public static string FilePath => Path.Combine(Host.DataDir, "explorer.json");

    // Pour les essais : oublier la copie en mémoire (le prochain accès relit le fichier).
    public static void Reset() { lock (gate) cache = null; }

    static JsonObject Load()
    {
        if (cache != null) return cache;
        try
        {
            if (File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject o) return cache = o;
        }
        catch (Exception ex) { Host.Log("explorer.json illisible, réglages vides : " + ex.Message); }
        return cache = new JsonObject();
    }

    // Tous les réglages, en JSON (à envoyer à la page).
    public static JsonElement All()
    {
        lock (gate) { using var d = JsonDocument.Parse(Load().ToJsonString()); return d.RootElement.Clone(); }
    }

    public static JsonNode Get(string key)
    {
        lock (gate) { var v = Load()[key]; return v?.DeepClone(); }
    }

    // Enregistre (ou, avec une valeur null, efface) un réglage. Refusé : clé hors du motif, valeur de plus de 256 Ko, plus de 200 clés. True : enregistré.
    public static bool Set(string key, JsonElement value)
    {
        if (key == null || !KeyOk.IsMatch(key)) return false;
        lock (gate)
        {
            var o = Load();
            if (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Undefined) o.Remove(key);
            else
            {
                string raw = value.GetRawText();
                if (raw.Length > MaxValueBytes || (!o.ContainsKey(key) && o.Count >= MaxKeys)) return false;
                o[key] = JsonNode.Parse(raw);
            }
            Save(o);
            return true;
        }
    }

    static void Save(JsonObject o)
    {
        try
        {
            Directory.CreateDirectory(Host.DataDir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, o.ToJsonString());
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex) { Host.Log("explorer.json : " + ex.Message); }
    }
}
