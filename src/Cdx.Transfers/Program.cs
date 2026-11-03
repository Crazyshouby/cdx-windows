namespace Cdx;

// cdx-transfers.exe [--theme <nom>]  ouvre « Transferts » : tout ce qui est en cours de copie, de déplacement, de téléchargement (et plus tard de torrent), en une seule fenêtre, avec
// l'historique des derniers. Les programmes qui font le travail l'annoncent par le bus (message « xfer ») et gardent la main : la fenêtre leur renvoie les ordres (pause, reprise, annulation).
// Une seule instance : lancé alors qu'elle tourne déjà, il lui demande de passer devant (message « show ») et s'arrête. Elle se lance d'elle-même quand un transfert dure ou échoue.
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Host.Init("transfers");
        var (theme, _) = Host.ParseArgs(args);
        if (!SingleInstance.Acquire("transfers", out var mutex)) { SingleInstance.Relay("transfers", "show", new { }); return; }
        using (mutex)
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => Host.Log("exception (interface) : " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Host.Log("exception fatale : " + e.ExceptionObject);

            var app = new TransfersApp(theme);
            Bus.Listen("transfers", app.OnBus);
            Suite.RegisterApp("transfers", Environment.ProcessPath);
            app.Anchor.BeginInvoke(new Action(() => app.Open()));   // dans la boucle de messages : WebView2 en a besoin
            Host.StartControlPipe("transfers", app.Anchor, app.HandleControl, () => app.Closing);
            Application.Run(app);
        }
    }
}
