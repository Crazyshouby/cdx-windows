using System.Drawing;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Cdx;

// cdx-explorer.exe [--theme <nom>] [dossier]  ouvre une fenêtre de l'Explorateur CDX. Une seule instance : lancé alors qu'elle tourne déjà, il lui passe le
// dossier (une nouvelle fenêtre s'ouvre) et s'arrête. Un plantage de l'Explorateur n'emporte ni la barre ni le navigateur.
//   cdx-explorer.exe --selftest-fs | --selftest-web   essais sans fenêtre visible (résultat dans %LOCALAPPDATA%\CdxShell)
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Host.Init("explorer");
        if (args.Length > 0 && args[0] == "--selftest-fs") { SelfTest.Run(); return; }
        if (args.Length > 0 && args[0] == "--selftest-web") { SelfTest.RunWeb(); return; }

        var (theme, target) = Host.ParseArgs(args);
        if (!SingleInstance.Acquire("explorer", out var mutex)) { SingleInstance.Relay("explorer", "open-path", target == null ? new { } : new { path = target }); return; }
        using (mutex) CdxApp.Run("explorer", () => new ExplorerApp(theme), app => app.Open(target));   // le démarrage commun : CdxApp.cs
    }
}
