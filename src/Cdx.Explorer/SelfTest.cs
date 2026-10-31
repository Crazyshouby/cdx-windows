using System.Drawing;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Cdx;

// cdx-shell.exe --selftest-fs : exerce le côté fichiers de l'Explorateur (liste, nouveau dossier, renommer, copier-coller, couper-coller,
// suppression à la corbeille) dans un dossier temporaire, sans fenêtre ni entrée simulée. Le résultat est écrit dans
// %LOCALAPPDATA%\CdxShell\selftest.txt (une ligne par vérification : OK ou ÉCHEC).
static class SelfTest
{
    [STAThread]
    public static void Run()
    {
        var lines = new List<string>(); int bad = 0;
        string dir = Path.Combine(Path.GetTempPath(), "cdx-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var replies = new List<JsonElement>();
        void Send(object o) { using var d = JsonDocument.Parse(JsonSerializer.Serialize(o)); replies.Add(d.RootElement.Clone()); }
        void Do(string json) { replies.Clear(); using var d = JsonDocument.Parse(json); Fs.Handle(IntPtr.Zero, Send, d.RootElement.GetProperty("cmd").GetString(), d.RootElement); }
        void Check(string what, bool ok) { lines.Add((ok ? "OK     " : "ÉCHEC  ") + what); if (!ok) bad++; }
        string J(string s) => JsonSerializer.Serialize(s);   // échappe les antislashs du chemin

        try
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "un");
            File.WriteAllText(Path.Combine(dir, "b.txt"), "deux");
            Directory.CreateDirectory(Path.Combine(dir, "sous"));

            Do($"{{\"cmd\":\"fs.list\",\"path\":{J(dir)},\"tab\":\"t\"}}");
            Check("liste : 3 éléments (2 fichiers, 1 dossier)", replies.Count == 1 && replies[0].GetProperty("items").GetArrayLength() == 3);

            Do($"{{\"cmd\":\"fs.list\",\"path\":{J(Path.Combine(dir, "inexistant"))},\"tab\":\"t\"}}");
            Check("liste : dossier inexistant -> message d'erreur", replies[0].TryGetProperty("error", out _));

            Do("{\"cmd\":\"fs.list\",\"path\":\"\",\"tab\":\"t\"}");
            Check("liste : racine « Ce PC » -> au moins un disque", replies[0].GetProperty("items").GetArrayLength() > 0);

            Do($"{{\"cmd\":\"fs.newfolder\",\"dir\":{J(dir)},\"tab\":\"t\"}}");
            Check("nouveau dossier créé", Directory.Exists(Path.Combine(dir, "Nouveau dossier")));
            Do($"{{\"cmd\":\"fs.newfolder\",\"dir\":{J(dir)},\"tab\":\"t\"}}");
            Check("nouveau dossier : nom unique « (2) »", Directory.Exists(Path.Combine(dir, "Nouveau dossier (2)")));

            Do($"{{\"cmd\":\"fs.rename\",\"path\":{J(Path.Combine(dir, "a.txt"))},\"name\":\"renomme.txt\",\"tab\":\"t\"}}");
            Check("renommer a.txt -> renomme.txt", File.Exists(Path.Combine(dir, "renomme.txt")) && !File.Exists(Path.Combine(dir, "a.txt")));
            Do($"{{\"cmd\":\"fs.rename\",\"path\":{J(Path.Combine(dir, "b.txt"))},\"name\":\"mauvais:nom.txt\",\"tab\":\"t\"}}");
            Check("renommer : caractère interdit refusé, fichier intact", replies[0].TryGetProperty("error", out _) && File.Exists(Path.Combine(dir, "b.txt")));

            Do($"{{\"cmd\":\"fs.copy\",\"paths\":[{J(Path.Combine(dir, "b.txt"))}],\"cut\":false}}");
            Do($"{{\"cmd\":\"fs.paste\",\"dir\":{J(dir)},\"tab\":\"t\"}}");
            Check("copier-coller dans le même dossier : « b (2).txt »", File.Exists(Path.Combine(dir, "b (2).txt")));
            Do($"{{\"cmd\":\"fs.paste\",\"dir\":{J(Path.Combine(dir, "sous"))},\"tab\":\"t\"}}");
            Check("coller dans un sous-dossier", File.Exists(Path.Combine(dir, "sous", "b.txt")) && File.Exists(Path.Combine(dir, "b.txt")));

            Do($"{{\"cmd\":\"fs.copy\",\"paths\":[{J(Path.Combine(dir, "renomme.txt"))}],\"cut\":true}}");
            Do($"{{\"cmd\":\"fs.paste\",\"dir\":{J(Path.Combine(dir, "sous"))},\"tab\":\"t\"}}");
            Check("couper-coller : déplacé (absent de l'origine)", File.Exists(Path.Combine(dir, "sous", "renomme.txt")) && !File.Exists(Path.Combine(dir, "renomme.txt")));

            // --- glisser-déposer (fs.drop) : déplacer, copier, refus d'un dossier dans lui-même ---
            string cible = Path.Combine(dir, "cible"); Directory.CreateDirectory(cible);
            File.WriteAllText(Path.Combine(dir, "dnd1.txt"), "dnd");
            bool HasError() => replies.Count > 0 && replies[0].TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String;
            Do($"{{\"cmd\":\"fs.drop\",\"paths\":[{J(Path.Combine(dir, "dnd1.txt"))}],\"dir\":{J(cible)},\"move\":true,\"tab\":\"t\"}}");
            Check("glisser : déplacer un fichier dans un dossier", !HasError() && File.Exists(Path.Combine(cible, "dnd1.txt")) && !File.Exists(Path.Combine(dir, "dnd1.txt")));
            Do($"{{\"cmd\":\"fs.drop\",\"paths\":[{J(Path.Combine(dir, "b.txt"))}],\"dir\":{J(cible)},\"move\":false,\"tab\":\"t\"}}");
            Check("glisser avec Ctrl : copier (l'original reste)", !HasError() && File.Exists(Path.Combine(cible, "b.txt")) && File.Exists(Path.Combine(dir, "b.txt")));
            Do($"{{\"cmd\":\"fs.drop\",\"paths\":[{J(Path.Combine(cible, "dnd1.txt"))}],\"dir\":{J(cible)},\"move\":false,\"tab\":\"t\"}}");
            Check("copier dans le dossier d'origine : « dnd1 (2).txt »", File.Exists(Path.Combine(cible, "dnd1 (2).txt")));
            Do($"{{\"cmd\":\"fs.drop\",\"paths\":[{J(Path.Combine(cible, "dnd1.txt"))}],\"dir\":{J(cible)},\"move\":true,\"tab\":\"t\"}}");
            Check("déplacer dans le dossier d'origine : rien ne change, pas d'erreur", !HasError() && File.Exists(Path.Combine(cible, "dnd1.txt")) && Directory.GetFiles(cible).Length == 3);
            string sub2 = Path.Combine(cible, "sub2"); Directory.CreateDirectory(sub2);
            Do($"{{\"cmd\":\"fs.drop\",\"paths\":[{J(cible)}],\"dir\":{J(sub2)},\"move\":true,\"tab\":\"t\"}}");
            Check("un dossier ne se déplace pas dans son propre sous-dossier (refus, rien de perdu)", HasError() && Directory.Exists(cible) && Directory.Exists(sub2));
            Do($"{{\"cmd\":\"fs.drop\",\"paths\":[{J(cible)}],\"dir\":{J(cible)},\"move\":false,\"tab\":\"t\"}}");
            Check("ni dans lui-même en copie", HasError() && !Directory.Exists(cible + " (2)"));
            Do($"{{\"cmd\":\"fs.drop\",\"paths\":[{J(Path.Combine(dir, "Nouveau dossier"))}],\"dir\":{J(cible)},\"move\":true,\"tab\":\"t\"}}");
            Check("glisser un dossier dans un autre : déplacé avec son contenu", Directory.Exists(Path.Combine(cible, "Nouveau dossier")) && !Directory.Exists(Path.Combine(dir, "Nouveau dossier")));
            // --- noms déjà pris : la page décide (jamais de boîte de Windows), rien n'est touché tant qu'elle n'a pas répondu ---
            File.WriteAllText(Path.Combine(cible, "x.txt"), "ancien"); File.WriteAllText(Path.Combine(dir, "x.txt"), "nouveau");
            string Conf(string c) => c == null ? "" : $",\"conflict\":\"{c}\"";
            Do($"{{\"cmd\":\"fs.drop\",\"paths\":[{J(Path.Combine(dir, "x.txt"))}],\"dir\":{J(cible)},\"move\":false,\"tab\":\"t\"}}");
            Check("un nom déjà pris : message fs.conflict avec le nom, rien n'est touché", replies.Count == 1 && replies[0].GetProperty("type").GetString() == "fs.conflict" && replies[0].GetProperty("names")[0].GetString() == "x.txt" && File.ReadAllText(Path.Combine(cible, "x.txt")) == "ancien");
            Do($"{{\"cmd\":\"fs.drop\",\"paths\":[{J(Path.Combine(dir, "x.txt"))}],\"dir\":{J(cible)},\"move\":false,\"tab\":\"t\"{Conf("skip")}}}");
            Check("réponse « ignorer » : le fichier de la destination reste, pas de copie", File.ReadAllText(Path.Combine(cible, "x.txt")) == "ancien" && !File.Exists(Path.Combine(cible, "x (2).txt")));
            Do($"{{\"cmd\":\"fs.drop\",\"paths\":[{J(Path.Combine(dir, "x.txt"))}],\"dir\":{J(cible)},\"move\":false,\"tab\":\"t\"{Conf("keep")}}}");
            Check("réponse « garder les deux » : « x (2).txt » créé, l'ancien intact", File.ReadAllText(Path.Combine(cible, "x (2).txt")) == "nouveau" && File.ReadAllText(Path.Combine(cible, "x.txt")) == "ancien");
            Do($"{{\"cmd\":\"fs.drop\",\"paths\":[{J(Path.Combine(dir, "x.txt"))}],\"dir\":{J(cible)},\"move\":true,\"tab\":\"t\"{Conf("replace")}}}");
            Check("réponse « remplacer » (déplacement) : la destination prend le contenu, l'original disparaît", File.ReadAllText(Path.Combine(cible, "x.txt")) == "nouveau" && !File.Exists(Path.Combine(dir, "x.txt")) && !HasError());

            // --- fichiers venus d'une autre application ou d'une autre fenêtre : même disque = déplacer, Ctrl = copier ---
            Check("même disque détecté ; disque différent ou liste vide : non", Fs.SameVolume(new[] { Path.Combine(dir, "x.txt") }, cible) && !Fs.SameVolume(new[] { @"Z:\nulle-part\a.txt" }, cible) && !Fs.SameVolume(Array.Empty<string>(), cible));
            File.WriteAllText(Path.Combine(dir, "ext1.txt"), "e1"); File.WriteAllText(Path.Combine(dir, "ext2.txt"), "e2"); File.WriteAllText(Path.Combine(dir, "ext3.txt"), "e3");
            replies.Clear(); Fs.DropFiles(IntPtr.Zero, Send, new[] { Path.Combine(dir, "ext1.txt") }, cible, false, false, "t");
            Check("déposé sans touche, même disque : déplacé", File.Exists(Path.Combine(cible, "ext1.txt")) && !File.Exists(Path.Combine(dir, "ext1.txt")));
            replies.Clear(); Fs.DropFiles(IntPtr.Zero, Send, new[] { Path.Combine(dir, "ext2.txt") }, cible, false, true, "t");
            Check("déposé avec Ctrl : copié (l'original reste)", File.Exists(Path.Combine(cible, "ext2.txt")) && File.Exists(Path.Combine(dir, "ext2.txt")));
            replies.Clear(); File.WriteAllText(Path.Combine(cible, "ext3.txt"), "autre"); Fs.DropFiles(IntPtr.Zero, Send, new[] { Path.Combine(dir, "ext3.txt") }, cible, true, false, "t");
            Check("déposé avec Maj sur un nom pris : message fs.conflict (avec le choix « déplacer » mémorisé), rien n'est touché", replies.Count == 1 && replies[0].GetProperty("type").GetString() == "fs.conflict" && replies[0].GetProperty("move").GetBoolean() && File.ReadAllText(Path.Combine(cible, "ext3.txt")) == "autre");

            Do($"{{\"cmd\":\"fs.drop\",\"paths\":[{J(Path.Combine(cible, "b.txt"))}],\"dir\":{J(Path.Combine(dir, "n-existe-pas"))},\"move\":true,\"tab\":\"t\"}}");
            Check("destination qui n'existe pas : message d'erreur, fichier intact", HasError() && File.Exists(Path.Combine(cible, "b.txt")));
            Do($"{{\"cmd\":\"fs.drop\",\"paths\":[\"relatif/x.txt\",\"\"],\"dir\":{J(cible)},\"move\":true,\"tab\":\"t\"}}");
            Check("chemins relatifs ou vides ignorés sans erreur", !HasError());

            // --- réglages (explorer.json) : écriture atomique, relecture, refus des clés et valeurs invalides ---
            Settings.Reset();
            using (var sj = JsonDocument.Parse("{\"a\":1,\"b\":[1,2,3]}"))
            {
                Check("réglages : une clé valide s'enregistre", Settings.Set("vue.dossier", sj.RootElement));
                Check("réglages : une clé hors du motif est refusée (majuscule, chemin, vide)", !Settings.Set("Vue", sj.RootElement) && !Settings.Set("../x", sj.RootElement) && !Settings.Set("", sj.RootElement) && !Settings.Set(null, sj.RootElement));
            }
            Settings.Reset();
            Check("réglages : relus depuis le fichier après oubli de la mémoire", Settings.Get("vue.dossier")?["b"]?.AsArray().Count == 3 && File.Exists(Settings.FilePath) && !File.Exists(Settings.FilePath + ".tmp"));
            using (var nul = JsonDocument.Parse("null")) Check("réglages : une valeur null efface la clé", Settings.Set("vue.dossier", nul.RootElement) && Settings.Get("vue.dossier") == null);
            using (var big = JsonDocument.Parse("\"" + new string('x', 300 * 1024) + "\"")) Check("réglages : une valeur de plus de 256 Ko est refusée", !Settings.Set("gros", big.RootElement));
            File.WriteAllText(Settings.FilePath, "{ pas du json"); Settings.Reset();
            Check("réglages : un fichier illisible donne des réglages vides sans exception", Settings.All().ValueKind == JsonValueKind.Object && Settings.Get("vue.dossier") == null);
            File.Delete(Settings.FilePath); Settings.Reset();

            // --- file de tâches : ordre, avancement, pause, reprise, annulation ---
            var q = new JobQueue(); var order = new List<int>(); var seen = new List<JobState>();
            q.Changed += j => { lock (seen) seen.Add(j.State); };
            var j1 = q.Enqueue("copy", "un", jb => { order.Add(1); jb.Report(50, 100, "a.txt"); });
            var j2 = q.Enqueue("copy", "deux", jb => { order.Add(2); });
            bool WaitState(Job j, JobState s, int ms = 5000) { var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { if (j.State == s) return true; Thread.Sleep(20); } return j.State == s; }
            Check("tâches : exécutées l'une après l'autre, dans l'ordre", WaitState(j1, JobState.Done) && WaitState(j2, JobState.Done) && order.SequenceEqual(new[] { 1, 2 }));
            Check("tâches : l'avancement est gardé (50 sur 100, élément courant)", j1.Done == 50 && j1.Total == 100 && j1.Current == "a.txt");
            Check("tâches : chaque changement d'état est signalé", seen.Contains(JobState.Queued) && seen.Contains(JobState.Running) && seen.Contains(JobState.Done));
            var gate = new ManualResetEventSlim(false); int steps = 0;
            var j3 = q.Enqueue("copy", "long", jb => { for (int i = 0; i < 400; i++) { jb.WaitIfPaused(); Interlocked.Increment(ref steps); Thread.Sleep(10); if (i == 2) gate.Set(); } });
            gate.Wait(3000);
            Check("tâches : une pause arrête l'avancement", q.Pause(j3.Id) && j3.State == JobState.Paused);
            Thread.Sleep(150); int frozen = steps; Thread.Sleep(250);
            Check("tâches : en pause, plus aucun pas n'est fait", steps == frozen);
            Check("tâches : la reprise relance l'avancement", q.Resume(j3.Id) && (WaitState(j3, JobState.Running) && SpinWait.SpinUntil(() => steps > frozen + 2, 2000)));
            var j4 = q.Enqueue("copy", "attend", jb => { order.Add(4); });
            Check("tâches : une tâche en attente annulée ne démarre jamais", q.Cancel(j4.Id) && j4.State == JobState.Canceled);
            Check("tâches : annuler la tâche en cours l'arrête (même après une pause)", q.Pause(j3.Id) && q.Cancel(j3.Id) && WaitState(j3, JobState.Canceled) && steps < 400 && !order.Contains(4));
            var j5 = q.Enqueue("copy", "échoue", jb => throw new IOException("disque plein"));
            Check("tâches : une exception met la tâche en échec avec son message, la file continue", WaitState(j5, JobState.Failed) && j5.Error == "disque plein");
            var j6 = q.Enqueue("copy", "après", jb => { order.Add(6); });
            Check("tâches : la file continue après un échec", WaitState(j6, JobState.Done) && order.Contains(6));
            q.Prune(2);
            Check("tâches : Prune ne garde que les dernières tâches terminées", q.Snapshot().Count == 2);

            // --- ce que le menu emprunte à Windows : Ouvrir avec, Envoyer vers, Nouveau, raccourcis, terminal ---
            var ow = ShellData.OpenWith(".txt");
            // (un runner de CI n'a aucun programme associé à .txt : la liste vide y est acceptée)
            Check("Ouvrir avec : « .txt » a au moins un programme, dont l'exécutable existe", (ow.Count >= 1 || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") && ow.All(p => File.Exists(p.exe) && p.exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && p.name.Length > 0));
            Check("Ouvrir avec : une extension inconnue ou piégée donne une liste vide, sans exception", ShellData.OpenWith(".zzqq9").Count == 0 && ShellData.OpenWith("txt").Count == 0 && ShellData.OpenWith(".a\\b").Count == 0 && ShellData.OpenWith(null).Count == 0);
            Check("Ouvrir avec : on ne lance qu'un .exe existant sur un fichier qui existe", !ShellData.OpenWithRun(@"C:\nulle\part.exe", Path.Combine(dir, "x.txt")) && !ShellData.OpenWithRun(Path.Combine(dir, "x.txt"), Path.Combine(dir, "x.txt")) && !ShellData.OpenWithRun(ow[0].exe, Path.Combine(dir, "n-existe-pas.txt")) && !ShellData.OpenWithRun(null, null));
            var kinds = ShellData.NewKinds();
            Check("Nouveau : Windows sait créer un document texte (modèle ShellNew de « .txt »)", kinds.Any(k => k.Ext == ".txt") && kinds.All(k => k.Name.Length > 0 && k.Ext != ".lnk"));
            string nd = Path.Combine(dir, "nouveaux"); Directory.CreateDirectory(nd);
            string n1 = ShellData.CreateNew(nd, ".txt"), n2 = ShellData.CreateNew(nd, ".txt");
            Check("Nouveau : crée un fichier .txt vide, puis un second au nom différent", n1 != null && n2 != null && n1 != n2 && File.Exists(n1) && new FileInfo(n1).Length == 0 && n1.EndsWith(".txt") && Path.GetDirectoryName(n1) == nd);
            Check("Nouveau : une extension sans modèle ou un dossier inexistant donne null", ShellData.CreateNew(nd, ".zzqq9") == null && ShellData.CreateNew(Path.Combine(dir, "nulle"), ".txt") == null && ShellData.CreateNew(nd, null) == null);
            string lnk = ShellData.CreateShortcut(nd, "vers cible", cible);
            Check("Raccourci : « vers cible.lnk » est créé et pointe vers le dossier", lnk != null && lnk.EndsWith("vers cible.lnk") && File.Exists(lnk) && ShellData.ResolveLink(lnk)?.target.TrimEnd('\\') == cible.TrimEnd('\\'));
            string lnk2 = ShellData.CreateShortcut(nd, "vers cible", cible);
            Check("Raccourci : un second du même nom devient « vers cible (2).lnk »", lnk2 != null && lnk2.EndsWith("vers cible (2).lnk"));
            Check("Raccourci : une cible vide ou un dossier inexistant donne null", ShellData.CreateShortcut(nd, "x", "  ") == null && ShellData.CreateShortcut(Path.Combine(dir, "nulle"), "x", cible) == null);
            var st = ShellData.SendTo();
            Check("Envoyer vers : la liste se lit sans exception, chaque élément a un nom et un chemin qui existe", st.All(s => s.Name.Length > 0 && (File.Exists(s.Path) || Directory.Exists(s.Path))));
            Check("Envoyer vers : un chemin qui n'est pas dans la liste est refusé (jamais un chemin quelconque)", ShellData.SendToTarget(cible) == (null, null) && ShellData.SendToTarget(lnk) == (null, null) && ShellData.SendToTarget(null) == (null, null));
            var wt = ShellData.TerminalCommand(@"C:\Mes Dossiers\x", true); var cm = ShellData.TerminalCommand(@"C:\Mes Dossiers\x", false);
            Check("Terminal : Windows Terminal reçoit -d et le dossier ; sinon cmd /k cd /d", wt.exe == "wt.exe" && wt.args.SequenceEqual(new[] { "-d", @"C:\Mes Dossiers\x" }) && wt.shell && cm.exe == "cmd.exe" && cm.args[0] == "/k" && cm.args[1].Contains("cd /d \"C:\\Mes Dossiers\\x\"") && !cm.shell);

            // --- recherche dans les sous-dossiers ---
            string sr = Path.Combine(dir, "arbre"); Directory.CreateDirectory(Path.Combine(sr, "a", "b")); Directory.CreateDirectory(Path.Combine(sr, "c"));
            void W(string rel, long bytes, DateTime? when = null) { string f = Path.Combine(sr, rel); File.WriteAllBytes(f, new byte[bytes]); if (when != null) File.SetLastWriteTime(f, when.Value); }
            W("facture-2025.pdf", 2_000_000); W("facture-2026.pdf", 10); W(Path.Combine("a", "photo.JPG"), 5); W(Path.Combine("a", "b", "FACTURE vieille.txt"), 3, DateTime.Now.AddDays(-400));
            W(Path.Combine("c", "musique.mp3"), 4); W(Path.Combine("c", "code.cs"), 7); W("notes.md", 1, DateTime.Now.AddDays(-3));
            File.WriteAllText(Path.Combine(sr, "a", "secret.txt"), "s"); File.SetAttributes(Path.Combine(sr, "a", "secret.txt"), FileAttributes.Hidden);
            Settings.Reset(); File.Delete(Settings.FilePath);
            List<JsonElement> Find(string root, string q, string kind = "all", string date = "any", string size = "any", CancellationToken ct = default)
            {
                var got = new List<JsonElement>();
                Search.Run(new SearchSpec(root, q, kind, date, size), "t", 1, ct, o => { using var d = JsonDocument.Parse(JsonSerializer.Serialize(o)); got.Add(d.RootElement.Clone()); });
                return got;
            }
            string Rel(List<JsonElement> msgs) => string.Join(",", msgs.SelectMany(m => m.GetProperty("items").EnumerateArray()).Select(i => i.GetProperty("n").GetString()).OrderBy(x => x, StringComparer.Ordinal));
            var fa = Find(sr, "facture");
            Check("recherche : « facture » trouve les trois fichiers, sans tenir compte de la casse ni du sous-dossier", Rel(fa) == Path.Combine("a", "b", "FACTURE vieille.txt") + ",facture-2025.pdf,facture-2026.pdf");
            Check("recherche : le dernier message dit done=true, truncated=false, et les chemins relatifs sont uniques", fa[^1].GetProperty("done").GetBoolean() && !fa[^1].GetProperty("truncated").GetBoolean() && fa.Take(fa.Count - 1).All(m => !m.GetProperty("done").GetBoolean()));
            var one = fa.SelectMany(m => m.GetProperty("items").EnumerateArray()).First(i => i.GetProperty("b").GetString() == "FACTURE vieille.txt");
            Check("recherche : un résultat porte son nom, son dossier relatif et son chemin complet", one.GetProperty("r").GetString() == Path.Combine("a", "b") && one.GetProperty("p").GetString() == Path.Combine(sr, "a", "b", "FACTURE vieille.txt") && !one.GetProperty("d").GetBoolean());
            Check("recherche : plusieurs mots = tous doivent être dans le nom (« facture 2026 »)", Rel(Find(sr, "facture 2026")) == "facture-2026.pdf");
            Check("recherche : joker « *.pdf » et « fact?re-20*6.pdf »", Rel(Find(sr, "*.pdf")) == "facture-2025.pdf,facture-2026.pdf" && Rel(Find(sr, "fact?re-20*6.pdf")) == "facture-2026.pdf");
            Check("recherche : type « image », « audio », « code », « document »", Rel(Find(sr, "", "image")) == Path.Combine("a", "photo.JPG") && Rel(Find(sr, "", "audio")) == Path.Combine("c", "musique.mp3") && Rel(Find(sr, "", "code")) == Path.Combine("c", "code.cs") && Rel(Find(sr, "", "document")).Split(',').Length == 4);
            Check("recherche : type « dossier » ne rend que les dossiers (a, a\\b, c)", Rel(Find(sr, "", "folder")) == "a," + Path.Combine("a", "b") + ",c");
            Check("recherche : date « 7 derniers jours » exclut la facture de 400 jours", !Rel(Find(sr, "facture", "all", "week")).Contains("vieille") && Rel(Find(sr, "notes", "all", "week")) == "notes.md");
            Check("recherche : date « aujourd'hui » exclut notes.md (il y a 3 jours)", !Rel(Find(sr, "", "all", "today")).Contains("notes.md"));
            Check("recherche : taille « plus de 1 Mo » ne rend que la facture de 2 Mo, jamais un dossier", Rel(Find(sr, "", "all", "any", "medium")) == "facture-2025.pdf" && Rel(Find(sr, "", "all", "any", "small")).Contains("facture-2026.pdf") && !Rel(Find(sr, "", "all", "any", "small")).Split(',').Contains("a"));
            Check("recherche : combiner nom + type + taille (« facture », documents, moins de 1 Mo) = facture-2026.pdf et la vieille", Rel(Find(sr, "facture", "document", "any", "small")) == Path.Combine("a", "b", "FACTURE vieille.txt") + ",facture-2026.pdf");
            Check("recherche : les éléments cachés sont ignorés par défaut", !Rel(Find(sr, "secret")).Contains("secret"));
            using (var tr = JsonDocument.Parse("true")) Settings.Set("show.hidden", tr.RootElement);
            Check("recherche : avec show.hidden vrai, secret.txt est trouvé", Rel(Find(sr, "secret")) == Path.Combine("a", "secret.txt"));
            File.Delete(Settings.FilePath); Settings.Reset();
            Check("recherche : sans mot ni critère, tous les éléments (11 : 3 dossiers + 7 fichiers visibles + ... )".Substring(0, 0) + "recherche : sans mot ni critère, tout est rendu (3 dossiers + 7 fichiers visibles = 10)", Rel(Find(sr, "")).Split(',').Length == 10);
            using (var cc = new CancellationTokenSource()) { cc.Cancel(); bool cancelled = false; try { Find(sr, "", ct: cc.Token); } catch (OperationCanceledException) { cancelled = true; } Check("recherche : un jeton annulé arrête le parcours (OperationCanceledException)", cancelled); }
            Check("recherche : un dossier inexistant ne rend rien et ne plante pas", Find(Path.Combine(dir, "nulle"), "x").Count == 1 && Find(Path.Combine(dir, "nulle"), "x")[0].GetProperty("done").GetBoolean());
            var swp = Stopwatch.StartNew(); var pm = Search.NameMatcher(string.Concat(Enumerable.Repeat("*a", 30)) + "b"); pm(new string('a', 5000));
            Check("recherche : NameMatcher résiste à une requête de jokers pathologique (pas d'explosion, rend vite)", swp.ElapsedMilliseconds < 3000);

            // --- le menu de Windows (« Plus d'options ») : construit pour de bon, jamais affiché ---
            string sm = Path.Combine(dir, "natif"); Directory.CreateDirectory(sm); string smf = Path.Combine(sm, "un.txt"), smg = Path.Combine(sm, "deux.txt"); File.WriteAllText(smf, "1"); File.WriteAllText(smg, "2");
            Check("menu natif : seuls des éléments existants du même dossier sont acceptés", ShellMenu.Validate(new[] { smf, smg }).Length == 2 && File.Exists(Path.Combine(dir, "b.txt")) && ShellMenu.Validate(new[] { smf, Path.Combine(dir, "b.txt") }).Length == 0 && ShellMenu.Validate(new[] { smf, Path.Combine(sm, "nulle.txt") }).Length == 1 && ShellMenu.Validate(new[] { "relatif.txt", "" }).Length == 0 && ShellMenu.Validate(null).Length == 0);
            var nat = ShellMenu.Probe(new[] { smf });
            Check("menu natif : Windows construit un menu pour un fichier (au moins 5 entrées, toutes avec un libellé)", nat.Count >= 5 && nat.All(x => x.Length > 0));
            Check("menu natif : il contient « Propriétés » (ou Properties)", nat.Any(x => x.StartsWith("Propriétés", StringComparison.OrdinalIgnoreCase) || x.StartsWith("Properties", StringComparison.OrdinalIgnoreCase)));
            var natDir = ShellMenu.Probe(new[] { sm });
            Check("menu natif : pour un dossier aussi, et pour deux fichiers", natDir.Count >= 5 && ShellMenu.Probe(new[] { smf, smg }).Count >= 3);
            Check("menu natif : un chemin refusé ne plante pas et ne rend aucun menu (liste vide)", ShellMenu.Probe(new[] { Path.Combine(sm, "nulle.txt") }).Count == 0 && ShellMenu.Probe(new string[0]).Count == 0);
            Check("menu natif : rien d'actif en dehors d'un affichage (Forward ne détourne aucun message)", !ShellMenu.Active);

            // --- corbeille : lecture des « $I », mise à la corbeille, restauration, effacement (sur des éléments créés ici seulement) ---
            byte[] Info(long ver, long size, string path)
            {
                var ms2 = new MemoryStream(); var w2 = new BinaryWriter(ms2);
                w2.Write(ver); w2.Write(size); w2.Write(new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc());
                if (ver == 2) { w2.Write(path.Length + 1); w2.Write(System.Text.Encoding.Unicode.GetBytes(path + "\0")); }
                else { var pb = new byte[520]; System.Text.Encoding.Unicode.GetBytes(path + "\0").CopyTo(pb, 0); w2.Write(pb); }
                return ms2.ToArray();
            }
            var pi2 = Recycle.ParseInfo(Info(2, 1234, @"C:\Users\Essai\Documents\mon fichier é.txt")); var pi1 = Recycle.ParseInfo(Info(1, 55, @"D:\vieux\fichier.doc"));
            Check("corbeille : un « $I » de version 2 (Windows 10 et 11) est lu : chemin, taille, date", pi2 != null && pi2.Value.original == @"C:\Users\Essai\Documents\mon fichier é.txt" && pi2.Value.size == 1234 && pi2.Value.deleted.ToUniversalTime().Year == 2026);
            Check("corbeille : un « $I » de version 1 (Windows 7 et 8) aussi", pi1 != null && pi1.Value.original == @"D:\vieux\fichier.doc" && pi1.Value.size == 55);
            byte[] badInfo = Info(2, 1, @"C:\x.txt"); BitConverter.GetBytes(99999).CopyTo(badInfo, 24);
            Check("corbeille : un « $I » invalide (court, version inconnue, longueur absurde, chemin relatif) donne null", Recycle.ParseInfo(new byte[10]) == null && Recycle.ParseInfo(Info(3, 1, @"C:\x")) == null && Recycle.ParseInfo(badInfo) == null && Recycle.ParseInfo(Info(2, 1, "relatif.txt")) == null && Recycle.ParseInfo(null) == null);
            string bin1 = Path.Combine(dir, "pour-corbeille.txt"); File.WriteAllText(bin1, "contenu à garder");
            var be = Recycle.DeleteToBin(bin1);
            Check("corbeille : DeleteToBin met le fichier à la corbeille et rend son entrée (origine, taille, date récente)", be != null && !File.Exists(bin1) && be.Original == bin1 && be.Size == new FileInfo(be.DataPath).Length && be.DataPath != null && File.Exists(be.DataPath) && (DateTime.Now - be.Deleted).TotalMinutes < 2 && !be.IsDir);
            Check("corbeille : l'entrée est dans la liste et son identifiant est reconnu ; un chemin ordinaire ne l'est pas", Recycle.List().Any(x => x.Id == be.Id) && Recycle.IsEntryId(be.Id) && !Recycle.IsEntryId(bin1) && !Recycle.IsEntryId(@"C:\Windows\notepad.exe") && !Recycle.IsEntryId(null));
            var (rerr, rdest) = Recycle.Restore(be.Id);
            Check("corbeille : Restore remet le fichier à sa place avec son contenu, et l'entrée disparaît", rerr == null && rdest == bin1 && File.ReadAllText(bin1) == "contenu à garder" && !Recycle.List().Any(x => x.Id == be.Id));
            var be2 = Recycle.DeleteToBin(bin1); File.WriteAllText(bin1, "un autre fichier au même nom");
            var (cerr, _) = Recycle.Restore(be2.Id);
            Check("corbeille : restaurer sur un nom pris ne l'écrase jamais (message, l'entrée reste)", cerr != null && cerr.Contains("existe déjà") && File.ReadAllText(bin1) == "un autre fichier au même nom" && Recycle.List().Any(x => x.Id == be2.Id));
            Check("corbeille : Purge efface l'entrée pour de bon (« $R » et « $I »)", Recycle.Purge(new[] { be2.Id }) == null && !Recycle.List().Any(x => x.Id == be2.Id) && !File.Exists(be2.DataPath) && !File.Exists(be2.Id));
            Check("corbeille : Purge refuse tout ce qui n'est pas une entrée (aucun fichier ordinaire n'est touché)", Recycle.Purge(new[] { bin1, @"C:\Windows\notepad.exe", "relatif", null }) == null && File.Exists(bin1) && File.Exists(@"C:\Windows\notepad.exe"));
            string bd = Path.Combine(dir, "dossier-corbeille"); Directory.CreateDirectory(Path.Combine(bd, "sous")); File.WriteAllText(Path.Combine(bd, "sous", "f.txt"), "dans le dossier");
            var bde = Recycle.DeleteToBin(bd);
            Check("corbeille : un dossier part avec son contenu (IsDir), et revient avec lui", bde != null && bde.IsDir && !Directory.Exists(bd) && Recycle.Restore(bde.Id).error == null && File.ReadAllText(Path.Combine(bd, "sous", "f.txt")) == "dans le dossier");
            Check("corbeille : DeletePermanently efface sans corbeille, et refuse une racine de disque, un chemin relatif ou vide", Recycle.DeletePermanently(new[] { bin1, bd, @"C:\", "relatif", "", null }) == null && !File.Exists(bin1) && !Directory.Exists(bd) && Directory.Exists(@"C:\") && !Recycle.List().Any(x => x.Original == bin1));

            // --- annuler / rétablir ---
            var ul = new UndoLog();
            string ua = Path.Combine(dir, "ua"); Directory.CreateDirectory(ua); string uf = Path.Combine(ua, "u1.txt"), ug = Path.Combine(ua, "sous", "u1.txt"); File.WriteAllText(uf, "u");
            Directory.CreateDirectory(Path.GetDirectoryName(ug)); File.Move(uf, ug);
            ul.Push(Undo.Moved("Déplacer", new List<(string, string)> { (uf, ug) }));
            Check("annuler : l'étiquette du dernier geste est dite (« Déplacer »), rien à rétablir", ul.UndoLabel == "Déplacer" && ul.RedoLabel == null && ul.UndoCount == 1);
            var u1 = ul.DoUndo();
            Check("annuler un déplacement : le fichier revient à son dossier d'origine", u1 == ("Déplacer", null) && File.Exists(uf) && !File.Exists(ug) && ul.RedoLabel == "Déplacer" && ul.UndoLabel == null);
            var u2 = ul.DoRedo();
            Check("rétablir : le déplacement est refait", u2 == ("Déplacer", null) && File.Exists(ug) && !File.Exists(uf) && ul.UndoLabel == "Déplacer");
            File.WriteAllText(uf, "occupé"); var u3 = ul.DoUndo();
            Check("annuler quand l'origine est occupée : refusé avec un message, rien n'est écrasé, le geste est retiré de la pile", u3.error != null && u3.error.Contains("existe déjà") && File.ReadAllText(uf) == "occupé" && File.Exists(ug) && ul.UndoCount == 0);
            File.Delete(uf); File.Delete(ug);
            string cf = Path.Combine(ua, "cree.txt"); File.WriteAllText(cf, "créé");
            ul.Push(Undo.Created("Nouveau fichier", new[] { cf }));
            var c1 = ul.DoUndo();
            Check("annuler une création : l'élément va à la corbeille", c1 == ("Nouveau fichier", null) && !File.Exists(cf));
            var c2 = ul.DoRedo();
            Check("rétablir une création : il revient de la corbeille, avec son contenu", c2 == ("Nouveau fichier", null) && File.ReadAllText(cf) == "créé");
            string df = Path.Combine(ua, "supprime.txt"); File.WriteAllText(df, "supprimé"); var de = Recycle.DeleteToBin(df);
            ul.Push(Undo.Deleted("Supprimer", new List<BinEntry> { de }));
            var d1 = ul.DoUndo();
            Check("annuler une suppression : le fichier est restauré", d1 == ("Supprimer", null) && File.ReadAllText(df) == "supprimé");
            var d2 = ul.DoRedo();
            Check("rétablir une suppression : il retourne à la corbeille ; l'annuler de nouveau le restaure encore", d2 == ("Supprimer", null) && !File.Exists(df) && ul.DoUndo() == ("Supprimer", null) && File.Exists(df));
            for (int k = 0; k < 60; k++) ul.Push(Undo.Moved("n" + k, new List<(string, string)>()));
            int count50 = ul.UndoCount; var last = ul.DoUndo(); int redoAfter = ul.RedoCount;
            ul.Push(Undo.Moved("x", new List<(string, string)>()));
            Check("annuler : la pile garde 50 gestes au plus, annule le dernier (n59), et un nouveau geste efface « rétablir »", count50 == 50 && last.label == "n59" && redoAfter == 1 && ul.RedoCount == 0);
            Check("annuler : sur une pile vide, rien ne se passe", new UndoLog().DoUndo() == (null, null) && new UndoLog().DoRedo() == (null, null));
            var win = new IntPtr(424242); Undo.For(win).Push(Undo.Moved("Renommer", new List<(string, string)>()));
            bool oneWin = Undo.For(win).UndoLabel == "Renommer" && Undo.For(new IntPtr(424243)).UndoLabel == null && ((dynamic)Undo.State(win)).undo == "Renommer";
            Undo.Forget(win);
            Check("annuler : une pile par fenêtre (l'état dit « Renommer » pour l'une, rien pour l autre) ; elle s oublie à la fermeture", oneWin && Undo.For(win).UndoLabel == null);
            // ne rien laisser dans la vraie corbeille de Valentin : tout ce qui vient du dossier de l'essai est effacé
            Recycle.Purge(Recycle.List().Where(x => x.Original.StartsWith(dir, StringComparison.OrdinalIgnoreCase)).Select(x => x.Id));
            Check("corbeille : plus rien de l'essai dans la corbeille", !Recycle.List().Any(x => x.Original.StartsWith(dir, StringComparison.OrdinalIgnoreCase)));

            // --- moteur de copie : pas de boîte Windows, progression, pause, annulation sans reste, erreurs par fichier ---
            string cp = Path.Combine(dir, "moteur"); Directory.CreateDirectory(cp);
            void MakeBig(string path, int mb) { using var fsb = new FileStream(path, FileMode.Create); fsb.SetLength((long)mb << 20); fsb.Position = 0; fsb.WriteByte(7); fsb.Position = ((long)mb << 20) - 1; fsb.WriteByte(9); }
            string Sha(string path) { using var h = System.Security.Cryptography.SHA256.Create(); using var s = File.OpenRead(path); return Convert.ToHexString(h.ComputeHash(s)); }
            bool WaitFor(Func<bool> f, int ms) { var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { if (f()) return true; Thread.Sleep(15); } return f(); }
            string srcDir = Path.Combine(cp, "src"), dstDir = Path.Combine(cp, "dst"); Directory.CreateDirectory(srcDir); Directory.CreateDirectory(dstDir);
            string tree = Path.Combine(srcDir, "arbre"); Directory.CreateDirectory(Path.Combine(tree, "vide")); Directory.CreateDirectory(Path.Combine(tree, "n1", "n2"));
            File.WriteAllText(Path.Combine(tree, "racine.txt"), "à la racine"); File.WriteAllText(Path.Combine(tree, "n1", "n2", "profond é.txt"), "profond");
            File.SetAttributes(Path.Combine(tree, "racine.txt"), FileAttributes.ReadOnly); var tracine = new DateTime(2020, 5, 17, 12, 30, 0, DateTimeKind.Utc); File.SetLastWriteTimeUtc(Path.Combine(tree, "racine.txt"), tracine);
            var rc = Fs.Transfer(new[] { tree }, dstDir, false);
            Check("moteur : copier un dossier recrée l'arborescence (dossier vide compris), le contenu, l'horodatage ; l'original reste intact", rc.Error == null && Directory.Exists(Path.Combine(dstDir, "arbre", "vide")) && File.ReadAllText(Path.Combine(dstDir, "arbre", "n1", "n2", "profond é.txt")) == "profond"
                && File.GetLastWriteTimeUtc(Path.Combine(dstDir, "arbre", "racine.txt")) == tracine && File.Exists(Path.Combine(tree, "racine.txt")) && rc.Done.Count == 1);
            File.SetAttributes(Path.Combine(tree, "racine.txt"), FileAttributes.Normal);
            File.WriteAllText(Path.Combine(tree, "racine.txt"), "changé"); File.SetAttributes(Path.Combine(dstDir, "arbre", "racine.txt"), FileAttributes.ReadOnly);
            var rr = Fs.Transfer(new[] { tree }, dstDir, false, "replace");
            Check("moteur : « remplacer » écrase aussi un fichier en lecture seule et fusionne le dossier", rr.Error == null && File.ReadAllText(Path.Combine(dstDir, "arbre", "racine.txt")) == "changé" && File.Exists(Path.Combine(dstDir, "arbre", "n1", "n2", "profond é.txt")));
            var rk = Fs.Transfer(new[] { tree }, dstDir, false, "keep");
            Check("moteur : « garder les deux » crée « arbre (2) » à côté, sans toucher au premier", rk.Error == null && Directory.Exists(Path.Combine(dstDir, "arbre (2)", "n1")) && File.ReadAllText(Path.Combine(dstDir, "arbre", "racine.txt")) == "changé");
            string mv = Path.Combine(srcDir, "a-deplacer"); Directory.CreateDirectory(mv); File.WriteAllText(Path.Combine(mv, "x.txt"), "x");
            var rm = Fs.Transfer(new[] { mv }, dstDir, true);
            Check("moteur : déplacer sur le même disque renomme (le dossier arrive avec son contenu, l'original disparaît)", rm.Error == null && !Directory.Exists(mv) && File.ReadAllText(Path.Combine(dstDir, "a-deplacer", "x.txt")) == "x" && rm.Done[0].src == mv);
            // chemin très long (plus de 260 caractères)
            string deep = srcDir; for (int k = 0; k < 6; k++) deep = Path.Combine(deep, new string('d', 50) + k);
            Directory.CreateDirectory(Copier.Long(deep)); File.WriteAllText(Copier.Long(Path.Combine(deep, "profond.txt")), "très long");
            var rl = Fs.Transfer(new[] { Path.Combine(srcDir, new string('d', 50) + "0") }, dstDir, false);
            string deepDst = dstDir; for (int k = 0; k < 6; k++) deepDst = Path.Combine(deepDst, new string('d', 50) + k);
            Check("moteur : un chemin de plus de 260 caractères se copie (préfixe « \\\\?\\ »)", rl.Error == null && deepDst.Length > 260 && File.Exists(Copier.Long(Path.Combine(deepDst, "profond.txt"))));
            Copier.DeleteTree(Path.Combine(srcDir, new string('d', 50) + "0")); Copier.DeleteTree(Path.Combine(dstDir, new string('d', 50) + "0"));

            // progression, pause, reprise : une copie de 64 Mo dans la file, arrêtée par un point d'accroche au premier méga-octet
            var mq = new JobQueue(); string mbig = Path.Combine(srcDir, "gros.bin"); MakeBig(mbig, 64); string bigDst = Path.Combine(dstDir, "gros.bin");
            using (var mgate = new ManualResetEventSlim(false))
            {
                Job pj = null; CopyOutcome po = null;
                Copier.TestHook = (name, x) => { if (name == "gros.bin" && x >= (1 << 20) && !mgate.IsSet) { pj.SetPaused(true); mgate.Set(); } };
                var plan = new List<PlanItem> { new PlanItem(mbig, false, bigDst, false) };
                pj = mq.Enqueue("copy", "gros.bin", job => { pj = job; po = Copier.Run(plan, false, job); });
                bool hit = mgate.Wait(15000); Thread.Sleep(400); long md1 = pj.Done; Thread.Sleep(400); long md2 = pj.Done;
                Check("pause : la copie s'arrête au méga-octet demandé et n'avance plus tant qu'on ne la reprend pas", hit && md1 == md2 && md2 < (64L << 20) && pj.Total == (64L << 20) && pj.State == JobState.Running);
                bool paused = mq.Pause(pj.Id) && pj.State == JobState.Paused; bool resumed = mq.Resume(pj.Id) && pj.State == JobState.Running;
                Check("pause : la file annonce la tâche « en pause » puis « en cours » à la reprise", paused && resumed);
                Check("reprise : la copie se termine, le fichier est identique à l'original (même empreinte)", WaitFor(() => pj.State == JobState.Done, 20000) && File.Exists(bigDst) && Sha(mbig) == Sha(bigDst) && po.Done.Count == 1 && pj.Done == pj.Total);
                Copier.TestHook = null;
            }
            File.Delete(bigDst);

            // annulation en plein fichier : rien de moitié copié
            using (var mgate = new ManualResetEventSlim(false))
            {
                Job cj = null; CopyOutcome co = null;
                Copier.TestHook = (name, x) => { if (name == "gros.bin" && x >= (1 << 20) && !mgate.IsSet) { cj.SetPaused(true); mgate.Set(); } };
                var plan = new List<PlanItem> { new PlanItem(mbig, false, bigDst, false) };
                cj = mq.Enqueue("copy", "gros.bin", job => { cj = job; co = Copier.Run(plan, false, job); });
                bool hit = mgate.Wait(15000); mq.Cancel(cj.Id);
                Check("annulation en plein fichier : la tâche est « annulée », la copie partielle est effacée, l'original est intact, rien n'est inscrit pour « Annuler »", hit && WaitFor(() => cj.State == JobState.Canceled, 15000) && !File.Exists(bigDst) && File.Exists(mbig) && co != null && co.Canceled && co.Done.Count == 0);
                Copier.TestHook = null;
            }
            // annulation d'un dossier : ce que l'élément avait déjà copié disparaît avec lui
            string many = Path.Combine(srcDir, "lot"); Directory.CreateDirectory(many); foreach (var n in new[] { "f1.bin", "f2.bin", "f3.bin" }) MakeBig(Path.Combine(many, n), 4);
            using (var mgate = new ManualResetEventSlim(false))
            {
                Job dj = null; CopyOutcome dout = null;
                Copier.TestHook = (name, x) => { if (name == "f2.bin" && x >= (1 << 20) && !mgate.IsSet) { dj.SetPaused(true); mgate.Set(); } };
                var plan = new List<PlanItem> { new PlanItem(many, true, Path.Combine(dstDir, "lot"), false) };
                dj = mq.Enqueue("copy", "lot", job => { dj = job; dout = Copier.Run(plan, false, job); });
                bool hit = mgate.Wait(15000); mq.Cancel(dj.Id);
                Check("annulation d'un dossier : le dossier créé et ses fichiers déjà copiés sont retirés, l'original est intact", hit && WaitFor(() => dj.State == JobState.Canceled, 15000) && !Directory.Exists(Path.Combine(dstDir, "lot")) && Directory.GetFiles(many).Length == 3);
                Copier.TestHook = null;
            }
            // un fichier verrouillé n'arrête pas les autres ; l'original d'un déplacement n'est pas effacé
            string lk = Path.Combine(srcDir, "verrou"); Directory.CreateDirectory(lk); File.WriteAllText(Path.Combine(lk, "libre.txt"), "libre"); File.WriteAllText(Path.Combine(lk, "pris.txt"), "pris");
            using (var hold = new FileStream(Path.Combine(lk, "pris.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var ro = Copier.Run(new[] { new PlanItem(lk, true, Path.Combine(dstDir, "verrou"), false) }, false, null);
                Check("erreur par fichier : le fichier verrouillé est signalé, les autres sont copiés, l'élément est dit en échec (réessayable)", ro.Errors.Count == 1 && ro.Errors[0].StartsWith("pris.txt") && File.Exists(Path.Combine(dstDir, "verrou", "libre.txt")) && ro.Failed.SequenceEqual(new[] { lk }) && ro.Done.Count == 0);
                Directory.Delete(Path.Combine(dstDir, "verrou"), true);
                var rv = Copier.Run(new[] { new PlanItem(lk, true, Path.Combine(dstDir, "verrou2"), false) }, true, null);
                Check("déplacer un dossier dont un fichier est tenu ouvert échoue avec un message et laisse tout l'original en place", rv.Errors.Count == 1 && rv.Done.Count == 0 && File.Exists(Path.Combine(lk, "libre.txt")) && File.Exists(Path.Combine(lk, "pris.txt")));
            }
            if (Directory.Exists(Path.Combine(dstDir, "verrou2"))) Directory.Delete(Path.Combine(dstDir, "verrou2"), true);

            // la même chose par les messages de la page, avec la file de tâches : fs.drop en arrière-plan, puis « réessayer »
            Fs.Queue = new JobQueue();
            try
            {
                string dq = Path.Combine(cp, "dq"); Directory.CreateDirectory(dq);
                File.WriteAllText(Path.Combine(srcDir, "via-file.txt"), "par la file");
                Do($"{{\"cmd\":\"fs.drop\",\"paths\":[{J(Path.Combine(srcDir, "via-file.txt"))}],\"dir\":{J(dq)},\"move\":false,\"tab\":\"t\"}}");
                Check("file de tâches : fs.drop en arrière-plan rend fs.done sans erreur et le fichier est copié", WaitFor(() => replies.Count > 0 && replies.Any(x => x.GetProperty("type").GetString() == "fs.done"), 10000) && File.ReadAllText(Path.Combine(dq, "via-file.txt")) == "par la file");
                var qj = Fs.Queue.Snapshot().Last();
                Check("file de tâches : la tâche porte son titre, son dossier d'origine et de destination, et finit « done »", qj.Kind == "copy" && qj.Title == "via-file.txt" && qj.From == "src" && qj.To == "dq" && qj.State == JobState.Done && qj.Path == Path.Combine(dq, "via-file.txt"));
                Check("file de tâches : le geste est inscrit pour « Annuler » (« Copier »)", Undo.For(IntPtr.Zero).UndoLabel == "Copier");
                string lk2 = Path.Combine(srcDir, "verrou3"); Directory.CreateDirectory(lk2); File.WriteAllText(Path.Combine(lk2, "ok.txt"), "ok"); File.WriteAllText(Path.Combine(lk2, "tenu.txt"), "tenu");
                FileStream hold2 = new FileStream(Path.Combine(lk2, "tenu.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Do($"{{\"cmd\":\"fs.drop\",\"paths\":[{J(lk2)}],\"dir\":{J(dq)},\"move\":false,\"tab\":\"t\"}}");
                bool failed = WaitFor(() => replies.Any(x => x.GetProperty("type").GetString() == "fs.done" && x.TryGetProperty("error", out var ee) && ee.ValueKind == JsonValueKind.String), 10000);
                var fj = Fs.Queue.Snapshot().Last();
                Check("file de tâches : une copie avec un fichier verrouillé finit « échec » avec le message, et sait quoi réessayer", failed && fj.State == JobState.Failed && fj.Error.Contains("tenu.txt") && fj.Failed.Count == 1 && fj.Retry != null);
                hold2.Dispose(); var again = fj.Retry();
                Check("file de tâches : « réessayer » ne refait que l'élément en échec et mène la copie à terme", again != null && WaitFor(() => again.State == JobState.Done, 10000) && File.ReadAllText(Path.Combine(dq, "verrou3", "tenu.txt")) == "tenu");
                Do($"{{\"cmd\":\"fs.deleteperm\",\"paths\":[{J(Path.Combine(dq, "verrou3"))}],\"tab\":\"t\"}}");
                Check("file de tâches : la suppression définitive passe aussi par la file (fichier par fichier) et efface le dossier", WaitFor(() => !Directory.Exists(Path.Combine(dq, "verrou3")), 10000) && Fs.Queue.Snapshot().Last().Kind == "delete" && WaitFor(() => Fs.Queue.Snapshot().Last().State == JobState.Done, 5000));
            }
            finally { Fs.Queue = null; }
            Check("suppression : la racine d'un disque et un chemin relatif sont refusés (rien n'est effacé)", Copier.DeleteMany(new[] { @"C:\", "relatif" }, null) == null && Directory.Exists(@"C:\"));

            // --- lecture annulable d'un dossier ---
            string big2 = Path.Combine(dir, "gros"); Directory.CreateDirectory(big2);
            for (int i = 0; i < 300; i++) File.WriteAllText(Path.Combine(big2, "f" + i + ".txt"), "x");
            using (var cs = new CancellationTokenSource())
            {
                cs.Cancel(); bool cancelled = false;
                try { Fs.List(big2, "t", cs.Token); } catch (OperationCanceledException) { cancelled = true; }
                Check("lecture de dossier : un jeton annulé arrête la lecture (OperationCanceledException)", cancelled);
            }
            Check("lecture de dossier : sans annulation, les 300 fichiers sont rendus", ((dynamic)Fs.List(big2, "t")).items.Count == 300);

            // --- éléments cachés (réglage show.hidden) et lecture par paquets ---
            string hid = Path.Combine(dir, "caches"); Directory.CreateDirectory(hid);
            File.WriteAllText(Path.Combine(hid, "visible.txt"), "v"); File.WriteAllText(Path.Combine(hid, "secret.txt"), "s"); File.SetAttributes(Path.Combine(hid, "secret.txt"), FileAttributes.Hidden);
            JsonElement L(string path) { using var d = JsonDocument.Parse(JsonSerializer.Serialize(Fs.List(path, "t"))); return d.RootElement.Clone(); }
            string Names(JsonElement e) => string.Join(",", e.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("n").GetString()).OrderBy(x => x));
            Settings.Reset(); File.Delete(Settings.FilePath);
            Check("éléments cachés : absents par défaut", Names(L(hid)) == "visible.txt");
            using (var tr = JsonDocument.Parse("true")) Settings.Set("show.hidden", tr.RootElement);
            var withHidden = L(hid);
            Check("réglage show.hidden vrai : présents, et marqués h:true pour que la page les estompe", Names(withHidden) == "secret.txt,visible.txt" && withHidden.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("n").GetString() == "secret.txt").TryGetProperty("h", out var hh) && hh.GetBoolean());
            using (var fl = JsonDocument.Parse("false")) Settings.Set("show.hidden", fl.RootElement);
            Check("réglage show.hidden faux : de nouveau absents", Names(L(hid)) == "visible.txt");
            File.Delete(Settings.FilePath); Settings.Reset();
            var msgs = new List<JsonElement>();
            Fs.ListStream(big2, "t", default, o => { using var d = JsonDocument.Parse(JsonSerializer.Serialize(o)); msgs.Add(d.RootElement.Clone()); }, 100);
            Check("lecture par paquets de 100 : fs.list (more), des fs.list.more, le dernier more=false (vide si le total tombe juste)", msgs.Count >= 3 && msgs[0].GetProperty("type").GetString() == "fs.list" && msgs[0].GetProperty("more").GetBoolean() && msgs.Skip(1).All(m => m.GetProperty("type").GetString() == "fs.list.more") && !msgs[^1].GetProperty("more").GetBoolean() && msgs.Take(msgs.Count - 1).All(m => m.GetProperty("more").GetBoolean()));
            Check("lecture par paquets : 300 éléments en tout, le chemin et l'espace libre dans le premier seulement", msgs.Sum(m => m.GetProperty("items").GetArrayLength()) == 300 && msgs[0].TryGetProperty("free", out _) && !msgs[1].TryGetProperty("free", out _));

            Do($"{{\"cmd\":\"fs.delete\",\"paths\":[{J(Path.Combine(dir, "sous"))},{J(Path.Combine(dir, "b (2).txt"))}],\"tab\":\"t\"}}");
            Check("suppression : dossier et fichier partis (corbeille)", !Directory.Exists(Path.Combine(dir, "sous")) && !File.Exists(Path.Combine(dir, "b (2).txt")));
        }
        catch (Exception ex) { lines.Add("EXCEPTION " + ex); bad++; }
        finally { try { Directory.Delete(dir, true); } catch { } }

        lines.Add(bad == 0 ? "TOUT EST BON" : $"{bad} vérification(s) en échec");
        File.WriteAllLines(Path.Combine(Host.DataDir, "selftest.txt"), lines);
        Environment.ExitCode = bad == 0 ? 0 : 1;
    }


    // cdx-shell.exe --selftest-web : charge ui/_test-preview.html dans un vrai WebView2 caché (hors écran) qui doit afficher une image et
    // un son venant d'un disque via x.drive.cdx.shell, et NE PAS pouvoir les lire par fetch (DenyCors). Résultat dans selftest-web.txt.
    [STAThread]
    public static void RunWeb()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        string dir = Path.Combine(Path.GetTempPath(), "cdx-selftest-web-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        using (var bmp = new Bitmap(8, 6)) { using (var g = Graphics.FromImage(bmp)) g.Clear(Color.Red); bmp.Save(Path.Combine(dir, "image test é.png"), System.Drawing.Imaging.ImageFormat.Png); }
        File.WriteAllBytes(Path.Combine(dir, "son.wav"), Wav(0.3));
        File.WriteAllText(Path.Combine(dir, "texte.txt"), "bonjour");

        string Url(string f) => dir[0].ToString().ToLowerInvariant() + ".drive.cdx.shell/" + string.Join("/", dir[3..].Split('\\').Append(f).Select(Uri.EscapeDataString));
        var results = new List<string>(); var done = false;
        var form = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(300, 200) };
        var web = new WebView2 { Dock = DockStyle.Fill }; form.Controls.Add(web);

        void Finish()
        {
            if (done) return; done = true;
            bool Has(string s) => results.Any(r => r.StartsWith(s));
            var lines = new List<string>
            {
                (Has("img ok") ? "OK     " : "ÉCHEC  ") + "image d'un disque affichée via l'hôte virtuel (nom avec espace et accent)",
                (Has("audio ok") ? "OK     " : "ÉCHEC  ") + "son d'un disque chargé via l'hôte virtuel",
                (Has("fetch bloqué") ? "OK     " : "ÉCHEC  ") + "fetch du même fichier refusé (DenyCors) : la page ne peut pas lire le contenu",
                "        détail : " + string.Join(" | ", results),
            };
            File.WriteAllLines(Path.Combine(Host.DataDir, "selftest-web.txt"), lines);
            Environment.ExitCode = Has("img ok") && Has("audio ok") && Has("fetch bloqué") ? 0 : 1;
            try { Directory.Delete(dir, true); } catch { }
            Application.ExitThread();
        }

        form.Load += async (s, e) =>
        {
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Host.DataDir, "webview-selftest"), new CoreWebView2EnvironmentOptions("--disable-lcd-text"));
                await web.EnsureCoreWebView2Async(env);
                var c = web.CoreWebView2;
                c.SetVirtualHostNameToFolderMapping("cdx.shell", Host.FindWebRoot(), CoreWebView2HostResourceAccessKind.Allow);
                ExplorerForm.MapDrives(c);
                c.WebMessageReceived += (s2, e2) =>
                {
                    using var d = JsonDocument.Parse(e2.WebMessageAsJson); string t = d.RootElement.GetProperty("t").GetString();
                    if (t == "done") Finish(); else results.Add(t);
                };
                c.Navigate("https://cdx.shell/explorer/_test-preview.html?img=" + Uri.EscapeDataString("https://" + Url("image test é.png"))
                    + "&aud=" + Uri.EscapeDataString("https://" + Url("son.wav")) + "&txt=" + Uri.EscapeDataString("https://" + Url("texte.txt")));
            }
            catch (Exception ex) { results.Add("exception " + ex.Message); Finish(); }
        };
        var timer = new System.Windows.Forms.Timer { Interval = 20000 }; timer.Tick += (s, e) => { results.Add("délai dépassé"); Finish(); }; timer.Start();
        form.Show();
        Application.Run();
    }

    // 0,3 s de silence PCM 8 bits mono 8 kHz
    static byte[] Wav(double seconds)
    {
        int n = (int)(8000 * seconds); using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray()); w.Write(36 + n); w.Write("WAVEfmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(8000); w.Write(8000); w.Write((short)1); w.Write((short)8);
        w.Write("data"u8.ToArray()); w.Write(n); for (int i = 0; i < n; i++) w.Write((byte)128);
        return ms.ToArray();
    }

}
