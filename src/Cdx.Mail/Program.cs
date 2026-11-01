namespace Cdx;

// cdx-mail.exe [--theme <nom>]  ouvre « Courrier » : la boîte Gmail du compte Google relié à la suite (brique GoogleAuth de Cdx.Host), à la charte.
// Une seule instance : lancé alors qu'elle tourne déjà, il lui demande de passer devant (message « show ») et s'arrête.
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Host.Init("mail");
        var (theme, _) = Host.ParseArgs(args);
        if (!SingleInstance.Acquire("mail", out var mutex)) { SingleInstance.Relay("mail", "show", new { }); return; }
        using (mutex)
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => Host.Log("exception (interface) : " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Host.Log("exception fatale : " + e.ExceptionObject);

            var app = new MailApp(theme);
            Bus.Listen("mail", app.OnBus);
            Suite.RegisterApp("mail", Environment.ProcessPath);
            app.Anchor.BeginInvoke(new Action(() => app.Open()));   // dans la boucle de messages : WebView2 en a besoin
            Host.StartControlPipe("mail", app.Anchor, app.HandleControl, () => app.Closing);
            Host.StartPilotPipe("mail", app.Anchor, app.HandleControl, () => app.Closing, "state", "cmd", "search", "thread");
            Application.Run(app);
        }
    }
}
