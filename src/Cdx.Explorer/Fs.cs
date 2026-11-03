using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace Cdx;

// Le côté « système de fichiers » de l'Explorateur CDX : tout ce que la page demande par des messages « fs.* ».
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
            .Select(d => { var (kind, name) = DriveKind(d); return new { name, path = d.Name, ic = kind == "reseau" ? "serveur" : "disque", kind }; }).ToList();
        return new { type = "places", places, drives, home = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
    }

    // ---------- disques : type, partages d'un serveur, éjection ----------
    // Le type d'un disque (fixe, amovible, reseau, cdrom) et son nom affiché : son étiquette, sinon un nom d'après le type, puis sa lettre.
    public static (string kind, string name) DriveKind(DriveInfo d)
    {
        string k; try { k = d.DriveType switch { DriveType.Removable => "amovible", DriveType.Network => "reseau", DriveType.CDRom => "cdrom", _ => "fixe" }; } catch { k = "fixe"; }
        string label = null; try { label = d.VolumeLabel; } catch { }
        if (string.IsNullOrEmpty(label)) label = k switch { "amovible" => "Disque amovible", "reseau" => "Lecteur réseau", "cdrom" => "Lecteur de DVD", _ => "Disque local" };
        return (k, label + " (" + d.Name.TrimEnd('\\') + ")");
    }

    // « \\serveur » (ou « \\serveur\ ») : la racine d'un serveur du réseau, dont on liste les partages.
    public static bool IsServerRoot(string p) => p != null && System.Text.RegularExpressions.Regex.IsMatch(p, @"^\\\\[^\\?.][^\\]*\\?$");

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] static extern int NetShareEnum(string servername, int level, out IntPtr bufptr, int prefmaxlen, out int entriesread, out int totalentries, ref int resume_handle);
    [DllImport("netapi32.dll")] static extern int NetApiBufferFree(IntPtr buf);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct SHARE_INFO_1 { public string netname; public uint type; public string remark; }

    // Les partages « disque » visibles d'un serveur (les partages cachés en $ et les imprimantes n'y sont pas), comme un dossier.
    public static object ListShares(string server, string tab)
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
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, byte[] inBuf, int inSize, IntPtr outBuf, int outSize, out int ret, IntPtr ov);

    // Éjecte une clé USB ou un DVD (« E:\ ») : le volume est verrouillé (refusé si un programme y a un fichier ouvert), démonté, puis le support libéré. Null si c'est fait, sinon pourquoi pas.
    public static string Eject(string drive)
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

    static object ListDrives(string tab)
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
    public static void ListStream(string path, string tab, CancellationToken ct, Action<object> emit, int chunk = 4000)
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
    static object ListTrash(string tab, CancellationToken ct)
    {
        var items = Recycle.List().Select(e => (object)new { n = Path.GetFileName(e.DataPath), b = Path.GetFileName(e.Original.TrimEnd('\\')), r = Path.GetDirectoryName(e.Original) ?? "", d = e.IsDir, s = e.IsDir ? -1L : e.Size, m = new DateTimeOffset(e.Deleted).ToUnixTimeMilliseconds(), p = e.DataPath, o = e.Original, id = e.Id }).ToList();
        ct.ThrowIfCancellationRequested();
        return new { type = "fs.list", tab, path = Recycle.TrashPath, items, free = -1L, more = false };
    }

    // La lecture d'un coup (essais, commandes synchrones) : un seul message, tous les éléments.
    public static object List(string path, string tab, CancellationToken ct = default)
    {
        object result = null;
        ListStream(path, tab, ct, o => result = o, int.MaxValue);
        return result;
    }

    // ---------- aperçus étendus (lot 8) : Markdown rendu et code coloré (la page s'en charge, d'après kind et lang), documents Office (le texte en est extrait ici) ----------
    static readonly HashSet<string> CodeExt = new(StringComparer.OrdinalIgnoreCase)
    { ".js", ".mjs", ".ts", ".tsx", ".jsx", ".py", ".cs", ".rs", ".ps1", ".psm1", ".json", ".css", ".html", ".htm", ".xml", ".svg", ".c", ".h", ".cpp", ".hpp", ".java", ".kt", ".go", ".sh", ".bat", ".cmd", ".yml", ".yaml", ".toml", ".ini", ".cfg", ".sql", ".iss", ".cmake" };
    public static string KindOf(string ext) => ext is ".md" or ".markdown" ? "md" : CodeExt.Contains(ext) ? "code" : "text";

    // Le texte d'un document Office (docx, xlsx, pptx) : des zip de XML, dont on sort les paragraphes, les cellules de la première feuille, les textes des diapositives.
    // Lu EN FLUX (XmlReader, jamais XDocument.Load d'une partie entière) et arrêté dès 64 Ko de texte, 300 lignes ou 200 diapositives : un document énorme
    // ou piégé ne charge pas des centaines de Mo en mémoire. DTD interdites, aucune résolution externe, nombre de caractères lus borné par partie.
    const long MaxPartChars = 64L << 20;
    static System.Xml.XmlReader Part(System.IO.Compression.ZipArchiveEntry e) => System.Xml.XmlReader.Create(e.Open(), new System.Xml.XmlReaderSettings
    { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxPartChars, CloseInput = true });

    public static object OfficePreview(string path, string ext, CancellationToken ct = default)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            var sb = new System.Text.StringBuilder(); bool truncated = false;
            bool Full() { if (sb.Length >= 65536) truncated = true; return truncated; }
            void Add(string s) { if (!Full()) sb.Append(s); }
            const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main", S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main", A = "http://schemas.openxmlformats.org/drawingml/2006/main";
            if (ext == ".docx")
            {
                var e = zip.GetEntry("word/document.xml"); if (e == null) return new { type = "fs.preview", path, kind = "error", error = "Document Word sans texte lisible" };
                using var r = Part(e);
                for (bool more = r.Read(); more && !Full();)
                {
                    ct.ThrowIfCancellationRequested();
                    bool w = r.NamespaceURI == W, el = r.NodeType == System.Xml.XmlNodeType.Element;
                    if (w && el && r.LocalName == "t") { Add(r.ReadElementContentAsString()); more = !r.EOF; continue; }   // le lecteur est déjà sur le nœud suivant
                    if (w && el && r.LocalName == "tab") Add("\t");
                    else if (w && el && r.LocalName == "br") Add("\n");
                    else if (w && r.NodeType == System.Xml.XmlNodeType.EndElement && r.LocalName == "p") Add("\n");
                    more = r.Read();
                }
            }
            else if (ext == ".xlsx")
            {
                // les chaînes partagées : au plus 50 000 (celles des 300 premières lignes sont en tête dans les classeurs ordinaires)
                var shared = new List<string>();
                var se = zip.GetEntry("xl/sharedStrings.xml");
                if (se != null)
                {
                    using var r = Part(se); var cur = new System.Text.StringBuilder(); bool inSi = false;
                    for (bool more = r.Read(); more && shared.Count < 50000;)
                    {
                        bool s = r.NamespaceURI == S, el = r.NodeType == System.Xml.XmlNodeType.Element;
                        if (s && inSi && el && r.LocalName == "t") { string v = r.ReadElementContentAsString(); if (cur.Length < 4096) cur.Append(v); more = !r.EOF; continue; }
                        if (s && el && r.LocalName == "si") { inSi = true; cur.Clear(); if (r.IsEmptyElement) { shared.Add(""); inSi = false; } }
                        else if (s && r.NodeType == System.Xml.XmlNodeType.EndElement && r.LocalName == "si") { shared.Add(cur.ToString()); inSi = false; }
                        more = r.Read();
                    }
                }
                var sh = zip.GetEntry("xl/worksheets/sheet1.xml"); if (sh == null) return new { type = "fs.preview", path, kind = "error", error = "Classeur sans première feuille lisible" };
                using (var r = Part(sh))
                {
                    int rows = 0; var cells = new List<string>(); string cellType = null; string val = null;
                    for (bool more = r.Read(); more && !Full();)
                    {
                        ct.ThrowIfCancellationRequested();
                        bool s = r.NamespaceURI == S, el = r.NodeType == System.Xml.XmlNodeType.Element, end = r.NodeType == System.Xml.XmlNodeType.EndElement;
                        if (s && el && (r.LocalName == "v" || r.LocalName == "t")) { val += r.ReadElementContentAsString(); more = !r.EOF; continue; }
                        if (s && el && r.LocalName == "row") { if (++rows > 300) { truncated = true; break; } cells.Clear(); if (r.IsEmptyElement) Add("\n"); }
                        else if (s && el && r.LocalName == "c") { cellType = r.GetAttribute("t"); val = ""; if (r.IsEmptyElement) cells.Add(""); }
                        else if (s && end && r.LocalName == "c") cells.Add(cellType == "s" && int.TryParse(val, out int k) && k >= 0 && k < shared.Count ? shared[k] : val ?? "");
                        else if (s && end && r.LocalName == "row") { Add(string.Join("\t", cells)); Add("\n"); }
                        more = r.Read();
                    }
                }
            }
            else
            {
                var slides = zip.Entries.Where(e => System.Text.RegularExpressions.Regex.IsMatch(e.FullName, @"^ppt/slides/slide\d+\.xml$")).OrderBy(e => int.Parse(System.Text.RegularExpressions.Regex.Match(e.FullName, @"\d+").Value)).ToList();
                if (slides.Count == 0) return new { type = "fs.preview", path, kind = "error", error = "Présentation sans diapositive lisible" };
                int n = 0;
                foreach (var e in slides)
                {
                    if (Full()) break;
                    if (++n > 200) { truncated = true; break; }
                    Add($"— Diapositive {n} —\n");
                    using var r = Part(e); var para = new System.Text.StringBuilder();
                    for (bool more = r.Read(); more && !Full();)
                    {
                        ct.ThrowIfCancellationRequested();
                        bool a = r.NamespaceURI == A;
                        if (a && r.NodeType == System.Xml.XmlNodeType.Element && r.LocalName == "t") { para.Append(r.ReadElementContentAsString()); more = !r.EOF; continue; }
                        if (a && r.NodeType == System.Xml.XmlNodeType.EndElement && r.LocalName == "p") { if (para.Length > 0) { Add(para.ToString()); Add("\n"); } para.Clear(); }
                        more = r.Read();
                    }
                    Add("\n");
                }
            }
            return new { type = "fs.preview", path, kind = "office", lang = ext.TrimStart('.'), text = sb.ToString(), truncated };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new { type = "fs.preview", path, kind = "error", error = "Document illisible : " + ex.Message }; }
    }
    // Aperçu d'un fichier texte : les 64 premiers Ko, décodés en UTF-16 / UTF-8 (avec ou sans BOM), à défaut en Latin-1. Un octet nul = binaire.
    static object Preview(string path, string tab, CancellationToken ct = default)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return new { type = "fs.preview", path, kind = "error", error = "Fichier introuvable" };
            string ext = fi.Extension.ToLowerInvariant();
            if (ext is ".docx" or ".xlsx" or ".pptx") return OfficePreview(path, ext, ct);
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
            return new { type = "fs.preview", path, kind = KindOf(ext), lang = ext.TrimStart('.'), text, truncated };
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
    public sealed record TransferResult(string Error, List<string> Conflicts, List<(string src, string dst)> Done = null);

    // Ce qu'un transfert va faire, une fois les conflits réglés : chaque élément avec sa destination. `Error` : refusé d'avance ; `Conflicts` : des noms pris, il faut une consigne.
    sealed record TransferPlan(List<PlanItem> Items, bool Move, string Dir, string Error, List<string> Conflicts);

    static TransferPlan Plan(IEnumerable<string> sources, string dir, bool move, string onConflict)
    {
        if (string.IsNullOrEmpty(dir) || dir.Length > 520 || !Path.IsPathRooted(dir) || !Directory.Exists(dir)) return new(null, move, dir, "Dossier de destination introuvable.", null);
        string dirN = dir.TrimEnd('\\');
        var items = new List<(string src, bool isDir, string name, bool sameDir)>();
        foreach (var raw in sources)
        {
            if (string.IsNullOrEmpty(raw) || raw.Length > 520 || !Path.IsPathRooted(raw)) continue;
            string src = raw.TrimEnd('\\'); bool isDir = Directory.Exists(src);
            if (!isDir && !File.Exists(src)) continue;
            string name = Path.GetFileName(src);
            if (name.Length == 0) continue;   // la racine d'un disque ne se déplace pas
            if (isDir && (string.Equals(src, dirN, StringComparison.OrdinalIgnoreCase) || dirN.StartsWith(src + "\\", StringComparison.OrdinalIgnoreCase))) return new(null, move, dir, "Impossible de placer un dossier dans lui-même.", null);
            bool sameDir = string.Equals(Path.GetDirectoryName(src), dirN, StringComparison.OrdinalIgnoreCase);
            if (move && sameDir) continue;
            items.Add((src, isDir, name, sameDir));
        }
        var conflicts = items.Where(i => !i.sameDir && Path.Exists(Path.Combine(dir, i.name))).Select(i => i.name).ToList();
        if (conflicts.Count > 0 && onConflict == null) return new(null, move, dir, null, conflicts);
        var plan = new List<PlanItem>();
        foreach (var (src, isDir, name, sameDir) in items)
        {
            string dst = Path.Combine(dir, name); bool exists = !sameDir && Path.Exists(dst), overwrite = false;
            if (sameDir) dst = UniquePath(dir, name);
            else if (exists)
            {
                if (onConflict == "skip") continue;
                if (onConflict == "keep") dst = UniquePath(dir, name); else overwrite = onConflict == "replace";
            }
            plan.Add(new PlanItem(src, isDir, dst, overwrite));
        }
        return new(plan, move, dir, null, null);
    }

    static TransferResult ResultOf(CopyOutcome o)
    {
        string err = o.Errors.Count == 0 ? null : o.Errors[0] + (o.Errors.Count > 1 ? " (et " + (o.Errors.Count - 1) + " autre" + (o.Errors.Count > 2 ? "s" : "") + ")" : "");
        return new(err, null, o.Done);
    }

    public static TransferResult Transfer(IEnumerable<string> sources, string dir, bool move, string onConflict = null)
    {
        var p = Plan(sources, dir, move, onConflict);
        if (p.Error != null || p.Conflicts != null) return new(p.Error, p.Conflicts);
        return ResultOf(Copier.Run(p.Items, p.Move, null));
    }

    // La file de tâches de fond (posée par le programme : sans elle, par exemple dans l'auto-test, les transferts se font sur place) et la question « cette fenêtre existe-t-elle encore ? ».
    internal static JobQueue Queue;
    internal static Func<IntPtr, bool> WindowAlive = _ => true;

    // Le « 3 éléments » ou le nom d'un seul, pour intituler un transfert.
    static string TitleOf(List<PlanItem> items) => items.Count == 1 ? Path.GetFileName(items[0].Src) : items.Count + " éléments";
    static string FolderLabel(string p) { string n = Path.GetFileName(p.TrimEnd('\\')); return string.IsNullOrEmpty(n) ? p : n; }

    // Le transfert d'une fenêtre : conflits d'abord (la page demande), puis, si la file existe, en tâche de fond (le résultat revient par `send` quand c'est fini) ; sinon sur place.
    // `afterOk` : à faire une fois le transfert réussi (vider le presse-papiers après un « couper »).
    static void RunTransfer(IntPtr owner, Action<object> send, IEnumerable<string> sources, string dir, bool move, string onConflict, string tab, Action afterOk = null)
    {
        var list = sources.ToList(); var p = Plan(list, dir, move, onConflict);
        if (p.Error != null || p.Conflicts != null) { send(Outcome(new(p.Error, p.Conflicts), tab, list, dir, move)); return; }
        if (p.Items.Count == 0) { send(new { type = "fs.done", tab }); return; }
        if (Queue == null)
        {
            var res = ResultOf(Copier.Run(p.Items, move, null)); RecordTransfer(owner, send, res, move); if (res.Error == null) afterOk?.Invoke(); send(Outcome(res, tab, list, dir, move));
            return;
        }
        var ctx = SynchronizationContext.Current;
        void Finish(Job j)   // hors du fil d'interface : le retour à la page s'y fait par le contexte de synchronisation
        {
            void Back()
            {
                if (!WindowAlive(owner)) return;
                if (j.Tag is TransferResult r) RecordTransfer(owner, send, r, move);
                if (j.State == JobState.Done) afterOk?.Invoke();
                send(new { type = "fs.done", tab, error = j.State == JobState.Failed ? j.Error : null });
            }
            if (ctx != null) ctx.Post(_ => Back(), null); else Back();
        }
        Job Start(TransferPlan q, string from)
        {
            string parent = Path.GetDirectoryName(q.Items[0].Src);
            return Queue.Enqueue(move ? "move" : "copy", TitleOf(q.Items), job =>
            {
                var o = Copier.Run(q.Items, move, job);
                var r = ResultOf(o); job.Tag = r; job.Failed.AddRange(o.Failed);
                if (o.Canceled) throw new OperationCanceledException();
                if (r.Error != null) throw new IOException(r.Error);
            }, Finish, j =>
            {
                j.Items = q.Items.Count; j.To = FolderLabel(dir); j.Path = q.Items[0].Dst;
                j.From = from ?? (q.Items.All(i => Path.GetDirectoryName(i.Src) == parent) ? FolderLabel(parent) : "plusieurs dossiers");
                // « Réessayer » : seulement ce qui a échoué, en remplaçant ce qu'une copie interrompue aurait laissé à moitié
                j.Retry = () =>
                {
                    var again = j.Failed.Distinct().ToList(); if (again.Count == 0) return null;
                    var q2 = Plan(again, dir, move, "replace"); if (q2.Error != null || q2.Items.Count == 0) return null;
                    return Start(q2, j.From);
                };
            });
        }
        Start(p, null);
    }

    // Inscrit dans la pile « Annuler » de la fenêtre ce qu'un transfert a fait (déplacement ou copie), et dit à la page ce qu'on peut annuler.
    static void RecordTransfer(IntPtr owner, Action<object> send, TransferResult r, bool move)
    {
        if (r?.Done is not { Count: > 0 } d) return;
        Undo.For(owner).Push(move ? Undo.Moved(d.Count == 1 ? "Déplacer" : "Déplacer " + d.Count + " éléments", d) : Undo.Created(d.Count == 1 ? "Copier" : "Copier " + d.Count + " éléments", d.Select(x => x.dst)));
        send(Undo.State(owner));
    }

    // Le contenu du presse-papiers de Windows : les fichiers copiés ou coupés, et si c'est un « couper ».
    static (List<string> files, bool cut) ClipboardFiles()
    {
        var data = Clipboard.GetDataObject();
        if (data == null || !Clipboard.ContainsFileDropList()) return (new(), false);
        bool cut = false;
        if (data.GetData("Preferred DropEffect") is MemoryStream ms) { var bb = new byte[4]; ms.Position = 0; ms.Read(bb, 0, 4); cut = (BitConverter.ToInt32(bb, 0) & 2) != 0; }
        return (Clipboard.GetFileDropList().Cast<string>().ToList(), cut);
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
    public static void DropFiles(IntPtr owner, Action<object> send, IEnumerable<string> paths, string dir, bool shift, bool ctrl, string tab)
    {
        var list = (paths ?? Array.Empty<string>()).ToList();
        bool move = shift || (!ctrl && SameVolume(list, dir));
        try { RunTransfer(owner, send, list, dir, move, null, tab); }
        catch (OperationCanceledException) { send(new { type = "fs.done", tab }); }
        catch (Exception ex) { Host.Log("explorateur : dépôt de fichiers a échoué : " + ex.Message); send(new { type = "fs.done", tab, error = ex.Message }); }
    }

    // owner : fenêtre parente des boîtes de Windows ; send : où renvoyer les réponses (la page, ou l'auto-test).
    // Un travail long (effacer, vider la corbeille) fait sur un fil à part ; la réponse revient sur le fil de l'interface, si la fenêtre existe encore.
    static void OffUi(IntPtr owner, Action<object> send, Func<string> work, Func<string, object> reply)
    {
        var ctx = SynchronizationContext.Current;
        if (ctx == null) { string e; try { e = work(); } catch (Exception ex) { e = ex.Message; } send(reply(e)); return; }
        Task.Run(() => { string e; try { e = work(); } catch (Exception ex) { e = ex.Message; } ctx.Post(_ => { if (WindowAlive == null || WindowAlive(owner)) send(reply(e)); }, null); });
    }

    static readonly Dictionary<(IntPtr, string), CancellationTokenSource> previews = new();   // l'aperçu en cours de chaque onglet
    public static void Handle(IntPtr owner, Action<object> send, string cmd, JsonElement r)
    {
        string Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string[] Paths() => r.TryGetProperty("paths", out var v) ? v.EnumerateArray().Select(x => x.GetString()).ToArray() : Array.Empty<string>();
        string[] Ids() => r.TryGetProperty("ids", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToArray() : Array.Empty<string>();
        string tab = Str("tab");
        try
        {
            switch (cmd)
            {
                case "fs.places": send(Places()); break;
                case "fs.eject": { string err = Eject(Str("drive")); send(new { type = "fs.done", tab, error = err, ejected = err == null ? Str("drive") : null }); if (err == null) send(Places()); break; }
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
                        if (sdir != null) RunTransfer(owner, send, ps, sdir, false, null, tab);   // une copie ; si un nom est pris, la page demande
                        else if (sexe != null) ShellData.RunProgramOn(sexe, ps);
                        else send(new { type = "fs.done", tab, error = "Destination introuvable." });
                        break;
                    }
                case "fs.new":
                    {
                        string np = ShellData.CreateNew(Str("dir"), Str("ext"));
                        if (np != null) { Undo.For(owner).Push(Undo.Created("Nouveau fichier", new[] { np })); send(Undo.State(owner)); }
                        send(np != null ? new { type = "fs.done", tab, select = np, error = (string)null } : new { type = "fs.done", tab, select = (string)null, error = "Impossible de créer ce fichier." });
                        break;
                    }
                case "fs.newshortcut":
                    {
                        string sp = ShellData.CreateShortcut(Str("dir"), Str("name"), Str("target"));
                        if (sp != null) { Undo.For(owner).Push(Undo.Created("Nouveau raccourci", new[] { sp })); send(Undo.State(owner)); }
                        send(sp != null ? new { type = "fs.done", tab, select = sp, error = (string)null } : new { type = "fs.done", tab, select = (string)null, error = "Impossible de créer le raccourci (cible vide ?)." });
                        break;
                    }
                case "fs.props": SHObjectProperties(owner, 0x2 /* SHOP_FILEPATH */, Str("path"), null); break;
                case "fs.newfolder":
                    {
                        string p = UniquePath(Str("dir"), "Nouveau dossier");
                        Directory.CreateDirectory(p);
                        Undo.For(owner).Push(Undo.Created("Nouveau dossier", new[] { p })); send(Undo.State(owner));
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
                            Undo.For(owner).Push(Undo.Moved("Renommer", new List<(string, string)> { (src, dst) })); send(Undo.State(owner));
                        }
                        send(new { type = "fs.done", tab, select = dst });
                        break;
                    }
                case "fs.delete":   // vers la corbeille : on garde l'entrée de chacun pour pouvoir le restaurer (Annuler)
                    {
                        // Hors du fil de l'interface : Windows parcourt tout l'arbre avant de le déplacer (un dossier de développement de 1 Go = des dizaines de secondes) ;
                        // sur le fil de l'interface, la fenêtre se figeait et la suppression semblait ne jamais se faire.
                        var ps = Paths().Where(p => !string.IsNullOrEmpty(p) && Path.IsPathRooted(p) && Path.GetFileName(p.TrimEnd('\\')).Length > 0 && Path.Exists(p)).ToArray();
                        if (ps.Length == 0) { send(new { type = "fs.done", tab }); break; }
                        // Un disque sans corbeille (clé USB, partage, subst) : rien n'est supprimé avant que la page ait fait confirmer la suppression définitive.
                        var perm = ps.Where(p => !Recycle.HasBin(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        bool permOk = r.TryGetProperty("permOk", out var po) && po.ValueKind == JsonValueKind.True;
                        if (perm.Count > 0 && !permOk) { send(new { type = "fs.confirmperm", tab, paths = ps, perm = perm.ToArray() }); break; }
                        var gone = new List<BinEntry>(); int noBin = 0, kept = 0; string firstErr = null;
                        void Finish(string error)
                        {
                            if (gone.Count > 0) { Undo.For(owner).Push(Undo.Deleted(gone.Count == 1 ? "Supprimer" : "Supprimer " + gone.Count + " éléments", gone)); send(Undo.State(owner)); }
                            string msg = error ?? firstErr;
                            if (msg == null && kept > 0) msg = kept + " élément" + (kept > 1 ? "s n'ont" : " n'a") + " pas été supprimé" + (kept > 1 ? "s" : "") + " : la corbeille ne pouvait pas " + (kept > 1 ? "les" : "le") + " recevoir.";
                            if (msg == null && noBin > 0) msg = noBin + " élément" + (noBin > 1 ? "s ont" : " a") + " été supprimé" + (noBin > 1 ? "s" : "") + " définitivement (pas de corbeille sur ce disque).";
                            send(new { type = "fs.done", tab, error = msg });
                        }
                        void Recycle1(string p)
                        {
                            if (perm.Contains(p)) { string e1 = Recycle.DeletePermanently(new[] { p }); lock (gone) { if (e1 == null) noBin++; else firstErr ??= e1; } return; }
                            try { var e = Recycle.DeleteToBin(p, owner); lock (gone) { if (e != null) gone.Add(e); else noBin++; } }
                            catch (OperationCanceledException) { lock (gone) kept++; }   // Windows a prévenu (trop gros pour la corbeille) et l'utilisateur a refusé
                        }
                        if (Queue == null) { string err = null; foreach (var p in ps) try { Recycle1(p); } catch (Exception ex) { err ??= ex.Message; } Finish(err); break; }
                        var ctx = SynchronizationContext.Current; string first = ps[0], from = Path.GetDirectoryName(first.TrimEnd('\\'));
                        Queue.Enqueue("delete", (ps.Length == 1 ? Path.GetFileName(first.TrimEnd('\\')) : ps.Length + " éléments") + " → corbeille", job =>
                        {
                            job.Report(0, ps.Length);
                            for (int i = 0; i < ps.Length; i++)
                            {
                                job.WaitIfPaused();   // entre deux éléments : Windows ne sait pas interrompre une mise à la corbeille commencée
                                Recycle1(ps[i]);
                                job.Report(i + 1, ps.Length, Path.GetFileName(ps[i].TrimEnd('\\')));
                            }
                        }, j =>
                        {
                            void Back() { if (WindowAlive(owner)) Finish(j.State == JobState.Failed ? j.Error : null); }
                            if (ctx != null) ctx.Post(_ => Back(), null); else Back();
                        }, j => { j.Items = ps.Length; j.From = FolderLabel(from ?? first); j.To = "Corbeille"; j.Path = from; });
                        break;
                    }
                case "fs.deleteperm":   // Maj+Suppr, après confirmation dans la page : pas de corbeille, pas d'annulation
                    {
                        var ps = Paths();
                        if (Queue == null) { send(new { type = "fs.done", tab, error = Recycle.DeletePermanently(ps) }); break; }
                        var ctx = SynchronizationContext.Current; string first = ps.FirstOrDefault(x => !string.IsNullOrEmpty(x));
                        if (first == null) { send(new { type = "fs.done", tab }); break; }
                        Queue.Enqueue("delete", ps.Length == 1 ? Path.GetFileName(first.TrimEnd('\\')) : ps.Length + " éléments", job =>
                        {
                            string e = Copier.DeleteMany(ps, job);
                            if (e != null) throw new IOException(e);
                        }, j =>
                        {
                            void Back() { if (WindowAlive(owner)) send(new { type = "fs.done", tab, error = j.State == JobState.Failed ? j.Error : null }); }
                            if (ctx != null) ctx.Post(_ => Back(), null); else Back();
                        }, j => { j.Items = ps.Length; j.From = FolderLabel(Path.GetDirectoryName(first.TrimEnd('\\')) ?? first); j.Path = Path.GetDirectoryName(first.TrimEnd('\\')); });
                        break;
                    }
                case "fs.restore":   // depuis la corbeille : chaque élément retrouve sa place (jamais d'écrasement)
                    {
                        var back = new List<string>(); var errs = new List<string>();
                        foreach (var id in Ids()) { var (err, dest) = Recycle.Restore(id); if (err == null) back.Add(dest); else errs.Add(err); }
                        if (back.Count > 0) { Undo.For(owner).Push(Undo.Created(back.Count == 1 ? "Restaurer" : "Restaurer " + back.Count + " éléments", back)); send(Undo.State(owner)); }
                        send(new { type = "fs.done", tab, error = errs.Count == 0 ? null : errs[0] + (errs.Count > 1 ? " (et " + (errs.Count - 1) + " autre" + (errs.Count > 2 ? "s" : "") + ")" : "") });
                        break;
                    }
                case "fs.purge":   // effacer pour de bon des éléments de la corbeille (après confirmation) ; hors du fil de l'interface, comme la mise à la corbeille
                    {
                        var ids = Ids().Where(Recycle.IsEntryId).ToArray();
                        OffUi(owner, send, () => Recycle.Purge(ids), err => new { type = "fs.done", tab, error = err });
                        break;
                    }
                case "fs.emptybin": OffUi(owner, send, () => Recycle.EmptyAll() ? null : "La corbeille n'a pas pu être vidée.", err => new { type = "fs.done", tab, error = err }); break;
                case "fs.undo":
                case "fs.redo":
                    {
                        var log = Undo.For(owner); var (label, uerr) = cmd == "fs.undo" ? log.DoUndo() : log.DoRedo();
                        send(new { type = "fs.undone", redo = cmd == "fs.redo", label, error = uerr });
                        send(Undo.State(owner)); send(new { type = "fs.done", tab });
                        break;
                    }
                case "fs.preview":   // hors du fil de l'interface (un document lourd figeait toutes les fenêtres), annulé par l'aperçu suivant du même onglet
                    {
                        string pp = Str("path"); var key = (owner, tab ?? ""); var cts = new CancellationTokenSource();
                        lock (previews) { if (previews.TryGetValue(key, out var old)) old.Cancel(); previews[key] = cts; }
                        var ctx = SynchronizationContext.Current;
                        _ = Task.Run(() =>
                        {
                            object res; try { res = Preview(pp, tab, cts.Token); } catch (OperationCanceledException) { return; } catch (Exception ex) { res = new { type = "fs.preview", path = pp, kind = "error", error = ex.Message }; }
                            lock (previews) { if (previews.TryGetValue(key, out var cur) && cur == cts) previews.Remove(key); }
                            if (cts.IsCancellationRequested) return;
                            void Back() { if (WindowAlive(owner)) send(res); }
                            if (ctx != null) ctx.Post(_ => Back(), null); else Back();
                        });
                        break;
                    }
                case "fs.copy": SetClipboard(Paths(), r.TryGetProperty("cut", out var c) && c.GetBoolean()); break;
                case "fs.paste":
                    {
                        var (files, cut) = ClipboardFiles(); string pdir = Str("dir");
                        if (files.Count == 0) { send(new { type = "fs.done", tab }); break; }
                        RunTransfer(owner, send, files, pdir, cut, null, tab, cut ? () => Clipboard.Clear() : null);
                        break;
                    }
                case "fs.drop":
                    {
                        bool mv = r.TryGetProperty("move", out var mvv) && mvv.ValueKind == JsonValueKind.True;
                        var ps = Paths(); string dir = Str("dir");
                        RunTransfer(owner, send, ps, dir, mv, Str("conflict"), tab);
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
