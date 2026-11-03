using System.Text.Json;

namespace Cdx;

// cdx-repos.exe [--theme <nom>]  ouvre « Dépôts » : la liste de tous les projets git du PC, avec ce qui reste à enregistrer ou à envoyer sur GitHub.
// Une seule instance : lancé alors qu'elle tourne déjà, il lui demande de passer devant (message « show » du bus) et s'arrête. Lecture seule : aucun
// dépôt n'est jamais modifié.
//   cdx-repos.exe --selftest-git   essai sans fenêtre : de vrais dépôts jetables dans un dossier temporaire (résultat dans %LOCALAPPDATA%\CdxShell)
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Host.Init("repos");
        if (args.Length > 0 && args[0] == "--selftest-git") { SelfTest.Run(); return; }

        var (theme, _) = Host.ParseArgs(args);
        if (!SingleInstance.Acquire("repos", out var mutex)) { SingleInstance.Relay("repos", "show", new { }); return; }
        using (mutex) CdxApp.Run("repos", () => new ReposApp(theme), app => app.Open());   // le démarrage commun : CdxApp.cs
    }
}
