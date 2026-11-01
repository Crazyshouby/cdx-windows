using System.Drawing;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Cdx;

// cdx-browser.exe [--theme <nom>] [adresse]  ouvre le navigateur CDX (une fenêtre à onglets). Une seule instance : lancé alors qu'il tourne déjà, il lui passe
// l'adresse (un nouvel onglet s'ouvre) et s'arrête. Un plantage du navigateur n'emporte ni la barre ni l'Explorateur.
//   cdx-browser.exe --selftest-browser   ce qu'offre WebView2 (extensions, DRM, codecs), sans fenêtre visible
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Host.Init("browser");
        if (args.Length > 0 && args[0] == "--selftest-browser") { SelfTest.RunBrowser(); return; }

        var (theme, target) = Host.ParseArgs(args);
        if (!SingleInstance.Acquire("browser", out var mutex)) { SingleInstance.Relay("browser", "open-url", target == null ? new { } : new { url = target }); return; }
        using (mutex)
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => Host.Log("exception (interface) : " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Host.Log("exception fatale : " + e.ExceptionObject);

            var app = new BrowserApp(theme);
            Bus.Listen("browser", app.OnBus);
            Suite.RegisterApp("browser", Environment.ProcessPath);
            app.Anchor.BeginInvoke(new Action(() => app.Open(target)));   // dans la boucle de messages : WebView2 en a besoin
            Host.StartControlPipe("browser", app.Anchor, app.HandleControl, () => app.Closing);
            Host.StartPilotPipe("browser", app.Anchor, app.HandleControl, () => app.Closing, "state", "cmd", "use", "page.text", "page.find", "page.click", "page.fill", "page.scroll", "page.shot");
            Application.Run(app);
        }
    }
}
