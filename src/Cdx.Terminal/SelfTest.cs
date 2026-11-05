using System.Text;

namespace Cdx;

// cdx-terminal.exe --selftest-pty : exerce la pseudo-console (ConPty.cs) sans fenêtre, sans entrée simulée et sans réseau : un programme qui écrit puis se termine
// (sortie, code de sortie), un shell vivant qu'on pilote par son entrée (accents, redimensionnement), l'arrêt d'un programme encore vivant.
// Résultat dans %LOCALAPPDATA%\CdxShell\selftest-pty.txt : une ligne par vérification, OK ou ÉCHEC.
static class SelfTest
{
    public static void Run()
    {
        var lines = new List<string>(); int bad = 0;
        void Check(string what, bool ok) { lines.Add((ok ? "OK     " : "ÉCHEC  ") + what); if (!ok) bad++; }
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        string Text(byte[] b) => Encoding.UTF8.GetString(b);

        // Lit la sortie d'une pseudo-console dans un tampon, jusqu'à la fin ou jusqu'au délai.
        (ConPty pty, Func<string> read, ManualResetEventSlim done, int[] code) Open(string command, int cols, int rows)
        {
            var sb = new StringBuilder(); var done = new ManualResetEventSlim(); var code = new[] { int.MinValue };
            var p = ConPty.Start(command, null, cols, rows);
            p.Output += b => { lock (sb) sb.Append(Text(b)); };
            p.Exited += c => { code[0] = c; done.Set(); };
            return (p, () => { lock (sb) return sb.ToString(); }, done, code);
        }
        bool WaitFor(Func<string> read, string what, int ms = 8000)
        {
            for (int t = 0; t < ms; t += 50) { if (read().Contains(what)) return true; Thread.Sleep(50); }
            return false;
        }

        try
        {
            // --- un programme qui écrit puis se termine avec un code ---
            var a = Open("\"" + cmd + "\" /c \"echo CDXPTY-BONJOUR& exit 7\"", 100, 30);
            bool ended = a.done.Wait(8000);
            Check("le programme se termine et l'événement de fin arrive", ended);
            Check("son code de sortie est lu (7)", a.code[0] == 7);
            Check("sa sortie arrive avant l'événement de fin", a.read().Contains("CDXPTY-BONJOUR"));
            a.pty.Dispose();

            // --- un shell vivant : entrée, accents, redimensionnement ---
            var b = Open("\"" + cmd + "\" /q", 100, 30);
            Check("un shell vivant affiche son invite", WaitFor(b.read, ">"));
            b.pty.Write("chcp 65001 >nul\r");
            b.pty.Write("echo é€ß-CDXACC\r");
            Check("ce qu'on tape arrive au shell et les accents font l'aller-retour (UTF-8)", WaitFor(b.read, "é€ß-CDXACC"));
            b.pty.Write("mode con\r");
            Check("la taille de départ est celle demandée (100 colonnes)", WaitFor(b.read, "100"));
            b.pty.Resize(120, 40);
            b.pty.Write("mode con\r");
            Check("après un redimensionnement, le shell voit la nouvelle taille (120 colonnes)", WaitFor(b.read, "120"));
            b.pty.Write("exit\r");
            Check("« exit » termine le shell et la fenêtre est prévenue", b.done.Wait(8000));
            b.pty.Dispose();

            // --- fermer la fenêtre arrête ce qui tourne ---
            var c = Open("\"" + cmd + "\" /q", 80, 24);
            WaitFor(c.read, ">");
            int pid = c.pty.ProcessId;
            c.pty.Dispose();
            bool gone = false;
            for (int t = 0; t < 3000 && !gone; t += 100) { try { System.Diagnostics.Process.GetProcessById(pid).Dispose(); Thread.Sleep(100); } catch (ArgumentException) { gone = true; } }
            Check("fermer la pseudo-console arrête le programme encore vivant", gone);

            // --- un programme qui n'existe pas : une erreur claire, pas de plantage ---
            try { ConPty.Start("\"C:\\introuvable\\rien.exe\"", null, 80, 24).Dispose(); Check("un programme introuvable est refusé", false); }
            catch (System.ComponentModel.Win32Exception) { Check("un programme introuvable est refusé avec une erreur lisible", true); }
        }
        catch (Exception ex) { lines.Add("ÉCHEC  exception : " + ex); bad++; }
        finally
        {
            lines.Add(bad == 0 ? "RÉSULTAT : TOUT EST BON" : $"RÉSULTAT : {bad} ÉCHEC(S)");
            File.WriteAllLines(Path.Combine(Host.DataDir, "selftest-pty.txt"), lines);
            Environment.ExitCode = bad == 0 ? 0 : 1;
        }
    }
}
