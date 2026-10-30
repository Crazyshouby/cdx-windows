using System.Drawing;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Kdx;

static class SelfTest
{
    // kdx-shell.exe --selftest-sys : lit le son et le réseau, vérifie qu'on peut régler le volume (même valeur) et couper / rétablir le son.
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


    // kdx-shell.exe --selftest-audio : la liaison avec AudioLinker contre un faux serveur (tube de test, jamais « kdx.audiolinker » : on ne touche pas
    // au vrai AudioLinker). Vérifie : pas de connexion sans serveur, requête d'état à la connexion, état recopié et borné, ligne illisible ignorée,
    // commandes sortantes bien formées et bornées, déconnexion signalée, reconnexion. Résultat dans selftest-audio.txt.
    public static void RunAudio()
    {
        var lines = new List<string>(); int bad = 0;
        void Check(string what, bool ok) { lines.Add((ok ? "OK     " : "ÉCHEC  ") + what); if (!ok) bad++; }
        string name = "kdx.audiolinker-selftest-" + Guid.NewGuid().ToString("N")[..8];
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

    // kdx-shell.exe --selftest-bus : le bus et les réglages communs, sur un tube de test (préfixe KDX_PIPE_PREFIX) et un dossier temporaire (KDX_SUITE_DIR) :
    // jamais le vrai bus ni les vrais réglages. Message valide, type inconnu, champ invalide, ligne illisible, ligne de plus de 64 Ko, deux connexions,
    // puis suite.json (lecture, écriture atomique, fichier abîmé, version inconnue, surveillance). Résultat dans selftest-bus.txt.
    [STAThread]
    public static void RunBus()
    {
        var lines = new List<string>(); int bad = 0;
        void Check(string what, bool ok) { lines.Add((ok ? "OK     " : "ÉCHEC  ") + what); if (!ok) bad++; }
        string pid = Environment.ProcessId.ToString();
        string dir = Path.Combine(Path.GetTempPath(), "kdx-suite-test-" + pid); Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("KDX_PIPE_PREFIX", "kdxtest" + pid + ".");
        Environment.SetEnvironmentVariable("KDX_SUITE_DIR", dir);
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
                string pipe = "kdxtest" + pid + ".shell";
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
                using (var acl = new System.IO.Pipes.NamedPipeServerStream("kdxtest" + pid + ".acl", System.IO.Pipes.PipeDirection.In, 8, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly))
                {
                    var rules = System.IO.Pipes.PipesAclExtensions.GetAccessControl(acl).GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)).Cast<System.Security.AccessControl.AuthorizationRule>().Select(r => r.IdentityReference.Value).Distinct().ToList();
                    string me = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
                    Check($"droits du tube : l'utilisateur courant seulement ({string.Join(", ", rules)})", rules.Count == 1 && rules[0] == me);
                }

                // ---- suite.json
                Check("suite.json absent : rouge", Suite.ReadAccent() == "rouge");
                Suite.WriteAccent("bleu");
                Check("écriture puis lecture : bleu", Suite.ReadAccent() == "bleu");
                Check("écriture atomique : aucun .tmp ne reste", !File.Exists(Suite.FilePath + ".tmp"));
                Check("la surveillance signale bleu (< 1 s)", Until(() => accents.Contains("bleu"), 1000));
                bool threw = false; try { Suite.WriteAccent("../x"); } catch (ArgumentException) { threw = true; }
                Check("accent inconnu refusé à l'écriture", threw && Suite.ReadAccent() == "bleu");
                File.WriteAllText(Suite.FilePath, "{{{ abîmé");
                Check("fichier abîmé : repli (rouge), aucune exception", Suite.ReadAccent() == "rouge");
                File.WriteAllText(Suite.FilePath, "{\"version\":2,\"accent\":\"vert\"}");
                Check("version inconnue : ignorée", Suite.ReadAccent("ambre") == "ambre");
                int a0 = accents.Count; Thread.Sleep(700);
                Check("fichier abîmé ou d'une autre version : la surveillance ne change rien", accents.Count == a0);
                Suite.WriteAccent("violet");
                Check("retour à un fichier valide : violet signalé (< 1 s)", Until(() => accents.Contains("violet"), 1000));
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
}
