using System.Text;
using System.Text.RegularExpressions;

namespace Kdx.Coffre;

// Un lien [[Note]], [[Note|alias]], [[Note#Titre]] ou une incrustation ![[Note]].
public sealed class LinkRef
{
    public string Target;     // ce qui est écrit avant | et # (« Note » ou « Dossier/Note »)
    public string Heading;    // après #, sinon null
    public string Alias;      // après |, sinon null
    public bool Embed;        // ![[…]]
    public string Resolved;   // chemin de la note visée (relatif au coffre), null si elle n'existe pas
    public int Line;          // ligne (1 = première du fichier)
    public int Start, Length; // position du lien entier dans le texte
}

public sealed class TaskItem
{
    public string Path, Text, Due;
    public int Line;
    public bool Done;
}

public sealed class Heading
{
    public int Level, Line;
    public string Text;
}

// Ce qu'on sait lire dans un fichier Markdown, sans rien interpréter de plus que ce que fait Obsidian pour les liens, étiquettes et tâches.
public static class MarkdownParse
{
    static readonly Regex LinkRx = new(@"(?<embed>!)?\[\[(?<t>[^\[\]\|#\^\r\n]+?)(?:#(?<h>[^\[\]\|\r\n]*))?(?:\|(?<a>[^\[\]\r\n]*))?\]\]", RegexOptions.Compiled);
    static readonly Regex TagRx = new(@"(?<![\p{L}\p{N}_/#&\]\)])#(?<t>[\p{L}\p{N}_\-]+(?:/[\p{L}\p{N}_\-]+)*)", RegexOptions.Compiled);
    static readonly Regex TaskRx = new(@"^\s*[-*+]\s+\[(?<x>[ xX])\]\s+(?<t>.*)$", RegexOptions.Compiled);
    static readonly Regex DueRx = new(@"(?:📅|due:|échéance:)\s*(?<d>\d{4}-\d{2}-\d{2})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex HeadRx = new(@"^(?<h>#{1,6})\s+(?<t>\S.*?)\s*#*\s*$", RegexOptions.Compiled);
    static readonly Regex InlineCode = new(@"`[^`\r\n]*`", RegexOptions.Compiled);

    // En-tête de propriétés : un bloc « --- … --- » tout en haut. Valeurs : texte, ou liste (« [a, b] » ou lignes « - a »). Rend la fin de l'en-tête (index du premier
    // caractère du corps) ; 0 s'il n'y en a pas.
    public static int Frontmatter(string text, Dictionary<string, object> props)
    {
        if (text.Length < 4 || !text.StartsWith("---")) return 0;
        int eol = text.IndexOf('\n'); if (eol < 0 || text[..eol].Trim() != "---") return 0;
        int pos = eol + 1; string lastKey = null;
        while (pos < text.Length)
        {
            int e = text.IndexOf('\n', pos); string line = e < 0 ? text[pos..] : text[pos..e]; int next = e < 0 ? text.Length : e + 1;
            string t = line.TrimEnd('\r');
            if (t.Trim() == "---" || t.Trim() == "...") return next;
            var li = Regex.Match(t, @"^\s+-\s+(?<v>.*)$");
            if (li.Success && lastKey != null)
            {
                if (props.TryGetValue(lastKey, out var cur) && cur is List<string> l) l.Add(Unquote(li.Groups["v"].Value));
                else props[lastKey] = new List<string> { Unquote(li.Groups["v"].Value) };
            }
            else
            {
                var kv = Regex.Match(t, @"^(?<k>[^\s:#][^:]*?)\s*:\s*(?<v>.*)$");
                if (kv.Success)
                {
                    lastKey = kv.Groups["k"].Value.Trim(); string v = kv.Groups["v"].Value.Trim();
                    if (v.StartsWith("[") && v.EndsWith("]")) props[lastKey] = v[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Unquote).Where(x => x.Length > 0).ToList();
                    else if (v.Length == 0) props[lastKey] = "";   // une liste à lignes « - » peut suivre
                    else props[lastKey] = Unquote(v);
                }
            }
            pos = next;
        }
        return 0;   // jamais refermé : ce n'est pas un en-tête
    }

    // Entre apostrophes, « '' » est une apostrophe ; entre guillemets, seuls \" et \\ sont défaits (assez pour ce qu'on écrit et ce qu'écrit Obsidian).
    static string Unquote(string v)
    {
        v = v.Trim();
        if (v.Length >= 2 && v[0] == '\'' && v[^1] == '\'') return v[1..^1].Replace("''", "'");
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') return v[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        return v;
    }

    // Le texte où les blocs de code (clôturés ou en ligne) et l'en-tête sont remplacés par des espaces, mêmes positions et mêmes sauts de ligne :
    // on y cherche liens, étiquettes, tâches et titres sans tomber sur du code.
    public static string Mask(string text, int bodyStart)
    {
        var sb = new StringBuilder(text.Length); bool fence = false; int pos = 0;
        while (pos < text.Length)
        {
            int e = text.IndexOf('\n', pos); int end = e < 0 ? text.Length : e + 1;
            string line = text[pos..end]; string trimmed = line.TrimStart(' ').TrimEnd('\r', '\n');
            bool isFence = trimmed.StartsWith("```") || trimmed.StartsWith("~~~");
            if (pos < bodyStart || fence || isFence) sb.Append(Blank(line)); else sb.Append(InlineCode.Replace(line, m => new string(' ', m.Length)));
            if (isFence) fence = !fence;
            pos = end;
        }
        return sb.ToString();
    }
    static string Blank(string s) { var c = s.ToCharArray(); for (int i = 0; i < c.Length; i++) if (c[i] != '\n' && c[i] != '\r') c[i] = ' '; return new string(c); }

    static int LineOf(string text, int index) { int n = 1; for (int i = 0; i < index && i < text.Length; i++) if (text[i] == '\n') n++; return n; }

    public static List<LinkRef> Links(string text, string masked)
    {
        var l = new List<LinkRef>();
        // les numéros de ligne sont comptés au fil des correspondances (une seule passe sur le texte)
        int line = 1, at = 0;
        foreach (Match m in LinkRx.Matches(masked))
        {
            for (; at < m.Index; at++) if (masked[at] == '\n') line++;
            l.Add(new LinkRef
            {
                Target = m.Groups["t"].Value.Trim(), Heading = m.Groups["h"].Success ? m.Groups["h"].Value.Trim() : null, Alias = m.Groups["a"].Success ? m.Groups["a"].Value : null,
                Embed = m.Groups["embed"].Success, Line = line, Start = m.Index, Length = m.Length
            });
        }
        return l;
    }

    // Les étiquettes du corps (#projet, #a/b) : pas dans le code, pas dans un lien, jamais seulement des chiffres (#12).
    public static IEnumerable<string> Tags(string masked)
    {
        // un lien [[Note#Titre]] ou une adresse n'est pas une étiquette : on masque les liens avant de chercher
        string noLinks = LinkRx.Replace(masked, m => new string(' ', m.Length));
        foreach (Match m in TagRx.Matches(noLinks)) { string t = m.Groups["t"].Value; if (!t.All(char.IsDigit)) yield return t; }
    }

    public static List<TaskItem> Tasks(string path, string masked, string text)
    {
        var l = new List<TaskItem>(); var maskedLines = masked.Split('\n'); var lines = text.Split('\n');
        for (int i = 0; i < maskedLines.Length && i < lines.Length; i++)
        {
            if (maskedLines[i].Trim().Length == 0) continue;
            var m = TaskRx.Match(lines[i].TrimEnd('\r')); if (!m.Success) continue;
            string t = m.Groups["t"].Value.Trim(); if (t.Length == 0) continue; var due = DueRx.Match(t);
            l.Add(new TaskItem { Path = path, Line = i + 1, Done = m.Groups["x"].Value != " ", Text = t, Due = due.Success ? due.Groups["d"].Value : null });
        }
        return l;
    }

    public static List<Heading> Headings(string masked)
    {
        var l = new List<Heading>(); var lines = masked.Split('\n');
        for (int i = 0; i < lines.Length; i++) { var m = HeadRx.Match(lines[i].TrimEnd('\r')); if (m.Success) l.Add(new Heading { Level = m.Groups["h"].Length, Text = m.Groups["t"].Value, Line = i + 1 }); }
        return l;
    }
}


