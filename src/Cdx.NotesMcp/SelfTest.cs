using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cdx.Coffre;

namespace Cdx.NotesMcp;

// Les essais du moteur du coffre : un dossier jetable sous %TEMP%, jamais le vrai coffre. Résultat dans selftest-notes.txt (dossier des données, ou CDX_DATA_DIR).
static class SelfTest
{
    // Le nœud au bout d'un chemin de clés et d'indices, dans une réponse du serveur. S'il manque, l'exception compte l'essai en échec (EXCEPTION), comme le faisait le déréférencement direct.
    static JsonNode At(JsonNode? n, params object[] path)
    {
        foreach (var k in path) n = k is int i ? n?[i] : n?[(string)k];
        return n ?? throw new InvalidOperationException("réponse incomplète : " + string.Join("/", path));
    }

    public static int Run()
    {
        var lines = new List<string>(); int bad = 0;
        void Check(string what, bool ok) { lines.Add((ok ? "OK     " : "ÉCHEC  ") + what); if (!ok) bad++; }
        bool Throws(Action a, string? code = null) { try { a(); return false; } catch (VaultException e) { return code == null || e.Code == code; } }
        string dir = Path.Combine(Path.GetTempPath(), "cdx-notes-test-" + Environment.ProcessId); Directory.CreateDirectory(dir);
        string W(string rel, string text) { string f = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllText(f, text, new UTF8Encoding(false)); return f; }   // « ! » : f est un fichier sous dir
        try
        {
            // ---------- analyse d'un fichier ----------
            var p = new Dictionary<string, object>();
            string fm = "---\ntype: projet\nstatut: \"en pause\"\ntags: [a, b c, 'd']\nliste:\n  - un\n  - deux\nvide:\n---\nCorps";
            int body = MarkdownParse.Frontmatter(fm, p);
            Check("en-tête : texte, texte entre guillemets, liste entre crochets, liste à tirets", (string)p["type"] == "projet" && (string)p["statut"] == "en pause" && ((List<string>)p["tags"]).SequenceEqual(new[] { "a", "b c", "d" }) && ((List<string>)p["liste"]).SequenceEqual(new[] { "un", "deux" }));
            Check("en-tête : valeur vide gardée, le corps commence après l'en-tête", (string)p["vide"] == "" && fm[body..] == "Corps");
            Check("en-tête avec fins de ligne Windows (CRLF)", MarkdownParse.Frontmatter("---\r\ntype: projet\r\n---\r\nX", p = new()) > 0 && (string)p["type"] == "projet");
            Check("pas d'en-tête, ou jamais refermé : aucune propriété", MarkdownParse.Frontmatter("# Titre\n---\nx", p = new()) == 0 && p.Count == 0 && MarkdownParse.Frontmatter("---\nclé: v\nsuite", p = new()) == 0);
            Check("apostrophe doublée et antislash conservés dans une valeur", MarkdownParse.Frontmatter("---\na: 'l''eau'\nb: 'C:\\x\\y'\n---\n", p = new()) > 0 && (string)p["a"] == "l'eau" && (string)p["b"] == @"C:\x\y");

            string md = "# Titre\n\nTexte avec [[Alpha]] et [[Beta|alias]] et [[Gamma#Section]] et ![[image]] et [[Dossier/Delta]].\n\n```\n[[Code]] #pasune #tag-code\n- [ ] pas une tâche\n```\n\nEn ligne `[[Inline]] #nope` puis #vrai et #a/b/c, #123 et http://x.org/#ancre.\n\n- [ ] ouverte 📅 2026-11-05\n- [x] faite\n  - [ ] imbriquée due:2026-12-24\n\n## Sous-titre ##\n";
            var masked = MarkdownParse.Mask(md, 0);
            var links = MarkdownParse.Links(md, masked);
            Check("liens : Alpha, Beta (alias), Gamma (section), image (incrustation), Dossier/Delta ; rien dans le code", links.Select(l => l.Target).SequenceEqual(new[] { "Alpha", "Beta", "Gamma", "image", "Dossier/Delta" }));
            Check("liens : alias, section, incrustation reconnus", links[1].Alias == "alias" && links[2].Heading == "Section" && links[3].Embed && !links[0].Embed);
            Check("liens : numéro de ligne (3) et position dans le texte", links[0].Line == 3 && md.Substring(links[0].Start, links[0].Length) == "[[Alpha]]");
            var tags = MarkdownParse.Tags(masked).ToList();
            Check("étiquettes : #vrai et #a/b/c ; pas celles du code, ni #123, ni l'ancre d'une adresse, ni le # d'un lien", tags.SequenceEqual(new[] { "vrai", "a/b/c" }));
            var tasks = MarkdownParse.Tasks("n.md", masked, md);
            Check("tâches : ouverte (avec échéance), faite, imbriquée ; rien dans le code", tasks.Count == 3 && !tasks[0].Done && tasks[0].Due == "2026-11-05" && tasks[1].Done && tasks[2].Due == "2026-12-24" && tasks[0].Line == 12);
            var heads = MarkdownParse.Headings(masked);
            Check("titres : niveaux et texte, dièses de fin retirés", heads.Count == 2 && heads[0].Level == 1 && heads[1].Level == 2 && heads[1].Text == "Sous-titre");

            // ---------- un coffre ----------
            W("Alpha.md", "---\ntags: [exemple]\n---\n# Alpha\nVoir [[Beta]] et [[Introuvable]].\n- [ ] faire alpha 📅 2026-11-30\n");
            W("Beta.md", "# Beta\nRetour vers [[Alpha]] et [[Projets/Zeta|le projet]].\n#idée\n");
            W("Projets/Zeta.md", "---\ntype: projet\nstatut: actif\ndepot: 'C:\\Dev\\zeta'\npriorite: haute\n---\n# Zeta\n- [ ] ouvrir\n- [x] clore 📅 2020-01-01\n- [ ] lier [[Alpha]] 📅 2026-11-10\n");
            W("Projets/Eta.md", "---\ntype: Projet\nstatut: en pause\n---\n# Eta\n[[Zeta]]\n");
            W("Archives/Beta.md", "# Beta archivé\n");
            W(".obsidian/config.md", "# caché\n");
            W("Accentué.md", "# Réseau\nLe café est prêt : élément de la liste.\n");
            var v = new Vault(dir);
            NoteInfo Note(string path) => v.Get(path) ?? throw new InvalidOperationException("note absente de l'index : " + path);   // absente : l'essai échoue (EXCEPTION), comme avec le déréférencement d'avant
            Check("index : 6 notes (le dossier caché est ignoré)", v.Count == 6 && v.Get(".obsidian/config.md") == null);
            Check("lien par nom : [[Beta]] désigne la note la moins profonde (Beta.md, pas Archives/Beta.md)", Note("Alpha").Links[0].Resolved == "Beta.md");
            Check("lien par chemin : [[Projets/Zeta|le projet]] ; lien sans note : Introuvable", Note("Beta").Links.Any(l => l.Resolved == "Projets/Zeta.md") && Note("Alpha").Links.Any(l => l.Target == "Introuvable" && l.Resolved == null));
            Check("résolution : casse et extension ignorées, chemin partiel accepté", v.Resolve("alpha") == "Alpha.md" && v.Resolve("ALPHA.md") == "Alpha.md" && v.Resolve("Zeta") == "Projets/Zeta.md" && v.Resolve("Projets/zeta") == "Projets/Zeta.md" && v.Resolve("rien") == null);
            var bl = v.Backlinks("Alpha");
            Check("liens entrants d'Alpha : Beta et Zeta, avec la ligne qui contient le lien", bl.Select(b => b.Path).SequenceEqual(new[] { "Beta.md", "Projets/Zeta.md" }) && bl[0].Context.Contains("[[Alpha]]"));
            Check("liens sans cible : « Introuvable » (une note à écrire)", v.Unresolved().Any(u => u.Target == "Introuvable"));
            var tg = v.Tags();
            Check("étiquettes du coffre : exemple (en-tête) et idée (corps)", tg.ContainsKey("exemple") && tg.ContainsKey("idée") && tg["exemple"] == 1);

            // ---------- recherche ----------
            Check("recherche : titre avant contenu (« beta » : Beta.md en tête)", v.Search("beta").FirstOrDefault()?.Path == "Beta.md");
            Check("recherche : accents et casse ignorés (« reseau cafe » trouve Accentué)", v.Search("RESEAU cafe").Any(h => h.Path == "Accentué.md"));
            Check("recherche : tous les mots sont requis", v.Search("alpha zeta").All(h => h.Path != "Accentué.md") && v.Search("alpha introuvable").Count == 1);
            Check("recherche : phrase entre guillemets", v.Search("\"élément de la liste\"").Count == 1 && v.Search("\"liste de élément\"").Count == 0);
            Check("recherche : filtres tag:, in:, type: (sans mot)", v.Search("tag:exemple").Single().Path == "Alpha.md" && v.Search("in:Projets").Count == 2 && v.Search("type:projet").Count == 2);
            Check("recherche : un extrait autour du mot, et sa ligne", v.Search("café").Single() is { } h && h.Snippet.Contains("café") && h.Line == 2);
            Check("recherche : limite respectée", v.Search("e", 2).Count <= 2 && v.Search("").Count >= 1);

            // ---------- chemins refusés ----------
            foreach (var bad1 in new[] { "../dehors", "a/../../b", "C:/x", "C:\\x\\y", "//serveur/p", ".git/x", "dossier/.cache/x", "a//b", "", "   ", "CON", "dossier/aux.md", "x<y", new string('a', 130), string.Join("/", Enumerable.Repeat("abcdefghij", 26)) })
                Check($"chemin refusé : « {(bad1.Length > 30 ? bad1[..12] + "…" : bad1)} »", Throws(() => v.Create(bad1, "x"), "chemin"));
            Check("chemin avec antislash normalisé, .md ajouté", Vault.NormalizePath("Dossier\\Sous\\Note") == "Dossier/Sous/Note.md" && Vault.NormalizePath("/Note.MD") == "Note.MD");

            // ---------- écriture ----------
            var c = v.Create("Nouveau/Dossier/Créée", "# Créée\ntexte");
            Check("création : le dossier manquant est créé, .md ajouté, la note est indexée", c.Path == "Nouveau/Dossier/Créée.md" && File.Exists(Path.Combine(dir, "Nouveau", "Dossier", "Créée.md")));
            Check("création : refus si la note existe (rien n'est écrasé)", Throws(() => v.Create("Nouveau/Dossier/Créée.md", "autre"), "existe") && v.Read("Nouveau/Dossier/Créée", out _) == "# Créée\ntexte");
            string t0 = v.Read("Nouveau/Dossier/Créée", out string h0);
            Check("remplacement : l'empreinte est exigée", Throws(() => v.Overwrite("Nouveau/Dossier/Créée", "x", null), "empreinte"));
            Check("remplacement : une empreinte périmée est refusée (conflit)", Throws(() => v.Overwrite("Nouveau/Dossier/Créée", "x", "0000000000000000"), "conflit"));
            v.Overwrite("Nouveau/Dossier/Créée", "# Créée\nremplacé\n", h0);
            Check("remplacement : avec la bonne empreinte, c'est écrit (sans fichier temporaire)", v.Read("Nouveau/Dossier/Créée", out _) == "# Créée\nremplacé\n" && !Directory.GetFiles(dir, "*.cdx-tmp", SearchOption.AllDirectories).Any());
            Check("remplacement : une note qui n'existe pas est refusée", Throws(() => v.Overwrite("Fantôme", "x", "abc"), "introuvable"));
            v.Append("Nouveau/Dossier/Créée", "ajout");
            Check("ajout en fin de note : saut de ligne inséré si besoin", v.Read("Nouveau/Dossier/Créée", out _) == "# Créée\nremplacé\najout");
            Check("remplacement de texte : unique, introuvable, ambigu", v.ReplaceText("Nouveau/Dossier/Créée", "remplacé", "changé") != null && v.Read("Nouveau/Dossier/Créée", out _).Contains("changé") && Throws(() => v.ReplaceText("Nouveau/Dossier/Créée", "absent", "x"), "introuvable") && Throws(() => { v.Append("Nouveau/Dossier/Créée", "ajout"); v.ReplaceText("Nouveau/Dossier/Créée", "ajout", "x"); }, "ambigu"));
            string big = new string('x', Vault.MaxNoteBytes + 10);
            Check("taille : une note de plus de 2 Mo est refusée", Throws(() => v.Create("Énorme", big), "trop-grande"));

            // ---------- propriétés ----------
            v.SetProperty("Beta", "statut", "à relire");
            string bt = v.Read("Beta", out _);
            Check("propriété sur une note sans en-tête : l'en-tête est créé, le corps intact", bt.StartsWith("---\nstatut: à relire\n---\n# Beta\n") && Note("Beta").Prop("statut") == "à relire");
            v.SetProperty("Beta", "statut", "fait"); v.SetProperty("Beta", "tags", new[] { "x", "y z" });
            Check("propriété déjà là : remplacée, pas dupliquée ; liste écrite entre crochets", v.Read("Beta", out _).Split("statut:").Length == 2 && Note("Beta").Prop("statut") == "fait" && Note("Beta").Tags.Contains("x") && Note("Beta").Tags.Contains("y z"));
            v.SetProperty("Projets/Zeta", "depot", @"C:\Dev\autre");
            Check("propriété : un chemin Windows est mis entre apostrophes (lisible par Obsidian), le reste de l'en-tête est intact", v.Read("Projets/Zeta", out _).Contains("depot: 'C:\\Dev\\autre'") && Note("Projets/Zeta").Prop("priorite") == "haute" && Note("Projets/Zeta").Prop("depot") == @"C:\Dev\autre");
            W("Crlf.md", "---\r\ntype: x\r\n---\r\nCorps\r\n"); v.Refresh(); v.SetProperty("Crlf", "a", "b");
            Check("propriété : les fins de ligne Windows sont gardées", v.Read("Crlf", out _) == "---\r\ntype: x\r\na: b\r\n---\r\nCorps\r\n");
            Check("propriété : nom invalide refusé", Throws(() => v.SetProperty("Crlf", "a:b\nc", "x"), "propriété"));

            // ---------- déplacement et liens ----------
            int n = v.Move("Alpha", "Renommée");
            Check("renommer : les liens [[Alpha]] des autres notes sont réécrits (Beta et Zeta)", n == 2 && v.Read("Beta", out _).Contains("[[Renommée]]") && v.Read("Projets/Zeta", out _).Contains("[[Renommée]]") && !v.Read("Beta", out _).Contains("[[Alpha]]"));
            Check("renommer : plus aucun lien sans cible vers Alpha, et l'ancien fichier a disparu", v.Backlinks("Renommée").Count == 2 && !File.Exists(Path.Combine(dir, "Alpha.md")) && v.Get("Alpha") == null);
            v.Move("Renommée", "Rangées/Plus loin/Renommée");
            Check("déplacer dans un autre dossier : le lien par nom reste valable (nom inchangé) et la note est retrouvée", v.Backlinks("Rangées/Plus loin/Renommée").Count == 2 && v.Read("Beta", out _).Contains("[[Renommée]]"));
            v.Move("Beta", "Archives/Beta bis");
            W("Alias.md", "Voir [[Archives/Beta bis|ce texte]] et [[Beta ter|rien]]\n"); v.Refresh();
            v.Move("Archives/Beta bis", "Archives/Beta quater");
            Check("renommer : un lien par chemin et son alias sont réécrits ensemble", v.Read("Alias", out _).Contains("[[Archives/Beta quater|ce texte]]") && v.Read("Alias", out _).Contains("[[Beta ter|rien]]"));
            Check("déplacer : destination existante refusée, source absente refusée", Throws(() => v.Move("Alias", "Accentué"), "existe") && Throws(() => v.Move("Fantôme", "Autre"), "introuvable"));
            v.Move("Accentué", "accentué"); Check("renommer seulement la casse", v.Get("accentué") != null && File.Exists(Path.Combine(dir, "accentué.md")));
            W("Dup/Note.md", "x\n"); W("Dup2/Note.md", "y\n"); W("Lien.md", "[[Note]]\n"); v.Refresh();
            v.Move("Dup/Note", "Dup/Note renommée");
            Check("deux notes de même nom : le lien [[Note]] visait Dup/Note (la moins profonde), il suit la note renommée ; l'autre n'est pas touchée", v.Read("Lien", out _) == "[[Note renommée]]\n");

            // ---------- corbeille ----------
            string tr = v.Trash("Nouveau/Dossier/Créée");
            Check("retirer : la note va dans .trash (avec son dossier), n'est plus indexée, n'est pas supprimée", tr == ".trash/Nouveau/Dossier/Créée.md" && File.Exists(Path.Combine(dir, ".trash", "Nouveau", "Dossier", "Créée.md")) && v.Get("Nouveau/Dossier/Créée") == null);
            v.Create("Nouveau/Dossier/Créée", "autre"); string tr2 = v.Trash("Nouveau/Dossier/Créée");
            Check("retirer deux fois la même note : la seconde garde un nom daté, la première est intacte", tr2 != tr && File.Exists(Path.Combine(dir, ".trash", "Nouveau", "Dossier", "Créée.md")) && File.ReadAllText(Path.Combine(dir, ".trash", "Nouveau", "Dossier", "Créée.md")).StartsWith("# Créée"));
            Check("la corbeille n'est pas accessible comme une note", Throws(() => v.Read(".trash/Nouveau/Dossier/Créée.md", out _), "chemin"));

            // ---------- images, dossiers ----------
            void Bin(string rel) { string f = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllBytes(f, new byte[] { 1, 2, 3 }); }   // « ! » : f est un fichier sous dir
            Bin("Images/photo.png"); Bin("Images/sous/Autre.JPG"); Bin(".obsidian/cache.png"); Bin("Images/lisez-moi.txt"); Bin("Images/note.md.png");
            var att = v.Attachments();
            Check("images : png, jpg (majuscules) et png à double extension ; pas le dossier caché, pas le .txt", att.SequenceEqual(new[] { "Images/note.md.png", "Images/photo.png", "Images/sous/Autre.JPG" }));
            Check("images : ImagePath rend le fichier d'une image du coffre", v.ImagePath("Images/photo.png") == Path.Combine(dir, "Images", "photo.png") && v.ImagePath("images/SOUS/autre.jpg") != null);
            Check("images : refus d'un chemin hors du coffre, caché, sans image, absent, ou d'un fichier qui n'est pas une image",
                  v.ImagePath("../x.png") == null && v.ImagePath(".obsidian/cache.png") == null && v.ImagePath("Images/lisez-moi.txt") == null && v.ImagePath("Images/absente.png") == null && v.ImagePath("Alpha.md") == null && v.ImagePath("C:/Windows/x.png") == null && v.ImagePath(null) == null);
            W("Dossier A/N1.md", "# N1\nVers [[Plus loin]] et [[Dossier A/Sous/N2]].\n"); W("Dossier A/Sous/N2.md", "# N2\n"); Bin("Dossier A/pj.png"); Directory.CreateDirectory(Path.Combine(dir, "Dossier A", "Vide"));
            W("Pointe.md", "Lien par chemin : [[Dossier A/N1]] et par nom : [[N2]].\n"); W("Plus loin.md", "x\n"); v.Refresh();
            var fmv = v.MoveFolder("Dossier A", "Archives/Dossier A");
            Check("déplacer un dossier : les notes, l'image et le sous-dossier vide suivent ; l'ancien dossier a disparu", File.Exists(Path.Combine(dir, "Archives", "Dossier A", "N1.md")) && File.Exists(Path.Combine(dir, "Archives", "Dossier A", "Sous", "N2.md")) && File.Exists(Path.Combine(dir, "Archives", "Dossier A", "pj.png")) && Directory.Exists(Path.Combine(dir, "Archives", "Dossier A", "Vide")) && !Directory.Exists(Path.Combine(dir, "Dossier A")));
            Check("déplacer un dossier : la correspondance ancien -> nouveau chemin de chaque note", fmv.Moves.Count == 2 && fmv.Moves["Dossier A/N1.md"] == "Archives/Dossier A/N1.md" && fmv.Moves["Dossier A/Sous/N2.md"] == "Archives/Dossier A/Sous/N2.md");
            Check("déplacer un dossier : les liens par chemin sont réécrits, les liens par nom restent justes", v.Read("Pointe", out _) == "Lien par chemin : [[Archives/Dossier A/N1]] et par nom : [[N2]].\n" && v.Backlinks("Archives/Dossier A/N1").Count == 1 && v.Backlinks("Archives/Dossier A/Sous/N2").Count >= 2 && Note("Archives/Dossier A/N1").Links.All(l => l.Resolved != null) && fmv.Updated >= 1);
            Check("déplacer un dossier : refus dans lui-même, vers une destination non vide, source absente, même nom", Throws(() => v.MoveFolder("Archives", "Archives/Dedans"), "chemin") && Throws(() => v.MoveFolder("Archives", "Images"), "existe") && Throws(() => v.MoveFolder("Fantôme", "X"), "introuvable") && Throws(() => v.MoveFolder("Archives", "archives"), "chemin") && Throws(() => v.MoveFolder("../x", "Y"), "chemin"));
            var tgone = v.TrashFolder("Archives/Dossier A");
            Check("retirer un dossier : ses notes vont dans .trash, l'image reste (donc le dossier aussi)", tgone.Count == 2 && File.Exists(Path.Combine(dir, ".trash", "Archives", "Dossier A", "N1.md")) && File.Exists(Path.Combine(dir, "Archives", "Dossier A", "pj.png")) && v.Get("Archives/Dossier A/N1") == null);
            Directory.CreateDirectory(Path.Combine(dir, "Jetable", "Vide")); W("Jetable/Seule.md", "x\n"); v.Refresh();
            v.TrashFolder("Jetable");
            Check("retirer un dossier qui ne contient que des notes : il disparaît, les notes sont récupérables", !Directory.Exists(Path.Combine(dir, "Jetable")) && File.Exists(Path.Combine(dir, ".trash", "Jetable", "Seule.md")));

            // ---------- projets et tâches ----------
            var pr = v.Projects();
            Check("projets : Zeta (actif) avant Eta (en pause) ; « Projet » avec majuscule reconnu", pr.Select(x => x.Name).SequenceEqual(new[] { "Zeta", "Eta" }));
            var z = pr[0];
            Check("projet : dépôt, priorité, tâches ouvertes (2) et faites (1), prochaine échéance", z.Repo == @"C:\Dev\autre" && z.Priority == "haute" && z.OpenTasks == 2 && z.DoneTasks == 1 && z.NextDue == "2026-11-10");
            Check("projets : filtre par statut", v.Projects("actif").Count == 1 && v.Projects("en pause").Count == 1 && v.Projects("terminé").Count == 0);
            var tk = v.Tasks();
            Check("tâches ouvertes de tout le coffre, triées par échéance (celles sans échéance à la fin)", tk.Count >= 3 && tk[0].Due == "2026-11-10" && tk.All(t => !t.Done) && tk[^1].Due == null);
            Check("tâches : filtre par dossier, et avec les faites", v.Tasks("Projets").All(t => t.Path.StartsWith("Projets/")) && v.Tasks("Projets", true).Count == v.Tasks("Projets").Count + 1);
            var cp = v.CreateProject("Mon projet: test?", @"C:\Dev\mon", "Un résumé.");
            Check("créer un projet : fiche Projets/…, nom nettoyé, propriétés du modèle", cp.Path == "Projets/Mon projet  test.md" && cp.Prop("type") == "projet" && cp.Prop("statut") == "actif" && cp.Prop("depot") == @"C:\Dev\mon" && v.Projects().Any(x => x.Name == "Mon projet  test"));
            Check("créer un projet : nom vide refusé, doublon refusé", Throws(() => v.CreateProject("  ", null, null), "nom") && Throws(() => v.CreateProject("Zeta"), "existe"));

            // ---------- graphe, changements extérieurs ----------
            var g = v.Graph();
            Check("graphe : un nœud par note, des arêtes seulement entre notes qui existent, sans doublon ni boucle", g.Nodes.Count == v.Count && g.Edges.Count > 0 && g.Edges.Count == g.Edges.Select(e => e.ToString()).Distinct().Count());
            File.AppendAllText(Path.Combine(dir, "Projets", "Eta.md"), "- [ ] ajoutée à la main\n");
            W("Venue de dehors.md", "# dehors\n[[Eta]]\n"); File.Delete(Path.Combine(dir, "Crlf.md"));
            bool ch = v.Refresh();
            Check("changements faits hors du programme : relus (note modifiée, ajoutée, supprimée)", ch && Note("Projets/Eta").Tasks.Count == 1 && v.Get("Venue de dehors") != null && v.Get("Crlf") == null && v.Backlinks("Projets/Eta").Any(b => b.Path == "Venue de dehors.md"));
            Check("relecture sans changement : rien à signaler", !v.Refresh());

            // ---------- le serveur MCP, en mémoire ----------
            var srv = new Server(() => v);
            JsonNode? Rpc(string json) { var r = srv.Handle(json); return r == null ? null : JsonNode.Parse(r); }
            string Call(string tool, string args, out bool isErr, string id = "1")
            {
                var r = Rpc($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"tools/call\",\"params\":{{\"name\":\"{tool}\",\"arguments\":{args}}}}}");
                var res = At(r, "result"); isErr = At(res, "isError").GetValue<bool>(); return At(res, "content", 0, "text").GetValue<string>();
            }
            var init = Rpc("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"essai\",\"version\":\"0\"}}}");
            Check("initialize : la version demandée connue est reprise, outils annoncés, nom du serveur", At(init, "result", "protocolVersion").GetValue<string>() == "2024-11-05" && At(init, "result", "capabilities")["tools"] != null && At(init, "result", "serverInfo", "name").GetValue<string>() == "cdx-notes" && At(init, "result", "instructions").GetValue<string>().Contains("notes_trash"));
            Check("initialize : une version inconnue reçoit la plus récente du serveur", At(Rpc("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"1999-01-01\"}}"), "result", "protocolVersion").GetValue<string>() == Server.Protocols[0]);
            Check("notification (sans id) : aucune réponse", Rpc("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}") == null && Rpc("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{}}") == null);
            Check("ping : réponse vide, l'identifiant (texte ou nombre) est rendu tel quel", At(Rpc("{\"jsonrpc\":\"2.0\",\"id\":\"abc\",\"method\":\"ping\"}"), "id").GetValue<string>() == "abc" && At(Rpc("{\"jsonrpc\":\"2.0\",\"id\":42,\"method\":\"ping\"}"), "id").GetValue<int>() == 42);
            var tl = At(Rpc("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/list\"}"), "result", "tools").AsArray();
            var names = tl.Select(t => At(t, "name").GetValue<string>()).ToList();
            Check($"tools/list : {names.Count} outils, noms uniques, chacun avec un schéma d'entrée objet", names.Count == 17 && names.Distinct().Count() == 17 && tl.All(t => At(t, "inputSchema", "type").GetValue<string>() == "object" && At(t, "description").GetValue<string>().Length > 20));
            Check("tools/list : lecture seule déclarée pour la lecture, pas pour l'écriture ; aucun outil destructif", At(tl.Single(t => At(t, "name").GetValue<string>() == "notes_read"), "annotations", "readOnlyHint").GetValue<bool>() && !At(tl.Single(t => At(t, "name").GetValue<string>() == "notes_create"), "annotations", "readOnlyHint").GetValue<bool>() && tl.All(t => !At(t, "annotations", "destructiveHint").GetValue<bool>()));
            Check("aucun outil de suppression définitive (seul notes_trash retire)", !names.Any(x => x.Contains("delete") || x.Contains("remove") || x.Contains("rm")) && names.Contains("notes_trash"));
            Check("méthode inconnue : erreur -32601 ; JSON illisible : -32700 (id nul) ; tableau : -32600 ; sans méthode : -32600",
                  At(Rpc("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"nimporte/quoi\"}"), "error", "code").GetValue<int>() == -32601 && At(Rpc("{pas du json"), "error", "code").GetValue<int>() == -32700 && At(Rpc("{pas du json"))["id"] == null
                  && At(Rpc("[1,2]"), "error", "code").GetValue<int>() == -32600 && At(Rpc("{\"jsonrpc\":\"2.0\",\"id\":6}"), "error", "code").GetValue<int>() == -32600);

            string info = Call("vault_info", "{}", out bool e1);
            Check("vault_info : nombre de notes, dossiers, conventions d'écriture", !e1 && info.Contains("notes,") && info.Contains("Conventions") && info.Contains("notes_trash"));
            string sr = Call("notes_search", "{\"query\":\"zeta\"}", out bool e2);
            Check("notes_search : trouve la fiche Zeta, avec un extrait", !e2 && sr.Contains("Projets/Zeta.md") && sr.Contains("ligne"));
            string rd = Call("notes_read", "{\"path\":\"Projets/Zeta\"}", out bool e3);
            Check("notes_read : empreinte à 16 caractères, propriétés, liens, contenu après la ligne de séparation", !e3 && System.Text.RegularExpressions.Regex.IsMatch(rd, @"Empreinte \(hash\) : [0-9a-f]{16}") && rd.Contains("Propriété statut : actif") && rd.Contains("Liens :") && rd.Contains("------\n---\ntype: projet"));
            string hz = System.Text.RegularExpressions.Regex.Match(rd, @"hash\) : ([0-9a-f]{16})").Groups[1].Value;
            string cr = Call("notes_create", "{\"path\":\"Idées/Piste\",\"content\":\"# Piste\\nUne idée sur [[Zeta]].\"}", out bool e4);
            Check("notes_create : la note est créée et rend sa nouvelle empreinte", !e4 && cr.Contains("Idées/Piste.md") && cr.Contains("empreinte") && File.Exists(Path.Combine(dir, "Idées", "Piste.md")));
            Call("notes_create", "{\"path\":\"Idées/Piste\",\"content\":\"autre\"}", out bool e5);
            Check("notes_create : refus si la note existe", e5);
            string h1 = System.Text.RegularExpressions.Regex.Match(cr, @"empreinte ([0-9a-f]{16})").Groups[1].Value;
            Call("notes_write", "{\"path\":\"Idées/Piste\",\"content\":\"x\"}", out bool e6);
            Check("notes_write sans empreinte : refus (argument manquant)", e6);
            string wr = Call("notes_write", "{\"path\":\"Idées/Piste\",\"content\":\"x\",\"hash\":\"0000000000000000\"}", out bool e7);
            Check("notes_write avec une empreinte périmée : refus (conflit), la note n'a pas changé", e7 && wr.Contains("a changé") && File.ReadAllText(Path.Combine(dir, "Idées", "Piste.md")).StartsWith("# Piste"));
            string ed = Call("notes_edit", "{\"path\":\"Idées/Piste\",\"old_text\":\"Une idée\",\"new_text\":\"Une bonne idée\",\"hash\":\"" + h1 + "\"}", out bool e8);
            Check("notes_edit : avec la bonne empreinte, le passage est remplacé", !e8 && File.ReadAllText(Path.Combine(dir, "Idées", "Piste.md")).Contains("Une bonne idée"));
            Call("notes_edit", "{\"path\":\"Idées/Piste\",\"old_text\":\"idée\",\"new_text\":\"x\",\"hash\":\"" + h1 + "\"}", out bool e9);
            Check("notes_edit : l'empreinte d'avant la modification est maintenant périmée (refus)", e9);
            string ap = Call("notes_append", "{\"path\":\"Idées/Piste\",\"text\":\"- [ ] relire 📅 2026-11-20\"}", out bool e10);
            string sp = Call("notes_set_property", "{\"path\":\"Idées/Piste\",\"key\":\"tags\",\"value\":[\"idée\",\"test\"]}", out bool e11);
            Check("notes_append et notes_set_property (liste) : écrits", !e10 && !e11 && Note("Idées/Piste").Tasks.Count == 1 && Note("Idées/Piste").Tags.Contains("test"));
            string mv = Call("notes_move", "{\"from\":\"Idées/Piste\",\"to\":\"Projets/Piste retenue\"}", out bool e12);
            Check("notes_move : déplacée, les liens entrants réécrits (rien ici, mais aucun lien cassé)", !e12 && mv.Contains("Projets/Piste retenue.md") && v.Get("Idées/Piste") == null && v.Get("Projets/Piste retenue") != null);
            string lk = Call("notes_links", "{\"path\":\"Projets/Zeta\"}", out bool e13);
            Check("notes_links : sortants et entrants (la nouvelle note pointe vers Zeta)", !e13 && lk.Contains("Liens sortants") && lk.Contains("Projets/Piste retenue.md"));
            string unl = Call("notes_links", "{}", out bool e14);
            Check("notes_links sans chemin : les liens sans cible du coffre", !e14 && unl.Contains("Introuvable") && unl.Contains("sans note"));
            string tg2 = Call("notes_tags", "{\"tag\":\"test\"}", out bool e15);
            Check("notes_tags : les notes d'une étiquette", !e15 && tg2.Contains("Projets/Piste retenue.md"));
            string pl = Call("projects_list", "{\"status\":\"actif\"}", out bool e16);
            Check("projects_list : fiches actives, tâches ouvertes, dépôt", !e16 && pl.Contains("Zeta") && pl.Contains("priorité haute") && pl.Contains("tâche(s) ouverte(s)") && !pl.Contains("Eta ["));
            string pc = Call("projects_create", "{\"name\":\"Refonte du site\",\"repo\":\"C:\\\\Dev\\\\site\",\"summary\":\"Tout reprendre.\"}", out bool e17);
            Check("projects_create : fiche créée dans Projets/, visible dans projects_list", !e17 && File.Exists(Path.Combine(dir, "Projets", "Refonte du site.md")) && Call("projects_list", "{}", out _).Contains("Refonte du site"));
            string tkl = Call("tasks_list", "{\"under\":\"Projets\"}", out bool e18);
            Check("tasks_list : tâches du dossier, avec le fichier et la ligne", !e18 && tkl.Contains("(Projets/Zeta.md:") && tkl.Contains("relire"));
            string lst = Call("notes_list", "{\"folder\":\"Projets\"}", out bool e19);
            Check("notes_list : notes d'un dossier avec type et statut", !e19 && lst.Contains("Projets/Zeta.md  [projet, actif]"));
            string tr3 = Call("notes_trash", "{\"path\":\"Projets/Piste retenue\"}", out bool e20);
            Check("notes_trash : déplacée dans .trash, pas supprimée", !e20 && tr3.Contains(".trash/Projets/Piste retenue.md") && File.Exists(Path.Combine(dir, ".trash", "Projets", "Piste retenue.md")));
            Call("folders_create", "{\"path\":\"Archives/2026\"}", out bool e21);
            Check("folders_create : dossier créé", !e21 && Directory.Exists(Path.Combine(dir, "Archives", "2026")));

            // erreurs : jamais d'exception, jamais de chemin du disque dans le message, rien d'écrit hors du coffre
            string parent = Path.GetDirectoryName(dir)!;   // « ! » : dir est un dossier sous %TEMP%
            int before = Directory.GetFiles(parent, "dehors*").Length;
            foreach (var bad2 in new[] { "../dehors", "..\\dehors", "C:/Windows/dehors", "a/../../dehors", ".git/x", "Projets/../../dehors" })
            {
                string m = Call("notes_create", "{\"path\":" + JsonSerializer.Serialize(bad2) + ",\"content\":\"x\"}", out bool eb);
                Check($"outil notes_create, chemin « {bad2} » : refusé, message sans chemin du disque", eb && !m.Contains(dir) && !m.Contains(Path.GetTempPath()));
            }
            Check("rien n'a été écrit hors du coffre", Directory.GetFiles(parent, "dehors*").Length == before && !File.Exists(Path.Combine(parent, "dehors.md")));
            Check("notes_read hors du coffre ou sur un dossier caché : refusé", Call("notes_read", "{\"path\":\"../x\"}", out bool er1) is not null && er1 && Call("notes_read", "{\"path\":\".trash/Projets/Piste retenue.md\"}", out bool er2) is not null && er2);
            Call("nimporte_quel_outil", "{}", out bool eu); Check("outil inconnu : erreur d'outil (isError), pas d'exception", eu);
            string ba = Call("notes_search", "{\"query\":\"x\",\"limit\":\"beaucoup\"}", out bool eba); Check("argument de mauvais type : message clair", eba && ba.Contains("entier"));
            string mi = Call("notes_read", "{}", out bool emi); Check("argument manquant : message clair", emi && mi.Contains("manquant"));
            Check("argument inconnu ou nul ignoré sans planter", !Call("vault_info", "{\"x\":null}", out bool enul).Contains("Erreur interne") && !enul);
            string q2 = Call("notes_search", "{\"query\":\"" + new string('a', 600) + "\"}", out bool eq2); Check("requête trop longue : refusée", eq2 && q2.Contains("trop longue"));

            // ---------- le vrai programme, sur l'entrée et la sortie standard ----------
            string dir2 = Path.Combine(Path.GetTempPath(), "cdx-notes-stdio-" + Environment.ProcessId);
            var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("chemin du programme inconnu")) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
            psi.Environment["CDX_NOTES_DIR"] = dir2; psi.Environment["CDX_DATA_DIR"] = Path.Combine(dir2, "données");
            using (var proc = System.Diagnostics.Process.Start(psi)!)   // « ! » : sans UseShellExecute, Start lance le processus ou lève, il ne rend jamais null
            {
                proc.StandardInput.NewLine = "\n";
                proc.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}");
                proc.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
                proc.StandardInput.WriteLine("");
                proc.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
                proc.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"projects_list\",\"arguments\":{}}}");
                proc.StandardInput.Flush();
                var outs = new List<string>(); var started = DateTime.UtcNow;
                while (outs.Count < 3 && (DateTime.UtcNow - started).TotalSeconds < 20) { var rl = proc.StandardOutput.ReadLineAsync(); if (rl.Wait(5000)) { if (rl.Result == null) break; outs.Add(rl.Result); } }
                proc.StandardInput.Close();
                bool exited = proc.WaitForExit(8000);
                var parsed = outs.Select(o => { try { return JsonNode.Parse(o); } catch { return null; } }).ToList();
                Check("programme réel (stdio) : trois réponses, ni plus ni moins (la notification et la ligne vide n'en ont pas), toutes du JSON valide", outs.Count == 3 && parsed.All(x => x != null && At(x, "jsonrpc").GetValue<string>() == "2.0"));
                Check("programme réel : identifiants 1, 2, 3 dans l'ordre ; version 2025-06-18 ; 17 outils", parsed.Count == 3 && At(parsed[0], "id").GetValue<int>() == 1 && At(parsed[1], "id").GetValue<int>() == 2 && At(parsed[2], "id").GetValue<int>() == 3 && At(parsed[0], "result", "protocolVersion").GetValue<string>() == "2025-06-18" && At(parsed[1], "result", "tools").AsArray().Count == 17);
                Check("programme réel : un coffre neuf est créé avec ses notes d'exemple (un projet)", parsed.Count == 3 && At(parsed[2], "result", "content", 0, "text").GetValue<string>().Contains("Exemple de projet") && Directory.Exists(dir2));
                Check("programme réel : s'arrête proprement à la fermeture de l'entrée", exited && proc.ExitCode == 0);
            }
            try { Directory.Delete(dir2, true); } catch { }

            // ---------- configuration ----------
            string cdir = Path.Combine(Path.GetTempPath(), "cdx-notes-cfg-" + Environment.ProcessId);
            Environment.SetEnvironmentVariable("CDX_NOTES_DIR", cdir);
            var seeded = VaultConfig.Open();
            Check("configuration : CDX_NOTES_DIR désigne le coffre, un coffre neuf reçoit des notes d'exemple (accueil, projet, modèle)", VaultConfig.Locate() == Path.GetFullPath(cdir) && seeded.Get("Accueil") != null && seeded.Projects().Count == 1 && seeded.Get("Modèles/Projet") != null);
            Check("les notes d'exemple ont de vrais liens et une tâche à échéance", seeded.Backlinks("Accueil").Count >= 1 && seeded.Tasks().Any(t => t.Due == "2026-12-01"));
            Check("un dossier non vide n'est jamais « ensemencé »", Directory.GetFiles(dir, "Accueil.md").Length == 0 && new Vault(dir).Get("Accueil") == null);
            Environment.SetEnvironmentVariable("CDX_NOTES_DIR", Path.Combine(Path.GetTempPath(), "cdx-notes-absent-" + Environment.ProcessId));
            Check("coffre absent sans création demandée : message clair", Throws(() => VaultConfig.Open(false), "introuvable"));
            Environment.SetEnvironmentVariable("CDX_NOTES_DIR", null);
            try { Directory.Delete(cdir, true); } catch { }
        }
        catch (Exception ex) { lines.Add("EXCEPTION " + ex); bad++; }
        try { Directory.Delete(dir, true); } catch { }
        lines.Add(bad == 0 ? "TOUT EST BON" : $"{bad} vérification(s) en échec");
        string data = Environment.GetEnvironmentVariable("CDX_DATA_DIR") is { Length: > 0 } d ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CdxShell");
        Directory.CreateDirectory(data); File.WriteAllLines(Path.Combine(data, "selftest-notes.txt"), lines);
        foreach (var l in lines.Where(l => !l.StartsWith("OK"))) Console.Error.WriteLine(l);
        return bad == 0 ? 0 : 1;
    }
}




