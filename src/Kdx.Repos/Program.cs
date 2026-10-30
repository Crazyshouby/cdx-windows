using System.Text.Json;

namespace Kdx;

// kdx-repos.exe [--theme <nom>]  ouvre « Dépôts » : la liste de tous les projets git du PC, avec ce qui reste à enregistrer ou à envoyer sur GitHub.
// Une seule instance : lancé alors qu'elle tourne déjà, il lui demande de passer devant (message « show » du bus) et s'arrête. Lecture seule : aucun
// dépôt n'est jamais modifié.
//   kdx-repos.exe --selftest-git   essai sans fenêtre : de vrais dépôts jetables dans un dossier temporaire (résultat dans %LOCALAPPDATA%\KdxShell)
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Host.Init("repos");
        if (args.Length > 0 && args[0] == "--selftest-git") { SelfTest.Run(); return; }

        var (theme, _) = Host.ParseArgs(args);
        if (!SingleInstance.Acquire("repos", out var mutex)) { SingleInstance.Relay("repos", "show", new { }); return; }
        using (mutex)
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => Host.Log("exception (interface) : " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Host.Log("exception fatale : " + e.ExceptionObject);

            var app = new ReposApp(theme);
            Bus.Listen("repos", app.OnBus);
            Suite.RegisterApp("repos", Environment.ProcessPath);
            app.Anchor.BeginInvoke(new Action(() => app.Open()));   // dans la boucle de messages : WebView2 en a besoin
            Host.StartControlPipe("repos", app.Anchor, app.HandleControl, () => app.Closing);
            Application.Run(app);
        }
    }
}
