using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace Cdx;

// Les disques (type, partages d'un serveur, éjection) et la lecture d'un dossier, envoyée par morceaux. Sorti de Fs.cs (audit du 3/11/2026, 4.6).
static partial class Fs
{
    // ---------- disques : type, partages d'un serveur, éjection ----------
    // Le type d'un disque (fixe, amovible, reseau, cdrom) et son nom affiché : son étiquette, sinon un nom d'après le type, puis sa lettre.
    public static (string kind, string name) DriveKind(DriveInfo d)
    {
        string k; try { k = d.DriveType switch { DriveType.Removable => "amovible", DriveType.Network => "reseau", DriveType.CDRom => "cdrom", _ => "fixe" }; } catch { k = "fixe"; }
        string? label = null; try { label = d.VolumeLabel; } catch { }
        if (string.IsNullOrEmpty(label)) label = k switch { "amovible" => "Disque amovible", "reseau" => "Lecteur réseau", "cdrom" => "Lecteur de DVD", _ => "Disque local" };
        return (k, label + " (" + d.Name.TrimEnd('\\') + ")");
    }

    // « \\serveur » (ou « \\serveur\ ») : la racine d'un serveur du réseau, dont on liste les partages.
    public static bool IsServerRoot([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? p) => p != null && System.Text.RegularExpressions.Regex.IsMatch(p, @"^\\\\[^\\?.][^\\]*\\?$");

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] static extern int NetShareEnum(string servername, int level, out IntPtr bufptr, int prefmaxlen, out int entriesread, out int totalentries, ref int resume_handle);
    [DllImport("netapi32.dll")] static extern int NetApiBufferFree(IntPtr buf);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct SHARE_INFO_1 { public string netname; public uint type; public string remark; }

    // Les partages « disque » visibles d'un serveur (les partages cachés en $ et les imprimantes n'y sont pas), comme un dossier.
    public static object ListShares(string server, string? tab)
    {
        string host = server.Trim('\\');
        int resume = 0; var items = new List<object>();
        int rc = NetShareEnum(host, 1, out var buf, -1, out int read, out _, ref resume);
        if (rc != 0)
        {
            string why = rc is 53 or 1231 or 1707 or 2250 or 6118 ? "Serveur introuvable : " + host : rc == 5 ? "Accès refusé : " + host : $"Partages illisibles ({rc}) : " + host;
            return new { type = "fs.list", tab, error = why };
        }
        try
        {
            int sz = Marshal.SizeOf<SHARE_INFO_1>();
            for (int i = 0; i < read; i++)
            {
                var s = Marshal.PtrToStructure<SHARE_INFO_1>(buf + i * sz);
                if ((s.type & 0x3FFFFFFF) != 0 || s.netname.EndsWith("$")) continue;
                items.Add(new { n = s.netname, p = "\\\\" + host + "\\" + s.netname, d = true, s = -1L, m = 0L, share = true });
            }
        }
        finally { NetApiBufferFree(buf); }
        Host.Log($"explorateur : partages de {host} ({items.Count})");
        return new { type = "fs.list", tab, path = "\\\\" + host, items, free = -1L, more = false };
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, byte[]? inBuf, int inSize, IntPtr outBuf, int outSize, out int ret, IntPtr ov);

    // Éjecte une clé USB ou un DVD (« E:\ ») : le volume est verrouillé (refusé si un programme y a un fichier ouvert), démonté, puis le support libéré. Null si c'est fait, sinon pourquoi pas.
    public static string? Eject(string? drive)
    {
        if (string.IsNullOrEmpty(drive) || !System.Text.RegularExpressions.Regex.IsMatch(drive, @"^[A-Za-z]:\\?$")) return "Ce n'est pas un disque.";
        try { var di = new DriveInfo(drive[..1]); if (di.DriveType != DriveType.Removable && di.DriveType != DriveType.CDRom) return "Ce disque n'est pas amovible."; }
        catch (Exception ex) { return ex.Message; }
        using var h = CreateFile(@"\\.\" + drive[..2], 0xC0000000 /* lecture et écriture */, 3 /* partagé */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (h.IsInvalid) return "Impossible d'ouvrir le disque (" + Marshal.GetLastWin32Error() + ").";
        bool locked = false;
        for (int i = 0; i < 5 && !locked; i++) { locked = DeviceIoControl(h, 0x00090018 /* FSCTL_LOCK_VOLUME */, null, 0, IntPtr.Zero, 0, out _, IntPtr.Zero); if (!locked) Thread.Sleep(200); }
        if (!locked) return "Le disque est encore utilisé par un programme : ferme les fichiers ouverts dessus.";
        DeviceIoControl(h, 0x00090020 /* FSCTL_DISMOUNT_VOLUME */, null, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        DeviceIoControl(h, 0x002D4804 /* IOCTL_STORAGE_MEDIA_REMOVAL : ne plus retenir le support */, new byte[] { 0 }, 1, IntPtr.Zero, 0, out _, IntPtr.Zero);
        if (!DeviceIoControl(h, 0x002D4808 /* IOCTL_STORAGE_EJECT_MEDIA */, null, 0, IntPtr.Zero, 0, out _, IntPtr.Zero)) return "Éjection refusée par Windows (" + Marshal.GetLastWin32Error() + ").";
        Host.Log("explorateur : éjecté " + drive);
        return null;
    }

    static object ListDrives(string? tab)
    {
        var items = DriveInfo.GetDrives().Where(d => { try { return d.IsReady; } catch { return false; } }).Select(d => (object)new
        {
            n = DriveKind(d).name, p = d.Name, d = true, drive = true, kind = DriveKind(d).kind,
            s = d.TotalSize, free = d.AvailableFreeSpace, m = 0L
        }).ToList();
        return new { type = "fs.list", tab, path = "", items, free = -1L };
    }

    // Faut-il montrer les éléments cachés et système ? Réglage « show.hidden » de l'Explorateur (faux par défaut), le même pour toutes les fenêtres.
    static bool ShowHidden() => Settings.Get("show.hidden") is { } n && n.GetValueKind() == JsonValueKind.True;

    // Lit un dossier et l'envoie par paquets de `chunk` éléments : « fs.list » (le premier, avec le chemin et l'espace libre ; `more` dit s'il en vient d'autres) puis « fs.list.more ».
    // Un très grand dossier s'affiche donc dès le premier paquet. `ct` : la lecture s'arrête dès que la page a demandé un autre dossier pour cet onglet (OperationCanceledException,
    // que l'appelant ignore). Un élément caché est marqué h:true (la page l'estompe).
    public static void ListStream(string? path, string? tab, CancellationToken ct, Action<object> emit, int chunk = 4000)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) { emit(ListDrives(tab)); return; }
            if (path == Recycle.TrashPath) { emit(ListTrash(tab, ct)); return; }
            if (IsServerRoot(path)) { emit(ListShares(path, tab)); return; }
            var di = new DirectoryInfo(path);
            if (!di.Exists) { emit(new { type = "fs.list", tab, error = "Dossier introuvable : " + path }); return; }
            bool showHidden = ShowHidden();
            long free = -1; try { free = new DriveInfo(di.Root.FullName).AvailableFreeSpace; } catch { }
            string full = di.FullName;
            var items = new List<object>(Math.Min(chunk, 4096)); bool first = true; int total = 0;
            foreach (var e in di.EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var a = e.Attributes;
                    bool hid = (a & (FileAttributes.Hidden | FileAttributes.System)) != 0;
                    if (hid && !showHidden) continue;
                    bool d = (a & FileAttributes.Directory) != 0;
                    long m = new DateTimeOffset(e.LastWriteTime).ToUnixTimeMilliseconds();
                    items.Add(hid ? new { n = e.Name, d, s = d ? -1L : ((FileInfo)e).Length, m, h = true } : new { n = e.Name, d, s = d ? -1L : ((FileInfo)e).Length, m });
                    total++;
                }
                catch { }
                if (items.Count >= chunk)
                {
                    emit(first ? new { type = "fs.list", tab, path = full, items, free, more = true } : (object)new { type = "fs.list.more", tab, path = full, items, more = true });
                    items = new List<object>(Math.Min(chunk, 4096)); first = false;
                }
            }
            emit(first ? new { type = "fs.list", tab, path = full, items, free, more = false } : (object)new { type = "fs.list.more", tab, path = full, items, more = false });
            Host.Log($"explorateur : {full} ({total} éléments)");
        }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException) { emit(new { type = "fs.list", tab, error = "Accès refusé : " + path }); }
        catch (Exception ex) { emit(new { type = "fs.list", tab, error = ex.Message }); }
    }

    // La corbeille comme un dossier : chaque élément porte son nom d'origine (b), son dossier d'origine (r), sa date de suppression (m), sa taille, son chemin dans la corbeille (p) et son identifiant (id).
    static object ListTrash(string? tab, CancellationToken ct)
    {
        var items = Recycle.List().Select(e => (object)new { n = Path.GetFileName(e.DataPath), b = Path.GetFileName(e.Original.TrimEnd('\\')), r = Path.GetDirectoryName(e.Original) ?? "", d = e.IsDir, s = e.IsDir ? -1L : e.Size, m = new DateTimeOffset(e.Deleted).ToUnixTimeMilliseconds(), p = e.DataPath, o = e.Original, id = e.Id }).ToList();
        ct.ThrowIfCancellationRequested();
        return new { type = "fs.list", tab, path = Recycle.TrashPath, items, free = -1L, more = false };
    }

    // La lecture d'un coup (essais, commandes synchrones) : un seul message, tous les éléments.
    public static object List(string? path, string? tab, CancellationToken ct = default)
    {
        object? result = null;
        ListStream(path, tab, ct, o => result = o, int.MaxValue);
        return result!;   // « ! » : ListStream envoie toujours un message (ou lève)
    }
}
