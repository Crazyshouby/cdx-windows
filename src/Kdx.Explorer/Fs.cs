using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace Kdx;

// Le côté « système de fichiers » de l'Explorateur KDX : tout ce que la page demande par des messages « fs.* ».
// Les opérations lourdes (copie, déplacement) passent par Microsoft.VisualBasic.FileIO, qui affiche les boîtes de dialogue de Windows
// pour les conflits et la progression ; la suppression envoie à la corbeille.
static class Fs
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool SHObjectProperties(IntPtr hwnd, uint type, string obj, string page);

    public static object Places()
    {
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var places = new[]
        {
            new { name = "Accueil", path = user, ic = "utilisateur" },
            new { name = "Bureau", path = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ic = "pc" },
            new { name = "Documents", path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), ic = "dossier" },
            new { name = "Images", path = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), ic = "image" },
            new { name = "Musique", path = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), ic = "musique" },
            new { name = "Vidéos", path = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), ic = "video" },
            new { name = "Téléchargements", path = Path.Combine(user, "Downloads"), ic = "telecharger" },
        };
        var drives = DriveInfo.GetDrives().Where(d => { try { return d.IsReady; } catch { return false; } })
            .Select(d => new { name = (string.IsNullOrEmpty(d.VolumeLabel) ? "Disque local" : d.VolumeLabel) + " (" + d.Name.TrimEnd('\\') + ")", path = d.Name, ic = "disque" }).ToList();
        return new { type = "places", places, drives, home = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
    }

    static object ListDrives(string tab)
    {
        var items = DriveInfo.GetDrives().Where(d => { try { return d.IsReady; } catch { return false; } }).Select(d => (object)new
        {
            n = (string.IsNullOrEmpty(d.VolumeLabel) ? "Disque local" : d.VolumeLabel) + " (" + d.Name.TrimEnd('\\') + ")", p = d.Name, d = true, drive = true,
            s = d.TotalSize, free = d.AvailableFreeSpace, m = 0L
        }).ToList();
        return new { type = "fs.list", tab, path = "", items, free = -1L };
    }

    // Faut-il montrer les éléments cachés et système ? Réglage « show.hidden » de l'Explorateur (faux par défaut), le même pour toutes les fenêtres.
    static bool ShowHidden() => Settings.Get("show.hidden") is { } n && n.GetValueKind() == JsonValueKind.True;

    // Lit un dossier et l'envoie par paquets de `chunk` éléments : « fs.list » (le premier, avec le chemin et l'espace libre ; `more` dit s'il en vient d'autres) puis « fs.list.more ».
    // Un très grand dossier s'affiche donc dès le premier paquet. `ct` : la lecture s'arrête dès que la page a demandé un autre dossier pour cet onglet (OperationCanceledException,
    // que l'appelant ignore). Un élément caché est marqué h:true (la page l'estompe).
    public static void ListStream(string path, string tab, CancellationToken ct, Action<object> emit, int chunk = 4000)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) { emit(ListDrives(tab)); return; }
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

    // La lecture d'un coup (essais, commandes synchrones) : un seul message, tous les éléments.
    public static object List(string path, string tab, CancellationToken ct = default)
    {
        object result = null;
        ListStream(path, tab, ct, o => result = o, int.MaxValue);
        return result;
    }

    // Aperçu d'un fichier texte : les 64 premiers Ko, décodés en UTF-16 / UTF-8 (avec ou sans BOM), à défaut en Latin-1. Un octet nul = binaire.
    static object Preview(string path, string tab)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return new { type = "fs.preview", path, kind = "error", error = "Fichier introuvable" };
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[(int)Math.Min(65536, fi.Length)];
            int n = fs.Read(buf, 0, buf.Length);
            bool truncated = fi.Length > n;
            if (n == 0) return new { type = "fs.preview", path, kind = "text", text = "", truncated = false };
            string text;
            if (n >= 2 && buf[0] == 0xFF && buf[1] == 0xFE) text = System.Text.Encoding.Unicode.GetString(buf, 2, n - 2);
            else if (n >= 2 && buf[0] == 0xFE && buf[1] == 0xFF) text = System.Text.Encoding.BigEndianUnicode.GetString(buf, 2, n - 2);
            else
            {
                if (Array.IndexOf(buf, (byte)0, 0, n) >= 0) return new { type = "fs.preview", path, kind = "binary" };
                int start = n >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF ? 3 : 0;
                text = System.Text.Encoding.UTF8.GetString(buf, start, n - start);
                int bad = text.IndexOf('�');
                if (bad >= 0 && bad < text.Length - 3) text = System.Text.Encoding.Latin1.GetString(buf, start, n - start);   // pas de l'UTF-8 : Windows-1252 / Latin-1
            }
            return new { type = "fs.preview", path, kind = "text", text, truncated };
        }
        catch (Exception ex) { return new { type = "fs.preview", path, kind = "error", error = ex.Message }; }
    }

    internal static string UniquePath(string dir, string name)
    {
        string p = Path.Combine(dir, name);
        if (!Path.Exists(p)) return p;
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int i = 2; ; i++) { p = Path.Combine(dir, $"{stem} ({i}){ext}"); if (!Path.Exists(p)) return p; }
    }

    static void SetClipboard(string[] paths, bool cut)
    {
        var sc = new StringCollection(); sc.AddRange(paths);
        var data = new DataObject();
        data.SetFileDropList(sc);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 1)));   // 2 = déplacer, 1 = copier : compatible avec l'Explorateur de Windows
        Clipboard.SetDataObject(data, true);
    }

    // Glisser-déposer : déplace (move) ou copie des éléments vers un dossier. Un dossier ne se place jamais dans lui-même ; déplacer dans le dossier d'origine ne fait rien ; copier dans le dossier
    // d'origine donne « nom (2) ». Un nom déjà pris dans la destination est un CONFLIT : sans consigne (onConflict null), rien n'est fait et les noms sont rendus pour que la page demande ;
    // avec « replace » (remplacer), « keep » (garder les deux : « nom (2) ») ou « skip » (ignorer), l'opération suit. Les conflits ne passent jamais par les boîtes de Windows : elles s'ouvrent
    // sans fenêtre parente, peuvent rester cachées derrière les autres, et bloquent le fil d'interface pendant le glisser-déposer.
    public sealed record TransferResult(string Error, List<string> Conflicts);

    public static TransferResult Transfer(IEnumerable<string> sources, string dir, bool move, string onConflict = null)
    {
        if (string.IsNullOrEmpty(dir) || dir.Length > 520 || !Path.IsPathRooted(dir) || !Directory.Exists(dir)) return new("Dossier de destination introuvable.", null);
        string dirN = dir.TrimEnd('\\');
        var items = new List<(string src, bool isDir, string name, bool sameDir)>();
        foreach (var raw in sources)
        {
            if (string.IsNullOrEmpty(raw) || raw.Length > 520 || !Path.IsPathRooted(raw)) continue;
            string src = raw.TrimEnd('\\'); bool isDir = Directory.Exists(src);
            if (!isDir && !File.Exists(src)) continue;
            string name = Path.GetFileName(src);
            if (name.Length == 0) continue;   // la racine d'un disque ne se déplace pas
            if (isDir && (string.Equals(src, dirN, StringComparison.OrdinalIgnoreCase) || dirN.StartsWith(src + "\\", StringComparison.OrdinalIgnoreCase))) return new("Impossible de placer un dossier dans lui-même.", null);
            bool sameDir = string.Equals(Path.GetDirectoryName(src), dirN, StringComparison.OrdinalIgnoreCase);
            if (move && sameDir) continue;
            items.Add((src, isDir, name, sameDir));
        }
        var conflicts = items.Where(i => !i.sameDir && Path.Exists(Path.Combine(dir, i.name))).Select(i => i.name).ToList();
        if (conflicts.Count > 0 && onConflict == null) return new(null, conflicts);
        foreach (var (src, isDir, name, sameDir) in items)
        {
            string dst = Path.Combine(dir, name); bool exists = !sameDir && Path.Exists(dst), overwrite = false;
            if (sameDir) dst = UniquePath(dir, name);
            else if (exists)
            {
                if (onConflict == "skip") continue;
                if (onConflict == "keep") dst = UniquePath(dir, name); else overwrite = onConflict == "replace";
            }
            if (overwrite)   // sans boîte de dialogue : le choix est déjà fait
            {
                if (move) { if (isDir) FileSystem.MoveDirectory(src, dst, true); else FileSystem.MoveFile(src, dst, true); }
                else { if (isDir) FileSystem.CopyDirectory(src, dst, true); else FileSystem.CopyFile(src, dst, true); }
            }
            else if (move) { if (isDir) FileSystem.MoveDirectory(src, dst, UIOption.AllDialogs); else FileSystem.MoveFile(src, dst, UIOption.AllDialogs); }
            else { if (isDir) FileSystem.CopyDirectory(src, dst, UIOption.AllDialogs); else FileSystem.CopyFile(src, dst, UIOption.AllDialogs); }
        }
        return new(null, null);
    }

    // Tous les éléments sont-ils sur le disque (ou le partage) de la destination ?
    public static bool SameVolume(IEnumerable<string> paths, string dir)
    {
        string root(string p) { try { return Path.GetPathRoot(p) ?? ""; } catch { return ""; } }
        string dr = root(dir); var valid = paths.Where(p => !string.IsNullOrEmpty(p) && Path.IsPathRooted(p)).ToList();
        return dr.Length > 0 && valid.Count > 0 && valid.All(p => string.Equals(root(p), dr, StringComparison.OrdinalIgnoreCase));
    }

    // Le résultat d'un dépôt, à renvoyer à la page : « fs.done » (avec l'erreur éventuelle) ou « fs.conflict » (noms déjà pris ; la page demande, puis renvoie fs.drop avec sa réponse).
    static object Outcome(TransferResult r, string tab, IEnumerable<string> paths, string dir, bool move)
        => r.Conflicts is { Count: > 0 } ? new { type = "fs.conflict", tab, paths = paths.ToArray(), dir, move, names = r.Conflicts } : new { type = "fs.done", tab, error = r.Error };

    // Des fichiers glissés depuis une autre application (l'Explorateur de Windows, le Bureau) ou une autre fenêtre de l'Explorateur : leurs chemins viennent de WebView2 (HostForm.DroppedPaths).
    // Comme dans Windows : Maj déplace, Ctrl copie ; sans touche, on déplace sur le même disque et on copie d'un disque à l'autre.
    public static void DropFiles(Action<object> send, IEnumerable<string> paths, string dir, bool shift, bool ctrl, string tab)
    {
        var list = (paths ?? Array.Empty<string>()).ToList();
        bool move = shift || (!ctrl && SameVolume(list, dir));
        try { send(Outcome(Transfer(list, dir, move), tab, list, dir, move)); }
        catch (OperationCanceledException) { send(new { type = "fs.done", tab }); }
        catch (Exception ex) { Host.Log("explorateur : dépôt de fichiers a échoué : " + ex.Message); send(new { type = "fs.done", tab, error = ex.Message }); }
    }

    static void Paste(string dir)
    {
        var data = Clipboard.GetDataObject();
        if (data == null || !Clipboard.ContainsFileDropList()) return;
        bool cut = false;
        if (data.GetData("Preferred DropEffect") is MemoryStream ms) { var b = new byte[4]; ms.Position = 0; ms.Read(b, 0, 4); cut = (BitConverter.ToInt32(b, 0) & 2) != 0; }
        foreach (string src in Clipboard.GetFileDropList())
        {
            string name = Path.GetFileName(src.TrimEnd('\\'));
            bool isDir = Directory.Exists(src);
            string dst = Path.Combine(dir, name);
            if (string.Equals(Path.GetDirectoryName(src.TrimEnd('\\')), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) dst = UniquePath(dir, name);   // coller dans le dossier d'origine : « nom (2) »
            if (cut)
            {
                if (isDir) FileSystem.MoveDirectory(src, dst, UIOption.AllDialogs); else FileSystem.MoveFile(src, dst, UIOption.AllDialogs);
            }
            else
            {
                if (isDir) FileSystem.CopyDirectory(src, dst, UIOption.AllDialogs); else FileSystem.CopyFile(src, dst, UIOption.AllDialogs);
            }
        }
        if (cut) Clipboard.Clear();
    }

    // owner : fenêtre parente des boîtes de Windows ; send : où renvoyer les réponses (la page, ou l'auto-test).
    public static void Handle(IntPtr owner, Action<object> send, string cmd, JsonElement r)
    {
        string Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string[] Paths() => r.TryGetProperty("paths", out var v) ? v.EnumerateArray().Select(x => x.GetString()).ToArray() : Array.Empty<string>();
        string tab = Str("tab");
        try
        {
            switch (cmd)
            {
                case "fs.places": send(Places()); break;
                case "fs.list": send(List(Str("path"), tab)); break;
                case "fs.open": Process.Start(new ProcessStartInfo(Str("path")) { UseShellExecute = true }); Host.Log("explorateur : ouvert " + Str("path")); break;
                case "fs.copypath":   // « Copier le chemin » : chaque chemin entre guillemets, un par ligne (comme Windows)
                    { var ps = Paths().Where(p => !string.IsNullOrEmpty(p) && p.Length <= 520).Take(5000).ToArray(); if (ps.Length > 0) Clipboard.SetText(string.Join("\r\n", ps.Select(p => "\"" + p + "\""))); break; }
                case "fs.openwith": if (!ShellData.OpenWithRun(Str("exe"), Str("path"))) send(new { type = "fs.done", tab, error = "Impossible d'ouvrir avec ce programme." }); break;
                case "fs.openas": ShellData.OpenAsDialog(Str("path")); break;
                case "fs.terminal": ShellData.OpenTerminal(Str("path")); break;
                case "fs.sendto":
                    {
                        var (sdir, sexe) = ShellData.SendToTarget(Str("item")); var ps = Paths();
                        if (sdir != null) send(Outcome(Transfer(ps, sdir, false), tab, ps, sdir, false));   // une copie, avec la boîte « Éléments existants » si un nom est pris
                        else if (sexe != null) ShellData.RunProgramOn(sexe, ps);
                        else send(new { type = "fs.done", tab, error = "Destination introuvable." });
                        break;
                    }
                case "fs.new":
                    {
                        string np = ShellData.CreateNew(Str("dir"), Str("ext"));
                        send(np != null ? new { type = "fs.done", tab, select = np, error = (string)null } : new { type = "fs.done", tab, select = (string)null, error = "Impossible de créer ce fichier." });
                        break;
                    }
                case "fs.newshortcut":
                    {
                        string sp = ShellData.CreateShortcut(Str("dir"), Str("name"), Str("target"));
                        send(sp != null ? new { type = "fs.done", tab, select = sp, error = (string)null } : new { type = "fs.done", tab, select = (string)null, error = "Impossible de créer le raccourci (cible vide ?)." });
                        break;
                    }
                case "fs.props": SHObjectProperties(owner, 0x2 /* SHOP_FILEPATH */, Str("path"), null); break;
                case "fs.newfolder":
                    {
                        string p = UniquePath(Str("dir"), "Nouveau dossier");
                        Directory.CreateDirectory(p);
                        send(new { type = "fs.done", tab, select = p });
                        break;
                    }
                case "fs.rename":
                    {
                        string src = Str("path"), name = Str("name");
                        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Length == 0) { send(new { type = "fs.done", tab, error = "Nom invalide : les caractères \\ / : * ? \" < > | sont interdits." }); break; }
                        string dst = Path.Combine(Path.GetDirectoryName(src.TrimEnd('\\')), name);
                        if (!string.Equals(src, dst, StringComparison.Ordinal))
                        {
                            if (Directory.Exists(src)) Directory.Move(src, dst); else File.Move(src, dst);
                        }
                        send(new { type = "fs.done", tab, select = dst });
                        break;
                    }
                case "fs.delete":
                    foreach (string p in Paths())
                    {
                        if (Directory.Exists(p)) FileSystem.DeleteDirectory(p, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                        else FileSystem.DeleteFile(p, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    }
                    send(new { type = "fs.done", tab });
                    break;
                case "fs.preview": send(Preview(Str("path"), tab)); break;
                case "fs.copy": SetClipboard(Paths(), r.TryGetProperty("cut", out var c) && c.GetBoolean()); break;
                case "fs.paste": Paste(Str("dir")); send(new { type = "fs.done", tab }); break;
                case "fs.drop":
                    {
                        bool mv = r.TryGetProperty("move", out var mvv) && mvv.ValueKind == JsonValueKind.True;
                        var ps = Paths(); string dir = Str("dir");
                        send(Outcome(Transfer(ps, dir, mv, Str("conflict")), tab, ps, dir, mv));
                        break;
                    }
            }
        }
        catch (OperationCanceledException) { send(new { type = "fs.done", tab }); }
        catch (Exception ex)
        {
            Host.Log($"explorateur : {cmd} a échoué : {ex.Message}");
            send(new { type = "fs.done", tab, error = ex.Message });
        }
    }
}
