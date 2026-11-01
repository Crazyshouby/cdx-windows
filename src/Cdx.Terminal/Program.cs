namespace Cdx;

// cdx-terminal.exe [--theme <nom>] [dossier]  ouvre le terminal CDX : un shell dans une fenêtre de la charte (cadre, couleurs, accent de la suite), dans `dossier` s'il est donné.
// Une seule instance : lancé alors qu'elle tourne déjà, il lui demande de passer devant (message « show » du bus), ou d'ouvrir un onglet dans le dossier (« open-path »), et s'arrête.
//   CDX_TERMINAL_SHELL   remplace la commande lancée (essais) ; sinon PowerShell 7 s'il est installé, sinon Windows PowerShell
//   cdx-terminal.exe --selftest-pty   essai sans fenêtre de la pseudo-console (résultat dans %LOCALAPPDATA%\CdxShell\selftest-pty.txt)
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Host.Init("terminal");
        if (args.Length > 0 && args[0] == "--selftest-pty") { SelfTest.Run(); return; }

        var (theme, target) = Host.ParseArgs(args);
        string startDir = TerminalApp.ValidDir(target);
        if (!SingleInstance.Acquire("terminal", out var mutex))
        {
            if (startDir != null) SingleInstance.Relay("terminal", "open-path", new { path = startDir }); else SingleInstance.Relay("terminal", "show", new { });
            return;
        }
        using (mutex)
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => Host.Log("exception (interface) : " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Host.Log("exception fatale : " + e.ExceptionObject);

            var app = new TerminalApp(theme, startDir);
            Bus.Listen("terminal", app.OnBus);
            Suite.RegisterApp("terminal", Environment.ProcessPath);
            app.Anchor.BeginInvoke(new Action(() => app.Open()));   // dans la boucle de messages : WebView2 en a besoin
            Host.StartControlPipe("terminal", app.Anchor, app.HandleControl, () => app.Closing);
            Host.StartPilotPipe("terminal", app.Anchor, app.HandleControl, () => app.Closing);
            Application.Run(app);
        }
    }
}
