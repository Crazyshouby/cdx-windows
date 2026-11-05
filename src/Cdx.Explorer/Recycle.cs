using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Cdx;

// La corbeille de Windows, lue et pilotée sans passer par ses boîtes de dialogue : chaque disque fixe a un dossier « $Recycle.Bin\<identifiant de l'utilisateur> » où un élément supprimé devient
// deux fichiers — « $Rxxxxxx.ext » (l'élément lui-même) et « $Ixxxxxx.ext » (son origine : chemin d'avant, taille, date de suppression). Lire ces « $I », restaurer (déplacer le « $R » à sa place,
// effacer le « $I »), effacer pour de bon : c'est ce que fait l'Explorateur de Windows. Les identifiants d'élément (Id) sont les chemins des « $I » ; la page ne peut en désigner aucun autre.
sealed record BinEntry(string Id, string DataPath, string Original, long Size, DateTime Deleted, bool IsDir);

static class Recycle
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);

    // Le « chemin » qui désigne la corbeille dans l'Explorateur (la page l'utilise comme un dossier).
    public const string TrashPath = "::corbeille";

    static string? sid;
    static string Sid => sid ??= WindowsIdentity.GetCurrent().User?.Value ?? "";

    static IEnumerable<string> Roots()
    {
        foreach (var d in DriveInfo.GetDrives()) { bool ok; try { ok = d.DriveType == DriveType.Fixed && d.IsReady; } catch { ok = false; } if (ok) yield return d.Name; }
    }

    public static string BinDir(string root) => Path.Combine(root, "$Recycle.Bin", Sid);

    // Le contenu d'un « $I » : version 1 (Vista, 7, 8 : 8 + 8 + 8 octets, puis le chemin sur 520 octets) ou 2 (10, 11 : même entête, puis la longueur du chemin en caractères, puis le chemin).
    public static (long size, DateTime deleted, string original)? ParseInfo(byte[]? b)
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
    public static List<BinEntry> List(string? root = null)
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
    public static bool IsEntryId([NotNullWhen(true)] string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 520 || !Path.IsPathRooted(id) || !Path.GetFileName(id).StartsWith("$I", StringComparison.Ordinal)) return false;
        try { string? dir = Path.GetDirectoryName(id); return Roots().Any(r => string.Equals(BinDir(r).TrimEnd('\\'), dir?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)); } catch { return false; }
    }

    static BinEntry? Read(string? id)
    {
        if (!IsEntryId(id) || !File.Exists(id)) return null;
        var info = ParseInfo(File.ReadAllBytes(id)); if (info == null) return null;
        string data = Path.Combine(Path.GetDirectoryName(id)!, "$R" + Path.GetFileName(id)[2..]); bool isDir = Directory.Exists(data);   // « ! » : IsEntryId a vérifié que id est dans un dossier $Recycle.Bin
        return isDir || File.Exists(data) ? new BinEntry(id, data, info.Value.original, info.Value.size, info.Value.deleted, isDir) : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint QueryDosDevice(string device, StringBuilder target, int max);

    // Ce chemin a-t-il une corbeille ? Non pour un partage réseau, une clé USB (amovible), un lecteur créé par subst, ou tout disque non fixe :
    // Windows y supprime pour de bon. Le cas « trop gros pour la corbeille » ne se voit pas d'avance : DeleteToBin le fait demander par Windows.
    public static bool HasBin(string path)
    {
        string root; try { root = Path.GetPathRoot(Path.GetFullPath(path)) ?? ""; } catch { return false; }
        if (root.Length < 2 || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        try { if (new DriveInfo(root).DriveType != DriveType.Fixed) return false; } catch { return false; }
        var sb = new StringBuilder(1024);
        if (QueryDosDevice(root[..2], sb, sb.Capacity) > 0 && sb.ToString().StartsWith(@"\??\", StringComparison.Ordinal)) return false;   // subst : X: → \??\C:\dossier
        return true;
    }

    // Met un fichier ou un dossier à la corbeille et rend son entrée ; null si Windows l'a supprimé pour de bon.
    // Par IFileOperation, sans boîte de confirmation ni de progression, mais avec l'avertissement de Windows quand un élément ne peut pas aller à la corbeille
    // (trop gros, disque sans corbeille) : l'utilisateur choisit, au lieu d'une suppression définitive silencieuse. S'il refuse, OperationCanceledException.
    public static BinEntry? DeleteToBin(string path, IntPtr owner = default)
    {
        // Le Shell veut un fil STA : celui de la file de tâches ne l'est pas, on lui en prête un le temps de l'opération.
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            BinEntry? res = null; Exception? err = null;
            var t = new Thread(() => { try { res = DeleteToBin(path, owner); } catch (Exception ex) { err = ex; } }) { IsBackground = true, Name = "corbeille" };
            t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
            if (err != null) throw err;
            return res;
        }
        DateTime since = DateTime.Now.AddSeconds(-5);
        ShellDelete(path, owner);
        string? root = Path.GetPathRoot(path);
        return List(root).Where(e => e.Deleted >= since && string.Equals(e.Original.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).OrderByDescending(e => e.Deleted).FirstOrDefault();
    }

    // ---- IFileOperation (shobjidl.h), réduit à la suppression ----
    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);
        void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        void MoveItem(IShellItem item, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);
        void MoveItems(IntPtr items, IShellItem dest);
        void CopyItem(IShellItem item, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);
        void CopyItems(IntPtr items, IShellItem dest);
        void DeleteItem(IShellItem item, IntPtr sink);
        void DeleteItems(IntPtr items);
        void NewItem(IShellItem dest, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        void PerformOperations();
        [return: MarshalAs(UnmanagedType.Bool)] bool GetAnyOperationsAborted();
    }
    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem { }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);
    static readonly Guid FileOperationClsid = new("3ad05575-8857-4850-9277-11b85bdb8e09");
    const uint FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000, FOFX_RECYCLEONDELETE = 0x80000;

    static void ShellDelete(string path, IntPtr owner)
    {
        IFileOperation? op = null; IShellItem? item = null;
        try
        {
            op = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(FileOperationClsid, true)!)!;   // « ! » : avec throwOnError, GetTypeFromCLSID lève plutôt que de rendre null ; CreateInstance ne rend null que pour un Nullable<T>
            // Pas de « Voulez-vous vraiment… » (la page a déjà demandé), mais l'avertissement de suppression définitive reste (FOF_WANTNUKEWARNING l'emporte sur FOF_NOCONFIRMATION).
            op.SetOperationFlags(FOF_ALLOWUNDO | FOFX_RECYCLEONDELETE | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING | FOF_SILENT | FOF_NOERRORUI);
            if (owner != IntPtr.Zero) op.SetOwnerWindow(owner);
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItem).GUID, out item);
            op.DeleteItem(item, IntPtr.Zero);
            try { op.PerformOperations(); }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80270000) /* COPYENGINE_E_USER_CANCELLED */ || ex.HResult == unchecked((int)0x800704C7) /* ERROR_CANCELLED */) { }
            if (op.GetAnyOperationsAborted() && Path.Exists(path))
                throw new OperationCanceledException("« " + Path.GetFileName(path.TrimEnd('\\')) + " » n'a pas été supprimé : il ne pouvait pas aller à la corbeille.");
        }
        finally
        {
            if (item != null) Marshal.ReleaseComObject(item);
            if (op != null) Marshal.ReleaseComObject(op);
        }
    }

    // Remet l'élément à sa place d'origine (le dossier est recréé s'il a disparu). Rend (message d'erreur ou null, chemin retrouvé). Un nom déjà pris à l'emplacement d'origine n'est jamais écrasé.
    public static (string? error, string? dest) Restore(string id)
    {
        try
        {
            var e = Read(id); if (e == null) return ("L'élément n'est plus dans la corbeille.", null);
            if (Path.Exists(e.Original)) return ("« " + Path.GetFileName(e.Original) + " » existe déjà à son emplacement d'origine.", e.Original);
            Directory.CreateDirectory(Path.GetDirectoryName(e.Original)!);   // « ! » : l'origine d'une entrée est un chemin complet (ParseInfo), jamais une racine de disque
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
    public static string? Purge(IEnumerable<string?> ids)
    {
        string? err = null;
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
    public static string? DeletePermanently(IEnumerable<string?> paths)
    {
        string? err = null;
        foreach (var p in paths)
        {
            if (string.IsNullOrEmpty(p) || !Path.IsPathRooted(p) || p.Length > 520 || Path.GetFileName(p.TrimEnd('\\')).Length == 0) continue;   // jamais la racine d'un disque
            try { DeleteTree(p); } catch (Exception ex) { err ??= ex.Message; }
        }
        return err;
    }
}
