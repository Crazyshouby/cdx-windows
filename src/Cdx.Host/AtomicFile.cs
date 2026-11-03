// Annotations de nullabilité actives même dans Cdx.Vault, qui compile ce fichier sans les activer.
#nullable enable
using System.Text;
using System.Text.Json;

namespace Cdx;

// L'écriture d'un fichier d'état (réglages, historique, favoris, jeton…), en une seule version pour toute la suite :
// un plantage ou une coupure pendant l'écriture ne laisse jamais un fichier tronqué, et deux processus qui écrivent le même fichier
// ne se marchent pas dessus. Le fichier temporaire est propre à chaque appel et écrit jusqu'au disque, puis renommé par-dessus l'ancien ;
// si un lecteur tient le fichier à cet instant, le renommage est retenté. Le temporaire ne reste jamais en cas d'échec.
// Ce fichier est aussi compilé dans Cdx.Vault (lien dans Cdx.Vault.csproj) : il ne dépend de rien d'autre.
static class AtomicFile
{
    static readonly UTF8Encoding Utf8 = new(false);
    static int seq;

    public static void WriteAllText(string path, string text, string tmpSuffix = ".tmp") => WriteAllBytes(path, Utf8.GetBytes(text), tmpSuffix);

    public static void WriteJson<T>(string path, T value, JsonSerializerOptions? options = null) => WriteAllText(path, JsonSerializer.Serialize(value, options));

    // `tmpSuffix` : le coffre de notes garde « .cdx-tmp », que sa lecture ignore.
    public static void WriteAllBytes(string path, byte[] bytes, string tmpSuffix = ".tmp")
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + "." + Environment.ProcessId + "-" + Interlocked.Increment(ref seq) + tmpSuffix;   // unique : processus et appel
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);   // jusqu'au disque, pas seulement jusqu'au cache de Windows
            }
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(tmp, path, overwrite: true); return; }
                catch (Exception ex) when (attempt < 3 && (ex is UnauthorizedAccessException || ex is IOException)) { Thread.Sleep(50); }
            }
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }
}
