using System.Drawing;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Cdx;

static class SelfTest
{
    // cdx-shell.exe --selftest-sys : lit le son et le réseau, vérifie qu'on peut régler le volume (même valeur) et couper / rétablir le son.
    // Aucune action d'alimentation. Résultat dans selftest-sys.txt.
    public static void RunSys()
    {
        var lines = new List<string>(); int bad = 0;
        void Check(string what, bool ok) { lines.Add((ok ? "OK     " : "ÉCHEC  ") + what); if (!ok) bad++; }
        try
        {
            using var d0 = JsonDocument.Parse(JsonSerializer.Serialize(SysInfo.Center())); var c0 = d0.RootElement;
            int vol = c0.GetProperty("volume").GetInt32(); bool muted = c0.GetProperty("muted").GetBoolean();
            lines.Add($"        lu : volume {vol} %, coupé {muted}, périphérique « {c0.GetProperty("device").GetString()} », réseau {c0.GetProperty("net")}");
            Check("volume lu (0 à 100)", vol >= 0 && vol <= 100);
            Check("réseau : une connexion active trouvée", c0.GetProperty("net").GetProperty("up").GetBoolean());
            SysInfo.SetVolume(vol);
            using var d1 = JsonDocument.Parse(JsonSerializer.Serialize(SysInfo.Center()));
            Check("réglage du volume à la même valeur : inchangé", d1.RootElement.GetProperty("volume").GetInt32() == vol);
            SysInfo.SetMute(!muted);
            using var d2 = JsonDocument.Parse(JsonSerializer.Serialize(SysInfo.Center()));
            Check("coupure du son basculée", d2.RootElement.GetProperty("muted").GetBoolean() == !muted);
            SysInfo.SetMute(muted);
            using var d3 = JsonDocument.Parse(JsonSerializer.Serialize(SysInfo.Center()));
            Check("coupure du son rétablie à l'état d'origine", d3.RootElement.GetProperty("muted").GetBoolean() == muted);
        }
        catch (Exception ex) { lines.Add("EXCEPTION " + ex); bad++; }
        lines.Add(bad == 0 ? "TOUT EST BON" : $"{bad} vérification(s) en échec");
        File.WriteAllLines(Path.Combine(Host.DataDir, "selftest-sys.txt"), lines);
        Environment.ExitCode = bad == 0 ? 0 : 1;
    }


    // cdx-shell.exe --selftest-audio : la liaison avec AudioLinker contre un faux serveur (tube de test, jamais « cdx.audiolinker » : on ne touche pas
    // au vrai AudioLinker). Vérifie : pas de connexion sans serveur, requête d'état à la connexion, état recopié et borné, ligne illisible ignorée,
    // commandes sortantes bien formées et bornées, déconnexion signalée, reconnexion. Résultat dans selftest-audio.txt.
    public static void RunAudio()
    {
        var lines = new List<string>(); int bad = 0;
        void Check(string what, bool ok) { lines.Add((ok ? "OK     " : "ÉCHEC  ") + what); if (!ok) bad++; }
        string name = "cdx.audiolinker-selftest-" + Guid.NewGuid().ToString("N")[..8];
        var got = new List<JsonElement>(); var gl = new object();
        AudioLink link = null;
        bool Until(Func<bool> f, int ms) { var t0 = DateTime.UtcNow; while ((DateTime.UtcNow - t0).TotalMilliseconds < ms) { if (f()) return true; Thread.Sleep(25); } return f(); }
        JsonElement[] Got() { lock (gl) return got.ToArray(); }
        try
        {
            link = new AudioLink(o => { lock (gl) { using var d = JsonDocument.Parse(JsonSerializer.Serialize(o)); got.Add(d.RootElement.Clone()); } }, name);
            link.Start();
            Thread.Sleep(700);
            Check("sans serveur : pas connecté, aucun message", !link.Connected && Got().Length == 0);

            var srv = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
            Check("le serveur reçoit la connexion (nouvelle tentative < 5 s)", srv.WaitForConnectionAsync().Wait(5000));
            var rd = new StreamReader(srv); var wr = new StreamWriter(srv) { AutoFlush = true };
            string Read() { var t = rd.ReadLineAsync(); return t.Wait(3000) ? t.Result : null; }
            string l0 = Read();
            Check("à la connexion, le client demande l'état (audio.get)", l0 != null && l0.Contains("\"audio.get\""));

            wr.WriteLine("{\"type\":\"audio.state\",\"from\":\"audiolinker\",\"data\":{\"version\":\"1\",\"running\":true,\"starting\":false,\"volume\":42,\"muted\":true,\"playing\":\"B\",\"setups\":[{\"name\":\"A\",\"count\":1},{\"name\":\"B\",\"count\":3}]}}");
            Check("état reçu : relayé à la barre", Until(() => Got().Any(g => g.GetProperty("type").GetString() == "audio" && g.GetProperty("on").GetBoolean()), 3000));
            var s1 = Got().Last(g => g.GetProperty("on").GetBoolean());
            Check("état : volume 42, coupé, joué « B », deux setups, en marche",
                  s1.GetProperty("volume").GetInt32() == 42 && s1.GetProperty("muted").GetBoolean() && s1.GetProperty("playing").GetString() == "B"
                  && s1.GetProperty("setups").GetArrayLength() == 2 && s1.GetProperty("running").GetBoolean() && !s1.GetProperty("starting").GetBoolean());

            int before = Got().Length;
            wr.WriteLine("ceci n'est pas du JSON"); wr.WriteLine("{\"type\":\"autre\"}");
            wr.WriteLine("{\"type\":\"audio.state\",\"data\":{\"running\":false,\"volume\":500,\"muted\":false,\"playing\":\"\",\"setups\":[]}}");
            Check("lignes illisibles ou d'un autre type : ignorées ; volume 500 ramené à 100", Until(() => Got().Length == before + 1, 3000) && Got().Last().GetProperty("volume").GetInt32() == 100);

            link.Toggle(); link.Mute(true); link.Volume(37, true); link.Volume(150, false); link.Setup("A"); link.Setup("");
            var cmds = new List<string>(); for (int i = 0; i < 5; i++) cmds.Add(Read());
            Check("commandes : toggle, mute, volume 37 validé, volume 150 ramené à 100, setup A",
                  cmds[0]?.Contains("audio.toggle") == true && cmds[1]?.Contains("\"audio.mute\"") == true && cmds[1].Contains("\"on\":true")
                  && cmds[2]?.Contains("\"v\":37") == true && cmds[2].Contains("\"commit\":true") && cmds[3]?.Contains("\"v\":100") == true && cmds[3].Contains("\"commit\":false")
                  && cmds[4]?.Contains("\"name\":\"A\"") == true);
            var extra = rd.ReadLineAsync();
            Check("un setup sans nom n'est pas envoyé", !extra.Wait(400));

            srv.Dispose();
            Check("serveur fermé : « audio on:false » envoyé à la barre, client déconnecté", Until(() => !link.Connected && Got().Last().GetProperty("on").GetBoolean() == false, 4000));

            var srv2 = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
            Check("reconnexion automatique à un nouveau serveur (< 6 s)", srv2.WaitForConnectionAsync().Wait(6000) && Until(() => link.Connected, 2000));
            srv2.Dispose();
        }
        catch (Exception ex) { lines.Add("EXCEPTION " + ex); bad++; }
        finally { try { link?.Stop(); } catch { } }
        lines.Add(bad == 0 ? "TOUT EST BON" : $"{bad} vérification(s) en échec");
        File.WriteAllLines(Path.Combine(Host.DataDir, "selftest-audio.txt"), lines);
        Environment.ExitCode = bad == 0 ? 0 : 1;
    }

    // cdx-shell.exe --selftest-tasks : le regroupement des fenêtres par application (logique pure, aucune fenêtre réelle).
    public static void RunTasks()
    {
        var lines = new List<string>(); int bad = 0;
        void Check(string what, bool ok) { lines.Add((ok ? "OK     " : "ÉCHEC  ") + what); if (!ok) bad++; }
        TaskWin W(long h, string exe, string title, bool active = false, bool min = false) => new(new IntPtr(h), exe, title, active, min);
        try
        {
            const string chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe", note = @"C:\Windows\notepad.exe", frame = @"C:\Windows\System32\ApplicationFrameHost.exe";
            var g0 = TaskGrouping.Build(new[] { W(1, chrome, "A"), W(2, note, "N"), W(3, chrome, "B"), W(4, chrome, "C") });
            Check("trois fenêtres du même programme : un seul groupe, deux groupes en tout", g0.Count == 2 && g0[0].Windows.Count == 3 && g0[1].Windows.Count == 1);
            Check("le groupe prend la place de sa première fenêtre (Chrome avant Bloc-notes)", g0[0].Windows[0].H == new IntPtr(1) && g0[1].Windows[0].H == new IntPtr(2));
            Check("les fenêtres d'un groupe gardent leur ordre d'apparition", g0[0].Windows.Select(w => w.H.ToInt64()).SequenceEqual(new long[] { 1, 3, 4 }));
            Check("Many : vrai à partir de deux fenêtres", g0[0].Many && !g0[1].Many);

            var g1 = TaskGrouping.Build(new[] { W(1, chrome.ToUpperInvariant(), "A"), W(2, chrome, "B") });
            Check("la casse du chemin ne sépare pas un groupe", g1.Count == 1 && g1[0].Windows.Count == 2);

            var g2 = TaskGrouping.Build(new[] { W(1, frame, "Paramètres"), W(2, frame, "Calculatrice") });
            Check("applis du Store (ApplicationFrameHost) : jamais regroupées", g2.Count == 2 && g2.All(g => !g.Many));
            var g3 = TaskGrouping.Build(new[] { W(1, "", "Élevée 1"), W(2, null, "Élevée 2"), W(3, "", "Élevée 3") });
            Check("programme inconnu (processus élevé) : jamais regroupé", g3.Count == 3);
            Check("clé d'un groupe : le chemin en minuscules ; clé d'une fenêtre isolée : sa poignée", TaskGrouping.KeyOf(W(7, chrome, "x")) == chrome.ToLowerInvariant() && TaskGrouping.KeyOf(W(7, "", "x")) == "h7");

            var a = TaskGrouping.Build(new[] { W(1, chrome, "A", false, true), W(2, chrome, "B", true, false), W(3, chrome, "C", false, true) })[0];
            Check("groupe actif si une fenêtre l'est ; représentant = la fenêtre active", a.Active && a.Representative.H == new IntPtr(2));
            Check("groupe non réduit si une fenêtre ne l'est pas", !a.Min);
            var b = TaskGrouping.Build(new[] { W(1, chrome, "A", false, true), W(2, chrome, "B", false, true) })[0];
            Check("groupe réduit si toutes ses fenêtres le sont ; sans fenêtre active, représentant = la première", b.Min && !b.Active && b.Representative.H == new IntPtr(1));
            Check("aucune fenêtre : aucun groupe", TaskGrouping.Build(Array.Empty<TaskWin>()).Count == 0);
            Check("une fenêtre : un groupe à une fenêtre", TaskGrouping.Build(new[] { W(9, note, "N") }) is { Count: 1 } one && !one[0].Many);
        }
        catch (Exception ex) { lines.Add("EXCEPTION " + ex); bad++; }
        lines.Add(bad == 0 ? "TOUT EST BON" : $"{bad} vérification(s) en échec");
        File.WriteAllLines(Path.Combine(Host.DataDir, "selftest-tasks.txt"), lines);
        Environment.ExitCode = bad == 0 ? 0 : 1;
    }

    // cdx-shell.exe --selftest-icons : l'icône d'un raccourci sans la flèche. Un raccourci jetable vers notepad.exe : avec ResolveShortcuts, son icône est exactement
    // celle de notepad.exe ; sans, elle en diffère (flèche). Raccourci vers une cible disparue : une icône quand même (repli sur celle du .lnk).
    [STAThread]
    public static void RunIcons()
    {
        var lines = new List<string>(); int bad = 0;
        void Check(string what, bool ok) { lines.Add((ok ? "OK     " : "ÉCHEC  ") + what); if (!ok) bad++; }
        string dir = Path.Combine(Path.GetTempPath(), "cdx-icons-test-" + Environment.ProcessId); Directory.CreateDirectory(dir);
        try
        {
            string exe = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            string Make(string name, string target, string icon = null)
            {
                var t = Type.GetTypeFromProgID("WScript.Shell"); var sh = Activator.CreateInstance(t);
                string p = Path.Combine(dir, name + ".lnk");
                var sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, sh, new object[] { p });
                void Set(string n, string v) => sc.GetType().InvokeMember(n, System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { v });
                Set("TargetPath", target); if (icon != null) Set("IconLocation", icon);
                sc.GetType().InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sc); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sh);
                return p;
            }
            string lnk = Make("bloc", exe), lnk2 = Make("icone", exe, exe + ",0"), gone = Make("disparu", Path.Combine(dir, "plus-la.exe"));

            // La flèche de raccourci occupe le coin bas-gauche : on compte les pixels différents de celui du programme dans ce quart de l'image.
            double Corner(string a, string b)
            {
                using var ba = new Bitmap(new MemoryStream(Convert.FromBase64String(a))); using var bb = new Bitmap(new MemoryStream(Convert.FromBase64String(b)));
                int n = 0, tot = 0, h = ba.Height, w = ba.Width;
                for (int y = h / 2; y < h; y++) for (int x = 0; x < w / 2; x++) { tot++; var p = ba.GetPixel(x, y); var q = bb.GetPixel(x, y); if (Math.Abs(p.A - q.A) + Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B) > 60) n++; }
                return (double)n / tot;
            }
            Host.ResolveShortcuts = false;
            string withArrow = Host.IconPng(lnk, 32), plain = Host.IconPng(exe, 32);
            double dArrow = withArrow != null && plain != null ? Corner(withArrow, plain) : 0;
            Check($"sans résolution : le coin de l'icône du .lnk diffère de celle du programme ({dArrow:P0} des pixels : la flèche)", dArrow > 0.15);
            Host.ResolveShortcuts = true;
            string clean = Host.IconPng(lnk, 40), plain40 = Host.IconPng(exe, 40);
            double dClean = clean != null ? Corner(clean, plain40) : 1;
            Check($"avec résolution : le coin est celui du programme ({dClean:P0} de différence), sans flèche", clean != null && dClean < 0.05);
            string clean2 = Host.IconPng(lnk2, 40);
            double dClean2 = clean2 != null ? Corner(clean2, plain40) : 1;
            Check($"raccourci avec IconLocation explicite (fichier,0) : même icône ({dClean2:P0} de différence)", clean2 != null && dClean2 < 0.05);
            string miss = Host.IconPng(gone, 20);
            Check("raccourci vers une cible disparue : une icône quand même (repli)", miss != null && miss.Length > 100);
            Check("fichier .lnk inexistant : null, aucune exception", Host.IconPng(Path.Combine(dir, "rien.lnk"), 20) == null);
            Check("un programme (.exe) n'est pas touché par la résolution", Host.IconPng(exe, 24) != null);
        }
        catch (Exception ex) { lines.Add("EXCEPTION " + ex); bad++; }
        try { Directory.Delete(dir, true); } catch { }
        lines.Add(bad == 0 ? "TOUT EST BON" : $"{bad} vérification(s) en échec");
        File.WriteAllLines(Path.Combine(Host.DataDir, "selftest-icons.txt"), lines);
        Environment.ExitCode = bad == 0 ? 0 : 1;
    }

    // cdx-shell.exe --selftest-pins : le magasin des applications épinglées, sur un fichier temporaire (jamais le vrai epingles.json).
    // Valeurs par défaut, ajout à la barre (nouvelle épingle, tuile existante, doublon), retrait (la tuile reste, l'épingle seule disparaît),
    // déplacement, relecture après « redémarrage », cibles refusées, fichier abîmé ou d'une autre version, écriture atomique, plafond.
    public static void RunPins()
    {
        var lines = new List<string>(); int bad = 0;
        void Check(string what, bool ok) { lines.Add((ok ? "OK     " : "ÉCHEC  ") + what); if (!ok) bad++; }
        string dir = Path.Combine(Path.GetTempPath(), "cdx-pins-test-" + Environment.ProcessId); Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "epingles.json"), exe = Path.Combine(dir, "essai.exe"), exe2 = Path.Combine(dir, "autre.exe");
        File.WriteAllText(exe, "x"); File.WriteAllText(exe2, "x");
        try
        {
            var s = new PinStore(file);
            Check("fichier absent : 14 épingles par défaut (dont Notes, Terminal CDX, Agenda, Courrier et Pilote), 5 dans la barre", s.Items.Count == 14 && s.OnBar.Count == 5 && s.Items.All(p => p.Tile));
            Check("la barre commence par l'Explorateur et finit par le Navigateur CDX", s.OnBar[0].Target == "cdx:explorer" && s.OnBar[^1].Target == "cdx:navigateur");

            Check("ajout d'un fichier existant", s.AddToBar("Essai", exe) && s.OnBar.Count == 6 && s.OnBar[^1].Target == exe);
            Check("la nouvelle épingle n'est pas une tuile du menu Démarrer", !s.Find(exe).Tile);
            Check("doublon (autre casse) : pas de seconde épingle", s.AddToBar("Essai bis", exe.ToUpperInvariant()) && s.Items.Count(p => string.Equals(p.Target, exe, StringComparison.OrdinalIgnoreCase)) == 1);
            Check("ajout d'une tuile existante à la barre : même entrée, Bar vrai", s.AddToBar("Calculatrice", "calc.exe") && s.Items.Count(p => p.Target == "calc.exe") == 1 && s.Find("calc.exe").Bar && s.Find("calc.exe").Tile);

            Check("écriture atomique : aucun .tmp ne reste", File.Exists(file) && !File.Exists(file + ".tmp"));
            var s2 = new PinStore(file);
            Check("relecture : même ordre, mêmes drapeaux", s2.Items.Select(p => p.Target + p.Bar + p.Tile).SequenceEqual(s.Items.Select(p => p.Target + p.Bar + p.Tile)));

            Check("retrait d'une tuile : elle reste dans le menu Démarrer", s.RemoveFromBar("calc.exe") && s.Find("calc.exe") is { Bar: false, Tile: true });
            Check("retrait d'une épingle de la barre seule : elle disparaît", s.RemoveFromBar(exe) && s.Find(exe) == null);
            Check("retrait d'une application qui n'est pas dans la barre : refusé", !s.RemoveFromBar("calc.exe") && !s.RemoveFromBar("inconnu.exe"));

            s.AddToBar("Autre", exe2);
            var before = s.OnBar.Select(p => p.Target).ToList();
            Check("déplacement vers la gauche", s.Move(exe2, -1) && s.OnBar[^2].Target == exe2 && s.OnBar[^1].Target == before[^2]);
            Check("déplacement vers la droite", s.Move(exe2, 1) && s.OnBar[^1].Target == exe2);
            Check("déplacement au-delà des bords ou de plus d'un cran : refusé", !s.Move(exe2, 1) && !s.Move("cdx:explorer", -1) && !s.Move("cdx:explorer", 2) && !s.Move("inconnu.exe", 1));
            Check("les tuiles hors barre n'ont pas bougé de rang", s.Items.Select(p => p.Target).Intersect(new[] { "cdx:repos", "calc.exe", "notepad.exe", "shell:My Pictures" }).SequenceEqual(new[] { "cdx:repos", "calc.exe", "notepad.exe", "shell:My Pictures" }));

            // glisser-déposer : réordonner, insérer devant une épingle, ordre des tuiles indépendant
            var tilesBefore = s.Items.Where(p => p.Tile).Select(p => p.Target).ToList();
            Check("glisser une épingle devant une autre", s.MoveTo("cdx:navigateur", "cdx:explorer") && s.OnBar[0].Target == "cdx:navigateur" && s.OnBar[1].Target == "cdx:explorer");
            Check("rang dans la barre (envoyé à la page pour l'afficher dans le bon ordre)", s.OnBar.Select((p, i) => s.BarIndex(p.Target) == i).All(x => x) && s.BarIndex("calc.exe") == -1);
            Check("glisser une épingle à la fin (devant « rien »)", s.MoveTo("cdx:navigateur", "") && s.OnBar[^1].Target == "cdx:navigateur");
            Check("glisser à sa place actuelle ou sur soi-même : sans effet", !s.MoveTo("cdx:navigateur", "") && !s.MoveTo("cdx:explorer", "cdx:explorer"));
            Check("glisser devant une épingle qui n'est pas dans la barre ou inconnue : refusé", !s.MoveTo("cdx:explorer", "calc.exe") && !s.MoveTo("inconnu.exe", "cdx:explorer") && !s.MoveTo("cdx:explorer", "inconnu.exe"));
            Check("réordonner la barre ne change pas l'ordre des tuiles du menu Démarrer", s.Items.Where(p => p.Tile).Select(p => p.Target).SequenceEqual(tilesBefore));
            Check("épingle nouvelle déposée devant une autre", s.AddToBar("Insérée", exe, null, "wt.exe") && s.OnBar.FindIndex(p => p.Target == exe) == s.OnBar.FindIndex(p => p.Target == "wt.exe") - 1);
            Check("épingle déjà présente redéposée devant une autre : déplacée, pas dupliquée", s.AddToBar("Insérée", exe, null, "cdx:explorer") && s.OnBar[0].Target == exe && s.Items.Count(p => p.Target == exe) == 1);
            s.RemoveFromBar(exe);
            Check("tuile hors barre déposée devant une épingle", s.AddToBar("Calculatrice", "calc.exe", null, "cdx:explorer") && s.OnBar[0].Target == "calc.exe"); s.RemoveFromBar("calc.exe");
            var s5 = new PinStore(file);
            Check("l'ordre de la barre survit au redémarrage", s5.OnBar.Select(p => p.Target).SequenceEqual(s.OnBar.Select(p => p.Target)));
            File.WriteAllText(Path.Combine(dir, "a.lnk"), "x"); File.WriteAllText(Path.Combine(dir, "a.cmd"), "x");
            Check("fichiers déposés : programme, raccourci, script, dossier acceptés", PinStore.Pinnable(exe) && PinStore.Pinnable(dir) && PinStore.Pinnable(Path.Combine(dir, "a.lnk")) && PinStore.Pinnable(Path.Combine(dir, "a.cmd")));
            File.WriteAllText(Path.Combine(dir, "note.txt"), "x");
            Check("fichiers déposés : document, chemin inexistant ou relatif refusés", !PinStore.Pinnable(Path.Combine(dir, "note.txt")) && !PinStore.Pinnable(Path.Combine(dir, "nexiste.exe")) && !PinStore.Pinnable("a.exe"));

            Check("cible inexistante refusée", !s.AddToBar("Fantôme", Path.Combine(dir, "nexiste-pas.exe")));
            Check("chemin relatif refusé", !s.AddToBar("Rel", @"dossier\a.exe"));
            Check("caractère de contrôle refusé", !s.AddToBar("Ctl", "cdx:explorer\n") && !s.AddToBar("Nom\u0001", exe));
            Check("nom vide ou de plus de 80 caractères refusé", !s.AddToBar("  ", exe) && !s.AddToBar(new string('x', 81), exe));
            Check("cible de plus de 520 caractères refusée", !s.AddToBar("Long", "cdx:" + new string('x', 520)));
            Check("schémas connus et programmes nus acceptés", PinStore.Valid("A", "ms-settings:network") && PinStore.Valid("B", "shell:Downloads") && PinStore.Valid("C", "mmc.exe") && !PinStore.Valid("D", "http://exemple.org") && !PinStore.Valid("E", "cmd.exe /c calc"));

            File.WriteAllText(file, "{{{ abîmé");
            var s3 = new PinStore(file);
            Check("fichier abîmé : valeurs par défaut, aucune exception", s3.Items.Count == 14 && s3.OnBar.Count == 5);
            File.WriteAllText(file, "{\"version\":2,\"pins\":[]}");
            Check("version inconnue : valeurs par défaut", new PinStore(file).Items.Count == 14);
            string allSeen = "\"seen\":[" + string.Join(",", PinStore.Defaults.Select(d => "\"" + d.Target + "\"")) + "],";   // toutes les épingles par défaut déjà proposées : rien à ajouter
            File.WriteAllText(file, "{\"version\":1," + allSeen + "\"pins\":[{\"name\":\"Bon\",\"target\":\"cdx:repos\",\"bar\":true,\"tile\":false},{\"name\":\"Mauvais\",\"target\":\"C:\\\\nexiste\\\\pas.exe\",\"bar\":true},{\"name\":\"Doublon\",\"target\":\"CDX:REPOS\",\"bar\":true},{\"name\":\"Rien\",\"target\":\"cdx:explorer\"},42]}");
            var s4 = new PinStore(file);
            Check("lecture tolérante : l'entrée valide seule est gardée (cible inexistante, doublon, ni barre ni tuile, élément inattendu écartés)", s4.Items.Count == 1 && s4.Items[0].Name == "Bon" && s4.Items[0].Bar && !s4.Items[0].Tile);
            File.WriteAllText(file, "{\"version\":1," + allSeen + "\"pins\":[]}");
            Check("liste vide enregistrée : elle reste vide (pas de retour aux valeurs par défaut)", new PinStore(file).Items.Count == 0);

            // une nouvelle application de la suite (Notes) : sa tuile est proposée une fois aux fichiers déjà écrits, sans rien changer d'autre
            string legacy = Path.Combine(dir, "ancien.json");
            File.WriteAllText(legacy, "{\"version\":1,\"pins\":[{\"name\":\"Explorateur\",\"target\":\"cdx:explorer\",\"ic\":\"dossier\",\"bar\":true,\"tile\":true,\"barIndex\":0},{\"name\":\"Calculatrice\",\"target\":\"calc.exe\",\"ic\":\"apps\",\"bar\":false,\"tile\":true,\"barIndex\":-1}]}");
            var lg = new PinStore(legacy);
            Check("ancien fichier (sans « seen ») : seules les tuiles Notes, Terminal CDX, Agenda, Courrier et Pilote sont ajoutées (hors barre), les neuf d'origine ne reviennent pas", lg.Items.Select(p => p.Target).SequenceEqual(new[] { "cdx:explorer", "calc.exe", "cdx:notes", "cdx:terminal", "cdx:agenda", "cdx:courrier", "cdx:pilote" }) && lg.Find("cdx:notes") is { Tile: true, Bar: false } && lg.Find("cdx:terminal") is { Tile: true, Bar: false } && lg.OnBar.Count == 1);
            Check("la migration est écrite : « seen » contient cdx:notes, une relecture ne la refait pas", File.ReadAllText(legacy).Contains("\"seen\"") && File.ReadAllText(legacy).Contains("cdx:notes") && File.ReadAllText(legacy).Contains("cdx:terminal") && File.ReadAllText(legacy).Contains("cdx:agenda") && File.ReadAllText(legacy).Contains("cdx:courrier") && File.ReadAllText(legacy).Contains("cdx:pilote") && new PinStore(legacy).Items.Count == 7);
            File.WriteAllText(legacy, "{\"version\":1," + allSeen + "\"pins\":[{\"name\":\"Explorateur\",\"target\":\"cdx:explorer\",\"bar\":true,\"tile\":true,\"barIndex\":0}]}");
            Check("tuile Notes retirée par l'utilisateur (déjà dans « seen ») : elle ne revient pas", new PinStore(legacy).Items.Count == 1);

            var big = new PinStore(Path.Combine(dir, "grand.json")); big.Items.Clear();
            int added = 0; for (int i = 0; i < PinStore.Max + 5; i++) if (big.AddToBar("App" + i, "cdx:app" + i)) added++;
            Check($"plafond de {PinStore.Max} épingles", added == PinStore.Max && big.Items.Count == PinStore.Max);
        }
        catch (Exception ex) { lines.Add("EXCEPTION " + ex); bad++; }
        try { Directory.Delete(dir, true); } catch { }
        lines.Add(bad == 0 ? "TOUT EST BON" : $"{bad} vérification(s) en échec");
        File.WriteAllLines(Path.Combine(Host.DataDir, "selftest-pins.txt"), lines);
        Environment.ExitCode = bad == 0 ? 0 : 1;
    }

    // cdx-shell.exe --selftest-tray : le relais de la zone de notification, avec une vraie icône (NotifyIcon de WinForms) inscrite par le chemin normal
    // (Shell_NotifyIcon → FindWindow « Shell_TrayWnd »). Vérifie : le relais est trouvé en premier, icône reçue avec son image et son infobulle, infobulle
    // changée, clics gauche / double / droit rendus à l'appli, bulle transformée en notification, retrait, message mal formé ignoré, messages de barre
    // d'application renvoyés à la vraie barre (même position avant et pendant), relais retiré à l'arrêt. Aucune diffusion de « TaskbarCreated » avant la fin.
    [STAThread]
    public static void RunTray()
    {
        var lines = new List<string>(); int bad = 0;
        void Check(string what, bool ok) { lines.Add((ok ? "OK     " : "ÉCHEC  ") + what); if (!ok) bad++; }
        TrayRelay relay = null; NotifyIcon ni = null;
        try
        {
            Application.EnableVisualStyles();
            const uint ABM_GETTASKBARPOS = 5;
            (bool ok, RECT rc) TaskbarPos()
            {
                var abd = new APPBARDATA { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>() };
                bool r = N.SHAppBarMessage(ABM_GETTASKBARPOS, ref abd) != UIntPtr.Zero;
                return (r, abd.rc);
            }
            // Un shell déjà lancé tient son propre relais, qui refuse (à raison) les messages de barre d'application : les contrôles de position sont alors sautés.
            // Il reprend la première place toutes les 500 ms : le relais d'essai se ferait doubler en plein test. Le test attend donc qu'aucun shell ne tourne.
            bool liveShell = N.FindWindow("Shell_TrayWnd", null) != TrayRelay.ExplorerTray();
            if (liveShell)
            {
                lines.Add("SAUTÉ  un shell tourne déjà (son relais est en place) : ce test doit être lancé quand le shell est arrêté");
                File.WriteAllLines(Path.Combine(Host.DataDir, "selftest-tray.txt"), lines);
                return;
            }
            var before = TaskbarPos();
            Check($"référence : la barre de Windows répond (position {before.rc.Left},{before.rc.Top} {before.rc.Right},{before.rc.Bottom})", before.ok && before.rc.Right > before.rc.Left);

            var balloons = new List<string>();
            relay = new TrayRelay { OnBalloon = (t, x, e) => balloons.Add(t + "|" + x) };
            var scr = Screen.PrimaryScreen.Bounds;
            Check("relais créé", relay.Create(new Rectangle(scr.Left, scr.Bottom - 36, scr.Width, 36)));
            Check("FindWindow(Shell_TrayWnd) trouve le relais en premier", N.FindWindow("Shell_TrayWnd", null) == relay.Handle);
            Check("la vraie barre de Windows reste trouvable (autre processus)", TrayRelay.ExplorerTray() != IntPtr.Zero && TrayRelay.ExplorerTray() != relay.Handle);

            bool Until(Func<bool> f, int ms) { var t0 = DateTime.UtcNow; while ((DateTime.UtcNow - t0).TotalMilliseconds < ms) { Application.DoEvents(); relay.Flush(); if (f()) return true; Thread.Sleep(20); } Application.DoEvents(); relay.Flush(); return f(); }
            string me = System.Diagnostics.Process.GetCurrentProcess().ProcessName, tip1 = "CDX essai " + Environment.ProcessId;
            string KeyOf(string tip) { foreach (var o in relay.Items()) { using var d = JsonDocument.Parse(JsonSerializer.Serialize(o)); if (d.RootElement.GetProperty("tip").GetString() == tip) return d.RootElement.GetProperty("k").GetString(); } return null; }
            string PngOf(string tip) { foreach (var o in relay.Items()) { using var d = JsonDocument.Parse(JsonSerializer.Serialize(o)); if (d.RootElement.GetProperty("tip").GetString() == tip) return d.RootElement.GetProperty("png").GetString(); } return null; }

            var mouse = new List<string>();
            ni = new NotifyIcon { Icon = SystemIcons.Information, Text = tip1, Visible = true };
            ni.MouseUp += (s, e) => mouse.Add("up" + e.Button);
            ni.MouseDoubleClick += (s, e) => mouse.Add("double" + e.Button);
            Check("icône inscrite par le chemin normal : reçue avec son programme", Until(() => relay.Describe().Any(d => d.Contains(" " + me + " ") && d.Contains(tip1)), 3000));
            Check("icône affichée, avec une image PNG", Until(() => KeyOf(tip1) != null, 3000) && (PngOf(tip1) ?? "").Length > 100);

            ni.Text = tip1 + " bis";
            Check("infobulle changée", Until(() => KeyOf(tip1 + " bis") != null, 3000));
            string key = KeyOf(tip1 + " bis");

            Check("clic gauche renvoyé à l'appli", key != null && relay.Click(key, "l") && Until(() => mouse.Contains("upLeft"), 3000));
            Check("clic droit renvoyé à l'appli", relay.Click(key, "r") && Until(() => mouse.Contains("upRight"), 3000));
            Check("double-clic renvoyé à l'appli", relay.Click(key, "d") && Until(() => mouse.Contains("doubleLeft"), 3000));
            Check("clic sur une icône inconnue : refusé", !relay.Click("0:0", "l") && !relay.Click(key, "x"));

            ni.ShowBalloonTip(1000, "Titre essai", "Texte essai", ToolTipIcon.Info);
            Check("bulle de l'appli : remise comme notification", Until(() => balloons.Contains("Titre essai|Texte essai"), 3000));

            // un message de zone de notification tronqué (4 octets) : ignoré, sans exception
            int n0 = relay.Count;
            var mem = System.Runtime.InteropServices.Marshal.AllocHGlobal(32); var data = System.Runtime.InteropServices.Marshal.AllocHGlobal(8);
            System.Runtime.InteropServices.Marshal.WriteIntPtr(mem, 0, (IntPtr)1); System.Runtime.InteropServices.Marshal.WriteInt32(mem, IntPtr.Size, 4); System.Runtime.InteropServices.Marshal.WriteIntPtr(mem, IntPtr.Size * 2, data);
            var bad1 = N.SendMessage(relay.Handle, 0x4A, IntPtr.Zero, mem);
            System.Runtime.InteropServices.Marshal.FreeHGlobal(mem); System.Runtime.InteropServices.Marshal.FreeHGlobal(data);
            Check("message tronqué : refusé, aucune icône ajoutée", bad1 == IntPtr.Zero && relay.Count == n0);

            var during = TaskbarPos();
            Check("message de barre d'application pendant que le relais est en tête : échec franc (pas de rectangle trompeur)", !during.ok);
            relay.Suspend();
            var held = TaskbarPos();
            relay.Resume();
            if (!liveShell) Check($"relais en retrait : le message atteint la vraie barre, même position ({held.rc.Left},{held.rc.Top} {held.rc.Right},{held.rc.Bottom})",
                  held.ok && held.rc.Left == before.rc.Left && held.rc.Top == before.rc.Top && held.rc.Right == before.rc.Right && held.rc.Bottom == before.rc.Bottom);
            Check("relais remis en tête après le retrait", N.FindWindow("Shell_TrayWnd", null) == relay.Handle);

            ni.Visible = false;
            Check("icône retirée", Until(() => KeyOf(tip1 + " bis") == null, 3000));

            var h = relay.Handle; relay.Shutdown();
            Check("relais retiré : FindWindow ne le trouve plus", N.FindWindow("Shell_TrayWnd", null) != h && !N.IsWindow(h) && TrayRelay.Current == null);
            relay = null;
            var after = TaskbarPos();
            if (!liveShell) Check("la barre de Windows répond encore après le retrait", after.ok && after.rc.Left == before.rc.Left && after.rc.Bottom == before.rc.Bottom);
        }
        catch (Exception ex) { lines.Add("EXCEPTION " + ex); bad++; }
        finally { try { if (ni != null) { ni.Visible = false; ni.Dispose(); } } catch { } try { relay?.Shutdown(); } catch { } }
        lines.Add(bad == 0 ? "TOUT EST BON" : $"{bad} vérification(s) en échec");
        File.WriteAllLines(Path.Combine(Host.DataDir, "selftest-tray.txt"), lines);
        Environment.ExitCode = bad == 0 ? 0 : 1;
    }

    // cdx-shell.exe --selftest-snap : l'aimantation des fenêtres (Cdx.Host/Snap.cs, logique pure, aucune fenêtre réelle) : les mêmes cas que KIRC
    // (app.rs, tests snap_rect / snap_size), plus les bords gauche et haut et la taille minimale. B = une fenêtre voisine en (500,100) de 300 x 200.
    public static void RunSnap()
    {
        var lines = new List<string>(); int bad = 0;
        void Check(string what, bool ok) { lines.Add((ok ? "OK     " : "ÉCHEC  ") + what); if (!ok) bad++; }
        try
        {
            var one = new[] { new Rectangle(500, 100, 300, 200) }; var none = Array.Empty<Rectangle>(); Rectangle? screen = new Rectangle(0, 0, 1920, 1040);
            Point Mv(int x, int y, int w = 200, int h = 100, Rectangle[] o = null, Rectangle? a = null) => Snap.Move(new Rectangle(x, y, w, h), o ?? one, a, 10, 2);
            Check("déplacement : notre bord droit colle au bord gauche de B, 2 px d'écart", Mv(294, 120) == new Point(298, 120));
            Check("notre bord gauche colle au bord droit de B", Mv(805, 120) == new Point(802, 120));
            Check("sous B, 2 px d'écart", Mv(520, 304) == new Point(520, 302));
            Check("au-dessus de B", Mv(520, -4) == new Point(520, -2));
            Check("bords gauches alignés", Mv(503, 130) == new Point(500, 130));
            Check("trop loin : rien ne bouge", Mv(100, 120) == new Point(100, 120));
            Check("pas en face sur l'autre axe : rien ne bouge", Mv(490, 600) == new Point(490, 600));
            Check("bords de l'écran : sans écart", Mv(7, 6, 200, 100, none, screen) == new Point(0, 0) && Mv(1715, 935, 200, 100, none, screen) == new Point(1720, 940));
            Check("le bord le plus proche gagne (une fenêtre à 2 px plutôt que l'écran à 4)", Mv(4, 120, 200, 100, new[] { new Rectangle(204, 100, 300, 200) }, screen) == new Point(2, 120));
            Check("ni voisine ni écran : la position proposée", Mv(3, 3, 200, 100, none, null) == new Point(3, 3));

            Rectangle Sz(int x, int y, int w, int h, int edge = 8, Rectangle[] o = null, Rectangle? a = null) => Snap.Size(new Rectangle(x, y, w, h), edge, o ?? one, a, 10, 2);
            Check("redimensionner par le coin bas-droite : le bord droit s'arrête à 2 px de B", Sz(100, 120, 396, 100) == new Rectangle(100, 120, 398, 100));
            Check("loin de tout : la taille proposée", Sz(100, 120, 200, 500) == new Rectangle(100, 120, 200, 500));
            Check("bords droits alignés", Sz(100, 120, 697, 100) == new Rectangle(100, 120, 700, 100));
            Check("bords de l'écran", Sz(100, 120, 1815, 915, 8, none, screen) == new Rectangle(100, 120, 1820, 920));
            Check("rien à portée : rien ne change", Sz(100, 120, 300, 300, 8, one, screen) == new Rectangle(100, 120, 300, 300));
            Check("bord gauche tiré : il colle au bord droit de B, les autres bords ne bougent pas", Sz(804, 120, 200, 100, 1) == Rectangle.FromLTRB(802, 120, 1004, 220));
            Check("bord haut tiré : il s'arrête sous B", Sz(520, 304, 200, 100, 3) == Rectangle.FromLTRB(520, 302, 720, 404));
            Check("bord bas tiré, sans toucher à la largeur", Sz(520, 20, 200, 77, 6) == new Rectangle(520, 20, 200, 78));
            Check("taille minimale respectée", Snap.Size(new Rectangle(100, 100, 30, 30), 8, none, null, 10, 2, 50, 40) == new Rectangle(100, 100, 50, 40));
        }
        catch (Exception ex) { lines.Add("EXCEPTION " + ex); bad++; }
        lines.Add(bad == 0 ? "TOUT EST BON" : $"{bad} vérification(s) en échec");
        File.WriteAllLines(Path.Combine(Host.DataDir, "selftest-snap.txt"), lines);
        Environment.ExitCode = bad == 0 ? 0 : 1;
    }

    // cdx-shell.exe --selftest-bus : le bus et les réglages communs, sur un tube de test (préfixe CDX_PIPE_PREFIX) et un dossier temporaire (CDX_SUITE_DIR) :
    // jamais le vrai bus ni les vrais réglages. Message valide, type inconnu, champ invalide, ligne illisible, ligne de plus de 64 Ko, deux connexions,
    // puis suite.json (lecture, écriture atomique, fichier abîmé, version inconnue, surveillance). Résultat dans selftest-bus.txt.
    [STAThread]
    public static void RunBus()
    {
        var lines = new List<string>(); int bad = 0;
        void Check(string what, bool ok) { lines.Add((ok ? "OK     " : "ÉCHEC  ") + what); if (!ok) bad++; }
        string pid = Environment.ProcessId.ToString();
        string dir = Path.Combine(Path.GetTempPath(), "cdx-suite-test-" + pid); Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("CDX_PIPE_PREFIX", "cdxtest" + pid + ".");
        Environment.SetEnvironmentVariable("CDX_SUITE_DIR", dir);
        var anchor = new Control(); _ = anchor.Handle;
        var accepted = new List<string>(); int refused = 0;
        var accents = new List<string>();
        IDisposable watch = null;
        Bus.Listen("shell", (type, from, data) =>
        {
            // la même liste blanche que le shell : notify { title <= 80, text <= 400, level }
            string title = data.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (type == "notify" && !string.IsNullOrEmpty(title) && title.Length <= 80) { accepted.Add(from + "|" + title); return true; }
            refused++; return false;
        });
        watch = Suite.Watch("rouge", a => accents.Add(a));

        var worker = Task.Run(() =>
        {
            try
            {
                bool Until(Func<bool> f, int ms) { var t0 = DateTime.UtcNow; while ((DateTime.UtcNow - t0).TotalMilliseconds < ms) { if (f()) return true; Thread.Sleep(20); } return f(); }
                string pipe = "cdxtest" + pid + ".shell";
                Check("message valide envoyé et reçu", Bus.Send("shell", "notify", new { title = "Bonjour", text = "x" }, "kirc") && Until(() => accepted.Contains("kirc|Bonjour"), 3000));
                int r0 = refused;
                Bus.Send("shell", "ouvre-le-shell", new { cmd = "calc.exe" }, "kirc");
                Check("type inconnu : refusé, jamais exécuté", Until(() => refused == r0 + 1, 3000));
                Bus.Send("shell", "notify", new { title = new string('x', 81) }, "kirc");
                Check("champ invalide (titre de 81 caractères) : refusé", Until(() => refused == r0 + 2, 3000));
                Check("destinataire inconnu : false, rien d'écrit", !Bus.Send("nimporte", "notify", new { title = "a" }));

                // une connexion brute : ligne illisible, puis messages valides sur la même connexion
                using (var c = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.Out))
                {
                    c.Connect(2000);
                    void W(string s) { var b = Encoding.UTF8.GetBytes(s); c.Write(b, 0, b.Length); c.Flush(); }
                    int n0 = accepted.Count;
                    W("ceci n'est pas du JSON\n[1,2]\n{\"sans\":\"type\"}\n");
                    W(Bus.Compose("notify", "essai", new { title = "Après le bruit" }) + "\n");
                    Check("lignes illisibles, tableau, objet sans type : ignorés, la connexion continue", Until(() => accepted.Count == n0 + 1 && accepted[^1] == "essai|Après le bruit", 3000));
                }

                // deux connexions simultanées
                using (var a = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.Out))
                using (var b = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.Out))
                {
                    a.Connect(2000); b.Connect(2000);
                    int n0 = accepted.Count;
                    void W(System.IO.Pipes.NamedPipeClientStream s, string x) { var by = Encoding.UTF8.GetBytes(Bus.Compose("notify", x, new { title = x }) + "\n"); s.Write(by, 0, by.Length); s.Flush(); }
                    W(a, "A1"); W(b, "B1"); W(a, "A2"); W(b, "B2");
                    Check("deux connexions simultanées : les quatre messages arrivent", Until(() => accepted.Count == n0 + 4, 3000) && accepted.Contains("A2|A2") && accepted.Contains("B2|B2"));
                }

                // une ligne de plus de 64 Ko sans saut de ligne : la connexion est coupée
                using (var c = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.Out))
                {
                    c.Connect(2000);
                    bool cut = false;
                    try { var big = new byte[70000]; Array.Fill(big, (byte)'x'); c.Write(big, 0, big.Length); c.Flush(); for (int i = 0; i < 50 && !cut; i++) { Thread.Sleep(100); try { c.Write(new byte[] { (byte)'y' }, 0, 1); c.Flush(); } catch (IOException) { cut = true; } } }
                    catch (IOException) { cut = true; }
                    Check("ligne de plus de 64 Ko : connexion coupée par le serveur", cut);
                }
                int n1 = accepted.Count;
                Bus.Send("shell", "notify", new { title = "Encore là" }, "kirc");
                Check("le serveur sert encore après la coupure", Until(() => accepted.Count == n1 + 1, 3000));

                // droits d'un tube créé comme le fait Bus.Listen : l'utilisateur courant seulement
                using (var acl = new System.IO.Pipes.NamedPipeServerStream("cdxtest" + pid + ".acl", System.IO.Pipes.PipeDirection.In, 8, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly))
                {
                    var rules = System.IO.Pipes.PipesAclExtensions.GetAccessControl(acl).GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)).Cast<System.Security.AccessControl.AuthorizationRule>().Select(r => r.IdentityReference.Value).Distinct().ToList();
                    // CurrentUserOnly accorde le tube au propriétaire du jeton : l'utilisateur, ou le groupe Administrateurs quand le processus est élevé (CI).
                    var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                    string me = id.User.Value, owner = id.Owner?.Value;
                    Check($"droits du tube : l'utilisateur courant seulement ({string.Join(", ", rules)})", rules.Count == 1 && (rules[0] == me || rules[0] == owner));
                }

                // ---- suite.json
                Check("suite.json absent : rouge", Suite.ReadAccent() == "rouge");
                Suite.WriteAccent("jean");
                Check("écriture puis lecture : jean", Suite.ReadAccent() == "jean");
                Check("écriture atomique : aucun .tmp ne reste", !File.Exists(Suite.FilePath + ".tmp"));
                Check("la surveillance signale jean (< 1 s)", Until(() => accents.Contains("jean"), 1000));
                bool threw = false; try { Suite.WriteAccent("../x"); } catch (ArgumentException) { threw = true; }
                Check("accent inconnu refusé à l'écriture", threw && Suite.ReadAccent() == "jean");
                // les anciens noms (charte < 2.0.0) sont des alias : lus et signalés sous leur nom canonique
                Suite.WriteAccent("bleu");
                Check("ancien nom « bleu » : écrit, relu « jean », signalé « jean »", Suite.ReadAccent() == "jean" && Suite.Canonical("violet") == "reve-violet" && Suite.Canonical("cdx") == "rouge" && Suite.Canonical("x") == null);
                File.WriteAllText(Suite.FilePath, "{{{ abîmé");
                Check("fichier abîmé : repli (rouge), aucune exception", Suite.ReadAccent() == "rouge");
                File.WriteAllText(Suite.FilePath, "{\"version\":2,\"accent\":\"vert\"}");
                Check("version inconnue : ignorée", Suite.ReadAccent("ambre") == "ambre");
                int a0 = accents.Count; Thread.Sleep(700);
                Check("fichier abîmé ou d'une autre version : la surveillance ne change rien", accents.Count == a0);
                Suite.WriteAccent("lavande");
                Check("retour à un fichier valide : lavande signalé (< 1 s)", Until(() => accents.Contains("lavande"), 1000));
                Suite.RegisterApp("shell", @"C:\Windows\System32\notepad.exe");
                Check("apps.json : chemin enregistré, nom hors liste refusé", Suite.AppPath("shell") != null && Suite.AppPath("../evil") == null);
            }
            catch (Exception ex) { lines.Add("EXCEPTION " + ex); bad++; }
            finally { anchor.BeginInvoke(new Action(Application.ExitThread)); }
        });
        var guard = new System.Windows.Forms.Timer { Interval = 60000 }; guard.Tick += (s, e) => { lines.Add("délai dépassé"); bad++; Application.ExitThread(); }; guard.Start();
        Application.Run();
        watch?.Dispose();
        try { Directory.Delete(dir, true); } catch { }
        lines.Add(bad == 0 ? "TOUT EST BON" : $"{bad} vérification(s) en échec");
        File.WriteAllLines(Path.Combine(Host.DataDir, "selftest-bus.txt"), lines);
        Environment.ExitCode = bad == 0 ? 0 : 1;
    }

    // cdx-shell.exe --selftest-pilote : l'analyseur du flux de Claude Code (Cdx.Host/ClaudeFlux.cs) sur des lignes RELEVÉES en vrai le 01/11/2026 (Claude Code 2.1.316,
    // sonde en stream-json avec --permission-prompt-tool stdio), et la politique d'autorisation de Pilote (PilotPolicy.cs). Logique pure, aucun processus lancé.
    public static void RunPilot()
    {
        var lines = new List<string>(); int bad = 0;
        void Check(string what, bool ok) { lines.Add((ok ? "OK     " : "ÉCHEC  ") + what); if (!ok) bad++; }
        try
        {
            var f = new ClaudeFlux();
            var init = f.Line("""{"type":"system","subtype":"init","cwd":"C:\\Dev","session_id":"5e7baf9f-3aa3-41ed-b711-2bbf7c26dfb7","tools":["Bash","Read"],"mcp_servers":[{"name":"cdx-suite","status":"connected"}],"model":"claude-fable-5-1","permissionMode":"default","uuid":"x"}""");
            Check("system/init : session, modèle, serveurs MCP", init.Count == 1 && init[0].Kind == "init" && init[0].SessionId == "5e7baf9f-3aa3-41ed-b711-2bbf7c26dfb7" && init[0].Text == "claude-fable-5-1" && init[0].Mcp == "cdx-suite:connected");
            Check("ligne illisible ou inconnue : rien", f.Line("pas du json").Count == 0 && f.Line("""{"type":"system","subtype":"hook_started"}""").Count == 0);
            Check("system/status : requesting", f.Line("""{"type":"system","subtype":"status","status":"requesting","session_id":"s","uuid":"u"}""") is { Count: 1 } st && st[0].Kind == "status" && st[0].Text == "requesting");
            f.Line("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_011CgcU3SS8gMqkB9Da5ZAbH","type":"message","role":"assistant","content":[]}},"session_id":"s","uuid":"u"}""");
            var d1 = f.Line("""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Je crée "}},"session_id":"s","uuid":"u"}""");
            var d2 = f.Line("""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"le fichier."}},"session_id":"s","uuid":"u"}""");
            Check("text_delta : le texte au fil de l'eau", d1.Count == 1 && d1[0].Kind == "text" && d1[0].Text == "Je crée " && d2[0].Text == "le fichier.");
            var th = f.Line("""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"hum"}},"session_id":"s","uuid":"u"}""");
            Check("thinking_delta : signalé", th.Count == 1 && th[0].Kind == "thinking");
            var a1 = f.Line("""{"type":"assistant","message":{"model":"claude-fable-5-1","id":"msg_011CgcU3SS8gMqkB9Da5ZAbH","type":"message","role":"assistant","content":[{"type":"text","text":"Je crée le fichier."}],"stop_reason":null},"parent_tool_use_id":null,"session_id":"s","uuid":"u"}""");
            Check("message assistant dont le texte est déjà arrivé en morceaux : pas de doublon", a1.Count == 0);
            var a2 = f.Line("""{"type":"assistant","message":{"model":"claude-fable-5-1","id":"msg_011CgcU3SS8gMqkB9Da5ZAbH","type":"message","role":"assistant","content":[{"type":"tool_use","id":"toolu_01VmAK83ekoCfCmTdKDNRXb5","name":"Write","input":{"file_path":"C:\\Dev\\sonde_test.txt","content":"ok"},"caller":{"type":"direct"}}],"stop_reason":null},"parent_tool_use_id":null,"session_id":"s","uuid":"u"}""");
            Check("bloc tool_use : outil, identifiant, cible (le chemin), arguments", a2.Count == 1 && a2[0].Kind == "tool" && a2[0].ToolName == "Write" && a2[0].ToolId == "toolu_01VmAK83ekoCfCmTdKDNRXb5" && a2[0].Target == "C:\\Dev\\sonde_test.txt" && a2[0].Input.Contains("\"content\":\"ok\""));
            Check("le même bloc tool_use renvoyé : ignoré", f.Line("""{"type":"assistant","message":{"id":"msg_011CgcU3SS8gMqkB9Da5ZAbH","role":"assistant","content":[{"type":"tool_use","id":"toolu_01VmAK83ekoCfCmTdKDNRXb5","name":"Write","input":{"file_path":"C:\\Dev\\sonde_test.txt","content":"ok"}}]},"parent_tool_use_id":null,"session_id":"s","uuid":"u"}""").Count == 0);
            var perm = f.Line("""{"type":"control_request","request_id":"d8a44bfd-d948-411c-a0f2-31fd94fd62fb","request":{"subtype":"can_use_tool","tool_name":"Write","display_name":"Write","input":{"file_path":"C:\\Dev\\sonde_test.txt","content":"ok"},"description":"sonde_test.txt","permission_suggestions":[{"type":"setMode","mode":"acceptEdits","destination":"session"}],"tool_use_id":"toolu_01VmAK83ekoCfCmTdKDNRXb5"}}""");
            Check("control_request can_use_tool : demande d'accord (identifiant, outil, arguments, cible)", perm.Count == 1 && perm[0].Kind == "permission" && perm[0].RequestId == "d8a44bfd-d948-411c-a0f2-31fd94fd62fb" && perm[0].ToolName == "Write" && perm[0].Input.Contains("sonde_test.txt") && perm[0].Target == "C:\\Dev\\sonde_test.txt" && perm[0].ToolUseId == "toolu_01VmAK83ekoCfCmTdKDNRXb5" && perm[0].Suggestions.Contains("acceptEdits"));
            Check("control_request d'un autre sous-type : rien", f.Line("""{"type":"control_request","request_id":"r","request":{"subtype":"hook_callback"}}""").Count == 0);
            var res = f.Line("""{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_01VmAK83ekoCfCmTdKDNRXb5","type":"tool_result","content":"File created successfully at: C:\\Dev\\sonde_test.txt"}]},"parent_tool_use_id":null,"session_id":"s","uuid":"u","tool_use_result":{"type":"create"}}""");
            Check("tool_result : résultat lié à l'outil", res.Count == 1 && res[0].Kind == "result_tool" && res[0].ToolId == "toolu_01VmAK83ekoCfCmTdKDNRXb5" && res[0].Text.StartsWith("File created") && !res[0].IsError);
            var rese = f.Line("""{"type":"user","message":{"role":"user","content":[{"tool_use_id":"t2","type":"tool_result","content":[{"type":"text","text":"boum"}],"is_error":true}]},"session_id":"s","uuid":"u"}""");
            Check("tool_result en erreur, contenu en liste", rese.Count == 1 && rese[0].IsError && rese[0].Text == "boum");
            var a3 = f.Line("""{"type":"assistant","message":{"id":"msg_autre","role":"assistant","content":[{"type":"text","text":"C'est fait."}]},"parent_tool_use_id":null,"session_id":"s","uuid":"u"}""");
            Check("message assistant sans morceaux reçus : texte rendu entier", a3.Count == 1 && a3[0].Kind == "text" && a3[0].Text == "C'est fait.");
            var sub = f.Line("""{"type":"assistant","message":{"id":"msg_sub","role":"assistant","content":[{"type":"text","text":"(sous-agent)"},{"type":"tool_use","id":"t9","name":"Read","input":{"file_path":"a.txt"}}]},"parent_tool_use_id":"toolu_parent","session_id":"s","uuid":"u"}""");
            Check("sous-agent : ses outils, pas son texte", sub.Count == 1 && sub[0].Kind == "tool" && sub[0].ToolName == "Read");
            var q = f.Line("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","unifiedWindows":{"five_hour":{"utilization":0.42,"resetsAt":1762000000},"seven_day":{"utilization":0.1,"resetsAt":1762500000}}},"session_id":"s","uuid":"u"}""");
            Check("rate_limit_event : quota des 5 h et des 7 jours", q.Count == 1 && q[0].Kind == "quota" && Math.Abs(q[0].Part - 0.42) < 1e-9 && q[0].ResetsAt == 1762000000 && Math.Abs(q[0].SevenPart - 0.1) < 1e-9);
            var r = f.Line("""{"type":"result","subtype":"success","is_error":false,"duration_ms":6100,"duration_api_ms":5381,"num_turns":3,"result":"C'est fait : le fichier est créé.","stop_reason":"end_turn","session_id":"5e7baf9f","total_cost_usd":0.21994899999999998,"usage":{"input_tokens":34,"cache_creation_input_tokens":9813,"cache_read_input_tokens":53196,"output_tokens":201,"iterations":[{"input_tokens":2,"output_tokens":42,"cache_read_input_tokens":21836,"cache_creation_input_tokens":9524},{"input_tokens":32,"output_tokens":42,"cache_read_input_tokens":31360,"cache_creation_input_tokens":289}]},"modelUsage":{"claude-fable-5-1":{"inputTokens":34,"outputTokens":201,"cacheReadInputTokens":53196,"cacheCreationInputTokens":9813,"contextWindow":1000000}},"permission_denials":[],"uuid":"u"}""");
            Check("result : texte, coût, jetons, durée, contexte du dernier appel sur la fenêtre du modèle", r.Count == 1 && r[0].Kind == "result" && r[0].Text.StartsWith("C'est fait") && Math.Abs(r[0].CostUsd - 0.219949) < 1e-6 && r[0].DurationMs == 6100 && r[0].InputTokens == 34 + 9813 + 53196 && r[0].OutputTokens == 201 && r[0].ContextUsed == 32 + 31360 + 289 && r[0].ContextMax == 1000000 && !r[0].IsError && r[0].Subtype == "success");
            var rerr = f.Line("""{"type":"result","subtype":"error_during_execution","is_error":true,"result":"No conversation found with session ID: x","session_id":"x","usage":{}}""");
            Check("result en erreur : signalé avec son sous-type", rerr.Count == 1 && rerr[0].IsError && rerr[0].Subtype == "error_during_execution" && rerr[0].Text.Contains("No conversation found"));

            string um = ClaudeFlux.UserMessage("Bonjour \"toi\"");
            Check("message utilisateur : la forme attendue sur l'entrée", um.Contains("\"type\":\"user\"") && um.Contains("\"role\":\"user\"") && um.Contains("\"type\":\"text\"") && um.Contains("Bonjour \\\"toi\\\""));
            string ok = ClaudeFlux.Answer("d8a4", true, "{\"file_path\":\"C:\\\\Dev\\\\a.txt\"}", null), no = ClaudeFlux.Answer("d8a4", false, null, "Non.");
            Check("réponse d'accord : control_response success avec allow et updatedInput", ok.Contains("\"type\":\"control_response\"") && ok.Contains("\"subtype\":\"success\"") && ok.Contains("\"request_id\":\"d8a4\"") && ok.Contains("\"behavior\":\"allow\"") && ok.Contains("\"updatedInput\":{\"file_path\":"));
            Check("réponse de refus : deny avec le message", no.Contains("\"behavior\":\"deny\"") && no.Contains("\"message\":\"Non.\"") && !no.Contains("updatedInput"));
            Check("interruption : control_request interrupt", ClaudeFlux.Interrupt("i1").Contains("\"subtype\":\"interrupt\"") && ClaudeFlux.Interrupt("i1").Contains("\"request_id\":\"i1\""));
            using (var jd = System.Text.Json.JsonDocument.Parse("""{"command":"git status\nls","description":"État"}"""))
                Check("cible d'un outil : première ligne, bornée", ClaudeFlux.TargetOf("Bash", jd.RootElement) == "git status");

            // ---- la politique ----
            var roots = new[] { @"C:\Essai\Dev", @"C:\Essai\Notes CDX" };
            System.Text.Json.JsonElement J(string s) { using var d = System.Text.Json.JsonDocument.Parse(s); return d.RootElement.Clone(); }
            var A = PilotPolicy.Decision.Allow; var K = PilotPolicy.Decision.Ask;
            Check("lecture (Read, Grep, outils MCP de lecture) : permise à tous les niveaux", PilotPolicy.Levels.All(l => PilotPolicy.Decide("Read", J("""{"file_path":"C:\\Windows\\x"}"""), roots, l) == A && PilotPolicy.Decide("mcp__cdx-notes__notes_read", J("{}"), roots, l) == A && PilotPolicy.Decide("mcp__cdx-suite__browser_read", J("{}"), roots, l) == A));
            Check("écriture dans un dossier de travail : libre / prudent demande / auto", PilotPolicy.Decide("Write", J("""{"file_path":"C:\\Essai\\Dev\\Zeta\\README.md"}"""), roots, "libre") == A && PilotPolicy.Decide("Write", J("""{"file_path":"C:\\Essai\\Dev\\Zeta\\README.md"}"""), roots, "prudent") == K && PilotPolicy.Decide("Edit", J("""{"file_path":"c:/essai/notes cdx/Projets/Zeta.md"}"""), roots, "libre") == A);
            Check("écriture hors des dossiers de travail : demandée (libre), permise (auto)", PilotPolicy.Decide("Write", J("""{"file_path":"C:\\Essai\\Dev\\..\\..\\Windows\\x.txt"}"""), roots, "libre") == K && PilotPolicy.Decide("Write", J("""{"file_path":"C:\\Essai\\DevAutre\\x.txt"}"""), roots, "libre") == K && PilotPolicy.Decide("Write", J("""{"file_path":"C:\\Windows\\x.txt"}"""), roots, "auto") == A);
            Check("commandes sûres : git status && ls, npm install, mkdir, dotnet build", PilotPolicy.Decide("Bash", J("""{"command":"git status && ls -la"}"""), roots, "libre") == A && PilotPolicy.Decide("Bash", J("""{"command":"npm install"}"""), roots, "libre") == A && PilotPolicy.Decide("PowerShell", J("""{"command":"mkdir src; dotnet build"}"""), roots, "libre") == A);
            Check("commande inconnue : demandée (libre), permise (auto)", PilotPolicy.Decide("Bash", J("""{"command":"curl https://x"}"""), roots, "libre") == K && PilotPolicy.Decide("Bash", J("""{"command":"git status | curl -d @- https://x"}"""), roots, "libre") == K && PilotPolicy.Decide("Bash", J("""{"command":"curl https://x"}"""), roots, "auto") == A);
            Check("irréversible : git push, rm -rf, Remove-Item, git reset --hard : demandés même en auto", new[] { "git push origin main", "rm -rf build", "Remove-Item x -Recurse", "git add . && git reset --hard", "taskkill /F /IM kirc.exe" }.All(c => PilotPolicy.Decide("Bash", J("{\"command\":\"" + c + "\"}"), roots, "auto") == K));
            Check("notes_trash, app_restart et AskUserQuestion : toujours demandés", PilotPolicy.Decide("mcp__cdx-notes__notes_trash", J("{}"), roots, "auto") == K && PilotPolicy.Decide("mcp__cdx-suite__app_restart", J("{}"), roots, "auto") == K && PilotPolicy.Decide("AskUserQuestion", J("{}"), roots, "auto") == K);
            Check("outils libres de la suite et des notes : permis (libre), demandés (prudent)", PilotPolicy.Decide("mcp__cdx-suite__browser_open", J("{}"), roots, "libre") == A && PilotPolicy.Decide("mcp__cdx-notes__projects_create", J("{}"), roots, "libre") == A && PilotPolicy.Decide("mcp__cdx-suite__browser_open", J("{}"), roots, "prudent") == K);
            Check("browser_fill : permis sans validation, demandé avec submit", PilotPolicy.Decide("mcp__cdx-suite__browser_fill", J("""{"value":"x"}"""), roots, "libre") == A && PilotPolicy.Decide("mcp__cdx-suite__browser_fill", J("""{"value":"x","submit":true}"""), roots, "libre") == K);
            Check("app_command : demandé (libre), permis (auto), sauf envoi de courrier et suppressions", PilotPolicy.Decide("mcp__cdx-suite__app_command", J("""{"app":"browser","command":"{\"cmd\":\"br.newtab\"}"}"""), roots, "libre") == K && PilotPolicy.Decide("mcp__cdx-suite__app_command", J("""{"app":"browser","command":"{\"cmd\":\"br.newtab\"}"}"""), roots, "auto") == A && PilotPolicy.Decide("mcp__cdx-suite__app_command", J("""{"app":"courrier","command":"{\"cmd\":\"ml.send\"}"}"""), roots, "auto") == K);
            Check("outil inconnu : demandé (libre), permis (auto) ; niveau inconnu = libre", PilotPolicy.Decide("Task", J("{}"), roots, "libre") == K && PilotPolicy.Decide("Task", J("{}"), roots, "auto") == A && PilotPolicy.Decide("Task", J("{}"), roots, "nimporte") == K);
            Check("Under : casse ignorée, « .. » résolu, préfixe de nom refusé", PilotPolicy.Under(@"c:\essai\dev\a\b.txt", roots) && !PilotPolicy.Under(@"C:\Essai\Dev\..\x.txt", roots) && !PilotPolicy.Under(@"C:\Essai\Dev2\x.txt", roots) && !PilotPolicy.Under("", roots) && PilotPolicy.Under(@"C:\Essai\Dev", roots));
            var (t1, d1b) = PilotPolicy.Describe("mcp__cdx-suite__browser_open", J("""{"url":"https://example.org/"}"""));
            var (t2, d2b) = PilotPolicy.Describe("Bash", J("""{"command":"git push","description":"Envoi"}"""));
            Check("Describe : titre lisible et détail", t1 == "Suite · browser_open" && d1b == "url : https://example.org/" && t2 == "Bash" && d2b == "git push");
            Check("les outils de lecture passent par --allowedTools (nom exact des serveurs MCP)", PilotPolicy.ReadTools.Contains("mcp__cdx-notes__vault_info") && PilotPolicy.ReadTools.Contains("mcp__cdx-suite__suite_state") && !PilotPolicy.ReadTools.Contains("Write"));
        }
        catch (Exception ex) { lines.Add("EXCEPTION " + ex); bad++; }
        lines.Add(bad == 0 ? "TOUT EST BON" : $"{bad} vérification(s) en échec");
        File.WriteAllLines(Path.Combine(Host.DataDir, "selftest-pilote.txt"), lines);
        Environment.ExitCode = bad == 0 ? 0 : 1;
    }
}
