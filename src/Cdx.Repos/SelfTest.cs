namespace Cdx;

// cdx-repos.exe --selftest-git : exerce la lecture de git sur de vrais dépôts jetables (un dépôt propre, modifié, avec un fichier neuf, en avance sur son remote,
// vide, un dossier qui n'est pas un dépôt) et la recherche (profondeur, dossiers ignorés), sans fenêtre ni entrée simulée ni accès au réseau. Rien n'est écrit hors du
// dossier temporaire. Résultat dans %LOCALAPPDATA%\CdxShell\selftest-git.txt : une ligne par vérification, OK ou ÉCHEC.
static class SelfTest
{
    public static void Run()
    {
        var lines = new List<string>(); int bad = 0;
        void Check(string what, bool ok) { lines.Add((ok ? "OK     " : "ÉCHEC  ") + what); if (!ok) bad++; }
        string tmp = Path.Combine(Path.GetTempPath(), "cdx-selftest-git-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        // git commit exige une identité : donnée à chaque appel, sans toucher à la configuration de personne.
        string[] who = { "-c", "user.name=Essai", "-c", "user.email=essai@example.invalid", "-c", "commit.gpgsign=false" };
        (int code, string output, string error) G(string dir, params string[] a) => Git.Run(dir, 30000, who.Concat(a).ToArray());
        void Write(string dir, string file, string text) => File.WriteAllText(Path.Combine(dir, file), text);

        try
        {
            string v = Git.Version();
            Check("git est installé (" + v + ")", v != null);
            if (v == null) return;

            // --- un dépôt avec un remote local (aucun réseau) ---
            string bare = Path.Combine(tmp, "origine.git"), a = Path.Combine(tmp, "projets", "alpha");
            Directory.CreateDirectory(bare); Directory.CreateDirectory(a);
            G(bare, "init", "--bare", "-b", "main");
            G(a, "init", "-b", "main");
            Write(a, "un.txt", "un\n");
            G(a, "add", "."); G(a, "commit", "-m", "premier enregistrement");
            G(a, "remote", "add", "origin", bare);
            G(a, "push", "-u", "origin", "main");
            var r = Git.Inspect(a);
            Check("dépôt propre : aucune erreur, branche main, rien à enregistrer", r.Error == null && r.Branch == "main" && r.Changed == 0 && r.Untracked == 0);
            Check("dépôt propre : à jour avec son remote (0 à envoyer, 0 à récupérer)", r.Upstream == "origin/main" && r.Ahead == 0 && r.Behind == 0);
            Check("dernier enregistrement lu (message, auteur, date)", r.LastSubject == "premier enregistrement" && r.LastAuthor == "Essai" && r.LastDate != null && r.LastHash?.Length == 40);
            Check("remote connu, mais pas d'adresse web (chemin local)", r.Remote == bare && r.WebUrl == null);

            Write(a, "un.txt", "un modifié\n");
            Write(a, "neuf.txt", "neuf\n");
            r = Git.Inspect(a);
            Check("un fichier modifié et un fichier neuf sont comptés à part", r.Changed == 1 && r.Untracked == 1);
            var ch = Git.Changes(a);
            Check("liste des fichiers : M un.txt et ? neuf.txt", ch.Any(c => c.Code == "M" && c.Path == "un.txt") && ch.Any(c => c.Code == "?" && c.Path == "neuf.txt"));

            G(a, "add", "."); G(a, "commit", "-m", "deuxième");
            r = Git.Inspect(a);
            Check("après enregistrement : propre, mais 1 à envoyer", r.Changed == 0 && r.Untracked == 0 && r.Ahead == 1 && r.Behind == 0);
            var log = Git.Log(a);
            Check("journal : deux enregistrements, le plus récent d'abord", log.Count == 2 && log[0].Subject == "deuxième" && log[1].Subject == "premier enregistrement");

            // --- un dépôt de plus, en retard sur son remote (un autre clone y a envoyé) ---
            string c2 = Path.Combine(tmp, "projets", "alpha-clone");
            G(a, "push");
            Git.Run(null, 30000, "clone", "-q", bare, c2);
            Write(c2, "autre.txt", "autre\n"); G(c2, "add", "."); G(c2, "commit", "-m", "depuis l'autre clone"); G(c2, "push");
            G(a, "fetch", "-q");
            r = Git.Inspect(a);
            Check("après un fetch : 1 à récupérer", r.Behind == 1 && r.Ahead == 0);

            // --- vide, détaché, pas un dépôt ---
            string vide = Path.Combine(tmp, "projets", "vide"); Directory.CreateDirectory(vide); G(vide, "init", "-b", "main");
            r = Git.Inspect(vide);
            Check("dépôt sans aucun enregistrement : signalé vide, sans erreur", r.Error == null && r.NoCommits && r.Remote == null);
            string det = Path.Combine(tmp, "projets", "detache"); Directory.CreateDirectory(det); G(det, "init", "-b", "main");
            Write(det, "x.txt", "x\n"); G(det, "add", "."); G(det, "commit", "-m", "x"); G(det, "checkout", "-q", "--detach");
            r = Git.Inspect(det);
            Check("HEAD détaché reconnu (pas de nom de branche)", r.Detached && r.Branch == null);
            string rien = Path.Combine(tmp, "pas-un-depot"); Directory.CreateDirectory(rien);
            r = Git.Inspect(rien);
            Check("un dossier qui n'est pas un dépôt : une erreur lisible, pas d'exception", r.Error != null);

            // --- la lecture n'écrit rien (aucun verrou d'index, rien de nouveau dans .git) ---
            string idx = Path.Combine(a, ".git", "index.lock");
            Git.Inspect(a);
            Check("lecture seule : aucun index.lock laissé", !File.Exists(idx));

            // --- recherche ---
            Directory.CreateDirectory(Path.Combine(tmp, "projets", "node_modules", "piege", ".git"));
            Directory.CreateDirectory(Path.Combine(a, "sous-dossier", ".git"));          // un dépôt dans un dépôt : on ne descend pas
            Directory.CreateDirectory(Path.Combine(tmp, "profond", "1", "2", "3", "4", "trop-bas", ".git"));
            Directory.CreateDirectory(Path.Combine(tmp, "profond", "1", "2", "ok", ".git"));
            var found = Git.Scan(new[] { tmp });
            string N(string p) => Path.GetFileName(p);
            Check("recherche : trouve alpha, alpha-clone, vide, detache, ok", new[] { "alpha", "alpha-clone", "vide", "detache", "ok" }.All(n => found.Any(p => N(p) == n)));
            Check("recherche : ignore node_modules, ne descend pas dans un dépôt, s'arrête à 4 niveaux", !found.Any(p => N(p) == "piege" || N(p) == "sous-dossier" || N(p) == "trop-bas"));
            Check("recherche : le bare (origine.git) n'est pas pris pour un projet (pas de dossier .git)", !found.Any(p => N(p) == "origine.git"));

            // --- adresses web ---
            Check("adresse web : https GitHub", Git.WebUrl("https://github.com/Crazyshouby/cdx-messages.git") == "https://github.com/Crazyshouby/cdx-messages");
            Check("adresse web : ssh GitHub", Git.WebUrl("git@github.com:Crazyshouby/cdx-messages.git") == "https://github.com/Crazyshouby/cdx-messages");
            Check("adresse web : un jeton glissé dans l'adresse n'en sort pas", Git.WebUrl("https://ghp_SECRET:x-oauth@github.com/a/b") == "https://github.com/a/b");
            Check("adresse web : hébergeur inconnu ou chemin local refusés", Git.WebUrl("https://exemple.org/a/b") == null && Git.WebUrl(@"C:\depot") == null && Git.WebUrl("") == null);
            Check("adresse web : caractères étranges refusés", Git.WebUrl("https://github.com/a/b c") == null && Git.WebUrl("https://github.com/a") == null);
        }
        catch (Exception ex) { lines.Add("ÉCHEC  exception : " + ex); bad++; }
        finally
        {
            // les dépôts de git ont des fichiers en lecture seule : on les rend supprimables
            try { foreach (var f in Directory.EnumerateFiles(tmp, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(tmp, true); } catch { }
            lines.Add(bad == 0 ? "RÉSULTAT : TOUT EST BON" : $"RÉSULTAT : {bad} ÉCHEC(S)");
            File.WriteAllLines(Path.Combine(Host.DataDir, "selftest-git.txt"), lines);
            Environment.ExitCode = bad == 0 ? 0 : 1;
        }
    }
}
