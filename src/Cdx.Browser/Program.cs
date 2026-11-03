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
        using (mutex) CdxApp.Run("browser", () => new BrowserApp(theme), app => app.Open(target));   // le démarrage commun : CdxApp.cs
    }
}
