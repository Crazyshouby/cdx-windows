using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Kdx;

// Un dépôt git tel que la page l'affiche. Rien ici n'écrit dans un dépôt : on ne lance que des commandes de lecture (status, log, config), avec
// --no-optional-locks (git ne rafraîchit pas l'index) et sans jamais joindre le réseau, si bien que « à envoyer » et « à récupérer » se comptent
// d'après la dernière synchronisation connue (git fetch, git push, git pull faits ailleurs).
sealed class RepoInfo
{
    public string Path, Name, Branch, Upstream, Remote, WebUrl, LastHash, LastDate, LastAuthor, LastSubject, Error;
    public bool Detached, NoCommits;
    public int Changed, Untracked, Ahead, Behind;
}

sealed record Commit(string Hash, string Date, string Author, string Subject);
sealed record FileChange(string Code, string Path);

static class Git
{
    // Ne sont jamais parcourus : dépendances, sorties de compilation, caches. (Un dépôt ne contient jamais un autre dépôt qu'on cherche.)
    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", ".git", "target", "build", "build-dev", "dist", "bin", "obj", ".venv", "venv", "__pycache__", ".gradle", ".cargo", ".rustup", "AppData", ".cache", "$RECYCLE.BIN" };

    // ---------- lancement ----------
    // Rend (code de sortie, sortie standard, sortie d'erreur). Délai dépassé : le processus est arrêté et le code est -1.
    public static (int code, string output, string error) Run(string dir, int timeoutMs, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--no-optional-locks");
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("core.quotepath=false");
        if (dir != null) { psi.ArgumentList.Add("-C"); psi.ArgumentList.Add(dir); }
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";   // jamais de demande d'identifiants
        try
        {
            using var p = Process.Start(psi);
            var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, "", "délai dépassé"); }
            p.WaitForExit();
            return (p.ExitCode, o.Result, e.Result);
        }
        catch (Exception ex) { return (-2, "", ex.Message); }   // git introuvable, par exemple
    }

    public static string Version()
    {
        var r = Run(null, 5000, "--version");
        return r.code == 0 ? r.output.Trim() : null;
    }

    // ---------- recherche ----------
    // Les dossiers (au plus `maxDepth` niveaux sous chaque racine) qui contiennent « .git » : dossier, ou fichier pour un worktree. On ne descend pas dans un dépôt,
    // ni dans les liens et jonctions (pas de boucle), ni dans les dossiers de SkipDirs.
    public static List<string> Scan(IEnumerable<string> roots, int maxDepth = 4, CancellationToken ct = default)
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(string dir, int depth)
        {
            if (ct.IsCancellationRequested) return;
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"))) { found.Add(dir); return; }
            if (depth >= maxDepth) return;
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir).ToList(); } catch { return; }
            foreach (var s in subs)
            {
                string name = Path.GetFileName(s);
                if (SkipDirs.Contains(name)) continue;
                try { if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                Walk(s, depth + 1);
            }
        }
        foreach (var root in roots)
        {
            try { string full = Path.GetFullPath(root).TrimEnd('\\', '/'); if (Directory.Exists(full)) Walk(full, 0); } catch { }
        }
        return found.ToList();
    }

    // ---------- lecture d'un dépôt ----------
    public static RepoInfo Inspect(string path)
    {
        var r = new RepoInfo { Path = path, Name = Path.GetFileName(path) };
        var st = Run(path, 20000, "status", "--porcelain=v2", "--branch");
        if (st.code != 0) { r.Error = Short(st); return r; }
        ParseStatus(r, st.output);

        var lg = Run(path, 10000, "log", "-1", "--format=%H%x1f%aI%x1f%an%x1f%s");
        var f = lg.code == 0 ? lg.output.Trim().Split('\x1f') : null;
        if (f != null && f.Length >= 4) { r.LastHash = f[0]; r.LastDate = f[1]; r.LastAuthor = f[2]; r.LastSubject = f[3]; }
        else r.NoCommits = true;

        var cf = Run(path, 5000, "config", "--get-regexp", @"^remote\..*\.url$");
        if (cf.code == 0)
        {
            string first = null, origin = null;
            foreach (var line in cf.output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int sp = line.IndexOf(' '); if (sp < 0) continue;
                string key = line[..sp].Trim(), url = line[(sp + 1)..].Trim();
                first ??= url; if (key == "remote.origin.url") origin = url;
            }
            r.Remote = origin ?? first;
        }
        r.WebUrl = WebUrl(r.Remote);
        return r;
    }

    // « git status --porcelain=v2 --branch » : les lignes « # branch.* », puis « 1 »/« 2 »/« u » (fichier suivi qui a changé) et « ? » (nouveau fichier).
    public static void ParseStatus(RepoInfo r, string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            string l = raw.TrimEnd('\r');
            if (l.StartsWith("# branch.head ")) { r.Branch = l[14..]; if (r.Branch == "(detached)") { r.Detached = true; r.Branch = null; } }
            else if (l.StartsWith("# branch.upstream ")) r.Upstream = l[18..];
            else if (l.StartsWith("# branch.ab "))
            {
                var m = Regex.Match(l, @"\+(\d+) -(\d+)");
                if (m.Success) { r.Ahead = int.Parse(m.Groups[1].Value); r.Behind = int.Parse(m.Groups[2].Value); }
            }
            else if (l.StartsWith("1 ") || l.StartsWith("2 ") || l.StartsWith("u ")) r.Changed++;
            else if (l.StartsWith("? ")) r.Untracked++;
        }
    }

    public static List<Commit> Log(string path, int n = 40)
    {
        var res = new List<Commit>();
        var lg = Run(path, 15000, "log", "-n", n.ToString(), "--format=%h%x1f%aI%x1f%an%x1f%s");
        if (lg.code != 0) return res;
        foreach (var line in lg.output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.TrimEnd('\r').Split('\x1f');
            if (f.Length >= 4) res.Add(new Commit(f[0], f[1], f[2], f[3]));
        }
        return res;
    }

    // Les fichiers qui ont changé : M modifié, A ajouté, D supprimé, R renommé, U en conflit, ? nouveau (pas encore suivi). Au plus `max`.
    public static List<FileChange> Changes(string path, int max = 300)
    {
        var res = new List<FileChange>();
        var st = Run(path, 20000, "status", "--porcelain=v1");
        if (st.code != 0) return res;
        foreach (var raw in st.output.Split('\n'))
        {
            string l = raw.TrimEnd('\r');
            if (l.Length < 4) continue;
            string xy = l[..2], p = l[3..];
            int arrow = p.IndexOf(" -> ", StringComparison.Ordinal); if (arrow >= 0) p = p[(arrow + 4)..];
            string code = xy == "??" ? "?" : xy.Contains('U') || xy == "AA" || xy == "DD" ? "U" : xy.Contains('R') ? "R" : xy.Contains('D') ? "D" : xy.Contains('A') ? "A" : "M";
            res.Add(new FileChange(code, p.Trim('"')));
            if (res.Count >= max) break;
        }
        return res;
    }

    // ---------- adresse web ----------
    // L'adresse web d'un dépôt à partir de son remote (https ou ssh), seulement pour les hébergeurs connus ; jamais d'identifiant (un jeton glissé
    // dans une adresse https n'atteint ni la page ni le navigateur). Sinon null.
    static readonly string[] KnownHosts = { "github.com", "gitlab.com", "bitbucket.org", "codeberg.org" };
    public static string WebUrl(string remote)
    {
        if (string.IsNullOrWhiteSpace(remote)) return null;
        remote = remote.Trim();
        var m = Regex.Match(remote, @"^(?:ssh://)?git@([^:/]+)[:/](.+?)(?:\.git)?/?$");
        if (!m.Success) m = Regex.Match(remote, @"^https?://(?:[^@/]+@)?([^/]+)/(.+?)(?:\.git)?/?$");
        if (!m.Success) return null;
        string host = m.Groups[1].Value.ToLowerInvariant(), rest = m.Groups[2].Value;
        if (Array.IndexOf(KnownHosts, host) < 0) return null;
        if (!Regex.IsMatch(rest, @"^[\w.\-]+(/[\w.\-]+)+$")) return null;
        return "https://" + host + "/" + rest;
    }

    static string Short((int code, string output, string error) r)
    {
        string e = (r.error ?? "").Trim().Split('\n')[0].Trim();
        if (e.Contains("dubious ownership")) return "dépôt d'un autre propriétaire (git le refuse)";
        return e.Length > 0 ? e : "git a échoué (code " + r.code + ")";
    }
}
