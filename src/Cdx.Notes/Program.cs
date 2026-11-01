using System.Text.Json;

namespace Cdx;

// cdx-notes.exe [--theme <nom>] [chemin d'une note]  ouvre « Notes » : le coffre de notes (fichiers Markdown, liens [[…]], projets, tâches), avec Claude qui y range (serveur MCP cdx-notes).
// Une seule instance : lancé alors qu'elle tourne déjà, il lui demande de passer devant (et d'ouvrir la note donnée) puis s'arrête.
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Host.Init("notes");
        var (theme, target) = Host.ParseArgs(args);
        if (!SingleInstance.Acquire("notes", out var mutex)) { SingleInstance.Relay("notes", string.IsNullOrEmpty(target) ? "show" : "open-note", string.IsNullOrEmpty(target) ? new { } : new { path = target }); return; }
        using (mutex)
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => Host.Log("exception (interface) : " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Host.Log("exception fatale : " + e.ExceptionObject);

            var app = new NotesApp(theme, target);
            Bus.Listen("notes", app.OnBus);
            Suite.RegisterApp("notes", Environment.ProcessPath);
            app.Anchor.BeginInvoke(new Action(() => app.Open()));   // dans la boucle de messages : WebView2 en a besoin
            Host.StartControlPipe("notes", app.Anchor, app.HandleControl, () => app.Closing);
            Host.StartPilotPipe("notes", app.Anchor, app.HandleControl, () => app.Closing);
            Application.Run(app);
        }
    }
}
