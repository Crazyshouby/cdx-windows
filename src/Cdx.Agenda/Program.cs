namespace Cdx;

// cdx-agenda.exe [--theme <nom>]  ouvre « Agenda » : le calendrier et les tâches du compte Google relié à la suite (brique GoogleAuth de Cdx.Host), à la charte.
// Une seule instance : lancé alors qu'elle tourne déjà, il lui demande de passer devant (message « show ») et s'arrête.
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Host.Init("agenda");
        var (theme, _) = Host.ParseArgs(args);
        if (!SingleInstance.Acquire("agenda", out var mutex)) { SingleInstance.Relay("agenda", "show", new { }); return; }
        using (mutex)
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => Host.Log("exception (interface) : " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Host.Log("exception fatale : " + e.ExceptionObject);

            var app = new AgendaApp(theme);
            Bus.Listen("agenda", app.OnBus);
            Suite.RegisterApp("agenda", Environment.ProcessPath);
            app.Anchor.BeginInvoke(new Action(() => app.Open()));   // dans la boucle de messages : WebView2 en a besoin
            Host.StartControlPipe("agenda", app.Anchor, app.HandleControl, () => app.Closing);
            Host.StartPilotPipe("agenda", app.Anchor, app.HandleControl, () => app.Closing, "state", "cmd", "events", "tasks");
            Application.Run(app);
        }
    }
}
