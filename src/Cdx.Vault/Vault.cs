using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Cdx.Coffre;

// Une erreur à dire telle quelle à celui qui a demandé (l'application, ou Claude) : le message est en français et ne contient aucun chemin du disque.
public sealed class VaultException : Exception
{
    public string Code { get; }
    public VaultException(string code, string message) : base(message) { Code = code; }
}

public sealed class NoteInfo
{
    public string Path, Title;
    public DateTime Modified; public long Size;
    public Dictionary<string, object> Props = new(StringComparer.OrdinalIgnoreCase);   // en-tête : texte, ou List<string>
    public List<string> Tags = new();
    public List<LinkRef> Links = new();
    public List<TaskItem> Tasks = new();
    public List<Heading> Headings = new();
    public string Folder { get { int i = Path.LastIndexOf('/'); return i < 0 ? "" : Path[..i]; } }
    public string Prop(string key) => Props.TryGetValue(key, out var v) ? (v is List<string> l ? string.Join(", ", l) : v as string) : null;
}

public sealed class SearchHit { public string Path, Title, Snippet; public int Score, Line; public List<string> Tags; }
public sealed class Backlink { public string Path, Title, Context; public int Line; }
public sealed class ProjectInfo { public string Path, Name, Status, Repo, Priority, Summary; public int OpenTasks, DoneTasks; public DateTime Modified; public string NextDue; }

// Le coffre : un dossier de fichiers .md. Tout chemin est relatif au coffre, avec des « / ». Rien n'écrit hors du coffre, jamais dans un dossier caché (.obsidian, .git…),
// jamais autre chose qu'un .md ; rien ne se supprime : « retirer » déplace la note dans .trash/ (récupérable).
public sealed class Vault
{
    public const int MaxNoteBytes = 2_000_000;
    public string Root { get; }
    sealed class Entry { public NoteInfo Info; public string Text; public long Ticks, Size; }
    readonly Dictionary<string, Entry> notes = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, List<string>> byName = new(StringComparer.OrdinalIgnoreCase);

    public Vault(string root)
    {
        Root = Path.GetFullPath(root).TrimEnd('\\', '/');
        if (!Directory.Exists(Root)) throw new VaultException("introuvable", "Le dossier du coffre n'existe pas.");
        Refresh();
    }

    // ---------- chemins ----------
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    // Le chemin d'une note, normalisé (« / », « .md » ajouté) ; refuse tout ce qui sortirait du coffre ou toucherait aux dossiers cachés.
    public static string NormalizePath(string rel, bool addExt = true)
    {
        if (string.IsNullOrWhiteSpace(rel)) throw new VaultException("chemin", "Le chemin de la note est vide.");
        rel = rel.Replace('\\', '/').Trim();
        if (rel.StartsWith("//")) throw new VaultException("chemin", "Un chemin réseau (« \\\\ ») n'est pas un chemin du coffre.");
        rel = rel.TrimStart('/');
        if (rel.Length > 240) throw new VaultException("chemin", "Le chemin est trop long (240 caractères au plus).");
        if (rel.Contains(':')) throw new VaultException("chemin", "Le chemin doit être relatif au coffre (pas de lecteur, pas de réseau).");
        var segs = rel.Split('/');
        foreach (var s in segs)
        {
            if (s.Length == 0) throw new VaultException("chemin", "Le chemin contient un nom vide (« // »).");
            if (s == "." || s == "..") throw new VaultException("chemin", "« . » et « .. » ne sont pas permis dans un chemin.");
            if (s.StartsWith(".")) throw new VaultException("chemin", "Les dossiers et fichiers cachés (commençant par un point) ne sont pas accessibles.");
            if (s.Length > 120 || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || s.EndsWith(" ")) throw new VaultException("chemin", $"Nom invalide : « {s} ».");
            string stem = s.Split('.')[0]; if (Reserved.Contains(stem)) throw new VaultException("chemin", $"Nom réservé par Windows : « {s} ».");
        }
        if (addExt && !rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) rel += ".md";
        if (rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && rel.Length <= 3) throw new VaultException("chemin", "La note n'a pas de nom.");
        return rel;
    }

    string Full(string rel)
    {
        string full = Path.GetFullPath(Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new VaultException("chemin", "Ce chemin sort du coffre.");
        // un lien symbolique ou une jonction dans le chemin pourrait mener hors du coffre
        string cur = Root;
        foreach (var seg in rel.Split('/'))
        {
            cur = Path.Combine(cur, seg);
            if ((File.Exists(cur) || Directory.Exists(cur)) && (File.GetAttributes(cur) & FileAttributes.ReparsePoint) != 0) throw new VaultException("chemin", "Les liens symboliques ne sont pas suivis.");
        }
        return full;
    }

    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    // ---------- index ----------
    static bool Hidden(string rel) => rel.Split('/').Any(s => s.StartsWith("."));

    // Relit le dossier : seules les notes modifiées sont réanalysées. Rend vrai si quelque chose a changé.
    public bool Refresh()
    {
        bool changed = false; var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(Root, "*.md", SearchOption.AllDirectories))
        {
            string rel = f[(Root.Length + 1)..].Replace('\\', '/');
            if (Hidden(rel)) continue;
            FileInfo fi; try { fi = new FileInfo(f); if ((fi.Attributes & FileAttributes.ReparsePoint) != 0 || fi.Length > MaxNoteBytes) continue; } catch { continue; }
            seen.Add(rel);
            if (notes.TryGetValue(rel, out var e) && e.Ticks == fi.LastWriteTimeUtc.Ticks && e.Size == fi.Length) continue;
            string text; try { text = ReadRetry(f); } catch { continue; }   // toujours verrouillé : relu au prochain passage
            notes[rel] = new Entry { Info = Parse(rel, text, fi), Text = text, Ticks = fi.LastWriteTimeUtc.Ticks, Size = fi.Length };
            changed = true;
        }
        foreach (var gone in notes.Keys.Where(k => !seen.Contains(k)).ToList()) { notes.Remove(gone); changed = true; }
        if (changed) ResolveAll();
        return changed;
    }

    static NoteInfo Parse(string rel, string text, FileInfo fi)
    {
        var n = new NoteInfo { Path = rel, Title = Path.GetFileNameWithoutExtension(rel), Modified = fi.LastWriteTime, Size = fi.Length };
        int body = MarkdownParse.Frontmatter(text, n.Props);
        string masked = MarkdownParse.Mask(text, body);
        n.Links = MarkdownParse.Links(text, masked);
        n.Tasks = MarkdownParse.Tasks(rel, masked, text);
        n.Headings = MarkdownParse.Headings(masked);
        var tags = new List<string>();
        if (n.Props.TryGetValue("tags", out var tv)) tags.AddRange(tv is List<string> l ? l : (IEnumerable<string>)((string)tv).Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries));
        if (n.Props.TryGetValue("tag", out var t1) && t1 is string s1) tags.AddRange(s1.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries));
        tags.AddRange(MarkdownParse.Tags(masked));
        n.Tags = tags.Select(t => t.TrimStart('#')).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return n;
    }

    void ResolveAll()
    {
        byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (var p in notes.Keys.OrderBy(k => k.Length).ThenBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileNameWithoutExtension(p);
            if (!byName.TryGetValue(name, out var l)) byName[name] = l = new List<string>();
            l.Add(p);
        }
        foreach (var e in notes.Values) foreach (var lk in e.Info.Links) lk.Resolved = Resolve(lk.Target);
    }

    // Comme Obsidian : « Note » désigne la note de ce nom (la moins profondément rangée s'il y en a plusieurs) ; « Dossier/Note » vise un chemin.
    public string Resolve(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        string t = target.Replace('\\', '/').Trim(); if (t.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) t = t[..^3];
        if (t.Contains('/'))
        {
            t = t.TrimStart('/');
            return notes.Keys.Where(k => { string kk = k[..^3]; return kk.Equals(t, StringComparison.OrdinalIgnoreCase) || kk.EndsWith("/" + t, StringComparison.OrdinalIgnoreCase); }).OrderBy(k => k.Length).FirstOrDefault();
        }
        return byName.TryGetValue(t, out var l) ? l[0] : null;
    }

    public IReadOnlyList<NoteInfo> Notes => notes.Values.Select(e => e.Info).OrderBy(n => n.Path, StringComparer.OrdinalIgnoreCase).ToList();
    public NoteInfo Get(string path) { try { return notes.TryGetValue(NormalizePath(path), out var e) ? e.Info : null; } catch (VaultException) { return null; } }
    public int Count => notes.Count;

    // Les dossiers du coffre (y compris vides), sans les cachés.
    public IReadOnlyList<string> Folders()
    {
        var l = new List<string>();
        foreach (var d in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories))
        {
            string rel = d[(Root.Length + 1)..].Replace('\\', '/');
            if (!Hidden(rel) && (File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0) l.Add(rel);
        }
        l.Sort(StringComparer.OrdinalIgnoreCase); return l;
    }

    // ---------- images ----------
    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp" };
    public const long MaxImageBytes = 25_000_000;
    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path ?? "").ToLowerInvariant());

    // Les images du coffre (chemins relatifs), pour [[image.png]] et ![](image.png). Jamais dans un dossier caché.
    public List<string> Attachments(int max = 20000)
    {
        var l = new List<string>();
        foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            if (!IsImage(f)) continue;
            string rel = f[(Root.Length + 1)..].Replace('\\', '/');
            if (Hidden(rel)) continue;
            l.Add(rel); if (l.Count >= max) break;
        }
        l.Sort(StringComparer.OrdinalIgnoreCase); return l;
    }

    // Le fichier d'une image du coffre, ou null : chemin sûr, extension d'image, taille raisonnable. C'est la seule porte par laquelle l'application sert un fichier autre qu'une note.
    public string ImagePath(string rel)
    {
        try
        {
            if (!IsImage(rel)) return null;
            string n = NormalizePath(rel, addExt: false); string full = Full(n);
            return File.Exists(full) && new FileInfo(full).Length <= MaxImageBytes ? full : null;
        }
        catch (VaultException) { return null; }
    }

    // ---------- lecture ----------
    public string Read(string path, out string hash)
    {
        string rel = NormalizePath(path);
        string full = Full(rel);
        if (!File.Exists(full)) throw new VaultException("introuvable", $"La note « {rel} » n'existe pas.");
        if (new FileInfo(full).Length > MaxNoteBytes) throw new VaultException("trop-grande", "La note dépasse 2 Mo.");
        string text = ReadRetry(full); hash = Hash(text); return text;
    }

    // Un fichier qu'un autre programme est en train d'écrire (l'application, un éditeur, un autre processus) est parfois verrouillé une fraction de seconde : on réessaie quelques fois.
    static string ReadRetry(string full)
    {
        for (int i = 0; ; i++)
        {
            try { return File.ReadAllText(full, Encoding.UTF8); }
            catch (IOException) when (i < 6) { Thread.Sleep(40); }
            catch (IOException) { throw new VaultException("occupé", "La note est utilisée par un autre programme : réessayer dans un instant."); }
        }
    }

    // ---------- écriture ----------
    static void WriteAtomic(string full, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(full));
        string tmp = full + ".cdx-tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, full, true);
    }

    static void CheckSize(string text) { if (Encoding.UTF8.GetByteCount(text) > MaxNoteBytes) throw new VaultException("trop-grande", "La note dépasserait 2 Mo."); }

    // Crée une note ; refuse si elle existe (jamais d'écrasement par mégarde).
    public NoteInfo Create(string path, string text)
    {
        string rel = NormalizePath(path); CheckSize(text ?? "");
        string full = Full(rel);
        if (File.Exists(full)) throw new VaultException("existe", $"La note « {rel} » existe déjà : la modifier avec son empreinte, ou choisir un autre nom.");
        WriteAtomic(full, text ?? ""); Refresh(); return Get(rel);
    }

    // Remplace tout le contenu d'une note qui existe. L'empreinte lue avec Read est exigée : si la note a changé depuis (modifiée dans l'application, par exemple), on refuse.
    public NoteInfo Overwrite(string path, string text, string expectedHash)
    {
        string rel = NormalizePath(path); CheckSize(text ?? "");
        string current = Read(rel, out string h);
        if (string.IsNullOrEmpty(expectedHash)) throw new VaultException("empreinte", "Pour remplacer une note, donner l'empreinte lue avec la note (hash) : cela évite d'écraser un changement qu'on n'a pas vu.");
        if (!string.Equals(expectedHash, h, StringComparison.OrdinalIgnoreCase)) throw new VaultException("conflit", "La note a changé depuis sa lecture : la relire, puis refaire la modification.");
        WriteAtomic(Full(rel), text ?? ""); Refresh(); return Get(rel);
    }

    public NoteInfo Append(string path, string text)
    {
        string rel = NormalizePath(path); string cur = Read(rel, out _);
        string add = (cur.Length > 0 && !cur.EndsWith("\n") ? "\n" : "") + text;
        CheckSize(cur + add); WriteAtomic(Full(rel), cur + add); Refresh(); return Get(rel);
    }

    // Remplace un passage ; il doit apparaître exactement une fois (sinon on dit combien de fois, pour préciser).
    public NoteInfo ReplaceText(string path, string oldText, string newText, string expectedHash = null)
    {
        string rel = NormalizePath(path); string cur = Read(rel, out string h);
        if (!string.IsNullOrEmpty(expectedHash) && !string.Equals(expectedHash, h, StringComparison.OrdinalIgnoreCase)) throw new VaultException("conflit", "La note a changé depuis sa lecture : la relire, puis refaire la modification.");
        if (string.IsNullOrEmpty(oldText)) throw new VaultException("texte", "Le texte à remplacer est vide.");
        int n = 0, i = 0; while ((i = cur.IndexOf(oldText, i, StringComparison.Ordinal)) >= 0) { n++; i += oldText.Length; }
        if (n == 0) throw new VaultException("introuvable", "Le texte à remplacer n'est pas dans la note.");
        if (n > 1) throw new VaultException("ambigu", $"Le texte à remplacer apparaît {n} fois : en donner davantage pour qu'il soit unique.");
        string next = cur.Replace(oldText, newText ?? "", StringComparison.Ordinal); CheckSize(next);
        WriteAtomic(Full(rel), next); Refresh(); return Get(rel);
    }

    // Fixe une propriété de l'en-tête (créé au besoin) ; une liste devient « [a, b] ». Le reste de la note est inchangé.
    public NoteInfo SetProperty(string path, string key, object value)
    {
        string rel = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(key) || !Regex.IsMatch(key, @"^[\p{L}\p{N}_\- ]{1,40}$")) throw new VaultException("propriété", "Nom de propriété invalide.");
        string cur = Read(rel, out _); string nl = cur.Contains("\r\n") ? "\r\n" : "\n";
        string rendered = value is IEnumerable<string> l ? "[" + string.Join(", ", l.Select(Quote)) + "]" : Quote(value?.ToString() ?? "");
        string line = key.Trim() + ": " + rendered;
        var props = new Dictionary<string, object>(); int body = MarkdownParse.Frontmatter(cur, props);
        string next;
        if (body == 0) next = "---" + nl + line + nl + "---" + nl + cur;
        else
        {
            string head = cur[..body], rest = cur[body..];
            var lines = head.Split('\n').Select(x => x.TrimEnd('\r')).ToList();
            if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            int idx = lines.FindIndex(x => Regex.IsMatch(x, @"^" + Regex.Escape(key.Trim()) + @"\s*:", RegexOptions.IgnoreCase));
            if (idx > 0)
            {
                int end = idx + 1; while (end < lines.Count - 1 && Regex.IsMatch(lines[end], @"^\s+-\s")) end++;   // une liste à lignes « - » fait partie de la propriété
                lines.RemoveRange(idx, end - idx); lines.Insert(idx, line);
            }
            else lines.Insert(lines.Count - 1, line);   // avant la ligne « --- » de fin
            next = string.Join(nl, lines) + nl + rest;
        }
        CheckSize(next); WriteAtomic(Full(rel), next); Refresh(); return Get(rel);
    }

    // Une valeur simple s'écrit telle quelle ; sinon entre apostrophes (YAML n'y interprète aucun antislash : un chemin Windows y reste lisible par Obsidian).
    static string Quote(string v) => v.Length > 0 && Regex.IsMatch(v, @"^[^\s:#\[\]{},&*!|>'""%@`\\-][^:#\[\]{},\\]*$") && v == v.Trim() ? v : "'" + v.Replace("'", "''") + "'";

    public void CreateFolder(string path)
    {
        string rel = NormalizePath(path, addExt: false); Directory.CreateDirectory(Full(rel));
    }

    // Déplace ou renomme une note ; les liens [[…]] qui la visent sont réécrits dans tout le coffre. Rend le nombre de notes dont des liens ont changé.
    // Les liens à réécrire sont repérés AVANT le déplacement, avec la résolution de ce moment-là : deux notes de même nom ne se confondent pas.
    public int Move(string from, string to, bool updateLinks = true)
    {
        string a = NormalizePath(from), b = NormalizePath(to);
        string fa = Full(a), fb = Full(b);
        if (!File.Exists(fa)) throw new VaultException("introuvable", $"La note « {a} » n'existe pas.");
        if (File.Exists(fb) && !a.Equals(b, StringComparison.OrdinalIgnoreCase)) throw new VaultException("existe", $"La destination « {b} » existe déjà.");
        if (a.Equals(b, StringComparison.Ordinal)) return 0;
        string selfKey = notes.Keys.FirstOrDefault(k => k.Equals(a, StringComparison.OrdinalIgnoreCase)) ?? a;
        // note (chemin avant déplacement) -> ses liens qui visaient la note déplacée
        var pending = updateLinks ? notes.Values.Select(e => (Path: e.Info.Path, Text: e.Text, Links: e.Info.Links.Where(l => string.Equals(l.Resolved, a, StringComparison.OrdinalIgnoreCase)).ToList())).Where(x => x.Links.Count > 0).ToList() : null;
        Directory.CreateDirectory(Path.GetDirectoryName(fb));
        File.Move(fa, fb, a.Equals(b, StringComparison.OrdinalIgnoreCase));   // renommer seulement la casse : même fichier
        Refresh();   // l'index connaît la note à son nouvel endroit
        int updated = 0;
        if (pending != null)
        {
            string newName = Path.GetFileNameWithoutExtension(b);
            foreach (var x in pending)
            {
                string path = x.Path.Equals(selfKey, StringComparison.OrdinalIgnoreCase) ? b : x.Path;   // la note déplacée a elle-même changé de chemin
                var sb = new StringBuilder(x.Text); bool any = false;
                foreach (var l in x.Links.OrderByDescending(y => y.Start))
                {
                    string repl = LinkTargetFor(b, l.Target, newName);
                    if (repl == null || repl == l.Target) continue;
                    string rewritten = x.Text.Substring(l.Start, l.Length).Replace("[[" + l.Target, "[[" + repl, StringComparison.Ordinal);
                    sb.Remove(l.Start, l.Length); sb.Insert(l.Start, rewritten); any = true;
                }
                if (any) { WriteAtomic(Full(path), sb.ToString()); updated++; }
            }
            Refresh();
        }
        return updated;
    }
    // Comment écrire un lien vers la note à son nouvel emplacement : son nom si c'est sans ambiguïté, sinon son chemin.
    string LinkTargetFor(string newPath, string oldTarget, string newName)
    {
        bool pathStyle = oldTarget.Contains('/');
        if (pathStyle) return newPath[..^3];
        return Resolve(newName) == newPath ? newName : newPath[..^3];
    }

    // « Retirer » : la note part dans .trash/ (avec son dossier), récupérable à la main. Aucune suppression définitive par ce chemin.
    public string Trash(string path)
    {
        string rel = NormalizePath(path); string full = Full(rel);
        if (!File.Exists(full)) throw new VaultException("introuvable", $"La note « {rel} » n'existe pas.");
        string dest = Path.Combine(Root, ".trash", rel.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(dest)) dest = Path.Combine(Path.GetDirectoryName(dest), Path.GetFileNameWithoutExtension(dest) + " (" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ").md");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)); File.Move(full, dest); Refresh();
        return dest[(Root.Length + 1)..].Replace('\\', '/');
    }

    public sealed class FolderMove { public Dictionary<string, string> Moves = new(StringComparer.OrdinalIgnoreCase); public int Updated; }

    // Déplace ou renomme un dossier : chaque note qui s'y trouve est déplacée (les liens qui la visent sont réécrits), puis les autres fichiers (images…) et les dossiers vides suivent.
    // Rend où est allée chaque note et combien de notes ont vu leurs liens changer.
    public FolderMove MoveFolder(string from, string to)
    {
        string a = NormalizePath(from, false), b = NormalizePath(to, false);
        string fa = Full(a), fb = Full(b);
        if (!Directory.Exists(fa)) throw new VaultException("introuvable", $"Le dossier « {a} » n'existe pas.");
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) throw new VaultException("chemin", "Le dossier de destination est le même.");
        if (b.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase)) throw new VaultException("chemin", "Un dossier ne peut pas être déplacé dans lui-même.");
        if (File.Exists(fb) || (Directory.Exists(fb) && Directory.EnumerateFileSystemEntries(fb).Any())) throw new VaultException("existe", $"La destination « {b} » existe déjà.");
        var res = new FolderMove();
        // les sous-dossiers (même vides) existent à destination
        foreach (var d in Directory.EnumerateDirectories(fa, "*", SearchOption.AllDirectories))
        {
            string rel = d[(Root.Length + 1)..].Replace('\\', '/'); if (!Hidden(rel)) Directory.CreateDirectory(Full(b + rel[a.Length..]));
        }
        Directory.CreateDirectory(fb);
        foreach (var k in notes.Keys.Where(k => k.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList())
        {
            string np = b + k[a.Length..]; res.Updated += Move(k, np); res.Moves[k] = np;
        }
        foreach (var f in Directory.EnumerateFiles(fa, "*", SearchOption.AllDirectories).ToList())
        {
            string rel = f[(Root.Length + 1)..].Replace('\\', '/'); if (Hidden(rel)) continue;
            string dest = Full(b + rel[a.Length..]); if (!File.Exists(dest)) File.Move(f, dest);
        }
        RemoveEmptyDirs(fa);
        Refresh(); return res;
    }

    // Vide les dossiers devenus vides (de bas en haut), puis le dossier lui-même s'il l'est : un dossier vide n'est pas une perte.
    static void RemoveEmptyDirs(string full)
    {
        foreach (var d in Directory.EnumerateDirectories(full, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length).ToList())
            if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
        if (!Directory.EnumerateFileSystemEntries(full).Any()) Directory.Delete(full);
    }

    // « Retirer » un dossier : toutes ses notes partent dans .trash ; ce qui n'est pas une note (images…) reste, avec le dossier. Rend les notes retirées.
    public List<string> TrashFolder(string path)
    {
        string a = NormalizePath(path, false); string fa = Full(a);
        if (!Directory.Exists(fa)) throw new VaultException("introuvable", $"Le dossier « {a} » n'existe pas.");
        var gone = new List<string>();
        foreach (var k in notes.Keys.Where(k => k.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList()) { Trash(k); gone.Add(k); }
        if (Directory.Exists(fa)) RemoveEmptyDirs(fa);
        return gone;
    }

    // ---------- liens ----------
    public List<Backlink> Backlinks(string path)
    {
        string rel = NormalizePath(path); var l = new List<Backlink>();
        foreach (var e in notes.Values)
        {
            if (e.Info.Path.Equals(rel, StringComparison.OrdinalIgnoreCase)) continue;
            var lines = e.Text.Split('\n');
            foreach (var lk in e.Info.Links.Where(x => string.Equals(x.Resolved, rel, StringComparison.OrdinalIgnoreCase)))
                l.Add(new Backlink { Path = e.Info.Path, Title = e.Info.Title, Line = lk.Line, Context = lk.Line <= lines.Length ? lines[lk.Line - 1].Trim() : "" });
        }
        return l.OrderBy(b => b.Path, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Line).ToList();
    }

    // Les liens qui ne mènent à aucune note : des notes à écrire.
    public List<(string Target, int Count, string From)> Unresolved()
    {
        return notes.Values.SelectMany(e => e.Info.Links.Where(l => l.Resolved == null && !l.Embed).Select(l => (l.Target, e.Info.Path)))
            .GroupBy(x => x.Target, StringComparer.OrdinalIgnoreCase).Select(g => (g.Key, g.Count(), g.First().Path)).OrderByDescending(x => x.Item2).ThenBy(x => x.Key).ToList();
    }

    public Dictionary<string, int> Tags()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in notes.Values) foreach (var t in e.Info.Tags) d[t] = d.GetValueOrDefault(t) + 1;
        return d.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    // ---------- recherche ----------
    static readonly CompareInfo Cmp = CultureInfo.InvariantCulture.CompareInfo;
    const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
    static int IndexOfLoose(string text, string term, int start = 0) => start >= text.Length ? -1 : Cmp.IndexOf(text, term, start, Loose);

    // Termes séparés par des espaces (tous requis, accents et casse ignorés), « phrase entre guillemets », et filtres : tag:projet  in:Projets  type:projet.
    public List<SearchHit> Search(string query, int limit = 20)
    {
        var terms = new List<string>(); string tag = null, folder = null, type = null;
        foreach (Match m in Regex.Matches(query ?? "", "\"([^\"]+)\"|(\\S+)"))
        {
            string t = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (!m.Groups[1].Success && t.StartsWith("tag:", StringComparison.OrdinalIgnoreCase)) tag = t[4..].TrimStart('#');
            else if (!m.Groups[1].Success && t.StartsWith("in:", StringComparison.OrdinalIgnoreCase)) folder = t[3..].Trim('/');
            else if (!m.Groups[1].Success && t.StartsWith("type:", StringComparison.OrdinalIgnoreCase)) type = t[5..];
            else if (t.Length > 0) terms.Add(t);
        }
        limit = Math.Clamp(limit, 1, 100);
        var hits = new List<SearchHit>();
        foreach (var e in notes.Values)
        {
            var n = e.Info;
            if (tag != null && !n.Tags.Any(x => x.Equals(tag, StringComparison.OrdinalIgnoreCase) || x.StartsWith(tag + "/", StringComparison.OrdinalIgnoreCase))) continue;
            if (folder != null && !(n.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase) || n.Folder.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))) continue;
            if (type != null && !string.Equals(n.Prop("type"), type, StringComparison.OrdinalIgnoreCase)) continue;
            int score = 0; bool all = true; int line = 0; string snippet = null;
            foreach (var term in terms)
            {
                int s = 0;
                if (IndexOfLoose(n.Title, term) >= 0) s += n.Title.Equals(term, StringComparison.OrdinalIgnoreCase) ? 30 : 10;
                if (n.Tags.Any(x => IndexOfLoose(x, term) >= 0)) s += 6;
                if (n.Headings.Any(h => IndexOfLoose(h.Text, term) >= 0)) s += 4;
                if (IndexOfLoose(n.Path, term) >= 0) s += 3;
                int at = IndexOfLoose(e.Text, term), cnt = 0;
                for (int i = at; i >= 0 && cnt < 10; i = IndexOfLoose(e.Text, term, i + Math.Max(1, term.Length))) cnt++;
                s += cnt;
                if (at >= 0 && snippet == null) { snippet = Snippet(e.Text, at, term.Length); line = LineOfIndex(e.Text, at); }
                if (s == 0) { all = false; break; }
                score += s;
            }
            if (!all) continue;
            if (terms.Count == 0) score = 1;   // filtres seuls : tout ce qui correspond, du plus récent
            else score += Math.Max(0, 3 - n.Path.Count(ch => ch == '/'));   // à égalité, la note la moins profondément rangée passe devant
            hits.Add(new SearchHit { Path = n.Path, Title = n.Title, Score = score, Snippet = snippet ?? "", Line = line, Tags = n.Tags });
        }
        return hits.OrderByDescending(h => h.Score).ThenByDescending(h => Get(h.Path).Modified).Take(limit).ToList();
    }

    static string Snippet(string text, int at, int len)
    {
        int a = Math.Max(0, at - 60), b = Math.Min(text.Length, at + len + 80);
        string s = text[a..b].Replace('\r', ' ').Replace('\n', ' ');
        return (a > 0 ? "…" : "") + s.Trim() + (b < text.Length ? "…" : "");
    }
    static int LineOfIndex(string text, int index) { int n = 1; for (int i = 0; i < index && i < text.Length; i++) if (text[i] == '\n') n++; return n; }

    // ---------- projets et tâches ----------
    static bool IsTemplate(NoteInfo n) { string f = n.Folder; return f.Equals("Modèles", StringComparison.OrdinalIgnoreCase) || f.StartsWith("Modèles/", StringComparison.OrdinalIgnoreCase) || f.Equals("Templates", StringComparison.OrdinalIgnoreCase) || f.StartsWith("Templates/", StringComparison.OrdinalIgnoreCase); }
    static bool IsProject(NoteInfo n) { if (IsTemplate(n)) return false; var t = n.Prop("type"); return t != null && (t.Equals("projet", StringComparison.OrdinalIgnoreCase) || t.Equals("project", StringComparison.OrdinalIgnoreCase)); }

    public List<ProjectInfo> Projects(string status = null)
    {
        var l = new List<ProjectInfo>();
        foreach (var n in notes.Values.Select(e => e.Info).Where(IsProject))
        {
            string st = n.Prop("statut") ?? n.Prop("status") ?? "";
            if (status != null && !st.Equals(status, StringComparison.OrdinalIgnoreCase)) continue;
            var open = n.Tasks.Where(t => !t.Done).ToList();
            l.Add(new ProjectInfo
            {
                Path = n.Path, Name = n.Title, Status = st, Repo = n.Prop("depot") ?? n.Prop("repo"), Priority = n.Prop("priorite") ?? n.Prop("priorité") ?? n.Prop("priority"), Summary = n.Prop("resume") ?? n.Prop("résumé"),
                OpenTasks = open.Count, DoneTasks = n.Tasks.Count - open.Count, Modified = n.Modified, NextDue = open.Where(t => t.Due != null).Select(t => t.Due).OrderBy(x => x).FirstOrDefault()
            });
        }
        return l.OrderBy(p => p.Status == "actif" ? 0 : p.Status == "en pause" ? 1 : 2).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Les tâches « - [ ] » (et faites si demandé) de tout le coffre, ou d'un dossier / d'une note.
    public List<TaskItem> Tasks(string under = null, bool includeDone = false)
    {
        string u = under?.Replace('\\', '/').Trim('/');
        return notes.Values.Select(e => e.Info).Where(n => !IsTemplate(n)).Where(n => u == null || n.Path.Equals(u, StringComparison.OrdinalIgnoreCase) || n.Path.Equals(u + ".md", StringComparison.OrdinalIgnoreCase) || n.Path.StartsWith(u + "/", StringComparison.OrdinalIgnoreCase))
            .SelectMany(n => n.Tasks).Where(t => includeDone || !t.Done).OrderBy(t => t.Due ?? "9999").ThenBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Line).ToList();
    }

    public const string ProjectTemplate = "---\ntype: projet\nstatut: actif\ndepot: {depot}\npriorite: normale\nresume: {resume}\n---\n# {nom}\n\n## Objectif\n\n{resume}\n\n## Prochaines actions\n\n- [ ] \n\n## Notes\n\n## Journal\n";

    public NoteInfo CreateProject(string name, string repo = null, string summary = null)
    {
        string clean = Regex.Replace(name ?? "", @"[\\/:*?""<>|\x00-\x1f]", " ").Trim().TrimEnd('.');
        if (clean.Length == 0) throw new VaultException("nom", "Le nom du projet est vide.");
        string text = ProjectTemplate.Replace("{nom}", clean).Replace("{depot}", Quote(repo ?? "")).Replace("{resume}", (summary ?? "").Replace("\r", " ").Replace("\n", " ").Trim());
        return Create("Projets/" + clean + ".md", text);
    }

    // ---------- graphe ----------
    public (List<object> Nodes, List<object> Edges) Graph()
    {
        var nodes = notes.Values.Select(e => e.Info).Select(n => (object)new { id = n.Path, title = n.Title, folder = n.Folder, type = n.Prop("type") ?? "", tags = n.Tags, size = n.Size }).ToList();
        var edges = new List<object>(); var seen = new HashSet<string>();
        foreach (var e in notes.Values) foreach (var l in e.Info.Links.Where(x => x.Resolved != null && !x.Resolved.Equals(e.Info.Path, StringComparison.OrdinalIgnoreCase)))
            if (seen.Add(e.Info.Path + ">" + l.Resolved)) edges.Add(new { from = e.Info.Path, to = l.Resolved });
        return (nodes, edges);
    }
}




