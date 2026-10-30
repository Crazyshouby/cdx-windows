using System.Drawing;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Kdx;

// kdx-shell.exe --selftest-fs : exerce le côté fichiers de l'Explorateur (liste, nouveau dossier, renommer, copier-coller, couper-coller,
// suppression à la corbeille) dans un dossier temporaire, sans fenêtre ni entrée simulée. Le résultat est écrit dans
// %LOCALAPPDATA%\KdxShell\selftest.txt (une ligne par vérification : OK ou ÉCHEC).
static class SelfTest
{
    [STAThread]
    public static void Run()
    {
        var lines = new List<string>(); int bad = 0;
        string dir = Path.Combine(Path.GetTempPath(), "kdx-selftest-" + Guid.NewGuid().ToString("N")[..8]);
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
            replies.Clear(); Fs.DropFiles(Send, new[] { Path.Combine(dir, "ext1.txt") }, cible, false, false, "t");
            Check("déposé sans touche, même disque : déplacé", File.Exists(Path.Combine(cible, "ext1.txt")) && !File.Exists(Path.Combine(dir, "ext1.txt")));
            replies.Clear(); Fs.DropFiles(Send, new[] { Path.Combine(dir, "ext2.txt") }, cible, false, true, "t");
            Check("déposé avec Ctrl : copié (l'original reste)", File.Exists(Path.Combine(cible, "ext2.txt")) && File.Exists(Path.Combine(dir, "ext2.txt")));
            replies.Clear(); File.WriteAllText(Path.Combine(cible, "ext3.txt"), "autre"); Fs.DropFiles(Send, new[] { Path.Combine(dir, "ext3.txt") }, cible, true, false, "t");
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

            Do($"{{\"cmd\":\"fs.delete\",\"paths\":[{J(Path.Combine(dir, "sous"))},{J(Path.Combine(dir, "b (2).txt"))}],\"tab\":\"t\"}}");
            Check("suppression : dossier et fichier partis (corbeille)", !Directory.Exists(Path.Combine(dir, "sous")) && !File.Exists(Path.Combine(dir, "b (2).txt")));
        }
        catch (Exception ex) { lines.Add("EXCEPTION " + ex); bad++; }
        finally { try { Directory.Delete(dir, true); } catch { } }

        lines.Add(bad == 0 ? "TOUT EST BON" : $"{bad} vérification(s) en échec");
        File.WriteAllLines(Path.Combine(Host.DataDir, "selftest.txt"), lines);
        Environment.ExitCode = bad == 0 ? 0 : 1;
    }


    // kdx-shell.exe --selftest-web : charge ui/_test-preview.html dans un vrai WebView2 caché (hors écran) qui doit afficher une image et
    // un son venant d'un disque via x.drive.kdx.shell, et NE PAS pouvoir les lire par fetch (DenyCors). Résultat dans selftest-web.txt.
    [STAThread]
    public static void RunWeb()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        string dir = Path.Combine(Path.GetTempPath(), "kdx-selftest-web-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        using (var bmp = new Bitmap(8, 6)) { using (var g = Graphics.FromImage(bmp)) g.Clear(Color.Red); bmp.Save(Path.Combine(dir, "image test é.png"), System.Drawing.Imaging.ImageFormat.Png); }
        File.WriteAllBytes(Path.Combine(dir, "son.wav"), Wav(0.3));
        File.WriteAllText(Path.Combine(dir, "texte.txt"), "bonjour");

        string Url(string f) => dir[0].ToString().ToLowerInvariant() + ".drive.kdx.shell/" + string.Join("/", dir[3..].Split('\\').Append(f).Select(Uri.EscapeDataString));
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
                c.SetVirtualHostNameToFolderMapping("kdx.shell", Host.FindWebRoot(), CoreWebView2HostResourceAccessKind.Allow);
                ExplorerForm.MapDrives(c);
                c.WebMessageReceived += (s2, e2) =>
                {
                    using var d = JsonDocument.Parse(e2.WebMessageAsJson); string t = d.RootElement.GetProperty("t").GetString();
                    if (t == "done") Finish(); else results.Add(t);
                };
                c.Navigate("https://kdx.shell/explorer/_test-preview.html?img=" + Uri.EscapeDataString("https://" + Url("image test é.png"))
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
