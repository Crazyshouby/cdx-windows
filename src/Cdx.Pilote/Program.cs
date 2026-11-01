namespace Cdx;

// cdx-pilote.exe [--theme <nom>]  ouvre « Pilote » : le chat avec Claude réservé à l'organisation et à la création des projets. Claude Code tourne en
// sous-processus (abonnement de Valentin) ; il voit la suite par le serveur MCP cdx-pilote-mcp (outils) et le coffre par cdx-notes-mcp.
// Une seule instance : lancé alors qu'elle tourne déjà, il lui demande de passer devant (message « show ») et s'arrête.
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Host.Init("pilote");
        var (theme, _) = Host.ParseArgs(args);
        if (!SingleInstance.Acquire("pilote", out var mutex)) { SingleInstance.Relay("pilote", "show", new { }); return; }
        using (mutex)
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => Host.Log("exception (interface) : " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Host.Log("exception fatale : " + e.ExceptionObject);

            var app = new PiloteApp(theme);
            Bus.Listen("pilote", app.OnBus);
            Suite.RegisterApp("pilote", Environment.ProcessPath);
            app.Anchor.BeginInvoke(new Action(() => app.Open()));   // dans la boucle de messages : WebView2 en a besoin
            Host.StartControlPipe("pilote", app.Anchor, app.HandleControl, () => app.Closing);
            PilotTools.Serve(app);   // le tube des outils, pour les serveurs MCP lancés par Claude Code
            Application.Run(app);
        }
    }
}
