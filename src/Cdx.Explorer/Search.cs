using System.Text.RegularExpressions;

namespace Cdx;

// La recherche dans les sous-dossiers : parcourt un dossier (ou, sans dossier, tous les disques fixes) en arrière-plan et envoie les éléments qui correspondent par paquets (« fs.found »).
// Critères : des mots du nom (tous doivent s'y trouver ; « * » et « ? » sont des jokers), un type (dossiers, documents, images...), une date de modification et une taille.
// Les liens et jonctions ne sont pas suivis (pas de boucle) ; les éléments cachés suivent le réglage « show.hidden » ; au plus MaxResults éléments.
sealed record SearchSpec(string Root, string? Query, string? Kind, string? Date, string? Size);   // null : critère absent

static class Search
{
    public const int MaxResults = 20000;

    static readonly Dictionary<string, HashSet<string>> Kinds = new()
    {
        ["document"] = new(StringComparer.OrdinalIgnoreCase) { ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf", ".txt", ".md", ".rtf", ".odt", ".ods", ".odp", ".csv" },
        ["image"] = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico", ".tif", ".tiff", ".heic", ".avif" },
        ["audio"] = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".opus", ".wma" },
        ["video"] = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".avi", ".mov", ".webm", ".wmv", ".m4v" },
        ["archive"] = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso" },
        ["code"] = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".js", ".ts", ".py", ".rs", ".c", ".cpp", ".h", ".hpp", ".html", ".css", ".json", ".xml", ".sh", ".ps1", ".java", ".go", ".yml", ".yaml", ".toml", ".sql", ".kt" },
        ["app"] = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".msi", ".bat", ".cmd", ".lnk" },
    };

    // Le test d'un nom : chaque mot de la requête doit s'y trouver (sous-chaîne sans casse), ou, s'il contient « * » ou « ? », correspondre au joker.
    public static Func<string, bool> NameMatcher(string? query)
    {
        var words = (query ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return _ => true;
        var tests = words.Select<string, Func<string, bool>>(w =>
        {
            if (w.IndexOfAny(new[] { '*', '?' }) < 0) return n => n.Contains(w, StringComparison.CurrentCultureIgnoreCase);
            var re = new Regex("^" + Regex.Escape(w).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            return n => { try { return re.IsMatch(n); } catch (RegexMatchTimeoutException) { return false; } };
        }).ToArray();
        return n => tests.All(t => t(n));
    }

    static DateTime? Cutoff(string? date) => date switch
    {
        "today" => DateTime.Today, "week" => DateTime.Now.AddDays(-7), "month" => DateTime.Now.AddDays(-30), "year" => DateTime.Now.AddDays(-365), _ => null,
    };

    static (long min, long max)? SizeRange(string? size) => size switch
    {
        "small" => (0, 1L << 20), "medium" => (1L << 20, 100L << 20), "large" => (100L << 20, long.MaxValue), "huge" => (1L << 30, long.MaxValue), _ => null,
    };

    // Un élément correspond-il aux critères autres que le nom ? (Les dossiers ne passent pas un filtre de taille.)
    public static bool Passes(SearchSpec s, bool isDir, string ext, long size, DateTime modified)
    {
        string k = s.Kind ?? "all";
        if (k == "folder") { if (!isDir) return false; }
        else if (k != "all") { if (isDir || !Kinds.TryGetValue(k, out var set) || !set.Contains(ext)) return false; }
        if (Cutoff(s.Date) is { } c && modified < c) return false;
        if (SizeRange(s.Size) is { } r && (isDir || size < r.min || size >= r.max)) return false;
        return true;
    }

    static bool ShowHidden() => Settings.Get("show.hidden") is { } n && n.GetValueKind() == System.Text.Json.JsonValueKind.True;

    // Parcourt et envoie. `emit` reçoit des messages « fs.found » ; le dernier a done = true. Lève OperationCanceledException si `ct` est annulé.
    public static void Run(SearchSpec s, string? tab, int sid, CancellationToken ct, Action<object> emit)
    {
        var match = NameMatcher(s.Query);
        bool showHidden = ShowHidden();
        var roots = string.IsNullOrEmpty(s.Root)
            ? DriveInfo.GetDrives().Where(d => { try { return d.IsReady && d.DriveType == DriveType.Fixed; } catch { return false; } }).Select(d => d.Name).ToList()
            : new List<string> { s.Root };
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, ReturnSpecialDirectories = false, MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = FileAttributes.ReparsePoint | (showHidden ? 0 : FileAttributes.Hidden | FileAttributes.System),
        };
        var batch = new List<object>(); int total = 0, scanned = 0; bool truncated = false; var last = DateTime.UtcNow;
        void Flush(bool done) { emit(new { type = "fs.found", tab, sid, items = batch, scanned, done, truncated }); batch = new List<object>(); last = DateTime.UtcNow; }
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var e in new DirectoryInfo(root).EnumerateFileSystemInfos("*", opts))
            {
                ct.ThrowIfCancellationRequested();
                scanned++;
                try
                {
                    bool isDir = (e.Attributes & FileAttributes.Directory) != 0;
                    if (!match(e.Name)) goto progress;
                    long size = isDir ? -1 : ((FileInfo)e).Length;
                    if (!Passes(s, isDir, isDir ? "" : e.Extension, size, e.LastWriteTime)) goto progress;
                    string rel = Path.GetRelativePath(root, e.FullName), dirRel = Path.GetDirectoryName(rel) ?? "";
                    batch.Add(new { n = string.IsNullOrEmpty(s.Root) ? e.FullName : rel, b = e.Name, r = string.IsNullOrEmpty(s.Root) ? (Path.GetDirectoryName(e.FullName) ?? "") : dirRel, d = isDir, s = size, m = new DateTimeOffset(e.LastWriteTime).ToUnixTimeMilliseconds(), p = e.FullName });
                    if (++total >= MaxResults) { truncated = true; Flush(true); return; }
                }
                catch { }
            progress:
                if (batch.Count >= 300 || (batch.Count > 0 || scanned % 2000 == 0) && (DateTime.UtcNow - last).TotalMilliseconds > 200) Flush(false);
            }
        }
        Flush(true);
    }
}
