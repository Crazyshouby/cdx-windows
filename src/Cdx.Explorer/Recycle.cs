using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace Cdx;

// La corbeille de Windows, lue et pilotée sans passer par ses boîtes de dialogue : chaque disque fixe a un dossier « $Recycle.Bin\<identifiant de l'utilisateur> » où un élément supprimé devient
// deux fichiers — « $Rxxxxxx.ext » (l'élément lui-même) et « $Ixxxxxx.ext » (son origine : chemin d'avant, taille, date de suppression). Lire ces « $I », restaurer (déplacer le « $R » à sa place,
// effacer le « $I »), effacer pour de bon : c'est ce que fait l'Explorateur de Windows. Les identifiants d'élément (Id) sont les chemins des « $I » ; la page ne peut en désigner aucun autre.
sealed record BinEntry(string Id, string DataPath, string Original, long Size, DateTime Deleted, bool IsDir);

static class Recycle
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);

    // Le « chemin » qui désigne la corbeille dans l'Explorateur (la page l'utilise comme un dossier).
    public const string TrashPath = "::corbeille";

    static string sid;
    static string Sid => sid ??= WindowsIdentity.GetCurrent().User?.Value ?? "";

    static IEnumerable<string> Roots()
    {
        foreach (var d in DriveInfo.GetDrives()) { bool ok; try { ok = d.DriveType == DriveType.Fixed && d.IsReady; } catch { ok = false; } if (ok) yield return d.Name; }
    }

    public static string BinDir(string root) => Path.Combine(root, "$Recycle.Bin", Sid);

    // Le contenu d'un « $I » : version 1 (Vista, 7, 8 : 8 + 8 + 8 octets, puis le chemin sur 520 octets) ou 2 (10, 11 : même entête, puis la longueur du chemin en caractères, puis le chemin).
    public static (long size, DateTime deleted, string original)? ParseInfo(byte[] b)
    {
        if (b == null || b.Length < 28) return null;
        long ver = BitConverter.ToInt64(b, 0), size = BitConverter.ToInt64(b, 8), ft = BitConverter.ToInt64(b, 16);
        string path;
        if (ver == 2)
        {
            int n = BitConverter.ToInt32(b, 24);
            if (n <= 0 || n > 32768 || 28 + n * 2 > b.Length) return null;
            path = Encoding.Unicode.GetString(b, 28, n * 2);
        }
        else if (ver == 1) path = Encoding.Unicode.GetString(b, 24, Math.Min(520, b.Length - 24));
        else return null;
        int z = path.IndexOf('\0'); if (z >= 0) path = path[..z];
        if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path)) return null;
        DateTime t; try { t = DateTime.FromFileTimeUtc(ft).ToLocalTime(); } catch { return null; }
        return (size, t, path);
    }

    // Les éléments de la corbeille d'un disque (ou de tous les disques fixes si `root` est null).
    public static List<BinEntry> List(string root = null)
    {
        var res = new List<BinEntry>();
        foreach (var r in root != null ? new[] { root } : Roots().ToArray())
        {
            string dir; try { dir = BinDir(r); if (!Directory.Exists(dir)) continue; } catch { continue; }
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "$I*"))
                {
                    try
                    {
                        var info = ParseInfo(File.ReadAllBytes(f)); if (info == null) continue;
                        string data = Path.Combine(dir, "$R" + Path.GetFileName(f)[2..]);
                        bool isDir = Directory.Exists(data); if (!isDir && !File.Exists(data)) continue;   // un « $I » sans « $R » : reste d'un effacement interrompu
                        res.Add(new BinEntry(f, data, info.Value.original, info.Value.size, info.Value.deleted, isDir));
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Host.Log("corbeille " + r + " : " + ex.Message); }
        }
        return res;
    }

    // Un identifiant venu de la page est-il bien un « $I » de la corbeille de l'utilisateur ?
    public static bool IsEntryId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 520 || !Path.IsPathRooted(id) || !Path.GetFileName(id).StartsWith("$I", StringComparison.Ordinal)) return false;
        try { string dir = Path.GetDirectoryName(id); return Roots().Any(r => string.Equals(BinDir(r).TrimEnd('\\'), dir?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)); } catch { return false; }
    }

    static BinEntry Read(string id)
    {
        if (!IsEntryId(id) || !File.Exists(id)) return null;
        var info = ParseInfo(File.ReadAllBytes(id)); if (info == null) return null;
        string data = Path.Combine(Path.GetDirectoryName(id), "$R" + Path.GetFileName(id)[2..]); bool isDir = Directory.Exists(data);
        return isDir || File.Exists(data) ? new BinEntry(id, data, info.Value.original, info.Value.size, info.Value.deleted, isDir) : null;
    }

    // Met un fichier ou un dossier à la corbeille (sans autre boîte que celles d'erreur) et rend son entrée ; null si Windows l'a supprimé pour de bon (disque réseau, élément trop gros...).
    public static BinEntry DeleteToBin(string path)
    {
        // Le Shell (SHFileOperation, sous FileSystem) veut un fil STA : celui de la file de tâches ne l'est pas, on lui en prête un le temps de l'opération.
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            BinEntry res = null; Exception err = null;
            var t = new Thread(() => { try { res = DeleteToBin(path); } catch (Exception ex) { err = ex; } }) { IsBackground = true, Name = "corbeille" };
            t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
            if (err != null) throw err;
            return res;
        }
        DateTime since = DateTime.Now.AddSeconds(-5);
        bool dir = Directory.Exists(path);
        if (dir) FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        else FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        string root = Path.GetPathRoot(path);
        return List(root).Where(e => e.Deleted >= since && string.Equals(e.Original.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).OrderByDescending(e => e.Deleted).FirstOrDefault();
    }

    // Remet l'élément à sa place d'origine (le dossier est recréé s'il a disparu). Rend (message d'erreur ou null, chemin retrouvé). Un nom déjà pris à l'emplacement d'origine n'est jamais écrasé.
    public static (string error, string dest) Restore(string id)
    {
        try
        {
            var e = Read(id); if (e == null) return ("L'élément n'est plus dans la corbeille.", null);
            if (Path.Exists(e.Original)) return ("« " + Path.GetFileName(e.Original) + " » existe déjà à son emplacement d'origine.", e.Original);
            Directory.CreateDirectory(Path.GetDirectoryName(e.Original));
            if (e.IsDir) Directory.Move(e.DataPath, e.Original); else File.Move(e.DataPath, e.Original);
            File.Delete(e.Id);
            return (null, e.Original);
        }
        catch (Exception ex) { return (ex.Message, null); }
    }

    static void DeleteTree(string path)
    {
        if (Directory.Exists(path))
        {
            foreach (var f in Directory.EnumerateFileSystemEntries(path, "*", System.IO.SearchOption.AllDirectories)) try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            Directory.Delete(path, true);
        }
        else if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
    }

    // Efface pour de bon des éléments de la corbeille (jamais irrécupérables par erreur : la page demande confirmation avant).
    public static string Purge(IEnumerable<string> ids)
    {
        string err = null;
        foreach (var id in ids)
        {
            try { var e = Read(id); if (e == null) continue; DeleteTree(e.DataPath); File.Delete(e.Id); }
            catch (Exception ex) { err ??= ex.Message; }
        }
        return err;
    }

    // Vide toute la corbeille, comme « Vider la corbeille » de Windows (sans confirmation ni son : la page a déjà demandé). Jamais appelée par un essai.
    public static bool EmptyAll() => SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4 /* sans confirmation, sans progression, sans son */) == 0;

    // Supprime des éléments sans passer par la corbeille (Maj+Suppr, après confirmation) ; sans autre boîte. Rend le premier message d'erreur.
    public static string DeletePermanently(IEnumerable<string> paths)
    {
        string err = null;
        foreach (var p in paths)
        {
            if (string.IsNullOrEmpty(p) || !Path.IsPathRooted(p) || p.Length > 520 || Path.GetFileName(p.TrimEnd('\\')).Length == 0) continue;   // jamais la racine d'un disque
            try { DeleteTree(p); } catch (Exception ex) { err ??= ex.Message; }
        }
        return err;
    }
}
