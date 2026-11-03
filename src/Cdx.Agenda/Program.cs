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
        using (mutex) CdxApp.Run("agenda", () => new AgendaApp(theme), app => app.Open());   // le démarrage commun : CdxApp.cs
    }
}
