using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace Cdx;

// Les aperçus : texte, code, Markdown, images, documents Office (lus en flux, bornés). Sorti de Fs.cs (audit du 3/11/2026, 4.6).
static partial class Fs
{
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
}
