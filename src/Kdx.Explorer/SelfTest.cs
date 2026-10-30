using System.Drawing;
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
            Do($"{{\"cmd\":\"fs.drop\",\"paths\":[{J(Path.Combine(cible, "b.txt"))}],\"dir\":{J(Path.Combine(dir, "n-existe-pas"))},\"move\":true,\"tab\":\"t\"}}");
            Check("destination qui n'existe pas : message d'erreur, fichier intact", HasError() && File.Exists(Path.Combine(cible, "b.txt")));
            Do($"{{\"cmd\":\"fs.drop\",\"paths\":[\"relatif/x.txt\",\"\"],\"dir\":{J(cible)},\"move\":true,\"tab\":\"t\"}}");
            Check("chemins relatifs ou vides ignorés sans erreur", !HasError());

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
