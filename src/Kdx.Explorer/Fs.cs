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

    public static object List(string path, string tab)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return ListDrives(tab);
            var di = new DirectoryInfo(path);
            if (!di.Exists) return new { type = "fs.list", tab, error = "Dossier introuvable : " + path };
            var items = new List<object>();
            foreach (var e in di.EnumerateFileSystemInfos())
            {
                try
                {
                    var a = e.Attributes;
                    if ((a & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    bool d = (a & FileAttributes.Directory) != 0;
                    items.Add(new { n = e.Name, d, s = d ? -1L : ((FileInfo)e).Length, m = new DateTimeOffset(e.LastWriteTime).ToUnixTimeMilliseconds() });
                }
                catch { }
            }
            long free = -1; try { free = new DriveInfo(di.Root.FullName).AvailableFreeSpace; } catch { }
            Host.Log($"explorateur : {di.FullName} ({items.Count} éléments)");
            return new { type = "fs.list", tab, path = di.FullName, items, free };
        }
        catch (UnauthorizedAccessException) { return new { type = "fs.list", tab, error = "Accès refusé : " + path }; }
        catch (Exception ex) { return new { type = "fs.list", tab, error = ex.Message }; }
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

    static string UniquePath(string dir, string name)
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
