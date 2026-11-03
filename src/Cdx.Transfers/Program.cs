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
        using (mutex) CdxApp.Run("transfers", () => new TransfersApp(theme), app => app.Open());   // le démarrage commun : CdxApp.cs
    }
}
